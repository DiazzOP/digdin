using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Roblox;

public enum PlayerLinkKind
{
    /// <summary>roblox-player: with a gameinfo ticket made by roblox.com: the Player signs in with it (one use).</summary>
    PlayerWithTicket,
    /// <summary>roblox-player: without a ticket: the Player uses its own saved sign-in.</summary>
    PlayerWithoutTicket,
    /// <summary>roblox:// app link (navigation): carries no sign-in; the app uses its own saved sign-in.</summary>
    AppLink,
    /// <summary>Not a Roblox app link, malformed or too long: never opened.</summary>
    Rejected,
}

/// <param name="Action">What the link asks for (launchmode, or the sanitized roblox:// path).</param>
/// <param name="ParameterNames">Parameter names only; values are never kept.</param>
/// <param name="Summary">Safe one-line description for the screen and the log: no ticket, ids or values.</param>
public sealed record PlayerLink(PlayerLinkKind Kind, string Scheme, string Action, IReadOnlyList<string> ParameterNames,
                                bool HasTicketLikeParameter, string Summary)
{
    public bool CarriesSignIn => Kind == PlayerLinkKind.PlayerWithTicket;
}

/// <summary>
/// Reads the roblox-player: / roblox:// links roblox.com asks the browser to open, to tell the person what
/// will happen in the Roblox app. It never builds, changes or forwards sign-in data: the link is opened
/// exactly as roblox.com made it, or not at all.
/// </summary>
public static partial class PlayerLinks
{
    public const int MaxLength = 8192;
    private static readonly string[] TicketKeys = ["gameinfo", "ticket", "authticket", "authenticationticket", "token", "authtoken"];
    private static readonly string[] KnownLaunchModes = ["play", "app", "edit", "plugin", "asset", "build"];

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]{0,40}$")]
    private static partial Regex SafeSegment();

    /// <summary>What the link opens, in plain words, for the dialog title. Generic when unknown.</summary>
    public static string Purpose(PlayerLink link) => link.Action switch
    {
        var a when a.Contains("currency_transfer", StringComparison.OrdinalIgnoreCase) => "Abrir envio de Robux no Roblox",
        "launchmode play" => "Abrir o jogo no Roblox",
        "launchmode edit" or "launchmode build" or "launchmode plugin" or "launchmode asset" => "Abrir o Roblox Studio",
        _ => "Abrir o aplicativo Roblox",
    };

    public static PlayerLink Classify(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.Length > MaxLength || uri.Any(char.IsControl) || uri.Contains(' '))
            return Rejected("link vazio, grande demais ou com caracteres inválidos");
        int colon = uri.IndexOf(':');
        if (colon <= 0) return Rejected("link sem esquema");
        string scheme = uri[..colon].ToLowerInvariant();
        return scheme switch
        {
            "roblox-player" => PlayerLink_(uri[(colon + 1)..]),
            "roblox" => AppLink_(uri),
            _ => Rejected($"esquema {(SafeSegment().IsMatch(scheme) ? scheme : "desconhecido")} não é do aplicativo Roblox"),
        };
    }

    /// <summary>roblox-player:1+launchmode:play+gameinfo:…+launchtime:… (also "roblox-player://1/1+…").</summary>
    private static PlayerLink PlayerLink_(string rest)
    {
        rest = rest.TrimStart('/');
        var names = new List<string>();
        string? launchMode = null;
        bool ticket = false;
        foreach (string part in rest.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = part.IndexOf(':');
            if (c <= 0) continue; // the leading version ("1" or "1/1")
            string key = part[..c];
            string value = part[(c + 1)..];
            if (!SafeSegment().IsMatch(key)) return Rejected("parâmetro do roblox-player com nome inválido");
            names.Add(key);
            if (key.Equals("launchmode", StringComparison.OrdinalIgnoreCase)) launchMode = value.ToLowerInvariant();
            if (key.Equals("gameinfo", StringComparison.OrdinalIgnoreCase) && value.Length > 0) ticket = true;
        }
        if (names.Count == 0) return Rejected("roblox-player sem parâmetros");
        string action = launchMode is null ? "sem launchmode" : KnownLaunchModes.Contains(launchMode) ? "launchmode " + launchMode : "launchmode desconhecido";
        bool ticketLike = names.Any(n => TicketKeys.Contains(n, StringComparer.OrdinalIgnoreCase));
        var kind = ticket ? PlayerLinkKind.PlayerWithTicket : PlayerLinkKind.PlayerWithoutTicket;
        return new PlayerLink(kind, "roblox-player", action, names, ticketLike, Describe("roblox-player", action, names, kind, ticketLike));
    }

    /// <summary>roblox://host/path?query: the path is kept only where it is made of plain words (ids become &lt;valor&gt;).</summary>
    private static PlayerLink AppLink_(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return Rejected("link roblox:// mal formado");
        var segments = new List<string>();
        if (u.Host.Length > 0) segments.Add(SafeSegment().IsMatch(u.Host) ? u.Host : "<valor>");
        foreach (string s in u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            segments.Add(SafeSegment().IsMatch(Uri.UnescapeDataString(s)) ? Uri.UnescapeDataString(s) : "<valor>");
        var names = new List<string>();
        foreach (string pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = Uri.UnescapeDataString(pair.Split('=')[0]);
            names.Add(SafeSegment().IsMatch(key) ? key : "<nome>");
        }
        string action = segments.Count == 0 ? "(raiz)" : string.Join("/", segments);
        bool ticketLike = names.Any(n => TicketKeys.Contains(n, StringComparer.OrdinalIgnoreCase));
        return new PlayerLink(PlayerLinkKind.AppLink, "roblox", action, names, ticketLike,
            Describe("roblox", action, names, PlayerLinkKind.AppLink, ticketLike));
    }

    private static string Describe(string scheme, string action, IReadOnlyList<string> names, PlayerLinkKind kind, bool ticketLike)
    {
        string login = kind switch
        {
            PlayerLinkKind.PlayerWithTicket => "com ticket de login do roblox.com (oculto)",
            PlayerLinkKind.PlayerWithoutTicket => "sem ticket de login",
            _ => ticketLike ? "link do aplicativo com parâmetro parecido com ticket (oculto)" : "link do aplicativo, sem login",
        };
        return $"{scheme} · {action} · {login} · parâmetros: {(names.Count == 0 ? "nenhum" : string.Join(", ", names))}";
    }

    private static PlayerLink Rejected(string why) => new(PlayerLinkKind.Rejected, "", "", [], false, "recusado: " + why);

    /// <summary>
    /// The link roblox.com's "Play" button opens with launchmode:app: the Roblox app signs in with the one-use
    /// ticket and opens on its home screen. Only built after the person asked for it, with a ticket from
    /// <see cref="RobloxApi.GetAuthTicketAsync"/>.
    /// </summary>
    public static string BuildAppSignIn(string ticket, DateTimeOffset now, long browserTrackerId)
    {
        if (!RobloxApi.LooksLikeTicket(ticket)) throw new ArgumentException("ticket em formato inesperado", nameof(ticket));
        if (browserTrackerId <= 0) throw new ArgumentOutOfRangeException(nameof(browserTrackerId));
        return $"roblox-player:1+launchmode:app+gameinfo:{ticket}+launchtime:{now.ToUnixTimeMilliseconds()}" +
               $"+browsertrackerid:{browserTrackerId}+robloxLocale:pt_br+gameLocale:pt_br+channel:+LaunchExp:InApp";
    }

    /// <summary>Only pages of roblox.com may ask to open the Roblox app.</summary>
    public static bool IsRobloxOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == Uri.UriSchemeHttps &&
        (o.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) || o.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the link may be offered to the person at all. Opening still needs their confirmation in the app.
    /// </summary>
    public static (bool Allowed, string Reason) MayOffer(PlayerLink link, string? origin, bool userInitiated)
    {
        if (link.Kind == PlayerLinkKind.Rejected) return (false, link.Summary);
        if (!IsRobloxOrigin(origin)) return (false, "pedido que não veio de uma página do roblox.com");
        if (!userInitiated) return (false, "pedido sem clique da pessoa");
        return (true, "");
    }
}
