using System.Net;
using System.Text.Json;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// Rolimons' public API (values, demand, holds). Its own HTTP client: no cookie, no session and no Roblox data is ever
/// sent; only https://api.rolimons.com is called. Rolimons limits requests, so the app caches what it reads.
/// </summary>
public sealed class RolimonsApi : IDisposable
{
    internal const string ItemDetailsUrl = "https://api.rolimons.com/items/v2/itemdetails";
    internal const string PlayerAssetsUrl = "https://api.rolimons.com/players/v1/playerassets/{0}";
    private readonly HttpClient _http;

    public RolimonsApi(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RbxSwitcher/0.4 (+account manager)");
    }

    public Task<ReadResult<Dictionary<long, (string Name, long? Rap, long? Value, int? Demand)>>> GetItemsAsync(CancellationToken ct = default) =>
        GetAsync(ItemDetailsUrl, LimitedParsers.RolimonsItems, ct);

    public Task<ReadResult<List<long>>> GetHoldsAsync(long userId, CancellationToken ct = default) =>
        GetAsync(string.Format(PlayerAssetsUrl, userId), LimitedParsers.RolimonsHolds, ct);

    private static readonly SemaphoreSlim PlayerGate = new(1, 1);
    private static DateTimeOffset _lastPlayerCall = DateTimeOffset.MinValue;
    /// <summary>Minimum gap between player reads (Rolimons limits them; 11 accounts in a row would be refused).</summary>
    internal static TimeSpan PlayerGap { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The player's copies (userAssetId → assetId) and which are on hold. Spaced out (see <see cref="PlayerGap"/>).</summary>
    public async Task<ReadResult<(Dictionary<long, long> AssetOf, List<long> Holds)>> GetPlayerAsync(long userId, CancellationToken ct = default)
    {
        await PlayerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastPlayerCall + PlayerGap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            return await GetAsync(string.Format(PlayerAssetsUrl, userId), LimitedParsers.RolimonsPlayer, ct).ConfigureAwait(false);
        }
        finally
        {
            _lastPlayerCall = DateTimeOffset.UtcNow;
            PlayerGate.Release();
        }
    }

    private async Task<ReadResult<T>> GetAsync<T>(string url, Func<JsonElement, T> parse, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("api.rolimons.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only https://api.rolimons.com is called.");
        try
        {
            using var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return new(ReadOutcome.RateLimited, default, RetryAfter: resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60), Problem: "Rolimons limitou os pedidos (HTTP 429)") { Http = code };
            if (!resp.IsSuccessStatusCode) return new(ReadOutcome.Failed, default, Problem: $"Rolimons respondeu HTTP {code}") { Http = code };
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                return new(ReadOutcome.Failed, default, Problem: "Rolimons respondeu sem sucesso") { Http = code };
            try { return new(ReadOutcome.Ok, parse(doc.RootElement)) { Http = code }; }
            catch (FormatException e) { return new(ReadOutcome.Failed, default, Problem: e.Message) { Http = code }; }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return new(ReadOutcome.Failed, default, Problem: "sem conexão com a Rolimons");
        }
    }

    public void Dispose() => _http.Dispose();
}
