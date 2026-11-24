using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;
using RbxSwitcher.Core.Security;

namespace RbxSwitcher.Core.Tests;

/// <summary>Links are fictitious; "FAKE-TICKET-…" stands for the one-use ticket roblox.com puts in gameinfo.</summary>
[Collection("Log")]
public class PlayerLinkTests
{
    private const string Ticket = "FAKE-TICKET-9f8e7d6c5b4a";
    private const string Play =
        "roblox-player:1+launchmode:play+gameinfo:" + Ticket + "+launchtime:1795000000000" +
        "+placelauncherurl:https%3A%2F%2Fwww.roblox.com%2FGame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame%26placeId%3D1818" +
        "+browsertrackerid:123456789+robloxLocale:pt_br+gameLocale:pt_br+channel:+LaunchExp:InApp";

    [Fact]
    public void Play_link_from_the_site_carries_a_ticket_that_never_appears_in_the_summary()
    {
        var link = PlayerLinks.Classify(Play);
        Assert.Equal(PlayerLinkKind.PlayerWithTicket, link.Kind);
        Assert.True(link.CarriesSignIn);
        Assert.Equal("launchmode play", link.Action);
        Assert.Contains("gameinfo", link.ParameterNames);
        Assert.DoesNotContain(Ticket, link.Summary);
        Assert.DoesNotContain("1818", link.Summary);          // no place id
        Assert.DoesNotContain("123456789", link.Summary);     // no tracker id
        Assert.Contains("com ticket de login do roblox.com (oculto)", link.Summary);
    }

    [Fact]
    public void Slash_form_and_app_launchmode_are_understood()
    {
        var link = PlayerLinks.Classify("roblox-player://1/1+launchmode:app+gameinfo:" + Ticket + "+launchtime:1");
        Assert.Equal(PlayerLinkKind.PlayerWithTicket, link.Kind);
        Assert.Equal("launchmode app", link.Action);
    }

    [Fact]
    public void Player_link_without_ticket_does_not_sign_in()
    {
        var link = PlayerLinks.Classify("roblox-player:1+launchmode:app+launchtime:1795000000000");
        Assert.Equal(PlayerLinkKind.PlayerWithoutTicket, link.Kind);
        Assert.False(link.CarriesSignIn);
        Assert.Contains("sem ticket de login", link.Summary);
        Assert.Equal(PlayerLinkKind.PlayerWithoutTicket, PlayerLinks.Classify("roblox-player:1+launchmode:play+gameinfo:+launchtime:1").Kind);
    }

    [Theory]
    [InlineData("roblox://navigation/send_robux?userId=98765", "navigation/send_robux")]
    [InlineData("roblox://experiences/start?placeId=1818&launchData=abc", "experiences/start")]
    [InlineData("roblox://users/98765/profile", "users/<valor>/profile")]
    public void App_links_carry_no_sign_in_and_keep_only_words(string uri, string action)
    {
        var link = PlayerLinks.Classify(uri);
        Assert.Equal(PlayerLinkKind.AppLink, link.Kind);
        Assert.False(link.CarriesSignIn);
        Assert.Equal(action, link.Action);
        Assert.DoesNotContain("98765", link.Summary);
        Assert.DoesNotContain("1818", link.Summary);
        Assert.DoesNotContain("abc", link.Summary);
        Assert.Contains("link do aplicativo, sem login", link.Summary);
    }

    [Theory]
    [InlineData("https://www.roblox.com/home")]
    [InlineData("ms-settings:privacy")]
    [InlineData("roblox-player:")]
    [InlineData("roblox-player:1+bad key:x")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_rejected(string? uri) =>
        Assert.Equal(PlayerLinkKind.Rejected, PlayerLinks.Classify(uri).Kind);

    [Fact]
    public void Oversized_links_are_rejected()
    {
        string huge = "roblox-player:1+launchmode:play+gameinfo:" + new string('a', PlayerLinks.MaxLength);
        Assert.Equal(PlayerLinkKind.Rejected, PlayerLinks.Classify(huge).Kind);
    }

    [Theory]
    [InlineData("https://www.roblox.com", true, true)]
    [InlineData("https://www.roblox.com", false, false)]   // not clicked by the person
    [InlineData("https://evil.example", true, false)]
    [InlineData("http://www.roblox.com", true, false)]
    [InlineData(null, true, false)]
    public void Only_a_click_on_a_roblox_page_may_offer_to_open_the_app(string? origin, bool clicked, bool allowed) =>
        Assert.Equal(allowed, PlayerLinks.MayOffer(PlayerLinks.Classify(Play), origin, clicked).Allowed);

    [Fact]
    public void Tickets_and_player_links_never_reach_the_log()
    {
        using var dir = new TempDir();
        string path = dir.File("app.log");
        Log.Init(path);
        Log.Error("launch failed", new InvalidOperationException("could not open " + Play));
        Log.Info("header rbx-authentication-ticket: " + Ticket);
        Log.Info("query ticket=" + Ticket + "&x=1");
        Log.Info("[player] " + PlayerLinks.Classify(Play).Summary);
        string text = File.ReadAllText(path);
        Assert.DoesNotContain(Ticket, text);
        Assert.Contains("roblox-player:" + Redact.Marker, text);
        Assert.Contains("[player] roblox-player · launchmode play", text);
    }
}
