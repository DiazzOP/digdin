using System.Text;
using System.Text.Json;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App;

/// <summary>
/// Limited distribution, step 1 (read-only): each source/destination account's Limiteds, public market data (Roblox
/// resale data and Rolimons, cached on disk — public data only), and the space each destination has for new lots.
/// </summary>
internal sealed partial class MainForm
{
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan RolimonsInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LiquidityInterval = TimeSpan.FromHours(6);
    private const int MaxLiquidityReadsPerPass = 20;
    private static readonly TimeSpan LiquidityRetry = TimeSpan.FromMinutes(30);
    /// <summary>Items whose Marketplace resale data was asked for this session (then the usual intervals apply).</summary>
    private readonly HashSet<long> _liquidityMarketTried = new();

    /// <summary>An account with a session that has the item (the Marketplace resale data is read logged in); else any.</summary>
    private Account? OwnerOf(long assetId)
    {
        var usable = _vault?.Accounts.All.Where(a => a.HasSession && a.Status != SessionStatus.Expired).ToList() ?? [];
        return usable.FirstOrDefault(a => a.Inventory?.Copies.Any(c => c.AssetId == assetId) == true) ?? usable.FirstOrDefault();
    }

    private readonly RolimonsApi _rolimons = new();
    private InventoryRefresher? _inventoryRefresher;
    private Dictionary<long, ItemMarket> _market = new();
    private DateTimeOffset? _rolimonsAt;
    private readonly HashSet<long> _inventoryBusy = new();
    private bool _marketBusy;

    private InventoryRefresher Inventories => _inventoryRefresher ??= new InventoryRefresher(_api, _rolimons, null,
        asset => _market.GetValueOrDefault(asset) is { } m ? (m.Name, m.Rap) : (null, null));
    private string MarketCachePath => Path.Combine(_dataDir, "market-cache.json");
    private DistributionSettings Distribution => _vault!.Settings.Distribution;

    /// <param name="CollectibleIds">Items' Marketplace ids (public catalog data; null = the item has none).</param>
    private sealed record MarketCache(DateTimeOffset? RolimonsAt, List<ItemMarket> Items, Dictionary<long, string?>? CollectibleIds = null);

    /// <summary>Marketplace ids saved by an earlier run, handed to the reader when it is created.</summary>
    private Dictionary<long, string?> _savedCollectibleIds = new();

    private enum Role { None, Source, Destination }

    private Role RoleOf(Account a) => _vault is null ? Role.None : Planner.RoleOf(Distribution, a.UserId, a.GroupOrDefault) switch
    {
        DistributionRole.Source => Role.Source,
        DistributionRole.Destination => Role.Destination,
        _ => Role.None,
    };

    private void WireLimiteds()
    {
        _plusPage.SpaceOf = SpaceCell;
        _plusView.LimitedsRefreshRequested += async () =>
        {
            if (PlusAccount(_plusPage.DetailId) is { } a) { await RefreshInventoryAsync(a, force: true); await EnsureMarketAsync(); }
        };
        LoadMarketCache();
    }

    private void EditDistribution()
    {
        if (_vault is null) return;
        using var d = new DistributionDialog(Distribution, _vault.Accounts.All, _vault.Accounts.Groups);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var before = Distribution;
        _vault.Settings.Distribution = d.Result;
        // Other sources or destinations: the discarded suggestions belonged to the old plan.
        bool accountsChanged = !before.SourceAccounts.Order().SequenceEqual(d.Result.SourceAccounts.Order())
            || before.DestinationGroup != d.Result.DestinationGroup
            || !before.DestinationAccounts.Order().SequenceEqual(d.Result.DestinationAccounts.Order());
        if (accountsChanged && _vault.Plan.Dismissed.Count > 0) _vault.Plan.Dismissed.Clear();
        RequestSave();
        if (accountsChanged && _vault.Plan.BuiltUtc is not null) { GeneratePlan(manual: true); return; }
        UpdateFinancePanel();
        _ = EnsureMarketAsync();
        SetStatus(d.Result.SourceAccounts.Count == 0
            ? "Distribuição sem contas de origem: escolha Alfa/Beta para começar."
            : "Distribuição salva. Os Limiteds são lidos junto com os saldos.");
    }

    // ───────────── reads ─────────────

    /// <summary>Every account's Limiteds are read (the Limiteds tab covers all); one that had none, every 2 h.</summary>
    private bool InventoryDue(Account a) =>
        a.Inventory?.LastAttemptUtc is not { } last
        || DateTimeOffset.UtcNow - last >= (a.Inventory is { Complete: true, Copies.Count: 0 } && RoleOf(a) == Role.None ? TimeSpan.FromHours(2) : InventoryInterval);

    /// <summary>Reads the account's Limiteds (after the cookie is confirmed to be its own). Respects the 429 pause.</summary>
    private async Task RefreshInventoryAsync(Account a, bool force = false, CancellationToken ct = default)
    {
        if (_closing || _vault is not { } vault || !a.HasSession || a.Status == SessionStatus.Expired) return;
        if (!force && !InventoryDue(a)) return;
        if (DateTimeOffset.UtcNow < _robloxPauseUntil || !_inventoryBusy.Add(a.UserId)) return;
        long id = a.UserId;
        string sent = a.RevealSession();
        UpdatePlusDetails();
        try
        {
            var r = await Inventories.RefreshAsync(id, sent, a.Inventory, ct);
            if (_closing || _vault != vault || vault.Accounts.Find(id) is not { } acc) return;
            acc.Inventory = r.Inventory;
            if (r.RotatedSession is { } fresh && vault.Accounts.ReplaceSessionIf(id, sent, fresh)) _browser.Find(id)?.ReplaceCookie(fresh);
            if (r.SessionRefused) { acc.Status = SessionStatus.Expired; _list.RefreshAccount(id); }
            if (r.RateLimitedFor is { } wait) NoteRateLimit(id, wait);
            RequestSave();
            if (r.Inventory is { Complete: true, Problem: null }) ReplanAfterChange(); // a successful read: adapt the plan to the new stock
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Inventory refresh failed", ex); }
        finally
        {
            _inventoryBusy.Remove(id);
            if (!_closing) UpdateFinancePanel();
        }
    }

    /// <summary>
    /// Public market data: Rolimons values/RAP/demand for every item (one request, every 30 min) and Roblox's sales
    /// per day for the source accounts' non-Small items (a few per pass, every 6 h). No session is sent.
    /// </summary>
    private async Task EnsureMarketAsync()
    {
        if (_marketBusy || _vault is null || Distribution.SourceAccounts.Count == 0) return;
        _marketBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_rolimonsAt is not { } at || now - at >= RolimonsInterval)
            {
                var items = await _rolimons.GetItemsAsync();
                if (items.Ok)
                {
                    foreach (var (id, it) in items.Value!)
                    {
                        var old = _market.GetValueOrDefault(id);
                        _market[id] = new ItemMarket(id, it.Name, it.Rap ?? old?.Rap, it.Value, it.Demand, old?.SalesPerDay, old?.AtUtc ?? DateTimeOffset.MinValue,
                            RapFromRolimons: it.Rap is not null || old?.RapFromRolimons == true);
                    }
                    _rolimonsAt = now;
                }
                else Log.Warn("[mercado] Rolimons: " + items.Problem);
            }

            // Every account's items (the Limiteds tab shows sales per day for all of them).
            var wanted = _vault.Accounts.All.Where(a => a.Inventory is { } inv)
                .SelectMany(a => Valuation.Value(a.Inventory!, Distribution, _market)).Where(v => !v.Small)
                .Select(v => v.Copy.AssetId).Distinct()
                // Never read first, then the oldest attempt: an item that keeps failing waits its turn instead of
                // taking every pass (its attempt time is recorded below).
                // Only items whose Marketplace id is known (or known to be none): before that, the classic answer may be
                // the one frozen at the migration ("0/dia").
                .Where(id => Seller.IdKnown(id))
                .Where(id => _market.GetValueOrDefault(id) is not { } m || now - m.AtUtc >= (m.SalesPerDay is null ? LiquidityRetry : LiquidityInterval)
                             // a Marketplace item read on the classic endpoint (its id was not known yet): read it again now
                             || (_sellReader?.KnownCollectibleId(id) is not null && !_liquidityMarketTried.Contains(id)))
                .OrderBy(id => _market.GetValueOrDefault(id)?.AtUtc ?? DateTimeOffset.MinValue)
                .Take(MaxLiquidityReadsPerPass).ToList();
            foreach (long id in wanted)
            {
                if (_closing || DateTimeOffset.UtcNow < _robloxPauseUntil) break;
                // Marketplace items have their own resale data, read logged in as roblox.com does (the classic one froze at
                // the migration: old RAP, "0/dia"). It is never used for them: unknown stays unknown, not 0.
                string? cid = _sellReader?.KnownCollectibleId(id);
                ReadResult<(long? Rap, double? SalesPerDay)> r;
                if (cid is not null)
                {
                    _liquidityMarketTried.Add(id);
                    if (OwnerOf(id) is not { } owner) continue;
                    var ctx = new SessionContext(owner.RevealSession());
                    string sent = ctx.Session;
                    r = await _api.GetMarketResaleDataAsync(ctx, cid, now);
                    if (ctx.Rotated is { } fresh && _vault?.Accounts.ReplaceSessionIf(owner.UserId, sent, fresh) == true) _browser.Find(owner.UserId)?.ReplaceCookie(fresh);
                }
                else r = await _api.GetResaleDataAsync(id, now);
                if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(0, r.RetryAfter); break; }
                var old = _market.GetValueOrDefault(id);
                if (!r.Ok)
                {
                    if (cid is not null) Log.Warn("[mercado] vendas por dia (Marketplace): " + r.Problem);
                    _market[id] = (old ?? new ItemMarket(id, null, null, null, null, null, now)) with { AtUtc = now, SalesPerDay = cid is not null ? null : old?.SalesPerDay };
                    continue;
                }
                // Rolimons' RAP wins; Roblox's fills the items Rolimons does not track.
                long? rap = old is { RapFromRolimons: true, Rap: { } rr } ? rr : r.Value.Rap ?? old?.Rap;
                _market[id] = new ItemMarket(id, old?.Name, rap, old?.RolimonsValue, old?.Demand, r.Value.SalesPerDay, now, old?.RapFromRolimons == true);
                await Task.Delay(400);
            }
            SaveMarketCache();
        }
        catch (Exception ex) { Log.Error("Market refresh failed", ex); }
        finally
        {
            _marketBusy = false;
            if (!_closing) UpdateFinancePanel();
        }
    }

    private void LoadMarketCache()
    {
        try
        {
            if (!File.Exists(MarketCachePath)) return;
            var c = JsonSerializer.Deserialize<MarketCache>(File.ReadAllText(MarketCachePath));
            if (c is null) return;
            _market = c.Items.ToDictionary(i => i.AssetId);
            _savedCollectibleIds = c.CollectibleIds ?? new();
            // A cache from before 0.7.4 does not mark Rolimons' RAP: read Rolimons again now, so a stale Roblox RAP
            // (frozen at the Marketplace migration) is replaced right away instead of in 30 min.
            _rolimonsAt = _market.Values.Any(m => m.RapFromRolimons) ? c.RolimonsAt : null;
        }
        catch (Exception e) when (e is JsonException or IOException) { Log.Warn("[mercado] cache ilegível; será lido de novo"); }
    }

    private void SaveMarketCache()
    {
        var ids = new Dictionary<long, string?>(_savedCollectibleIds);
        foreach (var (id, cid) in _sellReader?.CollectibleIds ?? new Dictionary<long, string?>()) ids[id] = cid;
        try { File.WriteAllText(MarketCachePath, JsonSerializer.Serialize(new MarketCache(_rolimonsAt, _market.Values.ToList(), ids))); }
        catch (IOException e) { Log.Warn("[mercado] cache não gravado: " + e.Message); }
    }

    // ───────────── screen ─────────────

    private (string Text, Color Ink, long Sort, string? Tip) SpaceCell(Account a)
    {
        switch (RoleOf(a))
        {
            case Role.None: return ("—", Theme.TextDim, -3, "Fora da distribuição");
            case Role.Source: return ("origem", Theme.Text2, -2, "Conta de origem (saques AstraFlip)");
        }
        var v = Valuation.Space(a.UserId, a.Finance, PlanInventoryOf(a), Distribution, _market, DateTimeOffset.UtcNow);
        return v.SpaceNow is { } sp && !v.Blocked
            ? (Fmt.N(sp), sp > 0 ? Theme.Text : Theme.TextDim, sp, $"Espaço para novos lotes agora; na venda (em {Distribution.DaysToSale} dias): {Fmt.N(v.SpaceAtSale ?? sp)}")
            : ("bloqueada", Theme.WarnInk, -1, "Sem novos lotes: " + string.Join("; ", v.Blocks));
    }

    private LimitedsDetail LimitedsDetailOf(Account a)
    {
        var role = RoleOf(a);
        var s = Distribution;
        string roleText = role switch
        {
            Role.Source => "Conta de origem: os Limiteds que não são Small vieram dos saques da AstraFlip.",
            Role.Destination => "Destinatária: recebe lotes enquanto houver espaço.",
            _ => s.SourceAccounts.Count == 0
                ? "Distribuição ainda não configurada (Configurações → Distribuição de Limiteds)."
                : "Fora da distribuição (não é origem nem está no grupo das destinatárias).",
        };
        var inv = PlanInventoryOf(a); // as the plan counts it (Rolimons-only copies checked)
        var space = role == Role.Destination ? Valuation.Space(a.UserId, a.Finance, inv, s, _market, DateTimeOffset.UtcNow) : null;
        var sb = new StringBuilder();
        if (inv is not null)
        {
            var valued = Valuation.Value(inv, s, _market);
            var main = valued.Where(v => !v.Small).OrderByDescending(v => v.Net ?? -1).ToList();
            var smalls = valued.Where(v => v.Small).ToList();
            sb.Append($"{Fmt.Count(valued.Count, "Limited", "Limiteds")} · {valued.Count(v => v.OnHold)} em holding · {Fmt.Count(smalls.Count, "Small", "Smalls")}");
            if (role == Role.Source)
            {
                var ready = main.Where(v => !v.OnHold && v.Net is not null).ToList();
                sb.Append($"\nProntos para distribuir: {ready.Count} · líquido {Fmt.N(ready.Sum(v => v.Net!.Value))}");
            }
            if (inv.Ambiguous > 0) sb.Append($" · {inv.Ambiguous} a conferir (sem id da cópia)");
            foreach (var v in main.Take(8))
            {
                var m = _market.GetValueOrDefault(v.Copy.AssetId);
                sb.Append('\n').Append(v.Copy.Name).Append(v.Copy.Serial is { } sn ? $" #{sn}" : "")
                  .Append(" — ").Append(v.Net is { } n ? $"líquido {Fmt.N(n)} ({SourceWord(v.Source)})" : "sem preço")
                  .Append(m?.SalesPerDay is { } pd ? $" · {Planner.Liquidity(m).Text}" : m?.Demand is { } dm ? $" · demanda {dm}/4" : "")
                  .Append(v.OnHold ? " · em holding" : "");
            }
            if (main.Count > 8) sb.Append($"\n… e mais {main.Count - 8}");
            if (smalls.Count > 0) sb.Append("\nSmalls: ").Append(string.Join(", ", smalls.Take(6).Select(v => v.Copy.Name + (v.OnHold ? " (holding)" : ""))))
                                    .Append(smalls.Count > 6 ? "…" : "");
        }
        string read = inv?.ReadUtc is { } at ? $"Limiteds lidos do Roblox em {Fmt.Time(at)}" : "Limiteds ainda não lidos";
        if (inv?.Problem is { } p) read += $" · última tentativa recusada: {p}";
        if (_rolimonsAt is { } ra) read += $" · valores da Rolimons de {Fmt.Time(ra)}";
        return new LimitedsDetail(roleText, space, s.DaysToSale, sb.ToString(), read,
            a.HasSession && a.Status != SessionStatus.Expired && !_inventoryBusy.Contains(a.UserId), _inventoryBusy.Contains(a.UserId));
    }

    private static string SourceWord(PriceSource s) => s switch
    {
        PriceSource.Manual => "preço manual",
        PriceSource.Rolimons => "valor Rolimons",
        _ => "RAP",
    };
}
