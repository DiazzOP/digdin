using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Limiteds;

/// <param name="RateLimitedFor">Set when Roblox answered 429: how long to wait before asking again.</param>
public sealed record InventoryRefreshResult(AccountInventory Inventory, string? RotatedSession, bool SessionRefused, TimeSpan? RateLimitedFor);

/// <summary>
/// Reads one account's Limiteds after confirming the cookie belongs to it. A failed read keeps the last complete
/// list (marked with the problem) instead of showing a shorter one. Rolimons holds are an optional cross-check.
/// </summary>
/// <param name="itemInfo">Name and RAP of an item (from the market cache), for copies only Rolimons lists.</param>
public sealed class InventoryRefresher(RobloxApi api, RolimonsApi? rolimons = null, Func<DateTimeOffset>? clock = null,
                                       Func<long, (string? Name, long? Rap)>? itemInfo = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// When the app first saw each copy: kept from the previous read; a copy that was not in a previous complete read
    /// arrived since — now. At the very first read nothing is known (null). Holds are counted from these sightings
    /// when no trade or purchase explains them.
    /// </summary>
    public static void MarkSeen(AccountInventory inv, AccountInventory? previous, DateTimeOffset now)
    {
        var before = previous?.Copies.Where(c => c.UserAssetId > 0).DistinctBy(c => c.UserAssetId).ToDictionary(c => c.UserAssetId) ?? [];
        bool known = previous is { Complete: true, ReadUtc: not null };
        for (int i = 0; i < inv.Copies.Count; i++)
        {
            var c = inv.Copies[i];
            var seen = before.TryGetValue(c.UserAssetId, out var old) ? old.SeenUtc : known ? now : null;
            if (seen != c.SeenUtc) inv.Copies[i] = c with { SeenUtc = seen };
        }
    }

    public async Task<InventoryRefreshResult> RefreshAsync(long userId, string session, AccountInventory? previous, CancellationToken ct = default)
    {
        var s = new SessionContext(session);
        var inv = previous is null ? new AccountInventory() : new AccountInventory
        {
            Copies = [.. previous.Copies], Ambiguous = previous.Ambiguous, HoldsFromRolimons = [.. previous.HoldsFromRolimons],
            ReadUtc = previous.ReadUtc, Complete = previous.Complete,
        };
        inv.LastAttemptUtc = _clock();
        inv.Problem = null;

        var who = await api.GetAuthenticatedUserAsync(s.Session, ct).ConfigureAwait(false);
        s.Adopt(who.RotatedSession);
        if (who.Outcome == AuthOutcome.Invalid) { inv.Problem = "sessão expirada"; return new(inv, s.Rotated, true, null); }
        if (who.Outcome == AuthOutcome.Unreachable) { inv.Problem = "o Roblox não respondeu"; return new(inv, s.Rotated, false, who.RetryAfter); }
        if (who.User!.Id != userId) { inv.Problem = "a sessão salva pertence a outra conta"; return new(inv, s.Rotated, true, null); }

        var res = await api.GetCollectiblesAsync(s, userId, ct).ConfigureAwait(false);
        if (res.Ok)
        {
            inv.Copies = res.Value.Copies.DistinctBy(c => c.UserAssetId).ToList();
            inv.Ambiguous = res.Value.Ambiguous;
            inv.ReadUtc = _clock();
            inv.Complete = true;
        }
        else
        {
            inv.Problem = res.Problem;
            Logging.Log.Warn($"[limiteds] leitura recusada: {res.Problem}");
            return new(inv, s.Rotated, res.Outcome == ReadOutcome.Unauthorized, res.Outcome == ReadOutcome.RateLimited ? res.RetryAfter ?? TimeSpan.FromSeconds(60) : null);
        }

        // Roblox's collectibles list does not have every Limited (new UGC Limiteds, e.g. FonteAlfa's withdrawals of 21/11).
        // Rolimons lists them: every copy Rolimons has and Roblox does not is added (marked as from Rolimons; the Limiteds
        // tab then checks it against the account's own copies on the Marketplace). Rolimons also says which copies are on
        // hold when Roblox does not (it never overrides Roblox).
        inv.RolimonsProblem = null;
        if (rolimons is not null)
        {
            var player = await rolimons.GetPlayerAsync(userId, ct).ConfigureAwait(false);
            if (player.Ok)
            {
                var (assetOf, holds) = player.Value;
                inv.HoldsFromRolimons = holds;
                var known = inv.Copies.Select(c => c.UserAssetId).ToHashSet();
                var held = holds.ToHashSet();
                int added = 0;
                foreach (var (uaid, asset) in assetOf.Where(kv => !known.Contains(kv.Key)).OrderBy(kv => kv.Key))
                {
                    var info = itemInfo?.Invoke(asset) ?? (null, null);
                    inv.Copies.Add(new LimitedCopy(uaid, asset, info.Name ?? $"Item {asset}", null, info.Rap, held.Contains(uaid) ? true : null, FromRolimons: true));
                    added++;
                }
                Logging.Log.Info($"[limiteds] Rolimons: {assetOf.Count} cópias listadas, {added} fora da lista do Roblox");
            }
            else
            {
                inv.RolimonsProblem = player.Problem ?? "sem resposta";
                Logging.Log.Warn($"[limiteds] Rolimons (itens da conta): {player.Problem}");
            }
        }
        MarkSeen(inv, previous, _clock());
        return new(inv, s.Rotated, false, null);
    }
}
