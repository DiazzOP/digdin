namespace RbxSwitcher.Core.Limiteds;

public enum ArrivalKind { Trade, Purchase }

/// <summary>
/// A copy that came into an account: bought on the store (Roblox's purchase records) or received in a trade (the
/// account's completed trades). Lives in the encrypted vault.
/// </summary>
/// <param name="Key">Dedupe key: the purchase record id, or "t{trade}:{n}".</param>
/// <param name="AtUtc">Purchase: when it was bought. Trade: when it completed, if the app saw the acceptance
/// (<paramref name="Exact"/>); otherwise when the trade was created — the earliest it can have completed.</param>
public sealed record Arrival(string Key, ArrivalKind Kind, long AssetId, long? UserAssetId, int? Serial, string Name, DateTimeOffset AtUtc, bool Exact,
                             long? TradeId = null);

/// <summary>An account's recent arrivals, and the trades the app already read (completed trades never change).</summary>
public sealed class AccountArrivals
{
    public List<Arrival> Items { get; set; } = [];
    public List<long> TradesRead { get; set; } = [];
    /// <summary>Trades accepted in this account's tab of the app, and when: the exact moment they completed.</summary>
    public Dictionary<long, DateTimeOffset> Accepted { get; set; } = [];
    public DateTimeOffset? ReadUtc { get; set; }
    public string? Problem { get; set; }

    public void Add(Arrival a)
    {
        int i = Items.FindIndex(x => x.Key == a.Key);
        if (i < 0) Items.Add(a);
        else if (!Items[i].Exact && a.Exact) Items[i] = a;
    }

    /// <summary>Keeps what can still explain a hold (purchases hold 7 days).</summary>
    public void Prune(DateTimeOffset now)
    {
        Items.RemoveAll(a => now - a.AtUtc > HoldClock.Lookback);
        foreach (var id in Accepted.Where(kv => now - kv.Value > HoldClock.Lookback).Select(kv => kv.Key).ToList()) Accepted.Remove(id);
        if (TradesRead.Count > 300) TradesRead.RemoveRange(0, TradesRead.Count - 300);
    }
}

/// <summary>When a held copy leaves the hold.</summary>
/// <param name="EarliestUtc">The earliest moment (equal to <paramref name="LatestUtc"/> when the arrival is known to the minute).</param>
/// <param name="LatestUtc">By then it is surely out of the hold.</param>
/// <param name="Kind">How it arrived; null when no trade or purchase explains it (only the app's own sighting).</param>
/// <param name="ArrivedUtc">When it arrived (for a trade the app did not see accepted: when the trade was created).</param>
/// <param name="Seen">When the app first saw the copy in the account, when known.</param>
public sealed record HoldRelease(DateTimeOffset EarliestUtc, DateTimeOffset LatestUtc, ArrivalKind? Kind, DateTimeOffset? ArrivedUtc, bool Exact,
                                 long? TradeId, DateTimeOffset? Seen)
{
    /// <summary>Not known to the minute: show both ends.</summary>
    public bool IsRange => LatestUtc - EarliestUtc > TimeSpan.FromMinutes(10);
}

/// <summary>
/// Roblox's hold on a Limited copy: exactly <see cref="AfterTrade"/> after it is received in a trade, exactly
/// <see cref="AfterPurchase"/> after it is bought with Robux on the store. Each held copy is matched to one arrival —
/// by copy id, then by serial, then the newest arrival of that item no later than when the app first saw the copy —
/// and without one, the app's own sighting gives both possible dates.
/// </summary>
public static class HoldClock
{
    public static readonly TimeSpan AfterTrade = TimeSpan.FromDays(2);
    public static readonly TimeSpan AfterPurchase = TimeSpan.FromDays(7);
    /// <summary>How far back an arrival can still explain a hold.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(9);
    /// <summary>Trades older than this cannot explain a hold any more (2 days + slack).</summary>
    public static readonly TimeSpan TradeLookback = TimeSpan.FromDays(3);
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    /// <param name="held">The account's copies on hold.</param>
    /// <param name="acceptedAnywhere">Trades accepted in any account's tab (trade id → when).</param>
    public static Dictionary<long, HoldRelease> ForAccount(IEnumerable<LimitedCopy> held, AccountArrivals? arrivals,
                                                           IReadOnlyDictionary<long, DateTimeOffset> acceptedAnywhere, DateTimeOffset now)
    {
        var result = new Dictionary<long, HoldRelease>();
        var pool = (arrivals?.Items ?? []).Where(a => now - a.AtUtc <= Lookback).ToList();
        var copies = held.Where(c => c.UserAssetId > 0).DistinctBy(c => c.UserAssetId)
            .OrderBy(c => c.SeenUtc ?? DateTimeOffset.MaxValue).ThenBy(c => c.UserAssetId).ToList();

        void Pass(Func<LimitedCopy, Arrival?> find)
        {
            foreach (var c in copies.Where(c => !result.ContainsKey(c.UserAssetId)).ToList())
                if (find(c) is { } a) { result[c.UserAssetId] = From(c, a, acceptedAnywhere); pool.Remove(a); }
        }
        Pass(c => pool.FirstOrDefault(a => a.UserAssetId == c.UserAssetId));
        Pass(c => c.Serial is { } s ? pool.FirstOrDefault(a => a.AssetId == c.AssetId && a.Serial == s) : null);
        // The rest: the newest arrival of the item that is not later than when the app first saw the copy (anonymous
        // arrivals only — one that names another copy or serial belongs to that copy).
        Pass(c => pool.Where(a => a.AssetId == c.AssetId && (a.UserAssetId is null || a.UserAssetId <= 0) && (a.Serial is null || c.Serial is null || a.Serial == c.Serial)
                                  && (c.SeenUtc is not { } seen || a.AtUtc <= seen + Slack))
                      .OrderByDescending(a => a.AtUtc).FirstOrDefault());

        // No arrival found: the app's own sighting. It came in before then, by trade or purchase.
        foreach (var c in copies.Where(c => !result.ContainsKey(c.UserAssetId) && c.SeenUtc is not null))
        {
            var seen = c.SeenUtc!.Value;
            result[c.UserAssetId] = new HoldRelease(seen + AfterTrade, seen + AfterPurchase, null, null, false, null, seen);
        }
        return result;
    }

    private static HoldRelease From(LimitedCopy c, Arrival a, IReadOnlyDictionary<long, DateTimeOffset> accepted)
    {
        if (a.Kind == ArrivalKind.Purchase)
            return new HoldRelease(a.AtUtc + AfterPurchase, a.AtUtc + AfterPurchase, ArrivalKind.Purchase, a.AtUtc, true, null, c.SeenUtc);
        if (a.Exact || a.TradeId is { } t && accepted.ContainsKey(t))
        {
            var at = a.Exact ? a.AtUtc : accepted[a.TradeId!.Value];
            return new HoldRelease(at + AfterTrade, at + AfterTrade, ArrivalKind.Trade, at, true, a.TradeId, c.SeenUtc);
        }
        // Created at AtUtc; completed then or later, and before the app saw the copy.
        var latest = c.SeenUtc is { } seen && seen > a.AtUtc ? seen : a.AtUtc;
        return new HoldRelease(a.AtUtc + AfterTrade, latest + AfterTrade, ArrivalKind.Trade, a.AtUtc, false, a.TradeId, c.SeenUtc);
    }

    /// <summary>What a trade brought to <paramref name="userId"/>: the other side's items.</summary>
    public static IEnumerable<Arrival> FromTrade(TradeDetail t, long userId, DateTimeOffset? acceptedUtc, DateTimeOffset fallbackCreated)
    {
        var at = acceptedUtc ?? t.CreatedUtc ?? fallbackCreated;
        int n = 0;
        foreach (var item in t.Offers.Where(o => o.UserId != userId).SelectMany(o => o.Items))
            yield return new Arrival($"t{t.Id}:{n++}", ArrivalKind.Trade, item.AssetId, item.UserAssetId, item.Serial, item.Name, at, acceptedUtc is not null, t.Id);
    }

    /// <summary>The store purchases of Limited items (records of type Asset).</summary>
    public static IEnumerable<Arrival> FromPurchases(IEnumerable<SaleRecord> purchases) =>
        purchases.Select(p => new Arrival("p" + p.Id, ArrivalKind.Purchase, p.AssetId, null, null, p.Name, p.CreatedUtc, true));
}
