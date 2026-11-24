using System.Net;
using System.Text;
using System.Text.Json;
using RbxSwitcher.Core.Avatars;

namespace RbxSwitcher.Core.Roblox;

public enum WriteOutcome { Ok, Unauthorized, RateLimited, Challenge, Refused, Failed }

public sealed record WriteResult<T>(WriteOutcome Outcome, T? Value, string? Problem = null, TimeSpan? RetryAfter = null, int? Http = null)
{
    public bool Ok => Outcome == WriteOutcome.Ok;
    /// <summary>The JSON body of a refusal (no session data), for callers that read structured errors.</summary>
    public JsonElement? ErrorBody { get; init; }
}

/// <summary>
/// One account's session during a series of calls: Roblox may rotate the cookie mid-way and the CSRF token is
/// reused between POSTs. <see cref="Rotated"/> is the new cookie to save, if any. Never logged.
/// </summary>
public sealed class SessionContext(string session)
{
    public string Session { get; private set; } = session;
    public string? Rotated { get; private set; }
    internal string? Csrf { get; set; }

    internal void Adopt(string? fresh)
    {
        if (fresh is null || fresh == Session) return;
        Session = fresh;
        Rotated = fresh;
    }

    public override string ToString() => "SessionContext";
}

/// <summary>
/// Avatar, catalog and inventory endpoints for the avatar builder. Writes (wear, colours, avatar type, free
/// item) are POSTs to *.roblox.com with the account's own cookie and a CSRF token, only after a click.
/// The only purchase is of items whose price is exactly 0: the request says expectedPrice 0, so Roblox refuses
/// it if the price is anything else, and no Robux can be spent.
/// </summary>
public sealed partial class RobloxApi
{
    internal const string CatalogSearchV2 = "https://catalog.roblox.com/v2/search/items/details";
    internal const string CatalogSearchV1 = "https://catalog.roblox.com/v1/search/items/details";
    internal const string BundleDetailsUrl = "https://catalog.roblox.com/v1/bundles/details?bundleIds=";
    internal const string ItemDetailsUrl = "https://catalog.roblox.com/v1/catalog/items/details";
    internal const string InventoryUrl = "https://inventory.roblox.com/v2/users/{0}/inventory/{1}?limit=100&sortOrder=Desc";
    internal const string OwnedBundlesUrl = "https://catalog.roblox.com/v1/users/{0}/bundles?limit=100&sortOrder=Desc";
    internal const string IsOwnedUrl = "https://inventory.roblox.com/v1/users/{0}/items/{1}/{2}/is-owned";
    internal const string AvatarUrl = "https://avatar.roblox.com/v1/avatar";
    internal const string AvatarRulesUrl = "https://avatar.roblox.com/v1/avatar-rules";
    private static readonly TimeSpan RulesLifetime = TimeSpan.FromHours(6);
    private (AvatarRules Rules, DateTimeOffset At)? _avatarRules;
    internal const string WearV2 = "https://avatar.roblox.com/v2/avatar/set-wearing-assets";
    internal const string WearV1 = "https://avatar.roblox.com/v1/avatar/set-wearing-assets";
    internal const string ColorsV2 = "https://avatar.roblox.com/v2/avatar/set-body-colors";
    internal const string ColorsV1 = "https://avatar.roblox.com/v1/avatar/set-body-colors";
    internal const string AvatarTypeUrl = "https://avatar.roblox.com/v1/avatar/set-player-avatar-type";
    internal const string PurchaseUrl = "https://economy.roblox.com/v1/purchases/products/{0}";
    internal const string CollectibleDetailsUrl = "https://apis.roblox.com/marketplace-items/v1/items/details";
    internal const string CollectiblePurchaseUrl = "https://apis.roblox.com/marketplace-sales/v1/item/{0}/purchase-item";
    internal const string AssetThumbsUrl = "https://thumbnails.roblox.com/v1/assets?assetIds={0}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
    internal const string BundleThumbsUrl = "https://thumbnails.roblox.com/v1/bundles/thumbnails?bundleIds={0}&size=150x150&format=Png&isCircular=false";
    internal const string AvatarThumbUrl = "https://thumbnails.roblox.com/v1/users/avatar?userIds={0}&size=420x420&format=Png&isCircular=false";
    internal const int MaxSearchPages = 8, MaxInventoryPages = 5;

    /// <summary>Catalog searches (legacy Category/Subcategory ids): accessories, clothing, body parts, bundles. Results
    /// are sorted into the builder's categories by asset/bundle type, so an unexpected subcategory cannot misfile items;
    /// overlapping searches only repeat items, which are merged.</summary>
    internal static readonly (int Category, int Subcategory)[] FreeSearches = [(11, 19), (3, 3), (4, 4), (1, 37)];

    // ───────────── catalog (public) ─────────────

    /// <summary>
    /// Free items made by Roblox, from the official catalog search (no session sent). Bundles come back with
    /// their parts filled in. A search that fails is reported; the others still count.
    /// </summary>
    public async Task<ReadResult<List<AvatarItem>>> GetFreeCatalogAsync(SessionContext? forAssetTypes, CancellationToken ct = default)
    {
        var all = new Dictionary<string, AvatarItem>();
        var problems = new List<string>();
        foreach (var (cat, sub) in FreeSearches)
        {
            string query = $"?Category={cat}&Subcategory={sub}&CreatorName=Roblox&MinPrice=0&MaxPrice=0&SalesTypeFilter=1&Limit=120";
            var res = await SearchAllPagesAsync(CatalogSearchV2 + query, ct).ConfigureAwait(false);
            if (!res.Ok && res.Http is 400 or 404 or 410)
                res = await SearchAllPagesAsync(CatalogSearchV1 + query, ct).ConfigureAwait(false);
            if (!res.Ok) { problems.Add($"busca {cat}/{sub}: {res.Problem}"); if (res.Outcome == ReadOutcome.RateLimited) return new(res.Outcome, null, RetryAfter: res.RetryAfter, Problem: res.Problem); continue; }
            foreach (var it in res.Value!) if (it.Free) all[it.Key] = it;
        }
        if (all.Count == 0 && problems.Count > 0)
            return new ReadResult<List<AvatarItem>>(ReadOutcome.Failed, null, Problem: string.Join("; ", problems));
        var items = await FillBundlesAsync(all.Values.ToList(), forAssetTypes, ct).ConfigureAwait(false);
        foreach (var p in problems) Logging.Log.Warn("[catálogo] " + p);
        return new ReadResult<List<AvatarItem>>(ReadOutcome.Ok, items, Problem: problems.Count > 0 ? string.Join("; ", problems) : null);
    }

    private async Task<ReadResult<List<AvatarItem>>> SearchAllPagesAsync(string baseUrl, CancellationToken ct)
    {
        var items = new List<AvatarItem>();
        string? cursor = null;
        var seen = new HashSet<string>();
        for (int page = 0; page < MaxSearchPages; page++)
        {
            string url = cursor is null ? baseUrl : baseUrl + "&Cursor=" + Uri.EscapeDataString(cursor);
            var res = await GetPublicJsonAsync(url, AvatarParsers.SearchPage, "catálogo", ct).ConfigureAwait(false);
            if (!res.Ok) return page == 0 ? new(res.Outcome, null, RetryAfter: res.RetryAfter, Problem: res.Problem) { Http = res.Http } : new(ReadOutcome.Ok, items);
            items.AddRange(res.Value.Items);
            cursor = res.Value.Next;
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor)) break;
            await Task.Delay(250, ct).ConfigureAwait(false); // gentle with the public search
        }
        return new ReadResult<List<AvatarItem>>(ReadOutcome.Ok, items);
    }

    /// <summary>Adds each bundle's parts (and their asset types, when a session can ask for them).</summary>
    public async Task<List<AvatarItem>> FillBundlesAsync(List<AvatarItem> items, SessionContext? forAssetTypes, CancellationToken ct = default)
    {
        var bundles = items.Where(i => i.Kind == ItemKind.Bundle && i.BundleAssets is null).Select(i => i.Id).ToList();
        if (bundles.Count == 0) return items;
        var details = new Dictionary<long, BundleInfo>();
        foreach (var chunk in bundles.Chunk(50))
        {
            var res = await GetPublicJsonAsync(BundleDetailsUrl + string.Join(',', chunk), AvatarParsers.BundleDetails, "pacotes", ct).ConfigureAwait(false);
            if (res.Ok) foreach (var b in res.Value!) details[b.Id] = b;
        }
        var types = new Dictionary<long, int>();
        var assetIds = details.Values.SelectMany(b => b.AssetIds).Distinct().ToList();
        if (forAssetTypes is not null)
            foreach (var chunk in assetIds.Chunk(100))
            {
                var body = JsonSerializer.Serialize(new { items = chunk.Select(id => new { itemType = "Asset", id }) });
                var res = await PostJsonAsync(ItemDetailsUrl, forAssetTypes, body, AvatarParsers.ItemAssetTypes, ct).ConfigureAwait(false);
                if (res.Ok) foreach (var kv in res.Value!) types[kv.Key] = kv.Value;
            }
        return items.Select(i => i.Kind == ItemKind.Bundle && details.TryGetValue(i.Id, out var b)
            ? i with { BundleAssets = b.AssetIds.ToDictionary(id => id, id => types.GetValueOrDefault(id)), ProductId = i.ProductId ?? b.ProductId,
                       CollectibleItemId = i.CollectibleItemId ?? b.CollectibleItemId }
            : i).ToList();
    }

    /// <summary>150×150 thumbnail URLs (Roblox CDN only) for catalog items, by item key.</summary>
    public async Task<Dictionary<string, string>> GetItemThumbnailUrlsAsync(IEnumerable<AvatarItem> items, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var list = items.ToList();
        foreach (var (kind, template, prefix) in new[] { (ItemKind.Asset, AssetThumbsUrl, "a"), (ItemKind.Bundle, BundleThumbsUrl, "b") })
            foreach (var chunk in list.Where(i => i.Kind == kind).Select(i => i.Id).Distinct().Chunk(100))
            {
                var res = await GetPublicJsonAsync(string.Format(template, string.Join(',', chunk)), ThumbUrls, null, ct).ConfigureAwait(false);
                if (res.Ok) foreach (var kv in res.Value!) result[prefix + kv.Key] = kv.Value;
            }
        return result;
    }

    /// <summary>Full-body picture of an account's avatar (public).</summary>
    public async Task<string?> GetAvatarImageUrlAsync(long userId, CancellationToken ct = default)
    {
        var res = await GetPublicJsonAsync(string.Format(AvatarThumbUrl, userId), ThumbUrls, null, ct).ConfigureAwait(false);
        return res.Ok && res.Value!.TryGetValue(userId, out var u) ? u : null;
    }

    internal static Dictionary<long, string> ThumbUrls(JsonElement r)
    {
        var map = new Dictionary<long, string>();
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException("miniaturas: campo data ausente");
        foreach (var e in data.EnumerateArray())
            if (AvatarParsers.Long(e, "targetId") is { } id && AvatarParsers.Str(e, "state") == "Completed" &&
                AvatarParsers.Str(e, "imageUrl") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var p) &&
                p.Scheme == Uri.UriSchemeHttps && p.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
                map[id] = u;
        return map;
    }

    // ───────────── the account's own items and avatar ─────────────

    /// <summary>Items of the builder's categories this account owns (inventory pages per asset type, then bundles).</summary>
    public async Task<ReadResult<List<AvatarItem>>> GetOwnedItemsAsync(SessionContext s, long userId, CancellationToken ct = default)
    {
        var items = new List<AvatarItem>();
        foreach (int type in AvatarCategories.All.SelectMany(d => d.AssetTypes).Distinct())
        {
            string? cursor = null;
            for (int page = 0; page < MaxInventoryPages; page++)
            {
                string url = string.Format(InventoryUrl, userId, type) + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
                var res = await GetAuthJsonAsync(url, s, AvatarParsers.InventoryPage, ct).ConfigureAwait(false);
                if (res.Outcome is ReadOutcome.Unauthorized or ReadOutcome.RateLimited) return new(res.Outcome, null, RetryAfter: res.RetryAfter, Problem: res.Problem);
                if (!res.Ok) break; // one type failing (e.g. a type Roblox no longer lists) does not hide the others
                items.AddRange(res.Value.Items.Select(x => new AvatarItem(ItemKind.Asset, x.Id, x.Name, type, 0, null, null, false)));
                cursor = res.Value.Next;
                if (string.IsNullOrEmpty(cursor)) break;
            }
        }
        string? bcursor = null;
        var bundles = new List<AvatarItem>();
        for (int page = 0; page < MaxInventoryPages; page++)
        {
            string url = string.Format(OwnedBundlesUrl, userId) + (bcursor is null ? "" : "&cursor=" + Uri.EscapeDataString(bcursor));
            var res = await GetAuthJsonAsync(url, s, AvatarParsers.OwnedBundlesPage, ct).ConfigureAwait(false);
            if (!res.Ok) break;
            bundles.AddRange(res.Value.Items.Select(x => new AvatarItem(ItemKind.Bundle, x.Id, x.Name, 0, x.BundleType, null, null, false))
                .Where(b => b.Category is not null));
            bcursor = res.Value.Next;
            if (string.IsNullOrEmpty(bcursor)) break;
        }
        items.AddRange(await FillBundlesAsync(bundles, s, ct).ConfigureAwait(false));
        return new ReadResult<List<AvatarItem>>(ReadOutcome.Ok, items.DistinctBy(i => i.Key).ToList());
    }

    public Task<ReadResult<CurrentAvatar>> GetCurrentAvatarAsync(SessionContext s, CancellationToken ct = default) =>
        GetAuthJsonAsync(AvatarUrl, s, AvatarParsers.Avatar, ct);

    public Task<ReadResult<bool>> IsOwnedAsync(SessionContext s, long userId, ItemRef item, CancellationToken ct = default) =>
        GetAuthJsonAsync(string.Format(IsOwnedUrl, userId, item.Kind == ItemKind.Bundle ? "Bundle" : "Asset", item.Id), s, AvatarParsers.IsOwned, ct);

    // ───────────── writes ─────────────

    /// <summary>
    /// Gets a free item for the account. Refused locally unless the catalog marked it free (made by Roblox,
    /// price 0, on sale, not limited); the request itself says expectedPrice 0.
    /// </summary>
    public async Task<WriteResult<PurchaseOutcome>> AcquireFreeAsync(SessionContext s, long userId, AvatarItem item, CancellationToken ct = default)
    {
        if (!item.Free) return new(WriteOutcome.Refused, default, "o item não é grátis; o RbxSwitcher não compra itens pagos");
        // Since the 2024 Marketplace migration Roblox sells its items (free ones too) through marketplace-sales; the old
        // economy "purchases/products" answers InvalidArguments for them. So: the marketplace first (finding the item's
        // marketplace id when the catalog search did not give it), the old endpoint only as a last resort.
        string? cid = item.CollectibleItemId ?? await CollectibleIdAsync(s, item, ct).ConfigureAwait(false);
        WriteResult<PurchaseOutcome>? viaMarket = null;
        if (cid is not null)
        {
            viaMarket = await AcquireCollectibleAsync(s, userId, cid, ct).ConfigureAwait(false);
            Logging.Log.Info($"[avatar] pegar grátis via marketplace: {viaMarket.Outcome} (HTTP {viaMarket.Http?.ToString() ?? "-"})");
            if (viaMarket.Ok || viaMarket.Outcome is WriteOutcome.Unauthorized or WriteOutcome.RateLimited or WriteOutcome.Challenge || item.ProductId is null)
                return viaMarket;
        }
        if (item.ProductId is { } productId)
        {
            string body = JsonSerializer.Serialize(new { expectedCurrency = 1, expectedPrice = 0, expectedSellerId = AvatarParsers.RobloxCreatorId });
            var legacy = Purchase(await PostJsonAsync(string.Format(PurchaseUrl, productId), s, body, AvatarParsers.Purchase, ct).ConfigureAwait(false));
            Logging.Log.Info($"[avatar] pegar grátis via economy: {legacy.Outcome} (HTTP {legacy.Http?.ToString() ?? "-"})");
            // When both refused, the marketplace answer is the meaningful one.
            return legacy.Ok || viaMarket is null ? legacy : viaMarket;
        }
        return viaMarket ?? new(WriteOutcome.Refused, default, "o Roblox não informou como pegar este item");
    }

    private async Task<WriteResult<PurchaseOutcome>> AcquireCollectibleAsync(SessionContext s, long userId, string cid, CancellationToken ct)
    {
        string detailsBody = JsonSerializer.Serialize(new { itemIds = new[] { cid } });
        var details = await PostJsonAsync(CollectibleDetailsUrl, s, detailsBody, r => AvatarParsers.CollectibleProduct(r, cid), ct).ConfigureAwait(false);
        if (!details.Ok) return new(details.Outcome, default, "detalhes do item no Marketplace: " + details.Problem, details.RetryAfter, details.Http);
        if (details.Value is not { } product) return new(WriteOutcome.Refused, default, "o Marketplace não informou o produto do item");
        // A known price other than 0 stops here; an unknown one is still safe: the purchase says expectedPrice 0.
        if (product.Price is { } p && p != 0) return new(WriteOutcome.Refused, default, "o preço do item não é mais 0");
        string body = JsonSerializer.Serialize(new
        {
            collectibleItemId = cid, collectibleProductId = product.ProductId, expectedCurrency = 1, expectedPrice = 0,
            expectedPurchaserId = userId.ToString(), expectedPurchaserType = "User",
            expectedSellerId = AvatarParsers.RobloxCreatorId, expectedSellerType = "User", idempotencyKey = Guid.NewGuid().ToString(),
        });
        return Purchase(await PostJsonAsync(string.Format(CollectiblePurchaseUrl, Uri.EscapeDataString(cid)), s, body, AvatarParsers.Purchase, ct).ConfigureAwait(false));
    }

    /// <summary>The item's marketplace id from catalog item details (it is missing from some search results).</summary>
    private async Task<string?> CollectibleIdAsync(SessionContext s, AvatarItem item, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new { items = new[] { new { itemType = item.Kind == ItemKind.Bundle ? "Bundle" : "Asset", id = item.Id } } });
        var res = await PostJsonAsync(ItemDetailsUrl, s, body, r => AvatarParsers.CollectibleIdOf(r, item.Id), ct).ConfigureAwait(false);
        return res.Ok ? res.Value : null;

    }

    private static WriteResult<PurchaseOutcome> Purchase(WriteResult<(PurchaseOutcome Outcome, string? Reason)> r) =>
        !r.Ok ? new(r.Outcome, default, r.Problem, r.RetryAfter, r.Http)
        : r.Value.Outcome == PurchaseOutcome.Refused ? new(WriteOutcome.Refused, PurchaseOutcome.Refused, "o Roblox não entregou o item (" + r.Value.Reason + ")", null, r.Http)
        : new(WriteOutcome.Ok, r.Value.Outcome, null, null, r.Http);

    /// <summary>
    /// How many of each type an avatar can wear (public, no cookie). Cached for 6 h; when Roblox does not answer,
    /// <see cref="AvatarRules.Default"/> (only the limits known for sure).
    /// </summary>
    public async Task<AvatarRules> GetAvatarRulesAsync(CancellationToken ct = default)
    {
        if (_avatarRules is { } c && DateTimeOffset.UtcNow - c.At < RulesLifetime) return c.Rules;
        var r = await GetPublicJsonAsync(AvatarRulesUrl, AvatarParsers.Rules, "regras do avatar", ct).ConfigureAwait(false);
        if (!r.Ok) { Logging.Log.Warn("[avatar] regras do avatar não lidas: " + r.Problem); return AvatarRules.Default; }
        _avatarRules = (r.Value!, DateTimeOffset.UtcNow);
        return r.Value!;
    }

    /// <summary>Replaces everything the account wears. Returns the ids Roblox refused (not owned, wrong type…).</summary>
    public async Task<WriteResult<IReadOnlyList<long>>> SetWearingAssetsAsync(SessionContext s, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        var v2 = await PostJsonAsync(WearV2, s, JsonSerializer.Serialize(new { assets = assetIds.Select(id => new { id }) }), AvatarParsers.InvalidAssetIds, ct).ConfigureAwait(false);
        var r = v2.Http is 404 or 405 ? await PostJsonAsync(WearV1, s, JsonSerializer.Serialize(new { assetIds }), AvatarParsers.InvalidAssetIds, ct).ConfigureAwait(false) : v2;
        // A validation refusal reads as a reason, not as Roblox's JSON.
        if (!r.Ok && r.ErrorBody is { } body && AvatarParsers.WearValidation(body) is { Count: > 0 } errors)
            return r with { Problem = $"o Roblox recusou a combinação (HTTP {r.Http}): {AvatarParsers.DescribeValidation(errors)}" };
        return r;
    }

    public async Task<WriteResult<bool>> SetSkinToneAsync(SessionContext s, SkinTone tone, CancellationToken ct = default)
    {
        string hex = tone.Hex;
        var v2 = await PostJsonAsync(ColorsV2, s, JsonSerializer.Serialize(new
        {
            headColor3 = hex, torsoColor3 = hex, rightArmColor3 = hex, leftArmColor3 = hex, rightLegColor3 = hex, leftLegColor3 = hex,
        }), _ => true, ct).ConfigureAwait(false);
        if (v2.Http is not (404 or 405)) return v2;
        int id = tone.BrickColorId;
        return await PostJsonAsync(ColorsV1, s, JsonSerializer.Serialize(new
        {
            headColorId = id, torsoColorId = id, rightArmColorId = id, leftArmColorId = id, rightLegColorId = id, leftLegColorId = id,
        }), _ => true, ct).ConfigureAwait(false);
    }

    public Task<WriteResult<bool>> SetAvatarTypeAsync(SessionContext s, string type, CancellationToken ct = default) =>
        type is "R15" or "R6"
            ? PostJsonAsync(AvatarTypeUrl, s, JsonSerializer.Serialize(new { playerAvatarType = type }), _ => true, ct)
            : Task.FromResult(new WriteResult<bool>(WriteOutcome.Refused, false, "tipo de avatar desconhecido"));

    // ───────────── HTTP helpers ─────────────

    private static Uri RobloxUri(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only https://*.roblox.com is called.");
        return uri;
    }

    /// <summary>GET without any cookie (public catalog and thumbnails).</summary>
    private async Task<ReadResult<T>> GetPublicJsonAsync<T>(string url, Func<JsonElement, T> parse, string? tag, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, RobloxUri(url));
        return await SendReadAsync(req, null, parse, tag, ct).ConfigureAwait(false);
    }

    private async Task<ReadResult<T>> GetAuthJsonAsync<T>(string url, SessionContext s, Func<JsonElement, T> parse, CancellationToken ct)
    {
        if (!LooksLikeSession(s.Session)) return new(ReadOutcome.Unauthorized, default, Problem: "sessão inválida");
        using var req = new HttpRequestMessage(HttpMethod.Get, RobloxUri(url));
        req.Headers.Add("Cookie", $"{CookieName}={s.Session}");
        return await SendReadAsync(req, s, parse, null, ct).ConfigureAwait(false);
    }

    private async Task<ReadResult<T>> SendReadAsync<T>(HttpRequestMessage req, SessionContext? s, Func<JsonElement, T> parse, string? tag, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(ReadOutcome.Failed, default, Problem: "sem conexão com o Roblox");
        }
        using (resp)
        {
            if (s is not null) s.Adopt(ReadRotatedSession(resp.Headers, s.Session));
            int code = (int)resp.StatusCode;
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                if (tag is not null) Logging.Log.Warn($"[{tag}] HTTP {code}");
                return resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new(ReadOutcome.Unauthorized, default, Problem: "sessão recusada (HTTP 401)") { Http = code },
                    HttpStatusCode.TooManyRequests => new(ReadOutcome.RateLimited, default, RetryAfter: resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30),
                        Problem: "limite de requisições do Roblox (HTTP 429)") { Http = code },
                    _ => new(ReadOutcome.Failed, default, Problem: $"Roblox respondeu HTTP {code}") { Http = code },
                };
            }
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                try { return new(ReadOutcome.Ok, parse(doc.RootElement)) { Http = code }; }
                catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    Logging.Log.Warn($"[{tag ?? req.RequestUri!.AbsolutePath.Split('/').Take(3).LastOrDefault()}] HTTP 200; campos: {Logging.JsonShape.Describe(doc.RootElement)}");
                    return new(ReadOutcome.Failed, default, Problem: e is FormatException ? e.Message : "estrutura inesperada") { Http = code };
                }
            }
            catch (JsonException) { return new(ReadOutcome.Failed, default, Problem: "resposta não é JSON") { Http = code }; }
        }
    }

    /// <summary>POST JSON with the account's cookie, Origin/Referer of roblox.com and the CSRF token (fetched on the first 403).</summary>
    private async Task<WriteResult<T>> PostJsonAsync<T>(string url, SessionContext s, string json, Func<JsonElement, T> parse, CancellationToken ct)
    {
        if (!LooksLikeSession(s.Session)) return new(WriteOutcome.Unauthorized, default, "sessão inválida");
        var uri = RobloxUri(url);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("Cookie", $"{CookieName}={s.Session}");
            req.Headers.Add("Origin", Website);
            req.Headers.Referrer = new Uri(Website + "/");
            if (s.Csrf is not null) req.Headers.Add("x-csrf-token", s.Csrf);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new(WriteOutcome.Failed, default, "sem conexão com o Roblox");
            }
            using (resp)
            {
                s.Adopt(ReadRotatedSession(resp.Headers, s.Session));
                int code = (int)resp.StatusCode;
                if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.TryGetValues("x-csrf-token", out var tok) && attempt == 0 &&
                    tok.FirstOrDefault() is { Length: > 0 } fresh && fresh != s.Csrf)
                {
                    s.Csrf = fresh;
                    continue;
                }
                if (resp.Headers.Contains("rblx-challenge-id") || resp.Headers.Contains("rblx-challenge-type"))
                    return new(WriteOutcome.Challenge, default, "o Roblox pediu uma verificação (captcha); faça esta etapa pelo site nesta conta", null, code);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) return new(WriteOutcome.Unauthorized, default, "sessão recusada (HTTP 401)", null, code);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    return new(WriteOutcome.RateLimited, default, "limite de requisições do Roblox (HTTP 429)", resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30), code);
                string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string? why = null;
                    JsonElement? body = null;
                    try
                    {
                        if (text.Length is > 0 and < 16_384)
                        {
                            using var d = JsonDocument.Parse(text);
                            why = EconomyParsers.ErrorReason(d.RootElement);
                            body = d.RootElement.Clone();
                        }
                    }
                    catch (JsonException) { }
                    Logging.Log.Warn($"[avatar] POST {uri.AbsolutePath.Split('/').Last()} HTTP {code}");
                    return new(code is 400 or 403 or 409 ? WriteOutcome.Refused : WriteOutcome.Failed, default,
                        $"o Roblox recusou (HTTP {code})" + (why is null ? "" : $": {why}"), null, code) { ErrorBody = body };
                }
                try
                {
                    using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                    return new(WriteOutcome.Ok, parse(doc.RootElement), null, null, code);
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    return new(WriteOutcome.Failed, default, e is FormatException ? e.Message : "resposta inesperada", null, code);
                }
            }
        }
        return new(WriteOutcome.Refused, default, "o Roblox não aceitou o token de segurança (CSRF)", null, 403);
    }
}
