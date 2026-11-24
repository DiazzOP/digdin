using System.Drawing.Drawing2D;
using RbxSwitcher.Core.Accounts;

namespace RbxSwitcher.App.UI;

/// <summary>
/// Owner-drawn account list with group headers. Drawing is per visible row only, so hundreds of
/// accounts scroll smoothly.
/// </summary>
internal sealed class AccountList : ListBox
{
    private sealed record GroupRow(string Name, int Count);

    private readonly Func<long, Image?> _avatar;
    private int _hover = -1;

    public long? ActiveUserId { get; set; }

    /// <summary>Raised on a left click (or Enter) on an account row.</summary>
    public event Action<Account>? AccountActivated;

    public AccountList(Func<long, Image?> avatar)
    {
        _avatar = avatar;
        DrawMode = DrawMode.OwnerDrawVariable;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        IntegralHeight = false;
    }

    public Account? SelectedAccount => SelectedItem as Account;

    public Account? AccountAt(Point p)
    {
        int i = IndexFromPoint(p);
        return i >= 0 && i < Items.Count ? Items[i] as Account : null;
    }

    public void SetAccounts(IReadOnlyList<Account> accounts, bool showGroups)
    {
        long? keep = SelectedAccount?.UserId;
        int top = TopIndex;
        BeginUpdate();
        Items.Clear();
        string? current = null;
        foreach (var a in accounts)
        {
            if (showGroups && !string.Equals(current, a.GroupOrDefault, StringComparison.OrdinalIgnoreCase))
            {
                current = a.GroupOrDefault;
                Items.Add(new GroupRow(current, accounts.Count(x => string.Equals(x.GroupOrDefault, current, StringComparison.OrdinalIgnoreCase))));
            }
            Items.Add(a);
        }
        if (keep is not null)
            for (int i = 0; i < Items.Count; i++)
                if (Items[i] is Account a && a.UserId == keep) { SelectedIndex = i; break; }
        if (top < Items.Count) TopIndex = top;
        EndUpdate();
    }

    public void RefreshAccount(long userId)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i] is Account a && a.UserId == userId)
                Invalidate(GetItemRectangle(i));
    }

    // Compact list (Bloxkeep's ".card.row" list adapted to a narrow sidebar): one thin row per account, a hairline
    // between rows, a small rounded-square avatar with a presence dot in its corner, the name with @user beside it,
    // the status as a coloured WORD (never a coloured background), the balance right-aligned. The account open in
    // the browser gets the --active step plus a 3 px inverse rail; hover is one tonal step.

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        float s = DeviceDpi / 96f;
        e.ItemHeight = (int)((e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is GroupRow ? 30 : 48) * s);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;
        var r = e.Bounds;
        int pad = (int)(12 * s);

        if (Items[e.Index] is GroupRow grp)
        {
            using (var bg = new SolidBrush(Theme.Sidebar)) g.FillRectangle(bg, r);
            // Mixed case, label size, muted; the count beside it (no uppercase + letter-spacing).
            TextRenderer.DrawText(g, $"{grp.Name}  {grp.Count}", Theme.Header,
                new Rectangle(r.X + pad, r.Y + (int)(6 * s), r.Width - 2 * pad, r.Height - (int)(6 * s)), Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        var a = (Account)Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool active = ActiveUserId == a.UserId;
        Color back = active ? Theme.Selected : selected || e.Index == _hover ? Theme.Hover : Theme.Sidebar;
        using (var bg = new SolidBrush(back)) g.FillRectangle(bg, r);
        using (var line = new Pen(Theme.Line)) g.DrawLine(line, r.X + pad, r.Bottom - 1, r.Right - pad, r.Bottom - 1);
        if (active)
            using (var rail = new SolidBrush(Theme.Inv)) g.FillRectangle(rail, r.X, r.Y, (int)(3 * s), r.Height);

        // Avatar: 30 px rounded square on a 1 px ring in the status colour (hairline when nothing to say).
        var (dot, ink, word) = StatusLook(a.Status);
        int size = (int)(30 * s);
        var ring = new RectangleF(r.X + pad, r.Y + (r.Height - size) / 2f, size, size);
        using (var ringPath = Theme.Round(ring, Theme.RSm * s))
        using (var rb = new SolidBrush(a.Status is SessionStatus.Valid or SessionStatus.Unknown ? Theme.Border : Theme.Mix(back, dot, .6f)))
            g.FillPath(rb, ringPath);
        var inner = RectangleF.Inflate(ring, -1.5f * s, -1.5f * s);
        using (var clip = Theme.Round(inner, (Theme.RSm - 1.5f) * s))
        {
            if (_avatar(a.UserId) is { } img)
            {
                var state = g.Save();
                g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, inner);
                g.Restore(state);
            }
            else
            {
                using var ph = new SolidBrush(Theme.SurfaceHover);
                g.FillPath(ph, clip);
                TextRenderer.DrawText(g, a.Username.Length > 0 ? a.Username[..1].ToUpperInvariant() : "?", Theme.Header, Rectangle.Round(inner), Theme.Text2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        // Presence dot: 10 px rounded square, 2 px border in the row colour, bottom-right of the avatar.
        float pd = 10 * s;
        var dotRect = new RectangleF(ring.Right - pd + 2 * s, ring.Bottom - pd + 2 * s, pd, pd);
        using (var border = Theme.Round(RectangleF.Inflate(dotRect, 2 * s, 2 * s), Theme.RXs * s))
        using (var bb = new SolidBrush(back)) g.FillPath(bb, border);
        using (var dp = Theme.Round(dotRect, (Theme.RXs - 2) * s))
        using (var db = new SolidBrush(dot)) g.FillPath(db, dp);

        int textX = (int)ring.Right + pad;
        int right = r.Right - pad;
        int staleW = 0;
        // Balance: right-aligned, so a column of numbers lines up.
        if (a.Finance?.Robux is { } robux)
        {
            string bal = robux.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
            bool fresh = a.Finance.RobuxAtUtc is { } at && DateTimeOffset.UtcNow - at <= RbxSwitcher.Core.Finance.QuotaCalculator.MaxAge
                         && !a.Finance.FailedReads.Contains(RbxSwitcher.Core.Finance.FinanceField.Robux);
            var bs = TextRenderer.MeasureText(g, bal, Theme.Bold);
            TextRenderer.DrawText(g, bal, Theme.Bold, new Rectangle(right - bs.Width, r.Y + (int)(7 * s), bs.Width, (int)(18 * s)),
                fresh ? Theme.Text2 : Theme.TextDim, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            right -= bs.Width + (int)(8 * s);
            // An old balance says so in words under the number (not only by a dimmer colour).
            if (!fresh && a.Finance.RobuxAtUtc is { } when)
            {
                string age = Age(DateTimeOffset.UtcNow - when);
                var ag = TextRenderer.MeasureText(g, age, Theme.Small);
                staleW = ag.Width + (int)(8 * s);
                TextRenderer.DrawText(g, age, Theme.Small, new Rectangle(r.Right - pad - ag.Width, r.Y + (int)(26 * s), ag.Width, (int)(18 * s)),
                    Theme.WarnInk, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
        }

        // Line 1: name (title weight) with @user beside it, smaller and muted, on the same baseline.
        int lineH = (int)(18 * s);
        int y1 = r.Y + (int)(7 * s);
        var name = a.Title;
        var ns = TextRenderer.MeasureText(g, name, Theme.Bold, Size.Empty, TextFormatFlags.NoPrefix);
        int nameW = Math.Min(ns.Width, Math.Max(20, right - textX));
        TextRenderer.DrawText(g, name, Theme.Bold, new Rectangle(textX, y1, nameW, lineH), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int userX = textX + nameW + (int)(6 * s);
        if (right - userX > 24 * s && !string.Equals(a.Title, a.Username, StringComparison.OrdinalIgnoreCase))
            TextRenderer.DrawText(g, "@" + a.Username, Theme.Small, new Rectangle(userX, y1 + (int)(1 * s), right - userX, lineH), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Line 2: the status word in its ink, then the Plus word when known.
        int y2 = r.Y + (int)(26 * s);
        string meta = word + (PlusWord(a) is { } plus ? "  ·  " + plus : "");
        var ws = TextRenderer.MeasureText(g, word, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix);
        int lineEnd = r.Right - pad - staleW;
        TextRenderer.DrawText(g, word, Theme.Small, new Rectangle(textX, y2, Math.Max(1, lineEnd - textX), lineH), ink,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (meta.Length > word.Length && textX + ws.Width < lineEnd)
            TextRenderer.DrawText(g, meta[word.Length..], Theme.Small, new Rectangle(textX + ws.Width, y2, lineEnd - textX - ws.Width, lineH), Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>"saldo de 3 h" — how old a balance is, shown when it is no longer fresh.</summary>
    private static string Age(TimeSpan t) =>
        "saldo de " + (t.TotalMinutes < 90 ? $"{Math.Max(1, (int)t.TotalMinutes)} min" : t.TotalHours < 36 ? $"{(int)Math.Round(t.TotalHours)} h" : $"{(int)t.TotalDays} d");

    private static string? PlusWord(Account a)
    {
        if (a.Finance is null) return null;
        var p = RbxSwitcher.Core.Finance.PlusStatus.Evaluate(a.Finance, DateTimeOffset.UtcNow);
        // "sem Plus", "Plus ativo", "Plus expirado · trial disponível" (never "Plus sem plus").
        return p.Label == RbxSwitcher.Core.Finance.PlusLabel.None
            ? "sem Plus" + (p.TrialAvailable ? " · trial disponível" : "")
            : "Plus " + Fmt.PlusText(p).ToLowerInvariant();
    }

    /// <summary>Dot colour, word ink and word for a session state (Bloxkeep's row-status vocabulary).</summary>
    public static (Color Dot, Color Ink, string Word) StatusLook(SessionStatus s) => s switch
    {
        SessionStatus.Valid => (Theme.Valid, Theme.OkInk, "Sessão ativa"),
        SessionStatus.Expired => (Theme.Expired, Theme.BadInk, "Precisa entrar"),
        SessionStatus.Unreachable => (Theme.Warning, Theme.WarnInk, "Sem resposta do Roblox"),
        SessionStatus.Checking => (Theme.Online, Theme.OnlineInk, "Verificando…"),
        _ => (Theme.Unknown, Theme.TextDim, "Não verificada"),
    };

    public static Color StatusColor(SessionStatus s) => StatusLook(s).Dot;

    public static string StatusText(SessionStatus s) => StatusLook(s).Word.ToLowerInvariant();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexFromPoint(e.Location);
        if (i != _hover)
        {
            int old = _hover;
            _hover = i;
            if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
            if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i));
        }
        Cursor = i >= 0 && i < Items.Count && Items[i] is Account ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        int old = _hover;
        _hover = -1;
        if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && i < Items.Count && Items[i] is GroupRow) return; // headers are not selectable
        if (e.Button == MouseButtons.Right && i >= 0) SelectedIndex = i;
        base.OnMouseDown(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && AccountAt(e.Location) is { } a)
            AccountActivated?.Invoke(a);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && SelectedAccount is { } a)
        {
            e.Handled = true;
            AccountActivated?.Invoke(a);
        }
    }
}
