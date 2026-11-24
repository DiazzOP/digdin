using System.Text.Json;
using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>One resale listing of an item: who sells it, for how much, and which copy (when Roblox says).</summary>
public sealed record ResaleListing(long SellerId, long Price, long? UserAssetId, string? InstanceId, int? Serial);

/// <param name="Complete">Every listing of the item is here (Roblox said there is no next page).</param>
public sealed record ItemListings(long AssetId, IReadOnlyList<ResaleListing> Lowest, DateTimeOffset ReadUtc, bool Complete = false);

/// <summary>One page of an item's cheapest listings; <see cref="Complete"/> when there is no next page.</summary>
public sealed record ListingsPage(List<ResaleListing> Items, bool Complete);

/// <summary>
/// One of the account's own copies as the resale page reports it: on sale (with a price) or not, held or not. The
/// Marketplace gives neither the copy id nor (often) the serial: such copies are matched to the account's copies in order.
/// </summary>
public sealed record OwnCopy(long? UserAssetId, int? Serial, long? Price, bool? Held = null);

/// <summary>
/// Which of an account's copies are on sale, and for how much. Read per item with the account's own session; lives in
/// the encrypted vault. A copy missing from <see cref="PriceByCopy"/> after its item was read is not on sale.
/// </summary>
public sealed class AccountListings
{
    /// <summary>userAssetId → price, only the copies on sale.</summary>
    public Dictionary<long, long> PriceByCopy { get; set; } = [];
    /// <summary>When each item (asset id) was last read for this account.</summary>
    public Dictionary<long, DateTimeOffset> ItemReadUtc { get; set; } = [];
    /// <summary>How many copies of each item the account has per Roblox's own read (all), and how many are held.</summary>
    public Dictionary<long, int> OwnedByItem { get; set; } = [];
    public Dictionary<long, int> HeldByItem { get; set; } = [];
    public string? Problem { get; set; }

    /// <summary>Applies one item's answer: its copies on sale now replace what was known for that item.</summary>
    public void Apply(long assetId, IEnumerable<OwnCopy> copies, IReadOnlyList<LimitedCopy> inventory, DateTimeOffset now)
    {
        var mine = inventory.Where(c => c.AssetId == assetId).ToList();
        foreach (var c in mine) PriceByCopy.Remove(c.UserAssetId);
        var free = mine.Where(c => c.OnHold != true).OrderBy(c => c.UserAssetId).ToList();
        var anonymous = new List<long>();
        foreach (var o in copies)
        {
            if (o.Price is not { } price || price <= 0 || o.Held == true) continue;
            // By id, or by serial; the Marketplace often gives neither.
            var copy = o.UserAssetId is { } id ? free.FirstOrDefault(c => c.UserAssetId == id) : null;
            copy ??= o.Serial is { } serial ? free.FirstOrDefault(c => c.Serial == serial) : null;
            if (copy is null) { if (o.UserAssetId is null && o.Serial is null) anonymous.Add(price); continue; }
            PriceByCopy[copy.UserAssetId] = price;
            free.Remove(copy);
        }
        // Copies of the same item are interchangeable here: unnamed listings go to the account's other released copies.
        foreach (var (price, copy) in anonymous.Order().Zip(free)) PriceByCopy[copy.UserAssetId] = price;
        var all = copies.ToList();
        OwnedByItem[assetId] = all.Count;
        HeldByItem[assetId] = all.Count(o => o.Held == true);
        ItemReadUtc[assetId] = now;
    }
}

/// <summary>Resale answers, classic (economy.roblox.com) and Marketplace (apis.roblox.com). Tolerant; value-free errors.</summary>
public static class SellParsers
{
    /// <summary>resellers: "data": [{ "seller": {"id"|"sellerId"}, "price", "userAssetId"?, "collectibleItemInstanceId"?, "serialNumber"? }]</summary>
    public static List<ResaleListing> Resellers(JsonElement r) => ResellersPage(r).Items;

    /// <summary>The page and whether it is the last one (no "nextPageCursor").</summary>
    public static ListingsPage ResellersPage(JsonElement r) =>
        new(ParseResellers(r), string.IsNullOrEmpty(TradeParsers.Str(r, "nextPageCursor")));

    private static List<ResaleListing> ParseResellers(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException("revendedores: campo data ausente");
        var list = new List<ResaleListing>();
        foreach (var e in data.EnumerateArray())
        {
            if (TradeParsers.Long(e, "price") is not { } price || price <= 0) continue;
            long seller = e.TryGetProperty("seller", out var s) && s.ValueKind == JsonValueKind.Object
                ? TradeParsers.Long(s, "id") ?? TradeParsers.Long(s, "sellerId") ?? 0
                : TradeParsers.Long(e, "sellerId") ?? 0;
            list.Add(new ResaleListing(seller, price, TradeParsers.Long(e, "userAssetId"),
                TradeParsers.Str(e, "collectibleItemInstanceId") ?? TradeParsers.Str(e, "collectibleInstanceId"),
                (int?)TradeParsers.Long(e, "serialNumber")));
        }
        return [.. list.OrderBy(l => l.Price)];
    }

    /// <summary>
    /// The account's copies of one item: classic resellable-copies ("data": [{userAssetId, price|null, serialNumber}]) or
    /// Marketplace resellable instances ("itemInstances"|"data": [{collectibleInstanceId, serialNumber, saleState, price}]).
    /// </summary>
    public static List<OwnCopy> OwnCopies(JsonElement r) => OwnCopiesPage(r).Items;

    public static (List<OwnCopy> Items, string? Next) OwnCopiesPage(JsonElement r) => (ParseOwnCopies(r), TradeParsers.Str(r, "nextPageCursor"));

    private static List<OwnCopy> ParseOwnCopies(JsonElement r)
    {
        JsonElement list = default;
        bool found = r.ValueKind == JsonValueKind.Object &&
                     (r.TryGetProperty("data", out list) || r.TryGetProperty("itemInstances", out list) || r.TryGetProperty("instances", out list));
        if (!found || list.ValueKind != JsonValueKind.Array) throw new FormatException("cópias à venda: lista ausente");
        var copies = new List<OwnCopy>();
        foreach (var e in list.EnumerateArray())
        {
            long? price = TradeParsers.Long(e, "price");
            // Marketplace: a price may stay on an instance taken off sale; the sale state decides.
            if (TradeParsers.Str(e, "saleState") is { } state && !state.Equals("OnSale", StringComparison.OrdinalIgnoreCase)) price = null;
            bool? held = e.TryGetProperty("isHeld", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False ? h.GetBoolean() : null;
            copies.Add(new OwnCopy(TradeParsers.Long(e, "userAssetId"), (int?)TradeParsers.Long(e, "serialNumber"), price is > 0 ? price : null, held));
        }
        return copies;
    }
}

public enum SellSituation
{
    /// <summary>On sale, and another seller asks the same or less.</summary>
    Outbid,
    /// <summary>On sale and the cheapest.</summary>
    Lowest,
    /// <summary>On sale; the other sellers were not read.</summary>
    OnSale,
    /// <summary>Released and not on sale.</summary>
    Ready,
    /// <summary>Released; whether it is on sale was not read yet.</summary>
    Unread,
    OnHold,
    /// <summary>Released, but the account has no active Plus: Roblox does not let it sell.</summary>
    NoPlus,
    /// <summary>A recent sale (from the account's transaction records).</summary>
    Sold,
}

/// <param name="LowestOther">The cheapest listing by someone else; <see cref="LowestOtherAccount"/> says when that is another of your accounts.</param>
/// <param name="Position">Your place among the cheapest listings (1 = first); null when beyond the ones read.</param>
/// <param name="InPlan">The distribution step holding this copy, if any.</param>
/// <param name="Note">Extra line under the name ("chegou 19/11", "no plano E004").</param>
public sealed record SellRow(long AccountId, string Username, string AccountTitle, long UserAssetId, long AssetId, string Name, int? Serial,
                             SellSituation Situation, bool Small, long? MyPrice, long? LowestOther, string? LowestOtherAccount, int? Position,
                             long? Rap, double? SalesPerDay, long? Net, bool NetEstimated, long? Suggestion, string? SuggestionWhy,
                             string? InPlan, string? Note, SaleRecord? Sale = null, HoldRelease? Hold = null)
{
    public string Label => Name + (Serial is { } s ? $" #{s}" : "");
}

/// <param name="NextRelease">The next quota the 30-day window gives back (amount, when).</param>
public sealed record SellAccount(long UserId, string Username, string Title, string? Group, bool Source, PlusLabel Plus, long? RemainingMonthly,
                                 long? MonthlyCap, (long Amount, DateTimeOffset AtUtc)? NextRelease, string? Problem, DateTimeOffset? ReadUtc)
{
    public bool CanSell => Plus is PlusLabel.Active or PlusLabel.Trial;
    /// <summary>Robux of sales still pending in the account (already net of the fee).</summary>
    public long? Pending { get; init; }
    public bool HasQuotaNow => RemainingMonthly is > 0;
}

public sealed record SellInput(AccountSnapshot Snapshot, string Title, AccountListings? Listings, AccountSales? Sales, AccountArrivals? Arrivals = null);

public sealed record SellBoardResult(IReadOnlyList<SellAccount> Accounts, IReadOnlyList<SellRow> Rows);

/// <summary>
/// The "Limiteds" panel: every account's copies with where they stand for selling (on hold, ready, on sale and the
/// cheapest or outbid), the account's quota, and a suggested price with its reason. Read-only: the person sells on
/// roblox.com.
/// </summary>
public static class SellBoard
{
    public static readonly TimeSpan SoldWindow = TimeSpan.FromDays(7);
    /// <summary>How many of the cheapest listings are read per item.</summary>
    public const int ListingsRead = 10;

    /// <summary>
    /// What the shown copies are worth net, by situation, plus the accounts' pending Robux. Sales already made are not
    /// counted again: their Robux are in the pending.
    /// </summary>
    public static (long OnSale, long Ready, long OnHold, long Pending) Totals(IEnumerable<SellRow> rows, IEnumerable<SellAccount> accounts)
    {
        long onSale = 0, ready = 0, hold = 0;
        foreach (var r in rows)
            switch (r.Situation)
            {
                case SellSituation.Outbid or SellSituation.Lowest or SellSituation.OnSale: onSale += r.Net ?? 0; break;
                case SellSituation.Ready or SellSituation.Unread or SellSituation.NoPlus: ready += r.Net ?? 0; break;
                case SellSituation.OnHold: hold += r.Net ?? 0; break;
            }
        return (onSale, ready, hold, accounts.Sum(a => a.Pending ?? 0));
    }

    /// <summary>Why the account's Limiteds are not (fully) known; null when the last read was complete.</summary>
    private static string? InventoryProblem(AccountSnapshot a) => a.Inventory switch
    {
        null => a.SessionOk ? "Limiteds ainda não lidos" : "sessão expirada: Limiteds não lidos",
        { Complete: false } inv => "Limiteds não lidos por completo" + (inv.Problem is { } p ? ": " + p : ""),
        { Problem: { } p } => "última leitura falhou: " + p,
        { RolimonsProblem: { } rp } => "Rolimons não leu a conta (itens novos/UGC podem faltar): " + rp,
        _ => null,
    };

    public static SellBoardResult Build(IReadOnlyList<SellInput> inputs, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket> market,
                                        IReadOnlyDictionary<long, ItemListings> listings, DistributionPlan plan, DateTimeOffset now)
    {
        var mine = inputs.ToDictionary(i => i.Snapshot.UserId, i => i.Snapshot.Username);
        var planned = plan.Steps.Where(x => x.IsOpenForWork)
            .SelectMany(x => x.AllCopies().Select(c => (c.UserAssetId, x.Id))).GroupBy(t => t.UserAssetId).ToDictionary(g => g.Key, g => g.First().Id);
        var arrived = plan.Steps.Where(x => x.State == StepState.Completed && x.CompletedUtc is not null)
            .SelectMany(x => (x.ActualFromSource ?? x.Limiteds).Concat(x.ActualFromDestination ?? []).Select(c => (c.UserAssetId, When: x.CompletedUtc!.Value)))
            .GroupBy(t => t.UserAssetId).ToDictionary(g => g.Key, g => g.Max(t => t.When));

        // Trades accepted in any account's tab: the exact moment the copies of both sides arrived.
        var accepted = new Dictionary<long, DateTimeOffset>();
        foreach (var (id, at) in inputs.SelectMany(i => i.Arrivals?.Accepted ?? [])) accepted.TryAdd(id, at);

        var accounts = new List<SellAccount>();
        var rows = new List<SellRow>();
        foreach (var input in inputs)
        {
            var a = input.Snapshot;
            var f = a.Finance;
            var q = f is null ? null : QuotaCalculator.Compute(f, now);
            var plus = PlusStatus.Evaluate(f, now).Label;
            var next = q?.MonthlyReleases.Where(r => r.AtUtc > now).OrderBy(r => r.AtUtc).FirstOrDefault();
            var account = new SellAccount(a.UserId, a.Username, input.Title, a.Group, s.SourceAccounts.Contains(a.UserId), plus, q?.RemainingMonthly,
                q?.EffectiveMonthly, next is { } n ? (n.Amount, n.AtUtc) : null, InventoryProblem(a), a.Inventory?.ReadUtc) { Pending = f?.PendingRobux };
            // (A failed read of the account's own listings is not shown: "on sale" then comes from the cheapest listings.)
            int before = rows.Count;

            if (a.Inventory is { } inv)
            {
                var valued = Valuation.Value(inv, s, market);
                var listed = ListedAmongLowest(a.UserId, valued, listings);
                var shown = new List<ValuedCopy>();
                foreach (var item in valued)
                {
                    var v = item;
                    // A copy only Rolimons lists is checked against the account's own copies on the Marketplace, when read:
                    // none → it already left the account; all held → on hold.
                    if (v.Copy.FromRolimons && input.Listings is { } own && own.OwnedByItem.TryGetValue(v.Copy.AssetId, out int owned))
                    {
                        if (owned == 0) continue;
                        if (own.HeldByItem.GetValueOrDefault(v.Copy.AssetId) >= owned) v = v with { OnHold = true };
                    }
                    shown.Add(v);
                }
                // When each held copy leaves the hold (2 days after a trade, 7 after a store purchase).
                var holds = HoldClock.ForAccount(shown.Where(v => v.OnHold).Select(v => v.Copy), input.Arrivals, accepted, now);
                foreach (var v in shown)
                    rows.Add(RowFor(input, account, v, market.GetValueOrDefault(v.Copy.AssetId), listings.GetValueOrDefault(v.Copy.AssetId),
                        mine, planned.GetValueOrDefault(v.Copy.UserAssetId), arrived.TryGetValue(v.Copy.UserAssetId, out var at) ? at : null, s,
                        listed.TryGetValue(v.Copy.UserAssetId, out var lp) ? lp : null) with { Hold = v.OnHold ? holds.GetValueOrDefault(v.Copy.UserAssetId) : null });
            }

            foreach (var sale in input.Sales?.Records.Where(r => now - r.CreatedUtc <= SoldWindow) ?? [])
                rows.Add(new SellRow(a.UserId, a.Username, input.Title, 0, sale.AssetId, sale.Name, null, SellSituation.Sold, false, null, null, null, null,
                    market.GetValueOrDefault(sale.AssetId)?.Rap, market.GetValueOrDefault(sale.AssetId)?.SalesPerDay, sale.Amount, false, null, null, null,
                    (sale.Pending ? "pendente · " : "") + "vendida em " + BrasiliaTime.ToBrasilia(sale.CreatedUtc).ToString("dd/MM HH:mm"), sale));

            // An account whose Limiteds could not be read stays, with the reason (it must not vanish silently).
            if (rows.Count > before || account.Problem is not null) accounts.Add(account);
        }
        return new SellBoardResult(accounts, rows);
    }

    /// <summary>
    /// The account's copies found among an item's cheapest listings (its own seller id), with their price: by copy id,
    /// by serial, or — when the listing names neither — the account's next released copy of that item.
    /// </summary>
    public static Dictionary<long, long> ListedAmongLowest(long userId, IReadOnlyList<ValuedCopy> valued, IReadOnlyDictionary<long, ItemListings> listings)
    {
        var result = new Dictionary<long, long>();
        foreach (var grp in valued.Where(v => !v.OnHold).GroupBy(v => v.Copy.AssetId))
        {
            if (!listings.TryGetValue(grp.Key, out var item)) continue;
            var free = grp.Select(v => v.Copy).ToList();
            foreach (var l in item.Lowest.Where(l => l.SellerId == userId).OrderBy(l => l.Price))
            {
                var copy = (l.UserAssetId is { } id ? free.FirstOrDefault(c => c.UserAssetId == id) : null)
                           ?? (l.Serial is { } serial ? free.FirstOrDefault(c => c.Serial == serial) : null)
                           ?? (l.UserAssetId is null && l.Serial is null ? free.FirstOrDefault() : null);
                if (copy is null) continue;
                free.Remove(copy);
                result[copy.UserAssetId] = l.Price;
            }
        }
        return result;
    }

    private static SellRow RowFor(SellInput input, SellAccount account, ValuedCopy v, ItemMarket? m, ItemListings? item,
                                  IReadOnlyDictionary<long, string> mine, string? inPlan, DateTimeOffset? arrived, DistributionSettings s,
                                  long? listedPrice)
    {
        var c = v.Copy;
        var a = input.Snapshot;
        // The account's own read of the item wins when it is as recent as the cheapest listings. Otherwise the listings
        // say which copies are on sale — and, when they are all of the item's listings, which are not; when they say
        // nothing about this copy (not among them, and not all of them), the older own read still holds.
        DateTimeOffset ownAt = default;
        bool ownKnown = input.Listings?.ItemReadUtc.TryGetValue(c.AssetId, out ownAt) == true;
        bool ownFresh = ownKnown && (item is null || ownAt >= item.ReadUtc - TimeSpan.FromMinutes(30));
        long? ownPrice = ownKnown && input.Listings!.PriceByCopy.TryGetValue(c.UserAssetId, out var p) ? p : null;
        bool listingsComplete = item is { Complete: true } || item is { Lowest.Count: < ListingsRead };
        long? myPrice = ownFresh ? ownPrice : listedPrice ?? (listingsComplete ? null : ownPrice);
        bool read = ownKnown || listedPrice is not null || listingsComplete;
        var others = item?.Lowest.Where(l => l.SellerId != a.UserId).OrderBy(l => l.Price).ToList();
        var lowest = others?.FirstOrDefault();
        string? lowestMine = lowest is not null && lowest.SellerId != a.UserId && mine.TryGetValue(lowest.SellerId, out var who) ? who : null;

        SellSituation situation;
        int? position = null;
        if (myPrice is { } price)
        {
            if (item is null) situation = SellSituation.OnSale;
            else
            {
                int ahead = others!.Count(l => l.Price <= price); // same price: the older listing shows first
                // Only the cheapest ListingsRead are read: a price above all of them has no known place.
                bool known = item.Complete || item.Lowest.Count < ListingsRead || price <= item.Lowest.Max(l => l.Price);
                position = known ? ahead + 1 : null;
                situation = lowest is not null && lowest.Price <= price ? SellSituation.Outbid : SellSituation.Lowest;
            }
        }
        else if (v.OnHold) situation = SellSituation.OnHold;
        else if (!account.CanSell) situation = SellSituation.NoPlus;
        else situation = read ? SellSituation.Ready : SellSituation.Unread;

        var (suggestion, why) = situation is SellSituation.Outbid or SellSituation.Ready or SellSituation.Unread or SellSituation.OnHold
            ? Suggest(lowest?.Price, m, c.Rap, s) : (null, null);
        long? net = myPrice is { } mp ? Valuation.Net(mp, s.FeePercent) : suggestion is { } sg ? Valuation.Net(sg, s.FeePercent) : v.Net;
        var notes = new List<string>();
        if (inPlan is not null) notes.Add("no plano " + inPlan);
        if (situation == SellSituation.OnHold && arrived is { } when) notes.Add("chegou " + BrasiliaTime.ToBrasilia(when).ToString("dd/MM"));
        return new SellRow(a.UserId, a.Username, input.Title, c.UserAssetId, c.AssetId, c.Name, c.Serial, situation, v.Small, myPrice,
            lowest?.Price, lowestMine, position, m?.Rap ?? c.Rap, m?.SalesPerDay, net, myPrice is null, suggestion, why, inPlan,
            notes.Count > 0 ? string.Join(" · ", notes) : null);
    }

    /// <summary>
    /// Price to ask: a little under the cheapest other seller, never under the floor (a share of the RAP); with nobody
    /// selling, the Rolimons value or the RAP.
    /// </summary>
    public static (long? Price, string? Why) Suggest(long? lowestOther, ItemMarket? m, long? copyRap, DistributionSettings s)
    {
        long? rap = Valuation.KnownRap(m?.Rap, copyRap);
        long? floor = rap is { } r && s.SellFloorPercent > 0 ? (r * s.SellFloorPercent + 99) / 100 : null;
        if (lowestOther is { } low)
        {
            long under = low - Math.Max(0, s.SellUndercut);
            if (floor is { } fl && under < fl)
                return (fl, $"o menor preço ({Planner.Fmt(low)}) está abaixo do piso de {s.SellFloorPercent}% do RAP: anunciar no piso ou esperar");
            return (Math.Max(1, under), s.SellUndercut > 0 ? $"{Planner.Fmt(s.SellUndercut)} abaixo do menor preço" : "igual ao menor preço");
        }
        if ((m?.RolimonsValue ?? rap) is { } basis and > 0)
            return (Math.Max(basis, floor ?? 0), m?.RolimonsValue is not null ? "ninguém vendendo: valor da Rolimons" : "ninguém vendendo: RAP");
        return (null, null);
    }
}
