using System.Text.Json;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App;

/// <summary>
/// The "Avatares" category: free catalog (cached on disk for 24 h — public data only), the open account's own
/// items and full-body picture (memory only), thumbnails (memory only), and applying designs to one or many
/// accounts through <see cref="AvatarApplier"/>.
/// </summary>
internal sealed partial class MainForm
{
    private const int ViewContas = 0, ViewPlus = 1, ViewAvatares = 2;
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromHours(24);

    private readonly AvatarsView _avatarsPage = new() { Visible = false };
    private AvatarApplier? _avatarApplier;
    private List<AvatarItem> _freeCatalog = [];
    private DateTimeOffset? _catalogAt;
    private bool _catalogLoading;
    private string? _catalogProblem;
    private readonly Dictionary<long, List<AvatarItem>> _ownedItems = new();
    private readonly HashSet<long> _ownedLoading = new();
    private readonly Dictionary<string, Image> _thumbs = new();
    private readonly HashSet<string> _thumbPending = new();
    private readonly Dictionary<long, Image> _fullAvatars = new();
    private CancellationTokenSource? _avatarCts;
    private bool _avatarApplying;

    private AvatarApplier Applier => _avatarApplier ??= new AvatarApplier(_api);
    private string CatalogCachePath => Path.Combine(_dataDir, "avatar-catalog.json");

    /// <summary>Version 2: items carry their Marketplace id (bundles too). Older caches are read again.</summary>
    private sealed record CatalogCache(DateTimeOffset SavedUtc, List<AvatarItem> Items, int Version = 0);
    private const int CatalogCacheVersion = 2;

    private void WireAvatars()
    {
        _avatarsPage.ThumbOf = key => _thumbs.GetValueOrDefault(key);
        _avatarsPage.NeedThumbs += items => _ = LoadThumbsAsync(items);
        _avatarsPage.CatalogRefreshRequested += () => _ = EnsureCatalogAsync(force: true);
        _avatarsPage.ApplyActiveRequested += d => _ = ApplyAvatarActiveAsync(d);
        _avatarsPage.ApplyManyRequested += ApplyAvatarMany;
        _avatarsPage.AccountChosen += async id => { if (_vault?.Accounts.Find(id) is { } a) await SwitchToAsync(a); };
        _avatarsPage.PresetSaveRequested += (name, design) =>
        {
            if (_vault is null) return;
            var existing = _vault.Avatars.FindIndex(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
            var preset = new AvatarPreset { Name = name, SavedUtc = DateTimeOffset.UtcNow, Design = design };
            if (existing >= 0) _vault.Avatars[existing] = preset; else _vault.Avatars.Add(preset);
            RequestSave();
            _avatarsPage.SetPresets(_vault.Avatars);
            _avatarsPage.ShowResult(existing >= 0 ? $"\"{name}\" atualizado." : $"\"{name}\" salvo.", NoteKind.Ok);
        };
        _avatarsPage.PresetDeleteRequested += p =>
        {
            if (_vault is null) return;
            if (MessageBox.Show(this, $"Excluir o avatar salvo \"{p.Name}\"?", "Avatares", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _vault.Avatars.Remove(p);
            RequestSave();
            _avatarsPage.SetPresets(_vault.Avatars);
        };
    }

    private async Task ShowAvatarsAsync()
    {
        if (_vault is null) return;
        _avatarsPage.SetPresets(_vault.Avatars);
        _avatarsPage.SetAccountChoices(_vault.Accounts.All.Where(a => a.HasSession).OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
            .Select(a => (a.UserId, "@" + a.Username)).ToList(), ActiveAccount()?.UserId);
        UpdateAvatarsAccount();
        await EnsureCatalogAsync(force: false);
        if (ActiveAccount() is { } account)
        {
            _ = LoadFullAvatarAsync(account.UserId, force: false);
            await LoadOwnedAsync(account, force: false);
        }
    }

    private void UpdateAvatarsAccount()
    {
        var a = ActiveAccount();
        _avatarsPage.SetAccount(a, a is null ? null : _ownedItems.GetValueOrDefault(a.UserId), a is not null && _ownedLoading.Contains(a.UserId));
        _avatarsPage.SetAvatarImage(a is null ? null : _fullAvatars.GetValueOrDefault(a.UserId));
    }

    private void ShowCatalogStatus()
    {
        string status = _catalogLoading ? "Carregando o catálogo grátis do Roblox…"
            : _freeCatalog.Count == 0 ? ""
            : $"{_freeCatalog.Count} itens grátis do Roblox" + (_catalogAt is { } at ? $" · catálogo de {Fmt.Time(at)}" : "")
              + (_catalogProblem is not null ? " · parte das buscas falhou (detalhes no log)" : "");
        // The full reason (every search) goes to the log; the screen says it in plain words.
        string? problem = _freeCatalog.Count > 0 || _catalogProblem is not { } raw ? null
            : raw.Contains("sem conexão") ? "sem conexão com o Roblox"
            : raw.Contains("429") ? "o Roblox limitou os pedidos; tente em alguns minutos"
            : "o Roblox respondeu de um jeito inesperado (detalhes no log)";
        _avatarsPage.SetCatalog(_freeCatalog, status, _catalogLoading, problem);
    }

    /// <summary>Free items from the disk cache when fresh, otherwise from Roblox's catalog search (public, no session).</summary>
    private async Task EnsureCatalogAsync(bool force)
    {
        if (_catalogLoading) return;
        if (!force && _freeCatalog.Count == 0 && File.Exists(CatalogCachePath))
        {
            try
            {
                var cache = JsonSerializer.Deserialize<CatalogCache>(await File.ReadAllTextAsync(CatalogCachePath));
                if (cache is { Items.Count: > 0, Version: CatalogCacheVersion })
                {
                    _freeCatalog = cache.Items.Where(i => i.Free && i.Category is not null).ToList();
                    _catalogAt = cache.SavedUtc;
                }
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { Log.Warn("[catálogo] cache ilegível; será lido de novo"); }
        }
        if (!force && _freeCatalog.Count > 0 && _catalogAt is { } at && DateTimeOffset.UtcNow - at < CatalogMaxAge) { ShowCatalogStatus(); return; }

        _catalogLoading = true;
        ShowCatalogStatus();
        try
        {
            var active = ActiveAccount();
            var ctx = active is { HasSession: true } ? new SessionContext(active.RevealSession()) : null;
            string? sent = ctx?.Session;
            var res = await _api.GetFreeCatalogAsync(ctx);
            if (ctx?.Rotated is { } fresh && active is not null && _vault?.Accounts.ReplaceSessionIf(active.UserId, sent!, fresh) == true)
            {
                _browser.Find(active.UserId)?.ReplaceCookie(fresh);
                RequestSave();
            }
            if (res.Ok && res.Value!.Count > 0)
            {
                _freeCatalog = res.Value;
                _catalogAt = DateTimeOffset.UtcNow;
                _catalogProblem = res.Problem is null ? null : "veja o log";
                try { await File.WriteAllTextAsync(CatalogCachePath, JsonSerializer.Serialize(new CatalogCache(_catalogAt.Value, _freeCatalog, CatalogCacheVersion))); }
                catch (IOException e) { Log.Warn("[catálogo] cache não gravado: " + e.Message); }
                Log.Info($"[catálogo] {_freeCatalog.Count} itens grátis do Roblox");
            }
            else
            {
                _catalogProblem = res.Problem ?? "nenhum item grátis encontrado";
                Log.Warn("[catálogo] não carregou: " + _catalogProblem);
            }
        }
        catch (Exception ex) { Log.Error("Catalog load failed", ex); _catalogProblem = "erro inesperado; veja o log"; }
        finally
        {
            _catalogLoading = false;
            if (!_closing) ShowCatalogStatus();
        }
    }

    /// <summary>The items this account owns in the builder's categories (memory only, per app run).</summary>
    private async Task LoadOwnedAsync(Account account, bool force)
    {
        long id = account.UserId;
        if (!account.HasSession || account.Status == SessionStatus.Expired || _ownedLoading.Contains(id)) return;
        if (!force && _ownedItems.ContainsKey(id)) return;
        _ownedLoading.Add(id);
        UpdateAvatarsAccount();
        try
        {
            var ctx = new SessionContext(account.RevealSession());
            string sent = ctx.Session;
            var res = await _api.GetOwnedItemsAsync(ctx, id);
            if (ctx.Rotated is { } fresh && _vault?.Accounts.ReplaceSessionIf(id, sent, fresh) == true)
            {
                _browser.Find(id)?.ReplaceCookie(fresh);
                RequestSave();
            }
            if (res.Ok) _ownedItems[id] = res.Value!;
            else
            {
                Log.Warn($"[avatar] itens da conta não lidos: {res.Problem}");
                if (res.Outcome == ReadOutcome.Unauthorized) { account.Status = SessionStatus.Expired; _list.RefreshAccount(id); }
            }
        }
        catch (Exception ex) { Log.Error("Owned items load failed", ex); }
        finally
        {
            _ownedLoading.Remove(id);
            if (!_closing) UpdateAvatarsAccount();
        }
    }

    /// <summary>Pictures that did not come: not asked again for 10 min (the lists ask on every repaint).</summary>
    private readonly Dictionary<string, DateTimeOffset> _thumbFailedAt = new();

    private async Task LoadThumbsAsync(IReadOnlyList<AvatarItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = items.Where(i => !_thumbs.ContainsKey(i.Key)
                                      && !(_thumbFailedAt.TryGetValue(i.Key, out var failed) && now - failed < TimeSpan.FromMinutes(10))
                                      && _thumbPending.Add(i.Key)).ToList();
        if (wanted.Count == 0) return;
        try
        {
            var urls = await _api.GetItemThumbnailUrlsAsync(wanted);
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(urls.Select(async kv =>
            {
                await gate.WaitAsync();
                try
                {
                    byte[]? bytes = await _api.DownloadImageAsync(kv.Value);
                    if (bytes is null || _closing) return;
                    try
                    {
                        using var full = Image.FromStream(new MemoryStream(bytes));
                        int size = LogicalToDeviceUnits(96);
                        _thumbs[kv.Key] = new Bitmap(full, size, size); // small copy: hundreds of tiles stay light
                    }
                    catch (ArgumentException) { }
                }
                finally { gate.Release(); }
            }));
        }
        catch (Exception ex) { Log.Error("Thumbnail load failed", ex); }
        finally
        {
            foreach (var i in wanted)
            {
                _thumbPending.Remove(i.Key);
                if (!_thumbs.ContainsKey(i.Key)) _thumbFailedAt[i.Key] = DateTimeOffset.UtcNow;
            }
            if (!_closing) { _avatarsPage.ThumbsLoaded(); _distributePage.ThumbsLoaded(); _sellPage.ThumbsLoaded(); _calendarPage.ThumbsLoaded(); }
        }
    }

    private async Task LoadFullAvatarAsync(long userId, bool force)
    {
        if (!force && _fullAvatars.ContainsKey(userId)) return;
        try
        {
            if (force) await Task.Delay(2500); // Roblox redraws the picture a moment after the change
            if (await _api.GetAvatarImageUrlAsync(userId) is not { } url || await _api.DownloadImageAsync(url) is not { } bytes) return;
            Image img;
            try { img = Image.FromStream(new MemoryStream(bytes)); } catch (ArgumentException) { return; }
            if (_fullAvatars.Remove(userId, out var old)) old.Dispose();
            _fullAvatars[userId] = img;
            if (!_closing && ActiveAccount()?.UserId == userId) _avatarsPage.SetAvatarImage(img);
        }
        catch (Exception ex) { Log.Error("Avatar picture load failed", ex); }
    }

    /// <summary>Free catalog plus every owned list read in this run (free entry wins when both exist).</summary>
    private Dictionary<string, AvatarItem> AvatarCatalogMap()
    {
        var map = new Dictionary<string, AvatarItem>();
        foreach (var i in _ownedItems.Values.SelectMany(x => x)) map[i.Key] = i;
        foreach (var i in _freeCatalog) map[i.Key] = i;
        return map;
    }

    private async Task ApplyAvatarActiveAsync(AvatarDesign design)
    {
        if (_vault is null || _avatarApplying || ActiveAccount() is not { } account) return;
        if (design.ChangesNothing) { _avatarsPage.ShowResult("Nada a aplicar: escolha itens, um tom de pele ou um tipo de corpo.", NoteKind.Warn); return; }
        if (!account.HasSession) { _avatarsPage.ShowResult($"@{account.Username} não tem sessão salva. Entre de novo na conta.", NoteKind.Bad); return; }
        _avatarApplying = true;
        _avatarsPage.SetBusy(true, $"Aplicando em @{account.Username}…");
        _avatarCts = new CancellationTokenSource();
        string sent = account.RevealSession();
        try
        {
            var r = await Applier.ApplyAsync(account.UserId, sent, design, AvatarCatalogMap(), acquireFree: true, _avatarCts.Token);
            HandleAvatarResult(account.UserId, sent, r);
            _avatarsPage.ShowResult(AvatarResultText(account.Username, r), r.Status switch
            {
                ApplyStatus.Applied => NoteKind.Ok,
                ApplyStatus.Partial or ApplyStatus.RateLimited => NoteKind.Warn,
                _ => NoteKind.Bad,
            });
        }
        catch (OperationCanceledException) { _avatarsPage.ShowResult("Cancelado.", NoteKind.Warn); }
        catch (Exception ex) { Log.Error("Avatar apply failed", ex); _avatarsPage.ShowResult("Erro inesperado ao aplicar; veja o log.", NoteKind.Bad); }
        finally
        {
            _avatarApplying = false;
            _avatarCts?.Dispose();
            _avatarCts = null;
            if (!_closing) _avatarsPage.SetBusy(false);
        }
    }

    private void ApplyAvatarMany(AvatarDesign design)
    {
        if (_vault is null || _avatarApplying) return;
        var accounts = _vault.Accounts.All.Where(a => a.HasSession && a.Status != SessionStatus.Expired)
            .OrderBy(a => a.GroupOrDefault).ThenBy(a => a.Username, StringComparer.OrdinalIgnoreCase).ToList();
        if (accounts.Count == 0) { _avatarsPage.ShowResult("Nenhuma conta com sessão ativa.", NoteKind.Warn); return; }
        var catalog = AvatarCatalogMap();
        using var dialog = new AvatarBatchDialog(accounts, _vault.Accounts.Groups, design, _freeCatalog, async (targets, progress, ct) =>
        {
            _avatarApplying = true;
            _avatarsPage.SetBusy(true);
            _avatarCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sentById = targets.ToDictionary(t => t.Account.UserId, t => t.Account.RevealSession());
            var byId = targets.ToDictionary(t => t.Account.UserId, t => t.Account);
            var batchTargets = targets.Select(t => new BatchTarget(t.Account.UserId, t.Account.Username, sentById[t.Account.UserId], t.Design)).ToList();
            try
            {
                await new AvatarBatch(Applier).RunAsync(batchTargets, catalog, acquireFree: true, (t, r, step) =>
                {
                    // The batch continues off the UI thread: hand every update back to it, in order.
                    if (IsDisposed) return;
                    BeginInvoke(() =>
                    {
                        if (r is not null && r.Status != ApplyStatus.Cancelled) HandleAvatarResult(t.UserId, sentById[t.UserId], r);
                        progress(byId[t.UserId], r, step);
                    });
                }, _avatarCts.Token);
                await Task.Yield(); // let the queued updates run before the dialog shows its summary
            }
            finally
            {
                _avatarApplying = false;
                _avatarCts?.Dispose();
                _avatarCts = null;
                if (!_closing) _avatarsPage.SetBusy(false);
            }
        }, ActiveAccount()?.UserId);
        dialog.ShowDialog(this);
    }

    /// <summary>Saves a rotated cookie, marks expired sessions and refreshes pictures and owned items.</summary>
    private void HandleAvatarResult(long id, string sent, AvatarApplyResult r)
    {
        if (_vault is null || _vault.Accounts.Find(id) is not { } acc) return;
        if (r.RotatedSession is { } fresh && _vault.Accounts.ReplaceSessionIf(id, sent, fresh))
        {
            _browser.Find(id)?.ReplaceCookie(fresh);
            RequestSave();
        }
        switch (r.Status)
        {
            case ApplyStatus.SessionExpired:
                acc.Status = SessionStatus.Expired;
                _list.RefreshAccount(id);
                break;
            case ApplyStatus.WrongAccount:
                acc.Status = SessionStatus.Expired;
                _list.RefreshAccount(id);
                ShowBanner($"A sessão salva de @{acc.Username} pertence a outra conta. Entre de novo nela (botão direito → Entrar de novo).");
                break;
            case ApplyStatus.Applied or ApplyStatus.Partial:
                Log.Info($"[avatar] aplicado ({r.Status}); {r.Acquired.Count} grátis obtido(s), {r.Skipped.Count} fora");
                if (r.Acquired.Count > 0) _ownedItems.Remove(id); // read again next time
                _avatars.Forget(id);
                _ = _avatars.LoadAsync([id]);
                if (ActiveAccount()?.UserId == id)
                {
                    _ = LoadFullAvatarAsync(id, force: true);
                    if (r.Acquired.Count > 0 && ActiveAccount() is { } a) _ = LoadOwnedAsync(a, force: true);
                }
                break;
        }
    }

    private static string AvatarResultText(string username, AvatarApplyResult r)
    {
        string got = r.Acquired.Count > 0 ? $" Itens grátis pegos: {string.Join(", ", r.Acquired)}." : "";
        string left = r.Skipped.Count > 0 ? " Ficaram de fora: " + string.Join("; ", r.Skipped.Select(s => $"{s.Name} ({s.Why})")) + "." : "";
        return r.Status switch
        {
            ApplyStatus.Applied => $"Avatar aplicado em @{username}.{got}",
            ApplyStatus.Partial => $"Avatar aplicado em parte em @{username}.{got}{left}",
            ApplyStatus.SessionExpired => $"@{username}: {r.Problem}. Entre de novo na conta.",
            ApplyStatus.RateLimited => $"O Roblox limitou os pedidos agora. Tente de novo em alguns minutos.{got}",
            _ => $"Não foi aplicado em @{username}: {r.Problem}.{got}",
        };
    }

    private void DisposeAvatarImages()
    {
        foreach (var img in _thumbs.Values) img.Dispose();
        _thumbs.Clear();
        foreach (var img in _fullAvatars.Values) img.Dispose();
        _fullAvatars.Clear();
    }
}
