using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Limiteds;

public enum PriceSource { Manual, Rolimons, Rap, None }

/// <summary>A copy with its price, net and class.</summary>
public sealed record ValuedCopy(LimitedCopy Copy, long? Price, PriceSource Source, long? Net, bool Small, bool OnHold);

/// <summary>
/// Space for new lots in one account:
/// ocupado = saldo + pendentes + estoque líquido + entradas comprometidas;
/// espaço = max(0, min(meta líquida, cota restante de 30 dias) − ocupado). The same with the quota the 30-day window will
/// have released by the time the sale's Robux arrive (<see cref="DistributionSettings.DaysToSale"/>).
/// </summary>
public sealed record SpaceView(long? Occupied, long? StockNet, long? SpaceNow, long? SpaceAtSale, long? Ceiling, int Copies, int OnHold, int Smalls,
                               int Unpriced, IReadOnlyList<string> Blocks)
{
    public bool Blocked => Blocks.Count > 0;
    // The parts of the sum, to show it ("teto − ocupado").
    public long? Balance { get; init; }
    public long? Pending { get; init; }
    public long Committed { get; init; }
    public long? RemainingMonthly { get; init; }
    /// <summary>Quota the 30-day window gives back by the time the sale pays (DaysToSale).</summary>
    public long? ReleasedBySale { get; init; }
    public long? CeilingAtSale { get; init; }
    public long Target { get; init; }
}

public static class Valuation
{
    /// <summary>Net per copy: price × (1 − fee), rounded down.</summary>
    public static long Net(long price, int feePercent) => price * (100 - Math.Clamp(feePercent, 0, 100)) / 100;

    public static (long? Price, PriceSource Source) PriceOf(long assetId, long? copyRap, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket>? market)
    {
        if (s.ManualPrice.TryGetValue(assetId, out long manual) && manual > 0) return (manual, PriceSource.Manual);
        var m = market?.GetValueOrDefault(assetId);
        if (s.PreferRolimonsValue && m?.RolimonsValue is { } v && v > 0) return (v, PriceSource.Rolimons);
        if (KnownRap(m?.Rap, copyRap) is { } rap) return (rap, PriceSource.Rap);
        return (null, PriceSource.None);
    }

    /// <summary>Small = support item: RAP at most the configured limit (RAP, not the sale price, decides the class).</summary>
    /// <remarks>
    /// Small only when every known price signal is within the limit: the market RAP (Rolimons, or Roblox's resale data —
    /// which can be off for migrated items), the copy's own RAP from the inventory, and the Rolimons value. One signal
    /// above the limit means it is not a support item. No signal at all (RAP 0 / unknown) is not a Small either.
    /// </remarks>
    public static bool IsSmall(LimitedCopy c, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket>? market) =>
        IsSmallBy(s.SmallRapLimit, market?.GetValueOrDefault(c.AssetId) is { } m ? [m.Rap, m.RolimonsValue, c.Rap] : [c.Rap]);

    public static bool IsSmallBy(long limit, params long?[] signals)
    {
        var known = signals.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return known.Count > 0 && known.All(v => v <= limit);
    }

    /// <summary>The first positive RAP (market, then the copy's own); 0 means "not known".</summary>
    public static long? KnownRap(long? market, long? copy) => market is > 0 ? market : copy is > 0 ? copy : null;

    public static List<ValuedCopy> Value(AccountInventory inv, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket>? market) =>
        inv.Copies.DistinctBy(c => c.UserAssetId).Select(c =>
        {
            var (price, src) = PriceOf(c.AssetId, c.Rap, s, market);
            return new ValuedCopy(c, price, src, price is { } p ? Net(p, s.FeePercent) : null, IsSmall(c, s, market), inv.IsOnHold(c));
        }).ToList();

    public static SpaceView Space(long userId, AccountFinance? f, AccountInventory? inv, DistributionSettings s,
                                  IReadOnlyDictionary<long, ItemMarket>? market, DateTimeOffset now, long committed = 0)
    {
        var blocks = new List<string>();
        var q = f is null ? null : QuotaCalculator.Compute(f, now);
        var plus = PlusStatus.Evaluate(f, now);
        if (plus.Label is not (PlusLabel.Active or PlusLabel.Trial)) blocks.Add("sem Plus ativo confirmado");
        if (q?.RemainingMonthly is not { } rem) blocks.Add("cota de 30 dias não lida");
        else if (rem <= 0) blocks.Add("cota de 30 dias esgotada");
        if (f?.Robux is null || f.PendingRobux is null) blocks.Add("saldo ou pendentes não lidos");
        if (inv is null || !inv.Complete) blocks.Add("inventário não lido por completo");

        var valued = inv is null ? [] : Value(inv, s, market);
        var stock = valued.Where(v => !v.Small).ToList();
        int unpriced = stock.Count(v => v.Net is null);
        if (unpriced > 0) blocks.Add(unpriced == 1 ? "1 cópia sem preço estimado" : $"{unpriced} cópias sem preço estimado");
        long? stockNet = inv is null || unpriced > 0 ? null : stock.Sum(v => v.Net!.Value);
        long? occupied = f?.Robux is { } bal && f.PendingRobux is { } pen && stockNet is { } sn ? bal + pen + sn + committed : null;

        long target = s.TargetNet(userId);
        long? ceiling = q?.RemainingMonthly is { } r ? (target > 0 ? Math.Min(target, r) : r) : null;
        long? releasedBySale = q?.RemainingMonthly is { } r2
            ? r2 + q.MonthlyReleases.Where(x => x.AtUtc > now && x.AtUtc <= now.AddDays(s.DaysToSale)).Sum(x => x.Amount) : null;
        long? ceilingAtSale = releasedBySale is { } ra ? (target > 0 ? Math.Min(target, ra) : ra) : null;
        long? spaceNow = ceiling is { } c && occupied is { } o ? Math.Max(0, c - o) : null;
        long? spaceAtSale = ceilingAtSale is { } c2 && occupied is { } o2 ? Math.Max(0, c2 - o2) : null;
        return new SpaceView(occupied, stockNet, spaceNow, spaceAtSale, ceiling, valued.Count, valued.Count(v => v.OnHold), valued.Count(v => v.Small),
                             unpriced, blocks)
        {
            Balance = f?.Robux, Pending = f?.PendingRobux, Committed = committed, RemainingMonthly = q?.RemainingMonthly,
            ReleasedBySale = releasedBySale is { } rb && q?.RemainingMonthly is { } rm ? rb - rm : null,
            CeilingAtSale = ceilingAtSale, Target = target,
        };
    }
}
