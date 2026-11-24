using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fictitious copies: when a held copy leaves the hold (2 days after a trade, 7 after a store purchase).</summary>
public class HoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<long, DateTimeOffset> NoAccepts = [];
    private static LimitedCopy Held(long uaid, long asset, int? serial = null, DateTimeOffset? seen = null) => new(uaid, asset, "Item", serial, 200, true, SeenUtc: seen);

    [Fact]
    public void A_trade_accepted_in_the_app_releases_exactly_two_days_later()
    {
        var accepted = Now.AddHours(-5);
        var arrivals = new AccountArrivals { Items = [new Arrival("t9:0", ArrivalKind.Trade, 7, 901, 12, "Inkwell Egg", Now.AddHours(-6), false, 9)] };
        var h = HoldClock.ForAccount([Held(901, 7)], arrivals, new Dictionary<long, DateTimeOffset> { [9] = accepted }, Now)[901];
        Assert.Equal((ArrivalKind.Trade, true, false), (h.Kind!.Value, h.Exact, h.IsRange));
        Assert.Equal(accepted.AddDays(2), h.LatestUtc);
        Assert.Equal(h.LatestUtc, h.EarliestUtc);
        Assert.Equal(9, h.TradeId);
    }

    [Fact]
    public void A_store_purchase_releases_exactly_seven_days_later()
    {
        var bought = Now.AddDays(-3);
        var arrivals = new AccountArrivals();
        arrivals.Add(HoldClock.FromPurchases([new SaleRecord("b1", bought, 7, "Rocket Eggscape", 195, false)]).Single());
        var h = HoldClock.ForAccount([Held(902, 7)], arrivals, NoAccepts, Now)[902];
        Assert.Equal((ArrivalKind.Purchase, true), (h.Kind!.Value, h.Exact));
        Assert.Equal(bought.AddDays(7), h.EarliestUtc);
        Assert.Equal(bought.AddDays(7), h.LatestUtc);
    }

    [Fact]
    public void A_trade_accepted_elsewhere_lies_between_its_creation_and_when_the_app_saw_the_copy()
    {
        var created = Now.AddHours(-10);
        var seen = Now.AddHours(-9);
        var arrivals = new AccountArrivals { Items = [new Arrival("t5:0", ArrivalKind.Trade, 7, null, null, "Shaggy", created, false, 5)] };
        var h = HoldClock.ForAccount([Held(903, 7, seen: seen)], arrivals, NoAccepts, Now)[903];
        Assert.Equal((created.AddDays(2), seen.AddDays(2), false, true), (h.EarliestUtc, h.LatestUtc, h.Exact, h.IsRange));
    }

    [Fact]
    public void Without_a_trade_or_purchase_the_apps_sighting_gives_both_possible_dates()
    {
        var seen = Now.AddHours(-2);
        var h = HoldClock.ForAccount([Held(904, 7, seen: seen)], null, NoAccepts, Now)[904];
        Assert.Null(h.Kind);
        Assert.Equal((seen.AddDays(2), seen.AddDays(7)), (h.EarliestUtc, h.LatestUtc));
        // Nothing at all: no date is invented.
        Assert.Empty(HoldClock.ForAccount([Held(905, 7)], null, NoAccepts, Now));
    }

    [Fact]
    public void Two_copies_of_an_item_take_one_arrival_each_by_id_then_by_when_they_were_seen()
    {
        var arrivals = new AccountArrivals
        {
            Items =
            [
                new Arrival("p1", ArrivalKind.Purchase, 7, null, null, "Egg", Now.AddDays(-4), true),
                new Arrival("p2", ArrivalKind.Purchase, 7, null, null, "Egg", Now.AddDays(-1), true),
                new Arrival("t3:0", ArrivalKind.Trade, 7, 910, null, "Egg", Now.AddHours(-3), true, 3),
            ],
        };
        var copies = new[] { Held(910, 7), Held(911, 7, seen: Now.AddDays(-4).AddMinutes(20)), Held(912, 7, seen: Now.AddDays(-1).AddMinutes(15)) };
        var h = HoldClock.ForAccount(copies, arrivals, NoAccepts, Now);
        Assert.Equal(ArrivalKind.Trade, h[910].Kind);                                   // named by the trade
        Assert.Equal(Now.AddDays(-4).AddDays(7), h[911].LatestUtc);                     // the purchase before it was seen
        Assert.Equal(Now.AddDays(-1).AddDays(7), h[912].LatestUtc);
    }

    [Fact]
    public void A_trade_brings_the_other_sides_items()
    {
        var t = new TradeDetail(77, "Completed", Now.AddHours(-1), [
            new TradeOffer(1, [new TradeItem(500, 3, "Dominus", 4, 5_000)], 0),
            new TradeOffer(12, [new TradeItem(600, 7, "Egg", 2, 200)], 0)]);
        var got = HoldClock.FromTrade(t, 12, null, Now).ToList();
        Assert.Equal((500L, 3L, false), (got.Single().UserAssetId!.Value, got.Single().AssetId, got.Single().Exact));
        Assert.Equal(Now.AddHours(-1), got.Single().AtUtc);
        Assert.True(HoldClock.FromTrade(t, 12, Now, Now).Single().Exact);
    }

    [Fact]
    public void The_app_remembers_when_it_first_saw_each_copy()
    {
        var first = new AccountInventory { Complete = true, ReadUtc = Now.AddMinutes(-25), Copies = [new LimitedCopy(1, 7, "A", null, 100, null)] };
        InventoryRefresher.MarkSeen(first, null, Now.AddMinutes(-25));
        Assert.Null(first.Copies[0].SeenUtc);                                           // the first read: already there

        var next = new AccountInventory { Complete = true, ReadUtc = Now, Copies = [new LimitedCopy(1, 7, "A", null, 100, null), new LimitedCopy(2, 7, "A", null, 100, true)] };
        InventoryRefresher.MarkSeen(next, first, Now);
        Assert.Null(next.Copies[0].SeenUtc);
        Assert.Equal(Now, next.Copies[1].SeenUtc);                                      // arrived since the last read

        var later = new AccountInventory { Complete = true, ReadUtc = Now.AddMinutes(25), Copies = [new LimitedCopy(2, 7, "A", null, 100, true)] };
        InventoryRefresher.MarkSeen(later, next, Now.AddMinutes(25));
        Assert.Equal(Now, later.Copies[0].SeenUtc);                                     // kept
    }
}
