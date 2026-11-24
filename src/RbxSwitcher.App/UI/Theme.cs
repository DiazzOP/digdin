using System.Drawing.Drawing2D;

namespace RbxSwitcher.App.UI;

/// <summary>
/// Design tokens. The palette, sizes and rules are adapted from Bloxkeep's design system (DESIGN.md and the
/// :root tokens of RobloxManager/wwwroot/app.css, https://github.com/Bloxkeep/roblox-account-manager),
/// MIT License, Copyright (c) 2026 minhhoang0122 — see THIRD-PARTY-NOTICES.md. Nothing here is copied code:
/// Bloxkeep is CSS/Rust, this is WinForms; translucent CSS tokens are pre-blended into solid colours.
/// Rules kept: solid colours only; the primary button is inverse (light on dark), not coloured; green means
/// "alive" only; status = 7 px dot (or 4 px rail) + a word, never a coloured background behind text; mixed case.
/// </summary>
internal static class Theme
{
    // ── Neutrals (page → panel → raised) ──
    public static readonly Color Window = Hex(0x0a0c11);        // --bg
    public static readonly Color Bg2 = Hex(0x0d1017);           // --bg-2
    public static readonly Color Sidebar = Hex(0x11151e);       // --surface (panel)
    public static readonly Color Surface = Hex(0x161b26);       // --surface-2 (buttons, hover step)
    public static readonly Color SurfaceHover = Hex(0x1d2331);  // --surface-3
    public static readonly Color Raised = Hex(0x151a25);        // --raised (menus)
    public static readonly Color Field = Hex(0x0b0e15);         // --field
    public static readonly Color Card = Hex(0x12161f);          // --card
    public static readonly Color CardHover = Hex(0x161b26);     // --card-hover

    // ── Hairlines and tonal steps (white at 6/10/17 %, 6/9 % pre-blended over the panel) ──
    public static readonly Color Line = Hex(0x1f232c);          // --line
    public static readonly Color Border = Hex(0x292c35);        // --line-2
    public static readonly Color Line3 = Hex(0x3a3d44);         // --line-3
    public static readonly Color LineField = Hex(0x7f838d);     // --line-field (fields read as typeable)
    public static readonly Color Hover = Hex(0x1f232c);         // --hover
    public static readonly Color Selected = Hex(0x262a32);      // --active
    public static readonly Color Focus = Hex(0xb3b6bd);         // --focus

    // ── Text ──
    public static readonly Color Text = Hex(0xe9ecf3);
    public static readonly Color Text2 = Hex(0xb6bccc);
    public static readonly Color TextDim = Hex(0x969cad);       // --muted
    public static readonly Color Dim = Hex(0x697081);

    // ── Inverse = what "primary" looks like ──
    public static readonly Color Inv = Hex(0xe9ecf3);
    public static readonly Color InvHover = Hex(0xffffff);
    public static readonly Color InvInk = Hex(0x0b0d12);

    // ── Accent: go / alive only ──
    public static readonly Color Accent = Hex(0x22c55e);
    public static readonly Color AccentInk = Hex(0x04200d);

    // ── Status: dot colour, ink (text on panels) and soft fill (notes) ──
    public static readonly Color Valid = Hex(0x34d399);         // --ok
    public static readonly Color Online = Hex(0x60a5fa);
    public static readonly Color Warning = Hex(0xfbbf24);       // --warn
    public static readonly Color Expired = Hex(0xfb7185);       // --bad
    public static readonly Color Unknown = Hex(0x697081);       // --dim
    public static readonly Color OkInk = Hex(0xa7f3d0), OnlineInk = Hex(0xbfdbfe), WarnInk = Hex(0xfde68a), BadInk = Hex(0xfecdd3);
    public static readonly Color OkSoft = Hex(0x172f2f), OnlineSoft = Hex(0x1a2435), WarnSoft = Hex(0x302c20), BadSoft = Hex(0x30222c);
    public static readonly Color BadLine = Hex(0x6b3a47);

    // ── Typography (Segoe UI; px → pt at 96 dpi: 11→8.25, 12→9, 13→9.75, 14→10.5, 18→13.5, 28→21) ──
    public static readonly Font Base = new("Segoe UI", 9.75f);                  // body 13
    public static readonly Font Bold = new("Segoe UI Semibold", 9.75f);         // names, values
    public static readonly Font Small = new("Segoe UI", 9f);                    // row meta 12
    public static readonly Font Header = new("Segoe UI Semibold", 9f);          // label 12 / 600 (mixed case)
    public static readonly Font Fine = new("Segoe UI", 9f);                     // metadata 12 (never smaller for text people read)
    public static readonly Font Tiny = new("Segoe UI Semibold", 8.25f);         // count pills only (11)
    public static readonly Font Title = new("Segoe UI Semibold", 10.5f);        // card titles 14
    public static readonly Font Headline = new("Segoe UI Semibold", 13.5f);     // 18
    public static readonly Font Display = new("Segoe UI", 21f, FontStyle.Bold); // page title 28

    // ── Radii ──
    public const int RXs = 6, RSm = 9, RMd = 11, RLg = 14, RXl = 22;

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    /// <summary>a mixed toward b by t (0..1): for disabled states and soft fills on solid surfaces.</summary>
    public static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The colour actually behind a control (walks up past transparent parents).</summary>
    public static Color BackOf(Control? c)
    {
        for (; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Window;
    }

    public static void Dot(Graphics g, float cx, float cy, float size, Color color)
    {
        using var b = new SolidBrush(color);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(b, cx - size / 2, cy - size / 2, size, size);
    }

    /// <summary>
    /// Dark native scroll bars for a scrolling control and the scroll bars inside it (DataGridView, ListBox,
    /// AutoScroll panels, VScrollBar). Windows draws these; without this they stay light on the dark UI.
    /// </summary>
    public static T DarkScroll<T>(T control) where T : Control
    {
        void Apply(Control c)
        {
            Platform.Native.UseDarkControlTheme(c.Handle);
            foreach (Control child in c.Controls)
                if (child is ScrollBar && child.IsHandleCreated) Platform.Native.UseDarkControlTheme(child.Handle);
        }
        void Hook(ScrollBar sb) => sb.HandleCreated += (_, _) => Platform.Native.UseDarkControlTheme(sb.Handle);
        control.HandleCreated += (_, _) => Apply(control);
        // Child scroll bars get their handles after the parent's: theme each one when it is created.
        foreach (Control child in control.Controls) if (child is ScrollBar sb) Hook(sb);
        control.ControlAdded += (_, e) => { if (e.Control is ScrollBar sb) Hook(sb); };
        if (control.IsHandleCreated) Apply(control);
        return control;
    }

    /// <summary>Accessible name + tooltip for a control whose meaning is only an icon or a colour.</summary>
    public static T Named<T>(T control, string name, ToolTip? tip = null) where T : Control
    {
        control.AccessibleName = name;
        tip?.SetToolTip(control, name);
        return control;
    }

    // ── Factories (kept for existing call sites) ──

    /// <summary>Small button (30 px). <paramref name="primary"/>: the inverse look, one per surface.</summary>
    public static UiButton Button(string text, EventHandler onClick, bool primary = false) =>
        Button(text, onClick, primary ? ButtonKind.Primary : ButtonKind.Secondary);

    public static UiButton Button(string text, EventHandler onClick, ButtonKind kind, bool large = false)
    {
        var b = new UiButton { Text = text, Kind = kind, Large = large, Margin = new Padding(3) };
        b.Click += onClick;
        return b;
    }

    public static TextBox TextBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Field,
        ForeColor = Text,
        Font = Base,
    };

    /// <summary>Bloxkeep's menu: raised fill, 36 px items, small-radius hover, hairline groups, danger item in red ink.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new MenuRenderer();
        menu.BackColor = Raised;
        menu.ForeColor = Text2;
        menu.Font = Base;
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(6);
        menu.ItemAdded += (_, e) => Style(e.Item);
        foreach (ToolStripItem item in menu.Items) Style(item);

        static void Style(ToolStripItem? item)
        {
            if (item is ToolStripMenuItem mi)
            {
                mi.AutoSize = false;
                mi.Height = 36;
                mi.Width = 260;
                mi.Padding = new Padding(4, 0, 4, 0);
            }
        }
    }

    /// <summary>Marks a menu item as destructive (drawn in --bad-ink, red soft hover). Put it last.</summary>
    public static ToolStripItem Danger(this ToolStripItem item) { item.Tag = "danger"; return item; }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Raised);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Border);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2, 0, e.Item.Width - 4, e.Item.Height);
            using var path = Round(r, RSm);
            using var b = new SolidBrush(Equals(e.Item.Tag, "danger") ? BadSoft : Hover);
            g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool danger = Equals(e.Item.Tag, "danger");
            e.TextColor = !e.Item.Enabled ? Dim : danger ? BadInk : e.Item.Selected ? Text : Text2;
            e.TextRectangle = new Rectangle(e.TextRectangle.X + 6, e.TextRectangle.Y, e.TextRectangle.Width, e.TextRectangle.Height);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A small check on the current choice (no per-row decorative icons).
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var p = new Pen(Text, 1.75f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(p, [new PointF(r.X + 3, r.Y + r.Height / 2f), new PointF(r.X + r.Width / 2.5f, r.Bottom - 4), new PointF(r.Right - 3, r.Y + 4)]);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Border);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Raised;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Raised;
        public override Color MenuBorder => Border;
        public override Color ImageMarginGradientBegin => Raised;
        public override Color ImageMarginGradientMiddle => Raised;
        public override Color ImageMarginGradientEnd => Raised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
