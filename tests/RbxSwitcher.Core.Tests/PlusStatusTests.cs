using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

public class PlusStatusTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now; // 16/11 15:00 Brasília
    private static readonly DateTimeOffset Expires = new(2026, 12, 14, 22, 24, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset Renews = new(2026, 12, 11, 15, 0, 0, TimeSpan.Zero);

    private static AccountFinance With(PlusInfo p) => new() { Plus = p, PlusAtUtc = Now };

    [Fact]
    public void Active_with_confirmed_renewal_like_the_official_page()
    {
        var v = PlusStatus.Evaluate(With(new PlusInfo { Active = true, ExpiresUtc = Expires, RenewsUtc = Renews, WillRenew = true }), Now);
        Assert.Equal("Ativo", v.Text);
        Assert.False(v.Expiring);
        // 5 days before the end, renewal confirmed: still no alert
        Assert.False(PlusStatus.Evaluate(With(v.Info!), Expires.AddDays(-5)).Expiring);
    }

    [Fact]
    public void Expiring_only_within_7_days_and_without_confirmed_renewal()
    {
        var noRenewal = new PlusInfo { Active = true, ExpiresUtc = Expires, WillRenew = null };
        Assert.False(PlusStatus.Evaluate(With(noRenewal), Expires.AddDays(-8)).Expiring);
        Assert.True(PlusStatus.Evaluate(With(noRenewal), Expires.AddDays(-7)).Expiring);
        var cancelled = new PlusInfo { Active = true, Status = "Cancelled", ExpiresUtc = Expires, WillRenew = false };
        var v = PlusStatus.Evaluate(With(cancelled), Expires.AddDays(-2));
        Assert.Equal("Ativo", v.Text);
        Assert.True(v.Expiring);
    }

    [Fact]
    public void Trial_expired_none_and_to_verify()
    {
        Assert.Equal("Trial", PlusStatus.Evaluate(With(new PlusInfo { Active = true, Trial = true, ExpiresUtc = Expires }), Now).Text);
        Assert.Equal("Expirado", PlusStatus.Evaluate(With(new PlusInfo { Active = false, ExpiresUtc = Now.AddDays(-1) }), Now).Text);
        Assert.Equal("Sem Plus", PlusStatus.Evaluate(With(new PlusInfo { NoSubscription = true }), Now).Text);

        var failed = With(new PlusInfo { Active = false, NoSubscription = true }); // an old valid "Sem Plus"…
        failed.FailedReads = [FinanceField.Plus];                                  // …and this attempt failed
        failed.FailureReasons[FinanceField.Plus] = "assinatura: campo status não é texto";
        var v = PlusStatus.Evaluate(failed, Now);
        Assert.Equal("A conferir", v.Text);
        Assert.Equal("assinatura: campo status não é texto", v.Reason);
        Assert.Equal("A conferir", PlusStatus.Evaluate(null, Now).Text);
    }

    [Fact]
    public void Validity_passed_since_the_read()
    {
        var renewing = new PlusInfo { Active = true, ExpiresUtc = Expires, WillRenew = true };
        Assert.Equal("A conferir", PlusStatus.Evaluate(With(renewing), Expires.AddMinutes(1)).Text); // renewed? only a new read can say
        var notRenewing = new PlusInfo { Active = true, ExpiresUtc = Expires, WillRenew = false };
        Assert.Equal("Expirado", PlusStatus.Evaluate(With(notRenewing), Expires.AddMinutes(1)).Text);
    }

    [Theory]
    [InlineData("""{"productType":"Blackbird","status":"Active","expirationTimestampMs":1797297840000,"autoRenew":true,"cancelAtPeriodEnd":true}""", "campos de renovação contraditórios")]
    [InlineData("""{"productType":"Blackbird","status":"Cancelled","expirationTimestampMs":1797297840000,"willRenew":true}""", "status cancelado e campo de renovação ativo")]
    [InlineData("""{"productType":"Blackbird","status":"Active","expirationTimestampMs":1797297840000,"isTrial":"yes"}""", "campo isTrial não é verdadeiro/falso")]
    [InlineData("""{"productType":"Blackbird","status":"Active","renewalDate":12345}""", "campo renewalDate não é data em texto")]
    public void Auxiliary_field_problems_become_notes_and_never_block_the_status(string item, string note)
    {
        var json = System.Text.Json.JsonDocument.Parse("{\"subscriptions\":[" + item + "]}").RootElement;
        var info = EconomyParsers.DecidePlus(EconomyParsers.PlusPage(json).Items, Now);
        Assert.True(info.Active);
        Assert.Contains(info.Notes, n => n.Contains(note));
        Assert.NotEqual(true, info.WillRenew);   // a broken renewal signal is never read as "renews"
        Assert.False(info.Trial);
    }
}

[Collection("Log")]
public class PlusDiagnosticsTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static readonly string Vault = Fake.Session("vault"), Browser = Fake.Session("browser");

    private static async Task<(PlusDiagnosticReport R, string Text, FixtureRoblox H)> Run(Action<FixtureRoblox> setup, string? browser = null)
    {
        var h = new FixtureRoblox { IdentityFor = _ => 77 };
        setup(h);
        using var api = new RobloxApi(h);
        var r = await PlusDiagnostics.RunAsync(api, 77, Vault, browser, Now);
        return (r, PlusDiagnostics.Format(r, "altconta35", Now, "0.3.3"), h);
    }

    [Fact]
    public async Task Official_page_shape_is_accepted_with_validity_and_renewal()
    {
        var (r, text, _) = await Run(h => h.Subscriptions = "subscriptions-official-renewing.json", Browser);
        Assert.Null(r.Rejected);
        Assert.Equal(PlusDiagnostics.BrowserSource, r.SessionUsed);
        Assert.True(r.Parsed!.Active);
        Assert.True(r.Parsed.WillRenew);
        Assert.Contains("sessão salva no cofre: HTTP 200, conta confere", text);
        Assert.Contains("sessão do navegador do app: HTTP 200, conta confere", text);
        Assert.Contains("ACEITA: Ativo; validade 14/12/2026 22:24; renovação 11/12/2026 12:00; renovação confirmada: sim", text);
        Assert.Contains("subscriptions[].renewalTimestampMs:number", text);
        Assert.Contains("subscriptions[].paymentMethod.last4:string", text);   // field name only
        foreach (var value in new[] { "0000", "Card", "Blackbird", "Active", "sub-fake", "1797297840000", "WARNING", Vault[^12..], Browser[^12..] })
            Assert.DoesNotContain(value, text);
    }

    [Fact]
    public async Task Rejected_field_is_named_and_plus_stays_to_verify()
    {
        var (r, text, _) = await Run(h => h.Subscriptions = "subscriptions-numeric-status.json");
        Assert.Equal("assinatura: campo status não é texto", r.Rejected);
        Assert.Contains("Recusado nesta página: assinatura: campo status não é texto", text);
        Assert.Contains("RECUSADA: assinatura: campo status não é texto → Plus fica \"A conferir\"", text);
        Assert.Contains("subscriptions[].status:number", text);
        Assert.Contains("aba da conta não está aberta", text);
    }

    [Fact]
    public async Task Missing_list_and_http_errors_are_reported_with_status()
    {
        var (_, noList, _) = await Run(h => h.Subscriptions = "subscriptions-no-list.json");
        Assert.Contains("RECUSADA: assinaturas: lista de assinaturas não encontrada", noList);
        Assert.Contains("Campos: (raiz):object, result.count:number, result:object", noList);
        var (_, http, _) = await Run(h => h.Subscriptions = "");
        Assert.Contains("Página 1: HTTP 404", http);
    }

    [Fact]
    public async Task Browser_session_of_another_account_is_flagged_and_not_used()
    {
        var h = new FixtureRoblox { Subscriptions = "subscriptions-official-renewing.json" };
        h.IdentityFor = cookie => cookie.Contains(Browser) ? 999 : 77;
        using var api = new RobloxApi(h);
        var r = await PlusDiagnostics.RunAsync(api, 77, Vault, Browser, Now);
        string text = PlusDiagnostics.Format(r, "altconta35", Now, "0.3.3");
        Assert.Contains("sessão do navegador do app: HTTP 200, pertence a OUTRA conta", text);
        Assert.Equal(PlusDiagnostics.VaultSource, r.SessionUsed);
    }

    [Fact]
    public async Task No_confirmed_session_means_nothing_is_queried()
    {
        var h = new FixtureRoblox { IdentityFor = _ => 0 };
        using var api = new RobloxApi(h);
        var r = await PlusDiagnostics.RunAsync(api, 77, Vault, Browser, Now);
        Assert.DoesNotContain(h.Requested, u => u.AbsolutePath.Contains("subscriptions"));
        Assert.Contains("nenhuma sessão confirmada", r.Rejected);
    }

    [Fact]
    public async Task Trial_is_shown_only_when_roblox_says_so()
    {
        var (r, _, _) = await Run(h => h.Subscriptions = "subscriptions-trial.json");
        Assert.True(r.Parsed!.Trial);
        var (r2, _, _) = await Run(h => h.Subscriptions = "subscriptions-official-renewing.json");
        Assert.False(r2.Parsed!.Trial);
    }
}
