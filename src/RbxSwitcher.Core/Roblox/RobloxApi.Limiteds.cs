using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Roblox;

/// <summary>Limited readings: the account's collectibles (own session) and public resale data. Read-only.</summary>
public sealed partial class RobloxApi
{
    internal const string CollectiblesUrl = "https://inventory.roblox.com/v1/users/{0}/assets/collectibles?limit=100&sortOrder=Asc";
    internal const string ResaleDataUrl = "https://economy.roblox.com/v1/assets/{0}/resale-data";
    internal const int MaxCollectiblePages = 30;

    /// <summary>Every page of the account's Limiteds; partial lists are never returned as complete.</summary>
    public async Task<ReadResult<(List<LimitedCopy> Copies, int Ambiguous)>> GetCollectiblesAsync(SessionContext s, long userId, CancellationToken ct = default)
    {
        var copies = new List<LimitedCopy>();
        int ambiguous = 0;
        string? cursor = null;
        var seen = new HashSet<string>();
        for (int page = 0; page < MaxCollectiblePages; page++)
        {
            string url = string.Format(CollectiblesUrl, userId) + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var res = await GetAuthJsonAsync(url, s, LimitedParsers.CollectiblesPage, ct).ConfigureAwait(false);
            if (!res.Ok) return new(res.Outcome, default, RetryAfter: res.RetryAfter, Problem: "Limiteds: " + res.Problem) { Http = res.Http };
            copies.AddRange(res.Value.Copies);
            ambiguous += res.Value.Ambiguous;
            cursor = res.Value.Next;
            if (string.IsNullOrEmpty(cursor)) return new(ReadOutcome.Ok, (copies, ambiguous));
            if (!seen.Add(cursor)) return new(ReadOutcome.Failed, default, Problem: "Limiteds: paginação repetida");
        }
        return new(ReadOutcome.Failed, default, Problem: "Limiteds: páginas demais");
    }

    /// <summary>Public resale data: RAP and sales per day of the last 30 days (liquidity).</summary>
    public Task<ReadResult<(long? Rap, double? SalesPerDay)>> GetResaleDataAsync(long assetId, DateTimeOffset now, CancellationToken ct = default) =>
        GetPublicJsonAsync(string.Format(ResaleDataUrl, assetId), r => LimitedParsers.ResaleData(r, now), null, ct);
}
