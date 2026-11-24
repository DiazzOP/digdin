using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App;

/// <summary>
/// The "Limiteds" category: every account's Limiteds for selling (on hold, ready, on sale, outbid), read from Roblox
/// one account at a time, with the cheapest listings of each item. Read-only: "Abrir para vender" / "Mudar preço" open
/// the item page in that account's tab and the person sells there. The app never lists, reprices or takes off sale.
/// </summary>
internal sealed partial class MainForm
{
    private const int ViewLimiteds = 4;
    /// <summary>Automatic re-read of what is on sale and the cheapest prices (only while something is for sale or ready).</summary>
    private static readonly TimeSpan SellInterval = TimeSpan.FromMinutes(15);
    /// <summary>Opening the tab re-reads when the last read is older than this.</summary>
    private static readonly TimeSpan SellStaleOnOpen = TimeSpan.FromMinutes(10);

    private readonly LimitedsView _sellPage = new() { Visible = false };
    /// <summary>The cheapest listings of each item, kept in the vault (they survive a restart; they hold the accounts' own listings).</summary>
    private Dictionary<long, ItemListings> _resellers => _vault?.Resellers ?? _noResellers;
    private readonly Dictionary<long, ItemListings> _noResellers = new();
    private SellReader? _sellReader;
    private bool _sellBusy;
    private DateTimeOffset? _sellAt;
    private string? _sellRecording;

    private SellReader Seller
    {
        get
        {
            if (_sellReader is null) { _sellReader = new SellReader(_api); _sellReader.Seed(_savedCollectibleIds); }
            return _sellReader;
        }
    }

    private void WireSelling()
    {
        _sellPage.ThumbOf = assetId => _thumbs.GetValueOrDefault("a" + assetId);
        _sellPage.NeedThumbs += items => _ = LoadThumbsAsync(
            [.. items.DistinctBy(i => i.AssetId).Select(i => new AvatarItem(ItemKind.Asset, i.AssetId, i.Name, 0, 0, null, null, false))]);
        _sellPage.RefreshRequested += () => _ = RefreshSellingAsync(full: true);
        _sellPage.ConfigureRequested += EditDistribution;
        _sellPage.RecordRequested += () => _ = RecordSellPageAsync();
        _sellPage.OpenRequested += row => _ = OpenForSaleAsync(row);
        _sellPage.RereadRequested += row => _ = RereadCopyAsync(row);
    }

    // ───────────── board ─────────────

    private SellBoardResult BuildSellBoard(out int hidden)
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = _vault!.Accounts.All.Where(a => a.HasSession || a.Inventory is not null).ToList();
        var inputs = accounts.Select(a => new SellInput(
            new AccountSnapshot(a.UserId, a.Username, a.Group, a.HasSession && a.Status != SessionStatus.Expired, a.Finance, a.Inventory),
            a.Title, a.Listings, a.Sales, a.Arrivals)).ToList();
        var board = SellBoard.Build(inputs, Distribution, _market, _resellers, Plan, now);
        hidden = accounts.Count(a => a.Inventory is { Complete: true } && board.Accounts.All(x => x.UserId != a.UserId));
        return board;
    }

    /// <summary>Recomputes the panel (cheap) and the outbid count on the "Limiteds" tab.</summary>
    private void UpdateSelling()
    {
        if (_vault is null) return;
        var board = BuildSellBoard(out int hidden);
        int outbid = board.Rows.Count(r => r.Situation == SellSituation.Outbid && !r.Small);
        _tabs.SetCounts(null, null, null, null, outbid > 0 ? outbid : null);
        if (!_sellPage.Visible) return;
        var mine = _vault.Accounts.All.ToDictionary(a => a.UserId, a => a.Username);
        _sellPage.SetBoard(board, _resellers, _market, mine, hidden, Distribution.FeePercent, _sellBusy, _sellRecording,
            null,
            pendingAll: _vault.Accounts.All.Where(a => a.HasSession).Sum(a => a.Finance?.PendingRobux ?? 0));
    }

    private void OnLimitedsShown()
    {
        UpdateSelling();
        if (_sellAt is not { } at || DateTimeOffset.UtcNow - at > SellStaleOnOpen) _ = RefreshSellingAsync(full: false);
    }

    /// <summary>From the finance timer: keeps "superada" current while something is for sale or ready.</summary>
    private void StartAutomaticSellRefresh()
    {
        // Not while the balances are being read: one batch of Roblox reads at a time.
        if (_vault is null || _sellBusy || _closing || _financeCts is not null || DateTimeOffset.UtcNow < _robloxPauseUntil) return;
        if (_sellAt is { } at && DateTimeOffset.UtcNow - at < SellInterval) return;
        bool anything = _vault.Accounts.All.Any(a => a.Inventory?.Copies.Any(c => c.OnHold != true) == true);
        if (anything) _ = RefreshSellingAsync(full: false);
    }

    // ───────────── reads ─────────────

    /// <summary>
    /// Every read of this tab, in an order where nothing starves: each account's Limiteds (when due, or always with
    /// <paramref name="full"/>), the Marketplace ids of every item (a few requests in batches), the cheapest listings of
    /// every item, then each account's own listings — items read longest ago first, so an interrupted pass is picked up
    /// where it stopped. When Roblox limits the requests (429) the reads wait for the pause and go on; they are not
    /// dropped. A failure only affects the item that failed.
    /// </summary>
    private async Task RefreshSellingAsync(bool full)
    {
        if (_vault is not { } vault || _sellBusy) return;
        _sellBusy = true;
        UpdateSelling();
        try
        {
            var accounts = vault.Accounts.All.Where(a => a.HasSession && a.Status != SessionStatus.Expired).ToList();
            int i = 0;
            foreach (var a in accounts)
            {
                if (_closing || _vault != vault) return;
                await WaitRobloxPauseAsync("as leituras dos Limiteds");
                SetStatus($"Limiteds: lendo @{a.Username} ({++i}/{accounts.Count})…");
                if (full ? a.Inventory?.LastAttemptUtc is not { } at || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(2) : InventoryDue(a))
                    await RefreshInventoryAsync(a, force: true);
            }
            if (_vault != vault) return;
            await EnsureCollectibleIdsAsync();
            await ReadResellersAsync(full: full);
            i = 0;
            foreach (var a in accounts)
            {
                if (_closing || _vault != vault) return;
                SetStatus($"Limiteds: anúncios de @{a.Username} ({++i}/{accounts.Count})…");
                await ReadOwnListingsAsync(a, full);
                UpdateSelling();
            }
            foreach (var a in accounts.Where(a => a.Inventory?.Copies.Count > 0 || HasPendingRobux(a)))
            {
                if (_closing || _vault != vault) break;
                await WaitRobloxPauseAsync("as vendas");
                await ReadSalesAsync(a, force: full);
            }
            // When the held copies leave the hold: their purchases and trades.
            foreach (var a in accounts.Where(a => a.Inventory is { } inv && inv.Copies.Any(inv.IsOnHold)))
            {
                if (_closing || _vault != vault) break;
                await ReadArrivalsAsync(a, force: full);
            }
            await EnsureMarketAsync(); // sales per day (Marketplace items, now that their ids are known)
            _sellAt = DateTimeOffset.UtcNow;
            SetStatus("Limiteds atualizados.");
        }
        catch (Exception ex) { Log.Error("Limiteds refresh failed", ex); }
        finally
        {
            _sellBusy = false;
            if (!_closing) { RequestSave(); UpdateSelling(); }
        }
    }

    /// <summary>The items this tab reads: every account's released, non-Small copies.</summary>
    private List<(long AssetId, Account Owner)> SellItems(long? onlyAsset = null)
    {
        var owners = new Dictionary<long, Account>();
        foreach (var a in _vault!.Accounts.All.Where(a => a.HasSession && a.Status != SessionStatus.Expired && a.Inventory is { Complete: true }))
            foreach (var c in a.Inventory!.Copies.Where(c => !a.Inventory.IsOnHold(c) && !Valuation.IsSmall(c, Distribution, _market)))
                if ((onlyAsset is null || c.AssetId == onlyAsset) && !owners.ContainsKey(c.AssetId)) owners[c.AssetId] = a;
        return [.. owners.Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>The Marketplace ids of every item not known yet, in batches of 50 (they are kept with the market cache).</summary>
    private async Task EnsureCollectibleIdsAsync()
    {
        if (_vault is not { } vault) return;
        var items = SellItems();
        // Held copies too: their sales per day are shown, and they will be sold.
        var all = vault.Accounts.All.SelectMany(a => a.Inventory?.Copies ?? []).Where(c => !Valuation.IsSmall(c, Distribution, _market))
            .Select(c => c.AssetId).Concat(items.Select(x => x.AssetId)).Distinct().Where(id => !Seller.IdKnown(id)).ToList();
        if (all.Count == 0 || items.Select(x => x.Owner).Concat(vault.Accounts.All.Where(a => a.HasSession && a.Status != SessionStatus.Expired)).FirstOrDefault() is not { } who) return;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await WaitRobloxPauseAsync("a busca dos itens no Marketplace");
            if (_closing || _vault != vault) return;
            var ctx = new SessionContext(who.RevealSession());
            string sent = ctx.Session;
            var r = await Seller.EnsureIdsAsync(ctx, all);
            if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(who.UserId, sent, fresh)) _browser.Find(who.UserId)?.ReplaceCookie(fresh);
            if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(who.UserId, r.RetryAfter); continue; }
            if (r.Ok && r.Value > 0) Log.Warn($"[limiteds] {r.Value} itens sem id do Marketplace por enquanto; tenta de novo na próxima leitura");
            break;
        }
        SaveMarketCache();
    }

    /// <summary>
    /// Which of the account's released copies are on sale, item by item (its own session), longest unread first.
    /// Light pass: items with a copy on sale and items not read for an hour. Waits out Roblox's pauses (429).
    /// </summary>
    private async Task ReadOwnListingsAsync(Account a, bool full, long? onlyAsset = null)
    {
        if (_vault is not { } vault || a.Inventory is not { Complete: true } inv) return;
        var listings = a.Listings ?? new AccountListings();
        var now = DateTimeOffset.UtcNow;
        var assets = inv.Copies.Where(c => !inv.IsOnHold(c) && !Valuation.IsSmall(c, Distribution, _market)).Select(c => c.AssetId).Distinct()
            .Where(id => onlyAsset is null || id == onlyAsset)
            .Where(id => !Seller.OwnCopiesUnreadable(id)) // classic-only item Roblox does not answer for: its listings say it
            .Where(id => full || onlyAsset is not null || !listings.ItemReadUtc.TryGetValue(id, out var read) || now - read > TimeSpan.FromHours(1)
                         || inv.Copies.Any(c => c.AssetId == id && listings.PriceByCopy.ContainsKey(c.UserAssetId)))
            .OrderBy(id => listings.ItemReadUtc.TryGetValue(id, out var read) ? read : DateTimeOffset.MinValue)
            .ToList();
        if (assets.Count == 0) return;
        var ctx = new SessionContext(a.RevealSession());
        string sent = ctx.Session;
        int failed = 0;
        try
        {
            foreach (long asset in assets)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    await WaitRobloxPauseAsync("os anúncios");
                    if (_closing || _vault != vault) return;
                    var r = await Seller.OwnCopiesAsync(ctx, a.UserId, asset);
                    if (_vault != vault) return;
                    if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(a.UserId, r.RetryAfter); continue; } // wait, then this item again
                    if (r.Outcome == ReadOutcome.Unauthorized) { listings.Problem = "sessão recusada ao ler os anúncios"; return; }
                    if (r.Ok) { listings.Apply(asset, r.Value!, inv.Copies, DateTimeOffset.UtcNow); listings.Problem = null; }
                    else { failed++; Log.Warn($"[limiteds] anúncios de @{a.Username}, item {asset}: {r.Problem}"); }
                    break;
                }
                await Task.Delay(250);
            }
            if (failed == 0) listings.Problem = null;
        }
        finally
        {
            if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(a.UserId, sent, fresh)) _browser.Find(a.UserId)?.ReplaceCookie(fresh);
            a.Listings = listings;
        }
    }

    /// <summary>
    /// The cheapest listings of each item that is ready or on sale somewhere (with an owning account's session), longest
    /// unread first; light pass: those older than 10 min. Kept in the vault. Waits out Roblox's pauses (429).
    /// </summary>
    private async Task ReadResellersAsync(long? onlyAsset = null, bool full = true)
    {
        if (_vault is not { } vault) return;
        var now = DateTimeOffset.UtcNow;
        var items = SellItems(onlyAsset)
            .Where(x => full || onlyAsset is not null || !_resellers.TryGetValue(x.AssetId, out var old) || now - old.ReadUtc > TimeSpan.FromMinutes(10))
            .OrderBy(x => _resellers.TryGetValue(x.AssetId, out var old) ? old.ReadUtc : DateTimeOffset.MinValue).ToList();
        int n = 0;
        foreach (var (asset, owner) in items)
        {
            SetStatus($"Limiteds: menores preços ({++n}/{items.Count})…");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await WaitRobloxPauseAsync("os menores preços");
                if (_closing || _vault != vault) return;
                var ctx = new SessionContext(owner.RevealSession());
                string sent = ctx.Session;
                var r = await Seller.ListingsAsync(ctx, asset);
                if (_vault != vault) return;
                if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(owner.UserId, sent, fresh)) _browser.Find(owner.UserId)?.ReplaceCookie(fresh);
                if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(owner.UserId, r.RetryAfter); continue; } // wait, then this item again
                if (r.Ok) _resellers[asset] = new ItemListings(asset, r.Value!.Items, DateTimeOffset.UtcNow, r.Value.Complete);
                else Log.Warn($"[limiteds] menores preços do item {asset}: {r.Problem}");
                break;
            }
            await Task.Delay(250);
        }
        // Items no account has any more leave the vault.
        if (onlyAsset is null)
        {
            var owned = vault.Accounts.All.SelectMany(a => a.Inventory?.Copies ?? []).Select(c => c.AssetId).ToHashSet();
            foreach (var gone in _resellers.Keys.Where(id => !owned.Contains(id)).ToList()) _resellers.Remove(gone);
        }
    }

    /// <summary>
    /// Where the account's held copies came from: its store purchases of the last 9 days (+7 days of hold) and the items
    /// it received in its completed trades of the last 3 days (+2 days). Each trade is read once (a completed trade does
    /// not change). Skipped when every held copy is already explained and the last read is recent.
    /// </summary>
    private async Task ReadArrivalsAsync(Account a, bool force = false)
    {
        if (_vault is not { } vault || !a.HasSession || a.Status == SessionStatus.Expired || a.Inventory is not { } inv) return;
        var held = inv.Copies.Where(inv.IsOnHold).ToList();
        if (held.Count == 0) return;
        var arrivals = a.Arrivals ?? new AccountArrivals();
        var now = DateTimeOffset.UtcNow;
        if (!force && arrivals.ReadUtc is { } last)
        {
            var known = HoldClock.ForAccount(held, arrivals, AcceptedTrades(), now);
            bool allExplained = held.All(c => known.TryGetValue(c.UserAssetId, out var h) && h.Kind is not null);
            if (now - last < (allExplained ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(20))) return;
        }
        var ctx = new SessionContext(a.RevealSession());
        string sent = ctx.Session;
        try
        {
            await WaitRobloxPauseAsync("as compras e trades");
            if (_closing || _vault != vault) return;
            var buys = await _api.GetPurchasesAsync(ctx, a.UserId, now - HoldClock.Lookback);
            if (buys.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(a.UserId, buys.RetryAfter); return; }
            if (buys.Ok) foreach (var arrival in HoldClock.FromPurchases(buys.Value!)) arrivals.Add(arrival);
            else Log.Warn($"[limiteds] compras de @{a.Username}: {buys.Problem}");

            await WaitRobloxPauseAsync("as compras e trades");
            if (_closing || _vault != vault) return;
            var trades = await _api.GetTradesAsync(ctx, "Completed");
            if (trades.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(a.UserId, trades.RetryAfter); return; }
            if (!trades.Ok) { arrivals.Problem = trades.Problem; Log.Warn($"[limiteds] trades de @{a.Username}: {trades.Problem}"); }
            else
            {
                var accepted = AcceptedTrades();
                foreach (var t in trades.Value!.Where(t => t.CreatedUtc is not { } c || now - c <= HoldClock.TradeLookback)
                                                .Where(t => !arrivals.TradesRead.Contains(t.Id)).Take(12))
                {
                    await WaitRobloxPauseAsync("as trades");
                    if (_closing || _vault != vault) return;
                    var d = await _api.GetTradeAsync(ctx, t.Id);
                    if (d.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(a.UserId, d.RetryAfter); break; }
                    if (!d.Ok || !d.Value!.IsCompleted) continue;
                    foreach (var arrival in HoldClock.FromTrade(d.Value, a.UserId, accepted.TryGetValue(t.Id, out var at) ? at : null, t.CreatedUtc ?? now))
                        arrivals.Add(arrival);
                    arrivals.TradesRead.Add(t.Id);
                    await Task.Delay(250);
                }
                arrivals.Problem = null;
            }
            arrivals.ReadUtc = now;
            arrivals.Prune(now);
        }
        finally
        {
            if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(a.UserId, sent, fresh)) _browser.Find(a.UserId)?.ReplaceCookie(fresh);
            a.Arrivals = arrivals;
            RequestSave();
        }
    }

    /// <summary>Trades accepted in any account's tab of the app, and when (they completed then).</summary>
    private Dictionary<long, DateTimeOffset> AcceptedTrades()
    {
        var all = new Dictionary<long, DateTimeOffset>();
        foreach (var a in _vault?.Accounts.All ?? [])
            foreach (var (id, at) in a.Arrivals?.Accepted ?? []) all.TryAdd(id, at);
        return all;
    }

    // ───────────── actions ─────────────

    /// <summary>Opens the item page in the copy's account; the suggested price goes to the clipboard.</summary>
    private async Task OpenForSaleAsync(SellRow row)
    {
        if (_vault?.Accounts.Find(row.AccountId) is not { } a) return;
        await SwitchToAsync(a);
        if (_browser.Active?.UserId != a.UserId) return;
        Go($"https://www.roblox.com/catalog/{row.AssetId}");
        ShowView(ViewContas);
        string copied = "";
        if (row.Suggestion is { } sug)
            try { Clipboard.SetText(sug.ToString()); copied = $" Sugestão {Fmt.N(sug)} copiada."; } catch (Exception) { }
        string what = row.MyPrice is null ? "use Vender" : "mude o preço do seu anúncio";
        SetStatus($"@{a.Username}: página de {row.Label} — {what} e confirme no Roblox.{copied} Depois use Reler na aba Limiteds.");
    }

    private async Task RereadCopyAsync(SellRow row)
    {
        if (_vault is not { } vault || vault.Accounts.Find(row.AccountId) is not { } a || _sellBusy) return;
        _sellBusy = true;
        UpdateSelling();
        try
        {
            SetStatus($"Relendo {row.Label} em @{a.Username}…");
            await WaitRobloxPauseAsync("a releitura");
            await RefreshInventoryAsync(a, force: true);
            await ReadOwnListingsAsync(a, full: true, onlyAsset: row.AssetId);
            if (_vault == vault) await ReadResellersAsync(row.AssetId);
            if (_vault == vault && row.Situation == SellSituation.OnHold) await ReadArrivalsAsync(a, force: true);
            SetStatus($"{row.Label} relido.");
        }
        catch (Exception ex) { Log.Error("Limited re-read failed", ex); }
        finally
        {
            _sellBusy = false;
            if (!_closing) { RequestSave(); UpdateSelling(); }
        }
    }

    /// <summary>
    /// Records, value-free, the calls roblox.com makes for 60 s in the copy's account tab while the person opens a sell
    /// page — to confirm the endpoints this tab reads. Saved to venda-chamadas.txt in the data folder.
    /// </summary>
    private async Task RecordSellPageAsync()
    {
        if (_vault is not { } vault || _sellRecording is not null) return;
        var row = _sellPage.SelectedRow;
        var account = row is not null ? vault.Accounts.Find(row.AccountId) : ActiveAccount();
        if (account is null) { SetStatus("Escolha uma cópia na lista (ou abra uma conta) antes de gravar."); return; }
        if (_browser.Active?.UserId != account.UserId) await SwitchToAsync(account);
        if (_browser.Find(account.UserId) is not { Ready: true } tab) { SetStatus("Abra a conta no navegador do app antes de gravar."); return; }
        var calls = new List<ApiCall>();
        tab.StartApiRecording(c => BeginInvoke(() => calls.Add(c)));
        if (row is not null) Go($"https://www.roblox.com/catalog/{row.AssetId}");
        ShowView(ViewContas);
        ShowBanner($"Gravando 60 s na aba de @{account.Username}: na página do item, abra Vender (ou o anúncio) e veja os vendedores. Pode cancelar antes de confirmar — nada é vendido pelo app.");
        try
        {
            for (int left = 60; left > 0 && !_closing; left--)
            {
                _sellRecording = $"Gravando… {left} s";
                SetStatus($"Gravando a página de venda na aba de @{account.Username}: faltam {left} s. Não feche o app até terminar.");
                UpdateSelling();
                await Task.Delay(1000);
            }
        }
        finally
        {
            tab.StopApiRecording();
            _sellRecording = null;
            HideBanner();
            UpdateSelling();
        }
        try
        {
            File.WriteAllText(Path.Combine(_dataDir, "venda-chamadas.txt"),
                $"RbxSwitcher {Application.ProductVersion} — chamadas da página de venda (sem valores)\n{ApiCalls.Format(calls)}\n");
            SetStatus($"Gravação salva em venda-chamadas.txt ({calls.Count} chamadas). Configurações → Abrir pasta de dados: envie esse arquivo para ajustar as leituras.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Não deu para salvar a gravação: " + ex.Message); }
    }
}
