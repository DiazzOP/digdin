using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.App.UI;

/// <summary>
/// Compact card for the active account, at the top of "Contas": balance, pending, what can be sent now and
/// the Plus status. The Plus status opens the Plus category. The account list stays right below.
/// </summary>
internal sealed class AccountSummary : Panel
{
    // A card (Bloxkeep's .card: --card fill, hairline, 14 px radius) on the sidebar: title row, a grid of
    // label/value pairs, the Plus status as a chip with a dot and a word, and the reason for "a conferir" as a note.
    private readonly CardPanel _card = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 12) };
    private readonly Label _title = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Card, Margin = new Padding(0) };
    private readonly Label _read = new() { AutoSize = true, Font = Theme.Fine, ForeColor = Theme.TextDim, BackColor = Theme.Card, Margin = new Padding(8, 3, 0, 0) };
    private readonly Value _balance = new("Saldo"), _pending = new("Pendentes"), _capacity = new("Pode enviar");
    private readonly Value _today = new("Restante hoje"), _month = new("Restante 30 dias");
    private readonly Chip _plus = new("Plus") { Margin = new Padding(0, 0, 6, 0) };
    private readonly UiButton _refresh;
    /// <summary>Main reason for "a conferir", on one line with "…"; the full text is in the tooltip.</summary>
    private readonly NoteLabel _why = new() { Kind = NoteKind.Warn, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Font = Theme.Small, Padding = new Padding(10, 6, 10, 6) };
    private readonly ToolTip _tip = new() { InitialDelay = 200, AutoPopDelay = 20_000 };
    /// <summary>A transfer limit went down since the previous read: stays until "Ok, entendi" or until it is back.</summary>
    private readonly NoteLabel _drop = new() { Kind = NoteKind.Warn, Font = Theme.Small, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 8, 0, 4), Visible = false };
    private readonly UiButton _ack;
    private readonly FlowLayoutPanel _dropRow = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0), Visible = false };

    public event Action? PlusClicked;
    public event Action? RefreshRequested;
    public event Action? LimitDropAcknowledged;

    public AccountSummary()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Sidebar;
        Padding = new Padding(10, 6, 10, 8);
        Margin = new Padding(0);

        _plus.Click += (_, _) => PlusClicked?.Invoke();
        _tip.SetToolTip(_plus, "Abrir esta conta na aba Plus");
        _refresh = Theme.Named(new UiButton { Kind = ButtonKind.Ghost, Icon = UiIcon.Refresh, Margin = new Padding(0) }, "Atualizar saldo, Plus e limites desta conta", _tip);
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Card, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 6) };
        head.Controls.AddRange([_title, _read]);

        // Figures on two rows: three short ones, then the two quotas (wider: "10.000 / 10.000" never wraps).
        var values = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, RowCount = 2, Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 8) };
        for (int c = 0; c < 6; c++) values.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
        values.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        values.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var (v, col, row, span) in new[] { (_balance, 0, 0, 2), (_pending, 2, 0, 2), (_capacity, 4, 0, 2), (_today, 0, 1, 3), (_month, 3, 1, 3) })
        {
            v.Dock = DockStyle.Fill;
            values.Controls.Add(v, col, row);
            values.SetColumnSpan(v, span);
        }

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(0) };
        actions.Controls.AddRange([_plus, _refresh]);

        _why.Height = TextRenderer.MeasureText("Ag", Theme.Small).Height + 14;
        _why.Margin = new Padding(0, 8, 0, 0);
        grid.Controls.Add(head, 0, 0);
        grid.Controls.Add(values, 0, 1);
        grid.Controls.Add(actions, 0, 2);
        grid.Controls.Add(_why, 0, 3);
        _ack = Theme.Button("Ok, entendi", (_, _) => LimitDropAcknowledged?.Invoke(), ButtonKind.Ghost);
        _ack.Margin = new Padding(0);
        _drop.Visible = true;
        _dropRow.Controls.AddRange([_drop, _ack]);
        grid.Controls.Add(_dropRow, 0, 4);
        for (int r = 0; r < 5; r++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _card.Controls.Add(grid);
        Controls.Add(_card);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int width = Math.Max(1, ClientSize.Width - Padding.Horizontal - _card.Padding.Horizontal - 2);
        if (_drop.MaximumSize.Width != width) _drop.MaximumSize = new Size(width, 0);
    }

    public void ShowLoading(bool busy)
    {
        _refresh.Enabled = !busy;
        _refresh.AccessibleName = busy ? "Atualizando…" : "Atualizar saldo, Plus e limites desta conta";
    }

    public void Show(string username, AccountFinance? f, DateTimeOffset now)
    {
        _title.Text = "@" + username;
        _drop.Text = Fmt.LimitDrops(LimitWatch.Pending(f));
        _dropRow.Visible = _drop.Text.Length > 0;
        var plus = PlusStatus.Evaluate(f, now);
        _plus.Text = "Plus · " + Fmt.PlusText(plus);
        _plus.DotColor = Fmt.PlusColor(plus);
        _tip.SetToolTip(_plus, (plus.Reason is { } why ? "A conferir: " + why : Fmt.PlusDates(plus)) + "\nClique para ver os detalhes desta conta na aba Plus.");

        if (f is null)
        {
            foreach (var v0 in new[] { _balance, _pending, _capacity, _today, _month }) v0.Set("—", Theme.TextDim);
            _read.Text = "saldo ainda não consultado";
            _why.Text = "";
            _why.Visible = false;
            return;
        }
        var v = QuotaCalculator.Compute(f, now);
        bool fresh(DateTimeOffset? at, string field) => at is { } t && now - t <= QuotaCalculator.MaxAge && !v.FailedReads.Contains(field);

        // Numbers in the text colour (green is for "alive", not for values); "a conferir" in the warning ink.
        if (v.Robux is { } bal && fresh(f.RobuxAtUtc, FinanceField.Robux)) _balance.Set(Fmt.N(bal), Theme.Text);
        else _balance.Set(Fmt.ToVerify, Theme.WarnInk);
        if (v.PendingRobux is { } pen && fresh(f.PendingAtUtc, FinanceField.Pending)) _pending.Set(Fmt.N(pen), Theme.Text);
        else _pending.Set(Fmt.ToVerify, Theme.WarnInk);

        if (v.State == CapacityState.Estimated) _capacity.Set(Fmt.N(v.Capacity!.Value), Theme.Text);
        else _capacity.Set(Fmt.ToVerify, Theme.WarnInk);
        // Remaining transfer quota (panel estimate): shown whenever it can be computed, even if "Pode enviar" is a conferir.
        _today.Set(v.AvailableToday is { } rd ? $"{Fmt.N(rd)} / {Fmt.N(v.EffectiveDaily!.Value)}" : Fmt.ToVerify, v.AvailableToday is null ? Theme.WarnInk : Theme.Text);
        _month.Set(v.RemainingMonthly is { } rm ? $"{Fmt.N(rm)} / {Fmt.N(v.EffectiveMonthly!.Value)}" : Fmt.ToVerify, v.RemainingMonthly is null ? Theme.WarnInk : Theme.Text);
        _tip.SetToolTip(_today, "Estimativa: o menor entre o restante do teto diário (desde 00:00 de Brasília) e o restante dos 30 dias." +
            (v.TodayLimitedByMonth ? $" Hoje quem limita é a cota de 30 dias; do teto diário restariam {Fmt.N(v.RemainingDaily!.Value)}." : "") +
            $" O dia renova {Fmt.Time(v.NextDailyResetUtc)}.");
        _tip.SetToolTip(_month, "Estimativa: teto mensal efetivo − enviado nos últimos 30 dias." +
            (v.MonthlyReleases.FirstOrDefault(r => r.AtUtc > now) is { } next ? $" Próxima liberação: +{Fmt.N(next.Amount)} em {Fmt.Time(next.AtUtc)}." : ""));
        _tip.SetToolTip(_capacity, v.State == CapacityState.Estimated
            ? "Estimativa: menor valor entre saldo, restante de hoje e restante de 30 dias."
            : "A conferir porque:\n• " + string.Join("\n• ", v.ToVerifyReasons));
        _why.Text = v.State == CapacityState.ToVerify && v.ToVerifyReasons.Count > 0 ? v.ToVerifyReasons[0] : "";
        _why.Visible = _why.Text.Length > 0;
        _tip.SetToolTip(_why, v.ToVerifyReasons.Count == 0 ? null : "A conferir porque:\n• " + string.Join("\n• ", v.ToVerifyReasons));
        _read.Text = v.LastReadUtc is { } lr ? "consultado " + Fmt.Time(lr) : "saldo ainda não consultado";
    }

    /// <summary>Caption over a value, one line each, never wrapped ("…" if a column is too narrow; the tooltip has the rest).</summary>
    private sealed class Value : Control
    {
        private readonly string _caption;
        private string _value = "—";
        private Color _ink = Theme.TextDim;

        public Value(string caption)
        {
            _caption = caption;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Margin = new Padding(0, 0, 8, 6);
            Height = TextRenderer.MeasureText("Ag", Theme.Small).Height + TextRenderer.MeasureText("Ag", Theme.Title).Height + 2;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = caption;
        }

        public override Size GetPreferredSize(Size proposed) =>
            new(Math.Max(TextRenderer.MeasureText(_caption, Theme.Small).Width, TextRenderer.MeasureText(_value, Theme.Title).Width),
                TextRenderer.MeasureText("Ag", Theme.Small).Height + TextRenderer.MeasureText("Ag", Theme.Title).Height + 2);

        public void Set(string text, Color color)
        {
            _value = text;
            _ink = color;
            AccessibleName = $"{_caption}: {text}";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            const TextFormatFlags F = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int h1 = TextRenderer.MeasureText("Ag", Theme.Small).Height;
            TextRenderer.DrawText(g, _caption, Theme.Small, new Rectangle(0, 0, Width, h1), Theme.TextDim, F);
            TextRenderer.DrawText(g, _value, Theme.Title, new Rectangle(0, h1 + 1, Width, Height - h1 - 1), _ink, F);
        }
    }
}
