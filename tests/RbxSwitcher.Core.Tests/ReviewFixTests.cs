using System.Text.Json;
using RbxSwitcher.Core.Limiteds;
using Xunit;

namespace RbxSwitcher.Core.Tests;

/// <summary>Cases from the 0.6.1 review (fictitious data).</summary>
public class ReviewFixTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;
    private static LimitedCopy C(long u, long a, string n, long rap, bool hold = false) => PlanFixture.Copy(u, a, n, rap, hold);
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static PlanStep Step(StepState state = StepState.Opened) => new()
    {
        Id = "E001", SourceId = PlanFixture.Alfa, SourceName = "FonteAlfa", DestinationId = PlanFixture.Sigma, DestinationName = "sigma2",
        Limiteds = [new(101, 1, "Valk", 7, 2_100)], SourceSmall = new(104, 4, "Epic Egg", 4), DestinationSmall = new(401, 4, "Epic Egg", 1),
        CreatedUtc = Now.AddMinutes(-30), State = state,
    };

    private static string V2(string status, string srcItems, string dstItems) =>
        "{\"id\":556,\"status\":\"" + status + "\",\"participantAOffer\":{\"user\":{\"id\":1},\"items\":[" + srcItems + "],\"robux\":0}," +
        "\"participantBOffer\":{\"user\":{\"id\":12},\"items\":[" + dstItems + "],\"robux\":0}}";
    private static string I(long asset, string name, int serial) =>
        $$"""{"itemTarget":{"targetId":{{asset}}},"itemName":"{{name}}","serialNumber":{{serial}},"collectibleItemInstanceId":"x-{{asset}}-{{serial}}"}""";

    [Fact]
    public void An_opened_step_whose_copies_arrived_reads_as_a_finished_trade()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000));
        var kuba = PlanFixture.Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300));
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());
        plan.Steps[0].State = StepState.Opened;

        alfa.Inventory!.Copies.Clear();
        alfa.Inventory.ReadUtc = Now.AddMinutes(10);
        kuba.Inventory!.Copies.Add(C(101, 1, "Valk", 3_000, hold: true));
        Planner.Revalidate(plan, new PlanInput([alfa, kuba], s, PlanFixture.Market, Now.AddMinutes(10)));
        Assert.Equal(StepState.NeedsCheck, plan.Steps[0].State);
        Assert.Contains("a trade parece concluída", plan.Steps[0].Attention);
    }

    [Fact]
    public void The_destination_line_shows_the_sum_with_pending_and_parked_Limiteds()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000));
        var kuba = PlanFixture.Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300), C(202, 6, "Hat", 1_000));
        kuba.Finance!.PendingRobux = 400;
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());
        var why = plan.Steps[0].Why;
        Assert.Contains(why, w => w.StartsWith("Espaço de @altconta72 na venda: teto") && w.Contains("(agora:"));
        var occupied = Assert.Single(why, w => w.StartsWith("Ocupado em @altconta72"));
        Assert.Contains("pendentes 400", occupied);
        Assert.Contains("Limiteds parados 700 (1 cópia", occupied); // the Hat: 1.000 RAP − 30 %
        Assert.Contains(why, w => w.StartsWith("Teto de @altconta72") && w.Contains("meta 9.000"));
    }

    [Fact]
    public void The_Small_reserve_counts_only_released_Smalls()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        // One released Small and one on hold: the released one is the reserve and stays.
        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000), C(104, 4, "Epic Egg", 300), C(105, 5, "Wolf", 450, hold: true));
        var kuba = PlanFixture.Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300));
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());
        Assert.Null(Assert.Single(plan.Steps).SourceSmall);
    }

    [Fact]
    public void A_wrong_trade_id_keeps_the_trade_already_linked()
    {
        var step = Step(StepState.Sent);
        step.TradeId = 123456;
        var t = TradeParsers.Detail(J(V2("Open", I(1, "Valk", 7), "").Replace("\"id\":12", "\"id\":99")));
        var v = TradeVerifier.Verify(step, t, PlanFixture.Settings(), PlanFixture.Market);
        Assert.Equal(VerifyOutcome.WrongAccounts, v.Outcome);
        PlanUpdates.ApplyTrade(step, t, v, [], Now);
        Assert.Equal((StepState.Sent, 123456L), (step.State, step.TradeId!.Value));
    }

    [Fact]
    public void Another_serial_of_the_same_item_is_not_the_planned_copy()
    {
        var t = TradeParsers.Detail(J(V2("Completed", I(1, "Valk", 9) + "," + I(4, "Epic Egg", 4), I(4, "Epic Egg", 1))));
        var v = TradeVerifier.Verify(Step(), t, PlanFixture.Settings(), PlanFixture.Market);
        Assert.Equal(VerifyOutcome.NeedsCheck, v.Outcome);
        Assert.Contains(v.Notes, n => n.Contains("Valk #7 não está na trade"));
    }

    [Fact]
    public void A_v2_trade_exactly_as_planned_matches_without_copy_ids()
    {
        var t = TradeParsers.Detail(J(V2("Completed", I(1, "Valk", 7) + "," + I(4, "Epic Egg", 4), I(4, "Epic Egg", 1))));
        var v = TradeVerifier.Verify(Step(), t, PlanFixture.Settings(), PlanFixture.Market);
        Assert.Equal(VerifyOutcome.Matches, v.Outcome);
        Assert.Equal([101L, 104], v.FromSource.Select(c => c.UserAssetId));
        Assert.Equal([401L], v.FromDestination.Select(c => c.UserAssetId));
        Assert.Empty(v.Notes);
    }

    [Fact]
    public void Another_copy_of_the_same_Limited_with_its_id_is_the_same_lot()
    {
        // The trade page shows two identical copies without serial: the other one went. Roblox gives its id (v1).
        static string A(long uaid, long asset, string name, int serial, long rap) =>
            $$"""{"id":{{uaid}},"assetId":{{asset}},"name":"{{name}}","serialNumber":{{serial}},"recentAveragePrice":{{rap}}}""";
        string json = $$"""
            {"id":555,"status":"Completed","offers":[
              {"user":{"id":1},"userAssets":[{{A(109, 1, "Valk", 31, 3000)}},{{A(104, 4, "Epic Egg", 4, 300)}}],"robux":0},
              {"user":{"id":12},"userAssets":[{{A(401, 4, "Epic Egg", 1, 300)}}],"robux":0}]}
            """;
        var step = Step(StepState.Opened);
        var t = TradeParsers.Detail(J(json));
        var v = TradeVerifier.Verify(step, t, PlanFixture.Settings(), PlanFixture.Market);
        Assert.Equal(VerifyOutcome.Matches, v.Outcome);
        Assert.Contains(v.Notes, n => n.Contains("outra cópia de Valk") && n.Contains("Valk #31"));

        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, C(109, 1, "Valk", 3_000), C(104, 4, "Epic Egg", 300));
        var sigma = PlanFixture.Acc(PlanFixture.Sigma, "sigma2", "Alts", C(401, 4, "Epic Egg", 300));
        PlanUpdates.ApplyTrade(step, t, v, [alfa, sigma], Now);
        Assert.Equal(StepState.Completed, step.State);
        Assert.Equal((109L, 31), (step.Limiteds[0].UserAssetId, step.Limiteds[0].Serial!.Value)); // the plan follows the real copy
        Assert.Equal(2_100, step.Limiteds[0].Net);                                                 // same lot, same net
        Assert.Contains(sigma.Inventory!.Copies, c => c.UserAssetId == 109);
    }

    [Theory]
    [InlineData("countered")]
    [InlineData("INACTIVE")]
    [InlineData("Declined")]
    public void Closed_statuses_in_any_case(string status) =>
        Assert.True(new TradeDetail(1, status, null, []).IsClosedWithoutTrade);

    [Fact]
    public void A_sales_page_without_item_sales_still_reports_its_dates()
    {
        var (items, next, oldest) = SalesParsers.PageWithOldest(J($$$"""
            {"nextPageCursor":"c2","data":[
              {"idHash":"h1","created":"{{{Now:O}}}","details":{"id":9,"name":"Pass","type":"GamePass"},"currency":{"amount":10}},
              {"idHash":"h2","created":"{{{Now.AddDays(-1):O}}}","details":{"id":9,"name":"Pass","type":"GamePass"},"currency":{"amount":10}}]}
            """));
        Assert.Empty(items);
        Assert.Equal("c2", next);
        Assert.Equal(Now.AddDays(-1), oldest);
    }
}

public class TradeAssistTests
{
    [Fact]
    public void The_page_gets_the_steps_items_by_side()
    {
        var step = new PlanStep
        {
            Id = "E002", SourceName = "fonte_beta", DestinationName = "altconta18",
            Limiteds = [new(1, 10, "Black Headphones", 12, 2_240)], SourceSmall = new(2, 11, "Eggcient Woolly Mammoth", 44),
            DestinationSmall = new(3, 12, "Deteggctive W. Wolf", null),
        };
        var plan = TradeAssistPlan.For(step, "Beta", "sigma2");
        Assert.Equal(["Black Headphones #12", "Eggcient Woolly Mammoth #44"], plan.Give.Select(i => i.Label));
        Assert.Equal(["Deteggctive W. Wolf"], plan.Ask.Select(i => i.Label));
        string json = plan.ToJson();
        Assert.Contains("\"partner\":[\"altconta18\",\"sigma2\"]", json); // the page titles the inventory with the display name
        Assert.Contains("\"serial\":null", json);
        Assert.DoesNotContain("2240", json); // no values go to the page
    }

    [Fact]
    public void The_scripts_answer_becomes_a_status_line()
    {
        var plan = new TradeAssistPlan([new("A", 1), new("B", 2)], [new("C", null)], ["x"], ["y"]);
        var r = TradeAssistResult.Parse("""{"state":"done","selected":["A #1","C"],"missing":["B #2"],"ambiguous":[],"unconfirmed":[],"notes":[],"structure":"body"}""")!;
        Assert.False(r.Complete(plan));
        Assert.Equal("2 de 3 itens pré-selecionados na trade; não encontrei: B #2.", r.Summary(plan));
        Assert.Equal("body", r.Structure);
        Assert.Null(TradeAssistResult.Parse("""{"state":"running"}"""));
    }
}

public class TradeSignalTests
{
    private const string Page = "https://www.roblox.com/pt/users/11135965170/trade";

    [Fact]
    public void A_sent_trade_gives_its_id_and_partner()
    {
        var s = TradeSignals.From("POST", "https://trades.roblox.com/v1/trades/send", 200, """{"id":987654321}""", Page)!;
        Assert.Equal((TradeSignalKind.Sent, 987654321L, 11135965170L), (s.Kind, s.TradeId!.Value, s.PartnerId!.Value));
        Assert.Equal(55L, TradeSignals.From("POST", "https://trades.roblox.com/v2/trades/send", 200, """{"tradeId":55}""", Page)!.TradeId);
        Assert.Null(TradeSignals.From("POST", "https://trades.roblox.com/v1/trades/send", 200, "não é json", Page)!.TradeId); // still a send: located later
    }

    [Fact]
    public void Refusals_challenges_and_other_calls_are_not_signals()
    {
        Assert.Null(TradeSignals.From("POST", "https://trades.roblox.com/v1/trades/send", 403, "{}", Page)); // 2-step challenge first
        Assert.Null(TradeSignals.From("GET", "https://trades.roblox.com/v1/trades/send", 200, "{}", Page));
        Assert.Null(TradeSignals.From("POST", "https://trades.roblox.com/v1/trades/5/decline", 200, "{}", Page));
        Assert.Null(TradeSignals.From("POST", "https://evil.example/v1/trades/send", 200, """{"id":1}""", Page));
    }

    [Fact]
    public void An_accept_gives_the_trade_id()
    {
        var s = TradeSignals.From("POST", "https://trades.roblox.com/v1/trades/987654321/accept", 200, "{}", "https://www.roblox.com/trades")!;
        Assert.Equal((TradeSignalKind.Accepted, 987654321L), (s.Kind, s.TradeId!.Value));
    }
}

public class StepHistoryTests
{
    [Fact]
    public void The_same_event_twice_in_a_row_is_kept_once()
    {
        var step = new PlanStep();
        var t = PlanFixture.Now;
        step.Log(t, "trade 5 enviada, aguardando no Roblox (aberta)");
        step.Log(t.AddSeconds(3), "trade 5 enviada, aguardando no Roblox (aberta)");
        step.Log(t.AddSeconds(9), "trade 5 concluída e conferida");
        Assert.Equal(2, step.History.Count);
    }

    [Fact]
    public void Another_copy_of_the_planned_Small_is_not_a_swap()
    {
        // The screenshot case: "o plano previa Eggcient Woolly Mammoth, a trade levou Eggcient Woolly Mammoth".
        static string A(long uaid, long asset, string name, int serial, long rap) =>
            $$"""{"id":{{uaid}},"assetId":{{asset}},"name":"{{name}}","serialNumber":{{serial}},"recentAveragePrice":{{rap}}}""";
        string json = $$"""
            {"id":556,"status":"Completed","offers":[
              {"user":{"id":1},"userAssets":[{{A(101, 1, "Valk", 1, 3000)}},{{A(105, 4, "Epic Egg", 5, 300)}}],"robux":0},
              {"user":{"id":12},"userAssets":[{{A(402, 4, "Epic Egg", 2, 300)}}],"robux":0}]}
            """;
        var step = new PlanStep
        {
            Id = "E001", SourceId = PlanFixture.Alfa, SourceName = "FonteAlfa", DestinationId = PlanFixture.Sigma, DestinationName = "sigma2",
            Limiteds = [new(101, 1, "Valk", 1, 2_100)], SourceSmall = new(104, 4, "Epic Egg", 4), DestinationSmall = new(401, 4, "Epic Egg", 1),
        };
        var v = TradeVerifier.Verify(step, TradeParsers.Detail(JsonDocument.Parse(json).RootElement), PlanFixture.Settings(), PlanFixture.Market);
        Assert.Equal(VerifyOutcome.Matches, v.Outcome);
        Assert.Empty(v.Notes);
        Assert.Contains(v.FromSource, c => c.UserAssetId == 105);      // the copy that really went is recorded
        Assert.Contains(v.FromDestination, c => c.UserAssetId == 402);
    }
}
