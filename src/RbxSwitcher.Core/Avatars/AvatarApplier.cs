using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Avatars;

public enum ApplyStatus { Applied, Partial, SessionExpired, WrongAccount, RateLimited, Failed, Cancelled }

/// <param name="Acquired">Free items the account got now.</param>
/// <param name="Skipped">Chosen items not worn, with the reason.</param>
/// <param name="RotatedSession">New cookie Roblox handed back during the calls (save it).</param>
public sealed record AvatarApplyResult(ApplyStatus Status, IReadOnlyList<string> Acquired, IReadOnlyList<(string Name, string Why)> Skipped,
                                       string? Problem = null, string? RotatedSession = null, TimeSpan? RetryAfter = null)
{
    public bool Done => Status is ApplyStatus.Applied or ApplyStatus.Partial;
    public override string ToString() => $"AvatarApplyResult({Status})"; // never the session
}

/// <summary>
/// Puts a design on one account: confirms the cookie belongs to it, reads what it wears, makes sure it owns the
/// chosen items (getting the free ones, if allowed), then sets the avatar type, skin tone and worn assets.
/// Running it again is safe: owned items are not bought again and the worn list is replaced, not appended.
/// </summary>
public sealed class AvatarApplier(RobloxApi api)
{
    /// <summary>Times the worn list is trimmed and sent again after a "LimitExceeded" refusal.</summary>
    public const int MaxLimitRetries = 3;

    /// <summary>Pause between free-item requests of one account.</summary>
    public TimeSpan PurchaseGap { get; init; } = TimeSpan.FromMilliseconds(600);

    public async Task<AvatarApplyResult> ApplyAsync(long userId, string session, AvatarDesign design,
                                                    IReadOnlyDictionary<string, AvatarItem> catalog, bool acquireFree, CancellationToken ct = default)
    {
        var s = new SessionContext(session);
        var acquired = new List<string>();
        var skipped = new List<(string, string)>();
        AvatarApplyResult Stop(ApplyStatus st, string? why, TimeSpan? retry = null) => new(st, acquired, skipped, why, s.Rotated, retry);

        // 1. The cookie must belong to this account.
        var who = await api.GetAuthenticatedUserAsync(s.Session, ct).ConfigureAwait(false);
        s.Adopt(who.RotatedSession);
        if (who.Outcome == AuthOutcome.Invalid) return Stop(ApplyStatus.SessionExpired, "sessão expirada: entre de novo na conta");
        if (who.Outcome == AuthOutcome.Unreachable)
            return who.RetryAfter is { } ra ? Stop(ApplyStatus.RateLimited, "limite de requisições do Roblox", ra) : Stop(ApplyStatus.Failed, "o Roblox não respondeu");
        if (who.User!.Id != userId) return Stop(ApplyStatus.WrongAccount, "a sessão salva pertence a outra conta; nada foi alterado");

        // 2. What it wears now (kept categories and animations/emotes stay as they are).
        var current = await api.GetCurrentAvatarAsync(s, ct).ConfigureAwait(false);
        if (Map(current.Outcome) is { } bad) return Stop(bad, "avatar atual: " + current.Problem, current.RetryAfter);
        if (!current.Ok) return Stop(ApplyStatus.Failed, "avatar atual: " + current.Problem);

        // 3. Ownership: get the free items it lacks; anything else it lacks is left out.
        var unavailable = new HashSet<string>();
        bool challenged = false;
        foreach (var r in design.Slots.Values.Where(x => x.Mode == SlotMode.Items).SelectMany(x => x.Items).DistinctBy(x => x.Key))
        {
            ct.ThrowIfCancellationRequested();
            var owned = await api.IsOwnedAsync(s, userId, r, ct).ConfigureAwait(false);
            if (Map(owned.Outcome) is { } st) return Stop(st, owned.Problem, owned.RetryAfter);
            if (owned.Ok && owned.Value) continue;
            catalog.TryGetValue(r.Key, out var item);
            if (item is not { Free: true }) { unavailable.Add(r.Key); skipped.Add((r.Name, "a conta não possui este item e ele não é grátis")); continue; }
            if (!acquireFree) { unavailable.Add(r.Key); skipped.Add((r.Name, "a conta não possui este item (pegar grátis está desligado)")); continue; }
            if (challenged) { unavailable.Add(r.Key); skipped.Add((r.Name, "o Roblox pediu verificação (captcha) para pegar itens nesta conta")); continue; }
            var got = await api.AcquireFreeAsync(s, userId, item, ct).ConfigureAwait(false);
            switch (got.Outcome)
            {
                case WriteOutcome.Ok:
                    if (got.Value == PurchaseOutcome.Acquired) acquired.Add(item.Name);
                    Logging.Log.Info($"[avatar] item grátis {(got.Value == PurchaseOutcome.Acquired ? "obtido" : "já era da conta")}: {item.Kind} {item.Id}");
                    break;
                case WriteOutcome.Unauthorized: return Stop(ApplyStatus.SessionExpired, "sessão recusada ao pegar item grátis");
                case WriteOutcome.RateLimited: return Stop(ApplyStatus.RateLimited, got.Problem, got.RetryAfter);
                case WriteOutcome.Challenge:
                    challenged = true;
                    unavailable.Add(r.Key);
                    skipped.Add((r.Name, got.Problem ?? "o Roblox pediu verificação"));
                    break;
                default:
                    unavailable.Add(r.Key);
                    skipped.Add((r.Name, got.Problem ?? "o Roblox não entregou o item"));
                    break;
            }
            if (PurchaseGap > TimeSpan.Zero) await Task.Delay(PurchaseGap, ct).ConfigureAwait(false);
        }

        // 4. Avatar type, skin, then the worn list.
        if (design.AvatarType is { } type && !string.Equals(type, current.Value!.AvatarType, StringComparison.OrdinalIgnoreCase))
        {
            var t = await api.SetAvatarTypeAsync(s, type, ct).ConfigureAwait(false);
            if (WriteStop(t.Outcome) is { } st) return Stop(st, t.Problem, t.RetryAfter);
            if (!t.Ok) skipped.Add(("tipo " + type, t.Problem ?? "recusado"));
        }
        if (SkinTones.Find(design.SkinHex) is { } tone)
        {
            var c = await api.SetSkinToneAsync(s, tone, ct).ConfigureAwait(false);
            if (WriteStop(c.Outcome) is { } st) return Stop(st, c.Problem, c.RetryAfter);
            if (!c.Ok) skipped.Add(("tom de pele", c.Problem ?? "recusado"));
        }
        if (design.Slots.Values.Any(x => x.Mode != SlotMode.Keep))
        {
            var rules = await api.GetAvatarRulesAsync(ct).ConfigureAwait(false);
            var plan = AvatarComposer.Compose(current.Value!, design, catalog, unavailable, rules);
            foreach (var (item, why) in plan.Skipped)
                if (!skipped.Any(x => x.Item1 == item.Name)) skipped.Add((item.Name, why));
            // Roblox may still find a type over its limit (rules that changed, pieces it counts differently): take the
            // piece it points at off and try again, a few times at most.
            var entries = plan.Entries.ToList();
            WriteResult<IReadOnlyList<long>> wear;
            for (int attempt = 0; ; attempt++)
            {
                wear = await api.SetWearingAssetsAsync(s, entries.Select(e => e.Id).ToList(), ct).ConfigureAwait(false);
                if (wear.Ok || wear.Outcome != WriteOutcome.Refused || attempt >= MaxLimitRetries || wear.ErrorBody is not { } body) break;
                if (AvatarComposer.LimitCulprit(entries, AvatarParsers.WearValidation(body)) is not { } off) break;
                entries.Remove(off);
                Logging.Log.Info($"[avatar] limite de tipo do Roblox: tipo {off.AssetType} retirado e nova tentativa");
                if (off.From is { } f && !skipped.Any(x => x.Item1 == f.Name))
                    skipped.Add((f.Name, f.Kind == ItemKind.Bundle ? "uma peça do pacote ficou de fora: " + AvatarComposer.OverLimit : AvatarComposer.OverLimit));
            }
            if (WriteStop(wear.Outcome) is { } st) return Stop(st, wear.Problem, wear.RetryAfter);
            if (!wear.Ok) return Stop(ApplyStatus.Failed, "vestir: " + wear.Problem);
            var invalid = wear.Value!.ToHashSet();
            if (invalid.Count > 0)
                foreach (var r in design.Slots.Values.Where(x => x.Mode == SlotMode.Items).SelectMany(x => x.Items))
                {
                    bool refused = r.Kind == ItemKind.Asset ? invalid.Contains(r.Id)
                        : catalog.TryGetValue(r.Key, out var b) && b.BundleAssets?.Keys.Any(invalid.Contains) == true;
                    if (refused) skipped.Add((r.Name, "o Roblox recusou vestir este item"));
                }
        }
        return new AvatarApplyResult(skipped.Count == 0 ? ApplyStatus.Applied : ApplyStatus.Partial, acquired, skipped, null, s.Rotated);
    }

    private static ApplyStatus? Map(ReadOutcome o) => o switch
    {
        ReadOutcome.Unauthorized => ApplyStatus.SessionExpired,
        ReadOutcome.RateLimited => ApplyStatus.RateLimited,
        _ => null,
    };

    private static ApplyStatus? WriteStop(WriteOutcome o) => o switch
    {
        WriteOutcome.Unauthorized => ApplyStatus.SessionExpired,
        WriteOutcome.RateLimited => ApplyStatus.RateLimited,
        _ => null,
    };
}

/// <summary>One account of a batch: who, and which design it gets (the same one, or its own random pick).</summary>
public sealed record BatchTarget(long UserId, string Username, string Session, AvatarDesign Design);

/// <summary>
/// Applies designs to many accounts one after another, with a pause between accounts. When Roblox limits
/// requests (429) it waits and retries the same account a few times; if it keeps limiting, it stops and the
/// remaining accounts are reported as not applied. Nothing runs in parallel.
/// </summary>
public sealed class AvatarBatch(AvatarApplier applier)
{
    public TimeSpan Gap { get; init; } = TimeSpan.FromSeconds(1.5);
    public int MaxRateLimitWaits { get; init; } = 3;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task RunAsync(IReadOnlyList<BatchTarget> targets, IReadOnlyDictionary<string, AvatarItem> catalog, bool acquireFree,
                               Action<BatchTarget, AvatarApplyResult?, string> progress, CancellationToken ct)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (ct.IsCancellationRequested) { Report(i, "cancelado"); return; }
            AvatarApplyResult? r = null;
            for (int wait = 0; ; wait++)
            {
                progress(t, null, wait == 0 ? "aplicando…" : $"aplicando de novo ({wait + 1}ª tentativa)…");
                try { r = await applier.ApplyAsync(t.UserId, t.Session, t.Design, catalog, acquireFree, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Report(i, "cancelado"); return; }
                if (r.Status != ApplyStatus.RateLimited || wait >= MaxRateLimitWaits) break;
                var pause = TimeSpan.FromSeconds(Math.Clamp((r.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds, 20, 120));
                progress(t, null, $"limite do Roblox; aguardando {pause.TotalSeconds:0} s…");
                try { await Delay(pause, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Report(i, "cancelado"); return; }
            }
            progress(t, r, "");
            if (r.Status == ApplyStatus.RateLimited) { Report(i + 1, "pausado: o Roblox continuou limitando os pedidos"); return; }
            if (i < targets.Count - 1)
            {
                try { await Delay(Gap, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Report(i + 1, "cancelado"); return; }
            }
        }

        void Report(int from, string why)
        {
            for (int j = from; j < targets.Count; j++)
                progress(targets[j], new AvatarApplyResult(ApplyStatus.Cancelled, [], [], why), "");
        }
    }
}
