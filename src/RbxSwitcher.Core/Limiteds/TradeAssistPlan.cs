using System.Text.Json;

namespace RbxSwitcher.Core.Limiteds;

public sealed record AssistItem(string Name, int? Serial)
{
    public string Label => Name + (Serial is { } s ? $" #{s}" : "");
}

/// <summary>
/// What "Abrir trade" selects on roblox.com's trade page: the source's Limiteds and Small (its offer) and the
/// destination's Small (its request). Names and serials only; the page script never sends the trade.
/// </summary>
/// <param name="Self">The source's names as the page may show them (username, display name).</param>
/// <param name="Partner">The destination's names: the page titles its inventory with the display name ("Inventário de sigma2").</param>
/// <param name="OpenTradeId">Set for the "open" job: on the destination's trades page, open this trade (from Partner).</param>
public sealed record TradeAssistPlan(IReadOnlyList<AssistItem> Give, IReadOnlyList<AssistItem> Ask, IReadOnlyList<string> Self, IReadOnlyList<string> Partner,
                                     long? OpenTradeId = null)
{
    /// <summary>In the destination's tab: open the trade the source just sent (the person accepts it).</summary>
    public static TradeAssistPlan Open(long tradeId, string destination, string? destinationDisplay, string source, string? sourceDisplay) =>
        new([], [], Names(destination, destinationDisplay), Names(source, sourceDisplay), tradeId);

    public int Count => Give.Count + Ask.Count;

    public static TradeAssistPlan For(PlanStep s, string? sourceDisplayName = null, string? destinationDisplayName = null) => new(
        [.. s.Limiteds.Concat(s.SourceSmall is { } ss ? [ss] : []).Select(c => new AssistItem(c.Name, c.Serial))],
        [.. (s.DestinationSmall is { } ds ? [ds] : Array.Empty<CopyRef>()).Select(c => new AssistItem(c.Name, c.Serial))],
        Names(s.SourceName, sourceDisplayName), Names(s.DestinationName, destinationDisplayName));

    private static string[] Names(string username, string? display) =>
        [.. new[] { username, display }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    public string ToJson() => JsonSerializer.Serialize(new
    {
        mode = OpenTradeId is null ? "select" : "open",
        tradeId = OpenTradeId,
        give = Give.Select(i => new { name = i.Name, serial = i.Serial }),
        ask = Ask.Select(i => new { name = i.Name, serial = i.Serial }),
        self = Self,
        partner = Partner,
    });
}

/// <summary>What the page script reported. <see cref="Structure"/> is a value-free outline of the page, kept only when something was not selected.</summary>
/// <param name="Opened">"open" job: the trade (or the source's newest trade) was clicked open.</param>
/// <param name="Navigate">"open" job: a roblox.com link straight to the trade, for the app to open.</param>
public sealed record TradeAssistResult(IReadOnlyList<string> Selected, IReadOnlyList<string> Missing, IReadOnlyList<string> Ambiguous,
                                       IReadOnlyList<string> Unconfirmed, IReadOnlyList<string> Notes, string? Structure,
                                       bool Opened = false, string? Navigate = null)
{
    public bool Complete(TradeAssistPlan plan) => Selected.Count == plan.Count;

    public static TradeAssistResult? Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("state", out var st) || st.GetString() != "done") return null;
        List<string> L(string f) => r.TryGetProperty(f, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : [];
        string? structure = r.TryGetProperty("structure", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        bool opened = r.TryGetProperty("opened", out var o) && o.ValueKind == JsonValueKind.True;
        string? navigate = r.TryGetProperty("navigate", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new(L("selected"), L("missing"), L("ambiguous"), L("unconfirmed"), L("notes"), structure, opened, navigate);
    }

    /// <summary>One status line, in Portuguese: what was selected and what is left to do by hand.</summary>
    public string Summary(TradeAssistPlan plan)
    {
        var parts = new List<string> { $"{Selected.Count} de {plan.Count} {(plan.Count == 1 ? "item pré-selecionado" : "itens pré-selecionados")} na trade" };
        if (Unconfirmed.Count > 0) parts.Add("cliquei sem confirmar: " + string.Join(", ", Unconfirmed));
        if (Missing.Count > 0) parts.Add("não encontrei: " + string.Join(", ", Missing));
        if (Ambiguous.Count > 0) parts.Add("mais de um igual (escolha à mão): " + string.Join(", ", Ambiguous));
        if (Notes.Count > 0) parts.Add(string.Join(", ", Notes));
        return string.Join("; ", parts) + ".";
    }
}

public enum TradeSignalKind { Sent, Accepted }

/// <param name="TradeId">The trade's id, when Roblox's answer gives it.</param>
/// <param name="PartnerId">Who the trade is with, from the trade page's address (/users/{id}/trade).</param>
public sealed record TradeSignal(TradeSignalKind Kind, long? TradeId, long? PartnerId);

/// <summary>
/// Recognizes, in the account's own tab, the moment a trade is sent or accepted on roblox.com (after any 2-step
/// verification the person did): a successful POST to trades.roblox.com …/send or …/trades/{id}/accept. Only the
/// trade id is read from the answer. The app never makes these calls itself.
/// </summary>
public static class TradeSignals
{
    private static readonly System.Text.RegularExpressions.Regex AcceptPath = new(@"/trades/(\d+)/accept/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex TradePage = new(@"^(/[a-z]{2}(-[a-z]{2})?)?/users/(\d+)/trade/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsTradeCall(string method, string url) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("trades.roblox.com", StringComparison.OrdinalIgnoreCase);

    public static TradeSignal? From(string method, string url, int status, string? body, string? pageUrl)
    {
        if (!IsTradeCall(method, url) || status is < 200 or >= 300) return null;
        string path = new Uri(url).AbsolutePath;
        if (AcceptPath.Match(path) is { Success: true } a) return new(TradeSignalKind.Accepted, long.Parse(a.Groups[1].Value), null);
        if (!path.TrimEnd('/').EndsWith("/send", StringComparison.OrdinalIgnoreCase)) return null;
        long? id = null;
        try
        {
            if (body is { Length: > 0 and < 65_536 })
            {
                using var d = JsonDocument.Parse(body);
                id = TradeParsers.Long(d.RootElement, "id") ?? TradeParsers.Long(d.RootElement, "tradeId");
            }
        }
        catch (JsonException) { }
        long? partner = Uri.TryCreate(pageUrl, UriKind.Absolute, out var p) && TradePage.Match(p.AbsolutePath) is { Success: true } m
            ? long.Parse(m.Groups[3].Value) : null;
        return new(TradeSignalKind.Sent, id is > 0 ? id : null, partner);
    }
}
