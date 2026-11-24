using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App.UI;

/// <summary>
/// Avatar headshots in memory only (nothing on disk, so the PC holds no unencrypted list of
/// which accounts you have). Public images: no session is ever sent to fetch them.
/// </summary>
internal sealed class AvatarCache : IDisposable
{
    private readonly RobloxApi _api;
    private readonly Dictionary<long, Image> _images = new();
    private readonly HashSet<long> _pending = new();

    public event Action<long>? Loaded;

    public AvatarCache(RobloxApi api) => _api = api;

    public Image? Get(long userId) => _images.GetValueOrDefault(userId);

    /// <summary>Fetches missing avatars. Call from the UI thread; <see cref="Loaded"/> fires on it too.</summary>
    public async Task LoadAsync(IEnumerable<long> userIds)
    {
        var wanted = userIds.Where(id => !_images.ContainsKey(id) && _pending.Add(id)).ToList();
        if (wanted.Count == 0) return;
        try
        {
            var urls = await _api.GetHeadshotUrlsAsync(wanted);
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(urls.Select(async kv =>
            {
                await gate.WaitAsync();
                try
                {
                    byte[]? bytes = await _api.DownloadImageAsync(kv.Value);
                    if (bytes is null) return;
                    Image img;
                    try { img = Image.FromStream(new MemoryStream(bytes)); }
                    catch (ArgumentException) { return; }
                    _images[kv.Key] = img; // continuation resumes on the UI thread (WinForms sync context)
                    Loaded?.Invoke(kv.Key);
                }
                finally { gate.Release(); }
            }));
        }
        finally
        {
            foreach (var id in wanted) _pending.Remove(id);
        }
    }

    public void Forget(long userId)
    {
        if (_images.Remove(userId, out var img)) img.Dispose();
    }

    public void Dispose()
    {
        foreach (var img in _images.Values) img.Dispose();
        _images.Clear();
    }
}
