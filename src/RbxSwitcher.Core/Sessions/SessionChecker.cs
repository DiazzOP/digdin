using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Sessions;

/// <summary>Result for one account, applied to the store on the UI thread by the caller.</summary>
public sealed record CheckResult(long UserId, string SentSession, AuthResult Auth);

/// <summary>
/// Checks many saved sessions against Roblox politely: few at a time, spaced out, backing off
/// when Roblox rate-limits. 100 accounts take well under a minute.
/// </summary>
public sealed class SessionChecker
{
    private readonly RobloxApi _api;
    public int Concurrency { get; init; } = 3;
    public TimeSpan Spacing { get; init; } = TimeSpan.FromMilliseconds(200);

    public SessionChecker(RobloxApi api) => _api = api;

    public async Task CheckAsync(IReadOnlyList<(long UserId, string Session)> targets,
                                 Action<CheckResult> onResult, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Concurrency);
        var tasks = targets.Select(async t =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                AuthResult r = await _api.GetAuthenticatedUserAsync(t.Session, ct).ConfigureAwait(false);
                if (r.RetryAfter is { } wait)
                {
                    await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    r = await _api.GetAuthenticatedUserAsync(t.Session, ct).ConfigureAwait(false);
                }
                onResult(new CheckResult(t.UserId, t.Session, r));
                await Task.Delay(Spacing, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one result to the store. Returns true if the vault should be saved.
    /// A result for a different user id than expected is ignored (never overwrite one account with another).
    /// </summary>
    public static bool Apply(AccountStore store, CheckResult r)
    {
        var a = store.Find(r.UserId);
        if (a is null || a.RevealSession() != r.SentSession)
            return false; // removed or re-authenticated meanwhile
        switch (r.Auth.Outcome)
        {
            case AuthOutcome.Valid when r.Auth.User!.Id == r.UserId:
                a.Status = SessionStatus.Valid;
                a.LastValidUtc = DateTimeOffset.UtcNow;
                bool changed = a.Username != r.Auth.User.Name || a.DisplayName != r.Auth.User.DisplayName;
                a.Username = r.Auth.User.Name;
                a.DisplayName = r.Auth.User.DisplayName;
                if (r.Auth.RotatedSession is { } fresh)
                    changed |= store.ReplaceSessionIf(r.UserId, r.SentSession, fresh);
                return changed;
            case AuthOutcome.Valid:
                a.Status = SessionStatus.Expired; // the cookie belongs to someone else: treat as unusable
                return false;
            case AuthOutcome.Invalid:
                a.Status = SessionStatus.Expired;
                return false;
            default:
                a.Status = SessionStatus.Unreachable;
                return false;
        }
    }
}
