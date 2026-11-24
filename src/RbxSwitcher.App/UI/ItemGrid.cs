using System.Drawing.Drawing2D;
using RbxSwitcher.Core.Avatars;

namespace RbxSwitcher.App.UI;

/// <summary>
/// Catalog tiles (thumbnail + name), drawn by hand so hundreds of items scroll smoothly. Selected tiles get an
/// inverse ring; a small tag says "grátis" or "da conta". Asks for thumbnails only of the tiles in view.
/// </summary>
internal sealed class ItemGrid : Control
{
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right, SmallChange = 40, LargeChange = 200 };
    private IReadOnlyList<AvatarItem> _items = [];
    private int _hover = -1, _focus = -1;

    public Func<string, Image?> ThumbOf { get; set; } = _ => null;
    public Func<AvatarItem, bool> IsSelected { get; set; } = _ => false;
    public Func<AvatarItem, string?> TagOf { get; set; } = _ => null;
    public string EmptyText { get; set; } = "Nenhum item.";

    public event Action<AvatarItem>? ItemClicked;
    /// <summary>Thumbnails needed for the tiles now visible.</summary>
    public event Action<IReadOnlyList<AvatarItem>>? NeedThumbs;

    public ItemGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Window;
        Font = Theme.Small;
        Controls.Add(_scroll);
        Theme.DarkScroll(this);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Itens";
        _scroll.ValueChanged += (_, _) => { Invalidate(); AskThumbs(); };
    }

    public IReadOnlyList<AvatarItem> Items
    {
        get => _items;
        set { _items = value; _hover = -1; _focus = -1; _scroll.Value = 0; Relayout(); Invalidate(); AskThumbs(); AccessibleDescription = $"{value.Count} itens"; }
    }

    private float S => DeviceDpi / 96f;
    private int TileW => (int)(112 * S);
    private int TileH => (int)(146 * S);
    private int Gap => (int)(8 * S);
    private int Columns => Math.Max(1, (ClientSize.Width - _scroll.Width - Gap) / (TileW + Gap));

    private void Relayout()
    {
        int rows = (_items.Count + Columns - 1) / Columns;
        int content = rows * (TileH + Gap) + Gap;
        int view = Math.Max(1, ClientSize.Height);
        _scroll.Minimum = 0;
        _scroll.LargeChange = view;
        _scroll.Maximum = Math.Max(0, content - 1);
        _scroll.Enabled = content > view;
        if (_scroll.Value > Math.Max(0, content - view)) _scroll.Value = Math.Max(0, content - view);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); AskThumbs(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!_scroll.Enabled) return;
        int max = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
        _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * (TileH + Gap), 0, max);
    }

    private Rectangle TileRect(int index)
    {
        int cols = Columns;
        int used = cols * TileW + (cols - 1) * Gap;
        int left = Math.Max(Gap, (ClientSize.Width - _scroll.Width - used) / 2);
        return new Rectangle(left + index % cols * (TileW + Gap), Gap + index / cols * (TileH + Gap) - _scroll.Value, TileW, TileH);
    }

    private (int First, int Last) VisibleRange()
    {
        int cols = Columns;
        int first = Math.Max(0, (_scroll.Value - Gap) / (TileH + Gap)) * cols;
        int last = Math.Min(_items.Count - 1, ((_scroll.Value + ClientSize.Height) / (TileH + Gap) + 1) * cols - 1);
        return (first, last);
    }

    private void AskThumbs()
    {
        if (_items.Count == 0 || NeedThumbs is null) return;
        var (first, last) = VisibleRange();
        if (last < first) return;
        var visible = new List<AvatarItem>();
        for (int i = first; i <= last; i++) if (ThumbOf(_items[i].Key) is null) visible.Add(_items[i]);
        if (visible.Count > 0) NeedThumbs(visible);
    }

    private int IndexAt(Point p)
    {
        var (first, last) = VisibleRange();
        for (int i = first; i <= last; i++) if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (i != _hover) { _hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    // Keyboard: arrows move, Enter/Space choose, Home/End jump.
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_items.Count == 0) { base.OnKeyDown(e); return; }
        int f = Math.Max(0, _focus), cols = Columns;
        int next = e.KeyCode switch
        {
            Keys.Left => f - 1, Keys.Right => f + 1, Keys.Up => f - cols, Keys.Down => f + cols,
            Keys.Home => 0, Keys.End => _items.Count - 1, _ => int.MinValue,
        };
        if (next != int.MinValue)
        {
            _focus = Math.Clamp(_focus < 0 ? 0 : next, 0, _items.Count - 1);
            ScrollInto(_focus);
            AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
            AccessibleName = "Itens: " + _items[_focus].Name + (IsSelected(_items[_focus]) ? ", escolhido" : "");
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Enter or Keys.Space && _focus >= 0)
        {
            ItemClicked?.Invoke(_items[_focus]);
            AccessibleName = "Itens: " + _items[_focus].Name + (IsSelected(_items[_focus]) ? ", escolhido" : ", não escolhido");
            Invalidate();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { if (_focus < 0 && _items.Count > 0) _focus = 0; Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    private void ScrollInto(int index)
    {
        var r = TileRect(index);
        int max = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
        if (r.Top < 0) _scroll.Value = Math.Clamp(_scroll.Value + r.Top - Gap, 0, max);
        else if (r.Bottom > ClientSize.Height) _scroll.Value = Math.Clamp(_scroll.Value + r.Bottom - ClientSize.Height + Gap, 0, max);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && IndexAt(e.Location) is var i and >= 0) { ItemClicked?.Invoke(_items[i]); Invalidate(); }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Theme.Base, new Rectangle(0, 0, ClientSize.Width - _scroll.Width, ClientSize.Height), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float s = S;
        var (first, last) = VisibleRange();
        for (int i = first; i <= last; i++)
        {
            var item = _items[i];
            var r = TileRect(i);
            bool selected = IsSelected(item), hover = i == _hover, focused = i == _focus && Focused;
            using (var path = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), Theme.RMd * s))
            {
                using (var b = new SolidBrush(hover ? Theme.CardHover : Theme.Card)) g.FillPath(b, path);
                using var p = new Pen(selected ? Theme.Inv : Theme.Line, selected ? 2 * s : 1);
                g.DrawPath(p, path);
            }
            if (focused)
            {
                using var fp = Theme.Round(RectangleF.Inflate(r, 3 * s, 3 * s), (Theme.RMd + 3) * s);
                using var p = new Pen(Theme.Focus, 2 * s) { DashStyle = DashStyle.Dot };
                g.DrawPath(p, fp);
            }
            int pad = (int)(8 * s);
            var pic = new Rectangle(r.X + pad, r.Y + pad, r.Width - 2 * pad, r.Width - 2 * pad);
            using (var pp = Theme.Round(pic, Theme.RSm * s))
            using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, pp);
            if (ThumbOf(item.Key) is { } img) g.DrawImage(img, pic);
            if (TagOf(item) is { } tag)
            {
                var ts = TextRenderer.MeasureText(tag, Theme.Fine);
                var tr = new Rectangle(pic.X + (int)(4 * s), pic.Y + (int)(4 * s), ts.Width + (int)(6 * s), ts.Height + (int)(2 * s));
                using (var tp = Theme.Round(tr, Theme.RXs * s))
                using (var b = new SolidBrush(Theme.Raised)) g.FillPath(b, tp);
                TextRenderer.DrawText(g, tag, Theme.Fine, tr, Theme.Text2, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (selected)
            {
                var cr = new RectangleF(pic.Right - 22 * s, pic.Y + 4 * s, 18 * s, 18 * s);
                using (var b = new SolidBrush(Theme.Inv)) g.FillEllipse(b, cr);
                using var p = new Pen(Theme.InvInk, 1.75f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(p, [new PointF(cr.X + 5 * s, cr.Y + 9 * s), new PointF(cr.X + 8 * s, cr.Y + 12 * s), new PointF(cr.X + 13 * s, cr.Y + 6 * s)]);
            }
            var nameRect = new Rectangle(r.X + pad, pic.Bottom + (int)(5 * s), r.Width - 2 * pad, r.Bottom - pic.Bottom - (int)(8 * s));
            TextRenderer.DrawText(g, item.Name, Theme.Small, nameRect, selected ? Theme.Text : Theme.Text2,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }
    }
}
