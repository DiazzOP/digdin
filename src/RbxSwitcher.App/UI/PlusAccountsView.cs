using System.Drawing.Drawing2D;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.App.UI;

/// <summary>
/// The "Plus" category: every account with its subscription and quotas, searchable and filtered by group
/// and status, with the selected account's details in a side panel. Look follows the Bloxkeep design rules
/// (see Theme.cs): compact rows on a card with hairlines, status as dot + word, chips filter, one primary.
/// </summary>
internal sealed class PlusAccountsView : Panel
{
    private const string AllGroups = "Todos os grupos";

    /// <summary>Status filters, in the order of <see cref="FilterIndex"/>. Index 0 = all.</summary>
    private static readonly (string Text, Color? Dot)[] StatusFilters =
    [
        ("Todos", null), ("Ativo", Theme.Valid), ("Expirando", Theme.Warning), ("Em teste", Theme.Online),
        ("Sem Plus", Theme.Unknown), ("Expirado", Theme.Expired), ("A conferir", Theme.Warning),
        ("Limite reduzido", Theme.Warning), // not a Plus status: accounts whose transfer limit went down
    ];
    private const int LimitDropFilter = 7;

    private readonly FieldBox _searchBox = new("Buscar conta…");
    private TextBox _search => _searchBox.Box;
    private readonly DropdownButton _groups = new("Grupo");
    private readonly Chip[] _chips;
    private int _status;
    /// <summary>Totals of what the search and group show (not the status chip), and how old the oldest read is.</summary>
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Text2, Padding = new Padding(0, 0, 0, 10) };
    private int _sortColumn = -1;
    private bool _sortDesc;
    private readonly Label _count = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.Small, Margin = new Padding(0, 9, 10, 0) };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TextDim, BackColor = Theme.Card };
    private readonly DataGridView _list = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, AllowUserToResizeColumns = false, MultiSelect = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None,
        BackgroundColor = Theme.Card, GridColor = Theme.Line, CellBorderStyle = DataGridViewCellBorderStyle.None,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        StandardTab = true,
    };
    private readonly Panel _detail = new() { Dock = DockStyle.Right, BackColor = Theme.Sidebar, Visible = false, Padding = new Padding(1, 0, 0, 0) };
    private readonly UiButton _details, _refreshAll;
    private Row[] _rows = [];
    private int _hoverRow = -1;
    private bool _updating;

    public long? SelectedId => _list.CurrentRow?.Tag as long?;
    public long? DetailId { get; private set; }
    public event Action? FiltersChanged;
    public event Action? DetailChanged;
    public event Action? RefreshAllRequested;

    public PlusAccountsView(PlusView details)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;

        // ── Heading: title + subtitle, the batch refresh on the right (secondary: the page has no single "go") ──
        _refreshAll = Theme.Button("Atualizar tudo", (_, _) => RefreshAllRequested?.Invoke());
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        titles.Controls.AddRange([
            new Label { Text = "Roblox Plus", AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = Padding.Empty },
            new Label { Text = "Assinatura e cotas de transferência de cada conta", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 0) },
        ]);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 12) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(titles, 0, 0);
        heading.Controls.Add(_refreshAll, 1, 0);
        _refreshAll.Anchor = AnchorStyles.Right | AnchorStyles.Top;

        // ── Filters: search field + group select, then status chips with counts ──
        _searchBox.Width = LogicalToDeviceUnits(240);
        _searchBox.Margin = new Padding(0, 0, 8, 0);
        _groups.Margin = new Padding(0, 2, 0, 0);
        _search.TextChanged += (_, _) => FiltersChanged?.Invoke();
        _groups.SelectionChanged += () => { if (!_updating) FiltersChanged?.Invoke(); };
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 8) };
        filters.Controls.AddRange([_searchBox, _groups]);

        _chips = StatusFilters.Select((f, i) =>
        {
            var chip = new Chip(f.Text) { DotColor = f.Dot, On = i == 0 };
            chip.Click += (_, _) => SetStatus(i);
            return chip;
        }).ToArray();
        var chips = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 10) };
        chips.Controls.AddRange(_chips);

        // ── List on a card ──
        _details = Theme.Button("Ver detalhes", (_, _) => OpenSelected());
        _details.Enabled = false;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([_count, _details]);

        _list.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, SelectionBackColor = Theme.Selected, SelectionForeColor = Theme.Text,
            Font = Theme.Base, Padding = new Padding(12, 0, 6, 0),
        };
        _list.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.TextDim, SelectionBackColor = Theme.Card, Font = Theme.Header, Padding = new Padding(12, 0, 6, 0),
        };
        _list.ColumnHeadersHeight = LogicalToDeviceUnits(36);
        _list.RowTemplate.Height = LogicalToDeviceUnits(48);
        AddColumn("Conta", 130, 28);
        AddColumn("Status do Plus", 118, 22);
        AddColumn("Renovação", 104, 16);
        AddColumn("Cota hoje", 104, 16);
        AddColumn("Cota 30 dias", 118, 18);
        AddColumn("Espaço", 92, 14);
        _list.CellPainting += PaintCell;
        _list.RowPostPaint += PaintRail;
        _list.CellMouseEnter += (_, e) => SetHover(e.RowIndex);
        _list.MouseLeave += (_, _) => SetHover(-1);
        _list.CurrentCellChanged += (_, _) =>
        {
            _details.Enabled = SelectedId is not null;
            _list.Invalidate();
            if (!_updating && _detail.Visible) OpenSelected();
        };
        _list.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };

        Theme.DarkScroll(_list);
        _list.ColumnHeaderMouseClick += (_, e) =>
        {
            // Click a header to sort by it; again to reverse. Numbers start from the largest, text from A.
            if (_sortColumn == e.ColumnIndex) _sortDesc = !_sortDesc;
            else { _sortColumn = e.ColumnIndex; _sortDesc = e.ColumnIndex >= 3; }
            _rows = [];
            FiltersChanged?.Invoke();
        };
        foreach (DataGridViewColumn c in _list.Columns) c.HeaderCell.ToolTipText = "Clique para ordenar";
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 8) };
        card.Controls.AddRange([_empty, _list]);

        var overview = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), BackColor = Theme.Window };
        overview.Controls.AddRange([card, actions, chips, filters, _summary, heading]);

        // ── Side panel: hairline on the left, title + ghost close ──
        var close = Theme.Button("Fechar", (_, _) => SetDetail(null), ButtonKind.Ghost);
        close.Anchor = AnchorStyles.Right;
        var detailHeader = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14, 10, 10, 0), BackColor = Theme.Sidebar };
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeader.Controls.Add(new Label { Text = "Detalhes", AutoSize = true, Font = Theme.Header, ForeColor = Theme.TextDim, Anchor = AnchorStyles.Left }, 0, 0);
        detailHeader.Controls.Add(close, 1, 0);
        details.BackColor = Theme.Sidebar;
        _detail.Controls.AddRange([details, detailHeader]);
        _detail.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, 0, _detail.Height); };
        Controls.AddRange([overview, _detail]);
        Resize += (_, _) => SizeDetails();
    }

    private void AddColumn(string title, int minimum, float weight) => _list.Columns.Add(new DataGridViewTextBoxColumn
    {
        HeaderText = title, MinimumWidth = LogicalToDeviceUnits(minimum), FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable,
    });

    private void SetHover(int row)
    {
        if (_hoverRow == row) return;
        int old = _hoverRow;
        _hoverRow = row;
        if (old >= 0 && old < _list.RowCount) _list.InvalidateRow(old);
        if (row >= 0 && row < _list.RowCount) _list.InvalidateRow(row);
    }

    private void SetStatus(int index, bool notify = true)
    {
        _status = index;
        for (int i = 0; i < _chips.Length; i++) _chips[i].On = i == index;
        if (notify) FiltersChanged?.Invoke();
    }

    // ── Drawing: compact rows, hairlines, status as dot + word, active row with a 3 px inverse rail ──

    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var g = e.Graphics!;
        var r = e.CellBounds;
        float s = DeviceDpi / 96f;
        int pad = (int)(12 * s);
        if (e.RowIndex < 0)
        {
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r);
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            bool sorted = e.ColumnIndex == _sortColumn;
            var hs = TextRenderer.MeasureText(g, e.Value as string, Theme.Header);
            int arrow = sorted ? (int)(16 * s) : 0;
            TextRenderer.DrawText(g, e.Value as string, Theme.Header, new Rectangle(r.X + pad, r.Y, Math.Max(1, r.Width - pad - arrow), r.Height), sorted ? Theme.Text : Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (sorted)
                Icons.Draw(g, _sortDesc ? UiIcon.ChevronDown : UiIcon.ChevronUp,
                    new RectangleF(Math.Min(r.X + pad + hs.Width, r.Right - arrow), r.Y, arrow, r.Height), Theme.Text, s);
            e.Handled = true;
            return;
        }
        if (e.RowIndex >= _rows.Length) return;
        var row = _rows[e.RowIndex];
        bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
        using (var b = new SolidBrush(selected ? Theme.Selected : e.RowIndex == _hoverRow ? Theme.Hover : Theme.Card)) g.FillRectangle(b, r);
        using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);

        const TextFormatFlags One = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var text = new Rectangle(r.X + pad, r.Y, Math.Max(1, r.Width - pad - (int)(6 * s)), r.Height);
        switch (e.ColumnIndex)
        {
            case 0:
                int h1 = TextRenderer.MeasureText("Ag", Theme.Bold).Height, h2 = TextRenderer.MeasureText("Ag", Theme.Small).Height;
                int top = r.Y + (r.Height - h1 - h2) / 2;
                TextRenderer.DrawText(g, row.Title, Theme.Bold, new Rectangle(text.X, top, text.Width, h1), Theme.Text, One);
                TextRenderer.DrawText(g, "@" + row.Username, Theme.Small, new Rectangle(text.X, top + h1, text.Width, h2), Theme.TextDim, One);
                break;
            case 1:
                Theme.Dot(g, text.X + 3.5f * s, r.Y + r.Height / 2f, 7 * s, row.Dot);
                var word = new Rectangle(text.X + (int)(14 * s), r.Y, Math.Max(1, text.Width - (int)(14 * s)), r.Height);
                TextRenderer.DrawText(g, row.Status, Theme.Base, word, row.Ink, One | TextFormatFlags.VerticalCenter);
                break;
            default:
                string value = e.ColumnIndex switch { 2 => row.Date, 3 => row.Daily, 4 => row.Monthly, _ => row.Space };
                bool dropped = (e.ColumnIndex == 3 && row.DailyDropped) || (e.ColumnIndex == 4 && row.MonthlyDropped);
                if (dropped) value = "↓ " + value; // the limit went down since the previous read
                Color ink = e.ColumnIndex == 5 ? row.SpaceInk
                    : value == "—" ? Theme.TextDim : dropped ? Theme.WarnInk : e.ColumnIndex == 2 ? Theme.Text2 : Theme.Text;
                TextRenderer.DrawText(g, value, Theme.Base, text, ink, One | TextFormatFlags.VerticalCenter);
                break;
        }
        e.Handled = true;
    }

    private void PaintRail(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (_list.CurrentRow?.Index != e.RowIndex) return;
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rb = e.RowBounds;
        using var path = Theme.Round(new RectangleF(rb.X, rb.Y + 8 * s, 3 * s, rb.Height - 16 * s), 1.5f * s);
        using var b = new SolidBrush(Theme.Inv);
        g.FillPath(b, path);
    }

    // Narrow enough that the table keeps whole numbers ("10.000 / 10.000") at 1366 px.
    private void SizeDetails() => _detail.Width = Math.Min(LogicalToDeviceUnits(460),
        Math.Max(LogicalToDeviceUnits(380), ClientSize.Width - LogicalToDeviceUnits(660)));

    private void OpenSelected()
    {
        if (SelectedId is { } id) SetDetail(id);
    }

    public void OpenAccount(long id)
    {
        int index = Array.FindIndex(_rows, row => row.Id == id);
        if (index < 0)
        {
            _updating = true;
            try { _search.Clear(); _groups.Selected = AllGroups; SetStatus(0, notify: false); }
            finally { _updating = false; }
            FiltersChanged?.Invoke();
            index = Array.FindIndex(_rows, row => row.Id == id);
        }
        if (index < 0) return;
        _list.CurrentCell = _list.Rows[index].Cells[0];
        SetDetail(id);
    }

    private void SetDetail(long? id)
    {
        if (DetailId == id) return;
        DetailId = id;
        SizeDetails();
        _detail.Visible = id is not null;
        DetailChanged?.Invoke();
    }

    /// <summary>Text, ink and sort key of the "Espaço" column (space for new Limited lots).</summary>
    public Func<Account, (string Text, Color Ink, long Sort, string? Tip)> SpaceOf { get; set; } = _ => ("—", Theme.TextDim, -1, null);

    public void SetAccounts(AccountStore? store, DateTimeOffset now, bool refreshingAll)
    {
        _refreshAll.Enabled = !refreshingAll && store?.All.Any(a => a.HasSession && a.Status != SessionStatus.Expired) == true;
        _refreshAll.Text = refreshingAll ? "Atualizando…" : "Atualizar tudo";
        string[] groups = [AllGroups, .. (store?.Groups ?? [])];
        _updating = true;
        try
        {
            if (!_groups.Items.SequenceEqual(groups)) _groups.SetItems(groups);
            string? group = _groups.Selected is { } g && g != AllGroups ? g : null;
            var accounts = (store?.Search(_search.Text, group) ?? [])
                .Select(a => (Account: a, Status: PlusStatus.Evaluate(a.Finance, now))).ToArray();

            // Counts per status for what search + group leave, so each chip says how many it would show.
            var counts = new int[StatusFilters.Length];
            counts[0] = accounts.Length;
            foreach (var a in accounts)
            {
                counts[FilterIndex(a.Status)]++;
                if (LimitWatch.Pending(a.Account.Finance).Count > 0) counts[LimitDropFilter]++;
            }
            for (int i = 0; i < _chips.Length; i++)
            {
                if (_chips[i].Count == counts[i]) continue;
                _chips[i].Count = counts[i];
                _chips[i].Resize_();
                _chips[i].Invalidate();
            }

            _summary.Text = Summary(accounts.Select(a => (a.Account, a.Status)).ToList(), now);
            var rows = Sort(accounts.Where(a => _status == 0
                    || (_status == LimitDropFilter ? LimitWatch.Pending(a.Account.Finance).Count > 0 : _status == FilterIndex(a.Status)))
                .Select(a => MakeRow(a.Account, a.Status, now) is var r && SpaceOf(a.Account) is var sp ? r with { Space = sp.Text, SpaceInk = sp.Ink, SpaceSort = sp.Sort, SpaceTip = sp.Tip } : null!)).ToArray();
            _count.Text = $"{rows.Length} de {store?.Count ?? 0} contas";
            _empty.Text = store?.Count > 0 ? "Nenhuma conta corresponde aos filtros." : "Adicione uma conta na aba Contas para acompanhar o Plus.";
            _empty.Visible = rows.Length == 0;
            _list.Visible = rows.Length > 0;
            if (_rows.SequenceEqual(rows)) return;
            long? selected = SelectedId;
            int firstRow = _list.FirstDisplayedScrollingRowIndex;
            _rows = rows; // before the rows exist: painting reads it by index
            _list.Rows.Clear();
            foreach (var row in rows)
            {
                var gr = _list.Rows[_list.Rows.Add(row.Title, row.Status, row.Date, row.Daily, row.Monthly, row.Space)];
                gr.Tag = row.Id;
                gr.Cells[5].ToolTipText = row.SpaceTip ?? "";
            }
            int index = Array.FindIndex(rows, row => row.Id == selected);
            if (rows.Length > 0)
            {
                _list.CurrentCell = _list.Rows[Math.Max(index, 0)].Cells[0];
                if (index >= 0 && firstRow >= 0) _list.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, rows.Length - 1);
            }
            _details.Enabled = rows.Length > 0;
        }
        finally { _updating = false; }
        if (DetailId is { } id && !_rows.Any(row => row.Id == id)) SetDetail(null);
    }

    private IEnumerable<Row> Sort(IEnumerable<Row> rows)
    {
        if (_sortColumn < 0) return rows;
        IOrderedEnumerable<Row> o = _sortColumn switch
        {
            0 => Order(rows, r => r.Title.ToLowerInvariant()),
            1 => Order(rows, r => r.Rank),
            2 => Order(rows, r => r.When ?? (_sortDesc ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue)),
            3 => Order(rows, r => r.DailyLeft ?? -1),
            4 => Order(rows, r => r.MonthlyLeft ?? -1),
            _ => Order(rows, r => r.SpaceSort),
        };
        return o.ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);

        IOrderedEnumerable<Row> Order<TKey>(IEnumerable<Row> src, Func<Row, TKey> key) => _sortDesc ? src.OrderByDescending(key) : src.OrderBy(key);
    }

    /// <summary>"12 contas · 8 com Plus (1 expirando) · disponível hoje 54.300 em 11 de 12 · leitura mais antiga 18/11 14:20".</summary>
    private static string Summary(List<(Account Account, PlusStatusView Status)> accounts, DateTimeOffset now)
    {
        if (accounts.Count == 0) return "";
        int plus = accounts.Count(a => a.Status.Label is PlusLabel.Active or PlusLabel.Trial);
        int expiring = accounts.Count(a => a.Status.Expiring);
        long today = 0;
        int known = 0;
        DateTimeOffset? oldest = null;
        foreach (var (a, _) in accounts)
        {
            if (a.Finance is not { } f) continue;
            var q = QuotaCalculator.Compute(f, now);
            if (q.AvailableToday is { } t) { today += t; known++; }
            if (q.LastReadUtc is { } at && (oldest is null || at < oldest)) oldest = at;
        }
        var parts = new List<string> { $"{accounts.Count} conta{(accounts.Count == 1 ? "" : "s")}", $"{plus} com Plus" + (expiring > 0 ? $" ({expiring} expirando)" : "") };
        if (known > 0) parts.Add($"disponível hoje {Fmt.N(today)}" + (known < accounts.Count ? $" em {known} de {accounts.Count}" : ""));
        if (oldest is { } o) parts.Add("leitura mais antiga " + Fmt.Time(o));
        return string.Join("  ·  ", parts);
    }

    private static int FilterIndex(PlusStatusView status) => status.Expiring ? 2 : status.Label switch
    {
        PlusLabel.Active => 1, PlusLabel.Trial => 3, PlusLabel.None => 4, PlusLabel.Expired => 5, _ => 6,
    };

    private static Row MakeRow(Account account, PlusStatusView status, DateTimeOffset now)
    {
        string date = status.Label switch
        {
            PlusLabel.Active or PlusLabel.Trial when status.Info is { WillRenew: true, RenewsUtc: { } renewal } => "Renova " + Fmt.ShortDay(renewal),
            PlusLabel.Active or PlusLabel.Trial when status.Info?.ExpiresUtc is { } end => "Até " + Fmt.ShortDay(end),
            PlusLabel.Expired when status.Info?.ExpiresUtc is { } expired => "Venceu " + Fmt.ShortDay(expired),
            _ => "—",
        };
        var quota = account.Finance is { } finance ? QuotaCalculator.Compute(finance, now) : null;
        var drops = LimitWatch.Pending(account.Finance);
        DateTimeOffset? when = status.Info is { } info ? (info.WillRenew == true ? info.RenewsUtc : null) ?? info.ExpiresUtc : null;
        return new(account.UserId, account.Title, account.Username, Fmt.PlusText(status), Fmt.PlusColor(status), Fmt.PlusInk(status), date,
            FormatQuota(quota?.AvailableToday, quota?.EffectiveDaily), FormatQuota(quota?.RemainingMonthly, quota?.EffectiveMonthly),
            drops.Any(d => d.Kind == LimitKind.Daily), drops.Any(d => d.Kind == LimitKind.Monthly),
            FilterIndex(status), when, quota?.AvailableToday, quota?.RemainingMonthly);
    }

    private static string FormatQuota(long? remaining, long? limit) =>
        remaining is { } r && limit is { } l ? $"{Fmt.N(r)} / {Fmt.N(l)}" : "—";

    private sealed record Row(long Id, string Title, string Username, string Status, Color Dot, Color Ink, string Date, string Daily, string Monthly,
                              bool DailyDropped, bool MonthlyDropped, int Rank, DateTimeOffset? When, long? DailyLeft, long? MonthlyLeft)
    {
        public string Space { get; init; } = "—";
        public Color SpaceInk { get; init; } = Theme.TextDim;
        public long SpaceSort { get; init; } = -1;
        public string? SpaceTip { get; init; }
    }
}
