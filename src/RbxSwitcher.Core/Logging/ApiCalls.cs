using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Logging;

/// <param name="Path">Path with ids, numbers and hashes replaced by &lt;id&gt;.</param>
/// <param name="QueryNames">Query parameter names only.</param>
/// <param name="Shape">Field names and types of a JSON body (<see cref="JsonShape"/>); never values.</param>
/// <param name="TrialEligible">Free-trial eligibility found in the body (a yes/no, never other values).</param>
public sealed record ApiCall(string Method, string Host, string Path, IReadOnlyList<string> QueryNames, int Status, string? Shape,
                             bool? TrialEligible = null);

/// <summary>
/// Turns the requests a roblox.com page makes into value-free records, to learn which official endpoint and
/// fields a page uses (e.g. the free-trial offer on the Roblox Plus page). Nothing else is kept: no query values,
/// no headers, no cookies, no response values.
/// </summary>
public static partial class ApiCalls
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z_\-]{0,40}$")]
    private static partial Regex WordSegment();

    [GeneratedRegex(@"^v\d{1,2}$")]
    private static partial Regex VersionSegment();

    public static ApiCall? From(string method, string uri, int status, string? contentType, string? body)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        if (!(u.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))) return null;
        if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;

        var path = string.Join("/", u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => VersionSegment().IsMatch(seg) || WordSegment().IsMatch(Uri.UnescapeDataString(seg)) ? Uri.UnescapeDataString(seg) : "<id>"));
        var names = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Uri.UnescapeDataString(p.Split('=')[0]))
            .Select(n => WordSegment().IsMatch(n) ? n : "<nome>")
            .Distinct().ToList();

        string? shape = null;
        bool? trial = null;
        if (!string.IsNullOrEmpty(body) && body.Length <= 512 * 1024)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                shape = JsonShape.Describe(doc.RootElement);
                if (status == 200) trial = Roblox.EconomyParsers.TrialEligibleIn(doc.RootElement);
            }
            catch (JsonException) { shape = "(corpo não é JSON)"; }
        }
        return new ApiCall(method.ToUpperInvariant(), u.Host.ToLowerInvariant(), "/" + path, names, status, shape, trial);
    }

    /// <summary>One block per distinct call (method, host, path, status), with how many times it happened.</summary>
    public static string Format(IEnumerable<ApiCall> calls)
    {
        var groups = calls.GroupBy(c => (c.Method, c.Host, c.Path, c.Status)).ToList();
        if (groups.Count == 0) return "Nenhuma chamada JSON do roblox.com foi feita nesta aba durante a gravação.";
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            var first = g.First();
            var query = g.SelectMany(c => c.QueryNames).Distinct().ToList();
            sb.AppendLine($"{first.Method} {first.Host}{first.Path}" + (query.Count > 0 ? "?" + string.Join("&", query.Select(q => q + "=…")) : "") +
                          $" → HTTP {first.Status}" + (g.Count() > 1 ? $" (×{g.Count()})" : "") +
                          (g.Select(c => c.TrialEligible).FirstOrDefault(t => t is not null) is { } te ? $" · teste grátis: {(te ? "disponível" : "indisponível")}" : ""));
            if (g.Select(c => c.Shape).FirstOrDefault(s => s is not null) is { } shape) sb.AppendLine("   campos: " + shape);
        }
        sb.Append("Só método, caminho (ids trocados por <id>), nomes de parâmetros, status e nomes/tipos de campos. Sem valores, cabeçalhos ou cookies.");
        return sb.ToString();
    }
}
