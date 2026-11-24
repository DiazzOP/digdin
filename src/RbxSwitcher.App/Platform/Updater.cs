using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Updates;

namespace RbxSwitcher.App.Platform;

/// <summary>
/// Optional in-app update from a GitHub repository the person configured (⚙ → Atualizações).
/// No cookie or account data is ever sent: this client is separate from RobloxApi and talks only to
/// github.com. The new executable is installed only if its SHA-256 matches the published ".sha256".
/// </summary>
internal sealed class Updater : IDisposable
{
    private readonly HttpClient _http;

    public Updater()
    {
        var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RbxSwitcher-Updater");
    }

    public static Version Current
    {
        get
        {
            string? info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Releases.ParseTag(info?.Split('+')[0]) ?? new Version(0, 0, 0);
        }
    }

    public async Task<ReleaseInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Releases.LatestApi(repo));
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"[atualização] GitHub respondeu HTTP {(int)resp.StatusCode}");
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var info = Releases.ParseLatest(doc.RootElement, repo);
        return info is not null && Releases.IsNewer(Current, info.Version) ? info : null;
    }

    /// <summary>Downloads, verifies and swaps the executable, then starts the new version. Returns false if anything did not check out.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, "Não foi possível localizar o executável atual.");
        string dir = Path.GetDirectoryName(exePath)!;
        string staged = Path.Combine(dir, "RbxSwitcher.exe.new");
        string old = exePath + ".old";
        try
        {
            string? expected = Releases.ParseSha256(System.Text.Encoding.UTF8.GetString(await DownloadAsync(release.ShaUrl, 4096, ct)));
            if (expected is null) return (false, "O arquivo .sha256 da versão não tem um hash válido.");

            byte[] exe = await DownloadAsync(release.ExeUrl, 400L * 1024 * 1024, ct);
            string actual = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
            {
                Log.Warn("[atualização] hash não confere; nada foi instalado");
                return (false, "O arquivo baixado não confere com o hash publicado. Nada foi instalado.");
            }

            await File.WriteAllBytesAsync(staged, exe, ct);
            if (File.Exists(old)) File.Delete(old);
            File.Move(exePath, old);          // Windows allows renaming the running executable
            File.Move(staged, exePath);
            Log.Info($"[atualização] instalada {release.Tag}");
            Process.Start(new ProcessStartInfo(exePath, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = dir });
            return (true, $"Versão {release.Tag} instalada.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log.Error("Update failed", e);
            // Put the current version back if the swap was half done.
            try { if (!File.Exists(exePath) && File.Exists(old)) File.Move(old, exePath); } catch { }
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            return (false, "Não foi possível instalar a atualização: " + e.Message);
        }
    }

    /// <summary>GET following redirects by hand, only to GitHub's download hosts, with a size limit.</summary>
    private async Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken ct)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            if (!Releases.IsAllowedDownloadHost(url)) throw new InvalidOperationException("download fora do GitHub recusado");
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                url = resp.Headers.Location is { IsAbsoluteUri: true } loc ? loc : new Uri(url, resp.Headers.Location!);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("arquivo grande demais");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > maxBytes) throw new InvalidOperationException("arquivo grande demais");
            return bytes;
        }
        throw new InvalidOperationException("redirecionamentos demais");
    }

    /// <summary>Removes the previous version left by an update (it could not be deleted while running).</summary>
    public static void CleanUpOldVersion()
    {
        try
        {
            if (Environment.ProcessPath is { } p && File.Exists(p + ".old")) File.Delete(p + ".old");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
