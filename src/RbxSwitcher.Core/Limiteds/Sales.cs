using System.Text.Json;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>One sale credited (or pending) to an account, from Roblox's transaction records. Amount is what the account received.</summary>
public sealed record SaleRecord(string Id, DateTimeOffset CreatedUtc, long AssetId, string Name, long Amount, bool Pending);

/// <summary>An account's recent sales. Lives in the encrypted vault.</summary>
public sealed class AccountSales
{
    public List<SaleRecord> Records { get; set; } = [];
    public DateTimeOffset? ReadUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? Problem { get; set; }
    /// <summary>Pending sales seen becoming credited (how long this account's Robux stay pending). See <see cref="RobuxCalendar"/>.</summary>
    public List<PendingRelease> Releases { get; set; } = [];
}

public static class SalesParsers
{
    private static readonly string[] IdFields = ["idHash", "id", "purchaseToken", "transactionId"];

    /// <summary>transaction-records/v1/users/{id}/transactions?transactionType=Sale</summary>
    public static (List<SaleRecord> Items, string? Next) Page(JsonElement r)
    {
        var (items, next, _) = PageWithOldest(r);
        return (items, next);
    }

    /// <summary>The page plus the oldest date of all its rows (game passes and other non-item sales included), for paging.</summary>
    public static (List<SaleRecord> Items, string? Next, DateTimeOffset? Oldest) PageWithOldest(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException("vendas: campo data ausente");
        var list = new List<SaleRecord>();
        DateTimeOffset? oldest = null;
        foreach (var t in data.EnumerateArray())
        {
            string? id = IdFields.Select(f => t.TryGetProperty(f, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null)
                .FirstOrDefault(x => x is { Length: > 0 } && x != "0");
            if (id is null) throw new FormatException("vendas: venda sem id");
            if (TradeParsers.Str(t, "created") is not { } c || !DateTimeOffset.TryParse(c, out var created)) throw new FormatException("vendas: venda sem data");
            if (oldest is null || created < oldest) oldest = created;
            if (!t.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Object) continue; // not an item sale
            if (TradeParsers.Str(d, "type") is { } type && !type.Equals("Asset", StringComparison.OrdinalIgnoreCase)) continue;
            if (TradeParsers.Long(d, "id") is not { } asset || asset <= 0) continue;
            long amount = t.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.Object ? TradeParsers.Long(cur, "amount") ?? 0 : 0;
            bool pending = t.TryGetProperty("isPending", out var p) && p.ValueKind == JsonValueKind.True;
            list.Add(new SaleRecord(id, created, asset, TradeParsers.Str(d, "name") ?? "", Math.Abs(amount), pending));
        }
        return (list, TradeParsers.Str(r, "nextPageCursor"), oldest);
    }
}

public enum SaleLink { Linked, Ambiguous, NotFromPlan }

/// <param name="Via">How the sale was tied to the copy ("única cópia do item na conta", "pelo inventário"), or why not.</param>
public sealed record SaleView(long AccountId, string AccountName, SaleRecord Sale, SaleLink Link, CopyRef? Copy, string? StepId, long? EstimatedNet, string Via);

public enum DeliveredState { OnHold, CanList, Sold, Gone, Unknown }

/// <summary>A copy the plan delivered to a destination, and where it stands now.</summary>
public sealed record DeliveredView(long AccountId, string AccountName, CopyRef Copy, string StepId, DateTimeOffset? DeliveredUtc, DeliveredState State, SaleView? Sale);

/// <summary>
/// Ties sales to the copies the plan delivered. Safe links only: the only delivered copy of that item in that account
/// (delivered before the sale), or — when there are several — the only one that left the account's inventory.
/// Anything else stays "a conferir".
/// </summary>
public static class SalesMatcher
{
    public static (List<SaleView> Sales, List<DeliveredView> Delivered) Match(DistributionPlan plan, IReadOnlyList<AccountSnapshot> accounts,
                                                                             IReadOnlyDictionary<long, AccountSales> sales)
    {
        var saleViews = new List<SaleView>();
        var delivered = new List<DeliveredView>();
        foreach (var a in accounts)
        {
            var copies = plan.Steps.Where(x => x.State == StepState.Completed && x.DestinationId == a.UserId)
                .SelectMany(x => x.Limiteds.Select(c => (Copy: c, Step: x)))
                .Where(t => t.Copy.UserAssetId > 0).ToList();
            var linked = new Dictionary<long, SaleView>(); // by userAssetId
            if (sales.TryGetValue(a.UserId, out var acct))
            {
                foreach (var sale in acct.Records.OrderBy(r => r.CreatedUtc))
                {
                    var cands = copies.Where(t => t.Copy.AssetId == sale.AssetId && !linked.ContainsKey(t.Copy.UserAssetId)
                                                  && (t.Step.CompletedUtc is not { } done || done <= sale.CreatedUtc)).ToList();
                    SaleView view;
                    if (cands.Count == 0)
                        view = new(a.UserId, a.Username, sale, SaleLink.NotFromPlan, null, null, null, "nenhuma cópia do plano deste item nesta conta");
                    else if (cands.Count == 1)
                        view = new(a.UserId, a.Username, sale, SaleLink.Linked, cands[0].Copy, cands[0].Step.Id, cands[0].Copy.Net, "única cópia do plano deste item na conta");
                    else if (a.Inventory is { Complete: true, ReadUtc: { } read } inv && read >= sale.CreatedUtc
                             && cands.Where(t => inv.Copies.All(c => c.UserAssetId != t.Copy.UserAssetId)).ToList() is { Count: 1 } gone)
                        view = new(a.UserId, a.Username, sale, SaleLink.Linked, gone[0].Copy, gone[0].Step.Id, gone[0].Copy.Net, "a única cópia que saiu do inventário");
                    else
                        view = new(a.UserId, a.Username, sale, SaleLink.Ambiguous, null, null, null, $"{cands.Count} cópias do plano deste item na conta: a conferir");
                    if (view is { Link: SaleLink.Linked, Copy: { } lc }) linked[lc.UserAssetId] = view;
                    saleViews.Add(view);
                }
            }
            foreach (var (copy, step) in copies)
            {
                DeliveredState state;
                var now = a.Inventory?.Copies.FirstOrDefault(c => c.UserAssetId == copy.UserAssetId);
                if (linked.ContainsKey(copy.UserAssetId)) state = DeliveredState.Sold;
                else if (a.Inventory is not { Complete: true }) state = DeliveredState.Unknown;
                else if (now is null) state = DeliveredState.Gone;
                else state = a.Inventory.IsOnHold(now) ? DeliveredState.OnHold : DeliveredState.CanList;
                delivered.Add(new(a.UserId, a.Username, copy, step.Id, step.CompletedUtc, state, linked.GetValueOrDefault(copy.UserAssetId)));
            }
        }
        return (saleViews, delivered);
    }
}
