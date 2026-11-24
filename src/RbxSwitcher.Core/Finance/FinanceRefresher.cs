using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Finance;

public enum IdentityCheck { Confirmed, SessionExpired, WrongAccount, Unreachable }

/// <param name="RotatedSession">A newer cookie Roblox handed out during the reads; the caller saves it in the vault.</param>
/// <param name="RateLimitedFor">Set when Roblox answered HTTP 429 during this refresh: how long to wait before asking again.</param>
public sealed record FinanceRefreshResult(AccountFinance Finance, IdentityCheck Identity, string? RotatedSession, TimeSpan? RateLimitedFor = null);

/// <summary>
/// Refreshes one account's balance, pending Robux, Plus, caps, restriction and transfer history.
/// The session is first checked against the saved user id; nothing else is read if it does not match.
/// Works on a copy of the finance data; the caller applies the result on the UI thread.
/// </summary>
public sealed class FinanceRefresher
{
    private readonly RobloxApi _api;
    private readonly Func<DateTimeOffset> _clock;
    public int MaxHistoryPages { get; init; } = 15;

    public FinanceRefresher(RobloxApi api, Func<DateTimeOffset>? clock = null)
    {
        _api = api;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <param name="fullHistory">Read the full 30 days from the newest page, ignoring the saved history and cursor.</param>
    public async Task<FinanceRefreshResult> RefreshAsync(long userId, string session, AccountFinance? current, CancellationToken ct = default, bool fullHistory = false)
    {
        var run = new Run(this, userId, session, current?.Clone() ?? new AccountFinance(), ct, fullHistory);
        return await run.ExecuteAsync().ConfigureAwait(false);
    }

    /// <summary>State of one refresh: the current session (it can rotate mid-way) and the errors so far.</summary>
    private sealed class Run(FinanceRefresher owner, long userId, string session, AccountFinance f, CancellationToken ct, bool fullHistory)
    {
        private string _session = session;
        private string? _rotated;
        private bool _rateLimited;
        private TimeSpan? _retryAfter;
        private readonly RobloxApi _api = owner._api;

        public async Task<FinanceRefreshResult> ExecuteAsync()
        {
            f.LastAttemptUtc = owner._clock();
            f.LastErrors = [];
            f.FailedReads = [];
            f.FailureReasons = [];

            // 1. Identity: the cookie must belong to the account it is saved under.
            var who = await _api.GetAuthenticatedUserAsync(_session, ct).ConfigureAwait(false);
            if (who.Outcome != AuthOutcome.Valid || who.User!.Id != userId)
            {
                f.FailedReads = [.. FinanceField.All]; // nothing was read: every old value is "a conferir"
                string why = who.Outcome switch
                {
                    AuthOutcome.Invalid => "sessão expirada",
                    AuthOutcome.Unreachable => "Roblox não respondeu",
                    _ => "sessão de outra conta",
                };
                foreach (var field in FinanceField.All) f.FailureReasons[field] = why;
            }
            switch (who.Outcome)
            {
                case AuthOutcome.Invalid:
                    f.LastErrors.Add("sessão expirada: entre de novo na conta");
                    return new FinanceRefreshResult(f, IdentityCheck.SessionExpired, null);
                case AuthOutcome.Unreachable:
                    f.LastErrors.Add("o Roblox não respondeu à verificação da sessão");
                    return new FinanceRefreshResult(f, IdentityCheck.Unreachable, null);
                case AuthOutcome.Valid when who.User!.Id != userId:
                    f.LastErrors.Add("a sessão salva pertence a outra conta; nada foi lido");
                    return new FinanceRefreshResult(f, IdentityCheck.WrongAccount, null);
            }
            Adopt(who.RotatedSession);

            // 2. Simple reads. A failure keeps the previous value and its time.
            if (await Read(s => _api.GetRobuxAsync(s, ct), FinanceField.Robux) is (true, var robux))
            { f.Robux = robux; f.RobuxAtUtc = owner._clock(); }

            if (await Read(s => _api.GetPendingRobuxAsync(s, userId, ct), FinanceField.Pending) is (true, var pending))
            { f.PendingRobux = pending; f.PendingAtUtc = owner._clock(); }

            if (await Read(s => _api.GetPlusAsync(s, owner._clock(), ct), FinanceField.Plus) is (true, var plus))
            { f.Plus = plus; f.PlusAtUtc = owner._clock(); }

            bool quotaFailed = false;
            bool hadLimits = f.CapsAtUtc is not null && f.RestrictionAtUtc is not null;
            var (oldCaps, oldRestriction) = (f.Caps, f.Restriction);
            bool capsRead = false, restrictionRead = false;
            if (await Read(s => _api.GetTransferCapsAsync(s, ct), FinanceField.Caps) is (true, var caps))
            { f.Caps = caps; f.CapsAtUtc = owner._clock(); capsRead = true; }
            else quotaFailed = true;

            if (await Read(s => _api.GetTransferRestrictionAsync(s, ct), FinanceField.Restriction) is (true, var restriction))
            { f.Restriction = restriction; f.RestrictionAtUtc = owner._clock(); restrictionRead = true; }
            else quotaFailed = true;

            // A lower limit is reported, not just shown: only when both parts were read now (a failure is not a change).
            if (capsRead && restrictionRead)
                LimitWatch.Track(f, hadLimits, oldCaps, oldRestriction, f.Caps, f.Restriction, owner._clock());

            // 3. History (paged, resumable).
            if (_rateLimited)
            {
                f.LastErrors.Add("histórico de envios: não lido (limite de requisições do Roblox)");
                f.FailedReads.Add(FinanceField.History);
                f.FailureReasons[FinanceField.History] = "não lido (limite de requisições do Roblox)";
                quotaFailed = true;
            }
            else
            {
                var sync = await LedgerSync.SyncAsync(
                    async (cursor, token) =>
                    {
                        var page = await _api.GetTransfersPageAsync(_session, userId, cursor, token).ConfigureAwait(false);
                        Adopt(page.RotatedSession);
                        return page;
                    },
                    f.Ledger, f.Checkpoint, owner._clock, owner.MaxHistoryPages, ct, fullHistory).ConfigureAwait(false);
                f.Ledger = sync.Ledger; // still the last complete one unless this crawl completed
                f.Checkpoint = sync.Checkpoint;
                f.LastHistoryRead = sync.Info;
                if (!sync.Completed && sync.Problem?.Contains("429") == true) _rateLimited = true;
                if (!sync.Completed)
                {
                    f.LastErrors.Add("histórico de envios: " + sync.Problem);
                    f.FailedReads.Add(FinanceField.History);
                    f.FailureReasons[FinanceField.History] = sync.Problem ?? "leitura incompleta";
                    quotaFailed = true;
                }
            }
            f.QuotaReadFailed = quotaFailed;
            return new FinanceRefreshResult(f, IdentityCheck.Confirmed, _rotated, _rateLimited ? _retryAfter ?? TimeSpan.FromSeconds(60) : null);
        }

        private async Task<(bool Ok, T? Value)> Read<T>(Func<string, Task<ReadResult<T>>> call, string field)
        {
            string what = FinanceField.Describe(field);
            if (_rateLimited)
            {
                f.LastErrors.Add($"{what}: não lido (limite de requisições do Roblox)");
                f.FailedReads.Add(field);
                f.FailureReasons[field] = "não lido (limite de requisições do Roblox)";
                return (false, default);
            }
            var res = await call(_session).ConfigureAwait(false);
            Adopt(res.RotatedSession);
            if (res.Outcome == ReadOutcome.RateLimited) { _rateLimited = true; _retryAfter = res.RetryAfter; }
            if (!res.Ok)
            {
                f.LastErrors.Add($"{what}: {res.Problem}");
                f.FailedReads.Add(field);
                f.FailureReasons[field] = res.Problem ?? "erro na leitura";
                return (false, default);
            }
            return (true, res.Value);
        }

        private void Adopt(string? fresh)
        {
            if (fresh is null) return;
            _session = fresh;
            _rotated = fresh;
        }
    }
}
