using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Security;

/// <summary>
/// Removes Roblox session secrets from any text before it reaches a log, an error
/// message or the clipboard-free UI. Applied by <see cref="Logging.Log"/> to every line.
/// </summary>
public static partial class Redact
{
    public const string Marker = "[REDACTED]";

    // The .ROBLOSECURITY value always starts with this warning prefix.
    [GeneratedRegex(@"_\|WARNING:-DO-NOT-SHARE-THIS[^\s;""',}]*", RegexOptions.CultureInvariant)]
    private static partial Regex WarningToken();

    // "name=value" / "name: value" / JSON "name":"value" forms of sensitive cookies or headers.
    [GeneratedRegex(@"(?i)(\.ROBLOSECURITY|RBXAuthenticationNegotiation|x-csrf-token|authorization|cookie)(""?\s*[:=]\s*""?)([^\s;""',}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecret();

    // Roblox app links carry one-use sign-in tickets: never log them.
    [GeneratedRegex(@"(?i)\broblox-player:[^\s""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLink();

    [GeneratedRegex(@"(?i)(gameinfo:|\b(?:ticket|authticket|authenticationticket|authtoken|rbx-authentication-ticket)\s*[=:]\s*)[^\s+&;""',}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Ticket();

    public static string Text(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;
        string s = WarningToken().Replace(input, Marker);
        s = PlayerLink().Replace(s, "roblox-player:" + Marker);
        s = Ticket().Replace(s, m => m.Groups[1].Value + Marker);
        s = NamedSecret().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Marker);
        return s;
    }
}
