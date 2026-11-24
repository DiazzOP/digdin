using Microsoft.Web.WebView2.Core;
using RbxSwitcher.App.Platform;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Security;
using RbxSwitcher.Core.Storage;

namespace RbxSwitcher.App;

internal static class Program
{
    public static string VaultPath(string dataDir) => Path.Combine(dataDir, "vault.bin");

    [STAThread]
    private static void Main(string[] args)
    {
        // Started by the updater: wait for the previous version to close before taking the single-instance lock.
        int wait = Array.IndexOf(args, "--wait-pid");
        if (wait >= 0 && wait + 1 < args.Length && int.TryParse(args[wait + 1], out int pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15_000); }
            catch (ArgumentException) { } // already gone
        }

        using var single = new Mutex(true, @"Local\RbxSwitcher.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("O RbxSwitcher já está aberto.", "RbxSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RbxSwitcher");
        Directory.CreateDirectory(dataDir);
        Log.Init(Path.Combine(dataDir, "app.log"));
        Updater.CleanUpOldVersion();
        Application.ThreadException += (_, e) => { Log.Error("Unhandled UI exception", e.Exception); ShowError(e.Exception.Message); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        if (!HasWebView2())
        {
            var r = MessageBox.Show(
                "Este app precisa do Microsoft Edge WebView2 Runtime (já vem no Windows 11 e na maioria dos Windows 10).\n\nAbrir a página oficial de download da Microsoft?",
                "WebView2 não encontrado", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
                Native.OpenExternal(new Uri("https://developer.microsoft.com/microsoft-edge/webview2/"));
            return;
        }

        var protector = new DpapiProtector();
        VaultFile? vault = OpenOrCreate(dataDir, protector);
        if (vault is null) return;

        Log.Info($"Started with {vault.Accounts.Count} accounts.");
        Application.Run(new MainForm(vault, protector, dataDir));
    }

    private static bool HasWebView2()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    private static VaultFile? OpenOrCreate(string dataDir, IKeyProtector protector)
    {
        string path = VaultPath(dataDir);
        if (!VaultFile.Exists(path))
        {
            string? pw = PromptDialog.AskNewPassword(null, "Bem-vindo — senha mestra",
                "Recomendado: defina uma senha mestra. Ela protege as contas mesmo contra outros programas no seu Windows. " +
                "Sem ela, o cofre fica protegido só pela sua conta do Windows. Clique em Cancelar para continuar sem senha (dá para definir depois).");
            return VaultFile.Create(path, protector, pw);
        }

        try
        {
            if (!VaultFile.NeedsPassword(path))
                return VaultFile.Open(path, protector, null);

            VaultFile? opened = null;
            string? entered = PromptDialog.AskPassword(null, "RbxSwitcher — desbloquear", "Senha mestra:", null, p =>
            {
                try { opened = VaultFile.Open(path, protector, p); return null; }
                catch (WrongPasswordException) { return "Senha incorreta."; }
            });
            return entered is null ? null : opened;
        }
        catch (VaultCorruptException e)
        {
            Log.Error("Vault could not be opened", e);
            ShowError(e.Message + "\n\nO arquivo não foi alterado: " + path);
            return null;
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(Redact.Text(message), "RbxSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
