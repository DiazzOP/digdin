namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// The inventory the distribution plan works with. Copies only Rolimons lists (new UGC Limiteds, which Roblox's
/// collectibles list leaves out) can lag behind a trade by hours:
/// <list type="bullet">
/// <item>a copy a completed step took out of the account is dropped while Rolimons still lists it there;</item>
/// <item>a copy a completed step brought in is kept (on hold) for a day while the reads do not show it yet;</item>
/// <item>the account's own copies on the Marketplace (read by the Limiteds tab after the inventory) cap how many
/// Rolimons-only copies of an item are real, and say when they are all on hold.</item>
/// </list>
/// Copies Roblox itself lists are never touched. The stored inventory is not changed: a new one is returned.
/// </summary>
public static class PlanInventory
{
    /// <summary>Rolimons usually shows a trade within hours; after this its list is trusted again.</summary>
    public static readonly TimeSpan RolimonsLag = TimeSpan.FromDays(2);

    /// <summary>A copy a trade brought in cannot be sold this soon (trade hold): kept while the reads miss it.</summary>
    public static readonly TimeSpan ArrivalGrace = TimeSpan.FromDays(1);

    private readonly record struct Move(bool Into, DateTimeOffset AtUtc, CopyRef Copy);

    public static AccountInventory? For(long userId, AccountInventory? inv, AccountListings? own, IEnumerable<PlanStep> steps, DateTimeOffset now)
    {
        if (inv is null) return null;

        // The last move of each copy into or out of this account by a completed step, recent ones only.
        var moves = new Dictionary<long, Move>();
        foreach (var s in steps.Where(x => x.State == StepState.Completed && x.CompletedUtc is { } c && now - c <= RolimonsLag
                                            && (x.SourceId == userId || x.DestinationId == userId))
                               .OrderBy(x => x.CompletedUtc))
        {
            var at = s.CompletedUtc!.Value;
            var fromSource = s.ActualFromSource ?? [.. s.Limiteds, .. s.SourceSmall is { } ss ? new[] { ss } : []];
            var fromDestination = s.ActualFromDestination ?? (s.DestinationSmall is { } ds ? [ds] : []);
            foreach (var c in fromSource.Where(c => c.UserAssetId > 0)) moves[c.UserAssetId] = new(s.DestinationId == userId, at, c);
            foreach (var c in fromDestination.Where(c => c.UserAssetId > 0)) moves[c.UserAssetId] = new(s.SourceId == userId, at, c);
        }

        bool changed = false;
        var copies = new List<LimitedCopy>();
        foreach (var c in inv.Copies)
        {
            // Left in a completed trade; Rolimons has not caught up yet.
            if (c.FromRolimons && moves.TryGetValue(c.UserAssetId, out var m) && !m.Into) { changed = true; continue; }
            copies.Add(c);
        }

        // The account's own copies on the Marketplace, when read after the inventory (an older read says nothing about
        // copies that arrived since) and after any trade of the item in the plan.
        if (own is not null && inv.ReadUtc is { } invRead)
            foreach (var group in copies.Where(c => c.FromRolimons).GroupBy(c => c.AssetId).ToList())
            {
                if (!own.OwnedByItem.TryGetValue(group.Key, out int owned) || !own.ItemReadUtc.TryGetValue(group.Key, out var read)) continue;
                if (read < invRead || moves.Values.Any(m => m.Copy.AssetId == group.Key && m.AtUtc > read)) continue;
                int room = Math.Max(0, owned - copies.Count(c => c.AssetId == group.Key && !c.FromRolimons));
                // Copies a trade of the plan brought in are the surest; then the oldest ids.
                var keep = group.OrderBy(c => moves.ContainsKey(c.UserAssetId) ? 0 : 1).ThenBy(c => c.UserAssetId).Take(room).ToHashSet();
                bool allHeld = owned > 0 && own.HeldByItem.GetValueOrDefault(group.Key) >= owned;
                foreach (var c in group)
                {
                    int i = copies.IndexOf(c);
                    if (!keep.Contains(c)) { copies.RemoveAt(i); changed = true; }
                    else if (allHeld && c.OnHold != true) { copies[i] = c with { OnHold = true }; changed = true; }
                }
            }

        // Copies a completed step brought in that the reads do not show yet.
        foreach (var (uaid, m) in moves.Where(kv => kv.Value.Into && now - kv.Value.AtUtc <= ArrivalGrace))
            if (copies.All(c => c.UserAssetId != uaid))
            {
                copies.Add(new LimitedCopy(uaid, m.Copy.AssetId, m.Copy.Name, m.Copy.Serial, null, OnHold: true, FromRolimons: true));
                changed = true;
            }

        if (!changed) return inv;
        return new AccountInventory
        {
            Copies = copies, Ambiguous = inv.Ambiguous, HoldsFromRolimons = inv.HoldsFromRolimons, ReadUtc = inv.ReadUtc,
            LastAttemptUtc = inv.LastAttemptUtc, AdjustedUtc = inv.AdjustedUtc, Complete = inv.Complete, Problem = inv.Problem,
            RolimonsProblem = inv.RolimonsProblem,
        };
    }
}
