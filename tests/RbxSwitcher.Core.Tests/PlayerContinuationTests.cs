using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

public class PlayerContinuationTests
{
    [Fact]
    public void The_sites_send_robux_link_is_replayed_after_sign_in()
    {
        var link = PlayerLinks.Classify("roblox://navigation/currency_transfer?recipientUserId=98765");
        Assert.True(PlayerContinuation.CanContinue(link));
        Assert.Equal("navigation/currency_transfer", link.Action);
        Assert.DoesNotContain("98765", link.Summary);    // the recipient id never reaches the log
    }

    [Theory]
    [InlineData("roblox-player:1+launchmode:play+gameinfo:FAKETICKET0123456789+launchtime:1")] // already signs in
    [InlineData("roblox-player:1+launchmode:app+launchtime:1")]                              // not an app-navigation link
    [InlineData("roblox://navigation/x?ticket=abc")]                                         // carries sign-in-like data
    [InlineData("ms-settings:privacy")]
    public void Other_links_are_not_replayed(string uri) =>
        Assert.False(PlayerContinuation.CanContinue(PlayerLinks.Classify(uri)));

    [Theory]
    [InlineData("roblox://navigation/currency_transfer?recipientUserId=98765", "Abrir envio de Robux no Roblox")]
    [InlineData("roblox-player:1+launchmode:play+gameinfo:FAKETICKET0123456789+launchtime:1", "Abrir o jogo no Roblox")]
    [InlineData("roblox-player:1+launchmode:app+launchtime:1", "Abrir o aplicativo Roblox")]
    [InlineData("roblox://navigation/home", "Abrir o aplicativo Roblox")]
    public void The_dialog_title_says_what_the_link_opens(string uri, string title) =>
        Assert.Equal(title, PlayerLinks.Purpose(PlayerLinks.Classify(uri)));
}
