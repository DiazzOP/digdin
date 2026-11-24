namespace RbxSwitcher.Core.Finance;

public enum LimitKind { Daily, Monthly }

/// <summary>Who lowered the limit: Roblox's own cap (e.g. Plus ended) or the restriction set in the account settings.</summary>
public enum LimitSource { Roblox, Restriction }

/// <summary>An effective transfer limit that went down between two successful reads. Numbers only.</summary>
public sealed class LimitDrop
{
    public LimitKind Kind { get; set; }
    public LimitSource Source { get; set; }
    /// <summary>The effective limit before the drop (kept when it drops again, so recovery is measured against it).</summary>
    public long From { get; set; }
    public long To { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

/// <summary>
/// Notices when the effective daily or 30-day limit (min of Roblox's cap and the owner's restriction)
/// goes down, so the screen can say so instead of silently showing the smaller number. A drop stays until
/// the person acknowledges it or the limit is back to where it was. Only successful reads are compared.
/// </summary>
public static class LimitWatch
{
    /// <summary>Drops not yet acknowledged.</summary>
    public static IReadOnlyList<LimitDrop> Pending(AccountFinance? f) =>
        f is null ? [] : f.LimitDrops.Where(d => f.LimitDropsSeenUtc is not { } seen || d.AtUtc > seen).OrderBy(d => d.Kind).ToList();

    /// <summary>
    /// A refresh started before "Ok, entendi" returns a copy without it: keep the acknowledgement (and what it hid).
    /// </summary>
    public static void KeepAcknowledgement(AccountFinance? current, AccountFinance next)
    {
        if (current?.LimitDropsSeenUtc is not { } seen || next.LimitDropsSeenUtc >= seen) return;
        next.LimitDropsSeenUtc = seen;
        next.LimitDrops.RemoveAll(d => d.AtUtc <= seen);
    }

    /// <summary>"Ok, entendi": hides every current drop.</summary>
    public static void Acknowledge(AccountFinance f, DateTimeOffset now)
    {
        f.LimitDropsSeenUtc = now;
        f.LimitDrops.Clear();
    }

    /// <summary>
    /// Compares the limits before and after a refresh in which both caps and restriction were read.
    /// <paramref name="hadBefore"/> = both had been read successfully before (otherwise there is nothing to compare).
    /// </summary>
    public static void Track(AccountFinance f, bool hadBefore, TransferCaps? oldCaps, TransferRestriction? oldRestriction,
                             TransferCaps? newCaps, TransferRestriction? newRestriction, DateTimeOffset now)
    {
        if (f.LimitDropsSeenUtc is { } seen) f.LimitDrops.RemoveAll(d => d.AtUtc <= seen);
        Check(LimitKind.Daily, oldCaps?.Daily, oldRestriction?.Daily, newCaps?.Daily, newRestriction?.Daily);
        Check(LimitKind.Monthly, oldCaps?.Monthly, oldRestriction?.Monthly, newCaps?.Monthly, newRestriction?.Monthly);

        void Check(LimitKind kind, long? oldCap, long? oldR, long? newCap, long? newR)
        {
            long? after = QuotaCalculator.Effective(newCap, newR);
            if (after is not { } to) return; // nothing usable now: keep what we had
            var existing = f.LimitDrops.Find(d => d.Kind == kind);
            if (existing is not null)
            {
                if (to >= existing.From) { f.LimitDrops.Remove(existing); return; } // back to (or above) the original
                if (to != existing.To)
                {
                    existing.To = to;
                    existing.Source = SourceOf(oldCap, oldR, newCap, newR, to);
                    existing.AtUtc = now;
                }
                return;
            }
            if (!hadBefore || QuotaCalculator.Effective(oldCap, oldR) is not { } from || to >= from) return;
            f.LimitDrops.Add(new LimitDrop { Kind = kind, From = from, To = to, Source = SourceOf(oldCap, oldR, newCap, newR, to), AtUtc = now });
        }
    }

    private static LimitSource SourceOf(long? oldCap, long? oldR, long? newCap, long? newR, long to)
    {
        bool capDown = newCap is { } nc && (oldCap is null || nc < oldCap);
        bool restrictionDown = newR is { } nr && (oldR is null || nr < oldR);
        if (capDown != restrictionDown) return capDown ? LimitSource.Roblox : LimitSource.Restriction;
        return newR == to && newCap != to ? LimitSource.Restriction : LimitSource.Roblox; // both or neither: the one that binds
    }
}
