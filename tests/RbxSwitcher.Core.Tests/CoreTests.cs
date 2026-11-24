using System.Net;
using System.Text.Json;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;
using RbxSwitcher.Core.Security;
using RbxSwitcher.Core.Sessions;

namespace RbxSwitcher.Core.Tests;

public class AccountStoreTests
{
    [Fact]
    public void Same_user_id_is_never_saved_twice()
    {
        var s = new AccountStore();
        Assert.Equal(UpsertResult.Added, s.Upsert(10, "a", "A", Fake.Session("1")));
        string fresh = Fake.Session("2");
        Assert.Equal(UpsertResult.Updated, s.Upsert(10, "a_renamed", "A2", fresh));
        Assert.Equal(1, s.Count);
        Assert.Equal(fresh, s.Find(10)!.RevealSession());
        Assert.Equal("a_renamed", s.Find(10)!.Username);
    }

    [Fact]
    public void Duplicates_from_an_old_vault_are_collapsed_keeping_newest()
    {
        var older = new Account { UserId = 5, Username = "old", LastValidUtc = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = new Account { UserId = 5, Username = "new", LastValidUtc = DateTimeOffset.UtcNow };
        var s = new AccountStore([older, newer]);
        Assert.Equal(1, s.Count);
        Assert.Equal("new", s.Find(5)!.Username);
    }

    [Fact]
    public void Search_matches_username_display_alias_group_and_sorts_by_group()
    {
        var s = new AccountStore();
        s.Upsert(1, "Account_001", "Joao", Fake.Session());
        s.Upsert(2, "Account_035", "Maria", Fake.Session());
        s.Upsert(3, "Other", "Zed", Fake.Session());
        s.Find(2)!.Group = "Trades";
        s.Find(3)!.Alias = "minha main";

        Assert.Equal([1L, 2L], s.Search("account_").Select(a => a.UserId).Order());
        Assert.Equal(2, s.Search("@account_035").Single().UserId);
        Assert.Equal(3, s.Search("MAIN").Single().UserId);
        Assert.Equal(2, s.Search(null, "trades").Single().UserId);
        Assert.Equal(2, s.Search("").First().UserId); // named groups come before "Sem grupo"
        Assert.Equal(["Trades", AccountStore.NoGroup], s.Groups);
    }

    [Fact]
    public void Rotation_only_replaces_the_expected_old_session()
    {
        var s = new AccountStore();
        string a = Fake.Session("a"), b = Fake.Session("b"), c = Fake.Session("c");
        s.Upsert(1, "u", "u", a);
        Assert.False(s.ReplaceSessionIf(1, b, c));   // stale expectation
        Assert.True(s.ReplaceSessionIf(1, a, b));
        Assert.Equal(b, s.Find(1)!.RevealSession());
    }

    [Fact]
    public void Account_ToString_never_contains_the_session()
    {
        var s = new AccountStore();
        string session = Fake.Session();
        s.Upsert(1, "u", "u", session);
        Assert.DoesNotContain("WARNING", s.Find(1)!.ToString());
    }
}

[Collection("Log")]
public class RedactTests
{
    [Theory]
    [InlineData("Cookie: .ROBLOSECURITY={0}; other=1")]
    [InlineData("{{\".ROBLOSECURITY\":\"{0}\"}}")]
    [InlineData("failed with {0} in message")]
    public void Session_never_survives_redaction(string template)
    {
        string session = Fake.Session();
        string output = Redact.Text(string.Format(template, session));
        Assert.DoesNotContain("WARNING", output);
        Assert.DoesNotContain(session[^16..], output);
        Assert.Contains(Redact.Marker, output);
    }

    [Fact]
    public void Log_file_never_contains_session()
    {
        using var dir = new TempDir();
        string path = dir.File("app.log");
        Log.Init(path);
        string session = Fake.Session();
        Log.Error("request failed", new InvalidOperationException($"bad cookie .ROBLOSECURITY={session}"));
        Log.Info($"x-csrf-token: abc123 {session}");
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("WARNING", text);
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("request failed", text);
    }
}

public class LivePoolTests
{
    [Fact]
    public void Keeps_most_recent_and_never_evicts_active()
    {
        var p = new LivePool<int>(3);
        Assert.Empty(p.Touch(1));
        Assert.Empty(p.Touch(2));
        Assert.Empty(p.Touch(3));
        Assert.Equal([1], p.Touch(4));       // 1 is least recent
        Assert.Empty(p.Touch(2));            // already alive: no eviction
        Assert.Equal([3], p.Touch(5));
        Assert.Equal([5, 2, 4], p.Keys);
        p.Capacity = 1;
        Assert.Equal([4, 2], p.Trim());
        Assert.Equal([5], p.Keys);
    }
}

public class RobloxApiTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body)) };

    [Fact]
    public async Task Valid_session_returns_user_and_only_goes_to_roblox()
    {
        var h = new StubHandler(_ => Json(HttpStatusCode.OK, new { id = 99, name = "Account_035", displayName = "Disp" }));
        using var api = new RobloxApi(h);
        string session = Fake.Session();
        var r = await api.GetAuthenticatedUserAsync(session);
        Assert.Equal(AuthOutcome.Valid, r.Outcome);
        Assert.Equal(new AuthUser(99, "Account_035", "Disp"), r.User);
        var req = Assert.Single(h.Requests);
        Assert.Equal("users.roblox.com", req.RequestUri!.Host);
        Assert.Equal("https", req.RequestUri.Scheme);
        Assert.Equal($".ROBLOSECURITY={session}", req.Headers.GetValues("Cookie").Single());
    }

    [Fact]
    public async Task Unauthorized_means_expired()
    {
        using var api = new RobloxApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Equal(AuthOutcome.Invalid, (await api.GetAuthenticatedUserAsync(Fake.Session())).Outcome);
    }

    [Fact]
    public async Task Server_errors_are_unreachable_not_expired()
    {
        using var api = new RobloxApi(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(AuthOutcome.Unreachable, (await api.GetAuthenticatedUserAsync(Fake.Session())).Outcome);
    }

    [Fact]
    public async Task Rotated_cookie_is_picked_up_but_deletions_are_ignored()
    {
        string fresh = Fake.Session("fresh");
        var h = new StubHandler(_ =>
        {
            var resp = Json(HttpStatusCode.OK, new { id = 1, name = "u", displayName = "u" });
            resp.Headers.Add("Set-Cookie", ".ROBLOSECURITY=; expires=Thu, 01 Jan 1970 00:00:00 GMT");
            resp.Headers.Add("Set-Cookie", $".ROBLOSECURITY={fresh}; domain=.roblox.com; path=/; secure; HttpOnly");
            return resp;
        });
        using var api = new RobloxApi(h);
        var r = await api.GetAuthenticatedUserAsync(Fake.Session("old"));
        Assert.Equal(fresh, r.RotatedSession);
    }

    [Fact]
    public async Task Garbage_is_never_sent_as_a_session()
    {
        var h = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        using var api = new RobloxApi(h);
        Assert.Equal(AuthOutcome.Invalid, (await api.GetAuthenticatedUserAsync("abc")).Outcome);
        Assert.Equal(AuthOutcome.Invalid, (await api.GetAuthenticatedUserAsync(Fake.Session() + "\r\nX-Evil: 1")).Outcome);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Logout_retries_with_csrf_token()
    {
        int calls = 0;
        var h = new StubHandler(req =>
        {
            calls++;
            if (!req.Headers.Contains("x-csrf-token"))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                r.Headers.Add("x-csrf-token", "tok");
                return r;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var api = new RobloxApi(h);
        Assert.True(await api.LogoutAsync(Fake.Session()));
        Assert.Equal(2, calls);
        Assert.Equal("auth.roblox.com", h.Requests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task Avatar_urls_outside_roblox_cdn_are_rejected()
    {
        var h = new StubHandler(_ => Json(HttpStatusCode.OK, new
        {
            data = new object[]
            {
                new { targetId = 1, state = "Completed", imageUrl = "https://tr.rbxcdn.com/abc/48/48/AvatarHeadshot/Png" },
                new { targetId = 2, state = "Completed", imageUrl = "https://evil.example/x.png" },
                new { targetId = 3, state = "Pending", imageUrl = "" },
            }
        }));
        using var api = new RobloxApi(h);
        var urls = await api.GetHeadshotUrlsAsync([1, 2, 3]);
        Assert.Equal(new[] { 1L }, urls.Keys);
        Assert.False(h.Requests[0].Headers.Contains("Cookie")); // avatars never carry a session
    }
}

public class SessionCheckerTests
{
    [Fact]
    public void Result_for_a_different_user_never_overwrites_account()
    {
        var s = new AccountStore();
        string session = Fake.Session();
        s.Upsert(1, "mine", "mine", session);
        bool save = SessionChecker.Apply(s, new CheckResult(1, session, new AuthResult(AuthOutcome.Valid, new AuthUser(2, "someone", "else"))));
        Assert.False(save);
        Assert.Equal("mine", s.Find(1)!.Username);
        Assert.Equal(SessionStatus.Expired, s.Find(1)!.Status);
    }

    [Fact]
    public void Stale_result_after_reauth_is_ignored()
    {
        var s = new AccountStore();
        string old = Fake.Session("old"), fresh = Fake.Session("new");
        s.Upsert(1, "u", "u", old);
        s.Upsert(1, "u", "u", fresh); // re-authenticated while the check was running
        SessionChecker.Apply(s, new CheckResult(1, old, new AuthResult(AuthOutcome.Invalid, null)));
        Assert.Equal(SessionStatus.Valid, s.Find(1)!.Status);
    }

    [Fact]
    public void Network_failure_does_not_mark_expired()
    {
        var s = new AccountStore();
        string session = Fake.Session();
        s.Upsert(1, "u", "u", session);
        SessionChecker.Apply(s, new CheckResult(1, session, new AuthResult(AuthOutcome.Unreachable, null)));
        Assert.Equal(SessionStatus.Unreachable, s.Find(1)!.Status);
        Assert.Equal(session, s.Find(1)!.RevealSession());
    }
}
