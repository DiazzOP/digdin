using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

public class LimitWatchTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    [Fact]
    public async Task Plus_ending_lowers_the_caps_and_is_reported_once()
    {
        var fake = new FakeRoblox();
        using var api = new RobloxApi(fake);
        var clock = Now;
        var refresher = new FinanceRefresher(api, () => clock);
        var first = (await refresher.RefreshAsync(77, Fake.Session(), null)).Finance;
        Assert.Empty(LimitWatch.Pending(first)); // first read: nothing to compare with

        fake.Caps = new { dailyLimit = 500, monthlyLimit = 1000, perTransferLimit = 500 };
        clock = Now.AddMinutes(25);
        var second = (await refresher.RefreshAsync(77, Fake.Session(), first)).Finance;
        var drops = LimitWatch.Pending(second);
        Assert.Equal(2, drops.Count);
        Assert.Equal((LimitKind.Daily, 5000L, 500L, LimitSource.Roblox), (drops[0].Kind, drops[0].From, drops[0].To, drops[0].Source));
        Assert.Equal((LimitKind.Monthly, 10000L, 1000L, LimitSource.Roblox), (drops[1].Kind, drops[1].From, drops[1].To, drops[1].Source));

        // Same values again: still the same drop, same time.
        clock = Now.AddMinutes(50);
        var third = (await refresher.RefreshAsync(77, Fake.Session(), second)).Finance;
        Assert.Equal(Now.AddMinutes(25), LimitWatch.Pending(third)[0].AtUtc);

        // "Ok, entendi" hides it, and a later read of the same values does not bring it back.
        LimitWatch.Acknowledge(third, clock);
        clock = Now.AddMinutes(75);
        var fourth = (await refresher.RefreshAsync(77, Fake.Session(), third)).Finance;
        Assert.Empty(LimitWatch.Pending(fourth));
    }

    [Fact]
    public async Task The_owners_restriction_is_named_as_the_source()
    {
        var fake = new FakeRoblox();
        using var api = new RobloxApi(fake);
        var refresher = new FinanceRefresher(api, () => Now);
        var first = (await refresher.RefreshAsync(77, Fake.Session(), null)).Finance;
        fake.Settings = new { robuxTransferLimits = new { currentValue = new { daily = 500, monthly = (long?)null } } };
        var second = (await refresher.RefreshAsync(77, Fake.Session(), first)).Finance;
        var drop = Assert.Single(LimitWatch.Pending(second));
        Assert.Equal((LimitKind.Daily, 5000L, 500L, LimitSource.Restriction), (drop.Kind, drop.From, drop.To, drop.Source));
    }

    [Fact]
    public async Task A_failed_read_is_not_a_change()
    {
        var fake = new FakeRoblox();
        using var api = new RobloxApi(fake);
        var refresher = new FinanceRefresher(api, () => Now);
        var first = (await refresher.RefreshAsync(77, Fake.Session(), null)).Finance;
        fake.Caps = new { unexpected = true };
        var second = (await refresher.RefreshAsync(77, Fake.Session(), first)).Finance;
        Assert.Contains(FinanceField.Caps, second.FailedReads);
        Assert.Empty(LimitWatch.Pending(second));
    }

    [Fact]
    public void Back_to_the_original_limit_clears_it_and_a_further_drop_updates_it()
    {
        var f = new AccountFinance();
        var high = new TransferCaps { Daily = 5000, Monthly = 10000 };
        LimitWatch.Track(f, true, high, null, new TransferCaps { Daily = 500, Monthly = 10000 }, null, Now);
        Assert.Equal(500, Assert.Single(f.LimitDrops).To);

        LimitWatch.Track(f, true, new TransferCaps { Daily = 500, Monthly = 10000 }, null, new TransferCaps { Daily = 100, Monthly = 10000 }, null, Now.AddHours(1));
        var d = Assert.Single(f.LimitDrops);
        Assert.Equal((5000L, 100L), (d.From, d.To)); // still measured from the original

        LimitWatch.Track(f, true, new TransferCaps { Daily = 100, Monthly = 10000 }, null, high, null, Now.AddHours(2));
        Assert.Empty(f.LimitDrops);
    }

    [Fact]
    public void Raising_a_limit_or_reading_it_for_the_first_time_is_not_a_drop()
    {
        var f = new AccountFinance();
        LimitWatch.Track(f, false, null, null, new TransferCaps { Daily = 500, Monthly = 1000 }, null, Now);
        LimitWatch.Track(f, true, new TransferCaps { Daily = 500, Monthly = 1000 }, null, new TransferCaps { Daily = 5000, Monthly = 10000 }, null, Now);
        Assert.Empty(f.LimitDrops);
    }

    [Fact]
    public void An_acknowledgement_made_during_a_refresh_is_kept()
    {
        var before = new AccountFinance { LimitDrops = [new LimitDrop { Kind = LimitKind.Daily, From = 5000, To = 500, AtUtc = Now }] };
        var refreshed = before.Clone();                 // the refresh started from this copy
        LimitWatch.Acknowledge(before, Now.AddMinutes(1)); // "Ok, entendi" while it ran
        LimitWatch.KeepAcknowledgement(before, refreshed);
        Assert.Empty(LimitWatch.Pending(refreshed));
    }

    [Fact]
    public async Task A_429_on_the_history_is_reported_once_with_the_wait()
    {
        var fake = new FakeRoblox();
        fake.Pages[""] = (null, null); // the history answers 429
        using var api = new RobloxApi(fake);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null);

        Assert.NotNull(r.RateLimitedFor);
        var reasons = QuotaCalculator.Compute(r.Finance, Now).ToVerifyReasons;
        Assert.Single(reasons, x => x.StartsWith(QuotaCalculator.RateLimitedReason));
        Assert.DoesNotContain("erro na leitura da cota", reasons);
        Assert.DoesNotContain(reasons, x => x.Contains("com mais de 30 minutos") || x.Contains("não cobre 30 dias"));
    }

    [Fact]
    public async Task A_normal_refresh_is_not_rate_limited()
    {
        using var api = new RobloxApi(new FakeRoblox());
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null);
        Assert.Null(r.RateLimitedFor);
    }
}
