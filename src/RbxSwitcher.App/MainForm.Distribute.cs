using System.Text.RegularExpressions;
using RbxSwitcher.App.Browser;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App;

/// <summary>
/// The "Distribuir" category (steps 2–4): suggestions, "Abrir trade" in the source account, checking the trade Roblox
/// shows (by id or by searching the source's trades), replanning, and sales tied to the delivered copies. The app never
/// sends, accepts or changes a trade: it opens the page and reads Roblox.
/// </summary>
internal sealed partial class MainForm
{
    private const int ViewDistribuir = 3;
    private static readonly TimeSpan SalesInterval = TimeSpan.FromMinutes(25);

    private readonly DistributeView _distributePage = new() { Visible = false };
    private bool _distributeBusy;
    private string _distributeSignature = "";

    private DistributionPlan Plan => _vault!.Plan;

    private void WireDistribute()
    {
        _distributePage.GenerateRequested += () => GeneratePlan(manual: true);
        _distributePage.RefreshRequested += () => _ = RefreshDistributionReadsAsync();
        _distributePage.ConfigureRequested += EditDistribution;
        _distributePage.OpenTradeRequested += id => _ = OpenTradeAsync(id);
        _distributePage.LocateTradeRequested += id => _ = LocateTradeAsync(id);
        _distributePage.EnterTradeIdRequested += id => _ = EnterTradeIdAsync(id);
        _distributePage.CheckNowRequested += id => { if (StepById(id) is { TradeId: { } t } s) _ = VerifyTradeAsync(s, t, quiet: false); };
        _distributePage.DiscardRequested += DiscardStep;
        _distributePage.ManualPriceRequested += SetManualPrice;
        _distributePage.RestoreRequested += RestoreCopy;
        _distributePage.RestoreAllRequested += () =>
        {
            if (_vault is null || Plan.Dismissed.Count == 0) return;
            Plan.Dismissed.Clear();
            RequestSave();
            GeneratePlan(manual: true);
        };
        _plusView.TargetRequested += () => { if (PlusAccount(_plusPage.DetailId) is { } a) SetAccountTarget(a); };
        // Item pictures: Roblox's public thumbnails, in the cache the Avatares tab uses (key "a" + asset id).
        _distributePage.ThumbOf = assetId => _thumbs.GetValueOrDefault("a" + assetId);
        _distributePage.NeedThumbs += items => _ = LoadThumbsAsync(
            [.. items.DistinctBy(i => i.AssetId).Select(i => new AvatarItem(ItemKind.Asset, i.AssetId, i.Name, 0, 0, null, null, false))]);
    }

    private PlanStep? StepById(string id) => _vault?.Plan.Steps.FirstOrDefault(s => s.Id == id);

    private List<AccountSnapshot> Snapshots() =>
        _vault!.Accounts.All.Select(a => new AccountSnapshot(a.UserId, a.Username, a.GroupOrDefault,
            a.HasSession && a.Status != SessionStatus.Expired, a.Finance, a.Inventory)).ToList();

    /// <summary>
    /// The accounts as the plan sees them: Limiteds that only Rolimons lists are checked against the plan's finished
    /// trades and the account's own copies on the Marketplace (Rolimons can take hours to show a trade). The stored
    /// inventories are not changed.
    /// </summary>
    private List<AccountSnapshot> PlanSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        return _vault!.Accounts.All.Select(a => new AccountSnapshot(a.UserId, a.Username, a.GroupOrDefault,
            a.HasSession && a.Status != SessionStatus.Expired, a.Finance, PlanInventory.For(a.UserId, a.Inventory, a.Listings, Plan.Steps, now))).ToList();
    }

    /// <summary>One account's Limiteds as the plan counts them (same as <see cref="PlanSnapshots"/>).</summary>
    private AccountInventory? PlanInventoryOf(Account a) =>
        _vault is null ? a.Inventory : PlanInventory.For(a.UserId, a.Inventory, a.Listings, Plan.Steps, DateTimeOffset.UtcNow);

    private PlanInput PlanInputNow() => new(PlanSnapshots(), Distribution, _market, DateTimeOffset.UtcNow);

    // ───────────── plan ─────────────

    private void GeneratePlan(bool manual)
    {
        if (_vault is null || Distribution.SourceAccounts.Count == 0) return;
        var input = PlanInputNow();
        _vault.Plan = Planner.Build(input, Plan);
        Planner.Revalidate(Plan, input);
        RequestSave();
        UpdateDistribute(force: true);
        if (manual)
        {
            int n = Plan.Steps.Count(s => s.State == StepState.Suggested);
            int left = Plan.Exclusions.Count;
            SetStatus(n == 0 ? (left == 0 ? "Nenhuma sugestão agora." : $"Nenhuma sugestão agora: {Fmt.Count(left, "cópia ficou", "cópias ficaram")} de fora — veja o motivo em \"Ficaram de fora\".")
                             : $"{Fmt.Count(n, "sugestão", "sugestões")} de trade. Confira o motivo de cada uma antes de abrir.");
        }
    }

    /// <summary>After new readings: flag moved copies and, if suggestions were already generated, adapt them.</summary>
    private void ReplanAfterChange()
    {
        if (_vault is null || Distribution.SourceAccounts.Count == 0) return;
        var input = PlanInputNow();
        if (Plan.BuiltUtc is not null) _vault.Plan = Planner.Build(input, Plan);
        Planner.Revalidate(Plan, input);
        RequestSave();
        UpdateDistribute();
    }

    private void DiscardStep(string id)
    {
        if (StepById(id) is not { } step) return;
        bool suggestion = step.State == StepState.Suggested;
        string copies = string.Join(", ", step.Limiteds.Select(c => c.Label));
        if (MessageBox.Show(this, $"Descartar a etapa {step.Id} (@{step.SourceName} → @{step.DestinationName})?\n\n" +
                (suggestion ? $"{copies} não serão sugeridas de novo para @{step.DestinationName} (podem ir para outra destinatária), até você usar \"Voltar ao plano\" em \"Ficaram de fora\" ou mudar as contas em Configurar…."
                            : "As cópias voltam a ficar livres para o plano."),
                "Distribuir", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var now = DateTimeOffset.UtcNow;
        if (suggestion) Plan.Steps.Remove(step); // never acted on: gone, not history
        else
        {
            step.State = StepState.Discarded;
            step.Attention = null;
            step.Log(now, "descartada pelo operador");
        }
        if (suggestion)
        {
            Plan.Dismiss(step, now);
            ReplanAfterChange(); // the freed destination space and Smalls go to the other copies right away
            SetStatus($"{step.Id} descartada: {copies} não vão mais para @{step.DestinationName}.");
        }
        RequestSave();
        UpdateDistribute(force: true);
    }

    /// <summary>A discarded copy goes back into the suggestions.</summary>
    private void RestoreCopy(long userAssetId)
    {
        if (_vault is null || !Plan.Restore(userAssetId)) return;
        RequestSave();
        GeneratePlan(manual: false);
        SetStatus("Cópia de volta ao plano: as sugestões foram refeitas.");
    }

    private void SetManualPrice(long assetId, string name)
    {
        if (_vault is null) return;
        string current = Distribution.ManualPrice.TryGetValue(assetId, out long p) ? p.ToString() : "";
        string? text = PromptDialog.Ask(this, "Preço manual", $"Preço de venda estimado para {name}, em Robux (vazio = usar RAP/Rolimons):", current,
            "O líquido é calculado com a taxa configurada. Vale para todas as cópias deste item.");
        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text)) Distribution.ManualPrice.Remove(assetId);
        else if (long.TryParse(text.Trim().Replace(".", ""), out long v) && v > 0) Distribution.ManualPrice[assetId] = v;
        else { MessageBox.Show(this, "Use um número inteiro maior que zero.", "Preço manual"); return; }
        RequestSave();
        GeneratePlan(manual: false);
        UpdateFinancePanel();
    }

    private void SetAccountTarget(Account a)
    {
        if (_vault is null) return;
        string current = Distribution.TargetNetByAccount.TryGetValue(a.UserId, out long t) ? t.ToString() : "";
        string? text = PromptDialog.Ask(this, "Meta líquida da conta", $"Teto de Robux líquidos parados em @{a.Username} (vazio = usar o padrão de {Fmt.N(Distribution.DefaultTargetNet)}; 0 = sem teto):", current);
        if (text is null) return;
        if (string.IsNullOrWhiteSpace(text)) Distribution.TargetNetByAccount.Remove(a.UserId);
        else if (long.TryParse(text.Trim().Replace(".", ""), out long v) && v >= 0) Distribution.TargetNetByAccount[a.UserId] = v;
        else { MessageBox.Show(this, "Use um número inteiro (0 ou mais).", "Meta líquida"); return; }
        RequestSave();
        ReplanAfterChange();
        UpdateFinancePanel();
    }

    // ───────────── readings ─────────────

    /// <summary>Reads every account of the distribution one at a time (finance, Limiteds, market, trades in progress, sales).</summary>
    private async Task RefreshDistributionReadsAsync()
    {
        if (_vault is not { } vault || _distributeBusy) return;
        _distributeBusy = true;
        UpdateDistribute(force: true);
        try
        {
            var accounts = vault.Accounts.All.Where(a => RoleOf(a) != Role.None && a.HasSession && a.Status != SessionStatus.Expired)
                .OrderBy(a => RoleOf(a) == Role.Source ? 0 : 1).ToList();
            int i = 0;
            foreach (var a in accounts)
            {
                if (_closing || _vault != vault) return;
                await WaitRobloxPauseAsync($"leituras da distribuição ({i}/{accounts.Count})");
                SetStatus($"Lendo @{a.Username} ({++i}/{accounts.Count})…");
                await RefreshFinanceAsync(a);
                if (a.Inventory?.LastAttemptUtc is not { } at || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(2))
                    await RefreshInventoryAsync(a, force: true);
                await ReadSalesAsync(a, force: true);
                await Task.Delay(500);
            }
            await EnsureMarketAsync();
            await CheckSentTradesAsync();
            ReplanAfterChange();
            SetStatus($"Leituras da distribuição atualizadas ({accounts.Count} contas).");
        }
        catch (Exception ex) { Log.Error("Distribution refresh failed", ex); }
        finally
        {
            _distributeBusy = false;
            if (!_closing) UpdateDistribute(force: true);
        }
    }

    private async Task WaitRobloxPauseAsync(string what)
    {
        while (!_closing && DateTimeOffset.UtcNow < _robloxPauseUntil)
        {
            SetStatus($"O Roblox limitou os pedidos. {char.ToUpper(what[0]) + what[1..]} continuam às {BrasiliaTime.ToBrasilia(_robloxPauseUntil):HH:mm}…");
            await Task.Delay(1000);
        }
    }

    /// <summary>An account's sales (at most every 25 min unless forced): the last 31 days, so every sale still pending is there.</summary>
    private async Task ReadSalesAsync(Account a, bool force = false)
    {
        if (_vault is not { } vault || !a.HasSession) return;
        // The calendar needs every pending sale (Roblox keeps them up to 30 days); a destination also since its first delivery.
        var month = DateTimeOffset.UtcNow - RobuxCalendar.ReadWindow;
        var delivered = Plan.Steps.Where(s => s.State == StepState.Completed && s.DestinationId == a.UserId && s.CompletedUtc is not null)
            .Select(s => s.CompletedUtc!.Value).DefaultIfEmpty(month).Min();
        var first = delivered < month ? delivered : month;
        if (!force && a.Sales?.LastAttemptUtc is { } last && DateTimeOffset.UtcNow - last < SalesInterval) return;
        if (DateTimeOffset.UtcNow < _robloxPauseUntil) return;
        var ctx = new SessionContext(a.RevealSession());
        string sent = ctx.Session;
        var r = await _api.GetSalesAsync(ctx, a.UserId, first.AddDays(-1));
        if (_vault != vault) return;
        if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(a.UserId, sent, fresh)) _browser.Find(a.UserId)?.ReplaceCookie(fresh);
        var sales = a.Sales ?? new AccountSales();
        sales.LastAttemptUtc = DateTimeOffset.UtcNow;
        if (r.Ok) RobuxCalendar.ApplyRead(sales, r.Value!, DateTimeOffset.UtcNow); // pending sales now credited are remembered
        else
        {
            sales.Problem = r.Problem;
            if (r.Outcome == ReadOutcome.RateLimited) NoteRateLimit(a.UserId, r.RetryAfter);
            Log.Warn("[vendas] " + r.Problem);
        }
        a.Sales = sales;
        RequestSave();
        UpdateCalendar();
    }

    /// <summary>Robux of the account still pending (Roblox's summary or a pending sale): its sales feed the calendar.</summary>
    private static bool HasPendingRobux(Account a) => a.Finance?.PendingRobux > 0 || a.Sales?.Records.Any(r => r.Pending) == true;

    // ───────────── trades ─────────────

    /// <summary>Re-checks the step (re-reading both accounts if needed) and opens the trade page in the source account.</summary>
    private async Task OpenTradeAsync(string id)
    {
        if (_vault is null || StepById(id) is not { } step) return;
        if (_distributeBusy) { SetStatus("Aguarde as leituras da distribuição terminarem para abrir a trade."); return; }
        var src = _vault.Accounts.Find(step.SourceId);
        var dst = _vault.Accounts.Find(step.DestinationId);
        if (src is null || dst is null) return;
        if (step.State == StepState.Suggested)
        {
            var problems = Planner.ReadyCheck(step, PlanInputNow(), Plan);
            if (problems.Count > 0)
            {
                _distributeBusy = true;
                UpdateDistribute(force: true);
                try
                {
                    SetStatus($"Conferindo @{src.Username} e @{dst.Username} antes de abrir a trade…");
                    foreach (var a in new[] { src, dst })
                    {
                        await WaitRobloxPauseAsync("a conferência");
                        await RefreshFinanceAsync(a);
                        await RefreshInventoryAsync(a, force: true);
                    }
                }
                finally { _distributeBusy = false; }
                if (_vault is null || _closing) return; // locked or closing meanwhile
                ReplanAfterChange();
                if (StepById(id) is not { State: StepState.Suggested } again || Planner.ReadyCheck(again, PlanInputNow(), Plan) is { Count: > 0 } still)
                {
                    var why = StepById(id) is { State: StepState.Suggested } s2 ? Planner.ReadyCheck(s2, PlanInputNow(), Plan) : ["a etapa mudou com as leituras novas"];
                    MessageBox.Show(this, "A etapa não está pronta para trade:\n\n• " + string.Join("\n• ", why) +
                        "\n\nAs sugestões foram recalculadas com as leituras novas. Confira a aba Distribuir.", "Abrir trade", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateDistribute(force: true);
                    return;
                }
                step = again;
            }
            step.State = StepState.Opened;
            step.OpenedUtc = DateTimeOffset.UtcNow;
            step.Log(DateTimeOffset.UtcNow, "conferida e aberta no Roblox pelo RbxSwitcher");
            RequestSave();
        }
        await SwitchToAsync(src);
        if (_browser.Active is not { } tab || tab.UserId != src.UserId)
        {
            SetStatus($"{step.Id}: não deu para abrir a aba de @{src.Username}. Use \"Abrir de novo\" na aba Distribuir.");
            return;
        }
        string url = $"https://www.roblox.com/users/{dst.UserId}/trade";
        string what = $"envie " + string.Join(", ", step.Limiteds.Select(c => c.Label)) +
                      (step.SourceSmall is { } ss ? $" + {ss.Label}" : "") + (step.DestinationSmall is { } ds ? $"; peça {ds.Label}" : "");
        const string after = "Confira e envie no Roblox; depois use \"Localizar trade no Roblox\" na aba Distribuir.";
        ShowView(ViewContas);
        UpdateDistribute(force: true);
        if (!Distribution.PreselectTradeItems || _assisting)
        {
            Go(url);
            SetStatus($"{step.Id}: monte a trade com @{dst.Username} — {what}. {after}");
            return;
        }

        // Pre-select the items on the page; the person still reviews and sends.
        var vault = _vault;
        _assisting = true;
        try
        {
            SetStatus($"{step.Id}: abrindo a trade com @{dst.Username} e selecionando os itens…");
            bool loaded = await tab.NavigateAndWaitAsync(url, TimeSpan.FromSeconds(30));
            if (!loaded || _browser.Active != tab || _vault is null || _vault != vault)
            {
                SetStatus($"{step.Id}: monte a trade com @{dst.Username} — {what}. {after}");
                return;
            }
            var plan = TradeAssistPlan.For(step, src.DisplayName, dst.DisplayName);
            var run = await TradeAssist.RunAsync(tab, plan);
            if (_closing || _vault is null || _vault != vault) return; // locked or closing meanwhile
            if (run.Result is not { } r)
            {
                SetStatus($"{step.Id}: não deu para pré-selecionar ({run.Failure}). Monte a trade — {what}. {after}");
                return;
            }
            string? saved = r.Structure is { } outline ? SaveTradePageOutline(outline) : null;
            step.Log(DateTimeOffset.UtcNow, "itens pré-selecionados na página de trade: " + r.Summary(plan));
            Log.Info($"[distribuir] {step.Id}: pré-seleção {r.Selected.Count}/{plan.Count} (faltando {r.Missing.Count}, iguais {r.Ambiguous.Count}, sem confirmar {r.Unconfirmed.Count})");
            RequestSave();
            SetStatus(r.Complete(plan)
                ? $"{step.Id}: {r.Summary(plan)} {after}"
                : $"{step.Id}: {r.Summary(plan)} Selecione o resto à mão — {what}. {after}" +
                  (saved is null ? "" : $" A estrutura da página foi salva em {saved} (Configurações → Abrir pasta de dados): envie esse arquivo para ajustar a pré-seleção."));
            UpdateDistribute(force: true);
        }
        finally { _assisting = false; }
    }

    private bool _assisting;

    // ───────────── after the person sends / accepts ─────────────

    /// <summary>
    /// A trade was sent or accepted on roblox.com in one of the app's tabs (after any 2-step verification the person
    /// did there). Sent: it is checked against the step and, if it matches, the destination is opened on it to be
    /// accepted. Accepted: it is checked again so the step completes and the plan moves on. The app itself never
    /// sends or accepts.
    /// </summary>
    private async Task OnTradeSignalAsync(SessionTab tab, TradeSignal signal)
    {
        if (_vault is not { } vault || tab.UserId is not { } uid) return;
        try
        {
            // Any trade accepted here (planned or not): its copies arrived now — the hold starts now (Limiteds tab).
            if (signal is { Kind: TradeSignalKind.Accepted, TradeId: { } acceptedId } && vault.Accounts.Find(uid) is { } acceptor)
            {
                (acceptor.Arrivals ??= new AccountArrivals()).Accepted.TryAdd(acceptedId, DateTimeOffset.UtcNow);
                RequestSave();
            }
            if (signal.Kind == TradeSignalKind.Sent)
            {
                // Steps of this source waiting for a trade (opened first, newest first); the trade itself picks among them.
                var waiting = vault.Plan.Steps
                    .Where(x => x.SourceId == uid && x.State is StepState.Opened or StepState.NeedsCheck
                                && (signal.PartnerId is null || x.DestinationId == signal.PartnerId))
                    .OrderBy(x => x.State != StepState.Opened).ThenByDescending(x => x.OpenedUtc ?? x.CreatedUtc).ToList();
                if (waiting.Count == 0) return; // not a trade of the plan
                var first = waiting[0];
                Log.Info($"[distribuir] {first.Id}: trade enviada no site{(signal.TradeId is null ? " (sem id na resposta)" : "")}");
                if (signal.TradeId is not { } id)
                {
                    SetStatus($"{first.Id}: trade enviada. Use \"Localizar trade no Roblox\" na aba Distribuir para conferir.");
                    return;
                }
                SetStatus($"{first.Id}: trade {id} enviada; conferindo com a etapa…");
                await Task.Delay(1000); // let Roblox list it
                var (t, _) = await ReadTradeAsync([uid, first.DestinationId], id);
                if (t is null || _vault != vault)
                {
                    if (_vault == vault)
                        SetStatus($"{first.Id}: trade {id} enviada, mas não deu para ler agora. Use \"Informar ID…\" ou \"Localizar trade no Roblox\" na aba Distribuir.");
                    return;
                }
                // The step this trade is: the one it matches; else the newest with these two accounts (to show what differs).
                var step = waiting.FirstOrDefault(x => TradeVerifier.Verify(x, t, Distribution, _market).Outcome == VerifyOutcome.Matches)
                           ?? waiting.FirstOrDefault(x => TradeVerifier.Verify(x, t, Distribution, _market).Outcome != VerifyOutcome.WrongAccounts);
                if (step is null) return; // a trade with an account outside these steps
                ApplyVerifiedTrade(step, t, quiet: true);
                step = StepById(step.Id) ?? step;
                if (step.State == StepState.NeedsCheck)
                    SetStatus($"{step.Id}: a trade {id} NÃO bate com a etapa — não aceite antes de conferir. {step.Attention}");
                else if (step.State == StepState.Sent && Distribution.OpenDestinationAfterSend)
                    await OpenForDestinationAsync(step, id);
                else if (step.State == StepState.Sent)
                    SetStatus($"{step.Id}: trade {id} enviada e conferida. Aceite em @{step.DestinationName}.");
            }
            else if (signal.TradeId is { } accepted)
            {
                var step = vault.Plan.Steps.FirstOrDefault(x => x.TradeId == accepted && x.State is StepState.Sent or StepState.NeedsCheck);
                if (step is null)
                {
                    // The send was not seen (made elsewhere, or Roblox gave no id): a step of this destination still without
                    // a trade, if the trade is between its two accounts.
                    var waiting = vault.Plan.Steps.Where(x => x.DestinationId == uid && x.TradeId is null && x.State is StepState.Opened or StepState.NeedsCheck)
                        .OrderBy(x => x.State != StepState.Opened).ThenByDescending(x => x.OpenedUtc ?? x.CreatedUtc).ToList();
                    if (waiting.Count == 0) return;
                    var (t, _) = await ReadTradeAsync([uid], accepted);
                    if (t is null || _vault != vault) return;
                    step = waiting.FirstOrDefault(x => TradeVerifier.Verify(x, t, Distribution, _market).Outcome == VerifyOutcome.Matches)
                           ?? waiting.FirstOrDefault(x => TradeVerifier.Verify(x, t, Distribution, _market).Outcome != VerifyOutcome.WrongAccounts);
                    if (step is null) return;
                }
                SetStatus($"{step.Id}: trade {accepted} aceita; conferindo…");
                // Roblox may take a moment to finish a trade it accepted.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 1500 : 4000);
                    if (_closing || _vault != vault) return;
                    await VerifyTradeAsync(step, accepted, quiet: true);
                    step = StepById(step.Id) ?? step;
                    if (step.State is not (StepState.Sent or StepState.Opened)) break;
                }
                SetStatus(step.State switch
                {
                    StepState.Completed => $"{step.Id}: trade {accepted} concluída e conferida. As próximas sugestões já levam isso em conta.",
                    StepState.Sent => $"{step.Id}: trade {accepted} aceita; o Roblox ainda está concluindo. O app confere de novo sozinho.",
                    StepState.Declined => $"{step.Id}: a trade {accepted} foi recusada ou expirou; as cópias voltaram para o plano.",
                    _ => $"{step.Id}: trade {accepted}: {step.Attention ?? "confira na aba Distribuir"}",
                });
            }
        }
        catch (Exception ex) { Log.Error("Trade signal handling failed", ex); }
    }

    /// <summary>Switches to the destination and opens the trade the source just sent, for the person to accept.</summary>
    private async Task OpenForDestinationAsync(PlanStep step, long tradeId)
    {
        if (_vault is not { } vault || vault.Accounts.Find(step.DestinationId) is not { } dst || vault.Accounts.Find(step.SourceId) is not { } src) return;
        if (_assisting)
        {
            SetStatus($"{step.Id}: trade {tradeId} enviada e conferida. Aceite em @{dst.Username} (Trocas).");
            return;
        }
        _assisting = true;
        try
        {
            SetStatus($"{step.Id}: trade {tradeId} enviada e conferida. Abrindo @{dst.Username} para aceitar…");
            await SwitchToAsync(dst);
            if (_browser.Active is not { } tab || tab.UserId != dst.UserId || _vault != vault) return;
            ShowView(ViewContas);
            string manual = $"abra a trade de @{src.Username} em Trocas e aceite no Roblox (o app nunca aceita)";
            if (!await tab.NavigateAndWaitAsync("https://www.roblox.com/trades", TimeSpan.FromSeconds(30)))
            {
                SetStatus($"{step.Id}: em @{dst.Username}, {manual}.");
                return;
            }
            var run = await TradeAssist.RunAsync(tab, TradeAssistPlan.Open(tradeId, dst.Username, dst.DisplayName, src.Username, src.DisplayName));
            if (_vault != vault) return;
            if (run.Result is { Navigate: { } link }) tab.Navigate(link);
            bool opened = run.Result?.Opened == true || run.PageReplaced;
            string? saved = run.Result?.Structure is { } outline ? SaveTradePageOutline(outline, "trocas-pagina.txt") : null;
            SetStatus(opened
                ? $"{step.Id}: trade {tradeId} aberta em @{dst.Username}. Confira e aceite no Roblox (o app nunca aceita); depois disso o app confere sozinho."
                : $"{step.Id}: em @{dst.Username}, {manual}." +
                  (saved is null ? "" : $" A estrutura da página foi salva em {saved} (Configurações → Abrir pasta de dados): envie esse arquivo para ajustar."));
        }
        finally { _assisting = false; }
    }

    /// <summary>The value-free outline of a trade page (no values or account names), to adjust the page script.</summary>
    private string? SaveTradePageOutline(string outline, string name = "trade-pagina.txt")
    {
        try
        {
            File.WriteAllText(Path.Combine(_dataDir, name),
                $"RbxSwitcher {Application.ProductVersion} — estrutura da página de trade (sem valores nem nomes de conta)\n{outline}\n");
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("[distribuir] estrutura da página não salva: " + ex.Message); return null; }
    }

    private async Task EnterTradeIdAsync(string id)
    {
        if (StepById(id) is not { } step) return;
        string? text = PromptDialog.Ask(this, "Conferir trade", "ID da trade (o número no endereço da trade no Roblox):", "",
            "Pode colar o endereço inteiro: o app pega o número.");
        if (text is null) return;
        var m = Regex.Matches(text, @"\d{5,}");
        if (m.Count == 0 || !long.TryParse(m[^1].Value, out long tradeId)) { MessageBox.Show(this, "Não encontrei um ID de trade nesse texto.", "Conferir trade"); return; }
        if (_vault is { } vault && PlanUpdates.LinkedElsewhere(vault.Plan, step, tradeId) is { } other
            && MessageBox.Show(this, $"A trade {tradeId} já está ligada à etapa {other.Id} (@{other.SourceName} → @{other.DestinationName}).\n\nLigar a {step.Id} mesmo assim?",
                   "Conferir trade", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await VerifyTradeAsync(step, tradeId, quiet: false);
    }

    /// <summary>Reads one trade with the session of the first of these accounts that can (the source's first).</summary>
    /// <returns>The trade, or why not (Problem null: the vault was locked meanwhile).</returns>
    private async Task<(TradeDetail? Trade, string? Problem)> ReadTradeAsync(long[] users, long tradeId)
    {
        if (_vault is not { } vault) return (null, null);
        ReadResult<TradeDetail>? read = null;
        foreach (long uid in users)
        {
            if (vault.Accounts.Find(uid) is not { HasSession: true } a) continue;
            var ctx = new SessionContext(a.RevealSession());
            string sent = ctx.Session;
            read = await _api.GetTradeAsync(ctx, tradeId);
            if (_vault != vault) return (null, null); // locked meanwhile
            if (ctx.Rotated is { } fresh && vault.Accounts.ReplaceSessionIf(uid, sent, fresh)) _browser.Find(uid)?.ReplaceCookie(fresh);
            if (read.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(uid, read.RetryAfter); break; }
            if (read.Ok) break;
        }
        return read is { Ok: true } ? (read.Value!, null) : (null, read?.Problem ?? "sem resposta");
    }

    /// <summary>Reads one trade (with the source's session, then the destination's) and applies what it shows.</summary>
    private async Task<bool> VerifyTradeAsync(PlanStep step, long tradeId, bool quiet)
    {
        var (t, problem) = await ReadTradeAsync([step.SourceId, step.DestinationId], tradeId);
        if (t is null)
        {
            if (!quiet && problem is not null) MessageBox.Show(this, $"Não deu para ler a trade {tradeId}: {problem}.", "Conferir trade", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ApplyVerifiedTrade(step, t, quiet);
        return true;
    }

    /// <summary>Checks a trade Roblox gave against the step and applies the outcome (state, history, copies moved).</summary>
    private void ApplyVerifiedTrade(PlanStep step, TradeDetail t, bool quiet)
    {
        if (_vault is not { } vault) return;
        if (!vault.Plan.Steps.Contains(step)) step = vault.Plan.Steps.FirstOrDefault(x => x.Id == step.Id) ?? step; // replanned meanwhile
        var v = TradeVerifier.Verify(step, t, Distribution, _market);
        var before = step.State;
        PlanUpdates.ApplyTrade(step, t, v, Snapshots(), DateTimeOffset.UtcNow);
        Log.Info($"[distribuir] {step.Id}: trade conferida ({v.Outcome}, {t.Status}) → {step.State}");
        RequestSave();
        if (step.State is StepState.Completed or StepState.Declined && before != step.State) ReplanAfterChange();
        else UpdateDistribute(force: true);
        if (!quiet)
        {
            string msg = step.State switch
            {
                StepState.Completed => $"Trade {t.Id} concluída e conferida. As etapas futuras foram ajustadas.",
                StepState.Sent => $"Trade {t.Id} enviada e ainda aberta no Roblox. O app confere de novo sozinho.",
                StepState.Declined => $"Trade {t.Id} {PlanUpdates.StatusWord(t.Status)}: as cópias voltaram para o plano.",
                _ => step.Attention ?? "A trade não bate com a etapa.",
            };
            if (v.Notes.Count > 0 && step.State != StepState.NeedsCheck) msg += "\n\n• " + string.Join("\n• ", v.Notes);
            MessageBox.Show(this, msg, "Conferir trade", MessageBoxButtons.OK,
                step.State is StepState.Completed or StepState.Sent ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    /// <summary>Searches the source's newest trades for one with the destination made after the step, and checks it.</summary>
    private async Task LocateTradeAsync(string id)
    {
        if (_vault is not { } vault || StepById(id) is not { } step || vault.Accounts.Find(step.SourceId) is not { HasSession: true } src) return;
        if (!_locating.Add(id)) return; // already searching for this step
        try { await LocateTradeCoreAsync(vault, step, src); }
        finally { _locating.Remove(id); }
    }

    private readonly HashSet<string> _locating = [];

    private async Task LocateTradeCoreAsync(RbxSwitcher.Core.Storage.VaultFile vault, PlanStep step, Account src)
    {
        SetStatus($"Procurando a trade de {step.Id} nas trades de @{src.Username}…");
        var ctx = new SessionContext(src.RevealSession());
        string sent = ctx.Session;
        void SaveRotated()
        {
            if (ctx.Rotated is { } fresh && fresh != sent && vault.Accounts.ReplaceSessionIf(src.UserId, sent, fresh))
            {
                _browser.Find(src.UserId)?.ReplaceCookie(fresh);
                sent = fresh;
            }
        }
        var all = new List<TradeSummary>();
        foreach (var kind in new[] { "Outbound", "Completed", "Inactive", "Inbound" })
        {
            await WaitRobloxPauseAsync("a busca de trades");
            var r = await _api.GetTradesAsync(ctx, kind);
            if (_vault != vault) return;
            if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(src.UserId, r.RetryAfter); break; }
            if (r.Ok) all.AddRange(r.Value!);
            await Task.Delay(300);
        }
        SaveRotated();
        var candidates = PlanUpdates.Candidates(step, all, vault.Plan).Take(6).ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, $"Não achei trade de @{src.Username} com @{step.DestinationName} feita depois desta etapa.\n\nSe a trade já existe, use \"Informar ID…\".",
                "Localizar trade", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("");
            return;
        }
        // Prefer a trade that matches the step; otherwise apply the newest one so the mismatch is shown.
        (TradeDetail T, VerifyResult V)? best = null;
        foreach (var c in candidates)
        {
            var r = await _api.GetTradeAsync(ctx, c.Id);
            if (_vault != vault) return;
            if (r.Outcome == ReadOutcome.RateLimited) { NoteRateLimit(src.UserId, r.RetryAfter); break; }
            if (!r.Ok) continue;
            var v = TradeVerifier.Verify(step, r.Value!, Distribution, _market);
            if (v.Outcome == VerifyOutcome.Matches) { best = (r.Value!, v); break; }
            best ??= v.Outcome == VerifyOutcome.NeedsCheck ? (r.Value!, v) : null;
            await Task.Delay(300);
        }
        SaveRotated();
        if (best is not { } found)
        {
            MessageBox.Show(this, "Achei trades com essa conta, mas nenhuma pôde ser lida ou conferida. Tente \"Informar ID…\".", "Localizar trade");
            return;
        }
        await VerifyTradeAsync(step, found.T.Id, quiet: false);
    }

    /// <summary>Background: trades already sent are checked again until Roblox shows them completed or closed.</summary>
    private async Task CheckSentTradesAsync()
    {
        if (_vault is null) return;
        foreach (var step in Plan.Steps.Where(s => s.State == StepState.Sent && s.TradeId is not null).ToList())
        {
            if (_closing || DateTimeOffset.UtcNow < _robloxPauseUntil) return;
            await VerifyTradeAsync(step, step.TradeId!.Value, quiet: true);
            await Task.Delay(400);
        }
    }

    // ───────────── screen ─────────────

    private void UpdateDistribute(bool force = false)
    {
        if (_vault is null || !_distributePage.Visible) return;
        var now = DateTimeOffset.UtcNow;
        var s = Distribution;
        bool configured = s.SourceAccounts.Count > 0;
        var snaps = Snapshots();
        var planSnaps = PlanSnapshots();
        var input = new PlanInput(planSnaps, s, _market, now);
        var reserved = Planner.Reserved(Plan);
        reserved.UnionWith(Plan.Dismissed.Select(d => d.UserAssetId)); // discarded copies are not "ready" for the plan

        int readyCopies = 0, heldCopies = 0;
        long readyNet = 0;
        int destCount = 0, destWithSpace = 0;
        long freeSpace = 0;
        var destViews = new List<DestinationView>();
        DateTimeOffset? oldest = null;
        foreach (var a in planSnaps)
        {
            var role = Planner.RoleOf(s, a.UserId, a.Group);
            if (role == DistributionRole.None) continue;
            var reads = new[] { a.Inventory?.ReadUtc, a.Finance?.RobuxAtUtc };
            foreach (var r in reads) if (r is { } t && (oldest is null || t < oldest)) oldest = t;
            if (role == DistributionRole.Source && a.Inventory is { } inv)
                foreach (var v in Valuation.Value(inv, s, _market).Where(v => !v.Small && v.Net is not null && !reserved.Contains(v.Copy.UserAssetId)))
                {
                    if (v.OnHold) { heldCopies++; continue; }
                    readyCopies++;
                    readyNet += v.Net!.Value;
                }
            if (role == DistributionRole.Destination)
            {
                destCount++;
                long? space = Planner.Space(a, s, _market, now, Planner.Committed(Plan, a.UserId), out var view);
                if (space is { } sp && sp > 0) { destWithSpace++; freeSpace += sp; }
                var mine = a.Inventory is { } di ? Valuation.Value(di, s, _market).Where(v => v.Small && !reserved.Contains(v.Copy.UserAssetId)).ToList() : [];
                int free = mine.Count(v => !v.OnHold), held = mine.Count(v => v.OnHold);
                int planned = Plan.Steps.Count(x => x.State == StepState.Suggested && x.DestinationId == a.UserId);
                string? why = !a.SessionOk ? "sessão expirada"
                    : space is null ? string.Join("; ", view.Blocks)
                    : space == 0 ? $"sem espaço: ocupado {Fmt.N(view.Occupied ?? 0)} já chega ao teto {Fmt.N((s.UseSpaceAtSale ? view.CeilingAtSale : view.Ceiling) ?? 0)}"
                    : free + held == 0 ? "sem Small para mandar na trade"
                    : free == 0 && planned == 0 ? "só Smalls em holding (a etapa espera)"
                    : null;
                destViews.Add(new DestinationView(a.Username, space, free, held, planned, why));
            }
        }
        int Count(params StepState[] st) => Plan.Steps.Count(x => st.Contains(x.State));
        string summary = !configured ? "Distribuição não configurada."
            : $"Origens: {Fmt.Count(readyCopies, "cópia pronta", "cópias prontas")} · líquido {Fmt.N(readyNet)}{(heldCopies > 0 ? $" · {heldCopies} em holding" : "")}   ·   Destinatárias: {destWithSpace} de {destCount} com espaço · {Fmt.N(freeSpace)} livres\n" +
              $"Plano: {Fmt.Count(Count(StepState.Suggested), "sugerida", "sugeridas")} · {Count(StepState.Opened, StepState.Sent)} em andamento · {Count(StepState.NeedsCheck)} em conferência · {Fmt.Count(Count(StepState.Completed), "concluída", "concluídas")}";
        string? warning = !configured ? "Escolha as contas de origem (Alfa, Beta) e as destinatárias em \"Configurar…\"."
            : oldest is { } o && now - o > QuotaCalculator.MaxAge ? $"Há leituras de {Fmt.Time(o)} (mais de 30 min). Use \"Atualizar leituras\" antes de abrir trades — \"Abrir trade\" também relê as duas contas da etapa."
            : null;
        var salesDict = _vault.Accounts.All.Where(a => a.Sales is not null).ToDictionary(a => a.UserId, a => a.Sales!);
        var (sales, delivered) = SalesMatcher.Match(Plan, snaps, salesDict);
        var steps = Plan.Steps.Select(x => new StepView(x, x.State == StepState.Suggested ? Planner.ReadyCheck(x, input, Plan) : [])).ToList();
        var state = new DistributeState(summary, Plan.BuiltUtc is { } b ? $"sugestões de {Fmt.Time(b)}" : "sugestões ainda não geradas",
            warning, configured, _distributeBusy, steps, Plan.Exclusions, delivered, sales)
        { Destinations = destViews.OrderByDescending(d => d.Space ?? -1).ToList() };

        string sig = string.Join("|", summary, state.PlanInfo, warning, _distributeBusy,
            string.Join(",", steps.Select(v => $"{v.Step.Id}:{v.Step.State}:{string.Join("/", v.Problems)}:{v.Step.Attention}:{v.Step.History.Count}:{v.Step.Net}:" +
                $"{string.Join("+", v.Step.AllCopies().Select(c => c.UserAssetId))}:{v.Step.WaitsForRelease}:{v.Step.WaitsForSmall}:{v.Step.TradeId}:{string.Join("/", v.Step.Why)}")),
            string.Join(",", Plan.Exclusions.Select(e => $"{e.Copy.UserAssetId}:{e.Why}")), string.Join(",", destViews), sales.Count, string.Join(",", delivered.Select(d => d.State)));
        if (!force && sig == _distributeSignature) return; // nothing changed: keep the scroll position
        _distributeSignature = sig;
        _distributePage.SetState(state);
    }
}
