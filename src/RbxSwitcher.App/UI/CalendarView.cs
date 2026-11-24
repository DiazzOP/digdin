using System.Drawing.Drawing2D;
using System.Globalization;
using RbxSwitcher.Core.Limiteds;

namespace RbxSwitcher.App.UI;

/// <summary>
/// The "Calendário" category: when the Robux of the accounts' Limited sales should fall. Four figures on top (pending,
/// today, next 7 days, next drop), one line on how the dates are estimated, the month grid with its legend, and on the
/// right — as the Limiteds tab's detail panel — the chosen day's sales and every account's pending Robux. Choosing an
/// account (its row, or "Conta") shows only its sales. Read-only.
/// </summary>
internal sealed class CalendarView : Panel
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private const string AllAccounts = "Todas as contas";

    private readonly StatStrip _stats = new() { Dock = DockStyle.Top };
    private readonly Label _basis = new() { AutoSize = true, Font = Theme.Fine, ForeColor = Theme.TextDim, Margin = new Padding(0, 7, 8, 0) };
    private readonly UiButton _how;
    private readonly NoteLabel _howNote = new() { Kind = NoteKind.Info, Visible = false, Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _monthTitle = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(4, 6, 4, 0) };
    private readonly Label _monthTotal = new() { AutoSize = true, Font = Theme.Fine, ForeColor = Theme.TextDim, Margin = new Padding(10, 8, 0, 0) };
    private readonly UiButton _refresh, _prev, _next, _today;
    private readonly DropdownButton _account = new("Conta");
    private readonly MonthGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly CalendarLegend _legend = new() { Dock = DockStyle.Bottom };
    private readonly FlowLayoutPanel _detail = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Sidebar, Padding = new Padding(16, 12, 10, 12) };
    private readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 20_000 };
    private RobuxCalendarView? _view;
    private bool _busy, _neverRead, _howOpen;
    private DateOnly _month, _selected;
    private bool _chosen; // the person picked a day: keep it when the data refreshes
    private long? _filter; // only this account's sales
    private string _detailSig = "";

    public event Action? RefreshRequested;
    /// <summary>Item pictures by asset id (shared cache); <see cref="NeedThumbs"/> asks for the missing ones.</summary>
    public Func<long, Image?> ThumbOf { get; set; } = _ => null;
    public event Action<IReadOnlyList<(long AssetId, string Name)>>? NeedThumbs;

    public CalendarView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;
        Theme.DarkScroll(_detail);

        _refresh = Theme.Button("Atualizar vendas", (_, _) => RefreshRequested?.Invoke());
        _tip.SetToolTip(_refresh, "Lê de novo as vendas dos últimos 31 dias de cada conta com Limiteds ou Robux pendentes (uma por vez).");
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        titles.Controls.AddRange([
            new Label { Text = "Calendário de Robux", AutoSize = true, Font = Theme.Headline, ForeColor = Theme.Text, Margin = Padding.Empty },
            new Label { Text = "Quando os Robux pendentes das vendas de Limiteds devem cair em cada conta", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 2, 0, 0) },
        ]);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 10) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(titles, 0, 0);
        heading.Controls.Add(_refresh, 1, 0);

        _stats.DayClicked += Choose;

        // How the dates are estimated: one line; the whole explanation one click away.
        _how = new UiButton { Text = "Como é estimado", Kind = ButtonKind.Ghost, Icon = UiIcon.ChevronDown, IconAfterText = true, Margin = Padding.Empty };
        _how.Click += (_, _) =>
        {
            _howOpen = !_howOpen;
            _howNote.Visible = _howOpen;
            _how.Icon = _howOpen ? UiIcon.ChevronUp : UiIcon.ChevronDown;
            _how.AccessibleName = _howOpen ? "Esconder como as datas são estimadas" : "Mostrar como as datas são estimadas";
            _how.Invalidate();
        };
        var basisRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
        basisRow.Controls.AddRange([_basis, _how]);
        var howWrap = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        howWrap.Controls.Add(_howNote);

        _prev = Theme.Named(new UiButton { Icon = UiIcon.Back, Kind = ButtonKind.Ghost, Margin = Padding.Empty }, "Mês anterior (Page Up)", _tip);
        _next = Theme.Named(new UiButton { Icon = UiIcon.Forward, Kind = ButtonKind.Ghost, Margin = Padding.Empty }, "Próximo mês (Page Down)", _tip);
        _today = Theme.Button("Hoje", (_, _) => Choose(_view?.Today ?? _selected), ButtonKind.Ghost);
        _tip.SetToolTip(_today, "Voltar para hoje (Home)");
        _today.Margin = new Padding(6, 0, 0, 0);
        _prev.Click += (_, _) => ShowMonth(_month.AddMonths(-1));
        _next.Click += (_, _) => ShowMonth(_month.AddMonths(1));
        _account.Margin = Padding.Empty;
        _account.SetItems([AllAccounts]);
        _account.SelectionChanged += () =>
        {
            long? id = _view?.Accounts.FirstOrDefault(a => "@" + a.AccountName == _account.Selected)?.AccountId;
            if (id != _filter) { _filter = id; Render(); }
        };
        _tip.SetToolTip(_account, "Mostrar só as vendas de uma conta (ou clique na conta em \"Por conta\")");
        // Wraps when narrow (high scaling): the month's total goes under the arrows instead of under "Conta".
        var monthLeft = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        monthLeft.Controls.AddRange([_prev, _monthTitle, _next, _today, _monthTotal]);
        var monthBar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 8) };
        monthBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        monthBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monthBar.Controls.Add(monthLeft, 0, 0);
        monthBar.Controls.Add(_account, 1, 0);
        _grid.DayChosen += Choose;

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Margin = new Padding(0, 0, 16, 0) };
        left.Controls.Add(_grid);
        left.Controls.Add(_legend);
        left.Controls.Add(monthBar);
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        right.Controls.Add(_detail);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Window, Padding = new Padding(0, 6, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);

        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 12, 8), BackColor = Theme.Window };
        page.Controls.AddRange([body, howWrap, basisRow, _stats, heading]);
        Controls.Add(page);
        _detail.Resize += (_, _) => FitWidths();
        Resize += (_, _) => _howNote.MaximumSize = _basis.MaximumSize = new Size(Math.Max(200, Width - 220), 0);
    }

    /// <summary>New pictures arrived: repaint the sale rows (no rebuild, the scroll stays).</summary>
    public void ThumbsLoaded()
    {
        foreach (var row in _detail.Controls.OfType<SaleRow>()) row.Invalidate();
    }

    /// <param name="neverRead">No account's sales were read yet (first use).</param>
    public void SetState(RobuxCalendarView view, bool busy, bool neverRead)
    {
        _view = view;
        _busy = busy;
        _neverRead = neverRead;
        _refresh.Enabled = !busy;
        _refresh.Text = busy ? "Lendo…" : "Atualizar vendas";
        _account.SetItems([AllAccounts, .. view.Accounts.Select(a => "@" + a.AccountName)]);
        if (_filter is { } f && view.Accounts.All(a => a.AccountId != f)) { _filter = null; _account.Selected = AllAccounts; }
        var h = view.Overall;
        _basis.Text = "Datas estimadas: o Roblox não informa quando cada venda pendente cai. " +
                      (h.Basis == HoldBasis.Default ? $"Sem histórico ainda: ≈ {Days(h.Typical)} depois da venda." : $"Prazo ≈ {Days(h.Typical)} ({Fmt.Count(h.Samples, "venda vista", "vendas vistas")} caindo).") +
                      (view.OldestRead is { } read ? $" Vendas lidas até {Fmt.Time(read)}." : "") + (busy ? " Lendo vendas…" : "");
        _howNote.Text = HowNote(h);
        Render();
    }

    private static string HowNote(HoldEstimate h) =>
        "O Roblox deixa o valor de cada venda pendente por um tempo (diz só \"até 30 dias\") e não informa a data. O app lê as vendas a cada ~25 min " +
        "e anota cada venda que passa de pendente para creditada. O prazo de cada conta vem, nesta ordem: do histórico da própria conta; do que as " +
        "vendas lidas dela mostram (pendentes mais novas que as creditadas); do histórico de todas as contas; e, sem nada disso, 7 dias. " +
        (h.Basis == HoldBasis.Default ? "Ainda não há vendas vistas caindo." : $"Até agora: {Fmt.Count(h.Samples, "venda vista", "vendas vistas")} caindo, de {Range(h.Low ?? h.Typical, h.High)} depois da venda.") +
        " Uma venda que passou da estimativa fica em hoje, como atrasada, até cair.";

    /// <summary>"5 dias", "5,5 dias", "18 h".</summary>
    internal static string Days(TimeSpan t) => t.TotalDays < 1
        ? $"{Math.Max(1, (int)Math.Round(t.TotalHours))} h"
        : $"{t.TotalDays.ToString(t.TotalDays < 10 ? "0.#" : "0", Br)} {(Math.Round(t.TotalDays, 1) == 1 ? "dia" : "dias")}";

    /// <summary>"4,9–5,2 dias".</summary>
    internal static string Range(TimeSpan lo, TimeSpan hi)
    {
        string a = Days(lo), b = Days(hi);
        return a.EndsWith(" dias") && b.EndsWith(" dias") ? a[..^5] + "–" + b : a + "–" + b;
    }

    internal static string HoldText(HoldEstimate h, string account) => h.Basis switch
    {
        HoldBasis.AccountHistory => $"Prazo ≈ {Days(h.Typical)}: histórico de @{account} ({Fmt.Count(h.Samples, "venda vista", "vendas vistas")} caindo, {Range(h.Low ?? h.Typical, h.High)}).",
        HoldBasis.AccountSales => $"Prazo ≈ {Days(h.Typical)}: pelas vendas lidas de @{account} (mais de {Days(h.Low ?? TimeSpan.Zero)} e até {Days(h.High)}).",
        HoldBasis.AllHistory => $"Prazo ≈ {Days(h.Typical)}: histórico das contas ({Fmt.Count(h.Samples, "venda vista", "vendas vistas")} caindo).",
        _ => $"Prazo ≈ {Days(h.Typical)}: padrão até o app ver vendas caírem (o Roblox diz até 30 dias).",
    };

    private static string ShortHold(HoldEstimate h) => $"prazo ≈ {Days(h.Typical)}";

    private void ShowMonth(DateOnly month)
    {
        _month = new DateOnly(month.Year, month.Month, 1);
        if (_selected.Year != _month.Year || _selected.Month != _month.Month) _selected = _month;
        _chosen = true;
        Render();
    }

    private void Choose(DateOnly day)
    {
        _selected = day;
        _chosen = true;
        if (day.Year != _month.Year || day.Month != _month.Month) _month = new DateOnly(day.Year, day.Month, 1);
        Render();
    }

    private void ToggleAccount(long id)
    {
        _filter = _filter == id ? null : id;
        _account.Selected = _filter is { } f && _view?.Accounts.FirstOrDefault(a => a.AccountId == f) is { } a ? "@" + a.AccountName : AllAccounts;
        Render();
    }

    /// <summary>Every sale on the calendar, of the chosen account only when one is chosen.</summary>
    private List<CalendarEntry> Entries(RobuxCalendarView v) =>
        v.Days.Values.SelectMany(d => d.Entries).Where(e => _filter is null || e.AccountId == _filter).ToList();

    private void Render()
    {
        if (_view is not { } v) return;
        var entries = Entries(v);
        var due = entries.Where(e => !e.Fell).ToList();
        if (!_chosen)
        {
            // Today, or the next day something should fall.
            _selected = due.Any(e => RobuxCalendar.Day(e.ExpectedUtc) == v.Today) ? v.Today
                : due.Select(e => RobuxCalendar.Day(e.ExpectedUtc)).Where(d => d > v.Today).DefaultIfEmpty(v.Today).Min();
            _month = new DateOnly(_selected.Year, _selected.Month, 1);
        }

        // The four figures.
        var today = due.Where(e => RobuxCalendar.Day(e.ExpectedUtc) == v.Today).ToList();
        var week = due.Where(e => RobuxCalendar.Day(e.ExpectedUtc) is var d && d >= v.Today && d <= v.Today.AddDays(6)).ToList();
        var upcoming = due.Where(e => !e.Late && RobuxCalendar.Day(e.ExpectedUtc) > v.Today).OrderBy(e => e.ExpectedUtc).ToList();
        int lateCount = today.Count(e => e.Late);
        int accounts = due.Select(e => e.AccountId).Distinct().Count();
        StatStrip.Tile nextTile;
        if (upcoming.FirstOrDefault() is { } n)
        {
            var nd = RobuxCalendar.Day(n.ExpectedUtc);
            var sameDay = upcoming.Where(e => RobuxCalendar.Day(e.ExpectedUtc) == nd).ToList();
            nextTile = new("Próxima queda", WeekDay(nd),
                $"≈ {Fmt.N(sameDay.Sum(e => e.Amount))} · " + (sameDay.Select(e => e.AccountId).Distinct().Count() == 1 ? "@" + sameDay[0].AccountName : Fmt.Count(sameDay.Select(e => e.AccountId).Distinct().Count(), "conta", "contas")),
                null, nd);
        }
        else nextTile = new("Próxima queda", "—", "nada depois de hoje", null, null);
        _stats.Set([
            new("Pendentes de vendas", _neverRead ? "—" : Fmt.N(due.Sum(e => e.Amount)),
                _neverRead ? (_busy ? "lendo as vendas…" : "vendas ainda não lidas") : due.Count == 0 ? "nenhuma venda pendente" : $"{Fmt.Count(due.Count, "venda", "vendas")} · {Fmt.Count(accounts, "conta", "contas")}", null, null),
            new("Hoje", today.Count == 0 ? "—" : "≈ " + Fmt.N(today.Sum(e => e.Amount)),
                lateCount > 0 ? Fmt.Count(lateCount, "atrasada", "atrasadas") : today.Count == 0 ? "nada previsto" : Fmt.Count(today.Count, "venda", "vendas"),
                lateCount > 0 ? Theme.Warning : null, today.Count > 0 ? v.Today : null),
            new("Próximos 7 dias", week.Count == 0 ? "—" : "≈ " + Fmt.N(week.Sum(e => e.Amount)), $"até {v.Today.AddDays(6):dd/MM}", null, null),
            nextTile,
        ]);

        _monthTitle.Text = Capital(_month.ToString("MMMM 'de' yyyy", Br));
        var inMonth = entries.Where(e => RobuxCalendar.Day(e.ExpectedUtc) is var d && d.Year == _month.Year && d.Month == _month.Month).ToList();
        long monthDue = inMonth.Where(e => !e.Fell).Sum(e => e.Amount), monthFell = inMonth.Where(e => e.Fell).Sum(e => e.Amount);
        _monthTotal.Text = string.Join(" · ", new[] { monthDue > 0 ? $"≈ {Fmt.N(monthDue)} a cair" : null, monthFell > 0 ? $"{Fmt.N(monthFell)} caíram" : null }.OfType<string>());

        var cells = entries.GroupBy(e => RobuxCalendar.Day(e.ExpectedUtc)).ToDictionary(g => g.Key, g =>
        {
            var exp = g.Where(e => !e.Fell).ToList();
            var fell = g.Where(e => e.Fell).ToList();
            return new DayCell(exp.Sum(e => e.Amount), exp.Count, fell.Sum(e => e.Amount), fell.Count, exp.Any(e => e.Late), DayTip(g.Key, exp, fell));
        });
        _grid.Set(_month, v.Today, _selected, cells);
        RenderDetail(v, entries);
    }

    private static string DayTip(DateOnly day, List<CalendarEntry> due, List<CalendarEntry> fell)
    {
        var lines = new List<string> { Capital(day.ToString("dddd, dd/MM", Br)) };
        if (due.Count > 0)
        {
            lines.Add($"Devem cair ≈ {Fmt.N(due.Sum(e => e.Amount))} ({Fmt.Count(due.Count, "venda", "vendas")}){(due.Any(e => e.Late) ? ", com atrasada" : "")}");
            lines.AddRange(due.GroupBy(e => e.AccountName).Take(6).Select(g => $"  @{g.Key}: {Fmt.N(g.Sum(e => e.Amount))}"));
        }
        if (fell.Count > 0) lines.Add($"Caíram {Fmt.N(fell.Sum(e => e.Amount))} ({Fmt.Count(fell.Count, "venda", "vendas")})");
        return string.Join("\n", lines);
    }

    private void RenderDetail(RobuxCalendarView v, List<CalendarEntry> entries)
    {
        var day = entries.Where(e => RobuxCalendar.Day(e.ExpectedUtc) == _selected).OrderBy(e => e.Fell).ThenBy(e => e.ExpectedUtc).ToList();
        string sig = string.Join("|", _selected, _filter, _busy, _neverRead,
            string.Join(",", day.Select(e => $"{e.SaleId}:{e.ExpectedUtc:O}:{e.Fell}:{e.Late}")),
            string.Join(",", v.Accounts.Select(a => $"{a.AccountId}:{a.FromSales}:{a.PendingTotal}:{a.Next?.SaleId}:{a.Next?.ExpectedUtc:O}:{a.Hold}:{a.Gap}")));
        if (sig == _detailSig) return; // nothing changed: keep the scroll position
        _detailSig = sig;

        _detail.SuspendLayout();
        foreach (Control c in _detail.Controls) c.Dispose();
        _detail.Controls.Clear();

        if (_neverRead)
            _detail.Controls.Add(new NoteLabel
            {
                Kind = NoteKind.Info, Margin = new Padding(0, 0, 0, 12),
                Text = _busy ? "Lendo as vendas das contas pela primeira vez. O calendário aparece conforme cada conta termina."
                             : "As vendas ainda não foram lidas. Use \"Atualizar vendas\" (alguns segundos por conta).",
            });

        _detail.Controls.Add(Title(Capital(_selected.ToString("dddd, dd/MM", Br)) + (_selected == v.Today ? " · hoje" : "")));
        var due = day.Where(e => !e.Fell).ToList();
        var fell = day.Where(e => e.Fell).ToList();
        if (due.Count > 0) _detail.Controls.Add(Line($"Devem cair ≈ {Fmt.N(due.Sum(e => e.Amount))} Robux · {Fmt.Count(due.Count, "venda", "vendas")}", Theme.Text, Theme.Bold));
        if (fell.Count > 0) _detail.Controls.Add(Line($"Caíram {Fmt.N(fell.Sum(e => e.Amount))} Robux · {Fmt.Count(fell.Count, "venda vista", "vendas vistas")} pelo app", Theme.Text2, Theme.Bold));
        if (day.Count == 0)
        {
            _detail.Controls.Add(Line(_selected < v.Today ? "Nenhuma queda vista neste dia." : "Nada previsto para este dia.", Theme.TextDim));
            if (entries.Where(e => !e.Fell && RobuxCalendar.Day(e.ExpectedUtc) > _selected).OrderBy(e => e.ExpectedUtc).FirstOrDefault() is { } next)
            {
                var nd = RobuxCalendar.Day(next.ExpectedUtc);
                var go = Theme.Button($"Ir para a próxima queda: {nd:dd/MM}", (_, _) => Choose(nd), ButtonKind.Ghost);
                go.Margin = new Padding(0, 4, 0, 0);
                _detail.Controls.Add(go);
            }
        }
        var missing = new List<(long, string)>();
        foreach (var e in day)
        {
            _detail.Controls.Add(new SaleRow(e, ThumbOf, _tip) { Margin = new Padding(0, 6, 0, 0) });
            if (ThumbOf(e.AssetId) is null) missing.Add((e.AssetId, e.Name));
        }
        if (missing.Count > 0) NeedThumbs?.Invoke(missing);

        _detail.Controls.Add(Title("Por conta", Theme.Header, Theme.Text2, new Padding(0, 18, 0, 2)));
        if (v.Accounts.Count == 0) _detail.Controls.Add(Line(_neverRead ? "As contas aparecem aqui depois da leitura das vendas." : "Nenhuma conta com Robux pendentes.", Theme.TextDim));
        else _detail.Controls.Add(Line(_filter is null ? "Clique numa conta para ver só as vendas dela." : "Mostrando só a conta marcada. Clique nela de novo para ver todas.", Theme.TextDim));
        foreach (var a in v.Accounts)
        {
            var row = new AccountRow(a, a.AccountId == _filter, _tip) { Margin = new Padding(0, 4, 0, 0) };
            row.Click += (_, _) => ToggleAccount(a.AccountId);
            _detail.Controls.Add(row);
        }
        FitWidths();
        _detail.ResumeLayout();
    }

    private static Label Title(string text, Font? font = null, Color? ink = null, Padding? margin = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Theme.Title, ForeColor = ink ?? Theme.Text, Margin = margin ?? new Padding(0, 0, 0, 4) };

    private static Label Line(string text, Color ink, Font? font = null) =>
        new() { Text = text, AutoSize = true, Font = font ?? Theme.Small, ForeColor = ink, Margin = new Padding(0, 1, 0, 2) };

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Br) + s[1..];

    /// <summary>"Quarta, 25/11" (the weekday without "-feira").</summary>
    private static string WeekDay(DateOnly d) => Capital(d.ToString("dddd", Br).Replace("-feira", "")) + ", " + d.ToString("dd/MM", Br);

    private void FitWidths()
    {
        int width = Math.Max(160, _detail.ClientSize.Width - _detail.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        foreach (Control c in _detail.Controls)
        {
            if (c is SaleRow or AccountRow) c.Width = width;
            else if (c is Label l) l.MaximumSize = new Size(width, 0);
        }
    }

    // ───────────── pieces ─────────────

    /// <summary>The four figures in one card: caption, value, and a short line under it (with a status dot when it is one).</summary>
    internal sealed class StatStrip : Control
    {
        public sealed record Tile(string Caption, string Value, string Sub, Color? Dot, DateOnly? Day);
        private Tile[] _tiles = [];
        private int _hover = -1;
        public event Action<DateOnly>? DayClicked;

        public StatStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.Grouping;
            Fit();
        }

        private void Fit() => Height = TextRenderer.MeasureText("Ag", Theme.Small).Height + TextRenderer.MeasureText("Ag", Theme.Headline).Height
                                       + TextRenderer.MeasureText("Ag", Theme.Fine).Height + LogicalToDeviceUnits(22 + 12);

        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }

        public void Set(Tile[] tiles)
        {
            _tiles = tiles;
            AccessibleName = string.Join(". ", tiles.Select(t => $"{t.Caption}: {t.Value}, {t.Sub}"));
            Invalidate();
        }

        private Rectangle Card => new(0, 0, Width - 1, Height - 1 - LogicalToDeviceUnits(12));
        private Rectangle TileRect(int i) { var c = Card; int w = c.Width / Math.Max(1, _tiles.Length); return new Rectangle(c.X + i * w, c.Y, w, c.Height); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            using (var path = Theme.Round(new RectangleF(.5f, .5f, Card.Width - .5f, Card.Height - .5f), Theme.RLg * s))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
            }
            const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int hs = TextRenderer.MeasureText("Ag", Theme.Small).Height, hh = TextRenderer.MeasureText("Ag", Theme.Headline).Height, hf = TextRenderer.MeasureText("Ag", Theme.Fine).Height;
            for (int i = 0; i < _tiles.Length; i++)
            {
                var t = _tiles[i];
                var r = TileRect(i);
                if (i == _hover && t.Day is not null)
                    using (var hb = new SolidBrush(Theme.CardHover)) g.FillRectangle(hb, Rectangle.Inflate(r, -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(4)));
                if (i > 0) using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.X, r.Y + LogicalToDeviceUnits(12), r.X, r.Bottom - LogicalToDeviceUnits(12));
                int x = r.X + LogicalToDeviceUnits(16), w = r.Width - LogicalToDeviceUnits(24), y = r.Y + LogicalToDeviceUnits(11);
                TextRenderer.DrawText(g, t.Caption, Theme.Small, new Rectangle(x, y, w, hs), Theme.TextDim, F);
                y += hs;
                TextRenderer.DrawText(g, t.Value, Theme.Headline, new Rectangle(x, y, w, hh), Theme.Text, F);
                y += hh;
                int dx = 0;
                if (t.Dot is { } dot) { Theme.Dot(g, x + 3.5f * s, y + hf / 2f, 7 * s, dot); dx = LogicalToDeviceUnits(12); }
                TextRenderer.DrawText(g, t.Sub, Theme.Fine, new Rectangle(x + dx, y, w - dx, hf), t.Dot is not null ? Theme.WarnInk : Theme.TextDim, F);
            }
        }

        private int IndexAt(Point p) { for (int i = 0; i < _tiles.Length; i++) if (TileRect(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            Cursor = i >= 0 && _tiles[i].Day is not null ? Cursors.Hand : Cursors.Default;
            if (i != _hover) { _hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (IndexAt(e.Location) is var i and >= 0 && _tiles[i].Day is { } d) DayClicked?.Invoke(d);
            base.OnMouseClick(e);
        }
    }

    /// <summary>One sale of the chosen day: picture, item, account and date, the amount on the right, and where it stands.</summary>
    private sealed class SaleRow : Control
    {
        private readonly CalendarEntry _e;
        private readonly Func<long, Image?> _thumb;
        private readonly string _status;
        private readonly Color _dot, _ink;

        public SaleRow(CalendarEntry e, Func<long, Image?> thumb, ToolTip tip)
        {
            _e = e;
            _thumb = thumb;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            (_status, _dot, _ink) = e.Fell
                ? ($"caiu entre {Fmt.Time(e.EarliestUtc ?? e.ExpectedUtc)} e {Fmt.Time(e.LatestUtc)[6..]}", Theme.Unknown, Theme.TextDim)
                : e.Late
                    ? ($"atrasada · pode cair a qualquer momento (limite {Fmt.ShortDay(e.LatestUtc)})", Theme.Warning, Theme.WarnInk)
                    : ($"≈ {Fmt.Time(e.ExpectedUtc)}" + (e.EarliestUtc is { } lo ? $" · entre {Fmt.ShortDay(lo)} e {Fmt.ShortDay(e.LatestUtc)}" : $" · até {Fmt.ShortDay(e.LatestUtc)}"), Theme.Valid, Theme.Text2);
            string full = $"{e.Name} · @{e.AccountName} · {(e.Fell ? "" : "+")}{Fmt.N(e.Amount)} Robux · vendida em {Fmt.Time(e.SaleUtc)} · {_status}";
            AccessibleRole = AccessibleRole.ListItem;
            AccessibleName = full;
            tip.SetToolTip(this, full);
            int line = TextRenderer.MeasureText("Ag", Theme.Fine).Height;
            Height = Math.Max(LogicalToDeviceUnits(44), TextRenderer.MeasureText("Ag", Theme.Bold).Height + 2 * line) + LogicalToDeviceUnits(18);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = DeviceDpi / 96f;
            using (var path = Theme.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Theme.RMd * s))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
            }
            int pad = LogicalToDeviceUnits(10), tile = LogicalToDeviceUnits(40);
            var box = new Rectangle(pad, (Height - tile) / 2, tile, tile);
            using (var tp = Theme.Round(box, Theme.RSm * s))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, tp);
                if (_thumb(_e.AssetId) is { } img)
                {
                    var clip = g.Clip;
                    g.SetClip(tp);
                    g.DrawImage(img, box);
                    g.Clip = clip;
                }
            }
            const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int x = box.Right + LogicalToDeviceUnits(10), y = LogicalToDeviceUnits(8), line = TextRenderer.MeasureText("Ag", Theme.Fine).Height, big = TextRenderer.MeasureText("Ag", Theme.Bold).Height;
            string amount = (_e.Fell ? "" : "+") + Fmt.N(_e.Amount);
            int aw = TextRenderer.MeasureText(amount, Theme.Bold).Width + LogicalToDeviceUnits(4);
            int right = Width - pad;
            TextRenderer.DrawText(g, amount, Theme.Bold, new Rectangle(right - aw, y, aw, big), _e.Fell ? Theme.Text2 : Theme.Text, R);
            TextRenderer.DrawText(g, _e.Name, Theme.Bold, new Rectangle(x, y, right - aw - x - LogicalToDeviceUnits(8), big), Theme.Text, F);
            y += big;
            TextRenderer.DrawText(g, $"@{_e.AccountName} · vendida em {Fmt.Time(_e.SaleUtc)}", Theme.Fine, new Rectangle(x, y, right - x, line), Theme.TextDim, F);
            y += line;
            Theme.Dot(g, x + 3.5f * s, y + line / 2f, 7 * s, _dot);
            TextRenderer.DrawText(g, _status, Theme.Fine, new Rectangle(x + LogicalToDeviceUnits(12), y, right - x - LogicalToDeviceUnits(12), line), _ink, F);
        }
    }

    /// <summary>One account in "Por conta": its pending sales on the right, the next drop under it; click to see only it.</summary>
    private sealed class AccountRow : Control
    {
        private readonly AccountPendingView _a;
        private readonly bool _on;
        private bool _hover;
        private readonly string _second, _gap;

        public AccountRow(AccountPendingView a, bool on, ToolTip tip)
        {
            _a = a;
            _on = on;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            _second = a.Next is { } n
                ? (n.Late ? "atrasada · pode cair a qualquer momento" : $"próxima {Fmt.Time(n.ExpectedUtc)} · +{Fmt.N(n.Amount)}") + $" · {ShortHold(a.Hold)}"
                : "sem venda pendente";
            _gap = a.Gap switch
            {
                PendingGap.NotRead => "vendas ainda não lidas",
                PendingGap.Unplaced when a.PendingTotal is { } t => $"+{Fmt.N(t - a.FromSales)} pendentes sem data",
                PendingGap.SummaryLower => "alguma venda já caiu (a próxima leitura confirma)",
                _ => "",
            };
            string full = $"@{a.AccountName}: {Fmt.N(a.FromSales)} Robux em {Fmt.Count(a.PendingSales, "venda pendente", "vendas pendentes")}" +
                          (a.PendingTotal is { } pt ? $" (pendentes no Roblox: {Fmt.N(pt)})" : "") + ". " + HoldText(a.Hold, a.AccountName) + (a.Note is { } note ? " " + Capital(note) + "." : "") +
                          (on ? " Clique para ver todas as contas." : " Clique para ver só esta conta.");
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = full + (on ? " Selecionada." : "");
            tip.SetToolTip(this, full);
            int line = TextRenderer.MeasureText("Ag", Theme.Fine).Height;
            Height = TextRenderer.MeasureText("Ag", Theme.Bold).Height + line * (_gap.Length > 0 ? 2 : 1) + LogicalToDeviceUnits(16);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            var rf = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Round(rf, Theme.RMd * s))
            {
                using (var b = new SolidBrush(_on ? Theme.Selected : _hover ? Theme.CardHover : Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(Focused ? Theme.Focus : _on ? Theme.Line3 : Theme.Line, Focused ? 2f * s : 1f)) g.DrawPath(p, path);
            }
            // The rail the app's lists use for the selected row.
            if (_on) using (var rail = new SolidBrush(Theme.Text)) g.FillRectangle(rail, LogicalToDeviceUnits(1), LogicalToDeviceUnits(9), 3 * s, Height - LogicalToDeviceUnits(18));
            const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int pad = LogicalToDeviceUnits(12), x = pad, y = LogicalToDeviceUnits(8), right = Width - pad;
            int line = TextRenderer.MeasureText("Ag", Theme.Fine).Height, big = TextRenderer.MeasureText("Ag", Theme.Bold).Height;
            string amount = _a.PendingSales > 0 ? Fmt.N(_a.FromSales) : "—";
            int aw = TextRenderer.MeasureText(amount, Theme.Bold).Width + LogicalToDeviceUnits(4);
            TextRenderer.DrawText(g, amount, Theme.Bold, new Rectangle(right - aw, y, aw, big), Theme.Text, R);
            TextRenderer.DrawText(g, "@" + _a.AccountName, Theme.Bold, new Rectangle(x, y, right - aw - x - LogicalToDeviceUnits(8), big), Theme.Text, F);
            y += big;
            bool late = _a.Next?.Late == true;
            int dx = 0;
            if (late) { Theme.Dot(g, x + 3.5f * s, y + line / 2f, 7 * s, Theme.Warning); dx = LogicalToDeviceUnits(12); }
            TextRenderer.DrawText(g, _second, Theme.Fine, new Rectangle(x + dx, y, right - x - dx, line), late ? Theme.WarnInk : _a.Next is null ? Theme.TextDim : Theme.Text2, F);
            if (_gap.Length > 0)
            {
                y += line;
                Theme.Dot(g, x + 3.5f * s, y + line / 2f, 7 * s, Theme.Warning);
                TextRenderer.DrawText(g, _gap, Theme.Fine, new Rectangle(x + LogicalToDeviceUnits(12), y, right - x - LogicalToDeviceUnits(12), line), Theme.WarnInk, F);
            }
        }
    }
}
