using System.Text.Json;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>
/// @altconta72 (0.3.7): Sem Plus, but roblox.com/plus offers "Try it for free" — the subscriptions API does not say so.
/// The app now reads the yes/no from the official page's own responses. Bodies below are fictitious.
/// </summary>
public class PlusPageTrialTests
{
    private static readonly DateTimeOffset Now = QuotaCalculatorTests.Now;
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Theory]
    [InlineData("""{"offers":[{"productType":"RobloxPlus","isFreeTrialEligible":true,"trialDurationDays":7}]}""", true)]
    [InlineData("""{"eligibility":{"canStartTrial":false}}""", false)]
    [InlineData("""{"canStartTrial":true,"offers":[{"freeTrialEligible":false}]}""", null)]          // contradictory
    [InlineData("""{"priceCents":90000,"currency":"IDR"}""", null)]                                // nothing about trial
    [InlineData("""{"paymentMethods":[{"isTrialEligible":true}]}""", null)]                        // payment data never read
    public void Eligibility_is_read_from_any_official_response(string body, bool? expected) =>
        Assert.Equal(expected, EconomyParsers.TrialEligibleIn(J(body)));

    [Fact]
    public void Recorded_call_carries_only_the_yes_no()
    {
        var c = ApiCalls.From("GET", "https://apis.roblox.com/subscriptions/v1/offers?productType=RobloxPlus", 200, "application/json",
            """{"offers":[{"productType":"RobloxPlus","isFreeTrialEligible":true,"trialEndsAt":"2026-11-23T00:00:00Z","price":"IDR 90,000"}]}""")!;
        Assert.True(c.TrialEligible);
        string text = ApiCalls.Format([c]);
        Assert.Contains("→ HTTP 200 · teste grátis: disponível", text);
        Assert.DoesNotContain("90,000", text);
        Assert.DoesNotContain("2026-11-23", text);
        Assert.Null(ApiCalls.From("GET", "https://apis.roblox.com/x", 403, "application/json", """{"isFreeTrialEligible":true}""")!.TrialEligible);
    }

    private static AccountFinance SemPlus(bool? apiSays = null) => new()
    {
        Plus = new PlusInfo { NoSubscription = true, TrialEligible = apiSays },
        PlusAtUtc = Now,
    };

    [Fact]
    public void Offer_seen_on_the_official_page_marks_trial_available_for_a_day()
    {
        var f = SemPlus();
        Assert.False(PlusStatus.Evaluate(f, Now).TrialAvailable);
        f.TrialEligibleFromPage = true;
        f.TrialFromPageUtc = Now.AddMinutes(-2);
        var v = PlusStatus.Evaluate(f, Now);
        Assert.Equal("Sem Plus", v.Text);
        Assert.True(v.TrialAvailable);
        Assert.Equal("página oficial", v.TrialOffer);
        Assert.False(PlusStatus.Evaluate(f, Now.AddHours(25)).TrialAvailable);   // stale: open the page again
    }

    [Fact]
    public void Api_answer_wins_over_the_page_and_active_plus_never_gets_an_offer()
    {
        var no = SemPlus(apiSays: false);
        no.TrialEligibleFromPage = true;
        no.TrialFromPageUtc = Now;
        Assert.False(PlusStatus.Evaluate(no, Now).TrialAvailable);
        Assert.Equal("api", PlusStatus.Evaluate(SemPlus(apiSays: true), Now).TrialOffer);

        var active = new AccountFinance { Plus = new PlusInfo { Active = true, ExpiresUtc = Now.AddDays(20) }, PlusAtUtc = Now,
                                          TrialEligibleFromPage = true, TrialFromPageUtc = Now };
        Assert.False(PlusStatus.Evaluate(active, Now).TrialAvailable);
    }

    [Fact]
    public void Failed_plus_read_stays_to_verify_even_with_a_page_offer()
    {
        var f = SemPlus();
        f.FailedReads = [FinanceField.Plus];
        f.FailureReasons[FinanceField.Plus] = "Roblox respondeu HTTP 400";
        f.TrialEligibleFromPage = true;
        f.TrialFromPageUtc = Now;
        var v = PlusStatus.Evaluate(f, Now);
        Assert.Equal("A conferir", v.Text);
        Assert.False(v.TrialAvailable);
    }
}

public class TrialNameHeuristicTests
{
    [Theory]
    [InlineData("""{"freeTrialAvailable":true}""", true)]
    [InlineData("""{"plan":{"trialOfferAvailable":true}}""", true)]
    [InlineData("""{"canRedeemFreeTrial":false}""", false)]
    [InlineData("""{"isEligibleForPlusFreeTrial":true}""", true)]
    [InlineData("""{"showTrialBanner":true}""", true)]
    [InlineData("""{"isTrial":false,"inTrialPeriod":false}""", null)]                 // being in a trial ≠ eligible
    [InlineData("""{"paymentMethod":{"freeTrialAvailable":true}}""", null)]           // payment data never read
    [InlineData("""{"freeTrialAvailable":true,"trialOfferAvailable":false}""", null)] // disagreement
    public void Eligibility_like_names_are_recognized(string body, bool? expected) =>
        Assert.Equal(expected, RbxSwitcher.Core.Roblox.EconomyParsers.TrialEligibleIn(System.Text.Json.JsonDocument.Parse(body).RootElement));
}
