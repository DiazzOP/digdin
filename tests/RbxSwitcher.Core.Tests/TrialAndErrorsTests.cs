using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>
/// @altconta72 (0.3.6): account without a subscription → "Plus a conferir: Roblox respondeu HTTP 400".
/// Fictitious fixtures: the ProductType filter answers 400; the same endpoint without the filter answers a list.
/// </summary>
[Collection("Log")]
public class TrialAndErrorsTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;

    private static async Task<(AccountFinance F, FixtureRoblox H)> Refresh(Action<FixtureRoblox> setup)
    {
        var h = new FixtureRoblox();
        setup(h);
        using var api = new RobloxApi(h);
        return ((await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null)).Finance, h);
    }

    private static List<Uri> Subs(FixtureRoblox h) => h.Requested.Where(u => u.AbsolutePath.Contains("subscriptions")).ToList();

    [Fact]
    public async Task Http_400_on_the_filter_retries_without_it_and_a_valid_empty_list_is_sem_plus()
    {
        var (f, h) = await Refresh(x => { x.SubscriptionsFilteredError = "error-400-producttype.json"; x.SubscriptionsUnfiltered = "subscriptions-empty.json"; });
        var subs = Subs(h);
        Assert.Equal(2, subs.Count);
        Assert.Contains("ProductType=RobloxPlus", subs[0].Query);
        Assert.Equal("", subs[1].Query);
        Assert.Empty(f.FailureReasons);
        Assert.True(f.Plus!.NoSubscription);
        Assert.Contains("lido sem o filtro ProductType (o filtro respondeu HTTP 400)", f.Plus.Notes);
        var v = PlusStatus.Evaluate(f, Now);
        Assert.Equal("Sem Plus", v.Text);
        Assert.False(v.TrialAvailable);                       // not reported → never claimed
    }

    [Fact]
    public async Task Trial_available_only_when_roblox_says_so()
    {
        var (yes, _) = await Refresh(x => { x.SubscriptionsFilteredError = "error-400-producttype.json"; x.SubscriptionsUnfiltered = "subscriptions-empty-trial-eligible.json"; });
        Assert.True(yes.Plus!.NoSubscription);                // the RobloxPlus offer is not taken for a subscription
        Assert.True(yes.Plus.TrialEligible);
        Assert.True(PlusStatus.Evaluate(yes, Now).TrialAvailable);

        var (no, _) = await Refresh(x => x.Subscriptions = "subscriptions-empty-not-eligible.json");
        Assert.False(no.Plus!.TrialEligible);
        Assert.False(PlusStatus.Evaluate(no, Now).TrialAvailable);

        var (clash, _) = await Refresh(x => x.Subscriptions = "subscriptions-trial-contradiction.json");
        Assert.Contains("elegibilidade ao teste grátis contraditórios", clash.FailureReasons[FinanceField.Plus]);
        Assert.Equal("A conferir", PlusStatus.Evaluate(clash, Now).Text);
    }

    [Fact]
    public async Task Active_plus_is_never_offered_a_trial()
    {
        var (f, _) = await Refresh(x => x.Subscriptions = "subscriptions-renewal-zero.json");
        f.Plus!.TrialEligible = true;
        Assert.False(PlusStatus.Evaluate(f, Now).TrialAvailable);
    }

    [Fact]
    public async Task Both_requests_400_is_to_verify_with_roblox_reason_cleaned()
    {
        var (f, _) = await Refresh(x => { x.SubscriptionsFilteredError = "error-400-producttype.json"; x.SubscriptionsUnfilteredError = "error-400-grpc.json"; });
        string reason = f.FailureReasons[FinanceField.Plus];
        Assert.Equal("Roblox respondeu HTTP 400 (código 5: No subscriptions found)", reason);
        Assert.Equal("A conferir", PlusStatus.Evaluate(f, Now).Text);
    }

    [Fact]
    public void Error_reason_drops_ids_and_secrets()
    {
        string r = EconomyParsers.ErrorReason(JsonDocument.Parse(FixtureRoblox.Read("error-400-producttype.json")).RootElement)!;
        Assert.StartsWith("código 3: Invalid ProductType for user <n>", r);
        Assert.DoesNotContain("1234567890", r);
        Assert.DoesNotContain("WARNING", r);
        Assert.Null(EconomyParsers.ErrorReason(JsonDocument.Parse("""{"ok":true}""").RootElement));
    }

    [Fact]
    public async Task Diagnostic_shows_the_400_the_retry_and_the_trial_line()
    {
        var h = new FixtureRoblox { IdentityFor = _ => 77, SubscriptionsFilteredError = "error-400-producttype.json", SubscriptionsUnfiltered = "subscriptions-empty-trial-eligible.json" };
        using var api = new RobloxApi(h);
        var r = await PlusDiagnostics.RunAsync(api, 77, Fake.Session(), null, Now);
        string text = PlusDiagnostics.Format(r, "altconta72", Now, "0.3.7");
        Assert.Contains("Página 1: HTTP 400", text);
        Assert.Contains("— repetindo sem o filtro ProductType", text);
        Assert.Contains("ACEITA: Sem Plus", text);
        Assert.Contains("Teste grátis: disponível (informado pelo Roblox)", text);
        Assert.Contains("offers[].isTrialEligible:bool", text);
    }
}

public class ApiCallsTests
{
    [Fact]
    public void Records_path_names_status_and_shape_without_values()
    {
        var c = ApiCalls.From("get", "https://apis.roblox.com/subscriptions/v1/users/1234567/offers/9f8e7d6c5b4a3f2e?productType=RobloxPlus&cursor=abc",
            200, "application/json; charset=utf-8", """{"offers":[{"isTrialEligible":true,"priceCents":2990}]}""")!;
        Assert.Equal("/subscriptions/v1/users/<id>/offers/<id>", c.Path);
        Assert.Equal(["productType", "cursor"], c.QueryNames);
        Assert.Equal("(raiz):object, offers:array(1), offers[].isTrialEligible:bool, offers[].priceCents:number, offers[]:object", c.Shape);
        string text = ApiCalls.Format([c, c]);
        Assert.Contains("GET apis.roblox.com/subscriptions/v1/users/<id>/offers/<id>?productType=…&cursor=… → HTTP 200 (×2)", text);
        foreach (var value in new[] { "1234567", "9f8e7d6c5b4a3f2e", "RobloxPlus", "abc", "2990", "true" })
            Assert.DoesNotContain(value, text);
    }

    [Theory]
    [InlineData("https://evil.example/api", "application/json")]
    [InlineData("http://apis.roblox.com/x", "application/json")]
    [InlineData("https://www.roblox.com/home", "text/html")]
    public void Other_hosts_plain_http_and_non_json_are_ignored(string uri, string type) =>
        Assert.Null(ApiCalls.From("GET", uri, 200, type, "{}"));
}
