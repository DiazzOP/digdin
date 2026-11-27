using System.Net;
using System.Text;
using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Roblox;
using Xunit;

namespace RbxSwitcher.Core.Tests;

/// <summary>Limiteds tab (fictitious accounts and items).</summary>
public class SellingTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static LimitedCopy C(long u, long a, string n, long rap, bool hold = false) => PlanFixture.Copy(u, a, n, rap, hold);

    [Fact]
    public void Resellers_classic_and_Marketplace()
    {
        var classic = SellParsers.Resellers(J("""{"data":[{"userAssetId":9,"seller":{"id":5,"type":"User","name":"x"},"price":3100,"serialNumber":4},{"seller":{"id":6},"price":3050}]}"""));
        Assert.Equal([(6L, 3050L), (5L, 3100L)], classic.Select(l => (l.SellerId, l.Price)));
        Assert.Equal(9L, classic[1].UserAssetId);
        var market = SellParsers.Resellers(J("""{"data":[{"collectibleItemInstanceId":"i-1","seller":{"sellerId":7,"sellerType":"User"},"price":180,"serialNumber":12}]}"""));
        Assert.Equal((7L, 180L, "i-1", 12), (market[0].SellerId, market[0].Price, market[0].InstanceId, market[0].Serial!.Value));
        Assert.Throws<FormatException>(() => SellParsers.Resellers(J("{}")));
    }

    [Fact]
    public void Own_copies_classic_and_Marketplace()
    {
        var classic = SellParsers.OwnCopies(J("""{"data":[{"userAssetId":101,"price":3200,"serialNumber":1},{"userAssetId":102,"price":null,"serialNumber":2}]}"""));
        Assert.Equal([(101L, 3200L), (102L, (long?)null)], classic.Select(c => (c.UserAssetId!.Value, c.Price)));
        var market = SellParsers.OwnCopies(J("""{"itemInstances":[{"collectibleInstanceId":"i-1","serialNumber":44,"saleState":"OnSale","price":199},{"serialNumber":45,"saleState":"OffSale","price":250}]}"""));
        Assert.Equal([(44, (long?)199), (45, null)], market.Select(c => (c.Serial!.Value, c.Price)));
    }

    [Fact]
    public void Listings_follow_the_latest_read_of_each_item()
    {
        var inv = new List<LimitedCopy> { C(101, 1, "Valk", 3000), C(102, 1, "Valk", 3000), C(201, 4, "Egg", 300) };
        var l = new AccountListings();
        l.Apply(1, [new OwnCopy(101, 1, 3200), new OwnCopy(102, 2, null)], inv, Now);
        l.Apply(4, [new OwnCopy(null, 1, 299)], inv, Now); // Marketplace: by serial (copy 201 has serial 1)
        Assert.Equal(new Dictionary<long, long> { [101] = 3200, [201] = 299 }, l.PriceByCopy);
        l.Apply(1, [], inv, Now.AddMinutes(5)); // taken off sale
        Assert.False(l.PriceByCopy.ContainsKey(101));
        Assert.Equal(Now.AddMinutes(5), l.ItemReadUtc[1]);
    }

    [Fact]
    public void Marketplace_instances_without_id_or_serial_fill_the_accounts_copies_in_order()
    {
        // As recorded on roblox.com (19/11): itemInstances[] with price, saleState, isHeld and serialNumber null.
        var page = SellParsers.OwnCopiesPage(J("""
            {"itemInstances":[{"collectibleInstanceId":"a","serialNumber":null,"isHeld":false,"saleState":"OnSale","price":1825},
                              {"collectibleInstanceId":"b","serialNumber":null,"isHeld":false,"saleState":"OffSale","price":0},
                              {"collectibleInstanceId":"c","serialNumber":null,"isHeld":true,"saleState":"OffSale","price":0}],
             "nextPageCursor":null,"previousPageCursor":null}
            """));
        Assert.Null(page.Next);
        var inv = new List<LimitedCopy> { C(102, 1, "Fedora", 1_713), C(101, 1, "Fedora", 1_713), C(103, 1, "Fedora", 1_713, hold: true) };
        var l = new AccountListings();
        l.Apply(1, page.Items, inv, Now);
        Assert.Equal(new Dictionary<long, long> { [101] = 1_825 }, l.PriceByCopy); // one on sale, on a released copy
        Assert.True(l.ItemReadUtc.ContainsKey(1));
    }

    [Fact]
    public void Sales_per_day_count_decimal_volumes_of_the_last_30_days()
    {
        string day(int ago) => Now.AddDays(-ago).ToString("yyyy-MM-ddT00:00:00Z");
        var (rap, perDay) = LimitedParsers.ResaleData(J($$"""
            {"recentAveragePrice":1825,"volumeDataPoints":[{"value":3.0,"date":"{{day(1)}}"},{"value":12,"date":"{{day(10)}}"},{"value":40,"date":"{{day(45)}}"}],"priceDataPoints":[]}
            """), Now);
        Assert.Equal(1_825, rap);
        Assert.Equal(0.5, perDay); // (3 + 12) / 30; the 45-day-old point is out
    }

    [Fact]
    public void A_full_list_of_listings_says_the_rest_is_not_on_sale()
    {
        var page = SellParsers.ResellersPage(J("""{"data":[{"seller":{"sellerId":7},"price":180}],"nextPageCursor":"","previousPageCursor":null}"""));
        Assert.True(page.Complete);
        Assert.False(SellParsers.ResellersPage(J("""{"data":[],"nextPageCursor":"abc"}""")).Complete);
    }

    [Fact]
    public void The_suggestion_undercuts_but_never_goes_under_the_floor()
    {
        var s = new DistributionSettings { SellUndercut = 1, SellFloorPercent = 80 };
        var m = new ItemMarket(1, "Valk", 3_000, 3_200, 3, 2, Now);
        Assert.Equal((3_049L, "1 abaixo do menor preço"), Suggest(3_050));
        var (floor, why) = SellBoard.Suggest(2_000, m, null, s);
        Assert.Equal(2_400, floor);
        Assert.Contains("abaixo do piso de 80%", why);
        Assert.Equal((3_200L, "ninguém vendendo: valor da Rolimons"), Suggest(null));
        (long?, string?) Suggest(long? low) => SellBoard.Suggest(low, m, null, s);
    }

    private static SellInput Input(long id, string name, AccountListings? listings, params LimitedCopy[] copies) =>
        new(PlanFixture.Acc(id, name, "Alts", copies), name, listings, null);

    [Fact]
    public void Each_copy_gets_its_situation_position_and_suggestion()
    {
        var s = PlanFixture.Settings();
        var sigma = Input(PlanFixture.Sigma, "sigma2", null, C(101, 1, "Valk", 3_000), C(102, 3, "Dominus", 5_400), C(103, 6, "Hat", 1_000, hold: true),
            C(104, 2, "Fedora", 12_500), C(105, 4, "Epic Egg", 300));
        var listings = new AccountListings();
        listings.Apply(1, [new OwnCopy(101, 1, 3_200)], sigma.Snapshot.Inventory!.Copies, Now);
        listings.Apply(3, [new OwnCopy(102, 2, 5_000)], sigma.Snapshot.Inventory.Copies, Now);
        listings.Apply(2, [], sigma.Snapshot.Inventory.Copies, Now);
        sigma = sigma with { Listings = listings };
        var market = new Dictionary<long, ItemListings>
        {
            [1] = new(1, [new(99, 3_050, null, null, null), new(98, 3_150, null, null, null), new(PlanFixture.Sigma, 3_200, 101, null, 1)], Now),
            [3] = new(3, [new(PlanFixture.Sigma, 5_000, 102, null, 2), new(97, 5_290, null, null, null)], Now),
        };
        var board = SellBoard.Build([sigma], s, PlanFixture.Market, market, new DistributionPlan(), Now);
        var rows = board.Rows.ToDictionary(r => r.UserAssetId);

        Assert.Equal((SellSituation.Outbid, 3_050L, 3), (rows[101].Situation, rows[101].LowestOther!.Value, rows[101].Position!.Value));
        Assert.Equal(3_049, rows[101].Suggestion);
        Assert.Equal(2_240, rows[101].Net);                       // 3.200 − 30 %
        Assert.Equal((SellSituation.Lowest, 1), (rows[102].Situation, rows[102].Position!.Value));
        Assert.Equal(SellSituation.OnHold, rows[103].Situation);
        Assert.Equal(SellSituation.Ready, rows[104].Situation);    // item read: not on sale
        Assert.True(rows[105].Small);
        Assert.Equal(SellSituation.Unread, rows[105].Situation);   // item never read
        Assert.Single(board.Accounts);
    }

    [Fact]
    public void Your_other_account_cheaper_is_named_and_planned_copies_are_marked()
    {
        var s = PlanFixture.Settings();
        var a = Input(PlanFixture.Sigma, "sigma2", null, C(101, 1, "Valk", 3_000));
        var b = Input(PlanFixture.Kuba, "altconta72", null, C(201, 1, "Valk", 3_000));
        var la = new AccountListings(); la.Apply(1, [new OwnCopy(101, 1, 3_200)], a.Snapshot.Inventory!.Copies, Now);
        var lb = new AccountListings(); lb.Apply(1, [new OwnCopy(201, 1, 3_100)], b.Snapshot.Inventory!.Copies, Now);
        var market = new Dictionary<long, ItemListings> { [1] = new(1, [new(PlanFixture.Kuba, 3_100, 201, null, 1), new(PlanFixture.Sigma, 3_200, 101, null, 1)], Now) };
        var plan = new DistributionPlan { Steps = [new PlanStep { Id = "E004", State = StepState.Suggested, Limiteds = [new(201, 1, "Valk", 1)] }] };
        var board = SellBoard.Build([a with { Listings = la }, b with { Listings = lb }], s, PlanFixture.Market, market, plan, Now);
        var outbid = board.Rows.Single(r => r.UserAssetId == 101);
        Assert.Equal((SellSituation.Outbid, "altconta72"), (outbid.Situation, outbid.LowestOtherAccount));
        Assert.Equal("no plano E004", board.Rows.Single(r => r.UserAssetId == 201).Note);
    }

    [Fact]
    public void A_Small_received_in_a_Distribuir_trade_shows_on_hold_before_the_reads_see_it()
    {
        // Kuba got FonteAlfa's Small 30 h ago; Roblox's list leaves held copies out and Rolimons has not caught up.
        var step = new PlanStep
        {
            Id = "E065", State = StepState.Completed, SourceId = PlanFixture.Alfa, DestinationId = PlanFixture.Kuba, CompletedUtc = Now.AddHours(-30),
            Limiteds = [new(101, 1, "Valk", 1, 2_100)], SourceSmall = new(104, 4, "Epic Egg", 4), DestinationSmall = new(401, 4, "Epic Egg", 1),
        };
        var plan = new DistributionPlan { Steps = [step] };
        var kuba = Input(PlanFixture.Kuba, "altconta72", null);
        var rows = SellBoard.Build([kuba], PlanFixture.Settings(), PlanFixture.Market, new Dictionary<long, ItemListings>(), plan, Now).Rows;
        var small = rows.Single(r => r.UserAssetId == 104);
        Assert.True(small.Small);
        Assert.Equal(SellSituation.OnHold, small.Situation);
        Assert.Equal(SellSituation.OnHold, rows.Single(r => r.UserAssetId == 101).Situation);
        // After the 2-day hold Roblox's own list shows the copies again; nothing is added any more.
        Assert.Empty(SellBoard.Build([kuba], PlanFixture.Settings(), PlanFixture.Market, new Dictionary<long, ItemListings>(), plan, Now.AddDays(3)).Rows);
    }

    [Fact]
    public void Each_destination_shows_how_much_it_can_still_receive()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = new SellInput(PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, C(1, 1, "Valk", 3_000)), "Alfa", null, null);
        var kuba = Input(PlanFixture.Kuba, "altconta72", null, C(201, 6, "Hat", 1_000));
        var expired = Input(PlanFixture.Lua, "altconta73", null, C(301, 6, "Hat", 1_000));
        expired = expired with { Snapshot = expired.Snapshot with { SessionOk = false } };
        // A trade already sent to Kuba counts against its space, as in the Distribuir tab.
        var plan = new DistributionPlan { Steps = [new PlanStep { Id = "E001", State = StepState.Sent, DestinationId = PlanFixture.Kuba, Limiteds = [new(9, 1, "Valk", 3, 700)] }] };
        var board = SellBoard.Build([alfa, kuba, expired], s, PlanFixture.Market, new Dictionary<long, ItemListings>(), plan, Now);

        var expected = Planner.Space(kuba.Snapshot, s, PlanFixture.Market, Now, 700, out _);
        Assert.NotNull(expected);
        var dest = board.Accounts.Single(x => x.UserId == PlanFixture.Kuba);
        Assert.Equal(expected, dest.Space);
        Assert.Null(dest.SpaceBlock);
        var source = board.Accounts.Single(x => x.UserId == PlanFixture.Alfa);
        Assert.True(source.Source);
        Assert.Null(source.Space);
        var lua = board.Accounts.Single(x => x.UserId == PlanFixture.Lua);
        Assert.Equal((null, "sessão expirada"), (lua.Space, lua.SpaceBlock));
    }

    [Fact]
    public void Accounts_without_Plus_cannot_sell_and_accounts_without_anything_are_left_out()
    {
        var s = PlanFixture.Settings();
        var noPlus = Input(PlanFixture.Lua, "Lua", null, C(301, 1, "Valk", 3_000));
        noPlus.Snapshot.Finance!.Plus = new PlusInfo { Active = false, Status = "None" };
        var empty = Input(PlanFixture.Kuba, "altconta72", null);
        var board = SellBoard.Build([noPlus, empty], s, PlanFixture.Market, new Dictionary<long, ItemListings>(), new DistributionPlan(), Now);
        Assert.Equal(SellSituation.NoPlus, board.Rows.Single().Situation);
        Assert.Equal(["Lua"], board.Accounts.Select(x => x.Username));
    }

    [Fact]
    public void Sale_dates_are_shown_in_Brasilia_time()
    {
        // The screenshot of 19/11 22:39 (Brasília): a sale at 00:23 UTC of the 20th happened at 21:23 of the 19th.
        var at = new DateTimeOffset(2026, 11, 20, 0, 23, 0, TimeSpan.Zero);
        var sales = new AccountSales { Records = [new("h1", at, 1, "Manly Bee Hat", 1_540, true)] };
        var input = Input(PlanFixture.Sigma, "sigma1", null) with { Sales = sales };
        var row = Assert.Single(SellBoard.Build([input], PlanFixture.Settings(), PlanFixture.Market, new Dictionary<long, ItemListings>(), new DistributionPlan(), at.AddHours(1)).Rows);
        Assert.Equal("pendente · vendida em 19/11 21:23", row.Note);
    }

    [Fact]
    public void The_total_adds_every_copy_and_the_pending_but_not_the_sales()
    {
        var s = PlanFixture.Settings();
        var sigma = Input(PlanFixture.Sigma, "sigma2", null, C(101, 1, "Valk", 3_000), C(103, 6, "Hat", 1_000, hold: true));
        sigma.Snapshot.Finance!.PendingRobux = 1_540;
        var l = new AccountListings();
        l.Apply(1, [new OwnCopy(101, 1, 3_200)], sigma.Snapshot.Inventory!.Copies, Now);
        var sales = new AccountSales { Records = [new("h1", Now.AddHours(-2), 9, "Old", 1_540, true)] };
        var board = SellBoard.Build([sigma with { Listings = l, Sales = sales }], s, PlanFixture.Market, new Dictionary<long, ItemListings>(), new DistributionPlan(), Now);
        var t = SellBoard.Totals(board.Rows, board.Accounts);
        Assert.Equal(2_240, t.OnSale);  // 3.200 − 30 %
        Assert.Equal(1_540, t.Pending); // the sale is counted once, as pending
        Assert.True(t.OnHold > 0);
    }

    [Fact]
    public void A_copy_with_RAP_0_is_not_a_Small_and_shows_up()
    {
        // The FonteAlfa/Beta case: items just moved to the Marketplace report RAP 0 and were hidden as Smalls.
        var s = PlanFixture.Settings();
        var alfa = Input(PlanFixture.Alfa, "FonteAlfa", null, C(801, 77, "New Item", 0), C(802, 78, "Unknown Rap", 0));
        var market = new Dictionary<long, ItemListings> { [77] = new(77, [new(9, 5_000, null, null, null)], Now, true) };
        var rows = SellBoard.Build([alfa], s, new Dictionary<long, ItemMarket> { [77] = new(77, "New Item", 0, null, null, null, Now) }, market, new DistributionPlan(), Now).Rows;
        Assert.All(rows, r => Assert.False(r.Small));
        Assert.Equal(4_999, rows.Single(r => r.UserAssetId == 801).Suggestion); // priced from the sellers
    }

    [Fact]
    public void One_price_signal_above_the_limit_means_not_a_Small()
    {
        // FonteAlfa's 3 Limiteds: Roblox's resale data gave a low RAP; the inventory RAP and the Rolimons value were high.
        var s = PlanFixture.Settings(); // Small limit 500
        var market = new Dictionary<long, ItemMarket>
        {
            [70] = new(70, "Off RAP", 300, null, null, null, Now),    // market RAP low, copy RAP high
            [71] = new(71, "Valued", 200, 2_000, null, null, Now),   // RAP low, Rolimons value high
            [72] = new(72, "Egg", 300, 350, null, null, Now),        // a real Small
        };
        Assert.False(Valuation.IsSmall(C(1, 70, "Off RAP", 3_000), s, market));
        Assert.False(Valuation.IsSmall(C(2, 71, "Valued", 200), s, market));
        Assert.True(Valuation.IsSmall(C(3, 72, "Egg", 300), s, market));
        Assert.False(Valuation.IsSmall(C(4, 73, "Unknown", 0), s, market));
    }

    [Fact]
    public void Copies_only_Rolimons_lists_are_checked_on_the_Marketplace()
    {
        var s = PlanFixture.Settings();
        var copies = new[]
        {
            new LimitedCopy(901, 80, "Atomic Blue Hairdo", null, 3_436, null, FromRolimons: true),
            new LimitedCopy(902, 81, "Molten Lava Wings", null, 1_835, null, FromRolimons: true),
            new LimitedCopy(903, 82, "Already Sold", null, 2_000, null, FromRolimons: true),
        };
        var alfa = Input(PlanFixture.Alfa, "FonteAlfa", null, copies);
        var l = new AccountListings();
        l.Apply(80, [new OwnCopy(null, null, null, Held: true)], copies, Now);  // Roblox: owned, on hold
        l.Apply(81, [new OwnCopy(null, null, null, Held: false)], copies, Now); // owned, released
        l.Apply(82, [], copies, Now);                                            // not owned any more
        var rows = SellBoard.Build([alfa with { Listings = l }], s, new Dictionary<long, ItemMarket>(), new Dictionary<long, ItemListings>(), new DistributionPlan(), Now)
            .Rows.ToDictionary(r => r.UserAssetId);
        Assert.Equal(SellSituation.OnHold, rows[901].Situation);
        Assert.Equal(SellSituation.Ready, rows[902].Situation);
        Assert.False(rows.ContainsKey(903));
    }

    [Fact]
    public void A_Rolimons_failure_shows_on_the_account_instead_of_hiding_it()
    {
        var alfa = Input(PlanFixture.Alfa, "FonteAlfa", null);
        alfa.Snapshot.Inventory!.RolimonsProblem = "Rolimons limitou os pedidos (HTTP 429)";
        var board = SellBoard.Build([alfa], PlanFixture.Settings(), PlanFixture.Market, new Dictionary<long, ItemListings>(), new DistributionPlan(), Now);
        Assert.Contains("HTTP 429", Assert.Single(board.Accounts).Problem);
    }

    [Fact]
    public void Recent_sales_show_as_sold_rows()
    {
        var s = PlanFixture.Settings();
        var sales = new AccountSales { Records = [new("h1", Now.AddHours(-3), 1, "Valk", 2_240, false), new("h0", Now.AddDays(-9), 1, "Valk", 2_000, false)] };
        var input = Input(PlanFixture.Sigma, "sigma2", null) with { Sales = sales };
        var row = Assert.Single(SellBoard.Build([input], s, PlanFixture.Market, new Dictionary<long, ItemListings>(), new DistributionPlan(), Now).Rows);
        Assert.Equal((SellSituation.Sold, 2_240L), (row.Situation, row.Net!.Value));
    }

    [Fact]
    public void Without_its_own_read_the_cheapest_listings_say_what_is_on_sale()
    {
        // The real case of 19/11: the own-copies read answered 404; the listings showed "3º · você 1.825".
        var s = PlanFixture.Settings();
        var sigma = Input(PlanFixture.Sigma, "sigma0", null, C(101, 1, "Snow Leopard Fedora", 1_713), C(102, 3, "Shaggy", 1_331), C(103, 6, "Hat", 1_000));
        var market = new Dictionary<long, ItemListings>
        {
            // no copy id or serial in the listing: it goes to the account's copy of that item
            [1] = new(1, [new(91, 1_823, null, null, null), new(92, 1_824, null, null, null), new(PlanFixture.Sigma, 1_825, null, null, null)], Now),
            // fewer than 10 listings and none is the account's: its copy is not on sale
            [3] = new(3, [new(93, 1_394, null, null, null)], Now),
            // 10 listings, none the account's: it may be on sale above them — not known
            [6] = new(6, [.. Enumerable.Range(0, 10).Select(i => new ResaleListing(100 + i, 990 + i, null, null, null))], Now),
        };
        var rows = SellBoard.Build([sigma], s, PlanFixture.Market, market, new DistributionPlan(), Now).Rows.ToDictionary(r => r.UserAssetId);
        Assert.Equal((SellSituation.Outbid, 1_825L, 3), (rows[101].Situation, rows[101].MyPrice!.Value, rows[101].Position!.Value));
        Assert.Equal(SellSituation.Ready, rows[102].Situation);
        Assert.Equal(SellSituation.Unread, rows[103].Situation);
    }

    [Fact]
    public void An_older_own_read_still_counts_when_the_listings_say_nothing_about_the_copy()
    {
        var s = PlanFixture.Settings();
        var sigma = Input(PlanFixture.Sigma, "sigma2", null, C(101, 6, "Hat", 1_000), C(102, 1, "Valk", 3_000));
        var own = new AccountListings();
        own.Apply(6, [], sigma.Snapshot.Inventory!.Copies, Now.AddHours(-3));                      // not on sale, 3 h ago
        own.Apply(1, [new OwnCopy(102, 2, 3_400)], sigma.Snapshot.Inventory.Copies, Now.AddHours(-3)); // on sale for 3.400, 3 h ago
        sigma = sigma with { Listings = own };
        var market = new Dictionary<long, ItemListings>
        {
            // 10 cheapest listings now, none the account's: they say nothing about its copy → the older read holds
            [6] = new(6, [.. Enumerable.Range(0, 10).Select(i => new ResaleListing(100 + i, 990 + i, null, null, null))], Now),
            [1] = new(1, [.. Enumerable.Range(0, 10).Select(i => new ResaleListing(200 + i, 3_000 + i, null, null, null))], Now),
        };
        var rows = SellBoard.Build([sigma], s, PlanFixture.Market, market, new DistributionPlan(), Now).Rows.ToDictionary(r => r.UserAssetId);
        Assert.Equal(SellSituation.Ready, rows[101].Situation);                   // not "não lido"
        Assert.Equal((SellSituation.Outbid, 3_400L), (rows[102].Situation, rows[102].MyPrice!.Value));

        // All of the item's listings now, without the account: it sold or left the sale — the listings win.
        market[1] = new(1, [new(200, 3_000, null, null, null)], Now, Complete: true);
        rows = SellBoard.Build([sigma], s, PlanFixture.Market, market, new DistributionPlan(), Now).Rows.ToDictionary(r => r.UserAssetId);
        Assert.Equal(SellSituation.Ready, rows[102].Situation);
        Assert.Null(rows[102].MyPrice);
    }

    [Fact]
    public void Two_copies_listed_by_id_and_serial_go_to_the_right_copies()
    {
        var copies = new List<ValuedCopy>
        {
            new(C(101, 1, "Valk", 3_000), 3_000, PriceSource.Rap, 2_100, false, false),
            new(C(102, 1, "Valk", 3_000), 3_000, PriceSource.Rap, 2_100, false, false),
            new(C(103, 1, "Valk", 3_000, hold: true), 3_000, PriceSource.Rap, 2_100, false, true),
        };
        var market = new Dictionary<long, ItemListings>
        {
            [1] = new(1, [new(PlanFixture.Sigma, 3_100, null, null, 2), new(PlanFixture.Sigma, 3_200, 101, null, null), new(5, 3_000, null, null, null)], Now),
        };
        var listed = SellBoard.ListedAmongLowest(PlanFixture.Sigma, copies, market);
        Assert.Equal(new Dictionary<long, long> { [102] = 3_100, [101] = 3_200 }, listed); // serial 2 = copy 102; the held copy is never on sale
    }

    // ── the reader: classic vs Marketplace ──

    private sealed class Market : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        public Dictionary<long, string?> Ids = [];
        public HashSet<long> Unknown = []; // the catalog leaves these out of its answer
        public bool MarketFails, ClassicOwnGone;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var u = req.RequestUri!;
            Paths.Add(u.Host + u.AbsolutePath);
            if (req.Method == HttpMethod.Post && !req.Headers.Contains("x-csrf-token"))
            {
                var r403 = new HttpResponseMessage(HttpStatusCode.Forbidden);
                r403.Headers.Add("x-csrf-token", "T");
                return r403;
            }
            if (u.AbsolutePath == "/v1/catalog/items/details")
            {
                using var d = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                var ids = d.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).Where(id => !Unknown.Contains(id));
                return Json(new { data = ids.Select(id => new { id, collectibleItemId = Ids.GetValueOrDefault(id) }).ToArray() });
            }
            if (u.Host == "apis.roblox.com")
                return MarketFails ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : u.AbsolutePath.EndsWith("/resellers") ? Json(new { data = new[] { new { seller = new { sellerId = 7 }, price = 180 } } })
                    : Json(new { itemInstances = new[] { new { serialNumber = 44, saleState = "OnSale", price = 199 } } });
            if (u.AbsolutePath.EndsWith("/resellers")) return Json(new { data = new[] { new { seller = new { id = 5 }, price = 3050 } } });
            if (u.AbsolutePath.EndsWith("/resellable-copies"))
                return ClassicOwnGone ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(new { data = new[] { new { userAssetId = 101, price = 3200 } } });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Classic_items_read_on_economy_and_Marketplace_items_on_apis()
    {
        var fake = new Market { Ids = { [2] = "cid-2" } };
        using var api = new RobloxApi(fake);
        var reader = new SellReader(api);
        var s = new SessionContext(Fake.Session());

        var classic = await reader.ListingsAsync(s, 1);
        var market = await reader.ListingsAsync(s, 2);
        var mine = await reader.OwnCopiesAsync(s, 12, 2);
        Assert.Equal(3_050, classic.Value!.Items.Single().Price);
        Assert.Equal(180, market.Value!.Items.Single().Price);
        Assert.Equal(44, mine.Value!.Single().Serial);
        Assert.Contains("economy.roblox.com/v1/assets/1/resellers", fake.Paths);
        Assert.Contains("apis.roblox.com/marketplace-sales/v1/item/cid-2/resellers", fake.Paths);
        // One lookup per item (the first after the CSRF handshake); the second read of item 2 reuses its id.
        Assert.Equal(3, fake.Paths.Count(p => p.EndsWith("/v1/catalog/items/details")));
    }

    [Fact]
    public async Task A_404_own_copies_read_stops_only_that_item()
    {
        // 24/11: one classic item answering 404 used to stop the own-copies read of every item of every account for the
        // rest of the session — most rows ended "Livre · não lido". Now only that item is skipped.
        var fake = new Market { ClassicOwnGone = true, Ids = { [2] = "cid-2" } };
        using var api = new RobloxApi(fake);
        var reader = new SellReader(api);
        var s = new SessionContext(Fake.Session());
        Assert.False((await reader.OwnCopiesAsync(s, 12, 1)).Ok);
        Assert.True(reader.OwnCopiesUnreadable(1));
        int calls = fake.Paths.Count;
        Assert.False((await reader.OwnCopiesAsync(s, 12, 1)).Ok);
        Assert.Equal(calls, fake.Paths.Count);                       // that item is not asked again
        Assert.True((await reader.OwnCopiesAsync(s, 12, 2)).Ok);     // the other items still are
        Assert.True((await reader.OwnCopiesAsync(s, 13, 2)).Ok);     // and the other accounts
        Assert.False(reader.OwnCopiesUnreadable(2));
    }

    [Fact]
    public async Task Failures_never_add_up_to_stop_the_reads()
    {
        var fake = new Market { Ids = { [2] = "cid-2" }, MarketFails = true };
        using var api = new RobloxApi(fake);
        var reader = new SellReader(api);
        var s = new SessionContext(Fake.Session());
        for (int i = 0; i < 5; i++) Assert.False((await reader.OwnCopiesAsync(s, 12, 2)).Ok);
        fake.MarketFails = false;
        Assert.True((await reader.OwnCopiesAsync(s, 12, 2)).Ok);
        // A Marketplace item is never read on the old endpoints (404, or numbers frozen at the migration).
        Assert.DoesNotContain(fake.Paths, p => p.StartsWith("economy.roblox.com"));
    }

    [Fact]
    public async Task Item_ids_are_looked_up_in_batches_and_an_unknown_one_is_not_read_on_the_old_endpoints()
    {
        var fake = new Market { Ids = { [2] = "cid-2", [3] = "cid-3", [4] = "cid-4" }, Unknown = { 5 } };
        using var api = new RobloxApi(fake);
        var reader = new SellReader(api);
        var s = new SessionContext(Fake.Session());
        var r = await reader.EnsureIdsAsync(s, [2, 3, 4, 5]);
        Assert.Equal(1, r.Value);                                    // item 5 still unknown
        Assert.Equal(2, fake.Paths.Count(p => p.EndsWith("/v1/catalog/items/details"))); // the CSRF handshake + one request
        Assert.True(reader.IdKnown(3));
        Assert.False(reader.IdKnown(5));

        var unknown = await reader.ListingsAsync(s, 5);
        Assert.False(unknown.Ok);
        Assert.Contains("id do item no Marketplace", unknown.Problem);
        Assert.DoesNotContain(fake.Paths, p => p.StartsWith("economy.roblox.com"));

        var saved = new SellReader(api);
        saved.Seed(reader.CollectibleIds);                           // ids kept by an earlier run: no lookup at all
        int before = fake.Paths.Count;
        Assert.True((await saved.ListingsAsync(s, 3)).Ok);
        Assert.Equal(before + 1, fake.Paths.Count);
    }
}
