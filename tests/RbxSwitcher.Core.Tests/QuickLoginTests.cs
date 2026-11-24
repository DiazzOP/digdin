using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Tests;

public class QuickLoginTests
{
    [Fact]
    public void The_page_that_answered_404_is_not_used_and_settings_is_the_last_resort()
    {
        Assert.DoesNotContain("https://www.roblox.com/crossdevicelogin", QuickLogin.Pages); // 404 on altconta35
        Assert.Equal("https://www.roblox.com/crossdevicelogin/ConfirmCode", QuickLogin.Pages[0]);
        Assert.True(QuickLogin.IsSettingsFallback(QuickLogin.Pages[^1]));
        Assert.All(QuickLogin.Pages, p => Assert.StartsWith("https://www.roblox.com/", p));
    }

    [Theory]
    [InlineData(true, 200, true)]
    [InlineData(true, 302, true)]
    [InlineData(false, 404, false)]
    [InlineData(true, 404, false)]     // the site's "Página não encontrada" still counts as not found
    [InlineData(false, 500, false)]
    [InlineData(true, 0, true)]        // status not reported but the navigation succeeded
    [InlineData(false, 0, false)]
    public void A_page_is_kept_only_when_it_really_loads(bool success, int status, bool kept) =>
        Assert.Equal(kept, QuickLogin.Loaded(success, status));
}
