using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.Core.Roblox;

/// <summary>
/// Trade and sale readings for the distribution plan. All GETs with the account's own session: the app never sends,
/// accepts or changes a trade.
/// </summary>
public sealed partial class RobloxApi
{
    internal const string TradeV1Url = "https://trades.roblox.com/v1/trades/{0}";
    internal const string TradeV2Url = "https://trades.roblox.com/v2/trades/{0}";
    internal const string TradeListUrl = "https://trades.roblox.com/v1/trades/{0}?limit=100&sortOrder=Desc";
    internal const string SalesUrl = "https://apis.roblox.com/transaction-records/v1/users/{0}/transactions?transactionType=Sale&limit=100&sortOrder=Desc";
    internal const string PurchasesUrl = "https://apis.roblox.com/transaction-records/v1/users/{0}/transactions?transactionType=Purchase&limit=100&sortOrder=Desc";
    internal const int MaxSalesPages = 10;

    public async Task<ReadResult<TradeDetail>> GetTradeAsync(SessionContext s, long tradeId, CancellationToken ct = default)
    {
        var v1 = await GetAuthJsonAsync(string.Format(TradeV1Url, tradeId), s, TradeParsers.Detail, ct).ConfigureAwait(false);
        if (v1.Ok || v1.Outcome is ReadOutcome.Unauthorized or ReadOutcome.RateLimited) return v1;
        var v2 = await GetAuthJsonAsync(string.Format(TradeV2Url, tradeId), s, TradeParsers.Detail, ct).ConfigureAwait(false);
        return v2.Ok ? v2 : v1.Http == 404 && v2.Http == 404 ? new(ReadOutcome.Failed, null, Problem: "trade não encontrada nesta conta") : v1;
    }

    /// <summary>First page (newest 100) of a trade list: Inbound, Outbound, Completed or Inactive.</summary>
    public async Task<ReadResult<List<TradeSummary>>> GetTradesAsync(SessionContext s, string kind, CancellationToken ct = default)
    {
        var r = await GetAuthJsonAsync(string.Format(TradeListUrl, kind), s, TradeParsers.List, ct).ConfigureAwait(false);
        return r.Ok ? new(ReadOutcome.Ok, r.Value.Items) : new(r.Outcome, null, RetryAfter: r.RetryAfter, Problem: r.Problem) { Http = r.Http };
    }

    /// <summary>Sales newer than <paramref name="since"/> (pages until older, at most 10).</summary>
    public Task<ReadResult<List<SaleRecord>>> GetSalesAsync(SessionContext s, long userId, DateTimeOffset since, CancellationToken ct = default) =>
        GetItemRecordsAsync(SalesUrl, "vendas", s, userId, since, ct);

    /// <summary>Store purchases of items newer than <paramref name="since"/> (same records as the sales; amount is what was paid).</summary>
    public Task<ReadResult<List<SaleRecord>>> GetPurchasesAsync(SessionContext s, long userId, DateTimeOffset since, CancellationToken ct = default) =>
        GetItemRecordsAsync(PurchasesUrl, "compras", s, userId, since, ct);

    private async Task<ReadResult<List<SaleRecord>>> GetItemRecordsAsync(string urlFormat, string what, SessionContext s, long userId, DateTimeOffset since, CancellationToken ct)
    {
        var all = new List<SaleRecord>();
        string? cursor = null;
        for (int page = 0; page < MaxSalesPages; page++)
        {
            string url = string.Format(urlFormat, userId) + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var r = await GetAuthJsonAsync(url, s, SalesParsers.PageWithOldest, ct).ConfigureAwait(false);
            if (!r.Ok) return new(r.Outcome, null, RetryAfter: r.RetryAfter, Problem: what + ": " + r.Problem) { Http = r.Http };
            all.AddRange(r.Value.Items);
            cursor = r.Value.Next;
            // Page on the dates of every row: a page of game pass sales has no items but is not the end.
            if (string.IsNullOrEmpty(cursor) || r.Value.Oldest is not { } oldest || oldest < since) break;
        }
        return new(ReadOutcome.Ok, all.Where(x => x.CreatedUtc >= since).DistinctBy(x => x.Id).ToList());
    }
}
