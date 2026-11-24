namespace RbxSwitcher.Core.Roblox;

/// <summary>
/// After the Roblox app is signed in automatically, the link roblox.com asked for (e.g.
/// roblox://navigation/currency_transfer for the player whose profile was open) is handed to Windows again,
/// unchanged, so the app opens that screen in the right account. Nothing is confirmed in the app.
/// </summary>
public static class PlayerContinuation
{
    /// <summary>How long to wait for the Roblox app window to appear after the sign-in link.</summary>
    public static readonly TimeSpan WaitForWindow = TimeSpan.FromSeconds(60);
    /// <summary>Extra time after the window appears, for the app to finish signing in before the screen is asked.</summary>
    public static readonly TimeSpan SettleAfterWindow = TimeSpan.FromSeconds(8);
    /// <summary>The banner keeps "click to open again" this long.</summary>
    public static readonly TimeSpan ReopenFor = TimeSpan.FromMinutes(5);

    /// <summary>Only the site's own app-navigation link without any sign-in data is replayed.</summary>
    public static bool CanContinue(PlayerLink original) =>
        original.Kind == PlayerLinkKind.AppLink && !original.HasTicketLikeParameter;
}
