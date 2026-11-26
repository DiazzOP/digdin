using System.Text.Json;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.Browser;

/// <summary>
/// Pre-selects a step's items on the trade page that "Abrir trade" opened in the source account's tab, with the script
/// in TradeAssist.js. The script only clicks item cards, inventory tabs and page arrows; it never sends, accepts or
/// declines, and reads no cookies or storage. The app reads back what was selected.
/// </summary>
internal static class TradeAssist
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(TradeAssist).Assembly.GetManifestResourceStream("RbxSwitcher.TradeAssist.js")
                           ?? throw new InvalidOperationException("TradeAssist.js missing from the build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The script's answer, or why there is none.</summary>
    /// <param name="PageReplaced">The page was reloaded or left while the script ran (for "open", usually the trade opening).</param>
    public sealed record Run(TradeAssistResult? Result, string? Failure, bool PageReplaced = false);

    /// <summary>The app waits this long; the script stops itself after 240 s.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(270);

    public static async Task<Run> RunAsync(SessionTab tab, TradeAssistPlan plan)
    {
        if (await tab.EvaluateAsync(Script.Value.Replace("__PLAN__", plan.ToJson())) is null) return new(null, "a página não aceitou o script");
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            if (!tab.Ready || !IsTradePage(tab.Url)) return new(null, "a página de trade foi fechada", true); // the person moved on
            string? raw = await tab.EvaluateAsync("JSON.stringify(window.__rbxswAssist || null)");
            if (raw is null) return new(null, "a página de trade foi fechada");
            try
            {
                // No answer object at all: the page was reloaded or replaced (another "Abrir trade"); the script is gone.
                if (JsonSerializer.Deserialize<string>(raw) is not { } json || json == "null") return new(null, "a página de trade foi recarregada", true);
                if (TradeAssistResult.Parse(json) is { } result) return new(result, null);
            }
            catch (JsonException) { return new(null, "resposta inesperada do script"); }
        }
        return new(null, "o tempo acabou antes de o script terminar");
    }

    /// <summary>A roblox.com trade page (the site may move from /users/{id}/trade to its newer trade address).</summary>
    public static bool IsTradePage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && SessionTab.IsRobloxHost(u.Host)
        && u.AbsolutePath.Contains("trade", StringComparison.OrdinalIgnoreCase);
}
