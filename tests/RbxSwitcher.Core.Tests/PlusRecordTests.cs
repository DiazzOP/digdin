using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>
/// altconta35 (0.3.4): "assinatura: sem status e sem expirationTimestampMs" — the parser found the Plus product
/// in a nested object and only looked there for the dates. Fixtures are fictitious shapes of that case.
/// </summary>
[Collection("Log")]
public class PlusRecordTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static readonly DateTimeOffset Expires = new(2026, 12, 14, 22, 24, 0, TimeSpan.FromHours(-3));

    private static async Task<AccountFinance> Refresh(string file)
    {
        var h = new FixtureRoblox { Subscriptions = file };
        using var api = new RobloxApi(h);
        return (await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null)).Finance;
    }

    [Fact]
    public async Task Product_in_a_nested_object_reads_dates_from_the_subscription_record()
    {
        var f = await Refresh("subscriptions-product-nested.json");
        Assert.Empty(f.FailureReasons);
        Assert.Equal(Expires, f.Plus!.ExpiresUtc);
        var v = PlusStatus.Evaluate(f, Now);
        Assert.Equal("Ativo", v.Text);
        Assert.False(v.Expiring);                      // 28 days left
    }

    [Fact]
    public async Task Status_and_dates_nested_one_level_below_the_record_are_found()
    {
        var f = await Refresh("subscriptions-dates-nested.json");
        Assert.True(f.Plus!.Active);
        Assert.Equal("Active", f.Plus.Status);
        Assert.Equal(Expires, f.Plus.ExpiresUtc);
    }

    [Fact]
    public async Task Card_expiry_is_never_taken_for_the_plus_validity()
    {
        var f = await Refresh("subscriptions-only-card-date.json");
        Assert.Null(f.Plus);
        Assert.Equal("assinatura: sem status e sem validade (expirationTimestampMs ausente ou vazio)", f.FailureReasons[FinanceField.Plus]);
        Assert.Equal("A conferir", PlusStatus.Evaluate(f, Now).Text);
    }

    [Fact]
    public async Task Same_field_with_two_different_values_is_refused()
    {
        var f = await Refresh("subscriptions-conflicting-dates.json");
        Assert.Contains("campo expirationTimestampMs aparece mais de uma vez com valores diferentes", f.FailureReasons[FinanceField.Plus]);
    }

    [Fact]
    public async Task Diagnostic_names_the_record_and_the_field_each_value_came_from()
    {
        var h = new FixtureRoblox { Subscriptions = "subscriptions-product-nested.json", IdentityFor = _ => 77 };
        using var api = new RobloxApi(h);
        var r = await PlusDiagnostics.RunAsync(api, 77, Fake.Session(), null, Now);
        string text = PlusDiagnostics.Format(r, "altconta35", Now, "0.3.5");
        Assert.Contains("Plus: registro em subscriptions[]; campos usados: validade ← subscriptions[].expirationTimestampMs", text);
        Assert.Contains("ACEITA: Ativo; validade 14/12/2026 22:24", text);
        Assert.Contains("paymentMethod.expiration:string", text);   // the field name is listed…
        Assert.DoesNotContain("08/35", text);                       // …never its value
        Assert.DoesNotContain("Blackbird", text);
    }
}
