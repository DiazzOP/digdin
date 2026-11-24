using System.Text.Json;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Tests;

/// <summary>Review of the Distribuir tab (22/11): Rolimons lag, twin copies in v2 trades, trade search, fresh balance.</summary>
public class DistributeReviewTests
{
    private static readonly DateTimeOffset Now = PlanFixture.Now;
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static LimitedCopy R(long uaid, long asset, string name, bool? hold = null) => new(uaid, asset, name, null, 1_835, hold, FromRolimons: true);

    private static PlanStep Done(long src, long dst, DateTimeOffset at, params CopyRef[] limiteds) => new()
    {
        Id = "E009", SourceId = src, SourceName = "FonteAlfa", DestinationId = dst, DestinationName = "altconta72", Limiteds = [.. limiteds],
        DestinationSmall = new(401, 4, "Epic Egg", 1), State = StepState.Completed, CreatedUtc = at.AddHours(-1), CompletedUtc = at, TradeId = 777,
        ActualFromSource = [.. limiteds], ActualFromDestination = [new(401, 4, "Epic Egg", 1)],
    };

    [Fact]
    public void A_copy_traded_away_is_not_planned_again_while_Rolimons_still_lists_it()
    {
        // Molten Lava Wings 901 went to kuba an hour ago; Rolimons still shows it at FonteAlfa and not yet at kuba.
        var step = Done(PlanFixture.Alfa, PlanFixture.Kuba, Now.AddHours(-1), new CopyRef(901, 7, "Molten Lava Wings", null, 1_284));
        var alfa = new AccountInventory { Complete = true, ReadUtc = Now, Copies = [R(901, 7, "Molten Lava Wings"), R(902, 7, "Molten Lava Wings")] };
        var kuba = new AccountInventory { Complete = true, ReadUtc = Now, Copies = [] }; // Roblox's own list already shows the Small gone

        var d = PlanInventory.For(PlanFixture.Alfa, alfa, null, [step], Now)!;
        Assert.Equal([902L, 401L], d.Copies.Select(c => c.UserAssetId)); // 901 left; the Small received (401) counts until the reads show it
        Assert.Equal(2, alfa.Copies.Count); // the stored inventory is not changed

        var k = PlanInventory.For(PlanFixture.Kuba, kuba, null, [step], Now)!;
        Assert.Contains(k.Copies, c => c.UserAssetId == 901 && c.OnHold == true); // counts in kuba's stock (space) meanwhile
        Assert.DoesNotContain(k.Copies, c => c.UserAssetId == 401);                 // its Small went to FonteAlfa

        // Days later Rolimons is trusted again, and a copy Roblox itself lists is never dropped.
        var robloxListed = new AccountInventory { Complete = true, ReadUtc = Now, Copies = [new LimitedCopy(901, 7, "Molten Lava Wings", null, 1_835, null)] };
        Assert.Contains(PlanInventory.For(PlanFixture.Alfa, robloxListed, null, [step], Now)!.Copies, c => c.UserAssetId == 901);
        var later = PlanInventory.For(PlanFixture.Alfa, alfa, null, [step], Now.AddDays(3))!;
        Assert.Equal(2, later.Copies.Count);
    }

    [Fact]
    public void The_accounts_own_Marketplace_copies_cap_Rolimons_only_copies_only_when_read_after_the_inventory()
    {
        var inv = new AccountInventory { Complete = true, ReadUtc = Now.AddMinutes(-10), Copies = [R(901, 7, "Molten Lava Wings"), R(902, 7, "Molten Lava Wings")] };
        var own = new AccountListings();
        own.OwnedByItem[7] = 1; own.HeldByItem[7] = 1; own.ItemReadUtc[7] = Now; // one copy left (sold), and it is on hold
        var v = PlanInventory.For(PlanFixture.Alfa, inv, own, [], Now)!;
        Assert.Equal([901L], v.Copies.Select(c => c.UserAssetId));
        Assert.True(v.Copies[0].OnHold);

        own.ItemReadUtc[7] = Now.AddMinutes(-20); // read before the inventory: a copy may have arrived since
        Assert.Equal(2, PlanInventory.For(PlanFixture.Alfa, inv, own, [], Now)!.Copies.Count);
    }

    private static string V2(long src, long dst, string srcItems, string dstItems) =>
        "{\"id\":556,\"status\":\"Completed\",\"participantAOffer\":{\"user\":{\"id\":" + src + "},\"items\":[" + srcItems + "],\"robux\":0}," +
        "\"participantBOffer\":{\"user\":{\"id\":" + dst + "},\"items\":[" + dstItems + "],\"robux\":0}}";

    private static string I(long asset, string name, string instance) =>
        "{\"itemTarget\":{\"targetId\":" + asset + "},\"itemName\":\"" + name + "\",\"collectibleItemInstanceId\":\"" + instance + "\"}";

    [Fact]
    public void Two_copies_of_the_same_item_without_ids_in_a_v2_trade_match_the_two_planned_ones()
    {
        var s = PlanFixture.Settings();
        var step = new PlanStep
        {
            Id = "E004", SourceId = PlanFixture.Alfa, SourceName = "FonteAlfa", DestinationId = PlanFixture.Kuba, DestinationName = "altconta72",
            Limiteds = [new(901, 7, "Molten Lava Wings", null, 1_284), new(902, 7, "Molten Lava Wings", null, 1_284)],
            DestinationSmall = new(401, 4, "Epic Egg", null), State = StepState.Opened, CreatedUtc = Now.AddHours(-1),
        };
        var t = TradeParsers.Detail(J(V2(PlanFixture.Alfa, PlanFixture.Kuba,
            I(7, "Molten Lava Wings", "x-1") + "," + I(7, "Molten Lava Wings", "x-2"), I(4, "Epic Egg", "x-3"))));
        var v = TradeVerifier.Verify(step, t, s, PlanFixture.Market);
        Assert.Equal(VerifyOutcome.Matches, v.Outcome);
        Assert.Equal([901L, 902L], v.Limiteds!.Select(c => c.UserAssetId));
        Assert.DoesNotContain(v.Notes, n => n.Contains("outra cópia"));

        // A third copy of the item is not in the plan: it needs a look.
        var extra = TradeParsers.Detail(J(V2(PlanFixture.Alfa, PlanFixture.Kuba,
            I(7, "Molten Lava Wings", "x-1") + "," + I(7, "Molten Lava Wings", "x-2") + "," + I(7, "Molten Lava Wings", "x-4"), I(4, "Epic Egg", "x-3"))));
        Assert.Equal(VerifyOutcome.NeedsCheck, TradeVerifier.Verify(step, extra, s, PlanFixture.Market).Outcome);
    }

    [Fact]
    public void Locating_a_trade_skips_trades_already_tied_to_another_step()
    {
        var done = Done(PlanFixture.Alfa, PlanFixture.Kuba, Now.AddMinutes(-20), new CopyRef(101, 1, "Valk", 1, 2_100));
        var open = new PlanStep { Id = "E010", SourceId = PlanFixture.Alfa, DestinationId = PlanFixture.Kuba, CreatedUtc = Now.AddMinutes(-30), State = StepState.Opened };
        var plan = new DistributionPlan { Steps = [done, open] };
        var trades = new[] { new TradeSummary(777, PlanFixture.Kuba, Now.AddMinutes(-25), "Completed"), new TradeSummary(778, PlanFixture.Kuba, Now.AddMinutes(-2), "Open") };
        Assert.Equal([778L], PlanUpdates.Candidates(open, trades, plan).Select(t => t.Id));
        Assert.Same(done, PlanUpdates.LinkedElsewhere(plan, open, 777));
    }

    [Fact]
    public void Opening_needs_a_fresh_balance_of_the_destination_and_a_held_copy_left_out_says_so()
    {
        var s = PlanFixture.Settings((PlanFixture.Kuba, 9_000));
        var alfa = PlanFixture.Acc(PlanFixture.Alfa, "FonteAlfa", null, PlanFixture.Copy(101, 1, "Valk", 3_000), PlanFixture.Copy(103, 2, "Fedora", 12_500, hold: true));
        var kuba = PlanFixture.Acc(PlanFixture.Kuba, "altconta72", "Alts", PlanFixture.Copy(201, 4, "Epic Egg", 300));
        var plan = Planner.Build(new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), new DistributionPlan());
        var step = plan.Steps.Single();
        Assert.Empty(Planner.ReadyCheck(step, new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), plan));
        Assert.Contains(plan.Exclusions, e => e.Copy.UserAssetId == 103 && e.Why.StartsWith("em holding; sem espaço"));

        kuba.Finance!.RobuxAtUtc = Now.AddHours(-2);
        Assert.Contains(Planner.ReadyCheck(step, new PlanInput([alfa, kuba], s, PlanFixture.Market, Now), plan), p => p == "@altconta72: saldo lido há mais de 30 minutos");
    }
}
