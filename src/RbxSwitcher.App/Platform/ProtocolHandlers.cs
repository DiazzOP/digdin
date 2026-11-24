using Microsoft.Win32;

namespace RbxSwitcher.App.Platform;

/// <summary>
/// Which program Windows starts for roblox-player: / roblox:// (read-only registry lookup, current user first).
/// The path shown has the user's folder replaced by %LocalAppData% / %AppData%, so no personal name is logged.
/// </summary>
internal static class ProtocolHandlers
{
    public sealed record Handler(bool Registered, string Program, string Location, bool Exists);

    public static Handler Describe(string scheme)
    {
        string? command = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command")
                            ?? Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
            command = key?.GetValue("") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        if (string.IsNullOrWhiteSpace(command)) return new Handler(false, "nenhum", "", false);
        string exe = command.TrimStart().StartsWith('"')
            ? command.TrimStart()[1..].Split('"')[0]
            : command.Trim().Split(' ')[0];
        string program = Path.GetFileName(exe);
        string folder = Path.GetDirectoryName(exe) ?? "";
        foreach (var (env, label) in new[] { (Environment.SpecialFolder.LocalApplicationData, "%LocalAppData%"), (Environment.SpecialFolder.ApplicationData, "%AppData%") })
        {
            string root = Environment.GetFolderPath(env);
            if (root.Length > 0 && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { folder = label + folder[root.Length..]; break; }
        }
        bool exists;
        try { exists = File.Exists(exe); } catch { exists = false; }
        return new Handler(true, program, folder, exists);
    }

    public static string Text(Handler h) =>
        !h.Registered ? "nenhum programa registrado no Windows"
        : $"{h.Program} ({h.Location})" + (h.Exists ? "" : " — arquivo não encontrado");
}
