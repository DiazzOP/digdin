using System.Net;
using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fictitious data only. "Now" is Mon 2026-11-16 15:00 in Brasília (18:00 UTC).</summary>
public class QuotaCalculatorTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Brt(int day, int h, int m = 0, int s = 0) => new(2026, 11, day, h, m, s, TimeSpan.FromHours(-3));

    internal static AccountFinance Fresh(long robux, long capDaily, long capMonthly, params (DateTimeOffset At, long Amount)[] sent)
    {
        int n = 0;
        return new AccountFinance
        {
            Robux = robux, RobuxAtUtc = Now.AddMinutes(-1),
            PendingRobux = 0, PendingAtUtc = Now.AddMinutes(-1),
            Plus = new PlusInfo { Active = true, Status = "Active", ExpiresUtc = Now.AddDays(20) }, PlusAtUtc = Now.AddMinutes(-1),
            Caps = new TransferCaps { Daily = capDaily, Monthly = capMonthly, PerTransfer = 2_000 }, CapsAtUtc = Now.AddMinutes(-1),
            Restriction = null, RestrictionAtUtc = Now.AddMinutes(-1),
            Ledger = new TransferLedger
            {
                Version = TransferLedger.CurrentVersion,
                CompleteAsOfUtc = Now.AddMinutes(-1),
                Entries = sent.Select(x => new TransferEntry { Id = "t" + n++, CreatedUtc = x.At, Amount = x.Amount }).ToList(),
            },
        };
    }

    [Fact]
    public void Case1_capacity_is_the_smallest_of_balance_daily_and_monthly()
    {
        // Sent today (Brasília): 1,200 → 5,000 − 1,200 = 3,800 left today.
        // Sent in 30 days: 1,200 + 8,747 = 9,947 → 10,000 − 9,947 = 53 left this month.
        var f = Fresh(1_693, 5_000, 10_000, (Brt(16, 9), 1_200), (Brt(1, 12), 8_747));
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(1_200, v.SentToday);
        Assert.Equal(9_947, v.Sent30Days);
        Assert.Equal(3_800, v.RemainingDaily);
        Assert.Equal(53, v.RemainingMonthly);
        Assert.Equal(53, v.Capacity);
        Assert.Equal(CapacityState.Estimated, v.State);
        Assert.Empty(v.ToVerifyReasons);
        Assert.Equal(2_000, v.PerTransferLimit); // kept apart from the total capacity
    }

    [Fact]
    public void Case3_configured_daily_restriction_lowers_the_cap()
    {
        var f = Fresh(10_000, 5_000, 50_000);
        f.Restriction = new TransferRestriction { Daily = 500 };
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(5_000, v.CapDaily);          // Roblox value, shown as is
        Assert.Equal(500, v.EffectiveDaily);
        Assert.Equal(50_000, v.EffectiveMonthly); // no monthly restriction
        Assert.Equal(500, v.Capacity);
    }

    [Fact]
    public void Zero_is_a_valid_limit()
    {
        var f = Fresh(10_000, 5_000, 50_000);
        f.Restriction = new TransferRestriction { Daily = 0, Monthly = 20_000 };
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(0, v.EffectiveDaily);
        Assert.Equal(20_000, v.EffectiveMonthly);
        Assert.Equal(0, v.Capacity);
        Assert.Equal(CapacityState.Estimated, v.State);
    }

    [Fact]
    public void Case4a_partial_history_is_to_verify_and_never_zero_usage()
    {
        var f = Fresh(5_000, 5_000, 10_000);
        f.Ledger = null;                    // the crawl never completed
        f.Checkpoint = new LedgerCheckpoint { AnchorUtc = Now, Cursor = "p2", Entries = [new TransferEntry { Id = "x", CreatedUtc = Now.AddHours(-1), Amount = 300 }] };
        f.QuotaReadFailed = true;
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Null(v.SentToday);
        Assert.Null(v.RemainingDaily);      // NOT 5,000: unknown usage is not zero usage
        Assert.Null(v.RemainingMonthly);
        Assert.Null(v.Capacity);
        Assert.Equal(CapacityState.ToVerify, v.State);
        Assert.Contains("histórico de envios não cobre 30 dias", v.ToVerifyReasons);
    }

    [Fact]
    public void Case4b_balance_older_than_30_minutes_is_to_verify()
    {
        var f = Fresh(1_000, 5_000, 10_000);
        f.RobuxAtUtc = Now.AddMinutes(-31);
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(CapacityState.ToVerify, v.State);
        Assert.Contains("saldo com mais de 30 minutos", v.ToVerifyReasons);
        Assert.Equal(Now.AddMinutes(-1), v.LastReadUtc); // last read time is kept
    }

    [Fact]
    public void Case4c_expired_or_inactive_plus_is_to_verify()
    {
        var f = Fresh(1_000, 5_000, 10_000);
        f.Plus = new PlusInfo { Active = true, ExpiresUtc = Now.AddSeconds(-1) };
        Assert.Contains("Plus expirado", QuotaCalculator.Compute(f, Now).ToVerifyReasons);
        f.Plus = new PlusInfo { Active = false, Status = "Sem assinatura" };
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(CapacityState.ToVerify, v.State);
        Assert.Contains("Plus expirado", v.ToVerifyReasons);
    }

    [Fact]
    public void Stale_quota_and_read_errors_are_to_verify()
    {
        var f = Fresh(1_000, 5_000, 10_000);
        f.Ledger!.CompleteAsOfUtc = Now.AddMinutes(-45);
        Assert.Contains("histórico de envios com mais de 30 minutos", QuotaCalculator.Compute(f, Now).ToVerifyReasons);
        f = Fresh(1_000, 5_000, 10_000);
        f.QuotaReadFailed = true;
        Assert.Contains("erro na leitura da cota", QuotaCalculator.Compute(f, Now).ToVerifyReasons);
    }

    [Fact]
    public void Case5_a_transfer_leaves_the_monthly_window_exactly_30_days_later()
    {
        var sentAt = new DateTimeOffset(2026, 10, 20, 14, 30, 0, TimeSpan.Zero);
        var f = Fresh(10_000, 5_000, 10_000, (sentAt, 4_000));
        var justBefore = sentAt.AddDays(30).AddSeconds(-1);
        var exactly = sentAt.AddDays(30);
        f.Ledger!.CompleteAsOfUtc = justBefore; f.RobuxAtUtc = f.CapsAtUtc = f.RestrictionAtUtc = f.PlusAtUtc = justBefore;
        Assert.Equal(4_000, QuotaCalculator.Compute(f, justBefore).Sent30Days);
        var v = QuotaCalculator.Compute(f, exactly);
        Assert.Equal(0, v.Sent30Days);
        Assert.Equal(10_000, v.RemainingMonthly);

        var atRelease = QuotaCalculator.Compute(f, justBefore).MonthlyReleases.Single();
        Assert.Equal(exactly, atRelease.AtUtc);
        Assert.Equal(4_000, atRelease.Amount);
    }

    [Fact]
    public void Case5_daily_estimate_turns_over_at_brasilia_midnight()
    {
        var sent = Brt(16, 23, 59, 0);               // 02:59 UTC on the 17th
        var f = Fresh(10_000, 5_000, 50_000, (sent, 700));
        var beforeMidnight = Brt(16, 23, 59, 59);
        var atMidnight = Brt(17, 0, 0, 0);           // 03:00 UTC
        f.Ledger!.CompleteAsOfUtc = beforeMidnight; f.RobuxAtUtc = f.CapsAtUtc = f.RestrictionAtUtc = f.PlusAtUtc = beforeMidnight;

        var before = QuotaCalculator.Compute(f, beforeMidnight);
        Assert.Equal(700, before.SentToday);
        Assert.Equal(4_300, before.RemainingDaily);
        Assert.Equal(atMidnight.ToUniversalTime(), before.NextDailyResetUtc.ToUniversalTime());

        var after = QuotaCalculator.Compute(f, atMidnight);
        Assert.Equal(0, after.SentToday);
        Assert.Equal(5_000, after.RemainingDaily);
        Assert.Equal(700, after.Sent30Days);         // still in the monthly window
    }

    [Fact]
    public void Future_records_are_not_counted()
    {
        var f = Fresh(10_000, 5_000, 10_000, (Now.AddMinutes(5), 999), (Now.AddMinutes(-5), 100));
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(100, v.SentToday);
        Assert.Equal(100, v.Sent30Days);
    }
}

/// <summary>Routes fake Roblox responses by URL; records every request.</summary>
internal sealed class FakeRoblox : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = [];
    public long UserId = 77;
    public long Robux = 1_693;
    public long Pending = 120;
    public object Subscriptions = new { subscriptions = new[] { new { productType = "RobloxPlus", status = "Active", expiration = "2026-12-31T00:00:00Z" } } };
    public object Caps = new { dailyLimit = 5000, monthlyLimit = 10000, perTransferLimit = 2000 };
    public object Settings = new { robuxTransferLimits = new { currentValue = (object?)null } };
    /// <summary>Pages of transactions by cursor ("" = first page). A page of null = respond 429.</summary>
    public Dictionary<string, (object[]? Items, string? Next)> Pages = new() { [""] = ([], null) };
    public string? RotateTo;

    public static object Tx(string id, DateTimeOffset at, long amount, string role) =>
        new { id, created = at.ToString("O"), currency = new { amount, type = "Robux" }, transferRole = role };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests.Add(req);
        var u = req.RequestUri!;
        string path = u.AbsolutePath;
        HttpResponseMessage resp;
        if (path == "/v1/users/authenticated") resp = Json(new { id = UserId, name = "Fake_" + UserId, displayName = "Fake" });
        else if (path == "/v1/user/currency") resp = Json(new { robux = Robux });
        else if (path.EndsWith("/transaction-totals")) resp = Json(new { pendingRobuxTotal = Pending, salesTotal = 0 });
        else if (path == "/subscriptions/v2/user/subscriptions") resp = Json(Subscriptions);
        else if (path == "/transfer/v1/robux-transfer/user-transfer-limit") resp = Json(Caps);
        else if (path.StartsWith("/user-settings-api/")) resp = Json(Settings);
        else if (path.Contains("/transaction-records/v1/users/"))
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            string cursor = q["cursor"] ?? "";
            var (items, next) = Pages[cursor];
            resp = items is null ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(new { previousPageCursor = (string?)null, nextPageCursor = next, data = items });
        }
        else resp = new HttpResponseMessage(HttpStatusCode.NotFound);
        if (RotateTo is not null && resp.IsSuccessStatusCode)
        {
            resp.Headers.Add("Set-Cookie", $".ROBLOSECURITY={RotateTo}; domain=.roblox.com; path=/; secure; HttpOnly");
            RotateTo = null;
        }
        return Task.FromResult(resp);
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o)) };
}

public class FinanceRefresherTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static DateTimeOffset Ago(double hours) => Now.AddHours(-hours);

    [Fact]
    public async Task Reads_everything_and_computes_from_fake_roblox()
    {
        var fake = new FakeRoblox();
        fake.Pages[""] = ([
            FakeRoblox.Tx("a", Ago(2), -1_200, "Sender"),
            FakeRoblox.Tx("in1", Ago(3), +900, "Receiver"),          // case 2: incoming never uses quota
            FakeRoblox.Tx("b", Ago(24 * 12), -8_747, "Sender"),
        ], "p2");
        fake.Pages["p2"] = ([FakeRoblox.Tx("old", Ago(24 * 31), -5_000, "Sender")], null);
        using var api = new RobloxApi(fake);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null);

        Assert.Equal(IdentityCheck.Confirmed, r.Identity);
        Assert.Empty(r.Finance.LastErrors);
        Assert.Equal(1_693, r.Finance.Robux);
        Assert.Equal(120, r.Finance.PendingRobux);
        Assert.True(r.Finance.Plus!.Active);
        Assert.DoesNotContain(r.Finance.Ledger!.Entries, e => e.Id == "in1");
        var v = QuotaCalculator.Compute(r.Finance, Now);
        Assert.Equal(1_200, v.SentToday);
        Assert.Equal(9_947, v.Sent30Days);
        Assert.Equal(53, v.Capacity);
        Assert.Equal(CapacityState.Estimated, v.State);
        Assert.All(fake.Requests, q => Assert.EndsWith(".roblox.com", q.RequestUri!.Host));
    }

    [Fact]
    public async Task Case2_received_transfer_raises_balance_but_not_quota_usage()
    {
        var fake = new FakeRoblox { Robux = 1_000 };
        fake.Pages[""] = ([FakeRoblox.Tx("s1", Ago(1), -300, "Sender")], null);
        using var api = new RobloxApi(fake);
        var refresher = new FinanceRefresher(api, () => Now);
        var first = await refresher.RefreshAsync(77, Fake.Session(), null);

        fake.Robux = 1_500; // Roblox credited a 500 transfer received
        fake.Pages[""] = ([FakeRoblox.Tx("r1", Now.AddSeconds(-30), +500, "Receiver"), FakeRoblox.Tx("s1", Ago(1), -300, "Sender")], null);
        var second = await refresher.RefreshAsync(77, Fake.Session(), first.Finance);
        var v = QuotaCalculator.Compute(second.Finance, Now);
        Assert.Equal(1_500, v.Robux);
        Assert.Equal(300, v.SentToday);
        Assert.Equal(4_700, v.RemainingDaily);
    }

    [Fact]
    public async Task Session_of_another_account_reads_nothing()
    {
        var fake = new FakeRoblox { UserId = 999 };
        using var api = new RobloxApi(fake);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null);
        Assert.Equal(IdentityCheck.WrongAccount, r.Identity);
        Assert.Single(fake.Requests); // only users/authenticated
        Assert.Null(r.Finance.Robux);
    }

    [Fact]
    public async Task Expired_session_reads_nothing()
    {
        var fake = new StubAuth(HttpStatusCode.Unauthorized);
        using var api = new RobloxApi(fake);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null);
        Assert.Equal(IdentityCheck.SessionExpired, r.Identity);
        Assert.Equal(1, fake.Calls);
    }

    private sealed class StubAuth(HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Calls++; return Task.FromResult(new HttpResponseMessage(code)); }
    }

    [Fact]
    public async Task Rate_limit_in_history_keeps_last_complete_ledger_and_checkpoint_resumes()
    {
        var fake = new FakeRoblox();
        fake.Pages[""] = ([FakeRoblox.Tx("a", Ago(1), -100, "Sender")], null);
        using var api = new RobloxApi(fake);
        DateTimeOffset clock = Now;
        var refresher = new FinanceRefresher(api, () => clock);
        var good = (await refresher.RefreshAsync(77, Fake.Session(), null)).Finance;
        Assert.NotNull(good.Ledger);

        // An hour later: a page fails with 429 halfway, and the ledger is older than 30 minutes.
        clock = Now.AddHours(1);
        fake.Pages[""] = ([FakeRoblox.Tx("new", clock.AddMinutes(-5), -50, "Sender")], "p2");
        fake.Pages["p2"] = (null, null);
        var paused = (await refresher.RefreshAsync(77, Fake.Session(), good)).Finance;
        Assert.True(paused.QuotaReadFailed);
        Assert.Equal(good.Ledger!.CompleteAsOfUtc, paused.Ledger!.CompleteAsOfUtc); // last complete history preserved
        Assert.Equal("p2", paused.Checkpoint!.Cursor);
        Assert.Contains(paused.Checkpoint.Entries, e => e.Id == "new");
        var pv = QuotaCalculator.Compute(paused, clock);
        Assert.Equal(CapacityState.ToVerify, pv.State);

        // Next refresh resumes at p2 (the first page is not read again), reaches the old ledger and completes.
        fake.Pages["p2"] = ([FakeRoblox.Tx("a", Ago(1), -100, "Sender"), FakeRoblox.Tx("older", Ago(3), -1, "Sender")], "p3");
        int before = fake.Requests.Count(q => q.RequestUri!.AbsolutePath.Contains("transaction-records") && !q.RequestUri.Query.Contains("cursor"));
        var done = (await refresher.RefreshAsync(77, Fake.Session(), paused)).Finance;
        int after = fake.Requests.Count(q => q.RequestUri!.AbsolutePath.Contains("transaction-records") && !q.RequestUri.Query.Contains("cursor"));
        Assert.Equal(before, after);
        Assert.False(done.QuotaReadFailed);
        Assert.Null(done.Checkpoint);
        Assert.Equal(clock, done.Ledger!.CompleteAsOfUtc);
        Assert.Equal(1, done.Ledger.Entries.Count(e => e.Id == "a")); // deduplicated
        var dv = QuotaCalculator.Compute(done, clock);
        Assert.Equal(151, dv.SentToday);
        Assert.Equal(CapacityState.Estimated, dv.State);
    }

    [Fact]
    public async Task Crawl_stops_at_the_30_day_window_and_dedupes_repeated_records()
    {
        var fake = new FakeRoblox();
        fake.Pages[""] = ([FakeRoblox.Tx("a", Ago(1), -10, "Sender"), FakeRoblox.Tx("b", Ago(48), -20, "Sender")], "p2");
        fake.Pages["p2"] = ([FakeRoblox.Tx("b", Ago(48), -20, "Sender"), FakeRoblox.Tx("c", Ago(24 * 29), -30, "Sender")], "p3");
        fake.Pages["p3"] = ([FakeRoblox.Tx("d", Ago(24 * 30.5), -40, "Sender")], "p4");
        fake.Pages["p4"] = ([FakeRoblox.Tx("e", Ago(24 * 60), -50, "Sender")], null); // must never be read
        using var api = new RobloxApi(fake);
        var f = (await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null)).Finance;
        Assert.DoesNotContain(fake.Requests, q => q.RequestUri!.Query.Contains("cursor=p4"));
        Assert.Equal(["a", "b", "c", "d"], f.Ledger!.Entries.Select(e => e.Id));
        Assert.Equal(60, QuotaCalculator.Compute(f, Now).Sent30Days); // d is outside the window
    }

    [Fact]
    public async Task Page_budget_pauses_without_inventing_a_ledger()
    {
        var fake = new FakeRoblox();
        for (int i = 0; i < 5; i++)
            fake.Pages[i == 0 ? "" : "p" + i] = ([FakeRoblox.Tx("t" + i, Ago(i + 1), -1, "Sender")], "p" + (i + 1));
        using var api = new RobloxApi(fake);
        var f = (await new FinanceRefresher(api, () => Now) { MaxHistoryPages = 3 }.RefreshAsync(77, Fake.Session(), null)).Finance;
        Assert.Null(f.Ledger);
        Assert.Equal("p3", f.Checkpoint!.Cursor);
        Assert.Equal(3, f.Checkpoint.Entries.Count);
        Assert.Null(QuotaCalculator.Compute(f, Now).RemainingDaily);
    }

    [Fact]
    public async Task Unknown_transaction_shape_fails_safely()
    {
        var fake = new FakeRoblox();
        fake.Pages[""] = ([new { amount = -5, created = Now.ToString("O"), transferRole = "Sender" }], null); // no id
        using var api = new RobloxApi(fake);
        var f = (await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null)).Finance;
        Assert.Null(f.Ledger);
        Assert.True(f.QuotaReadFailed);
        Assert.Contains(f.LastErrors, e => e.Contains("transação sem campo de ID"));
    }

    [Fact]
    public async Task Failed_balance_read_keeps_previous_value_and_time()
    {
        var fake = new FakeRoblox();
        using var api = new RobloxApi(fake);
        var prev = new AccountFinance { Robux = 42, RobuxAtUtc = Ago(2) };
        var handler = new FailingPath(fake, "/v1/user/currency");
        using var api2 = new RobloxApi(handler);
        var f = (await new FinanceRefresher(api2, () => Now).RefreshAsync(77, Fake.Session(), prev)).Finance;
        Assert.Equal(42, f.Robux);
        Assert.Equal(Ago(2), f.RobuxAtUtc);
        Assert.Contains(f.LastErrors, e => e.StartsWith("saldo"));
        Assert.Equal(CapacityState.ToVerify, QuotaCalculator.Compute(f, Now).State);
    }

    private sealed class FailingPath(HttpMessageHandler inner, string path) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            r.RequestUri!.AbsolutePath == path ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)) : base.SendAsync(r, ct);
    }

    [Fact]
    public async Task Rotated_cookie_is_used_for_the_following_reads_and_reported()
    {
        string fresh = Fake.Session("rotated");
        var fake = new FakeRoblox { RotateTo = fresh };
        using var api = new RobloxApi(fake);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session("old"), null);
        Assert.Equal(fresh, r.RotatedSession);
        Assert.Equal($".ROBLOSECURITY={fresh}", fake.Requests.Last().Headers.GetValues("Cookie").Single());
    }
}

public class EconomyParserTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    [Fact]
    public void Restriction_reads_current_value_and_null_means_none()
    {
        var r = EconomyParsers.Restriction(J("""{"robuxTransferLimits":{"currentValue":{"daily":500,"monthly":null},"options":[]}}"""));
        Assert.Equal(500, r!.Daily);
        Assert.Null(r.Monthly);
        Assert.Null(EconomyParsers.Restriction(J("""{"robuxTransferLimits":{"currentValue":null}}""")));
        Assert.Equal(0, EconomyParsers.Restriction(J("""{"userSettings":{"robuxTransferLimits":{"currentValue":{"daily":0,"monthly":0}}}}"""))!.Daily);
        Assert.Throws<FormatException>(() => EconomyParsers.Restriction(J("""{"other":1}""")));
    }

    [Fact]
    public void Plus_status_and_expiration()
    {
        var active = EconomyParsers.DecidePlus(EconomyParsers.PlusPage(J("""[{"productType":"RobloxPlus","status":"Active","expiration":"2026-12-01T00:00:00Z"}]""")).Items, Now);
        Assert.True(active.Active);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), active.ExpiresUtc);
        var expired = EconomyParsers.DecidePlus(EconomyParsers.PlusPage(J("""{"subscriptions":[{"productType":"RobloxPlus","status":"Active","expiration":"2026-11-01T00:00:00Z"}]}""")).Items, Now);
        Assert.False(expired.Active);
        var cancelledButPaid = EconomyParsers.DecidePlus(EconomyParsers.PlusPage(J("""{"data":[{"product":{"type":"RobloxPlus"},"status":"Cancelled","expiresAt":"2026-11-20T00:00:00Z"}]}""")).Items, Now);
        Assert.True(cancelledButPaid.Active);
        var none = EconomyParsers.DecidePlus(EconomyParsers.PlusPage(J("""{"subscriptions":[]}""")).Items, Now);
        Assert.False(none.Active);
    }

    [Fact]
    public void Caps_keep_per_transfer_separately_and_reject_empty()
    {
        var c = EconomyParsers.Caps(J("""{"dailyLimit":5000,"monthlyLimit":10000,"perTransferLimit":2000}"""));
        Assert.Equal((5000L, 10000L, 2000L), (c.Daily!.Value, c.Monthly!.Value, c.PerTransfer!.Value));
        Assert.Throws<FormatException>(() => EconomyParsers.Caps(J("""{"foo":1}""")));
    }

    [Fact]
    public void Transfer_page_variants()
    {
        var p = EconomyParsers.TransferPage(J("""{"nextPageCursor":"abc","data":[{"id":123,"created":"2026-11-16T10:00:00Z","amount":-50,"details":{"transferRole":"Sender"}}]}"""));
        var t = Assert.Single(p.Items);
        Assert.Equal("123", t.Ids["id"]);
        Assert.True(t.IsOutgoing);
        Assert.Equal(new PageCursor("cursor", "abc"), p.NextCursor);
        var ids = new Dictionary<string, string> { ["id"] = "x" };
        Assert.False(new TransferRecord(ids, Now, 50, "Sender").IsOutgoing);   // positive: not an outflow
        Assert.False(new TransferRecord(ids, Now, -50, "Receiver").IsOutgoing);
    }
}

public class AvailableTodayTests
{
    [Fact]
    public void Daily_remaining_never_exceeds_what_the_30_day_cap_leaves()
    {
        // @FonteAlfa: nothing sent today, 10,000 sent in 30 days with a 10,000 cap.
        var now = QuotaCalculatorTests.Now;
        var f = QuotaCalculatorTests.Fresh(94, 5_000, 10_000, (now.AddDays(-3), 10_000));
        var v = QuotaCalculator.Compute(f, now);
        Assert.Equal(5_000, v.RemainingDaily);   // the daily cap alone
        Assert.Equal(0, v.RemainingMonthly);
        Assert.Equal(0, v.AvailableToday);       // what is shown as "Restante hoje"
        Assert.True(v.TodayLimitedByMonth);
        Assert.Equal(0, v.Capacity);
    }

    [Fact]
    public void When_the_daily_cap_is_the_tighter_one_it_is_shown_as_is()
    {
        var now = QuotaCalculatorTests.Now;
        var v = QuotaCalculator.Compute(QuotaCalculatorTests.Fresh(10_000, 5_000, 50_000, (now.AddMinutes(-30), 1_000)), now);
        Assert.Equal(4_000, v.AvailableToday);
        Assert.False(v.TodayLimitedByMonth);
    }
}
