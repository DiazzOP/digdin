using System.Globalization;
using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.App.UI;

/// <summary>Number and date formatting shared by the account summary and the Plus page (pt-BR, Brasília time).</summary>
internal static class Fmt
{
    public const string ToVerify = "a conferir";
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    public static string N(long n) => n.ToString("N0", Br);
    /// <summary>"1 cópia", "2 cópias": the number with the right word (no "(s)").</summary>
    public static string Count(long n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";
    public static string Time(DateTimeOffset utc) => BrasiliaTime.ToBrasilia(utc).ToString("dd/MM HH:mm", Br);
    public static string Day(DateTimeOffset utc) => BrasiliaTime.ToBrasilia(utc).ToString("dd/MM/yyyy", Br);
    /// <summary>"11/12" this year, "11/12/27" otherwise (table cells).</summary>
    public static string ShortDay(DateTimeOffset utc)
    {
        var d = BrasiliaTime.ToBrasilia(utc);
        return d.Year == BrasiliaTime.ToBrasilia(DateTimeOffset.UtcNow).Year ? d.ToString("dd/MM", Br) : d.ToString("dd/MM/yy", Br);
    }

    public static string DayTime(DateTimeOffset utc) => BrasiliaTime.ToBrasilia(utc).ToString("dd/MM/yyyy HH:mm", Br);

    /// <summary>Dot colour for a Plus status: ok active, online trial / trial on offer, warn expiring or to verify, bad expired.</summary>
    public static Color PlusColor(PlusStatusView p) => p.Label switch
    {
        PlusLabel.Active => p.Expiring ? Theme.Warning : Theme.Valid,
        PlusLabel.Trial => p.Expiring ? Theme.Warning : Theme.Online,
        PlusLabel.Expired => p.TrialAvailable ? Theme.Online : Theme.Expired,
        PlusLabel.None => p.TrialAvailable ? Theme.Online : Theme.Unknown,
        _ => Theme.Warning,
    };

    /// <summary>Ink for the status word next to the dot (readable on panels; never a coloured background).</summary>
    public static Color PlusInk(PlusStatusView p) => p.Label switch
    {
        PlusLabel.Active => p.Expiring ? Theme.WarnInk : Theme.OkInk,
        PlusLabel.Trial => p.Expiring ? Theme.WarnInk : Theme.OnlineInk,
        PlusLabel.Expired => p.TrialAvailable ? Theme.OnlineInk : Theme.BadInk,
        PlusLabel.None => p.TrialAvailable ? Theme.OnlineInk : Theme.TextDim,
        _ => Theme.WarnInk,
    };

    /// <summary>"Ativo · Expirando" etc.</summary>
    public static string PlusText(PlusStatusView p) =>
        p.Text + (p.Expiring ? " · Expirando" : "") + (p.TrialAvailable ? " · Trial disponível" : "");

    /// <summary>"Teto diário caiu de 5.000 para 500 (Roblox); … Detectado em 17/11 14:20."</summary>
    public static string LimitDrops(IReadOnlyList<LimitDrop> drops)
    {
        if (drops.Count == 0) return "";
        var parts = drops.Select((d, i) =>
            $"{(i == 0 ? "T" : "t")}eto {(d.Kind == LimitKind.Daily ? "diário" : "de 30 dias")} caiu de {N(d.From)} para {N(d.To)} " +
            (d.Source == LimitSource.Roblox ? "(Roblox)" : "(sua restrição nas configurações)"));
        return string.Join("; ", parts) + $". Detectado em {Time(drops.Max(d => d.AtUtc))}.";
    }

    /// <summary>Validity / next renewal, as Roblox reported them.</summary>
    public static string PlusDates(PlusStatusView p)
    {
        if (p.TrialAvailable)
            return $"teste grátis disponível (informado pela {(p.TrialOffer == "api" ? "API de assinaturas" : "página oficial do Plus")})";
        if (p.Info is { NoSubscription: true } none)
            return none.TrialEligible == false
                ? "o Roblox informa que esta conta não tem teste grátis disponível"
                : "teste grátis: não informado — abra roblox.com/plus nesta conta para o app conferir";
        if (p.Info is not { } i) return "";
        var parts = new List<string>();
        if (i.WillRenew == true && i.RenewsUtc is { } r) parts.Add("renova " + Day(r));
        if (i.ExpiresUtc is { } e) parts.Add((i.Active ? "válido até " : "venceu em ") + DayTime(e));
        if (i.WillRenew == false) parts.Add("renovação cancelada");
        else if (i.WillRenew is null && i.Active) parts.Add("renovação não informada pelo Roblox");
        return string.Join(" · ", parts);
    }
}
