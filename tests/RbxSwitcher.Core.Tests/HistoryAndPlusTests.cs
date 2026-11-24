using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

[CollectionDefinition("Log", DisableParallelization = true)]
public sealed class LogCollection;

/// <summary>
/// Second comparison with the Limited Desk (altconta35, same evening): Limited Desk 9 outgoing = 3,165 and
/// Plus active until 14/12 22:24; RbxSwitcher 0.3.1 counted only the newest outgoing (400) and Plus failed.
/// Fixtures reproduce the suspected cause: the "id" field repeats across transfers, "idHash" is unique.
/// </summary>
[Collection("Log")]
public class HistoryAndPlusTests : IDisposable
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private readonly TempDir _dir = new();
    private readonly string _log;

    public HistoryAndPlusTests()
    {
        _log = _dir.File("app.log");
        Log.Init(_log);
    }

    public void Dispose() => _dir.Dispose();

    private static void CollidingHistory(FixtureRoblox h) => h.Pages = new()
    {
        [""] = "transactions-collide-p1.json",
        ["fixture-col-p2"] = "transactions-collide-p2.json",
    };

    private static async Task<(AccountFinance F, FixtureRoblox H)> Refresh(Action<FixtureRoblox> setup, AccountFinance? previous,
                                                                           DateTimeOffset at, bool full = false)
    {
        var h = new FixtureRoblox();
        setup(h);
        using var api = new RobloxApi(h);
        var r = await new FinanceRefresher(api, () => at).RefreshAsync(77, Fake.Session(), previous, fullHistory: full);
        return (r.Finance, h);
    }

    // ───────────────────────── history ─────────────────────────

    [Fact]
    public async Task Full_sync_ignores_saved_history_and_cursor_and_finds_the_nine_outgoing()
    {
        // What 0.3.1 left behind: a "complete" history with only the 400, plus a paused cursor.
        var stale = QuotaCalculatorTests.Fresh(10_000, 5_000, 10_000, (new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero), 400));
        stale.Checkpoint = new LedgerCheckpoint { AnchorUtc = Now.AddMinutes(-10), Cursor = "stale-cursor", StartedFromTop = true, Version = TransferLedger.CurrentVersion };

        var (f, h) = await Refresh(CollidingHistory, stale, Now, full: true);

        var history = h.Requested.Where(u => u.AbsolutePath.Contains("transaction-records")).ToList();
        Assert.DoesNotContain(history[0].Query, "cursor=");         // starts at the newest page
        Assert.DoesNotContain(history, u => u.Query.Contains("stale-cursor"));
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Equal(3_165, v.Sent30Days);
        Assert.Equal(400, v.SentToday);
        Assert.Equal(9, f.Ledger!.Entries.Count(e => e.CreatedUtc > Now - QuotaCalculator.Window));
        Assert.Equal("idHash", f.Ledger.IdField);                   // "id" repeats: it is not the transaction id
        var info = f.LastHistoryRead!;
        Assert.True(info.FullRead);
        Assert.Equal(1, info.Duplicates);                           // hash-a06 listed on both pages, counted once
        Assert.Equal(10, info.Outgoing);                            // 9 in the window + 1 older than 30 days
        Assert.Equal("leitura completa: cobriu 30 dias", info.Stop);
        Assert.Equal(CapacityState.Estimated, v.State);
        Assert.Equal(6_835, v.RemainingMonthly);
    }

    [Fact]
    public async Task Next_incremental_read_is_saved_history_plus_new_records_deduplicated_by_id()
    {
        var (full, _) = await Refresh(CollidingHistory, null, Now, full: true);
        var later = Now.AddHours(2);

        var (f, h) = await Refresh(x => x.Pages = new() { [""] = "transactions-incremental-p1.json" }, full, later);

        var history = h.Requested.Where(u => u.AbsolutePath.Contains("transaction-records")).ToList();
        Assert.Single(history);                                     // reached the saved history on page 1
        Assert.DoesNotContain(history[0].Query, "cursor=");
        Assert.False(f.LastHistoryRead!.FullRead);
        Assert.Equal("leitura incremental: alcançou o histórico salvo", f.LastHistoryRead.Stop);
        Assert.Equal(2, f.LastHistoryRead.Duplicates);              // hash-a01 and hash-a03 were already saved
        var v = QuotaCalculator.Compute(f, later);
        Assert.Equal(3_265, v.Sent30Days);                          // 3,165 saved + 100 new
        Assert.Equal(10, f.Ledger!.Entries.Count(e => e.CreatedUtc > later - QuotaCalculator.Window));
        Assert.Equal(f.Ledger.Entries.Count, f.Ledger.Entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task Saved_id_coming_back_with_different_amount_fails_and_keeps_saved_history()
    {
        var (full, _) = await Refresh(CollidingHistory, null, Now, full: true);
        var later = Now.AddHours(2);
        var (f, _) = await Refresh(x => x.Pages = new() { [""] = "transactions-incremental-mismatch.json" }, full, later);
        Assert.Equal(full.Ledger!.CompleteAsOfUtc, f.Ledger!.CompleteAsOfUtc); // saved complete history untouched
        Assert.Null(f.Checkpoint);                                            // a broken id is not resumed
        Assert.Contains("repetido com dados diferentes", f.FailureReasons[FinanceField.History]);
        Assert.Equal(CapacityState.ToVerify, QuotaCalculator.Compute(f, later).State);
    }

    [Fact]
    public async Task Repeating_id_without_another_id_field_fails_instead_of_counting_only_400()
    {
        var (f, _) = await Refresh(x => x.Pages = new() { [""] = "transactions-only-id-p1.json" }, null, Now, full: true);
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Null(f.Ledger);
        Assert.Null(v.Sent30Days);                                  // never 400
        Assert.Contains("nenhum campo de ID único", f.FailureReasons[FinanceField.History]);
        Assert.Equal(CapacityState.ToVerify, v.State);
    }

    [Fact]
    public async Task Checkpoint_that_did_not_start_at_the_newest_page_is_not_resumed()
    {
        // An old page reached from a cursor of unknown origin proves nothing about newer records.
        var prev = new AccountFinance
        {
            Checkpoint = new LedgerCheckpoint { AnchorUtc = Now.AddMinutes(-5), Cursor = "fixture-col-p2", StartedFromTop = false, Version = TransferLedger.CurrentVersion },
        };
        var (f, h) = await Refresh(CollidingHistory, prev, Now);
        var first = h.Requested.First(u => u.AbsolutePath.Contains("transaction-records"));
        Assert.DoesNotContain("cursor=", first.Query);
        Assert.Equal(3_165, QuotaCalculator.Compute(f, Now).Sent30Days);
    }

    [Fact]
    public async Task Paused_full_read_resumes_from_its_own_cursor_and_completes()
    {
        var h0 = new FixtureRoblox();
        CollidingHistory(h0);
        using var api = new RobloxApi(h0);
        var refresher = new FinanceRefresher(api, () => Now) { MaxHistoryPages = 1 };
        var paused = (await refresher.RefreshAsync(77, Fake.Session(), null, fullHistory: true)).Finance;
        Assert.Null(paused.Ledger);
        Assert.Equal("fixture-col-p2", paused.Checkpoint!.Cursor);
        Assert.True(paused.Checkpoint.StartedFromTop);

        var done = (await refresher.RefreshAsync(77, Fake.Session(), paused)).Finance;
        Assert.Equal(3_165, QuotaCalculator.Compute(done, Now).Sent30Days);
        Assert.Null(done.Checkpoint);
    }

    // ───────────────────────── Plus ─────────────────────────

    [Fact]
    public async Task Plus_read_logs_only_http_status_and_field_names_and_types()
    {
        await Refresh(x =>
        {
            x.Subscriptions = "subscriptions-blackbird-p1.json";
            x.SubscriptionPages["fixture-sub-p2"] = "subscriptions-blackbird-p2.json";
        }, null, Now);
        string log = File.ReadAllText(_log);
        Assert.Contains("[assinaturas p1] HTTP 200; campos:", log);
        Assert.Contains("[assinaturas p2] HTTP 200; campos:", log);
        Assert.Contains("subscriptions[].productType:string", log);
        Assert.Contains("subscriptions[].expirationTimestampMs:number", log);
        Assert.Contains("nextPageCursor:string", log);
        // no values: product names, ids, timestamps, cursors
        foreach (var value in new[] { "Blackbird", "RobloxPlus", "sub-fake", "1797297840000", "fixture-sub-p2", "Subscribed" })
            Assert.DoesNotContain(value, log);
    }

    [Fact]
    public async Task Failed_plus_read_logs_status_and_shows_only_plus_a_conferir_with_reason()
    {
        // Last valid read said "inactive"; this one fails. Never show "inativa" from the old read.
        var prev = QuotaCalculatorTests.Fresh(1_000, 5_000, 10_000);
        prev.Plus = new PlusInfo { Active = false, Status = "Sem assinatura" };
        var (f, _) = await Refresh(x => x.Subscriptions = "subscriptions-no-list.json", prev, Now);
        var v = QuotaCalculator.Compute(f, Now);
        Assert.Contains("Plus a conferir: assinaturas: lista de assinaturas não encontrada", v.ToVerifyReasons);
        Assert.DoesNotContain(v.ToVerifyReasons, r => r is "Sem Plus" or "Plus expirado");
        Assert.Equal("A conferir", PlusStatus.Evaluate(f, Now).Text);
        Assert.Equal(CapacityState.ToVerify, v.State);
        string log = File.ReadAllText(_log);
        Assert.Contains("[assinaturas p1] HTTP 200; campos: (raiz):object, result.count:number, result:object", log);
        Assert.Contains("[assinaturas p1] leitura recusada: assinaturas: lista de assinaturas não encontrada", log);

        var (f404, _) = await Refresh(x => x.Subscriptions = "", prev, Now);
        Assert.Contains("Plus a conferir: Roblox respondeu HTTP 404", QuotaCalculator.Compute(f404, Now).ToVerifyReasons);
        Assert.Contains("[assinaturas p1] HTTP 404", File.ReadAllText(_log));
    }

    [Fact]
    public async Task Plus_is_found_under_an_unknown_list_name()
    {
        var (f, _) = await Refresh(x => x.Subscriptions = "subscriptions-unknown-list-name.json", null, Now);
        Assert.True(f.Plus!.Active);
        Assert.Equal(new DateTimeOffset(2026, 12, 14, 22, 24, 0, TimeSpan.FromHours(-3)), f.Plus.ExpiresUtc);
    }

    [Fact]
    public void Shape_never_contains_values_or_data_like_keys()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"a":"secret-value","n":12345,"byId":{"98765":{"x":true}},"list":[{"k":null}]}""");
        string shape = JsonShape.Describe(doc.RootElement);
        Assert.DoesNotContain("secret-value", shape);
        Assert.DoesNotContain("12345", shape);
        Assert.DoesNotContain("98765", shape);
        Assert.Contains("byId.<chave>.x:bool", shape);
        Assert.Contains("list[].k:null", shape);
    }
}
