using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fictitious sales: when pending Robux should fall (learned from sales seen going from pending to credited).</summary>
public class RobuxCalendarTests
{
    // 23/11/2026 15:00 in Brasília.
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private static SaleRecord Sale(string id, double daysAgo, long amount, bool pending, string name = "Valk") =>
        new(id, Now.AddDays(-daysAgo), 1, name, amount, pending);

    [Fact]
    public void A_sale_seen_going_from_pending_to_credited_is_remembered_once()
    {
        var sales = new AccountSales { ReadUtc = Now.AddHours(-1), Records = [Sale("s1", 5, 700, true), Sale("s2", 1, 300, true)] };
        RobuxCalendar.ApplyRead(sales, [Sale("s1", 5, 700, false), Sale("s2", 1, 300, true)], Now);
        var r = Assert.Single(sales.Releases);
        Assert.Equal(("s1", 700L, Now.AddHours(-1), Now), (r.SaleId, r.Amount, r.SeenPendingUtc, r.SeenCreditedUtc));
        Assert.True(r.Precise);
        Assert.Equal(Now, sales.ReadUtc);
        Assert.Equal(2, sales.Records.Count);

        RobuxCalendar.ApplyRead(sales, [Sale("s1", 5, 700, false), Sale("s2", 1, 300, true)], Now.AddMinutes(30));
        Assert.Single(sales.Releases); // not twice
    }

    private static PendingRelease Seen(string id, double holdDays, double gapHours = 0.5) =>
        new(id, Now.AddDays(-20), Now.AddDays(-20 + holdDays).AddHours(-gapHours), Now.AddDays(-20 + holdDays), 500, 1, "Valk");

    [Fact]
    public void The_wait_comes_from_the_accounts_own_sightings_then_its_sales_then_every_account_then_a_week()
    {
        var own = new AccountSales { Releases = [Seen("a", 5), Seen("b", 6), Seen("c", 6.5)] };
        var h = RobuxCalendar.EstimateHold(own, [own]);
        Assert.Equal((HoldBasis.AccountHistory, TimeSpan.FromDays(6), 3), (h.Basis, h.Typical, h.Samples));
        Assert.Equal(TimeSpan.FromDays(6.5), h.High);

        // No sightings of its own: its sales say "more than 2 days, at most 4 days" (pending 2 days old, credited 4 days old).
        var fresh = new AccountSales { ReadUtc = Now, Records = [Sale("p", 2, 100, true), Sale("c", 4, 100, false), Sale("c2", 9, 100, false)] };
        var other = new AccountSales { Releases = [Seen("x", 7), Seen("y", 8)] };
        h = RobuxCalendar.EstimateHold(fresh, [fresh, other]);
        Assert.Equal((HoldBasis.AccountSales, TimeSpan.FromDays(4)), (h.Basis, h.Typical)); // the others' 8 days do not fit: at most 4
        Assert.Equal(TimeSpan.FromDays(2), h.Low);

        // Mixed ages (a credited sale younger than a pending one): its sales say nothing; every account's sightings.
        var mixed = new AccountSales { ReadUtc = Now, Records = [Sale("p", 6, 100, true), Sale("c", 3, 100, false)] };
        Assert.Equal((HoldBasis.AllHistory, TimeSpan.FromDays(8)), (RobuxCalendar.EstimateHold(mixed, [mixed, other]) is var m ? (m.Basis, m.Typical) : default));

        var none = RobuxCalendar.EstimateHold(null, []);
        Assert.Equal((HoldBasis.Default, TimeSpan.FromDays(7), TimeSpan.FromDays(30)), (none.Basis, none.Typical, none.High));
    }

    [Fact]
    public void Pending_sales_land_on_the_day_they_should_fall_and_late_ones_on_today()
    {
        var alfa = new AccountSales
        {
            ReadUtc = Now,
            Records = [Sale("s1", 1, 1_284, true, "Molten Lava Wings"), Sale("s2", 3, 700, true), Sale("s3", 10, 999, false)],
            Releases = [Seen("a", 5), Seen("b", 5)],
        };
        var beta = new AccountSales { ReadUtc = Now, Records = [Sale("m1", 6, 2_000, true)] };
        var v = RobuxCalendar.Build([
            new CalendarInput(1, "FonteAlfa", alfa, 2_284),
            new CalendarInput(2, "Beta", beta, 2_000),
            new CalendarInput(3, "altconta72", null, 450),
            new CalendarInput(4, "semnada", new AccountSales { ReadUtc = Now }, 0),
        ], Now);

        // FonteAlfa waits 5 days: s1 (yesterday) → 27/11, s2 (3 days ago) → 25/11. Its summary has 300 more than the sales.
        Assert.Equal(1_284, v.Days[new DateOnly(2026, 11, 27)].Expected);
        Assert.Equal(700, v.Days[new DateOnly(2026, 11, 25)].Expected);
        // Beta has no sightings and only a pending sale: every account's sightings (5 days) → already late: today.
        Assert.Equal(2_000, v.DueToday);
        var late = v.Days[new DateOnly(2026, 11, 23)].Entries.Single(e => !e.Fell);
        Assert.True(late.Late);
        Assert.Equal(Now.AddDays(-6).AddDays(30), late.LatestUtc); // past the estimate: Roblox's own limit
        Assert.Equal(3_984, v.DueNext7Days);
        Assert.Equal((3_984L, 3), (v.PendingFromSales, v.PendingCount));

        var d = v.Accounts.Single(a => a.AccountName == "FonteAlfa");
        Assert.Contains("300 pendentes sem venda de Limited", d.Note);
        Assert.Equal("s2", d.Next!.SaleId);
        Assert.Contains("ainda não lidas", v.Accounts.Single(a => a.AccountName == "altconta72").Note);
        Assert.DoesNotContain(v.Accounts, a => a.AccountName == "semnada");
        // Sightings show where the Robux fell (20 days ago + 5).
        Assert.Equal(1_000, v.Days[RobuxCalendar.Day(Now.AddDays(-15))].Fell);
    }
}
