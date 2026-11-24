namespace RbxSwitcher.Core.Finance;

public enum PlusLabel { ToVerify, None, Expired, Trial, Active }

/// <param name="Expiring">Alert: active, at most 7 days left and no renewal confirmed by Roblox.</param>
/// <param name="Reason">Why it is "A conferir" (null otherwise).</param>
/// <param name="TrialOffer">Where Roblox said a free trial can be started: "api", "página oficial" or null.</param>
public sealed record PlusStatusView(PlusLabel Label, bool Expiring, string? Reason, PlusInfo? Info, DateTimeOffset? ReadUtc,
                                    string? TrialOffer = null)
{
    /// <summary>No Plus now (Sem Plus / Expirado) and Roblox said a free trial can be started.</summary>
    public bool TrialAvailable => (Label is PlusLabel.None or PlusLabel.Expired) && TrialOffer is not null;

    public string Text => Label switch
    {
        PlusLabel.Active => "Ativo",
        PlusLabel.Trial => "Trial",
        PlusLabel.Expired => "Expirado",
        PlusLabel.None => "Sem Plus",
        _ => "A conferir",
    };
}

/// <summary>The one place that turns the last Plus read into what the screen says.</summary>
public static class PlusStatus
{
    public static readonly TimeSpan ExpiringWindow = TimeSpan.FromDays(7);
    /// <summary>An offer seen on the official page counts for a day; after that, open the page again.</summary>
    public static readonly TimeSpan PageOfferMaxAge = TimeSpan.FromHours(24);

    public static PlusStatusView Evaluate(AccountFinance? f, DateTimeOffset now)
    {
        var v = EvaluateCore(f, now);
        if (v.Label is not (PlusLabel.None or PlusLabel.Expired) || f is null) return v;
        // The subscription answer decides first; the official page's own data otherwise (if recent).
        string? offer = v.Info?.TrialEligible switch
        {
            true => "api",
            false => null,
            _ => f.TrialEligibleFromPage == true && f.TrialFromPageUtc is { } at && now - at <= PageOfferMaxAge ? "página oficial" : null,
        };
        return v with { TrialOffer = offer };
    }

    private static PlusStatusView EvaluateCore(AccountFinance? f, DateTimeOffset now)
    {
        if (f is null)
            return new(PlusLabel.ToVerify, false, "ainda não lida", null, null);
        if (f.FailedReads.Contains(FinanceField.Plus))
            return new(PlusLabel.ToVerify, false, f.FailureReasons.GetValueOrDefault(FinanceField.Plus, "erro na leitura"), f.Plus, f.PlusAtUtc);
        if (f.Plus is not { } p)
            return new(PlusLabel.ToVerify, false, "ainda não lida", null, null);
        if (p.NoSubscription)
            return new(PlusLabel.None, false, null, p, f.PlusAtUtc);

        DateTimeOffset? validUntil = p.ExpiresUtc ?? p.RenewsUtc;
        if (p.Active && validUntil is { } until && until <= now)
        {
            // It was valid when read and the date has passed since: if a renewal was confirmed, only a new read can say.
            return p.WillRenew == true
                ? new(PlusLabel.ToVerify, false, "a validade lida já passou; atualize para confirmar a renovação", p, f.PlusAtUtc)
                : new(PlusLabel.Expired, false, null, p, f.PlusAtUtc);
        }
        if (!p.Active)
            return new(PlusLabel.Expired, false, null, p, f.PlusAtUtc);

        bool expiring = p.WillRenew != true && p.ExpiresUtc is { } exp && exp - now <= ExpiringWindow;
        return new(p.Trial ? PlusLabel.Trial : PlusLabel.Active, expiring, null, p, f.PlusAtUtc);
    }
}
