using System.Drawing.Drawing2D;

namespace RbxSwitcher.App.UI;

/// <summary>One item of a trade side: picture, name, and its net value or "Small".</summary>
internal sealed record StripItem(long AssetId, string Name, int? Serial, long? Net, bool Small)
{
    public string Tip => Name + (Serial is { } s ? $" #{s}" : "") + (Small ? " · Small" : Net is { } n ? $" · líquido {Fmt.N(n)}" : "");
}

/// <summary>
/// The two sides of a trade as item pictures: what leaves the source ⇄ what leaves the destination. Pictures come from
/// <see cref="ThumbOf"/> (Roblox's public thumbnails, shared with the Avatares tab); a soft placeholder until they load.
/// Hovering a picture shows the full name, serial and value.
/// </summary>
internal sealed class TradeStrip : Control
{
    private const int TileL = 60, ColL = 104, GapL = 8, ArrowL = 48, TitleL = 18, NameL = 16, CapL = 16;
    private readonly string _leftTitle, _rightTitle;
    private readonly IReadOnlyList<StripItem> _left, _right;
    private readonly ToolTip _tip = new() { InitialDelay = 250, AutoPopDelay = 15_000 };
    private readonly List<(Rectangle R, string Text)> _hits = [];
    private string? _tipShown;

    public Func<long, Image?> ThumbOf { get; set; } = _ => null;

    public TradeStrip(string leftTitle, IReadOnlyList<StripItem> left, string rightTitle, IReadOnlyList<StripItem> right)
    {
        _leftTitle = leftTitle; _rightTitle = rightTitle; _left = left; _right = right;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Theme.Fine;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = $"{leftTitle}: {Describe(left)}. {rightTitle}: {Describe(right)}.";
        Size = GetPreferredSize(Size.Empty);
    }

    private static string Describe(IReadOnlyList<StripItem> items) => items.Count == 0 ? "nada" : string.Join(", ", items.Select(i => i.Tip));

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private int TilesWidth(int n) => Math.Max(1, n) * S(ColL) + (Math.Max(1, n) - 1) * S(GapL);
    // A side is as wide as its pictures, or its title ("Sai de @username") when that is longer.
    private int SideWidth(string title, int n) => Math.Max(TilesWidth(n), TextRenderer.MeasureText(title, Theme.Fine).Width + S(4));

    public override Size GetPreferredSize(Size proposed) =>
        new(SideWidth(_leftTitle, _left.Count) + S(ArrowL) + SideWidth(_rightTitle, _right.Count), S(TitleL) + S(TileL) + S(6) + S(NameL) + S(CapL));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BackOf(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _hits.Clear();
        int x = 0;
        Side(g, _leftTitle, _left, ref x);
        x = SideWidth(_leftTitle, _left.Count);
        // ⇄ between the sides, centred on the pictures.
        int ay = S(TitleL) + S(TileL) / 2, ax = x + S(10), aw = S(ArrowL) - S(20), d = S(4);
        using (var pen = new Pen(Theme.Dim, Math.Max(1f, 1.4f * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, ax, ay - d, ax + aw, ay - d); g.DrawLine(pen, ax + aw - d, ay - 2 * d, ax + aw, ay - d);
            g.DrawLine(pen, ax, ay + d, ax + aw, ay + d); g.DrawLine(pen, ax, ay + d, ax + d, ay + 2 * d);
        }
        x += S(ArrowL);
        Side(g, _rightTitle, _right, ref x);
    }

    private void Side(Graphics g, string title, IReadOnlyList<StripItem> items, ref int x)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, title, Theme.Fine, new Rectangle(x, 0, SideWidth(title, items.Count), S(TitleL)), Theme.TextDim, flags);
        int top = S(TitleL);
        if (items.Count == 0)
        {
            var box = new Rectangle(x, top, S(TileL), S(TileL));
            using var path = Theme.Round(box, Theme.RSm * DeviceDpi / 96f);
            using var dash = new Pen(Theme.Line3) { DashStyle = DashStyle.Dash };
            g.DrawPath(dash, path);
            TextRenderer.DrawText(g, "nada", Theme.Fine, new Rectangle(x, top + S(TileL) + S(6), S(ColL), S(NameL)), Theme.Dim, flags);
            return;
        }
        foreach (var it in items)
        {
            var box = new Rectangle(x, top, S(TileL), S(TileL));
            using (var path = Theme.Round(box, Theme.RSm * DeviceDpi / 96f))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                if (ThumbOf(it.AssetId) is { } img)
                {
                    var clip = g.Clip;
                    g.SetClip(path);
                    int pad = S(3);
                    g.DrawImage(img, new Rectangle(box.X + pad, box.Y + pad, box.Width - 2 * pad, box.Height - 2 * pad));
                    g.Clip = clip;
                }
                else // placeholder: the item's initial
                    TextRenderer.DrawText(g, it.Name.Length > 0 ? it.Name[..1].ToUpperInvariant() : "?", Theme.Title, box, Theme.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                using var edge = new Pen(Theme.Line);
                g.DrawPath(edge, path);
            }
            int ty = top + S(TileL) + S(6);
            TextRenderer.DrawText(g, it.Name, Theme.Fine, new Rectangle(x, ty, S(ColL), S(NameL)), Theme.Text, flags);
            string cap = it.Small ? "Small" : it.Net is { } n ? Fmt.N(n) : "";
            TextRenderer.DrawText(g, cap, Theme.Fine, new Rectangle(x, ty + S(NameL), S(ColL), S(CapL)), it.Small ? Theme.Dim : Theme.Text2, flags);
            _hits.Add((new Rectangle(x, top, S(ColL), S(TileL) + S(6) + S(NameL) + S(CapL)), it.Tip));
            x += S(ColL) + S(GapL);
        }
        x -= S(GapL);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? text = _hits.FirstOrDefault(h => h.R.Contains(e.Location)).Text;
        if (text == _tipShown) return;
        _tipShown = text;
        if (text is null) _tip.Hide(this);
        else _tip.Show(text, this, e.X + S(12), e.Y + S(16), 15_000);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipShown = null;
        _tip.Hide(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
