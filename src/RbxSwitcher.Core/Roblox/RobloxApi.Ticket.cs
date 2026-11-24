using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Roblox;

/// <param name="Ticket">One-use sign-in ticket for the Roblox app. Never logged, stored or shown.</param>
/// <param name="Problem">Why there is no ticket (value-free).</param>
/// <param name="SessionRefused">Roblox refused the session (401): the account must sign in again.</param>
public sealed record TicketResult(string? Ticket, string? Problem, int? Http, bool UsedAssertion, bool SessionRefused = false, string? RotatedSession = null)
{
    public bool Ok => Ticket is not null;
    public override string ToString() => Ok ? "TicketResult(ok)" : $"TicketResult({Problem})"; // never the ticket
}

/// <summary>
/// The sign-in ticket roblox.com's "Play" button uses to open the Roblox app already signed in:
/// a client assertion (since Sep 2026), then POST auth.roblox.com/v1/authentication-ticket/ with the account's
/// cookie and CSRF token; the ticket comes back in the rbx-authentication-ticket header. Only asked after the
/// person clicks "Entrar no app como @conta (automático)".
/// </summary>
public sealed partial class RobloxApi
{
    internal const string ClientAssertionUrl = "https://auth.roblox.com/v1/client-assertion/";
    internal const string AuthTicketUrl = "https://auth.roblox.com/v1/authentication-ticket/";
    private const string Website = "https://www.roblox.com";

    public async Task<TicketResult> GetAuthTicketAsync(string session, CancellationToken ct = default)
    {
        if (!LooksLikeSession(session)) return new TicketResult(null, "sessão inválida", null, false);
        try { return await TicketCoreAsync(_direct ?? _http, session, ct).ConfigureAwait(false); }
        catch (HttpRequestException) when (_direct is not null)
        {
            // Roblox not reachable without the proxy: ask through it (the app may refuse a ticket from another address).
            Logging.Log.Warn("[ticket] Roblox não respondeu sem o proxy do Windows; pedido pelo proxy");
            try { return await TicketCoreAsync(_http, session, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { return new TicketResult(null, "sem conexão com o Roblox", null, false); }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TicketResult(null, "o Roblox demorou demais para responder", null, false);
        }
    }

    private async Task<TicketResult> TicketCoreAsync(HttpClient http, string session, CancellationToken ct)
    {
        string? rotated = null;
        string? assertion = await ClientAssertionAsync(http, session, ct).ConfigureAwait(false);
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = TicketRequest(HttpMethod.Post, AuthTicketUrl, session, csrf, assertion ?? "{}");
            req.Headers.Add("RBXAuthenticationNegotiation", "1");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            rotated = ReadRotatedSession(resp.Headers, session) ?? rotated;
            int code = (int)resp.StatusCode;
            if (resp.Headers.TryGetValues("rbx-authentication-ticket", out var values) && values.FirstOrDefault() is { Length: > 0 } ticket)
            {
                if (!LooksLikeTicket(ticket)) return new TicketResult(null, "ticket em formato inesperado", code, assertion is not null, false, rotated);
                Logging.Log.Info($"[ticket] obtido (asserção do cliente: {(assertion is not null ? "sim" : "não")})");
                return new TicketResult(ticket, null, code, assertion is not null, false, rotated);
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden && csrf is null && resp.Headers.TryGetValues("x-csrf-token", out var tok))
            {
                csrf = tok.FirstOrDefault();
                continue;
            }
            string why = code switch
            {
                401 => "sessão recusada: entre de novo na conta",
                429 => "limite de pedidos do Roblox; tente em alguns minutos",
                >= 500 => $"o Roblox falhou (HTTP {code}); tente de novo",
                _ => $"o Roblox recusou o ticket (HTTP {code})",
            };
            if (await ErrorReasonAsync(resp, ct).ConfigureAwait(false) is { } detail && code is not 401) why += $" — {detail}";
            Logging.Log.Warn($"[ticket] recusado: HTTP {code}");
            return new TicketResult(null, why, code, assertion is not null, code == 401, rotated);
        }
        return new TicketResult(null, "o Roblox não aceitou o token de segurança (CSRF)", 403, assertion is not null, false, rotated);
    }

    /// <summary>The body roblox.com posts with the ticket request since Sep 2026; null when Roblox gives none.</summary>
    private async Task<string?> ClientAssertionAsync(HttpClient http, string session, CancellationToken ct)
    {
        using (var get = TicketRequest(HttpMethod.Get, ClientAssertionUrl, session, null, null))
        using (var resp = await http.SendAsync(get, ct).ConfigureAwait(false))
        {
            if (resp.IsSuccessStatusCode) return await JsonObjectBody(resp, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.MethodNotAllowed) { Logging.Log.Warn($"[ticket] asserção do cliente: HTTP {(int)resp.StatusCode}"); return null; }
        }
        string? csrf = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var post = TicketRequest(HttpMethod.Post, ClientAssertionUrl, session, csrf, "{}");
            using var resp = await http.SendAsync(post, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return await JsonObjectBody(resp, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Forbidden && csrf is null && resp.Headers.TryGetValues("x-csrf-token", out var tok)) { csrf = tok.FirstOrDefault(); continue; }
            Logging.Log.Warn($"[ticket] asserção do cliente: HTTP {(int)resp.StatusCode}");
            return null;
        }
        return null;
    }

    private static async Task<string?> JsonObjectBody(HttpResponseMessage resp, CancellationToken ct)
    {
        string body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        return body.StartsWith('{') && body.Length <= 64 * 1024 ? body : null;
    }

    private static HttpRequestMessage TicketRequest(HttpMethod method, string url, string session, string? csrf, string? json)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cookie", $"{CookieName}={session}");
        req.Headers.Add("Origin", Website);
        req.Headers.Referrer = new Uri(Website + "/");
        if (csrf is not null) req.Headers.Add("x-csrf-token", csrf);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-.=/%]{16,4096}$")]
    private static partial Regex TicketChars();

    /// <summary>Plain token characters only: anything else could break or extend the roblox-player link.</summary>
    internal static bool LooksLikeTicket(string ticket) => TicketChars().IsMatch(ticket);
}
