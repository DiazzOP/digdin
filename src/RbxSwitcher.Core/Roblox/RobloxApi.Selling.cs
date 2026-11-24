using System.Text.Json;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Roblox;

/// <summary>
/// Resale readings for the Limiteds tab: the cheapest listings of an item and which of an account's copies are on sale.
/// GETs only (plus the catalog lookup of an item's Marketplace id): the app never lists, reprices or takes off sale.
/// Classic Limiteds answer on economy.roblox.com; items on the new Marketplace on apis.roblox.com. Both are tried.
/// </summary>
public sealed partial class RobloxApi
{
    internal const string ResellersUrl = "https://economy.roblox.com/v1/assets/{0}/resellers?limit=10";
    internal const string ResellableCopiesUrl = "https://economy.roblox.com/v1/assets/{0}/users/{1}/resellable-copies";
    // Marketplace paths as roblox.com's own item page calls them (recorded 19/11/2026: venda-chamadas.txt).
    internal const string MarketResellersUrl = "https://apis.roblox.com/marketplace-sales/v1/item/{0}/resellers?limit=100";
    internal const string MarketOwnInstancesUrl = "https://apis.roblox.com/marketplace-sales/v1/item/{0}/resellable-instances?ownerType=User&ownerId={1}&limit=100";
    internal const string MarketResaleDataUrl = "https://apis.roblox.com/marketplace-sales/v1/item/{0}/resale-data";
    internal const int MaxOwnInstancePages = 5;

    /// <summary>The item's Marketplace id (null when it has none, e.g. a classic Limited). Resolved once per item.</summary>
    public async Task<ReadResult<string?>> GetCollectibleIdAsync(SessionContext s, long assetId, CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new { items = new[] { new { itemType = "Asset", id = assetId } } });
        var res = await PostJsonAsync(ItemDetailsUrl, s, body, r => AvatarParsers.CollectibleIdOf(r, assetId), ct).ConfigureAwait(false);
        return res.Ok ? new(ReadOutcome.Ok, res.Value)
            : new(res.Outcome == WriteOutcome.RateLimited ? ReadOutcome.RateLimited : res.Outcome == WriteOutcome.Unauthorized ? ReadOutcome.Unauthorized : ReadOutcome.Failed,
                  null, RetryAfter: res.RetryAfter, Problem: res.Problem) { Http = res.Http };
    }

    /// <summary>Most items one catalog details request takes.</summary>
    public const int CollectibleIdBatch = 50;

    /// <summary>
    /// The Marketplace ids of up to <see cref="CollectibleIdBatch"/> items in one request (null: the item has none). An item
    /// missing from the answer is missing from the dictionary too (not known).
    /// </summary>
    public async Task<ReadResult<Dictionary<long, string?>>> GetCollectibleIdsAsync(SessionContext s, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new { items = assetIds.Take(CollectibleIdBatch).Select(id => new { itemType = "Asset", id }) });
        var res = await PostJsonAsync(ItemDetailsUrl, s, body, AvatarParsers.CollectibleIds, ct).ConfigureAwait(false);
        return res.Ok ? new(ReadOutcome.Ok, res.Value)
            : new(res.Outcome == WriteOutcome.RateLimited ? ReadOutcome.RateLimited : res.Outcome == WriteOutcome.Unauthorized ? ReadOutcome.Unauthorized : ReadOutcome.Failed,
                  null, RetryAfter: res.RetryAfter, Problem: res.Problem) { Http = res.Http };
    }

    /// <summary>The cheapest listings of an item (any account's session can read them): 100 on the Marketplace, 10 classic.</summary>
    public async Task<ReadResult<ListingsPage>> GetResellersAsync(SessionContext s, long assetId, string? collectibleItemId, CancellationToken ct = default)
    {
        if (collectibleItemId is { Length: > 0 } cid)
            return await GetAuthJsonAsync(string.Format(MarketResellersUrl, Uri.EscapeDataString(cid)), s, SellParsers.ResellersPage, ct).ConfigureAwait(false);
        return await GetAuthJsonAsync(string.Format(ResellersUrl, assetId), s, SellParsers.ResellersPage, ct).ConfigureAwait(false);
    }

    /// <summary>The account's copies of an item and which are on sale (the account's own session; every page).</summary>
    public async Task<ReadResult<List<OwnCopy>>> GetOwnCopiesAsync(SessionContext s, long userId, long assetId, string? collectibleItemId, CancellationToken ct = default)
    {
        if (collectibleItemId is not { Length: > 0 } cid)
            return await GetAuthJsonAsync(string.Format(ResellableCopiesUrl, assetId, userId), s, SellParsers.OwnCopies, ct).ConfigureAwait(false);
        var all = new List<OwnCopy>();
        string? cursor = null;
        for (int page = 0; page < MaxOwnInstancePages; page++)
        {
            string url = string.Format(MarketOwnInstancesUrl, Uri.EscapeDataString(cid), userId) + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var r = await GetAuthJsonAsync(url, s, SellParsers.OwnCopiesPage, ct).ConfigureAwait(false);
            if (!r.Ok) return new(r.Outcome, null, RetryAfter: r.RetryAfter, Problem: r.Problem) { Http = r.Http };
            all.AddRange(r.Value.Items);
            cursor = r.Value.Next;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return new(ReadOutcome.Ok, all);
    }

    /// <summary>Marketplace resale data (RAP and daily sales volume), the same shape as the classic one. Read logged in, as the item page does.</summary>
    public Task<ReadResult<(long? Rap, double? SalesPerDay)>> GetMarketResaleDataAsync(SessionContext s, string collectibleItemId, DateTimeOffset now, CancellationToken ct = default) =>
        GetAuthJsonAsync(string.Format(MarketResaleDataUrl, Uri.EscapeDataString(collectibleItemId)), s, r => LimitedParsers.ResaleData(r, now), ct);
}
