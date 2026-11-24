using System.Net;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

/// <summary>Fake auth.roblox.com: client assertion, CSRF, ticket header. All values are fictitious.</summary>
internal sealed class FakeAuth : HttpMessageHandler
{
    public const string Ticket = "FAKETICKET0123456789abcdefABCDEF";
    public HttpStatusCode AssertionGet = HttpStatusCode.OK;
    public string AssertionBody = """{"clientAssertion":"fake-assertion"}""";
    public HttpStatusCode TicketStatus = HttpStatusCode.OK;
    public string? TicketHeader = Ticket;
    public readonly List<(HttpRequestMessage Req, string? Body)> Calls = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string? body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
        Calls.Add((req, body));
        bool csrf = req.Headers.Contains("x-csrf-token");
        if (req.RequestUri!.AbsolutePath == "/v1/client-assertion/")
        {
            if (req.Method == HttpMethod.Get)
                return new HttpResponseMessage(AssertionGet) { Content = new StringContent(AssertionGet == HttpStatusCode.OK ? AssertionBody : "") };
            if (!csrf) return Forbidden();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AssertionBody) };
        }
        if (req.RequestUri.AbsolutePath == "/v1/authentication-ticket/")
        {
            if (!csrf) return Forbidden();
            var resp = new HttpResponseMessage(TicketStatus) { Content = new StringContent(TicketStatus == HttpStatusCode.OK ? "" : """{"errors":[{"code":0,"message":"Unauthorized"}]}""") };
            if (TicketStatus == HttpStatusCode.OK && TicketHeader is not null) resp.Headers.Add("rbx-authentication-ticket", TicketHeader);
            return resp;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Forbidden()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
        r.Headers.Add("x-csrf-token", "fake-csrf");
        return r;
    }
}

[Collection("Log")]
public class AuthTicketTests
{
    private static async Task<(TicketResult R, FakeAuth H)> Get(Action<FakeAuth>? setup = null, string? session = null)
    {
        var h = new FakeAuth();
        setup?.Invoke(h);
        using var api = new RobloxApi(h);
        return (await api.GetAuthTicketAsync(session ?? Fake.Session()), h);
    }

    [Fact]
    public async Task Ticket_like_the_site_assertion_csrf_and_headers()
    {
        string session = Fake.Session();
        var (r, h) = await Get(session: session);
        Assert.True(r.Ok);
        Assert.Equal(FakeAuth.Ticket, r.Ticket);
        Assert.True(r.UsedAssertion);
        var posts = h.Calls.Where(c => c.Req.RequestUri!.AbsolutePath == "/v1/authentication-ticket/").ToList();
        Assert.Equal(2, posts.Count);                                         // 403 → retried with the CSRF token
        var final = posts[^1];
        Assert.Equal("""{"clientAssertion":"fake-assertion"}""", final.Body); // the assertion is the body
        Assert.Equal("1", final.Req.Headers.GetValues("RBXAuthenticationNegotiation").Single());
        Assert.Equal("https://www.roblox.com", final.Req.Headers.GetValues("Origin").Single());
        Assert.Equal("fake-csrf", final.Req.Headers.GetValues("x-csrf-token").Single());
        Assert.Equal($".ROBLOSECURITY={session}", final.Req.Headers.GetValues("Cookie").Single());
        Assert.All(h.Calls, c => Assert.Equal("auth.roblox.com", c.Req.RequestUri!.Host));
        Assert.DoesNotContain(FakeAuth.Ticket, r.ToString());
    }

    [Fact]
    public async Task Assertion_by_post_when_get_is_not_allowed_and_empty_body_without_one()
    {
        var (viaPost, h) = await Get(x => x.AssertionGet = HttpStatusCode.MethodNotAllowed);
        Assert.True(viaPost.UsedAssertion);
        Assert.Contains(h.Calls, c => c.Req.Method == HttpMethod.Post && c.Req.RequestUri!.AbsolutePath == "/v1/client-assertion/");

        var (none, h2) = await Get(x => x.AssertionGet = HttpStatusCode.NotFound);
        Assert.True(none.Ok);
        Assert.False(none.UsedAssertion);
        Assert.Equal("{}", h2.Calls.Last().Body);
    }

    [Fact]
    public async Task Refusals_are_explained_without_a_ticket()
    {
        var (expired, _) = await Get(x => x.TicketStatus = HttpStatusCode.Unauthorized);
        Assert.False(expired.Ok);
        Assert.True(expired.SessionRefused);
        Assert.Contains("entre de novo", expired.Problem);

        var (noHeader, _) = await Get(x => x.TicketHeader = null);
        Assert.False(noHeader.Ok);

        var (odd, _) = await Get(x => x.TicketHeader = "abc+launchmode:edit+evilpart");
        Assert.False(odd.Ok);                                                  // could extend the roblox-player link
        Assert.Equal("ticket em formato inesperado", odd.Problem);

        var (bad, h) = await Get(session: "not-a-session");
        Assert.False(bad.Ok);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Ticket_never_reaches_the_log_even_inside_the_app_link()
    {
        using var dir = new TempDir();
        string path = dir.File("app.log");
        Log.Init(path);
        var (r, _) = await Get();
        string link = PlayerLinks.BuildAppSignIn(r.Ticket!, QuotaCalculatorTests.Now, 123456789);
        Log.Info("abrindo " + link);
        Log.Info("[player] " + PlayerLinks.Classify(link).Summary);
        string text = File.ReadAllText(path);
        Assert.DoesNotContain(FakeAuth.Ticket, text);
        Assert.Contains("[ticket] obtido (asserção do cliente: sim)", text);
    }

    [Fact]
    public void App_sign_in_link_is_a_play_button_link_with_launchmode_app()
    {
        string link = PlayerLinks.BuildAppSignIn(FakeAuth.Ticket, QuotaCalculatorTests.Now, 987654321);
        Assert.StartsWith("roblox-player:1+launchmode:app+gameinfo:" + FakeAuth.Ticket + "+launchtime:", link);
        var c = PlayerLinks.Classify(link);
        Assert.Equal(PlayerLinkKind.PlayerWithTicket, c.Kind);
        Assert.Equal("launchmode app", c.Action);
        Assert.Throws<ArgumentException>(() => PlayerLinks.BuildAppSignIn("bad ticket+x", QuotaCalculatorTests.Now, 1));
    }
}
