using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RbxSwitcher.Core.Roblox;

public enum AuthOutcome { Valid, Invalid, Unreachable }

public sealed record AuthUser(long Id, string Name, string DisplayName);

/// <param name="RotatedSession">A new .ROBLOSECURITY Roblox handed back in this response, if any.</param>
public sealed record AuthResult(AuthOutcome Outcome, AuthUser? User, string? RotatedSession = null, TimeSpan? RetryAfter = null);

/// <summary>
/// The few Roblox web APIs the app needs. Talks only to *.roblox.com over HTTPS.
/// Sessions are sent only in the Cookie header of these requests; this class never
/// logs, stores or forwards them anywhere else.
/// </summary>
public sealed partial class RobloxApi : IDisposable
{
    public const string CookieName = ".ROBLOSECURITY";
    internal const string AuthenticatedUrl = "https://users.roblox.com/v1/users/authenticated";
    internal const string LogoutUrl = "https://auth.roblox.com/v2/logout";
    internal const string HeadshotUrl = "https://thumbnails.roblox.com/v1/users/avatar-headshot";

    private readonly HttpClient _http;
    /// <summary>
    /// Without Windows' proxy, for the sign-in ticket only: the ticket is bound to the address that asks for it and
    /// the Roblox app redeems it without a proxy. Null when a test handler is injected (then _http is used).
    /// </summary>
    private readonly HttpClient? _direct;

    public RobloxApi(HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            _direct = new HttpClient(new SocketsHttpHandler
            {
                UseCookies = false, AllowAutoRedirect = false, UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
            _direct.DefaultRequestHeaders.UserAgent.ParseAdd("RbxSwitcher/0.1 (+account manager)");
        }
        handler ??= new SocketsHttpHandler
        {
            UseCookies = false,            // no shared cookie jar: every request carries exactly one account's cookie
            AllowAutoRedirect = false,     // a redirect must never carry the Cookie header to another host
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RbxSwitcher/0.1 (+account manager)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Asks Roblox who this session belongs to. 401 means the session is no longer valid.</summary>
    public async Task<AuthResult> GetAuthenticatedUserAsync(string session, CancellationToken ct = default)
    {
        if (!LooksLikeSession(session))
            return new AuthResult(AuthOutcome.Invalid, null);
        using var req = new HttpRequestMessage(HttpMethod.Get, AuthenticatedUrl);
        req.Headers.Add("Cookie", $"{CookieName}={session}");
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new AuthResult(AuthOutcome.Unreachable, null);
        }
        using (resp)
        {
            string? rotated = ReadRotatedSession(resp.Headers, session);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var user = ParseUser(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    return user is null
                        ? new AuthResult(AuthOutcome.Unreachable, null)
                        : new AuthResult(AuthOutcome.Valid, user, rotated);
                case HttpStatusCode.Unauthorized:
                    return new AuthResult(AuthOutcome.Invalid, null);
                case HttpStatusCode.TooManyRequests:
                    return new AuthResult(AuthOutcome.Unreachable, null, null, resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10));
                default:
                    return new AuthResult(AuthOutcome.Unreachable, null);
            }
        }
    }

    /// <summary>
    /// Ends the session on Roblox's side (like "Log out" on the site). Used when the person removes an
    /// account and asks to sign it out, so a leaked copy of the cookie stops working.
    /// </summary>
    public async Task<bool> LogoutAsync(string session, CancellationToken ct = default)
    {
        if (!LooksLikeSession(session)) return false;
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, LogoutUrl) { Content = new StringContent("") };
            req.Headers.Add("Cookie", $"{CookieName}={session}");
            if (csrf is not null) req.Headers.Add("x-csrf-token", csrf);
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized)
                    return true; // 401: already signed out
                if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var v))
                {
                    csrf = v.FirstOrDefault();
                    continue;
                }
                return false;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Public avatar headshot URLs (no session needed), up to 100 ids per request.</summary>
    public async Task<Dictionary<long, string>> GetHeadshotUrlsAsync(IEnumerable<long> userIds, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in userIds.Distinct().Chunk(100))
        {
            string url = $"{HeadshotUrl}?userIds={string.Join(',', chunk)}&size=48x48&format=Png&isCircular=false";
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (item.TryGetProperty("state", out var st) && st.GetString() == "Completed" &&
                        item.TryGetProperty("imageUrl", out var img) && img.GetString() is { } u &&
                        Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
                        parsed.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
                    {
                        result[item.GetProperty("targetId").GetInt64()] = u;
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Avatars are cosmetic: skip this chunk.
            }
        }
        return result;
    }

    /// <summary>Downloads an avatar image; only from Roblox's CDN.</summary>
    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ||
            !u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return await _http.GetByteArrayAsync(u, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>A real .ROBLOSECURITY is long and starts with Roblox's warning text. Anything else is not sent.</summary>
    public static bool LooksLikeSession(string? value) =>
        value is { Length: >= 100 and <= 4096 } &&
        value.StartsWith("_|WARNING:", StringComparison.Ordinal) &&
        !value.Any(c => c is ';' or '\r' or '\n' or ' ' || char.IsControl(c));

    internal static AuthUser? ParseUser(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            long id = r.GetProperty("id").GetInt64();
            string name = r.GetProperty("name").GetString() ?? "";
            string display = r.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
            return id > 0 && name.Length > 0 ? new AuthUser(id, name, display) : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Reads a new .ROBLOSECURITY from Set-Cookie headers, ignoring deletions and the value already sent.</summary>
    internal static string? ReadRotatedSession(HttpResponseHeaders headers, string sent)
    {
        if (!headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (string header in values)
        {
            string? v = ParseSetCookie(header, CookieName);
            if (v is not null && v != sent && LooksLikeSession(v))
                return v;
        }
        return null;
    }

    internal static string? ParseSetCookie(string header, string name)
    {
        int semi = header.IndexOf(';');
        string pair = semi < 0 ? header : header[..semi];
        int eq = pair.IndexOf('=');
        if (eq <= 0) return null;
        if (!pair[..eq].Trim().Equals(name, StringComparison.Ordinal)) return null;
        string value = pair[(eq + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }

    public void Dispose()
    {
        _http.Dispose();
        _direct?.Dispose();
    }
}
