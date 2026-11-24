using System.Net;
using System.Text;
using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

public class LimitedParserTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    [Fact]
    public void Collectibles_count_each_copy_once_and_set_aside_copies_without_id()
    {
        var (copies, ambiguous, next) = LimitedParsers.CollectiblesPage(J("""
            {"previousPageCursor":null,"nextPageCursor":"p2","data":[
              {"userAssetId":111,"serialNumber":12,"assetId":5,"name":"Valk","recentAveragePrice":3000,"isOnHold":false},
              {"userAssetId":112,"serialNumber":null,"assetId":6,"name":"Egg","recentAveragePrice":400},
              {"assetId":7,"name":"Sem id","recentAveragePrice":900}]}
            """));
        Assert.Equal(2, copies.Count);
        Assert.Equal(1, ambiguous);
        Assert.Equal("p2", next);
        Assert.False(copies[0].OnHold);
        Assert.Null(copies[1].OnHold); // Roblox did not say
        var e = Assert.Throws<FormatException>(() => LimitedParsers.CollectiblesPage(J("""{"data":[{"userAssetId":1,"name":"segredo"}]}""")));
        Assert.DoesNotContain("segredo", e.Message);
    }

    [Fact]
    public void Resale_data_gives_rap_and_sales_per_day_of_the_last_30_days()
    {
        var (rap, perDay) = LimitedParsers.ResaleData(J($$"""
            {"recentAveragePrice":2500,"volumeDataPoints":[
              {"value":30,"date":"{{Now.AddDays(-2):O}}"},{"value":15,"date":"{{Now.AddDays(-20):O}}"},{"value":999,"date":"{{Now.AddDays(-40):O}}"}]}
            """), Now);
        Assert.Equal(2500, rap);
        Assert.Equal(1.5, perDay); // (30 + 15) / 30 — the 40-day-old point is outside
    }

    [Fact]
    public void Rolimons_items_and_holds()
    {
        var items = LimitedParsers.RolimonsItems(J("""{"success":true,"items":{"5":["Valk","",3000,3500,-1,2,-1,-1,-1,-1],"6":["Egg","",400,-1,-1,-1,-1,-1,-1,-1]}}"""));
        Assert.Equal(("Valk", 3000L, 3500L, 2), (items[5].Name, items[5].Rap, items[5].Value, items[5].Demand));
        Assert.Null(items[6].Value); // -1 = Rolimons has no value
        Assert.Equal([112L], LimitedParsers.RolimonsHolds(J("""{"success":true,"playerAssets":{"6":[112]},"holds":[112]}""")));
    }
}

public class ValuationTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    [Fact]
    public void Net_is_price_minus_fee_rounded_down()
    {
        Assert.Equal(2_100, Valuation.Net(3_000, 30));
        Assert.Equal(699, Valuation.Net(999, 30)); // 699.3 → 699
    }

    [Fact]
    public void Price_source_order_is_manual_then_rolimons_if_chosen_then_rap()
    {
        var s = new DistributionSettings();
        var market = new Dictionary<long, ItemMarket> { [5] = new(5, "Valk", 3_000, 3_600, 2, 1.2, Now) };
        Assert.Equal((3_000L, PriceSource.Rap), Valuation.PriceOf(5, 2_800, s, market));
        s.PreferRolimonsValue = true;
        Assert.Equal((3_600L, PriceSource.Rolimons), Valuation.PriceOf(5, 2_800, s, market));
        s.ManualPrice[5] = 4_000;
        Assert.Equal((4_000L, PriceSource.Manual), Valuation.PriceOf(5, 2_800, s, market));
        Assert.Equal((null, PriceSource.None), Valuation.PriceOf(9, null, s, market));
    }

    private static AccountFinance Finance(long robux, long pending, long sentLast30, long monthly = 10_000)
    {
        var f = QuotaCalculatorTests.Fresh(robux, 5_000, monthly, sentLast30 > 0 ? [(Now.AddDays(-25), sentLast30)] : []);
        f.PendingRobux = pending;
        return f;
    }

    private static AccountInventory Inv(params LimitedCopy[] c) => new() { Copies = [.. c], Complete = true, ReadUtc = Now };

    [Fact]
    public void Space_follows_the_desk_formula_and_counts_each_copy_once()
    {
        // Remaining 30-day quota 8.000 (10.000 − 2.000 sent), occupied 3.500 → space 4.500 when the ceiling is ≥ 8.000.
        var s = new DistributionSettings { SmallRapLimit = 500 };
        var copy = new LimitedCopy(1, 5, "Valk", null, 2_000, false); // net 1.400
        var f = Finance(robux: 1_600, pending: 500, sentLast30: 2_000);
        var v = Valuation.Space(77, f, Inv(copy, copy, new LimitedCopy(2, 6, "Egg", null, 300, false)), s, null, Now);
        Assert.Empty(v.Blocks);
        Assert.Equal(1_400, v.StockNet);    // the duplicate copy counts once; the Small is not stock
        Assert.Equal(3_500, v.Occupied);
        Assert.Equal(4_500, v.SpaceNow);
        Assert.Equal(1, v.Smalls);

        s.DefaultTargetNet = 6_000;         // the ceiling caps the space
        Assert.Equal(2_500, Valuation.Space(77, f, Inv(copy), s, null, Now).SpaceNow);
    }

    [Fact]
    public void Space_at_sale_adds_what_the_30_day_window_releases_before_the_robux_arrive()
    {
        var s = new DistributionSettings { DaysToSale = 7 };
        var f = Finance(robux: 0, pending: 0, sentLast30: 2_000); // the 2.000 sent 25 days ago leave the window in 5 days
        var v = Valuation.Space(77, f, Inv(), s, null, Now);
        Assert.Equal(8_000, v.SpaceNow);
        Assert.Equal(10_000, v.SpaceAtSale);
    }

    [Fact]
    public void Missing_reads_block_new_lots_with_the_reason()
    {
        var s = new DistributionSettings();
        var f = Finance(1_000, 0, 0);
        f.Plus = new PlusInfo { NoSubscription = true };
        var v = Valuation.Space(77, f, new AccountInventory(), s, null, Now);
        Assert.Contains("sem Plus ativo confirmado", v.Blocks);
        Assert.Contains("inventário não lido por completo", v.Blocks);
        var unpriced = Valuation.Space(77, Finance(1_000, 0, 0), Inv(new LimitedCopy(3, 9, "Sem preço", null, null, false)), s, null, Now);
        Assert.Null(unpriced.SpaceNow);
        Assert.Contains("1 cópia sem preço estimado", unpriced.Blocks);
    }
}

public class InventoryRefresherTests
{
    private sealed class Fake : HttpMessageHandler
    {
        public long UserId = 77;
        public bool WithdrawalOnHold;
        public List<string> Hosts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Hosts.Add(req.RequestUri!.Host + (req.Headers.Contains("Cookie") ? "+cookie" : ""));
            string p = req.RequestUri.AbsolutePath;
            object? body = p == "/v1/users/authenticated" ? new { id = UserId, name = "x", displayName = "x" }
                : p.EndsWith("/assets/collectibles") ? new { nextPageCursor = (string?)null, data = new object[] { new { userAssetId = 111L, assetId = 5L, name = "Valk", recentAveragePrice = 3000 } } }
                : p.StartsWith("/players/v1/playerassets/") ? (object)(WithdrawalOnHold
                    ? new { success = true, playerAssets = new Dictionary<string, long[]> { ["5"] = [111], ["900"] = [222, 223], ["901"] = [224] }, holds = new[] { 111L, 222L, 223L } }
                    : new { success = true, holds = new[] { 111L } })
                : null;
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Reads_the_account_and_takes_holds_from_rolimons_without_any_cookie_there()
    {
        var fake = new Fake();
        using var api = new RobloxApi(fake);
        using var rol = new RolimonsApi(fake);
        var r = await new InventoryRefresher(api, rol).RefreshAsync(77, Tests.Fake.Session(), null);
        Assert.True(r.Inventory.Complete);
        Assert.True(r.Inventory.IsOnHold(r.Inventory.Copies.Single()));
        Assert.Contains("api.rolimons.com", fake.Hosts);
        Assert.DoesNotContain("api.rolimons.com+cookie", fake.Hosts);
    }

    [Fact]
    public async Task Copies_on_hold_that_Roblox_leaves_out_come_from_Rolimons()
    {
        // FonteAlfa, 22/11: Atomic Blue Hairdo and 2 Molten Lava Wings arrived yesterday, on hold; Roblox's list omitted them.
        var fake = new Fake { WithdrawalOnHold = true };
        using var api = new RobloxApi(fake);
        using var rol = new RolimonsApi(fake);
        var refresher = new InventoryRefresher(api, rol, null, asset => asset == 900 ? ("Molten Lava Wings", 1_835) : (null, null));
        var r = await refresher.RefreshAsync(77, Tests.Fake.Session(), null);
        var inv = r.Inventory;
        Assert.Equal([111L, 222, 223, 224], inv.Copies.Select(c => c.UserAssetId).Order());
        var wings = inv.Copies.Where(c => c.AssetId == 900).ToList();
        Assert.All(wings, c => Assert.Equal(("Molten Lava Wings", (long?)1_835, (bool?)true, true), (c.Name, c.Rap, c.OnHold, c.FromRolimons)));
        var other = inv.Copies.Single(c => c.UserAssetId == 224);
        Assert.Equal(((bool?)null, true), (other.OnHold, other.FromRolimons)); // not held per Rolimons: checked later on the Marketplace
        Assert.False(inv.Copies.Single(c => c.UserAssetId == 111).FromRolimons);
        Assert.Null(inv.RolimonsProblem);
    }

    [Fact]
    public async Task Another_accounts_cookie_reads_nothing()
    {
        var fake = new Fake { UserId = 999 };
        using var api = new RobloxApi(fake);
        var r = await new InventoryRefresher(api).RefreshAsync(77, Tests.Fake.Session(), null);
        Assert.False(r.Inventory.Complete);
        Assert.True(r.SessionRefused);
        Assert.DoesNotContain(fake.Hosts, h => h.StartsWith("inventory"));
    }
}
