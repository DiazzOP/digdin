using System.Net;
using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>Serves the fictitious responses in Fixtures/ by endpoint; any file can be swapped per test.</summary>
internal sealed class FixtureRoblox : HttpMessageHandler
{
    public long UserId = 77;
    public string Currency = "currency.json";
    public string Totals = "transaction-totals.json";
    public string Subscriptions = "subscriptions-active.json";
    /// <summary>Extra subscription pages by cursor/token value (the first page is <see cref="Subscriptions"/>).</summary>
    public Dictionary<string, string> SubscriptionPages = new();
    public string Limits = "transfer-limit.json";
    public string Settings = "settings-none.json";
    public Dictionary<string, string> Pages = new()
    {
        [""] = "transactions-page1.json",
        ["fixture-cursor-p2"] = "transactions-page2.json",
    };
    public readonly List<Uri> Requested = [];
    /// <summary>Body of an HTTP 400 for the request WITH ?ProductType=… (null: answer normally).</summary>
    public string? SubscriptionsFilteredError;
    /// <summary>File for the request WITHOUT the ProductType filter (null: same as <see cref="Subscriptions"/>).</summary>
    public string? SubscriptionsUnfiltered;
    /// <summary>Body of an HTTP 400 for the request WITHOUT the filter.</summary>
    public string? SubscriptionsUnfilteredError;
    /// <summary>Who each Cookie header belongs to (0 = refused). Null: every session is <see cref="UserId"/>.</summary>
    public Func<string, long>? IdentityFor;

    /// <summary>The page key: value of "cursor" or "pageToken", whichever the request carried ("" = first page).</summary>
    private static string PageKey(Uri u)
    {
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        return q["cursor"] ?? q["pageToken"] ?? "";
    }

    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var u = req.RequestUri!;
        Requested.Add(u);
        string path = u.AbsolutePath;
        string? file = path switch
        {
            "/v1/users/authenticated" => null,
            "/v1/user/currency" => Currency,
            "/subscriptions/v2/user/subscriptions" => PageKey(u) is "" ? Subscriptions : SubscriptionPages.GetValueOrDefault(PageKey(u)),
            "/transfer/v1/robux-transfer/user-transfer-limit" => Limits,
            _ when path.EndsWith("/transaction-totals") => Totals,
            _ when path.StartsWith("/user-settings-api/") => Settings,
            _ when path.Contains("/transaction-records/") => Pages.GetValueOrDefault(PageKey(u)),
            _ => "",
        };
        if (path == "/v1/users/authenticated" && IdentityFor is not null)
        {
            string cookie = req.Headers.TryGetValues("Cookie", out var c) ? c.Single() : "";
            long who = IdentityFor(cookie);
            return Task.FromResult(who == 0
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Ok(JsonSerializer.Serialize(new { id = who, name = "Fixture_User", displayName = "Fixture" })));
        }
        if (path == "/subscriptions/v2/user/subscriptions")
        {
            bool filtered = u.Query.Contains("ProductType=", StringComparison.Ordinal);
            string? error = filtered ? SubscriptionsFilteredError : SubscriptionsUnfilteredError;
            if (error is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Read(error)) });
            if (!filtered && SubscriptionsUnfiltered is not null && PageKey(u) == "")
                return Task.FromResult(Ok(Read(SubscriptionsUnfiltered)));
        }
        HttpResponseMessage resp = path == "/v1/users/authenticated"
            ? Ok(JsonSerializer.Serialize(new { id = UserId, name = "Fixture_User", displayName = "Fixture" }))
            : string.IsNullOrEmpty(file) ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(Read(file));
        return Task.FromResult(resp);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

public class FixtureTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    private static async Task<(AccountFinance F, FinanceView V, FixtureRoblox H)> Run(Action<FixtureRoblox>? setup = null, AccountFinance? previous = null)
    {
        var h = new FixtureRoblox();
        setup?.Invoke(h);
        using var api = new RobloxApi(h);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), previous);
        return (r.Finance, QuotaCalculator.Compute(r.Finance, Now), h);
    }

    [Fact]
    public async Task Full_fixture_set_gives_case_1()
    {
        var (f, v, h) = await Run();
        Assert.Empty(f.FailedReads);
        Assert.Equal(1_693, v.Robux);
        Assert.Equal(120, v.PendingRobux);       // separate from the balance
        Assert.Equal(1_200, v.SentToday);        // the +900 received does not count
        Assert.Equal(9_947, v.Sent30Days);       // fake-tx-0000 (36 days ago) is outside; 0002 counted once
        Assert.Equal(3_800, v.RemainingDaily);
        Assert.Equal(53, v.RemainingMonthly);
        Assert.Equal(53, v.Capacity);
        Assert.Equal(2_000, v.PerTransferLimit);
        Assert.Equal(CapacityState.Estimated, v.State);
        Assert.DoesNotContain(h.Requested, u => u.Query.Contains("fixture-cursor-p3")); // window covered on page 2
    }

    [Fact]
    public async Task Daily_restriction_fixture_lowers_cap_to_500()
    {
        var (_, v, _) = await Run(h => h.Settings = "settings-daily-500.json");
        Assert.Equal(5_000, v.CapDaily);
        Assert.Equal(500, v.EffectiveDaily);
        Assert.Equal(0, v.RemainingDaily);       // 1,200 already sent today
        Assert.Equal(0, v.Capacity);
    }

    [Fact]
    public async Task Cancelled_plus_counts_as_active_until_expiration()
    {
        var (f, v, _) = await Run(h => h.Subscriptions = "subscriptions-cancelled.json");
        Assert.True(f.Plus!.Active);
        Assert.Equal(CapacityState.Estimated, v.State);

        var (f2, v2, _) = await Run(h => h.Subscriptions = "subscriptions-cancelled-expired.json");
        Assert.False(f2.Plus!.Active);
        Assert.Equal(CapacityState.ToVerify, v2.State);
    }

    [Theory]
    [InlineData("subscriptions-unknown-status.json")]
    [InlineData("subscriptions-cancelled-no-date.json")]
    public async Task Unknown_plus_format_is_to_verify_not_inactive(string file)
    {
        var (f, v, _) = await Run(h => h.Subscriptions = file);
        Assert.Null(f.Plus);                     // no guess stored
        Assert.Contains(FinanceField.Plus, f.FailedReads);
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task No_plus_subscription_is_inactive_and_to_verify()
    {
        var (f, v, _) = await Run(h => h.Subscriptions = "subscriptions-none.json");
        Assert.False(f.Plus!.Active);
        Assert.Contains("Sem Plus", v.ToVerifyReasons);
    }

    [Theory]
    [InlineData("transactions-unknown-role.json")]
    [InlineData("transactions-missing-amount.json")]
    public async Task Unclassifiable_transaction_makes_history_to_verify(string file)
    {
        var (f, v, _) = await Run(h => h.Pages = new() { [""] = file });
        Assert.Null(f.Ledger);
        Assert.Contains(FinanceField.History, f.FailedReads);
        Assert.Null(v.RemainingDaily);           // never treated as zero usage
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task Missing_pending_field_keeps_previous_value_but_is_to_verify()
    {
        var previous = new AccountFinance { PendingRobux = 50, PendingAtUtc = Now.AddMinutes(-5) };
        var (f, v, _) = await Run(h => h.Totals = "transaction-totals-missing-pending.json", previous);
        Assert.Equal(50, f.PendingRobux);
        Assert.Equal(Now.AddMinutes(-5), f.PendingAtUtc);
        Assert.Contains(FinanceField.Pending, v.FailedReads);
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task Balance_read_failing_now_is_to_verify_even_if_last_value_is_recent()
    {
        var previous = new AccountFinance { Robux = 1_000, RobuxAtUtc = Now.AddMinutes(-5) };
        var (f, v, _) = await Run(h => h.Currency = "", previous); // 404
        Assert.Equal(1_000, f.Robux);
        Assert.Contains(FinanceField.Robux, f.FailedReads);
        Assert.Equal(CapacityState.ToVerify, v.State);
        Assert.Contains("saldo a conferir: Roblox respondeu HTTP 404", v.ToVerifyReasons);
    }

    [Fact]
    public async Task Wrong_account_marks_every_field_to_verify()
    {
        var previous = QuotaCalculatorTests.Fresh(1_000, 5_000, 10_000);
        var (f, v, h) = await Run(h => h.UserId = 999, previous);
        Assert.Single(h.Requested);
        Assert.Equal(FinanceField.All.Order(), f.FailedReads.Order());
        Assert.Equal(CapacityState.ToVerify, v.State);
    }
}

/// <summary>Regressions from the comparison with the Limited Desk (account with Plus via "Blackbird"; 3,165 vs 400).</summary>
public class LimitedDeskComparisonTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static readonly DateTimeOffset PlusEnd = new(2026, 12, 14, 22, 24, 0, TimeSpan.FromHours(-3));

    private static async Task<(AccountFinance F, FinanceView V, FixtureRoblox H)> Run(Action<FixtureRoblox> setup, AccountFinance? previous = null)
    {
        var h = new FixtureRoblox();
        setup(h);
        using var api = new RobloxApi(h);
        var r = await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), previous);
        return (r.Finance, QuotaCalculator.Compute(r.Finance, Now), h);
    }

    private static void TokenHistory(FixtureRoblox h) => h.Pages = new()
    {
        [""] = "transactions-token-p1.json",
        ["fixture-txtoken-2"] = "transactions-token-p2.json",
        ["fixture-txtoken-3"] = "transactions-token-p3.json",
    };

    // ───────── Plus ─────────

    [Fact]
    public async Task Blackbird_on_page_two_is_active_until_expirationTimestampMs()
    {
        var (f, v, h) = await Run(h =>
        {
            h.Subscriptions = "subscriptions-blackbird-p1.json";
            h.SubscriptionPages["fixture-sub-p2"] = "subscriptions-blackbird-p2.json";
        });
        Assert.True(f.Plus!.Active);
        Assert.Equal(PlusEnd, f.Plus.ExpiresUtc);
        Assert.Equal("14/12 22:24", BrasiliaTime.ToBrasilia(f.Plus.ExpiresUtc!.Value).ToString("dd/MM HH:mm"));
        Assert.DoesNotContain(FinanceField.Plus, f.FailedReads);
        Assert.Contains(h.Requested, u => u.Query.Contains("cursor=fixture-sub-p2"));
        Assert.DoesNotContain(v.ToVerifyReasons, r => r is "Sem Plus" or "Plus expirado");
    }

    [Fact]
    public async Task PageToken_pagination_and_expiration_without_status_field()
    {
        var (f, _, h) = await Run(h =>
        {
            h.Subscriptions = "subscriptions-token-p1.json";
            h.SubscriptionPages["fixture-subtoken-2"] = "subscriptions-token-p2.json";
        });
        Assert.True(f.Plus!.Active);
        Assert.Equal(PlusEnd, f.Plus.ExpiresUtc);
        Assert.Contains(h.Requested, u => u.Query.Contains("pageToken=fixture-subtoken-2"));
    }

    [Fact]
    public void Unknown_status_value_is_to_verify_even_with_future_expiration()
    {
        Assert.Throws<FormatException>(() => EconomyParsers.DecidePlus([new PlusItem("Blackbird", "OnHold", PlusEnd)], Now));
    }

    [Fact]
    public async Task Plus_after_its_expiration_is_inactive()
    {
        var (f, _, _) = await Run(h =>
        {
            h.Subscriptions = "subscriptions-blackbird-p1.json";
            h.SubscriptionPages["fixture-sub-p2"] = "subscriptions-blackbird-p2.json";
        });
        var later = PlusEnd.AddMinutes(1);
        Assert.False(EconomyParsers.DecidePlus([new PlusItem("Blackbird", "Subscribed", PlusEnd)], later).Active);
        Assert.True(f.Plus!.Active);
    }

    [Fact]
    public async Task Failed_second_subscription_page_is_to_verify_not_inactive()
    {
        var (f, v, _) = await Run(h => h.Subscriptions = "subscriptions-blackbird-p1.json"); // page 2 → 404
        Assert.Null(f.Plus);
        Assert.Contains(FinanceField.Plus, f.FailedReads);
        Assert.DoesNotContain(v.ToVerifyReasons, r => r is "Sem Plus" or "Plus expirado");
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task Status_contradicting_a_future_expiration_is_to_verify()
    {
        var (f, _, _) = await Run(h => h.Subscriptions = "subscriptions-expired-but-future.json");
        Assert.Null(f.Plus);
        Assert.Contains(FinanceField.Plus, f.FailedReads);
    }

    [Fact]
    public async Task No_plus_entry_on_any_page_is_inactive()
    {
        var (f, _, _) = await Run(h => h.Subscriptions = "subscriptions-other-only.json");
        Assert.False(f.Plus!.Active);
        Assert.Empty(f.FailedReads);
    }

    // ───────── History ─────────

    [Fact]
    public async Task Reads_every_page_with_nextPageToken_and_sums_3165()
    {
        var (f, v, h) = await Run(TokenHistory);
        Assert.Equal(3_165, v.Sent30Days);     // 150 + 250 + 1,000 + 1,265 + 500; the 9,000 is 35 days old
        Assert.Equal(150, v.SentToday);        // the 250 was yesterday 17:00 in Brasília
        Assert.Contains(h.Requested, u => u.Query.Contains("pageToken=fixture-txtoken-2"));
        Assert.Contains(h.Requested, u => u.Query.Contains("pageToken=fixture-txtoken-3"));
        Assert.DoesNotContain(h.Requested, u => u.Query.Contains("fixture-txtoken-4"));
        var info = f.LastHistoryRead!;
        Assert.Equal(3, info.Pages);
        Assert.Equal(7, info.Records);
        Assert.Equal(6, info.Outgoing);        // includes the 35-day-old one, outside the window
        Assert.Equal(1, info.Incoming);
        Assert.Equal("leitura completa: cobriu 30 dias", info.Stop);
        Assert.True(info.FullRead);
    }

    [Fact]
    public async Task Ledger_from_the_old_parser_is_discarded_and_read_again()
    {
        // What 0.3.0 could have stored: a "complete" ledger built from page 1 only (400).
        var old = QuotaCalculatorTests.Fresh(10_000, 5_000, 10_000,
            (new DateTimeOffset(2026, 11, 16, 17, 0, 0, TimeSpan.Zero), 150), (new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero), 250));
        old.Ledger!.Version = 1;
        var before = QuotaCalculator.Compute(old, Now);
        Assert.Null(before.Sent30Days);        // never shown, even before the re-read
        Assert.Equal(CapacityState.ToVerify, before.State);

        var (f, v, _) = await Run(TokenHistory, old);
        Assert.Equal(TransferLedger.CurrentVersion, f.Ledger!.Version);
        Assert.True(f.LastHistoryRead!.FullRead);
        Assert.Equal(3_165, v.Sent30Days);
    }

    [Theory]
    [InlineData("transactions-ascending.json", "fora de ordem")]
    [InlineData("transactions-unknown-pagination.json", "campo continuationToken desconhecido")]
    [InlineData("transactions-amount-conflict.json", "amount e currency.amount diferentes")]
    public async Task Suspicious_history_is_to_verify(string file, string problem)
    {
        var (f, v, _) = await Run(h => h.Pages = new() { [""] = file, ["fixture-asc-2"] = "transactions-page2.json" });
        Assert.Null(f.Ledger);
        Assert.Contains(FinanceField.History, f.FailedReads);
        Assert.Null(v.Sent30Days);
        Assert.Contains(f.LastErrors, e => e.Contains(problem));
        Assert.Contains(problem, f.FailureReasons[FinanceField.History]);
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task Full_page_without_next_cursor_is_not_taken_as_the_end()
    {
        var items = Enumerable.Range(0, 100).Select(i => new
        {
            id = $"fake-full-{i:000}",
            created = Now.AddHours(-i).ToString("O"),
            transferRole = "Sender",
            currency = new { amount = -1, type = "Robux" },
        });
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        File.WriteAllText(Path.Combine(dir, "generated-full-page.json"), JsonSerializer.Serialize(new { nextPageCursor = (string?)null, data = items }));
        var (f, v, _) = await Run(h => h.Pages = new() { [""] = "generated-full-page.json" });
        Assert.Null(f.Ledger);
        Assert.Equal("erro: paginação não informada", f.LastHistoryRead!.Stop);
        Assert.Equal(CapacityState.ToVerify, v.State);
    }
}
