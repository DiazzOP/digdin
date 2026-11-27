using System.Drawing.Drawing2D;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.UI;

/// <summary>
/// The "Limiteds" category: every account's Limiteds in one list, grouped by account (or by item), with where each copy
/// stands for selling — on hold, ready, on sale and the cheapest, or outbid — the account's quota, and a suggested
/// price. Read-only: "Abrir para vender" / "Mudar preço" open the item page in that account's tab; the person sells
/// on roblox.com.
/// </summary>
internal sealed class LimitedsView : Panel
{
    private const string AllGroups = "Todos os grupos";
    private static readonly (string Text, Color? Dot)[] Filters =
    [
        ("Todas", null), ("Superadas", Theme.Expired), ("Prontas", Theme.Valid), ("À venda", Theme.Online), ("Em holding", Theme.Warning), ("Vendidas", Theme.Unknown),
    ];

    private readonly FieldBox _searchBox = new("Buscar item ou conta…");
    private readonly DropdownButton _groups = new("Grupo");
    private readonly Segmented _mode = new("Por conta", "Por item");
    private readonly Chip[] _chips;
    private readonly Chip _withQuota = new("Com cota agora") { Outline = true };
    private readonly Chip _smalls = new("Mostrar Smalls") { Outline = true };
    private int _filter;
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Text2, Padding = new Padding(0, 0, 0, 10) };
    private readonly Label _count = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.Small, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.TextDim, BackColor = Theme.Card };
    private readonly UiButton _refresh, _configure, _record;
    private readonly DataGridView _list = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, AllowUserToResizeColumns = false, MultiSelect = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None,
        BackgroundColor = Theme.Card, GridColor = Theme.Line, CellBorderStyle = DataGridViewCellBorderStyle.None,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, EnableHeadersVisualStyles = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, StandardTab = true,
    };
    private readonly Panel _detail = new() { Dock = DockStyle.Right, BackColor = Theme.Sidebar, Visible = false };
    private readonly FlowLayoutPanel _detailBody = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Sidebar, Padding = new Padding(16, 6, 14, 14) };
    private readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 15_000 };

    private SellBoardResult _board = new([], []);
    private IReadOnlyDictionary<long, ItemListings> _listings = new Dictionary<long, ItemListings>();
    private IReadOnlyDictionary<long, ItemMarket> _market = new Dictionary<long, ItemMarket>();
    private IReadOnlyDictionary<long, string> _mine = new Dictionary<long, string>();
    private int _hiddenAccounts, _fee = 30;
    private string? _note;
    private long _pendingAll;
    private Line[] _lines = [];
    private string? _selectedKey;
    private bool _updating;
    private int _hover = -1;

    public Func<long, Image?> ThumbOf { get; set; } = _ => null;
    public event Action<IReadOnlyList<(long AssetId, string Name)>>? NeedThumbs;
    public SellRow? SelectedRow => _lines.FirstOrDefault(l => l.Key == _selectedKey)?.Row;
    public event Action? RefreshRequested, ConfigureRequested, RecordRequested;
    /// <summary>Open the item page in the copy's account (to sell or change the price there).</summary>
    public event Action<SellRow>? OpenRequested;
    /// <summary>Read this copy's account and item again (after changing it on roblox.com).</summary>
    public event Action<SellRow>? RereadRequested;

    public LimitedsView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;

        _refresh = Theme.Button("Atualizar leituras", (_, _) => RefreshRequested?.Invoke());
        _tip.SetToolTip(_refresh, "Lê de novo os Limiteds, o que está à venda, os menores preços e as vendas de cada conta (uma por vez).");
        _configure = Theme.Button("Configurar…", (_, _) => ConfigureRequested?.Invoke(), ButtonKind.Ghost);
        _tip.SetToolTip(_configure, "Quantos Robux abaixo do menor preço e o piso das sugestões (em Distribuição de Limiteds).");
        _record = Theme.Button("Gravar venda", (_, _) => RecordRequested?.Invoke(), ButtonKind.Ghost);
        _tip.SetToolTip(_record, "Grava, sem valores, as chamadas que a página de venda do Roblox faz por 60 s — para ajustar as leituras desta aba.");

        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        titles.Controls.AddRange([
            new Label { Text = "Limiteds", AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = Padding.Empty },
            new Label { Text = "À venda, pronto ou em holding em cada conta. Você vende no Roblox.", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 0) },
        ]);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _refresh.Margin = new Padding(0, 0, 6, 0);
        right.Controls.AddRange([_refresh, _configure, _record]);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 6) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(titles, 0, 0);
        heading.Controls.Add(right, 1, 0);

        _searchBox.Width = LogicalToDeviceUnits(230);
        _searchBox.Margin = new Padding(0, 0, 8, 0);
        _searchBox.Box.TextChanged += (_, _) => Render();
        _groups.Margin = new Padding(0, 2, 8, 0);
        _groups.SelectionChanged += () => { if (!_updating) Render(); };
        _mode.Margin = new Padding(0, 2, 0, 0);
        _mode.SelectedIndexChanged += _ => Render();
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 8) };
        filters.Controls.AddRange([_searchBox, _groups, _mode]);

        _chips = Filters.Select((f, i) =>
        {
            var chip = new Chip(f.Text) { DotColor = f.Dot, On = i == 0 };
            chip.Click += (_, _) => { _filter = i; for (int j = 0; j < _chips!.Length; j++) _chips[j].On = j == i; Render(); };
            return chip;
        }).ToArray();
        _withQuota.Click += (_, _) => { _withQuota.On = !_withQuota.On; Render(); };
        _smalls.Click += (_, _) => { _smalls.On = !_smalls.On; Render(); };
        _tip.SetToolTip(_withQuota, "Só contas com cota de 30 dias agora: o Robux da venda pode seguir logo.");
        _tip.SetToolTip(_smalls, "Smalls são itens de apoio das trades; ficam ocultos por padrão.");
        var chips = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 10) };
        chips.Controls.AddRange(_chips);
        chips.Controls.AddRange([_withQuota, _smalls]);

        _list.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, SelectionBackColor = Theme.Selected, SelectionForeColor = Theme.Text, Font = Theme.Base,
        };
        _list.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Card, ForeColor = Theme.TextDim, SelectionBackColor = Theme.Card, Font = Theme.Header };
        _list.ColumnHeadersHeight = LogicalToDeviceUnits(34);
        AddColumn("Item", 150, 24);
        AddColumn("Situação", 112, 17);
        AddColumn("Meu preço", 80, 10);
        AddColumn("Menor", 64, 9);
        AddColumn("RAP · por dia", 92, 14);
        AddColumn("Líquido", 72, 10);
        AddColumn("", 112, 12);
        _list.ScrollBars = ScrollBars.Vertical;
        _list.Columns[3].HeaderCell.ToolTipText = "Menor preço de outro vendedor";
        _list.CellPainting += PaintCell;
        _list.RowPrePaint += PaintGroupRow;
        _list.RowPostPaint += PaintRail;
        _list.CellMouseEnter += (_, e) => SetHover(e.RowIndex);
        _list.MouseLeave += (_, _) => SetHover(-1);
        _list.CellMouseMove += (_, e) => _list.Cursor = e.ColumnIndex == 6 && RowAt(e.RowIndex) is { } r && ActionText(r) is not null ? Cursors.Hand : Cursors.Default;
        _list.CurrentCellChanged += (_, _) => OnCurrentChanged();
        _list.CellClick += (_, e) => { if (e.ColumnIndex == 6 && RowAt(e.RowIndex) is { } r && ActionText(r) is not null) OpenRequested?.Invoke(r); };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && RowAt(_list.CurrentRow?.Index ?? -1) is { } r) { OpenRequested?.Invoke(r); e.Handled = true; } };
        Theme.DarkScroll(_list);
        // Painted in one go (rows of two heights left traces of text while redrawing).
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(_list, true);
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 8) };
        card.Controls.AddRange([_empty, _list]);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(2, 4, 0, 0) };
        footer.Controls.Add(_count);

        var overview = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 10), BackColor = Theme.Window };
        overview.Controls.AddRange([card, footer, chips, filters, _summary, heading]);

        var close = Theme.Button("Fechar", (_, _) => { _detail.Visible = false; }, ButtonKind.Ghost);
        close.Anchor = AnchorStyles.Right;
        var detailHeader = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14, 10, 10, 0), BackColor = Theme.Sidebar };
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detailHeader.Controls.Add(new Label { Text = "Detalhes", AutoSize = true, Font = Theme.Header, ForeColor = Theme.TextDim, Anchor = AnchorStyles.Left }, 0, 0);
        detailHeader.Controls.Add(close, 1, 0);
        Theme.DarkScroll(_detailBody);
        _detail.Controls.AddRange([_detailBody, detailHeader]);
        _detail.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, 0, _detail.Height); };
        Controls.AddRange([overview, _detail]);
        Resize += (_, _) => _detail.Width = Math.Min(LogicalToDeviceUnits(420), Math.Max(LogicalToDeviceUnits(360), ClientSize.Width / 3));
    }

    private void AddColumn(string title, int minimum, float weight) => _list.Columns.Add(new DataGridViewTextBoxColumn
    {
        HeaderText = title, MinimumWidth = LogicalToDeviceUnits(minimum), FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable,
    });

    // ───────────── state ─────────────

    /// <param name="hiddenAccounts">Accounts read that have nothing to sell (shown as a count only).</param>
    public void SetBoard(SellBoardResult board, IReadOnlyDictionary<long, ItemListings> listings, IReadOnlyDictionary<long, ItemMarket> market,
                         IReadOnlyDictionary<long, string> mine, int hiddenAccounts, int feePercent, bool busy, string? recording, string? note = null,
                         long pendingAll = 0)
    {
        _note = note;
        _pendingAll = pendingAll;
        _board = board; _listings = listings; _market = market; _mine = mine; _hiddenAccounts = hiddenAccounts; _fee = feePercent;
        _refresh.Enabled = !busy;
        _refresh.Text = busy ? "Lendo…" : "Atualizar leituras";
        _record.Enabled = recording is null;
        _record.Text = recording ?? "Gravar venda";
        string[] groups = [AllGroups, .. board.Accounts.Select(a => a.Group).OfType<string>().Distinct().Order()];
        _updating = true;
        try { if (!_groups.Items.SequenceEqual(groups)) _groups.SetItems(groups); }
        finally { _updating = false; }
        Render();
    }

    public void ThumbsLoaded()
    {
        _list.Invalidate();
        foreach (var strip in _detailBody.Controls.OfType<ItemPicture>()) strip.Invalidate();
    }

    // ───────────── filtering and rows ─────────────

    private sealed record Line(SellAccount? Account, long? ItemId, string? ItemTitle, SellRow? Row)
    {
        public bool Header => Row is null;
        public string Key => Row is { } r ? $"{r.AccountId}:{r.UserAssetId}:{r.Sale?.Id}" : Account is { } a ? "a" + a.UserId : "i" + ItemId;
    }

    private static int Order(SellSituation s) => s switch
    {
        SellSituation.Outbid => 0, SellSituation.Ready => 1, SellSituation.Unread => 2, SellSituation.NoPlus => 3, SellSituation.Lowest => 4,
        SellSituation.OnSale => 5, SellSituation.OnHold => 6, _ => 7,
    };

    private bool InFilter(SellRow r, int filter) => filter switch
    {
        1 => r.Situation == SellSituation.Outbid,
        2 => r.Situation is SellSituation.Ready or SellSituation.Unread or SellSituation.NoPlus, // free copies (as in the total)
        3 => r.Situation is SellSituation.Outbid or SellSituation.Lowest or SellSituation.OnSale,
        4 => r.Situation == SellSituation.OnHold,
        5 => r.Situation == SellSituation.Sold,
        _ => true,
    };

    private void Render()
    {
        string q = _searchBox.Box.Text.Trim();
        string? group = _groups.Selected is { } g && g != AllGroups ? g : null;
        var accounts = _board.Accounts.ToDictionary(a => a.UserId);
        var visible = _board.Rows.Where(r =>
        {
            var a = accounts[r.AccountId];
            if (r.Small && !_smalls.On) return false;
            if (_withQuota.On && !a.HasQuotaNow) return false;
            if (group is not null && a.Group != group) return false;
            return q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || r.AccountTitle.Contains(q, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // Chip counts and the summary describe what search, group and toggles leave (not the status chip).
        for (int i = 0; i < _chips.Length; i++)
        {
            int n = visible.Count(r => InFilter(r, i) && (i == 0 ? r.Situation != SellSituation.Sold : true));
            if (_chips[i].Count == n) continue;
            _chips[i].Count = n;
            _chips[i].Resize_();
            _chips[i].Invalidate();
        }
        var copies = visible.Where(r => r.Situation != SellSituation.Sold).ToList();
        int outbid = copies.Count(r => r.Situation == SellSituation.Outbid), ready = copies.Count(r => r.Situation is SellSituation.Ready or SellSituation.Unread or SellSituation.NoPlus);
        var onSale = copies.Where(r => r.Situation is SellSituation.Outbid or SellSituation.Lowest or SellSituation.OnSale).ToList();
        int soldToday = visible.Count(r => r.Sale is { } s && Core.Finance.BrasiliaTime.ToBrasilia(s.CreatedUtc).Date == Core.Finance.BrasiliaTime.ToBrasilia(DateTimeOffset.UtcNow).Date);
        _summary.Text = _board.Accounts.Count == 0 ? "Nenhum Limited para vender nas contas lidas. Use \"Atualizar leituras\"."
            : $"{Fmt.Count(copies.Count, "cópia", "cópias")} em {Fmt.Count(copies.Select(r => r.AccountId).Distinct().Count(), "conta", "contas")} · " +
              $"{Fmt.Count(outbid, "superada", "superadas")} · {Fmt.Count(ready, "pronta", "prontas")} · {onSale.Count} à venda (líquido ≈ {Fmt.N(onSale.Sum(r => r.Net ?? 0))}) · " +
              $"{Fmt.Count(soldToday, "vendida", "vendidas")} hoje";
        if (_board.Accounts.Count > 0 || _pendingAll > 0)
        {
            // Holdings of every account, whatever the filters (sources like Alfa and Beta included, also copies on hold or
            // in a distribution step), without Smalls, + the pending Robux of all accounts.
            var all = _board.Rows.Where(r => r.Situation != SellSituation.Sold).ToList();
            var t = SellBoard.Totals(all.Where(r => !r.Small), []);
            long limiteds = t.OnSale + t.Ready + t.OnHold;
            _summary.Text += $"\nTotal de todas as contas ≈ {Fmt.N(limiteds + _pendingAll)} líquido: Limiteds {Fmt.N(limiteds)} (à venda {Fmt.N(t.OnSale)} · " +
                             $"prontas {Fmt.N(t.Ready)} · em holding {Fmt.N(t.OnHold)}) + pendentes {Fmt.N(_pendingAll)}";
            _tip.SetToolTip(_summary, "O total não depende dos filtros: soma os Limiteds de todas as contas lidas (origens inclusas, mesmo em holding ou em etapa da Distribuir; sem Smalls) " +
                                      "e os Robux pendentes de todas as contas. Líquido = depois da taxa; à venda pelo seu preço, o resto pela sugestão (≈). " +
                                      "Vendas já feitas não contam duas vezes (estão nos pendentes).");
        }

        var shown = visible.Where(r => InFilter(r, _filter) && (_filter != 0 || r.Situation != SellSituation.Sold)).ToList(); // sales have their own chip
        var lines = new List<Line>();
        if (_mode.SelectedIndex == 0)
        {
            foreach (var grp in shown.GroupBy(r => r.AccountId)
                         .OrderByDescending(g => g.Count(r => r.Situation == SellSituation.Outbid))
                         .ThenBy(g => accounts[g.Key].Source).ThenBy(g => accounts[g.Key].Title, StringComparer.CurrentCultureIgnoreCase))
            {
                lines.Add(new Line(accounts[grp.Key], null, null, null));
                lines.AddRange(grp.OrderBy(r => Order(r.Situation)).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new Line(accounts[grp.Key], null, null, r)));
            }
            // Only an account whose Limiteds could not be read shows without rows (with the reason); one with only hidden
            // Smalls stays out, as before.
            if (q.Length == 0 && _filter == 0)
                foreach (var a in _board.Accounts.Where(a => a.Problem is not null && lines.All(l => l.Account?.UserId != a.UserId)
                                                            && (group is null || a.Group == group)))
                    lines.Add(new Line(a, null, null, null));
        }
        else
        {
            foreach (var grp in shown.GroupBy(r => r.AssetId).OrderByDescending(g => g.Count(r => r.Situation == SellSituation.Outbid)).ThenBy(g => g.First().Name, StringComparer.CurrentCultureIgnoreCase))
            {
                lines.Add(new Line(null, grp.Key, grp.First().Name, null));
                lines.AddRange(grp.OrderBy(r => Order(r.Situation)).ThenBy(r => r.Username, StringComparer.CurrentCultureIgnoreCase).Select(r => new Line(accounts[r.AccountId], grp.Key, null, r)));
            }
        }

        int rowsShown = lines.Count(l => !l.Header);
        _count.Text = $"Mostrando {rowsShown} de {visible.Count}" + (_smalls.On ? "" : " · Smalls ocultos") +
                      (_hiddenAccounts > 0 ? $" · {Fmt.Count(_hiddenAccounts, "conta", "contas")} sem Limiteds para vender" : "") +
                      (_note is null ? "" : " · " + _note);
        _empty.Text = _board.Accounts.Count == 0 ? "Nenhum Limited para vender. As leituras incluem todas as contas com sessão." : "Nada corresponde aos filtros.";
        _empty.Visible = lines.Count == 0;
        _list.Visible = lines.Count > 0;

        var missing = lines.Where(l => l.Row is { } r && ThumbOf(r.AssetId) is null).Select(l => (l.Row!.AssetId, l.Row.Name)).DistinctBy(t => t.AssetId).ToList();
        if (missing.Count > 0) NeedThumbs?.Invoke(missing);

        if (lines.SequenceEqual(_lines)) { ShowDetail(); return; }
        string? keep = _selectedKey;
        int first = _list.FirstDisplayedScrollingRowIndex;
        _updating = true;
        try
        {
            _lines = [.. lines];
            _list.Rows.Clear();
            foreach (var l in _lines)
            {
                int i = _list.Rows.Add("", "", "", "", "", "", "");
                _list.Rows[i].Height = LogicalToDeviceUnits(l.Header ? 34 : 50);
                if (l.Header && l.Account is { } acc)
                    foreach (DataGridViewCell cell in _list.Rows[i].Cells) cell.ToolTipText = AccountTip(acc);
            }
            int index = Array.FindIndex(_lines, l => l.Key == keep);
            if (index < 0) index = Array.FindIndex(_lines, l => !l.Header);
            if (index >= 0) _list.CurrentCell = _list.Rows[index].Cells[0];
            if (first >= 0 && first < _lines.Length) _list.FirstDisplayedScrollingRowIndex = first;
        }
        finally { _updating = false; }
        OnCurrentChanged();
    }

    /// <summary>What the header's "Limiteds" and "pode receber" mean, for a trade made by hand.</summary>
    private static string AccountTip(SellAccount a)
    {
        var tip = "Limiteds: líquido dos Limiteds na conta agora, em holding inclusos (sem Smalls).";
        if (a.Source) return tip;
        return tip + "\nPode receber: cota de 30 dias (ou a meta da conta) menos saldo, pendentes, Limiteds e trades em andamento — o mesmo espaço da Distribuir."
            + (a.Space is null && a.SpaceBlock is { } why ? "\nNão pode receber agora: " + why + "." : "");
    }

    private SellRow? RowAt(int index) => index >= 0 && index < _lines.Length ? _lines[index].Row : null;

    private void OnCurrentChanged()
    {
        if (_updating) return;
        int i = _list.CurrentRow?.Index ?? -1;
        if (i >= 0 && i < _lines.Length && _lines[i].Header && i + 1 < _lines.Length)
        {
            _list.CurrentCell = _list.Rows[i + 1].Cells[0]; // group titles are not selectable
            return;
        }
        _selectedKey = i >= 0 && i < _lines.Length ? _lines[i].Key : null;
        _list.Invalidate();
        ShowDetail();
    }

    private void SetHover(int row)
    {
        if (_hover == row) return;
        int old = _hover;
        _hover = row;
        if (old >= 0 && old < _list.RowCount) _list.InvalidateRow(old);
        if (row >= 0 && row < _list.RowCount) _list.InvalidateRow(row);
    }

    // ───────────── drawing ─────────────

    private static (string Word, Color Dot, Color Ink) Look(SellRow r) => r.Situation switch
    {
        SellSituation.Outbid => ("Superada" + (r.Position is { } p ? $" · {p}º" : ""), Theme.Expired, Theme.BadInk),
        SellSituation.Lowest => ("À venda · " + (r.Position is { } p ? $"{p}º" : "mais barata"), Theme.Online, Theme.OnlineInk),
        SellSituation.OnSale => ("À venda", Theme.Online, Theme.OnlineInk),
        SellSituation.Ready => ("Pronta para vender", Theme.Valid, Theme.OkInk),
        SellSituation.Unread => ("Livre · não lido", Theme.Valid, Theme.Text2),
        SellSituation.OnHold => ("Em holding", Theme.Warning, Theme.WarnInk),
        SellSituation.NoPlus => ("Sem Plus", Theme.Unknown, Theme.TextDim),
        _ => (r.Sale?.Pending == true ? "Vendida · pendente" : "Vendida", Theme.Unknown, Theme.Text2),
    };

    private static string? ActionText(SellRow r) => r.Situation switch
    {
        SellSituation.Outbid or SellSituation.Lowest or SellSituation.OnSale => "Mudar preço",
        SellSituation.Ready or SellSituation.Unread => "Vender",
        _ => null,
    };

    private void PaintGroupRow(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _lines.Length || !_lines[e.RowIndex].Header) return;
        var g = e.Graphics;
        var r = e.RowBounds;
        float s = DeviceDpi / 96f;
        using (var b = new SolidBrush(Theme.Bg2)) g.FillRectangle(b, r);
        using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        var line = _lines[e.RowIndex];
        int x = r.X + (int)(14 * s);
        const TextFormatFlags One = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        void Text(string t, Font f, Color c)
        {
            if (x >= r.Right) return;
            var size = TextRenderer.MeasureText(g, t, f, Size.Empty, One);
            TextRenderer.DrawText(g, t, f, new Rectangle(x, r.Y, Math.Min(size.Width, r.Right - x), r.Height), c, One | TextFormatFlags.EndEllipsis);
            x += size.Width;
        }
        if (line.ItemId is { } item && line.Account is null)
        {
            int th = (int)(24 * s);
            DrawThumb(g, item, line.ItemTitle ?? "", new Rectangle(x, r.Y + (r.Height - th) / 2, th, th), 6 * s);
            x += th + (int)(10 * s);
            var copies = _board.Rows.Where(rw => rw.AssetId == item && rw.Situation != SellSituation.Sold).ToList();
            Text(line.ItemTitle ?? "", Theme.Bold, Theme.Text);
            Text($"   {Fmt.Count(copies.Count, "cópia", "cópias")} em {Fmt.Count(copies.Select(c => c.AccountId).Distinct().Count(), "conta", "contas")}", Theme.Small, Theme.Text2);
            var low = _listings.GetValueOrDefault(item)?.Lowest.Where(l => !_mine.ContainsKey(l.SellerId)).Select(l => (long?)l.Price).FirstOrDefault();
            if (low is { } lp) Text($" · menor de outro {Fmt.N(lp)}", Theme.Small, Theme.Text2);
            if (_market.GetValueOrDefault(item)?.Rap is { } rap) Text($" · RAP {Fmt.N(rap)}", Theme.Small, Theme.TextDim);
        }
        else if (line.Account is { } a)
        {
            Text(a.Title, Theme.Bold, Theme.Text);
            Text("  @" + a.Username, Theme.Small, Theme.TextDim);
            if (a.Source) Text("  · origem", Theme.Small, Theme.TextDim);
            // What the account holds (net, Smalls aside) and how much more it can receive — for a trade by hand.
            long held = _board.Rows.Where(rw => rw.AccountId == a.UserId && !rw.Small && rw.Situation != SellSituation.Sold).Sum(rw => rw.Net ?? 0);
            Text("  ·  Limiteds ≈ " + Fmt.N(held), Theme.Small, Theme.Text);
            if (!a.Source)
                Text(a.Space is { } room ? "  ·  pode receber ≈ " + Fmt.N(room) : "  ·  não pode receber", Theme.Small, a.Space is > 0 ? Theme.Valid : Theme.WarnInk);
            string quota = a.RemainingMonthly is { } rem ? $"cota 30 dias {Fmt.N(rem)}" + (a.MonthlyCap is { } cap ? $" de {Fmt.N(cap)}" : "") : "cota não lida";
            if (a.RemainingMonthly is <= 0 && a.NextRelease is { } next) quota += $" · libera {Fmt.N(next.Amount)} em {Fmt.ShortDay(next.AtUtc)}";
            Text("  ·  " + quota, Theme.Small, a.RemainingMonthly is <= 0 ? Theme.WarnInk : Theme.Text2);
            Text(a.CanSell ? "  ·  Plus ativo" : "  ·  sem Plus", Theme.Small, a.CanSell ? Theme.TextDim : Theme.WarnInk);
            if (a.Problem is { } problem) Text("  ·  " + problem, Theme.Small, Theme.WarnInk);
        }
        e.Handled = true;
    }

    private void DrawThumb(Graphics g, long assetId, string name, Rectangle box, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(box, radius);
        using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
        if (ThumbOf(assetId) is { } img)
        {
            var clip = g.Clip;
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int pad = Math.Max(1, box.Width / 14);
            g.DrawImage(img, new Rectangle(box.X + pad, box.Y + pad, box.Width - 2 * pad, box.Height - 2 * pad));
            g.Clip = clip;
        }
        else TextRenderer.DrawText(g, name.Length > 0 ? name[..1].ToUpperInvariant() : "?", Theme.Header, box, Theme.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using var edge = new Pen(Theme.Line);
        g.DrawPath(edge, path);
    }

    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var g = e.Graphics!;
        var r = e.CellBounds;
        float s = DeviceDpi / 96f;
        int pad = (int)(12 * s);
        const TextFormatFlags One = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        if (e.RowIndex < 0)
        {
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r);
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            TextRenderer.DrawText(g, e.Value as string, Theme.Header, new Rectangle(r.X + pad, r.Y, Math.Max(1, r.Width - pad), r.Height), Theme.TextDim, One);
            e.Handled = true;
            return;
        }
        if (RowAt(e.RowIndex) is not { } row) { e.Handled = true; return; }
        bool selected = _list.CurrentRow?.Index == e.RowIndex;
        using (var b = new SolidBrush(selected ? Theme.Selected : e.RowIndex == _hover ? Theme.Hover : Theme.Card)) g.FillRectangle(b, r);
        using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        var text = new Rectangle(r.X + pad, r.Y, Math.Max(1, r.Width - pad - (int)(6 * s)), r.Height);
        switch (e.ColumnIndex)
        {
            case 0:
            {
                int th = (int)(34 * s);
                DrawThumb(g, row.AssetId, row.Name, new Rectangle(text.X, r.Y + (r.Height - th) / 2, th, th), 8 * s);
                int tx = text.X + th + (int)(10 * s), tw = Math.Max(1, text.Right - tx);
                int h1 = TextRenderer.MeasureText("Ag", Theme.Bold).Height, h2 = TextRenderer.MeasureText("Ag", Theme.Small).Height;
                int top = r.Y + (r.Height - h1 - h2) / 2;
                TextRenderer.DrawText(g, row.Name, Theme.Bold, new Rectangle(tx, top, tw, h1), Theme.Text, One & ~TextFormatFlags.VerticalCenter);
                var sub = new List<string>();
                if (row.Serial is { } serial) sub.Add("#" + serial);
                if (_mode.SelectedIndex == 1) sub.Add("@" + row.Username);
                if (row.Small) sub.Add("Small");
                if (row.Note is { } note) sub.Add(note);
                TextRenderer.DrawText(g, string.Join(" · ", sub), Theme.Small, new Rectangle(tx, top + h1, tw, h2), Theme.TextDim, One & ~TextFormatFlags.VerticalCenter);
                break;
            }
            case 1:
            {
                var (word, dot, ink) = Look(row);
                Theme.Dot(g, text.X + 3.5f * s, r.Y + r.Height / 2f, 7 * s, dot);
                TextRenderer.DrawText(g, word, Theme.Base, new Rectangle(text.X + (int)(14 * s), r.Y, Math.Max(1, text.Width - (int)(14 * s)), r.Height), ink, One);
                break;
            }
            case 2:
                TextRenderer.DrawText(g, row.MyPrice is { } mp ? Fmt.N(mp) : row.Sale is { } sale ? Fmt.N(sale.Amount) : "—", Theme.Base, text,
                    row.MyPrice is null && row.Sale is null ? Theme.TextDim : Theme.Text, One);
                break;
            case 3:
                string low = row.LowestOther is { } lo ? Fmt.N(lo) + (row.LowestOtherAccount is { } acc ? $" (sua @{acc})" : "") : "—";
                TextRenderer.DrawText(g, low, Theme.Base, text, row.Situation == SellSituation.Outbid ? Theme.BadInk : row.LowestOther is null ? Theme.TextDim : Theme.Text, One);
                break;
            case 4:
                string rap = row.Rap is { } rp ? Fmt.N(rp) : "—";
                if (row.SalesPerDay is { } d) rap += $" · {d.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))}/dia";
                else if (_market.GetValueOrDefault(row.AssetId)?.Demand is { } dm and >= 0) rap += $" · demanda {dm}/4"; // Rolimons, when Roblox gave no sales count
                TextRenderer.DrawText(g, rap, Theme.Base, text, Theme.TextDim, One);
                break;
            case 5:
                TextRenderer.DrawText(g, row.Net is { } net ? (row.NetEstimated && row.Situation != SellSituation.Sold ? "≈ " : "") + Fmt.N(net) : "—", Theme.Base, text,
                    row.NetEstimated ? Theme.TextDim : Theme.Text, One);
                break;
            default:
                if (ActionText(row) is { } action)
                    TextRenderer.DrawText(g, action, Theme.Header, new Rectangle(text.X, r.Y, text.Width - (int)(6 * s), r.Height), Theme.Text2, One | TextFormatFlags.Right);
                break;
        }
        e.Handled = true;
    }

    private void PaintRail(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (_list.CurrentRow?.Index != e.RowIndex || RowAt(e.RowIndex) is null) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rb = e.RowBounds;
        using var path = Theme.Round(new RectangleF(rb.X, rb.Y + 8 * s, 3 * s, rb.Height - 16 * s), 1.5f * s);
        using var b = new SolidBrush(Theme.Inv);
        e.Graphics.FillPath(b, path);
    }

    // ───────────── detail panel ─────────────

    private string? _detailFor;

    private void ShowDetail()
    {
        var row = _lines.FirstOrDefault(l => l.Key == _selectedKey)?.Row;
        if (row is null) { _detail.Visible = false; _detailFor = null; return; }
        string sig = $"{_selectedKey}|{row}|{_listings.GetValueOrDefault(row.AssetId)?.ReadUtc}";
        if (sig == _detailFor && _detail.Visible) return;
        _detailFor = sig;
        _detail.Width = Math.Min(LogicalToDeviceUnits(420), Math.Max(LogicalToDeviceUnits(360), ClientSize.Width / 3));
        _detail.Visible = true;
        _detailBody.SuspendLayout();
        foreach (Control c in _detailBody.Controls) c.Dispose();
        _detailBody.Controls.Clear();
        int w = _detail.Width - LogicalToDeviceUnits(52);
        var account = _board.Accounts.First(a => a.UserId == row.AccountId);
        Label L(string t, Font f, Color c, int top = 0) => new() { Text = t, AutoSize = true, MaximumSize = new Size(w, 0), Font = f, ForeColor = c, Margin = new Padding(0, top, 0, 2) };

        // Picture and title
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Sidebar, Margin = new Padding(0, 6, 0, 6) };
        head.Controls.Add(new ItemPicture(this, row.AssetId, row.Name) { Size = new Size(LogicalToDeviceUnits(84), LogicalToDeviceUnits(84)), Margin = new Padding(0, 0, 12, 0) });
        var names = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };
        int nw = w - LogicalToDeviceUnits(96);
        names.Controls.Add(new Label { Text = row.Label, AutoSize = true, MaximumSize = new Size(nw, 0), Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 2) });
        names.Controls.Add(new Label { Text = $"@{row.Username}" + (row.AccountTitle != row.Username ? $" ({row.AccountTitle})" : ""), AutoSize = true, MaximumSize = new Size(nw, 0), Font = Theme.Small, ForeColor = Theme.TextDim });
        var status = new StatusText { Font = Theme.Bold, Margin = new Padding(0, 6, 0, 0) };
        var (word, dot, ink) = Look(row);
        status.Set(word, dot, ink);
        names.Controls.Add(status);
        head.Controls.Add(names);
        _detailBody.Controls.Add(head);

        if (row.InPlan is { } step)
            _detailBody.Controls.Add(new NoteLabel { Text = $"Esta cópia está na etapa {step} da Distribuir. Vender tira ela do plano (as sugestões são refeitas).", Kind = NoteKind.Warn, MaximumSize = new Size(w, 0), Margin = new Padding(0, 4, 0, 6) });
        if (row.Situation != SellSituation.Sold && account.RemainingMonthly is <= 0)
            _detailBody.Controls.Add(new NoteLabel
            {
                Text = "Sem cota de 30 dias agora: o Robux da venda fica parado na conta" + (account.NextRelease is { } nr ? $" até {Fmt.ShortDay(nr.AtUtc)} (libera {Fmt.N(nr.Amount)})." : "."),
                Kind = NoteKind.Warn, MaximumSize = new Size(w, 0), Margin = new Padding(0, 4, 0, 6),
            });
        if (row.Situation == SellSituation.NoPlus)
            _detailBody.Controls.Add(new NoteLabel { Text = "O Roblox só deixa vender Limiteds com Plus ativo.", Kind = NoteKind.Info, MaximumSize = new Size(w, 0), Margin = new Padding(0, 4, 0, 6) });
        if (row.Situation == SellSituation.OnHold) _detailBody.Controls.Add(HoldCard(row, w, L));

        if (row.Sale is { } sale)
        {
            var sc = Card(w);
            sc.Controls.Add(L("Venda", Theme.Header, Theme.Text));
            sc.Controls.Add(Pair("Creditado", Fmt.N(sale.Amount) + (sale.Pending ? " (pendente)" : ""), w));
            sc.Controls.Add(Pair("Quando", Fmt.Time(sale.CreatedUtc), w));
            _detailBody.Controls.Add(sc);
        }
        else
        {
            // Cheapest listings now, with yours marked
            var pc = Card(w);
            pc.Controls.Add(L("Menores preços agora", Theme.Header, Theme.Text));
            if (_listings.GetValueOrDefault(row.AssetId) is { } item)
            {
                int i = 0;
                foreach (var l in item.Lowest.Take(5))
                {
                    i++;
                    bool you = l.SellerId == row.AccountId && (l.UserAssetId is null || l.UserAssetId == row.UserAssetId || row.MyPrice == l.Price);
                    string who = you ? $"você (@{row.Username})" : _mine.TryGetValue(l.SellerId, out var mineName) ? $"sua conta @{mineName}" : "outro vendedor";
                    pc.Controls.Add(Pair($"{i}º · {who}", Fmt.N(l.Price), w, you ? Theme.OnlineInk : Theme.Text2));
                }
                if (item.Lowest.Count == 0) pc.Controls.Add(L("Ninguém está vendendo agora.", Theme.Small, Theme.TextDim));
                pc.Controls.Add(L($"lidos em {Fmt.Time(item.ReadUtc)}", Theme.Small, Theme.Dim, 4));
            }
            else pc.Controls.Add(L("Ainda não lidos. Use \"Reler\" ou \"Atualizar leituras\".", Theme.Small, Theme.TextDim));

            if (row.Suggestion is { } sug)
            {
                var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Theme.Surface, Padding = new Padding(10, 8, 8, 8), Margin = new Padding(0, 8, 0, 0), Width = w - LogicalToDeviceUnits(28) };
                box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                box.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var texts = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
                texts.Controls.Add(new Label { Text = $"Sugestão: {Fmt.N(sug)}", AutoSize = true, Font = Theme.Bold, ForeColor = Theme.Text, Margin = Padding.Empty });
                texts.Controls.Add(new Label { Text = $"{row.SuggestionWhy} · líquido {Fmt.N(Core.Limiteds.Valuation.Net(sug, _fee))}", AutoSize = true, MaximumSize = new Size(w - LogicalToDeviceUnits(130), 0), Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 0) });
                var copy = Theme.Button("Copiar", (_, _) => { try { Clipboard.SetText(sug.ToString()); } catch (Exception) { } });
                copy.Anchor = AnchorStyles.Right;
                _tip.SetToolTip(copy, "Copia o número para colar no Roblox.");
                box.Controls.Add(texts, 0, 0);
                box.Controls.Add(copy, 1, 0);
                pc.Controls.Add(box);
            }
            _detailBody.Controls.Add(pc);

            // Market and your other copies
            var mc = Card(w);
            mc.Controls.Add(L("Mercado", Theme.Header, Theme.Text));
            var m = _market.GetValueOrDefault(row.AssetId);
            mc.Controls.Add(Pair("RAP", row.Rap is { } rp ? Fmt.N(rp) : "—", w));
            mc.Controls.Add(Pair("Vendas", row.SalesPerDay is { } d ? $"{d.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))} por dia (30 dias, Roblox)" : "—", w));
            if (m?.Demand is { } demand and >= 0) mc.Controls.Add(Pair("Demanda (Rolimons)", $"{demand}/4", w));
            if (m?.RolimonsValue is { } rv) mc.Controls.Add(Pair("Valor Rolimons", Fmt.N(rv), w));
            var others = _board.Rows.Where(o => o.AssetId == row.AssetId && o.Situation != SellSituation.Sold && !(o.AccountId == row.AccountId && o.UserAssetId == row.UserAssetId)).ToList();
            mc.Controls.Add(Pair("Suas outras cópias", others.Count == 0 ? "nenhuma" : string.Join(", ", others.Take(4).Select(o => $"@{o.Username} ({Look(o).Word.ToLowerInvariant()})")) + (others.Count > 4 ? $" e mais {others.Count - 4}" : ""), w));
            _detailBody.Controls.Add(mc);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Sidebar, Margin = new Padding(0, 12, 0, 0) };
            string main = ActionText(row) switch { "Vender" => "Abrir para vender no Roblox", { } a => a + " no Roblox", _ => "Abrir no Roblox" };
            var open = Theme.Button(main, (_, _) => OpenRequested?.Invoke(row), primary: true);
            open.Margin = new Padding(0, 0, 8, 0);
            var reread = Theme.Button("Reler", (_, _) => RereadRequested?.Invoke(row), ButtonKind.Ghost);
            _tip.SetToolTip(reread, "Lê de novo esta conta e os menores preços deste item.");
            actions.Controls.AddRange([open, reread]);
            _detailBody.Controls.Add(actions);
            _detailBody.Controls.Add(L($"Abre a página do item na aba de @{row.Username}. Você ajusta e confirma no Roblox; depois use Reler.", Theme.Small, Theme.TextDim, 8));
        }
        _detailBody.ResumeLayout();
    }

    /// <summary>When the copy leaves the hold: 2 days after a trade, 7 after a store purchase (Brasília time).</summary>
    private CardStack HoldCard(SellRow row, int w, Func<string, Font, Color, int, Label> L)
    {
        var hc = Card(w);
        hc.Controls.Add(L("Saída do holding", Theme.Header, Theme.Text, 0));
        var now = DateTimeOffset.UtcNow;
        if (row.Hold is not { } h)
        {
            hc.Controls.Add(L("Data de chegada ainda desconhecida: o app lê as compras e as trades desta conta na próxima leitura. Use Reler para ler agora.", Theme.Small, Theme.TextDim, 4));
            return hc;
        }
        if (h.Kind is null)
        {
            // Only the app's sighting: both possible dates.
            hc.Controls.Add(L("Libera em " + When(h.EarliestUtc), Theme.Title, Theme.Text, 4));
            hc.Controls.Add(L($"se veio por trade; se foi comprada na loja, {When(h.LatestUtc)}", Theme.Small, Theme.TextDim, 0));
        }
        else
        {
            string when = !h.IsRange ? "Libera em " + When(h.LatestUtc)
                : Brt(h.EarliestUtc).Date == Brt(h.LatestUtc).Date ? $"Libera {Day(h.EarliestUtc)}, entre {Brt(h.EarliestUtc):HH:mm} e {Brt(h.LatestUtc):HH:mm}"
                : $"Libera entre {When(h.EarliestUtc)} e {When(h.LatestUtc)}";
            hc.Controls.Add(L(when, Theme.Title, Theme.Text, 4));
        }
        bool late = now > h.LatestUtc;
        hc.Controls.Add(L(late ? "já devia ter saído do holding"
                           : now >= h.EarliestUtc ? $"pode sair a qualquer momento (até {Brt(h.LatestUtc):HH:mm})"
                           : "faltam " + Span(h.EarliestUtc - now), Theme.Small, late ? Theme.WarnInk : Theme.TextDim, 2));
        string arrival = h.Kind switch
        {
            ArrivalKind.Purchase => $"comprada na loja em {Fmt.Time(h.ArrivedUtc!.Value)}",
            ArrivalKind.Trade when h.Exact => $"trade aceita no app em {Fmt.Time(h.ArrivedUtc!.Value)}",
            ArrivalKind.Trade => $"trade criada em {Fmt.Time(h.ArrivedUtc!.Value)}" + (h.Seen is { } sn && sn > h.ArrivedUtc ? $"; vista na conta em {Fmt.Time(sn)}" : ""),
            _ => $"vista na conta em {Fmt.Time(h.Seen!.Value)}; nenhuma trade ou compra encontrada",
        };
        hc.Controls.Add(Pair("Chegada", arrival, w));
        hc.Controls.Add(Pair("Prazo do Roblox", h.Kind switch
        {
            ArrivalKind.Purchase => "7 dias após a compra",
            ArrivalKind.Trade => "2 dias após a trade",
            _ => "2 dias (trade) ou 7 dias (compra)",
        }, w));
        if (h.TradeId is { } tid) hc.Controls.Add(Pair("Trade", tid.ToString(), w));
        if (h is { Kind: ArrivalKind.Trade, Exact: false })
            hc.Controls.Add(L("A trade não foi aceita no app: o Roblox não informa a hora em que ela foi concluída, só a da criação. Ela ficou entre a criação e o momento em que o app viu a cópia.", Theme.Small, Theme.TextDim, 6));
        if (late)
            hc.Controls.Add(new NoteLabel { Text = "Já passou da data prevista e o Roblox ainda mostra a cópia em holding. Use Reler para conferir.", Kind = NoteKind.Warn, MaximumSize = new Size(w - LogicalToDeviceUnits(28), 0), Margin = new Padding(0, 8, 0, 0) });
        // Inside the card's padding.
        foreach (var l in hc.Controls.OfType<Label>().Where(l => l is not NoteLabel)) l.MaximumSize = new Size(w - LogicalToDeviceUnits(30), 0);
        return hc;
    }

    private static DateTimeOffset Brt(DateTimeOffset utc) => Core.Finance.BrasiliaTime.ToBrasilia(utc);
    private static readonly System.Globalization.CultureInfo Br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
    /// <summary>"qua, 26/11".</summary>
    private static string Day(DateTimeOffset utc) => Brt(utc).ToString("ddd", Br).TrimEnd('.') + ", " + Brt(utc).ToString("dd/MM", Br);
    /// <summary>"qua, 26/11 às 14:05".</summary>
    private static string When(DateTimeOffset utc) => $"{Day(utc)} às {Brt(utc):HH:mm}";
    /// <summary>"1 dia e 5 h", "5 h e 20 min", "20 min".</summary>
    private static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} {((int)t.TotalDays == 1 ? "dia" : "dias")}" + (t.Hours > 0 ? $" e {t.Hours} h" : "")
        : t.TotalHours >= 1 ? $"{t.Hours} h" + (t.Minutes > 0 ? $" e {t.Minutes} min" : "")
        : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";

    private CardStack Card(int w) => new() { MinimumSize = new Size(w, 0), MaximumSize = new Size(w, 0), Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 10, 0, 0) };

    private KeyValue Pair(string key, string value, int w, Color? ink = null) =>
        new(key, value, ink ?? Theme.Text) { Width = w - LogicalToDeviceUnits(30), Margin = new Padding(0, 2, 0, 2) };

    /// <summary>"Chave ........ valor": the key on the left, the value aligned right (wraps when long).</summary>
    private sealed class KeyValue : Control
    {
        private readonly string _key, _value;
        private readonly Color _ink;
        private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

        public KeyValue(string key, string value, Color ink)
        {
            _key = key; _value = value; _ink = ink;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = key + ": " + value;
        }

        private const TextFormatFlags KeyFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        // The key stays on one line; the value takes the rest (and wraps).
        private int KeyWidth => Math.Min(Width * 3 / 5, TextRenderer.MeasureText(_key, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + LogicalToDeviceUnits(16));

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int h = Math.Max(TextRenderer.MeasureText(_key, Theme.Small, Size.Empty, KeyFlags).Height,
                             TextRenderer.MeasureText(_value, Theme.Small, new Size(Math.Max(1, Width - KeyWidth), 0), Flags).Height);
            if (Height != h) Height = h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.BackOf(Parent));
            int kw = KeyWidth;
            TextRenderer.DrawText(e.Graphics, _key, Theme.Small, new Rectangle(0, 0, kw, Height), Theme.TextDim, KeyFlags);
            TextRenderer.DrawText(e.Graphics, _value, Theme.Small, new Rectangle(kw, 0, Math.Max(1, Width - kw), Height), _ink, Flags | TextFormatFlags.Right);
        }
    }

    /// <summary>The item's picture in the detail panel (repaints when thumbnails arrive).</summary>
    private sealed class ItemPicture(LimitedsView owner, long assetId, string name) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Sidebar);
            owner.DrawThumb(e.Graphics, assetId, name, new Rectangle(0, 0, Width - 1, Height - 1), 12 * DeviceDpi / 96f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
