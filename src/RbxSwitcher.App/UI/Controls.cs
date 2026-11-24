using System.Drawing.Drawing2D;

namespace RbxSwitcher.App.UI;

// Controls drawn to the design rules in Theme (adapted from Bloxkeep's DESIGN.md, MIT — see THIRD-PARTY-NOTICES.md).

internal enum ButtonKind { Secondary, Primary, Ghost, Danger }

/// <summary>Icons drawn as strokes (no icon font or emoji: same look on every Windows and in every font).</summary>
internal enum UiIcon { None, Back, Forward, Refresh, Close, ChevronDown, ChevronUp }

internal static class Icons
{
    /// <summary>Draws <paramref name="icon"/> centred in <paramref name="r"/> with a 1.6 px stroke.</summary>
    public static void Draw(Graphics g, UiIcon icon, RectangleF r, Color ink, float s)
    {
        if (icon == UiIcon.None) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(ink, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, u = 4.5f * s;
        switch (icon)
        {
            case UiIcon.Back: g.DrawLines(p, [new PointF(cx + u / 2, cy - u), new PointF(cx - u / 2, cy), new PointF(cx + u / 2, cy + u)]); break;
            case UiIcon.Forward: g.DrawLines(p, [new PointF(cx - u / 2, cy - u), new PointF(cx + u / 2, cy), new PointF(cx - u / 2, cy + u)]); break;
            case UiIcon.ChevronDown: g.DrawLines(p, [new PointF(cx - u * .8f, cy - u * .4f), new PointF(cx, cy + u * .4f), new PointF(cx + u * .8f, cy - u * .4f)]); break;
            case UiIcon.ChevronUp: g.DrawLines(p, [new PointF(cx - u * .8f, cy + u * .4f), new PointF(cx, cy - u * .4f), new PointF(cx + u * .8f, cy + u * .4f)]); break;
            case UiIcon.Close:
                g.DrawLine(p, cx - u * .8f, cy - u * .8f, cx + u * .8f, cy + u * .8f);
                g.DrawLine(p, cx + u * .8f, cy - u * .8f, cx - u * .8f, cy + u * .8f);
                break;
            case UiIcon.Refresh:
                var arc = new RectangleF(cx - u, cy - u, 2 * u, 2 * u);
                g.DrawArc(p, arc, -60, 300);
                // arrow head at the arc's open end (top right)
                float ax = cx + u * (float)Math.Cos(-60 * Math.PI / 180), ay = cy + u * (float)Math.Sin(-60 * Math.PI / 180);
                g.DrawLines(p, [new PointF(ax - u * .7f, ay - u * .15f), new PointF(ax, ay), new PointF(ax + u * .1f, ay + u * .75f)]);
                break;
        }
    }
}

/// <summary>
/// Rounded button: secondary (surface-2 + hairline), primary (inverse), ghost (transparent) or danger (soft red).
/// 30 px (small radius) or 38 px when Large. One tonal step on hover, a 1 px sink on press, 45 % when disabled,
/// a 2 px focus outline only for keyboard focus.
/// </summary>
internal class UiButton : Button
{
    private bool _hover, _down;
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    public bool Large { get; set; }
    /// <summary>Stroke icon before the text (or alone); <see cref="IconAfterText"/> puts it after (dropdowns).</summary>
    public UiIcon Icon { get; set; }
    public bool IconAfterText { get; set; }

    public UiButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        Cursor = Cursors.Hand;
        Font = Theme.Header;
        ForeColor = Theme.Text;
    }

    public override Size GetPreferredSize(Size proposed)
    {
        float s = DeviceDpi / 96f;
        int h = (int)((Large ? 38 : 30) * s);
        int pad = (int)((Large ? 14 : 10) * s);
        if (Text.Length == 0) return new Size(h, h); // icon-only: square
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        int icon = Icon == UiIcon.None ? 0 : (int)(18 * s);
        return new Size(text.Width + 2 * pad + icon + (int)(6 * s), h); // slack: measured and drawn widths differ by a few px
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Theme.BackOf(Parent);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        bool hot = _hover && Enabled;
        (Color fill, Color line, Color ink) = Kind switch
        {
            ButtonKind.Primary => (hot ? Theme.InvHover : Theme.Inv, Color.Empty, Theme.InvInk),
            ButtonKind.Ghost => (hot ? Theme.Hover : back, Color.Empty, hot ? Theme.Text : Theme.Text2),
            ButtonKind.Danger => (hot ? Theme.Mix(Theme.BadSoft, Theme.Expired, .1f) : Theme.BadSoft, Theme.BadLine, Theme.BadInk),
            _ => (hot ? Theme.SurfaceHover : Theme.Surface, hot ? Theme.Line3 : Theme.Border, ForeColor),
        };
        if (!Enabled) { fill = Theme.Mix(back, fill, .45f); line = line.IsEmpty ? line : Theme.Mix(back, line, .45f); ink = Theme.Mix(back, ink, .45f); }
        var r = new RectangleF(.5f, .5f + (_down ? 1 : 0), Width - 1.5f, Height - 1.5f - (_down ? 1 : 0));
        float radius = (Large ? Theme.RMd : Theme.RSm) * s;
        using (var path = Theme.Round(r, radius))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (!line.IsEmpty) using (var p = new Pen(line)) g.DrawPath(p, path);
        }
        if (Icon == UiIcon.None)
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        else if (Text.Length == 0)
            Icons.Draw(g, Icon, r, ink, s);
        else
        {
            int pad = (int)((Large ? 14 : 10) * s), icon = (int)(18 * s);
            var tr = new Rectangle((int)r.X + pad + (IconAfterText ? 0 : icon), (int)r.Y, (int)r.Width - 2 * pad - icon, (int)r.Height);
            TextRenderer.DrawText(g, Text, Font, tr, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            var ir = IconAfterText ? new RectangleF(tr.Right + 2 * s, r.Y, icon - 2 * s, r.Height) : new RectangleF(r.X + pad - 2 * s, r.Y, icon, r.Height);
            Icons.Draw(g, Icon, ir, ink, s);
        }
        if (Focused && ShowFocusCues)
        {
            using var p = new Pen(Theme.Focus, 2f * s);
            using var fp = Theme.Round(RectangleF.Inflate(r, -2 * s, -2 * s), Math.Max(1, radius - 2 * s));
            g.DrawPath(p, fp);
        }
    }
}

/// <summary>Segmented control: one choice among a few views ("Contas | Plus"). Selected segment on surface-3.</summary>
internal sealed class Segmented : Control
{
    private readonly string[] _items;
    private int?[] _counts;
    private int _selected, _hover = -1;
    public event Action<int>? SelectedIndexChanged;

    /// <summary>A dim count after each label ("Em andamento 2"); null hides it.</summary>
    public void SetCounts(params int?[] counts)
    {
        var next = _items.Select((_, i) => i < counts.Length ? counts[i] : null).ToArray();
        if (next.SequenceEqual(_counts)) return;
        _counts = next;
        AccessibleName = string.Join(", ", _items.Select((t, i) => _counts[i] is { } n ? $"{t} {n}" : t));
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    private string? CountText(int i) => _counts[i] is { } n ? n.ToString() : null;
    private int CountWidth(int i) => CountText(i) is { } c ? TextRenderer.MeasureText(c, Theme.Fine).Width + (int)(4 * DeviceDpi / 96f) : 0;

    public Segmented(params string[] items)
    {
        _items = items;
        _counts = new int?[items.Length];
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Header;
        Cursor = Cursors.Hand;
        Size = GetPreferredSize(Size.Empty);
    }

    public int SelectedIndex
    {
        get => _selected;
        set { if (value == _selected || value < 0 || value >= _items.Length) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(value); }
    }

    private int[] Widths()
    {
        float s = DeviceDpi / 96f;
        return _items.Select((t, i) => TextRenderer.MeasureText(t, Font).Width + CountWidth(i) + (int)(28 * s)).ToArray();
    }

    public override Size GetPreferredSize(Size proposed)
    {
        float s = DeviceDpi / 96f;
        return new Size(Widths().Sum() + (int)(4 * s) + (int)(2 * s) * (_items.Length - 1), (int)(30 * s));
    }

    private int IndexAt(Point p)
    {
        float s = DeviceDpi / 96f;
        int x = (int)(2 * s);
        var w = Widths();
        for (int i = 0; i < w.Length; i++) { if (p.X >= x && p.X < x + w[i]) return i; x += w[i] + (int)(2 * s); }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.Location); if (i != _hover) { _hover = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e) { if (IndexAt(e.Location) is var i and >= 0) SelectedIndex = i; base.OnMouseClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _selected - 1);
        else if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(_items.Length - 1, _selected + 1);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using (var track = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.RSm * s))
        using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, track);
        int x = (int)(2 * s);
        var w = Widths();
        for (int i = 0; i < w.Length; i++)
        {
            var r = new RectangleF(x, 2 * s, w[i], Height - 4 * s);
            if (i == _selected || i == _hover)
            {
                using var seg = Theme.Round(r, (Theme.RSm - 2) * s);
                using var b = new SolidBrush(i == _selected ? Theme.SurfaceHover : Theme.Selected);
                g.FillPath(b, seg);
            }
            var ink = i == _selected ? Theme.Text : Theme.TextDim;
            if (CountText(i) is not { } count)
                TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(r), ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            else
            {
                // Label, then the count in the dim ink, centred together.
                int tw = TextRenderer.MeasureText(_items[i], Font).Width, cw = CountWidth(i);
                int left = (int)(r.X + (r.Width - tw - cw) / 2);
                var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                TextRenderer.DrawText(g, _items[i], Font, new Rectangle(left, (int)r.Y, tw, (int)r.Height), ink, flags);
                TextRenderer.DrawText(g, count, Theme.Fine, new Rectangle(left + tw + (int)(4 * s), (int)r.Y, cw, (int)r.Height), Theme.TextDim, flags);
            }
            x += w[i] + (int)(2 * s);
        }
        if (Focused && ShowFocusCues)
        {
            using var p = new Pen(Theme.Focus, 2 * s);
            using var fp = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), Theme.RSm * s);
            g.DrawPath(p, fp);
        }
    }
}

/// <summary>Filter chip: transparent, text-2; a 7 px status dot when it filters by status; a count pill. On: inverse.</summary>
internal sealed class Chip : Control
{
    private bool _hover, _on;
    private Color? _dot;
    public Color? DotColor { get => _dot; set { bool resize = (_dot is null) != (value is null); _dot = value; if (resize) Resize_(); Invalidate(); } }
    private int? _count;
    public int? Count { get => _count; set { _count = value; Describe(); } }
    /// <summary>A hairline around the chip when off, so it reads as clickable where it stands alone.</summary>
    public bool Outline { get; set; }

    public bool On { get => _on; set { _on = value; Describe(); Invalidate(); } }

    public Chip(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Font = Theme.Header;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 4, 0);
        Describe();
    }

    private void Describe() => AccessibleName = Text + (_count is { } c ? $", {c}" : "") + (_on ? ", selecionado" : "");

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    public void Resize_() => Size = GetPreferredSize(Size.Empty);

    public override Size GetPreferredSize(Size proposed)
    {
        float s = DeviceDpi / 96f;
        int w = TextRenderer.MeasureText(Text, Font).Width + (int)(26 * s); // a few px of slack: fonts measure differently
        if (DotColor is not null) w += (int)(12 * s);
        if (Count is { } c) w += TextRenderer.MeasureText(c.ToString(), Theme.Tiny).Width + (int)(16 * s);
        return new Size(w, (int)(30 * s));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Describe(); Resize_(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Theme.BackOf(Parent);
        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        if (_on || _hover)
        {
            using var path = Theme.Round(r, Theme.RSm * s);
            using var b = new SolidBrush(_on ? Theme.Inv : Theme.Hover);
            g.FillPath(b, path);
        }
        else if (Outline)
        {
            using var path = Theme.Round(r, Theme.RSm * s);
            using var p = new Pen(Theme.Border);
            g.DrawPath(p, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var fp = Theme.Round(RectangleF.Inflate(r, -1.5f * s, -1.5f * s), Theme.RSm * s);
            using var p = new Pen(Theme.Focus, 2 * s);
            g.DrawPath(p, fp);
        }
        Color ink = _on ? Theme.InvInk : _hover ? Theme.Text : Theme.Text2;
        float x = 10 * s;
        if (DotColor is { } dot) { Theme.Dot(g, x + 3.5f * s, Height / 2f, 7 * s, dot); x += 12 * s; }
        var size = TextRenderer.MeasureText(Text, Font);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)x, 0, size.Width, Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        x += size.Width + 4 * s;
        if (Count is { } c)
        {
            string t = c.ToString();
            var cs = TextRenderer.MeasureText(t, Theme.Tiny);
            var pill = new RectangleF(x, (Height - 18 * s) / 2, cs.Width + 8 * s, 18 * s);
            using var path = Theme.Round(pill, Theme.RMd * s);
            if (_on) { using var p = new Pen(Theme.Mix(Theme.Inv, Theme.InvInk, .4f)); g.DrawPath(p, path); }
            else { using var b = new SolidBrush(Theme.Selected); g.FillPath(b, path); }
            TextRenderer.DrawText(g, t, Theme.Tiny, Rectangle.Round(pill), ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Card: --card fill, 1 px --line hairline, 14 px radius, 16 px padding, no shadow. Optional 4 px status rail.</summary>
internal class CardPanel : Panel
{
    public Color? Rail { get; set; }
    public Color Fill { get; set; } = Theme.Card;

    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(16);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, Theme.RLg * s);
        using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
        using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
        if (Rail is { } rail)
        {
            using var rp = Theme.Round(new RectangleF(0, 12 * s, 4 * s, Height - 24 * s), 2 * s);
            using var b = new SolidBrush(rail);
            g.FillPath(b, rp);
        }
    }
}

/// <summary>Typeable field: --field fill, stronger --line-field border, 9 px radius, 34 px; border lightens on focus.</summary>
internal sealed class FieldBox : Panel
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Base };

    public FieldBox(string placeholder)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Field;
        Box.PlaceholderText = placeholder;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float s = DeviceDpi / 96f;
        Height = (int)(34 * s);
        int pad = (int)(11 * s);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - 2 * pad), Box.PreferredHeight);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using var path = Theme.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Theme.RSm * s);
        using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
        using (var p = new Pen(Box.Focused ? Theme.Text2 : Theme.Mix(Theme.Field, Theme.LineField, .8f))) g.DrawPath(p, path); // field edge ≥ 3:1
    }
}

/// <summary>Styled select: a secondary button "Label ▾" whose choices open in the app's own menu (no native popup).</summary>
internal sealed class DropdownButton : UiButton
{
    private readonly ContextMenuStrip _menu = new() { ShowCheckMargin = true };
    private string[] _items = [];
    private int _selected;
    private readonly string _prefix;
    public event Action? SelectionChanged;

    public DropdownButton(string prefix)
    {
        _prefix = prefix;
        Theme.Apply(_menu);
        _menu.ShowImageMargin = false;
        _menu.ShowCheckMargin = true;
        Click += (_, _) =>
        {
            if (_menu.Visible) { _menu.Close(); return; } // a second click on the opener closes it
            _menu.Items.Clear();
            for (int i = 0; i < _items.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected };
                item.Click += (_, _) => { Selected = _items[index]; };
                _menu.Items.Add(item);
            }
            _menu.Show(this, new Point(0, Height + 2));
        };
        UpdateText();
    }

    public IReadOnlyList<string> Items => _items;

    public string? Selected
    {
        get => _selected < _items.Length ? _items[_selected] : null;
        set
        {
            int i = value is null ? 0 : Array.IndexOf(_items, value);
            if (i < 0) i = 0;
            bool changed = i != _selected;
            _selected = i;
            UpdateText();
            if (changed) SelectionChanged?.Invoke();
        }
    }

    public int SelectedIndex => _selected;

    /// <summary>Replaces the choices, keeping the current one when it is still there (no event when it is).</summary>
    public void SetItems(IEnumerable<string> items)
    {
        string? keep = Selected;
        _items = items.ToArray();
        int i = keep is null ? 0 : Array.IndexOf(_items, keep);
        bool changed = i < 0 && keep is not null && _selected != 0;
        _selected = Math.Max(0, i);
        UpdateText();
        if (changed) SelectionChanged?.Invoke();
    }

    private void UpdateText()
    {
        Text = (_prefix.Length > 0 ? _prefix + ": " : "") + (Selected ?? "—");
        Icon = UiIcon.ChevronDown;
        IconAfterText = true;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }
}

internal enum NoteKind { Info, Warn, Bad, Ok }

/// <summary>Note: one component, soft fill + ink colour, 11 px radius, 10×12 padding. Never bare coloured text.</summary>
internal sealed class NoteLabel : Label
{
    private NoteKind _kind = NoteKind.Warn;
    public NoteKind Kind { get => _kind; set { _kind = value; ForeColor = Ink; Invalidate(); } }

    private Color Fill => _kind switch { NoteKind.Info => Theme.OnlineSoft, NoteKind.Bad => Theme.BadSoft, NoteKind.Ok => Theme.OkSoft, _ => Theme.WarnSoft };
    private Color Ink => _kind switch { NoteKind.Info => Theme.OnlineInk, NoteKind.Bad => Theme.BadInk, NoteKind.Ok => Theme.OkInk, _ => Theme.WarnInk };

    /// <summary>Measured with the same wrapping rules used to draw, so no word is cut at the right edge.</summary>
    public override Size GetPreferredSize(Size proposed)
    {
        int max = MaximumSize.Width > 0 ? MaximumSize.Width : proposed.Width > 0 && proposed.Width < int.MaxValue / 2 ? proposed.Width : 10_000;
        int inner = Math.Max(1, max - Padding.Horizontal - 8); // a few px of air: glyph widths vary between fonts
        var t = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(inner, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        // A long note takes the whole width it may use: drawn text can run a little wider than measured.
        int width = t.Width + Padding.Horizontal + 2 > max / 2 && max < 10_000 ? max : t.Width + Padding.Horizontal + 2;
        return new Size(Math.Min(max, width), t.Height + Padding.Vertical);
    }

    public NoteLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        Padding = new Padding(12, 9, 12, 9);
        Font = Theme.Base;
        ForeColor = Ink;
        Margin = new Padding(0, 4, 0, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.RMd * s))
        using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
        var r = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal - 8, Height - Padding.Vertical);
        TextRenderer.DrawText(g, Text, Font, r, ForeColor, TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | (AutoEllipsis ? TextFormatFlags.EndEllipsis : 0));
    }
}

/// <summary>Status as Bloxkeep draws it: a 7 px dot and a word in the status ink. No coloured background.</summary>
internal sealed class StatusText : Control
{
    private Color _dot = Theme.Unknown, _ink = Theme.Text2;

    public StatusText()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Title;
        Margin = new Padding(0, 2, 0, 4);
    }

    public void Set(string text, Color dot, Color ink)
    {
        _dot = dot;
        _ink = ink;
        Text = text;
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposed)
    {
        float s = DeviceDpi / 96f;
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + (int)(16 * s), t.Height + (int)(4 * s));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        float s = DeviceDpi / 96f;
        Theme.Dot(g, 3.5f * s + .5f, Height / 2f, 7 * s, _dot);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(14 * s), 0, Width, Height), _ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A card whose content flows top-down (same look as <see cref="CardPanel"/>), so it sizes to what it holds.</summary>
internal sealed class CardStack : FlowLayoutPanel
{
    public CardStack()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        BackColor = Theme.Card;
        Padding = new Padding(18, 14, 18, 16);
        Margin = new Padding(0, 0, 0, 12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        using var path = Theme.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Theme.RLg * s);
        using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
        using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
    }
}
