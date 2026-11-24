using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Updates;

/// <summary>A newer build published as a GitHub release of the repository the person configured.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri ExeUrl, Uri ShaUrl, Uri PageUrl);

/// <summary>
/// Reads "releases/latest" of the configured repository. Only github.com download links are accepted;
/// the executable must come with a ".sha256" asset, checked before anything is installed.
/// </summary>
public static partial class Releases
{
    public const string ExeAsset = "RbxSwitcher.exe";
    public const string ShaAsset = "RbxSwitcher.exe.sha256";

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\b([0-9a-fA-F]{64})\b")]
    private static partial Regex Sha256Pattern();

    /// <summary>"owner/repo" on GitHub; anything else (URLs, spaces, ..) is refused.</summary>
    public static bool IsValidRepo(string? repo) => repo is not null && RepoPattern().IsMatch(repo) && !repo.Contains("..");

    public static Uri LatestApi(string repo) =>
        IsValidRepo(repo) ? new Uri($"https://api.github.com/repos/{repo}/releases/latest") : throw new ArgumentException("repositório inválido", nameof(repo));

    public static Version? ParseTag(string? tag) =>
        tag is not null && TagPattern().Match(tag.Trim()) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    /// <returns>The release if it is usable (stable, versioned tag, both assets on github.com), else null.</returns>
    public static ReleaseInfo? ParseLatest(JsonElement r, string repo)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        string? tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (ParseTag(tag) is not { } version) return null;
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        Uri? exe = null, sha = null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null || !IsRepoDownload(url, repo, out var uri)) continue;
            if (name == ExeAsset) exe = uri;
            else if (name == ShaAsset) sha = uri;
        }
        if (exe is null || sha is null) return null;
        var page = new Uri($"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(tag!)}");
        return new ReleaseInfo(version, tag!, exe, sha, page);
    }

    /// <summary>Only https://github.com/{repo}/releases/download/… of the configured repository.</summary>
    public static bool IsRepoDownload(string url, string repo, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parsed.AbsolutePath.StartsWith($"/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>Hosts a github.com release download may redirect to.</summary>
    public static bool IsAllowedDownloadHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>The 64-hex SHA-256 in a ".sha256" file (formats "HASH", "HASH  RbxSwitcher.exe", PowerShell output).</summary>
    public static string? ParseSha256(string text) =>
        Sha256Pattern().Match(text) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    public static bool IsNewer(Version current, Version candidate) =>
        candidate > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
