using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>
/// altconta35 (0.3.5): "assinatura: campo nextRenewalTimestampMs inválido" — Roblox sent the renewal timestamp as 0
/// ("not set"), and an auxiliary field blocked the whole subscription status.
/// </summary>
[Collection("Log")]
public class PlusAuxiliaryTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static readonly DateTimeOffset Expires = new(2026, 12, 14, 22, 24, 0, TimeSpan.FromHours(-3));

    private static async Task<AccountFinance> Refresh(string file)
    {
        var h = new FixtureRoblox { Subscriptions = file + ".json" };
        using var api = new RobloxApi(h);
        return (await new FinanceRefresher(api, () => Now).RefreshAsync(77, Fake.Session(), null)).Finance;
    }

    [Fact]
    public async Task Renewal_timestamp_zero_is_not_reported_and_status_is_active()
    {
        var f = await Refresh("subscriptions-renewal-zero");
        Assert.Empty(f.FailureReasons);
        Assert.Equal(Expires, f.Plus!.ExpiresUtc);
        Assert.Null(f.Plus.RenewsUtc);
        Assert.Null(f.Plus.WillRenew);
        Assert.Contains("nextRenewalTimestampMs veio vazio (0): renovação não informada", f.Plus.Notes);
        var v = PlusStatus.Evaluate(f, Now);
        Assert.Equal("Ativo", v.Text);
        Assert.False(v.Expiring);                                   // 28 days left
        Assert.True(PlusStatus.Evaluate(f, Expires.AddDays(-6)).Expiring); // renewal not confirmed → alert in the last 7 days
        // With fresh quota data, an Ativo read like this one no longer holds "Pode enviar agora" at "a conferir".
        var withQuota = Merge(QuotaCalculatorTests.Fresh(602, 5_000, 10_000), f);
        Assert.Equal(CapacityState.Estimated, QuotaCalculator.Compute(withQuota, Now).State);
    }

    private static AccountFinance Merge(AccountFinance quota, AccountFinance plus)
    {
        quota.Plus = plus.Plus;
        quota.PlusAtUtc = plus.PlusAtUtc;
        quota.FailedReads = plus.FailedReads;
        quota.FailureReasons = plus.FailureReasons;
        return quota;
    }

    [Fact]
    public async Task Negative_renewal_is_a_note_not_a_refusal()
    {
        var f = await Refresh("subscriptions-renewal-negative");
        Assert.True(f.Plus!.Active);
        Assert.Contains(f.Plus.Notes, n => n.Contains("campo nextRenewalTimestampMs negativo"));
    }

    [Fact]
    public async Task Validity_zero_with_active_status_is_active_without_date()
    {
        var f = await Refresh("subscriptions-expiration-zero-active");
        Assert.True(f.Plus!.Active);
        Assert.Null(f.Plus.ExpiresUtc);
        Assert.Contains("expirationTimestampMs veio vazio (0)", f.Plus.Notes);
        Assert.False(PlusStatus.Evaluate(f, Now).Expiring);          // no date: no countdown
    }

    [Theory]
    [InlineData("subscriptions-expiration-zero-nostatus", "sem status e sem validade")]
    [InlineData("subscriptions-expiration-negative", "campo expirationTimestampMs negativo")]
    public async Task Essential_validity_problems_still_make_plus_to_verify(string file, string reason)
    {
        var f = await Refresh(file);
        Assert.Contains(reason, f.FailureReasons[FinanceField.Plus]);
        Assert.Equal("A conferir", PlusStatus.Evaluate(f, Now).Text);
    }

    [Fact]
    public async Task Every_read_logs_where_each_value_came_from_and_the_notes()
    {
        using var dir = new TempDir();
        string log = dir.File("app.log");
        RbxSwitcher.Core.Logging.Log.Init(log);
        await Refresh("subscriptions-renewal-zero");
        string text = File.ReadAllText(log);
        Assert.Contains("[assinaturas] registro em subscriptions[]; campos usados: status ← subscriptions[].status, validade ← subscriptions[].expirationTimestampMs; " +
                        "observações: nextRenewalTimestampMs veio vazio (0): renovação não informada", text);
        Assert.DoesNotContain("1797297840000", text);
        Assert.DoesNotContain("Blackbird", text);
    }
}
