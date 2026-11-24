using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RbxSwitcher.App.Platform;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Time since the last keyboard/mouse input anywhere on this Windows session.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(ms);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark scroll bars (and other common-control parts) on Windows 10 1809+ / 11; ignored elsewhere.</summary>
    public static void UseDarkControlTheme(IntPtr hwnd)
    {
        try { _ = SetWindowTheme(hwnd, "DarkMode_Explorer", null); } catch { }
    }

    /// <summary>Dark title bar on Windows 10 20H1+ / 11; ignored elsewhere.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        try { _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { }
    }

    /// <summary>Opens an https link in the person's default browser (never with any session).</summary>
    public static void OpenExternal(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>
    /// Hands a Roblox app link to Windows exactly as roblox.com made it (the same thing a browser does after its
    /// "Open Roblox?" prompt). Only roblox-player: / roblox:// links that passed <see cref="Core.Roblox.PlayerLinks"/>.
    /// </summary>
    public static bool OpenRobloxLink(string uri)
    {
        if (Core.Roblox.PlayerLinks.Classify(uri).Kind == Core.Roblox.PlayerLinkKind.Rejected) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Logging.Log.Warn("[player] Windows não abriu o link: " + e.GetType().Name);
            return false;
        }
    }
}
