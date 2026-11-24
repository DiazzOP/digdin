using System.Drawing.Drawing2D;
using System.Globalization;

namespace RbxSwitcher.App.UI;

/// <summary>What one day of the calendar shows.</summary>
/// <param name="Expected">Robux expected to fall that day (pending sales).</param>
/// <param name="Fell">Robux the app saw falling that day.</param>
/// <param name="Late">Some of the expected Robux are past their estimate (shown on today).</param>
/// <param name="Tip">The day in words, for the hover tooltip.</param>
internal sealed record DayCell(long Expected, int ExpectedCount, long Fell, int FellCount, bool Late, string Tip);

/// <summary>
/// A month as a grid of its weeks (Sunday first, as Brazilian calendars). A day with Robux expected gets the soft
/// green fill, its amount in text ink and a bar as long as its share of the month's biggest day; a late one is amber
/// with the word "atrasada" (never colour alone); a day whose Robux were seen falling says "caiu". Today has a blue
/// outline; the chosen day a light one (white when the grid has focus). The cells shrink with the window instead of
/// being cut: what does not fit drops out line by line. Click or keys: arrows, Home (today), Page Up/Down (month).
/// </summary>
internal sealed class MonthGrid : Control
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] WeekDays = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];
    private const int HeaderL = 24, GapL = 6;

    private DateOnly _month = new(2026, 1, 1), _today = new(2026, 1, 1), _selected = new(2026, 1, 1);
    private IReadOnlyDictionary<DateOnly, DayCell> _cells = new Dictionary<DateOnly, DayCell>();
    private long _max = 1;
    private int _hover = -1;
    private readonly ToolTip _tip = new() { InitialDelay = 250, AutoPopDelay = 20_000, ReshowDelay = 100 };

    /// <summary>A day was chosen (click or keys); a day of another month asks to show that month.</summary>
    public event Action<DateOnly>? DayChosen;

    public MonthGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Fine;
        TabStop = true;
        AccessibleRole = AccessibleRole.Table;
        // Small enough to fit 1366×768 at 150 %: a row is then one line (the day and its amount).
        MinimumSize = new Size(LogicalToDeviceUnits(7 * 40), LogicalToDeviceUnits(HeaderL + 5 * 22));
    }

    public void Set(DateOnly month, DateOnly today, DateOnly selected, IReadOnlyDictionary<DateOnly, DayCell> cells)
    {
        _month = new DateOnly(month.Year, month.Month, 1);
        _today = today;
        _selected = selected;
        _cells = cells;
        // The bar of a day is its share of the biggest day on screen.
        _max = Math.Max(1, Enumerable.Range(0, Weeks * 7).Select(i => _cells.TryGetValue(Start.AddDays(i), out var c) ? c.Expected : 0).DefaultIfEmpty(0).Max());
        AccessibleName = "Calendário de " + _month.ToString("MMMM 'de' yyyy", Br) + ". Dia escolhido: " + DayText(selected);
        _hover = -1;
        Invalidate();
    }

    private string DayText(DateOnly d) => _cells.TryGetValue(d, out var c) ? c.Tip : d.ToString("dddd, dd/MM", Br) + ": nada previsto";

    /// <summary>The Sunday that opens the grid.</summary>
    private DateOnly Start => _month.AddDays(-(int)_month.DayOfWeek);

    /// <summary>Only the weeks this month touches (4 to 6).</summary>
    private int Weeks => ((int)_month.DayOfWeek + DateTime.DaysInMonth(_month.Year, _month.Month) + 6) / 7;

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private Rectangle CellRect(int index)
    {
        int gap = S(GapL), top = S(HeaderL), weeks = Weeks;
        int w = (Width - 6 * gap) / 7, h = (Height - top - (weeks - 1) * gap) / weeks;
        return new Rectangle(index % 7 * (w + gap), top + index / 7 * (h + gap), w, h);
    }

    private int IndexAt(Point p)
    {
        for (int i = 0; i < Weeks * 7; i++) if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int gap = S(GapL), w = (Width - 6 * gap) / 7;
        for (int c = 0; c < 7; c++)
            TextRenderer.DrawText(g, WeekDays[c], Theme.Header, new Rectangle(c * (w + gap) + S(8), 0, w - S(8), S(HeaderL)), Theme.TextDim, Left | TextFormatFlags.VerticalCenter);

        int line = TextRenderer.MeasureText("Ag", Theme.Fine).Height, big = TextRenderer.MeasureText("Ag", Theme.Bold).Height;
        for (int i = 0; i < Weeks * 7; i++)
        {
            var day = Start.AddDays(i);
            var r = CellRect(i);
            bool inMonth = day.Month == _month.Month, selected = day == _selected, today = day == _today, past = day < _today;
            _cells.TryGetValue(day, out var cell);
            bool due = cell is { Expected: > 0 };

            // Fill: soft green (or amber) where Robux are expected; a quiet card for the other days of the month; nothing
            // for the days of the next/previous month, so they step back.
            Color? fill = due ? (cell!.Late ? Theme.WarnSoft : Theme.OkSoft) : inMonth ? Theme.Card : null;
            if (i == _hover) fill = Theme.Mix(fill ?? Theme.BackOf(Parent), Theme.Text, .06f);
            if (selected && !due) fill = Theme.Selected;
            var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1);
            using (var path = Theme.Round(rf, Theme.RMd * s))
            {
                if (fill is { } f) using (var b = new SolidBrush(f)) g.FillPath(b, path);
                if (today) using (var p = new Pen(Theme.Online, 2f * s)) g.DrawPath(p, path);
            }
            if (selected)
            {
                // The chosen day: a ring inside today's outline; white while the grid has focus (the keyboard cue).
                using var ring = Theme.Round(RectangleF.Inflate(rf, -(today ? 3 : 1) * s, -(today ? 3 : 1) * s), Math.Max(2, (Theme.RMd - 2) * s));
                using var p = new Pen(Focused ? Theme.Focus : Theme.Line3, (Focused ? 2f : 1.5f) * s);
                g.DrawPath(p, ring);
            }

            int pad = S(8), x = r.X + pad, tw = r.Width - 2 * pad, y = r.Y + S(5);
            var numberInk = today ? Theme.OnlineInk : !inMonth ? Theme.Dim : past ? Theme.TextDim : Theme.Text2;
            string amount = due ? Fmt.N(cell!.Expected) : "";
            bool compact = r.Height < line + big + S(14); // one line: the day and the amount side by side
            TextRenderer.DrawText(g, day.Day.ToString(), Theme.Header, new Rectangle(x, y, tw, line), numberInk, Left);
            if (today && !compact && tw > S(70))
                TextRenderer.DrawText(g, "hoje", Theme.Fine, new Rectangle(x, y, tw, line), Theme.OnlineInk, Right);
            var amountInk = inMonth ? Theme.Text : Theme.TextDim;
            if (compact)
            {
                // Beside the day number: the amount, shortened ("10,8k") when narrow; with no room at all only the fill
                // and the bar say it (the tooltip and the panel have the number).
                const TextFormatFlags Tight = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                int Width_(string t, Font f) => TextRenderer.MeasureText(g, t, f, Size.Empty, Tight).Width;
                int room = tw - Width_(day.Day.ToString(), Theme.Header) - S(8);
                if (due)
                {
                    int dot = cell!.Late ? S(12) : 0; // late: the amber dot too, never the fill colour alone
                    string text = Width_(amount, Theme.Bold) + dot <= room ? amount : Short(cell.Expected);
                    if (Width_(text, Theme.Bold) + dot <= room)
                    {
                        TextRenderer.DrawText(g, text, Theme.Bold, new Rectangle(x, y - S(1), tw, big), amountInk, Right | TextFormatFlags.NoPadding);
                        if (cell.Late) Theme.Dot(g, x + tw - Width_(text, Theme.Bold) - 8 * s, y + line / 2f, 7 * s, Theme.Warning);
                    }
                    else if (cell.Late) Theme.Dot(g, x + tw - 4 * s, y + line / 2f, 7 * s, Theme.Warning);
                }
                else if (cell is { Fell: > 0 } && Width_("caiu", Theme.Fine) <= room)
                    TextRenderer.DrawText(g, "caiu", Theme.Fine, new Rectangle(x, y, tw, line), Theme.TextDim, Right | TextFormatFlags.NoPadding);
            }
            else
            {
                y += line + S(1);
                if (due)
                {
                    TextRenderer.DrawText(g, "+" + amount, Theme.Bold, new Rectangle(x, y, tw, big), amountInk, Left);
                    y += big;
                    string? sub = cell!.Late ? "atrasada" : r.Height >= line * 2 + big + S(18) ? Fmt.Count(cell.ExpectedCount, "venda", "vendas") : null;
                    if (sub is not null && y + line <= r.Bottom - S(8))
                    {
                        int dx = 0;
                        if (cell.Late) { Theme.Dot(g, x + 3.5f * s, y + line / 2f, 7 * s, Theme.Warning); dx = S(12); }
                        TextRenderer.DrawText(g, sub, Theme.Fine, new Rectangle(x + dx, y, tw - dx, line), cell.Late ? Theme.WarnInk : Theme.TextDim, Left);
                        y += line;
                    }
                }
                if (cell is { Fell: > 0 } && y + line <= r.Bottom - S(6))
                    TextRenderer.DrawText(g, "caiu " + Fmt.N(cell.Fell), Theme.Fine, new Rectangle(x, y, tw, line), Theme.TextDim, Left);
            }
            if (due && r.Height >= line + S(18)) // a cell too short for it keeps only the fill
            {
                // The amount as a bar along the bottom: its share of the biggest day on screen.
                float bw = Math.Max(S(6), (float)(tw * Math.Min(1.0, (double)cell!.Expected / _max)));
                var bar = new RectangleF(x, r.Bottom - S(7), bw, 3.5f * s);
                using var bp = Theme.Round(bar, 1.75f * s);
                using var bb = new SolidBrush(cell.Late ? Theme.Warning : inMonth ? Theme.Valid : Theme.Mix(Theme.Valid, Theme.Card, .5f));
                g.FillPath(bb, bp);
            }
        }
    }

    /// <summary>"950", "2,1k", "10,8k".</summary>
    private static string Short(long n) => n < 1000 ? Fmt.N(n) : (n / 1000.0).ToString(n < 10_000 ? "0.#" : "0", Br) + "k";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover)
        {
            _hover = i;
            _tip.SetToolTip(this, i >= 0 ? DayText(Start.AddDays(i)) : "");
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Focus();
        if (IndexAt(e.Location) is var i and >= 0) DayChosen?.Invoke(Start.AddDays(i));
        base.OnMouseClick(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        DateOnly? to = e.KeyCode switch
        {
            Keys.Left => _selected.AddDays(-1),
            Keys.Right => _selected.AddDays(1),
            Keys.Up => _selected.AddDays(-7),
            Keys.Down => _selected.AddDays(7),
            Keys.Home => _today,
            Keys.PageUp => _selected.AddMonths(-1),
            Keys.PageDown => _selected.AddMonths(1),
            _ => null,
        };
        if (to is { } d) { DayChosen?.Invoke(d); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>What the colours of the calendar mean (one row under the grid).</summary>
internal sealed class CalendarLegend : Control
{
    public CalendarLegend()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Fine;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = "Legenda: verde, Robux que devem cair, a barra mostra o valor; âmbar, atrasada, passou da estimativa; caiu, já creditado; contorno azul, hoje.";
        Height = TextRenderer.MeasureText("Ag", Theme.Fine).Height + LogicalToDeviceUnits(14);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = TextRenderer.MeasureText("Ag", Theme.Fine).Height + LogicalToDeviceUnits(14); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        int h = TextRenderer.MeasureText("Ag", Theme.Fine).Height, y = LogicalToDeviceUnits(8), x = 0, sw = LogicalToDeviceUnits(18);
        const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        void Swatch(Color fill, Color? bar, Color? outline)
        {
            var r = new RectangleF(x, y + 1, sw, h - 2);
            using var path = Theme.Round(r, 4 * s);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (outline is { } o) using (var p = new Pen(o, 1.6f * s)) g.DrawPath(p, path);
            if (bar is { } c) using (var bb = new SolidBrush(c)) g.FillRectangle(bb, r.X + 3 * s, r.Bottom - 4.5f * s, r.Width * .6f, 2.5f * s);
            x += sw + LogicalToDeviceUnits(6);
        }
        void Text(string t, Color ink)
        {
            TextRenderer.DrawText(g, t, Theme.Fine, new Point(x, y), ink, F);
            x += TextRenderer.MeasureText(t, Theme.Fine).Width + LogicalToDeviceUnits(16);
        }
        Swatch(Theme.OkSoft, Theme.Valid, null); Text("deve cair (a barra mostra o valor)", Theme.TextDim);
        Swatch(Theme.WarnSoft, Theme.Warning, null); Text("atrasada", Theme.TextDim);
        Text("caiu = já creditado", Theme.TextDim);
        Swatch(Theme.Card, null, Theme.Online); Text("hoje", Theme.TextDim);
    }
}
