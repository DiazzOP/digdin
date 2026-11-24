using System.Text.Json;
using RbxSwitcher.Core.Updates;

namespace RbxSwitcher.Core.Tests;

public class UpdateTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private const string Good = """
        {"tag_name":"v0.3.3","draft":false,"prerelease":false,"assets":[
          {"name":"RbxSwitcher.exe","browser_download_url":"https://github.com/someone/rbx-switcher/releases/download/v0.3.3/RbxSwitcher.exe"},
          {"name":"RbxSwitcher.exe.sha256","browser_download_url":"https://github.com/someone/rbx-switcher/releases/download/v0.3.3/RbxSwitcher.exe.sha256"}]}
        """;

    [Fact]
    public void Reads_a_usable_release()
    {
        var r = Releases.ParseLatest(J(Good), "someone/rbx-switcher")!;
        Assert.Equal(new Version(0, 3, 3), r.Version);
        Assert.EndsWith("/RbxSwitcher.exe", r.ExeUrl.AbsoluteUri);
        Assert.True(Releases.IsNewer(new Version(0, 3, 2, 0), r.Version));
        Assert.False(Releases.IsNewer(new Version(0, 3, 3, 0), r.Version));
    }

    [Theory]
    [InlineData("https://evil.example/someone/rbx-switcher/releases/download/v0.3.3/RbxSwitcher.exe")]
    [InlineData("http://github.com/someone/rbx-switcher/releases/download/v0.3.3/RbxSwitcher.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v0.3.3/RbxSwitcher.exe")]
    public void Downloads_outside_the_configured_repository_are_refused(string url)
    {
        string json = Good.Replace("https://github.com/someone/rbx-switcher/releases/download/v0.3.3/RbxSwitcher.exe\"", url + "\"");
        Assert.Null(Releases.ParseLatest(J(json), "someone/rbx-switcher"));
    }

    [Fact]
    public void Drafts_prereleases_odd_tags_and_missing_hash_are_ignored()
    {
        Assert.Null(Releases.ParseLatest(J(Good.Replace("\"prerelease\":false", "\"prerelease\":true")), "someone/rbx-switcher"));
        Assert.Null(Releases.ParseLatest(J(Good.Replace("v0.3.3\",\"draft", "nightly\",\"draft")), "someone/rbx-switcher"));
        Assert.Null(Releases.ParseLatest(J(Good.Replace("RbxSwitcher.exe.sha256\",", "notes.txt\",")), "someone/rbx-switcher"));
    }

    [Theory]
    [InlineData("someone/rbx-switcher", true)]
    [InlineData("https://github.com/someone/rbx", false)]
    [InlineData("someone/../etc", false)]
    [InlineData("a b/c", false)]
    [InlineData("", false)]
    public void Repo_names_are_validated(string repo, bool ok) => Assert.Equal(ok, Releases.IsValidRepo(repo));

    [Fact]
    public void Sha256_file_formats()
    {
        string h = new string('a', 64);
        Assert.Equal(h, Releases.ParseSha256(h));
        Assert.Equal(h, Releases.ParseSha256(h.ToUpperInvariant() + "  RbxSwitcher.exe\n"));
        Assert.Null(Releases.ParseSha256("not a hash"));
    }
}
