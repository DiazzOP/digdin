namespace RbxSwitcher.Core.Roblox;

/// <summary>
/// Where an already signed-in tab approves the code the Roblox app shows ("Quick Log In"). Pages are tried in
/// order and kept only if they load without an HTTP error: "roblox.com/crossdevicelogin" (0.3.4–0.3.9) answered 404.
/// The last entry is the account's Security settings, where Quick Log In is also offered.
/// </summary>
public static class QuickLogin
{
    public static readonly IReadOnlyList<string> Pages =
    [
        "https://www.roblox.com/crossdevicelogin/ConfirmCode",
        "https://www.roblox.com/my/account#!/security",
    ];

    public static bool IsSettingsFallback(string url) => url.Contains("/my/account", StringComparison.OrdinalIgnoreCase);

    /// <summary>A page counts as found on 2xx/3xx; 404 or any error moves on. Unknown status (0) is accepted.</summary>
    public static bool Loaded(bool success, int httpStatus) =>
        httpStatus is >= 200 and < 400 || (success && httpStatus == 0);
}
