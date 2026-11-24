using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fictitious accounts: FonteAlfa (source) and destinations with a net ceiling that sets their space exactly.</summary>
internal static class PlanFixture
{
    public static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    public const long Alfa = 1, Beta = 2, Kuba = 10, Lua = 11, Sigma = 12;

    public static LimitedCopy Copy(long uaid, long asset, string name, long rap, bool hold = false) => new(uaid, asset, name, (int)(uaid % 100), rap, hold);

    public static AccountSnapshot Acc(long id, string name, string? group, params LimitedCopy[] copies) =>
        new(id, name, group, true, QuotaCalculatorTests.Fresh(0, 5_000, 10_000),
            new AccountInventory { Copies = [.. copies], Complete = true, ReadUtc = Now });

    public static DistributionSettings Settings(params (long Id, long Target)[] targets)
    {
        var s = new DistributionSettings { SourceAccounts = [Alfa, Beta], DestinationGroup = "Alts", SmallRapLimit = 500, MinSmallsReserve = 1 };
        foreach (var (id, t) in targets) s.TargetNetByAccount[id] = t;
        return s;
    }

    // Items: 1 Valk (RAP 3.000, 4/day), 2 Fedora (12.500, held), 3 Dominus (5.400, 0.3/day), 4 Epic Egg (300), 5 Wolf (450), 6 Hat (1.000, 2/day)
    public static Dictionary<long, ItemMarket> Market => new()
    {
        [1] = new(1, "Valk", 3_000, null, null, 4.2, Now), [2] = new(2, "Fedora", 12_500, null, null, 0.6, Now),
        [3] = new(3, "Dominus", 5_400, null, null, 0.3, Now), [4] = new(4, "Epic Egg", 300, null, null, null, Now),
        [5] = new(5, "Wolf", 450, null, null, null, Now), [6] = new(6, "Hat", 1_000, null, null, 2, Now),
    };
}

public class PlannerTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;
    private static AccountSnapshot Acc(long id, string n, string? g, params LimitedCopy[] c) => PlanFixture.Acc(id, n, g, c);
    private static LimitedCopy C(long u, long a, string n, long rap, bool hold = false) => PlanFixture.Copy(u, a, n, rap, hold);

    [Fact]
    public void Picks_by_liquidity_then_net_sends_to_the_most_space_and_explains_what_is_left_out()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 4_500), (PlanFixture.Lua, 2_000));
        var accounts = new[]
        {
            Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000), C(102, 3, "Dominus", 5_400), C(103, 2, "Fedora", 12_500, hold: true),
                C(104, 4, "Epic Egg", 300), C(105, 5, "Wolf", 450)),
            Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Epic Egg", 300)),
            Acc(PlanFixture.Lua, "Lua", "Alts", C(301, 5, "Wolf", 450)),
        };
        var plan = Planner.Build(new PlanInput(accounts, s, PlanFixture.Market, Now), new DistributionPlan());

        var step = Assert.Single(plan.Steps);
        Assert.Equal((PlanFixture.Alfa, PlanFixture.Kuba), (step.SourceId, step.DestinationId)); // Valk (4.2/day) first, to the most space
        Assert.Equal([101L], step.Limiteds.Select(c => c.UserAssetId));
        Assert.Equal(2_100, step.Net);                    // 3.000 − 30 %
        Assert.Equal(104, step.SourceSmall!.UserAssetId); // the cheaper Small; 1 more stays as AstraFlip reserve
        Assert.Equal(201, step.DestinationSmall!.UserAssetId);
        Assert.Contains(step.Why, w => w.StartsWith("Destino @altconta72"));
        Assert.Contains(step.Why, w => w.Contains("4.500 → 2.400"));

        // Dominus (net 3.780) no longer fits anywhere (kuba 2.400 left, Lua 2.000); the held Fedora is planned after the
        // released copies, and does not fit either.
        Assert.Contains(plan.Exclusions, e => e.Copy.UserAssetId == 102 && e.Why.Contains("sem espaço para 3.780") && e.Why.Contains("2.400 em @altconta72"));
        Assert.Contains(plan.Exclusions, e => e.Copy.UserAssetId == 103 && e.Why.Contains("sem espaço"));
        Assert.Equal("E001", step.Id);
    }

    [Fact]
    public void A_discarded_suggestion_does_not_come_back_with_new_readings_until_brought_back()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000), C(102, 3, "Dominus", 5_400));
        var kuba = Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Epic Egg", 300), C(202, 5, "Wolf", 450));
        var input = new PlanInput([alfa, kuba], s, PlanFixture.Market, Now);
        var plan = Planner.Build(input, new DistributionPlan());
        var step = plan.Steps.Single(x => x.Limiteds.Any(c => c.UserAssetId == 101));
        step.State = StepState.Discarded;
        plan.Dismiss(step, Now);

        var again = Planner.Build(input with { Now = Now.AddMinutes(30) }, plan);         // "Atualizar leituras"
        Assert.DoesNotContain(again.Steps, x => x.State == StepState.Suggested && x.Limiteds.Any(c => c.UserAssetId == 101));
        Assert.Contains(again.Exclusions, e => e.Copy.UserAssetId == 101 && e.Dismissed && e.Why.StartsWith("sugestão descartada por você para @altconta72"));

        // Another destination with space can still get it.
        var lua = Acc(PlanFixture.Lua, "LuaNoite", "Alts", C(301, 4, "Epic Egg", 300));
        var other = Planner.Build(new PlanInput([alfa, kuba, lua], PlanFixture.Settings((PlanFixture.Kuba, 9_000), (PlanFixture.Lua, 9_000)), PlanFixture.Market, Now), again);
        Assert.Contains(other.Steps, x => x.State == StepState.Suggested && x.DestinationId == PlanFixture.Lua && x.Limiteds.Any(c => c.UserAssetId == 101));
        Assert.DoesNotContain(other.Steps, x => x.State == StepState.Suggested && x.DestinationId == PlanFixture.Kuba && x.Limiteds.Any(c => c.UserAssetId == 101));

        Assert.True(again.Restore(101));                                                    // "Voltar ao plano"
        var back = Planner.Build(input, again);
        Assert.Contains(back.Steps, x => x.State == StepState.Suggested && x.Limiteds.Any(c => c.UserAssetId == 101));

        // A discarded copy that left the source is forgotten.
        again.Dismiss(step, Now);
        var gone = Planner.Build(input with { Accounts = [Acc(PlanFixture.Alfa, "FonteAlfa", null, C(102, 3, "Dominus", 5_400)), kuba] }, again);
        Assert.DoesNotContain(gone.Dismissed, d => d.UserAssetId == 101);
    }

    [Fact]
    public void Destinations_chosen_one_by_one_are_the_only_ones_that_receive()
    {
        var s = PlanFixture.Settings();
        Assert.Equal(DistributionRole.Destination, Planner.RoleOf(s, PlanFixture.Lua, "Alts"));
        s.DestinationAccounts = [PlanFixture.Kuba, PlanFixture.Alfa];
        Assert.Equal(DistributionRole.Destination, Planner.RoleOf(s, PlanFixture.Kuba, "Alts"));
        Assert.Equal(DistributionRole.None, Planner.RoleOf(s, PlanFixture.Lua, "Alts"));      // paused: not marked
        Assert.Equal(DistributionRole.Source, Planner.RoleOf(s, PlanFixture.Alfa, null));    // a source never receives
        Assert.Equal(DistributionRole.None, Planner.RoleOf(s, 99, "Outro"));                   // not marked
    }

    [Fact]
    public void Copies_on_hold_are_planned_as_steps_that_wait_for_the_release()
    {
        // FonteAlfa, 22/11: Atomic Blue Hairdo and 2 Molten Lava Wings arrived on hold. The plan is ready; the trade waits.
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000), C(102, 3, "Dominus", 5_400, hold: true));
        var kuba = Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Epic Egg", 300), C(202, 5, "Wolf", 450));
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());

        Assert.Equal(2, plan.Steps.Count);
        var now = plan.Steps.Single(x => !x.WaitsForRelease);
        var later = plan.Steps.Single(x => x.WaitsForRelease);
        Assert.Equal([101L], now.Limiteds.Select(c => c.UserAssetId));   // the released copy: its own trade, not held up
        Assert.Equal([102L], later.Limiteds.Select(c => c.UserAssetId));
        Assert.Contains(later.Why, w => w.Contains("espera a liberação"));
        Assert.Contains(Planner.ReadyCheck(later, new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), plan), p => p.Contains("em holding"));
    }

    [Fact]
    public void Fills_a_planned_trade_up_to_three_Limiteds_then_needs_another_Small()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var accounts = new[]
        {
            Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 6, "Hat", 1_000), C(102, 6, "Hat", 1_000), C(103, 6, "Hat", 1_000), C(104, 6, "Hat", 1_000)),
            Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Epic Egg", 300)),
        };
        var plan = Planner.Build(new PlanInput(accounts, s, PlanFixture.Market, Now), new DistributionPlan());
        var step = Assert.Single(plan.Steps);
        Assert.Equal(3, step.Limiteds.Count);
        Assert.Null(step.SourceSmall); // the source has no Small beyond the reserve
        var left = Assert.Single(plan.Exclusions);
        Assert.Contains("não têm Small para enviar", left.Why);
    }

    [Fact]
    public void Blocked_destinations_say_why_and_held_Smalls_make_the_step_wait()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000), (PlanFixture.Lua, 9_000));
        var lua = Acc(PlanFixture.Lua, "Lua", "Alts", C(301, 4, "Epic Egg", 300));
        lua.Finance!.Plus = new PlusInfo { NoSubscription = true };
        var accounts = new[]
        {
            Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000)),
            Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 5, "Wolf", 450, hold: true)),
            lua,
        };
        var plan = Planner.Build(new PlanInput(accounts, s, PlanFixture.Market, Now), new DistributionPlan());
        var step = Assert.Single(plan.Steps);
        Assert.Equal(PlanFixture.Kuba, step.DestinationId); // Lua is blocked (no Plus)
        Assert.True(step.WaitsForSmall);
        Assert.Contains(step.Why, w => w.Contains("ainda está em holding"));

        s.TargetNetByAccount[PlanFixture.Kuba] = 100; // now nobody fits
        var none = Planner.Build(new PlanInput(accounts, s, PlanFixture.Market, Now), new DistributionPlan());
        Assert.Empty(none.Steps);
        Assert.Contains("sem espaço para 2.100", Assert.Single(none.Exclusions).Why);
    }

    [Fact]
    public void A_source_without_Plus_cannot_trade()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000));
        alfa.Finance!.Plus = new PlusInfo { NoSubscription = true };
        var plan = Planner.Build(new PlanInput([alfa, Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300))], s, PlanFixture.Market, Now), new DistributionPlan());
        Assert.Empty(plan.Steps);
        Assert.Contains("sem Plus", Assert.Single(plan.Exclusions).Why);
    }

    [Fact]
    public void Steps_being_worked_on_are_kept_reserve_their_copies_and_count_as_incoming()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 4_500));
        var accounts = new[]
        {
            Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000), C(102, 6, "Hat", 1_000), C(104, 4, "Epic Egg", 300), C(105, 5, "Wolf", 450)),
            Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Epic Egg", 300), C(202, 5, "Wolf", 450)),
        };
        var input = new PlanInput(accounts, s, PlanFixture.Market, Now);
        var first = Planner.Build(input, new DistributionPlan());
        var opened = first.Steps.Single();
        Assert.Equal(2, opened.Limiteds.Count); // Valk and Hat fit together (2.100 + 700)
        opened.State = StepState.Opened;

        var again = Planner.Build(input, first);
        Assert.Same(opened, again.Steps.Single(x => x.State == StepState.Opened));
        Assert.DoesNotContain(again.Steps.Where(x => x.State == StepState.Suggested).SelectMany(x => x.Limiteds), c => c.UserAssetId is 101 or 102);
        Assert.Equal(2_800, Planner.Committed(again, PlanFixture.Kuba));

        // A rebuild with the same composition keeps the suggestion's id.
        var p1 = Planner.Build(input, new DistributionPlan());
        var p2 = Planner.Build(input, p1);
        Assert.Equal(p1.Steps.Single().Id, p2.Steps.Single().Id);
    }

    [Fact]
    public void Ready_check_wants_fresh_reads_and_the_copies_in_place()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var accounts = new[]
        {
            Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000)),
            Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300)),
        };
        var input = new PlanInput(accounts, s, PlanFixture.Market, Now);
        var plan = Planner.Build(input, new DistributionPlan());
        Assert.Empty(Planner.ReadyCheck(plan.Steps[0], input, plan));

        var later = input with { Now = Now.AddHours(1) };
        Assert.Contains(Planner.ReadyCheck(plan.Steps[0], later, plan), p => p.Contains("há mais de 30 minutos"));

        accounts[0].Inventory!.Copies.Clear();
        Assert.Contains(Planner.ReadyCheck(plan.Steps[0], input, plan), p => p.Contains("não está mais em @FonteAlfa"));
    }

    [Fact]
    public void Copies_already_at_the_destination_flag_a_trade_done_outside_the_app()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = Acc(PlanFixture.Alfa, "FonteAlfa", null, C(101, 1, "Valk", 3_000));
        var kuba = Acc(PlanFixture.Kuba, "altconta72", "Alts", C(201, 4, "Egg", 300));
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());

        alfa.Inventory!.Copies.Clear();
        alfa.Inventory.ReadUtc = Now.AddMinutes(10);
        kuba.Inventory!.Copies.Add(C(101, 1, "Valk", 3_000, hold: true));
        Planner.Revalidate(plan, new PlanInput([alfa, kuba], s, PlanFixture.Market, Now.AddMinutes(10)));
        Assert.Equal(StepState.NeedsCheck, plan.Steps[0].State);
        Assert.Contains("fora do RbxSwitcher", plan.Steps[0].Attention);
    }
}

public class TradeTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private static string V1(long src, long dst, string srcItems, string dstItems, string status = "Completed", int robux = 0) => $$"""
        {"id":555,"status":"{{status}}","created":"{{Now:O}}","isActive":false,"offers":[
          {"user":{"id":{{src}},"name":"a"},"userAssets":[{{srcItems}}],"robux":{{robux}}},
          {"user":{"id":{{dst}},"name":"b"},"userAssets":[{{dstItems}}],"robux":0}]}
        """;

    private static string A(long uaid, long asset, string name, long rap) => $$"""{"id":{{uaid}},"assetId":{{asset}},"name":"{{name}}","serialNumber":{{uaid % 100}},"recentAveragePrice":{{rap}}}""";

    private static PlanStep Step() => new()
    {
        Id = "E001", SourceId = PlanFixture.Alfa, SourceName = "FonteAlfa", DestinationId = PlanFixture.Sigma, DestinationName = "sigma2",
        Limiteds = [new(101, 1, "Valk", 1, 2_100)], SourceSmall = new(104, 4, "Epic Egg", 4), DestinationSmall = new(401, 4, "Epic Egg", 1),
        CreatedUtc = Now.AddMinutes(-30), State = StepState.Opened,
    };

    [Fact]
    public void Parses_v1_and_v2_trades()
    {
        var t1 = TradeParsers.Detail(J(V1(1, 12, A(101, 1, "Valk", 3000), A(401, 4, "Epic Egg", 300))));
        Assert.Equal((555L, "Completed", 2), (t1.Id, t1.Status, t1.Offers.Count));
        Assert.Equal(101, t1.Offers[0].Items[0].UserAssetId);

        var t2 = TradeParsers.Detail(J("""
            {"id":556,"status":"Open","participantAOffer":{"user":{"id":1},"items":[{"itemTarget":{"targetId":1},"itemName":"Valk","serialNumber":7,"collectibleItemInstanceId":"x-1"}],"robux":0},
             "participantBOffer":{"user":{"id":12},"items":[],"robux":0}}
            """));
        Assert.Null(t2.Offers[0].Items[0].UserAssetId);
        Assert.Equal("x-1", t2.Offers[0].Items[0].InstanceId);
        Assert.Throws<FormatException>(() => TradeParsers.Detail(J("""{"id":1,"offers":[]}""")));
    }

    [Fact]
    public void The_sigma2_case_another_Small_is_accepted_recorded_and_used_by_the_next_step()
    {
        // Plan: Epic Egg from FonteAlfa; the trade carried Deteggctive W. Wolf. Then Beta → sigma2 should use the Wolf.
        var s = PlanFixture.Settings((PlanFixture.Sigma, 20_000));
        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, PlanFixture.Copy(101, 1, "Valk", 3_000),
            PlanFixture.Copy(104, 4, "Epic Egg", 300), PlanFixture.Copy(106, 5, "Deteggctive W. Wolf", 450));
        var beta = PlanFixture.Acc(PlanFixture.Beta, "Beta", null, PlanFixture.Copy(701, 6, "Hat", 1_000));
        var sigma = PlanFixture.Acc(PlanFixture.Sigma, "sigma2", "Alts", PlanFixture.Copy(401, 4, "Epic Egg", 300));
        var accounts = new[] { alfa, beta, sigma };
        var step = Step();

        var t = TradeParsers.Detail(J(V1(1, 12, A(101, 1, "Valk", 3000) + "," + A(106, 5, "Deteggctive W. Wolf", 450), A(401, 4, "Epic Egg", 300))));
        var v = TradeVerifier.Verify(step, t, s, PlanFixture.Market);
        Assert.Equal(VerifyOutcome.Matches, v.Outcome);
        Assert.Contains(v.Notes, n => n.Contains("o plano previa Epic Egg #4, a trade levou Deteggctive W. Wolf"));

        PlanUpdates.ApplyTrade(step, t, v, accounts, Now);
        Assert.Equal(StepState.Completed, step.State);
        Assert.Equal(555, step.TradeId);
        Assert.Contains(sigma.Inventory!.Copies, c => c.UserAssetId == 106 && c.OnHold == true); // the real copy is at sigma2
        Assert.DoesNotContain(alfa.Inventory!.Copies, c => c.UserAssetId is 101 or 106);
        Assert.Contains(alfa.Inventory.Copies, c => c.UserAssetId == 401);

        var plan = new DistributionPlan { Steps = [step], NextNumber = 2 };
        var next = Planner.Build(new PlanInput(accounts, s, PlanFixture.Market, Now), plan).Steps.Single(x => x.State == StepState.Suggested);
        Assert.Equal((PlanFixture.Beta, 106L), (next.SourceId, next.DestinationSmall!.UserAssetId)); // sigma2 sends the Wolf it received
        Assert.True(next.WaitsForSmall);                                                                 // once released
    }

    [Fact]
    public void Missing_extra_or_wrong_trades_need_a_check_and_declined_ones_free_the_copies()
    {
        var s = PlanFixture.Settings();
        var accounts = Array.Empty<AccountSnapshot>();

        var missing = TradeParsers.Detail(J(V1(1, 12, A(104, 4, "Epic Egg", 300), A(401, 4, "Epic Egg", 300))));
        var step = Step();
        var v = TradeVerifier.Verify(step, missing, s, PlanFixture.Market);
        Assert.Equal(VerifyOutcome.NeedsCheck, v.Outcome);
        PlanUpdates.ApplyTrade(step, missing, v, accounts, Now);
        Assert.Equal(StepState.NeedsCheck, step.State);
        Assert.Contains("Valk #1 não está na trade", step.Attention);

        var extra = TradeParsers.Detail(J(V1(1, 12, A(101, 1, "Valk", 3000) + "," + A(102, 3, "Dominus", 5400), A(401, 4, "Epic Egg", 300))));
        Assert.Contains(TradeVerifier.Verify(Step(), extra, s, PlanFixture.Market).Notes, n => n.Contains("item não planejado saiu de @FonteAlfa: Dominus"));

        var wrong = TradeParsers.Detail(J(V1(1, 99, A(101, 1, "Valk", 3000), A(401, 4, "Epic Egg", 300))));
        var ws = Step();
        PlanUpdates.ApplyTrade(ws, wrong, TradeVerifier.Verify(ws, wrong, s, PlanFixture.Market), accounts, Now);
        Assert.Equal(StepState.Opened, ws.State);
        Assert.Null(ws.TradeId);

        var declined = TradeParsers.Detail(J(V1(1, 12, A(101, 1, "Valk", 3000), A(401, 4, "Epic Egg", 300), status: "Declined")));
        var ds = Step();
        PlanUpdates.ApplyTrade(ds, declined, TradeVerifier.Verify(ds, declined, s, PlanFixture.Market), accounts, Now);
        Assert.Equal(StepState.Declined, ds.State);
        Assert.False(ds.Commits);

        var open = TradeParsers.Detail(J(V1(1, 12, A(101, 1, "Valk", 3000), A(401, 4, "Epic Egg", 300), status: "Open")));
        var os = Step();
        PlanUpdates.ApplyTrade(os, open, TradeVerifier.Verify(os, open, s, PlanFixture.Market), accounts, Now);
        Assert.Equal(StepState.Sent, os.State);
    }

    [Fact]
    public void Locating_picks_trades_with_the_destination_created_after_the_step()
    {
        var step = Step();
        var found = PlanUpdates.Candidates(step, [
            new(1, PlanFixture.Sigma, Now.AddDays(-2), "Completed"),   // before the step
            new(2, 99, Now, "Completed"),                              // another partner
            new(3, PlanFixture.Sigma, Now.AddMinutes(-5), "Completed"),
            new(4, PlanFixture.Sigma, Now, "Open"),
        ]).Select(t => t.Id).ToList();
        Assert.Equal([4L, 3], found);
    }
}

public class SalesTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;

    [Fact]
    public void Sale_page_keeps_item_sales_only()
    {
        var (items, next) = SalesParsers.Page(JsonDocument.Parse($$$"""
            {"nextPageCursor":"c2","data":[
              {"idHash":"h1","created":"{{{Now:O}}}","isPending":true,"details":{"id":1,"name":"Valk","type":"Asset"},"currency":{"amount":2100,"type":"Robux"}},
              {"idHash":"h2","created":"{{{Now:O}}}","details":{"id":9,"name":"Pass","type":"GamePass"},"currency":{"amount":10,"type":"Robux"}}]}
            """).RootElement);
        var sale = Assert.Single(items);
        Assert.Equal(("h1", 1L, 2_100L, true), (sale.Id, sale.AssetId, sale.Amount, sale.Pending));
        Assert.Equal("c2", next);
    }

    private static PlanStep Delivered(string id, params CopyRef[] copies) => new()
    {
        Id = id, SourceId = PlanFixture.Alfa, DestinationId = PlanFixture.Kuba, DestinationName = "altconta72",
        Limiteds = [.. copies], State = StepState.Completed, CompletedUtc = Now.AddDays(-3),
    };

    [Fact]
    public void Links_only_when_safe()
    {
        var plan = new DistributionPlan
        {
            Steps = [Delivered("E001", new CopyRef(101, 1, "Valk", 1, 2_100)), Delivered("E002", new CopyRef(102, 6, "Hat", 2, 700), new CopyRef(103, 6, "Hat", 3, 700))],
        };
        var kuba = PlanFixture.Acc(PlanFixture.Kuba, "altconta72", "Alts", PlanFixture.Copy(103, 6, "Hat", 1_000));
        kuba.Inventory!.ReadUtc = Now;
        var sales = new Dictionary<long, AccountSales>
        {
            [PlanFixture.Kuba] = new()
            {
                Records = [
                    new("s1", Now.AddDays(-1), 1, "Valk", 2_100, false),  // the only Valk → linked
                    new("s2", Now.AddDays(-1), 6, "Hat", 700, false),     // two Hats; 102 left the inventory → linked by inventory
                    new("s3", Now.AddDays(-1), 9, "Other", 50, false),    // not from the plan
                ],
            },
        };
        var (views, delivered) = SalesMatcher.Match(plan, [kuba], sales);
        Assert.Equal(SaleLink.Linked, views.Single(v => v.Sale.Id == "s1").Link);
        var hat = views.Single(v => v.Sale.Id == "s2");
        Assert.Equal((SaleLink.Linked, 102L), (hat.Link, hat.Copy!.UserAssetId));
        Assert.Equal(SaleLink.NotFromPlan, views.Single(v => v.Sale.Id == "s3").Link);
        Assert.Equal(DeliveredState.CanList, delivered.Single(d => d.Copy.UserAssetId == 103).State);

        // Without a fresh inventory, two identical copies cannot be told apart: a conferir.
        kuba.Inventory.ReadUtc = Now.AddDays(-2);
        var (again, _) = SalesMatcher.Match(plan, [kuba], sales);
        Assert.Equal(SaleLink.Ambiguous, again.Single(v => v.Sale.Id == "s2").Link);
    }
}
