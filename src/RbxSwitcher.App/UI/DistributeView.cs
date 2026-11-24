using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.UI;

/// <summary>A step as the screen shows it: the step plus the result of the "ready" check made just now.</summary>
internal sealed record StepView(PlanStep Step, IReadOnlyList<string> Problems);

/// <summary>Everything the "Distribuir" page shows, computed by MainForm.</summary>
internal sealed record DistributeState(
    string Summary, string PlanInfo, string? Warning, bool Configured, bool Busy,
    IReadOnlyList<StepView> Steps, IReadOnlyList<Exclusion> Exclusions,
    IReadOnlyList<DeliveredView> Delivered, IReadOnlyList<SaleView> Sales)
{
    /// <summary>Each marked destination, and why it can (or cannot) take a lot now.</summary>
    public IReadOnlyList<DestinationView> Destinations { get; init; } = [];
}

/// <param name="Space">Free space now (null: blocked).</param>
/// <param name="Why">What blocks it, or what limits it (no Small, full).</param>
internal sealed record DestinationView(string Name, long? Space, int FreeSmalls, int HeldSmalls, int Planned, string? Why);

/// <summary>
/// The "Distribuir" category: suggested trades (what to send, from where, to whom, with which Smalls, and why), the
/// trades in progress, what was left out, the history and the sales. Every Roblox action is the operator's: the page only
/// opens the trade page in the source account and reads Roblox to check what happened.
/// </summary>
internal sealed class DistributeView : Panel
{
    private const int ViewSuggested = 0, ViewProgress = 1, ViewLeftOut = 2, ViewHistory = 3, ViewSales = 4;

    private readonly Label _summary = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Text2, Margin = new Padding(0, 4, 0, 10) };
    private readonly Label _planInfo = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(10, 9, 0, 0) };
    private readonly NoteLabel _warning = new() { Kind = NoteKind.Warn, Visible = false, Margin = new Padding(0, 0, 0, 10) };
    private readonly UiButton _generate, _refresh, _configure;
    private readonly Segmented _tabs = new("Sugestões", "Em andamento", "Ficaram de fora", "Histórico", "Vendas");
    private readonly FlowLayoutPanel _list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Window, Padding = new Padding(0, 4, 12, 12) };
    private readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 20_000 };
    private DistributeState? _state;
    private readonly HashSet<string> _detailsOpen = [];

    /// <summary>Item pictures by asset id (shared cache); <see cref="NeedThumbs"/> asks for the missing ones.</summary>
    public Func<long, Image?> ThumbOf { get; set; } = _ => null;
    public event Action<IReadOnlyList<(long AssetId, string Name)>>? NeedThumbs;

    /// <summary>New pictures arrived: repaint the strips (no rebuild, the scroll stays).</summary>
    public void ThumbsLoaded()
    {
        static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
        foreach (var strip in All(_list).OfType<TradeStrip>()) strip.Invalidate();
    }

    public event Action? GenerateRequested, RefreshRequested, ConfigureRequested;
    public event Action<string>? OpenTradeRequested, LocateTradeRequested, EnterTradeIdRequested, CheckNowRequested, DiscardRequested;
    public event Action<long, string>? ManualPriceRequested;
    /// <summary>A discarded copy (by userAssetId) goes back into the suggestions.</summary>
    public event Action<long>? RestoreRequested;
    public event Action? RestoreAllRequested;

    public DistributeView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;
        Theme.DarkScroll(_list);

        _generate = Theme.Button("Gerar sugestões", (_, _) => GenerateRequested?.Invoke(), ButtonKind.Primary);
        _tip.SetToolTip(_generate, "Recalcula as sugestões com as leituras atuais. Etapas que você já abriu, enviou ou está conferindo não mudam.");
        _refresh = Theme.Button("Atualizar leituras", (_, _) => RefreshRequested?.Invoke());
        _tip.SetToolTip(_refresh, "Lê de novo saldos, cotas, Plus, Limiteds, valores e vendas das contas da distribuição (uma por vez).");
        _configure = Theme.Button("Configurar…", (_, _) => ConfigureRequested?.Invoke(), ButtonKind.Ghost);

        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        titles.Controls.AddRange([
            new Label { Text = "Distribuir Limiteds", AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = Padding.Empty },
            new Label { Text = "Qual Limited mandar, para qual conta e com quais Smalls — você envia a trade no Roblox; o app confere", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 0) },
        ]);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _refresh.Margin = new Padding(0, 0, 6, 0);
        right.Controls.AddRange([_refresh, _configure]);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(titles, 0, 0);
        heading.Controls.Add(right, 1, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 10) };
        _generate.Margin = Padding.Empty;
        actions.Controls.AddRange([_generate, _planInfo]);
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        _tabs.Margin = Padding.Empty;
        _tabs.SelectedIndexChanged += _ => Render();
        tabs.Controls.Add(_tabs);
        var summaryRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        summaryRow.Controls.AddRange([_summary, _warning]);

        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 8, 8), BackColor = Theme.Window };
        page.Controls.AddRange([_list, tabs, actions, summaryRow, heading]);
        Controls.Add(page);
        _list.Resize += (_, _) => FitWidths();
    }

    public void SetState(DistributeState s)
    {
        _state = s;
        _summary.Text = s.Summary;
        _planInfo.Text = s.PlanInfo;
        _warning.Text = s.Warning ?? "";
        _warning.Visible = s.Warning is not null;
        _generate.Enabled = s.Configured && !s.Busy;
        _refresh.Enabled = s.Configured && !s.Busy;
        _refresh.Text = s.Busy ? "Lendo…" : "Atualizar leituras";
        int Of(params StepState[] st) => s.Steps.Count(v => st.Contains(v.Step.State));
        int? N(int n) => n > 0 ? n : null;
        _tabs.SetCounts(N(Of(StepState.Suggested)), N(Of(StepState.Opened, StepState.Sent, StepState.NeedsCheck)), N(s.Exclusions.Count(e => !e.Dismissed)), null,
                        N(s.Sales.Count(x => x.Link != SaleLink.NotFromPlan)));
        Render();
    }

    private int Width_ => Math.Max(200, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

    private void FitWidths()
    {
        int page = Math.Max(200, _list.ClientSize.Width - 12);
        _summary.MaximumSize = new Size(page, 0);
        _warning.MaximumSize = new Size(page, 0);
        foreach (Control c in _list.Controls)
        {
            c.MinimumSize = c.MaximumSize = new Size(Width_, 0);
            foreach (var l in c.Controls.OfType<Label>()) l.MaximumSize = new Size(Width_ - 40, 0);
        }
    }

    private void Render()
    {
        if (_state is not { } s) return;
        _list.SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();
        switch (_tabs.SelectedIndex)
        {
            case ViewSuggested:
                var sug = s.Steps.Where(v => v.Step.State == StepState.Suggested).ToList();
                if (sug.Count == 0) Empty(s.Configured ? "Nenhuma sugestão agora. Use \"Gerar sugestões\" depois de atualizar as leituras." : "Configure as contas de origem e as destinatárias para começar.");
                foreach (var v in sug) _list.Controls.Add(StepCard(v));
                int left = s.Exclusions.Count(e => !e.Dismissed);
                if (left > 0 && sug.Count > 0)
                    _list.Controls.Add(Hint((left == 1 ? "1 cópia ficou de fora" : $"{Fmt.Count(left, "cópia", "cópias")} ficaram de fora") + " — veja o motivo em \"Ficaram de fora\"."));
                if (s.Configured && s.Destinations.Count > 0) _list.Controls.Add(DestinationsCard(s.Destinations));
                break;
            case ViewProgress:
                var prog = s.Steps.Where(v => v.Step.State is StepState.Opened or StepState.Sent or StepState.NeedsCheck).ToList();
                if (prog.Count == 0) Empty("Nenhuma trade em andamento. Abra uma sugestão para começar.");
                foreach (var v in prog.OrderBy(v => v.Step.State != StepState.NeedsCheck)) _list.Controls.Add(StepCard(v));
                break;
            case ViewLeftOut:
                if (s.Exclusions.Count == 0) Empty("Nada ficou de fora na última geração.");
                else if (s.Exclusions.All(e => e.Dismissed)) { Empty("Nada ficou de fora por falta de espaço, Small ou preço."); _list.Controls.Add(ExclusionsCard(s.Exclusions)); }
                else _list.Controls.Add(ExclusionsCard(s.Exclusions));
                break;
            case ViewHistory:
                var hist = s.Steps.Where(v => v.Step.State is StepState.Completed or StepState.Declined or StepState.Discarded)
                    .OrderByDescending(v => v.Step.History.LastOrDefault()?.AtUtc ?? v.Step.CreatedUtc).ToList();
                if (hist.Count == 0) Empty("Nenhuma etapa concluída, recusada ou descartada ainda.");
                foreach (var v in hist.Take(60)) _list.Controls.Add(StepCard(v));
                break;
            case ViewSales:
                _list.Controls.Add(SalesCard(s.Delivered, s.Sales));
                break;
        }
        FitWidths();
        _list.ResumeLayout();
    }

    private void Empty(string text) => _list.Controls.Add(new Label { Text = text, AutoSize = true, Font = Theme.Base, ForeColor = Theme.TextDim, Margin = new Padding(0, 12, 0, 0) });

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 4, 0, 8) };

    private static Label Line(string text, Color? ink = null, Font? font = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Theme.Small, ForeColor = ink ?? Theme.Text2, Margin = new Padding(0, 1, 0, 2) };

    private static (string Word, Color Dot, Color Ink) Look(StepView v) => v.Step.State switch
    {
        StepState.Suggested when v.Step.WaitsForRelease => ("Aguardando liberação", Theme.Warning, Theme.WarnInk),
        StepState.Suggested when v.Step.WaitsForSmall => ("Aguardando Small", Theme.Warning, Theme.WarnInk),
        StepState.Suggested when v.Problems.Count == 0 => ("Pronta para trade", Theme.Valid, Theme.OkInk),
        StepState.Suggested => ("Reconferir antes", Theme.Warning, Theme.WarnInk),
        StepState.Opened => ("Em montagem no Roblox", Theme.Online, Theme.OnlineInk),
        StepState.Sent => ("Enviada, aguardando", Theme.Online, Theme.OnlineInk),
        StepState.NeedsCheck => ("Em conferência", Theme.Warning, Theme.WarnInk),
        StepState.Completed => ("Concluída", Theme.Valid, Theme.OkInk),
        StepState.Declined => ("Recusada ou expirada", Theme.Expired, Theme.BadInk),
        _ => ("Descartada", Theme.Unknown, Theme.TextDim),
    };

    private Control StepCard(StepView v)
    {
        var st = v.Step;
        var card = new CardStack();
        var (word, dot, ink) = Look(v);
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 6) };
        head.Controls.Add(new Label { Text = $"{st.Id} · @{st.SourceName} → @{st.DestinationName}", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 3, 12, 0) });
        var status = new StatusText { Font = Theme.Bold, Margin = new Padding(0, 3, 0, 0) };
        status.Set(word, dot, ink);
        head.Controls.Add(status);
        string meta = $"líquido {Fmt.N(st.Net)}" + (st.TradeId is { } tid ? $" · trade {tid}" : "");
        head.Controls.Add(new Label { Text = meta, AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(12, 6, 0, 0) });
        card.Controls.Add(head);

        // The trade as pictures: what leaves the source ⇄ what leaves the destination (a Small is tagged, never stock).
        var limitedIds = st.Limiteds.Select(c => c.UserAssetId).ToHashSet();
        var sends = st.ActualFromSource ?? [.. st.Limiteds, .. st.SourceSmall is { } ss ? new[] { ss } : []];
        var gets = st.ActualFromDestination ?? (st.DestinationSmall is { } ds ? [ds] : []);
        var left = sends.OrderBy(c => limitedIds.Contains(c.UserAssetId) ? 0 : 1)
            .Select(c => new StripItem(c.AssetId, c.Name, c.Serial, c.Net, !limitedIds.Contains(c.UserAssetId))).ToList();
        var right = gets.Select(c => new StripItem(c.AssetId, c.Name, c.Serial, null, true)).ToList();
        var strip = new TradeStrip($"Sai de @{st.SourceName}", left, $"Sai de @{st.DestinationName}", right)
        {
            ThumbOf = ThumbOf, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 4),
        };
        card.Controls.Add(strip);
        var missing = left.Concat(right).Where(i => ThumbOf(i.AssetId) is null).Select(i => (i.AssetId, i.Name)).ToList();
        if (missing.Count > 0) NeedThumbs?.Invoke(missing);

        // What needs a look stays in view; the reasons and the history are one click away.
        if (st.Attention is { } att) card.Controls.Add(new NoteLabel { Text = att, Kind = NoteKind.Warn, Margin = new Padding(0, 6, 0, 4) });
        else if (st.State == StepState.Suggested && v.Problems.Count > 0)
            card.Controls.Add(new NoteLabel { Text = "Antes de abrir: " + string.Join("; ", v.Problems) + (st.WaitsForRelease || st.WaitsForSmall
                ? ". \"Abrir trade\" liga quando a cópia sair do holding (\"Atualizar leituras\" confere)."
                : ". \"Abrir trade\" relê as duas contas e confere de novo."), Kind = NoteKind.Warn, Margin = new Padding(0, 6, 0, 4) });

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Card, Margin = new Padding(0, 8, 0, 0) };
        UiButton B(string text, Action<string>? ev, ButtonKind kind = ButtonKind.Secondary, string? tip = null)
        {
            var b = Theme.Button(text, (_, _) => ev?.Invoke(st.Id), kind);
            b.Margin = new Padding(0, 0, 6, 0);
            if (tip is not null) _tip.SetToolTip(b, tip);
            buttons.Controls.Add(b);
            return b;
        }
        switch (st.State)
        {
            case StepState.Suggested:
                B("Abrir trade", OpenTradeRequested, tip: $"Relê as duas contas, confere a etapa e abre a página de trade com @{st.DestinationName} na aba de @{st.SourceName}.")
                    .Enabled = !st.WaitsForSmall && !st.WaitsForRelease && _state?.Busy != true;
                B("Descartar", DiscardRequested, ButtonKind.Ghost);
                break;
            case StepState.Opened:
                B("Localizar trade no Roblox", LocateTradeRequested, tip: "Procura nas trades de @" + st.SourceName + " uma com @" + st.DestinationName + " feita depois desta etapa e confere os dois lados.");
                B("Informar ID…", EnterTradeIdRequested, ButtonKind.Ghost);
                B("Abrir de novo", OpenTradeRequested, ButtonKind.Ghost);
                B("Descartar", DiscardRequested, ButtonKind.Ghost);
                break;
            case StepState.Sent:
                B("Conferir agora", CheckNowRequested);
                break;
            case StepState.NeedsCheck:
                B("Localizar trade no Roblox", LocateTradeRequested);
                B("Informar ID…", EnterTradeIdRequested, ButtonKind.Ghost);
                if (st.TradeId is not null) B("Conferir de novo", CheckNowRequested, ButtonKind.Ghost);
                B("Descartar", DiscardRequested, ButtonKind.Ghost);
                break;
        }
        bool open = _detailsOpen.Contains(st.Id);
        if (st.Why.Count > 0 || st.History.Count > 0)
        {
            var toggle = new UiButton { Text = "Detalhes", Kind = ButtonKind.Ghost, Icon = open ? UiIcon.ChevronUp : UiIcon.ChevronDown, IconAfterText = true, Margin = new Padding(0, 0, 6, 0) };
            toggle.AccessibleName = open ? "Recolher os detalhes da etapa" : "Mostrar os motivos e o histórico da etapa";
            toggle.Click += (_, _) => { if (!_detailsOpen.Remove(st.Id)) _detailsOpen.Add(st.Id); Render(); };
            buttons.Controls.Add(toggle);
        }
        if (buttons.Controls.Count > 0) card.Controls.Add(buttons);
        if (open)
        {
            if (st.Why.Count > 0)
            {
                card.Controls.Add(new Label { Text = "Por que esta etapa", AutoSize = true, Font = Theme.Header, ForeColor = Theme.Text2, Margin = new Padding(0, 10, 0, 2) });
                foreach (var why in st.Why) card.Controls.Add(Line("• " + why, Theme.TextDim));
            }
            if (st.History.Count > 0)
            {
                card.Controls.Add(new Label { Text = "Histórico", AutoSize = true, Font = Theme.Header, ForeColor = Theme.Text2, Margin = new Padding(0, 10, 0, 2) });
                foreach (var e in st.History.TakeLast(10)) card.Controls.Add(Line($"{Fmt.Time(e.AtUtc)} — {e.Text}", Theme.TextDim));
            }
        }
        return card;
    }

    /// <summary>Every marked destination: free space, Smalls to send, what is planned, and what stops it.</summary>
    private Control DestinationsCard(IReadOnlyList<DestinationView> dests)
    {
        var card = new CardStack { Margin = new Padding(0, 4, 0, 12) };
        card.Controls.Add(new Label { Text = "Destinatárias", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 4) });
        card.Controls.Add(Line("Cada trade precisa de um Small livre da destinatária. Sem espaço ou sem Small, a conta não recebe nesta rodada.", Theme.TextDim));
        foreach (var d in dests)
        {
            string smalls = d.FreeSmalls == 0 && d.HeldSmalls == 0 ? "nenhum Small" : $"{Fmt.Count(d.FreeSmalls, "Small livre", "Smalls livres")}" + (d.HeldSmalls > 0 ? $" + {d.HeldSmalls} em holding" : "");
            string text = $"@{d.Name} — " + (d.Space is { } sp ? $"espaço {Fmt.N(sp)} · {smalls}" : "bloqueada") +
                          (d.Planned > 0 ? $" · {Fmt.Count(d.Planned, "trade sugerida", "trades sugeridas")}" : "") + (d.Why is { } w ? $" · {w}" : "");
            bool ok = d.Space is > 0 && d.Why is null;
            card.Controls.Add(Line((ok ? "● " : "○ ") + text, ok ? Theme.Text2 : Theme.WarnInk));
        }
        return card;
    }

    private Control ExclusionsCard(IReadOnlyList<Exclusion> items)
    {
        var card = new CardStack();
        var real = items.Where(e => !e.Dismissed).ToList();
        var dismissedList = items.Where(e => e.Dismissed).ToList();
        if (real.Count > 0)
        {
            card.Controls.Add(new Label { Text = $"Ficaram de fora ({real.Count})", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 6) });
            card.Controls.Add(Line("Cada cópia de origem que não entrou no plano, com o motivo. \"Preço…\" define um preço manual para o item.", Theme.TextDim));
        }
        // What you discarded: one line, out of the count.
        if (dismissedList.Count > 0)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, real.Count > 0 ? 0 : 0, 0, 6) };
            var back = Theme.Button(dismissedList.Count == 1 ? "Voltar ao plano" : "Voltar todas ao plano", (_, _) => RestoreAllRequested?.Invoke(), ButtonKind.Ghost);
            back.Margin = new Padding(0, 0, 8, 0);
            _tip.SetToolTip(back, "Devolve as cópias que você descartou e refaz as sugestões.");
            row.Controls.Add(back);
            row.Controls.Add(new Label
            {
                Text = $"{Fmt.Count(dismissedList.Count, "cópia descartada", "cópias descartadas")} por você e sem outra destinatária: " + string.Join(", ", dismissedList.Take(6).Select(e => e.Copy.Label)) + (dismissedList.Count > 6 ? "…" : ""),
                AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 8, 0, 0),
            });
            card.Controls.Add(row);
        }
        foreach (var e in real.Take(150))
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 0) };
            var price = e.Dismissed
                ? Theme.Button("Voltar ao plano", (_, _) => RestoreRequested?.Invoke(e.Copy.UserAssetId), ButtonKind.Ghost)
                : Theme.Button("Preço…", (_, _) => ManualPriceRequested?.Invoke(e.Copy.AssetId, e.Copy.Name), ButtonKind.Ghost);
            price.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(price);
            row.Controls.Add(new Label { Text = $"{e.Copy.Label} · @{e.SourceName} — {e.Why}", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Text2, Margin = new Padding(0, 8, 0, 0) });
            card.Controls.Add(row);
        }
        if (real.Count > 150) card.Controls.Add(Line($"… e mais {real.Count - 150}", Theme.TextDim));
        return card;
    }

    private static Control SalesCard(IReadOnlyList<DeliveredView> delivered, IReadOnlyList<SaleView> sales)
    {
        var card = new CardStack();
        card.Controls.Add(new Label { Text = "Vendas das cópias entregues", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 6) });
        var linked = sales.Where(x => x.Link == SaleLink.Linked).ToList();
        long credited = linked.Where(x => !x.Sale.Pending).Sum(x => x.Sale.Amount), pending = linked.Where(x => x.Sale.Pending).Sum(x => x.Sale.Amount);
        long estimated = linked.Sum(x => x.EstimatedNet ?? 0);
        card.Controls.Add(Line($"{Fmt.Count(delivered.Count, "cópia entregue", "cópias entregues")} · {Fmt.Count(linked.Count, "vendida", "vendidas")} · creditado {Fmt.N(credited)}" +
                               (pending > 0 ? $" · pendente {Fmt.N(pending)}" : "") + (linked.Count > 0 ? $" · estimado {Fmt.N(estimated)}" : ""), Theme.Text, Theme.Base));
        if (delivered.Count == 0) card.Controls.Add(Line("Nenhuma cópia entregue ainda. As vendas aparecem aqui depois das trades concluídas.", Theme.TextDim));
        foreach (var d in delivered.OrderBy(d => d.State).ThenBy(d => d.AccountName).Take(200))
        {
            string state = d.State switch
            {
                DeliveredState.Sold when d.Sale is { } s => $"vendida por {Fmt.N(s.Sale.Amount)}{(s.Sale.Pending ? " (pendente)" : "")} em {Fmt.Time(s.Sale.CreatedUtc)}" +
                    (s.EstimatedNet is { } e ? $" · estimado {Fmt.N(e)} ({(s.Sale.Amount - e >= 0 ? "+" : "")}{Fmt.N(s.Sale.Amount - e)})" : ""),
                DeliveredState.OnHold => "em holding (ainda não pode ser anunciada)",
                DeliveredState.CanList => "pode ser anunciada",
                DeliveredState.Gone => "saiu da conta sem venda ligada — a conferir",
                _ => "inventário não lido",
            };
            card.Controls.Add(Line($"{d.Copy.Label} · @{d.AccountName} · {d.StepId} — {state}",
                d.State is DeliveredState.Gone ? Theme.WarnInk : d.State == DeliveredState.Sold ? Theme.Text : Theme.Text2));
        }
        var doubt = sales.Where(x => x.Link == SaleLink.Ambiguous).ToList();
        if (doubt.Count > 0)
        {
            card.Controls.Add(new Label { Text = $"Vendas a conferir ({doubt.Count})", AutoSize = true, Font = Theme.Header, ForeColor = Theme.WarnInk, Margin = new Padding(0, 10, 0, 2) });
            foreach (var x in doubt.Take(50))
                card.Controls.Add(Line($"{x.Sale.Name} · @{x.AccountName} · {Fmt.N(x.Sale.Amount)} em {Fmt.Time(x.Sale.CreatedUtc)} — {x.Via}", Theme.WarnInk));
        }
        int other = sales.Count(x => x.Link == SaleLink.NotFromPlan);
        if (other > 0) card.Controls.Add(Line($"{Fmt.Count(other, "venda", "vendas")} de itens que não vieram do plano (não entram nos totais).", Theme.TextDim));
        return card;
    }
}
