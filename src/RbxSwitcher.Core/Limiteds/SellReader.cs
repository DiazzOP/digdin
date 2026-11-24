using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// Reads resale data for one item on the right system. An item with a Marketplace id (new Limiteds, and the classic
/// ones Roblox moved — every one tested so far) is read on apis.roblox.com; only an item the catalog says has no
/// Marketplace id is read on economy.roblox.com. An item whose id is not known yet is not read at all: the old
/// endpoints answer 404 or numbers frozen at the migration ("0/dia"), which looked like real answers.
/// Ids are looked up in batches and kept (the app saves them with the market cache). A failure only ever affects the
/// item that failed — never the other items or accounts. Read-only.
/// </summary>
public sealed class SellReader(RobloxApi api)
{
    private readonly Dictionary<long, string?> _collectibleIds = new();
    /// <summary>Classic-only items whose own-copies read answered 404: that item is not asked again this session.</summary>
    private readonly HashSet<long> _ownGone = [];

    /// <summary>The item's Marketplace id (null: not looked up yet, or a classic item — see <see cref="IdKnown"/>).</summary>
    public string? KnownCollectibleId(long assetId) => _collectibleIds.GetValueOrDefault(assetId);

    /// <summary>The catalog answered for this item (its Marketplace id, or that it has none).</summary>
    public bool IdKnown(long assetId) => _collectibleIds.ContainsKey(assetId);

    /// <summary>Every id looked up so far (to save).</summary>
    public IReadOnlyDictionary<long, string?> CollectibleIds => _collectibleIds;

    /// <summary>Ids saved earlier (Marketplace ids do not change).</summary>
    public void Seed(IEnumerable<KeyValuePair<long, string?>> ids)
    {
        foreach (var (id, cid) in ids) _collectibleIds.TryAdd(id, cid);
    }

    /// <summary>Roblox does not tell which copies of this (classic-only) item are on sale: "on sale" comes from its listings.</summary>
    public bool OwnCopiesUnreadable(long assetId) => _ownGone.Contains(assetId);

    /// <summary>Looks up the Marketplace ids not known yet, <see cref="RobloxApi.CollectibleIdBatch"/> per request.</summary>
    /// <returns>How many are still unknown; a failure only for a refusal (401) or Roblox's request limit (429).</returns>
    public async Task<ReadResult<int>> EnsureIdsAsync(SessionContext s, IEnumerable<long> assetIds, CancellationToken ct = default)
    {
        var missing = assetIds.Distinct().Where(id => !_collectibleIds.ContainsKey(id)).ToList();
        for (int i = 0; i < missing.Count; i += RobloxApi.CollectibleIdBatch)
        {
            var batch = missing.Skip(i).Take(RobloxApi.CollectibleIdBatch).ToList();
            var r = await api.GetCollectibleIdsAsync(s, batch, ct).ConfigureAwait(false);
            if (r.Outcome is ReadOutcome.Unauthorized or ReadOutcome.RateLimited) return new(r.Outcome, 0, RetryAfter: r.RetryAfter, Problem: r.Problem) { Http = r.Http };
            if (!r.Ok) continue; // this batch is tried again on the next read
            foreach (long id in batch)
                if (r.Value!.TryGetValue(id, out var cid)) _collectibleIds[id] = cid;
        }
        return new(ReadOutcome.Ok, missing.Count(id => !_collectibleIds.ContainsKey(id)));
    }

    /// <summary>The item's id, looking it up when needed; a failed result when it cannot be known now.</summary>
    private async Task<(bool Known, string? Cid, ReadResult<int>? Failure)> IdAsync(SessionContext s, long assetId, CancellationToken ct)
    {
        if (!_collectibleIds.ContainsKey(assetId))
        {
            var e = await EnsureIdsAsync(s, [assetId], ct).ConfigureAwait(false);
            if (!e.Ok) return (false, null, e);
        }
        return _collectibleIds.TryGetValue(assetId, out var cid) ? (true, cid, null) : (false, null, null);
    }

    private static ReadResult<T> NoId<T>(ReadResult<int>? failure) => failure is { } f
        ? new(f.Outcome, default, RetryAfter: f.RetryAfter, Problem: f.Problem) { Http = f.Http }
        : new(ReadOutcome.Failed, default, Problem: "o catálogo do Roblox não informou o id do item no Marketplace (tenta de novo na próxima leitura)");

    public async Task<ReadResult<ListingsPage>> ListingsAsync(SessionContext s, long assetId, CancellationToken ct = default)
    {
        var (known, cid, failure) = await IdAsync(s, assetId, ct).ConfigureAwait(false);
        if (!known) return NoId<ListingsPage>(failure);
        return await api.GetResellersAsync(s, assetId, cid, ct).ConfigureAwait(false);
    }

    public async Task<ReadResult<List<OwnCopy>>> OwnCopiesAsync(SessionContext s, long userId, long assetId, CancellationToken ct = default)
    {
        if (_ownGone.Contains(assetId)) return new(ReadOutcome.Failed, null, Problem: "o Roblox não informa as cópias à venda deste item") { Http = 404 };
        var (known, cid, failure) = await IdAsync(s, assetId, ct).ConfigureAwait(false);
        if (!known) return NoId<List<OwnCopy>>(failure);
        var r = await api.GetOwnCopiesAsync(s, userId, assetId, cid, ct).ConfigureAwait(false);
        if (cid is null && r.Http == 404) _ownGone.Add(assetId); // classic only, and gone: this item only
        return r;
    }
}
