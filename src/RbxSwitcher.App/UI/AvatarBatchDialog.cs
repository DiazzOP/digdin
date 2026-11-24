using System.Drawing.Drawing2D;
using RbxSwitcher.App.Platform;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;

namespace RbxSwitcher.App.UI;

/// <summary>
/// "Aplicar em várias contas": pick accounts (search, group, mark all), choose the same avatar for all or a
/// random one for each, then watch each account's result. Runs one account at a time; Cancel stops after the
/// current account. Roblox calls are made by the runner MainForm passes in.
/// </summary>
internal sealed class AvatarBatchDialog : Form
{
    public delegate Task Runner(IReadOnlyList<(Account Account, AvatarDesign Design)> targets,
                                Action<Account, AvatarApplyResult?, string> progress, CancellationToken ct);

    private const string AllGroups = "Todos os grupos";
    private readonly IReadOnlyList<Account> _accounts;
    private readonly AvatarDesign _design;
    private readonly IReadOnlyList<AvatarItem> _freePool;
    private readonly Runner _run;
    private readonly HashSet<long> _checked = [];
    private readonly Dictionary<long, (Color Dot, string Word, string Detail)> _status = [];
    private readonly FieldBox _search = new("Buscar conta…");
    private readonly DropdownButton _groups = new("Grupo");
    private readonly Segmented _mode = new("Este avatar", "Sortear um por conta");
    private readonly AccountChecklist _list = new();
    private readonly Label _count = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 8, 0, 0) };
    private readonly NoteLabel _note = new() { Kind = NoteKind.Info, Font = Theme.Small, Padding = new Padding(10, 7, 10, 7) };
    private readonly UiButton _start, _close, _all, _nonePick;
    private CancellationTokenSource? _cts;
    private bool _running, _ran;

    public AvatarBatchDialog(IReadOnlyList<Account> accounts, IEnumerable<string> groups, AvatarDesign design,
                             IReadOnlyList<AvatarItem> freePool, Runner run, long? preselect)
    {
        _accounts = accounts;
        _design = design;
        _freePool = freePool;
        _run = run;
        Text = "Aplicar em várias contas";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Sidebar;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        Size = new Size(LogicalToDeviceUnits(620), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(480), LogicalToDeviceUnits(420));
        Padding = new Padding(16, 12, 16, 12);

        _groups.SetItems([AllGroups, .. groups]);
        _groups.SelectionChanged += Filter;
        _search.Width = LogicalToDeviceUnits(220);
        _search.Margin = new Padding(0, 0, 8, 6);
        _search.Box.TextChanged += (_, _) => Filter();
        _groups.Margin = new Padding(0, 2, 8, 6);
        _all = Theme.Button("Marcar visíveis", (_, _) => { foreach (var a in _list.Items) _checked.Add(a.UserId); Changed(); }, ButtonKind.Ghost);
        _nonePick = Theme.Button("Desmarcar", (_, _) => { _checked.Clear(); Changed(); }, ButtonKind.Ghost);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        top.Controls.AddRange([_search, _groups]);

        _mode.Margin = new Padding(0, 0, 0, 6);
        _mode.SelectedIndexChanged += _ => Changed();
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 0, 4) };
        modeRow.Controls.AddRange([new Label { Text = "Avatar:", AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.Small, Margin = new Padding(0, 7, 8, 0) }, _mode]);

        _list.Dock = DockStyle.Fill;
        _list.IsChecked = a => _checked.Contains(a.UserId);
        _list.StatusOf = a => _status.TryGetValue(a.UserId, out var st) ? st : null;
        _list.Toggled += a => { if (_running || _ran) return; if (!_checked.Remove(a.UserId)) _checked.Add(a.UserId); Changed(); };
        Theme.DarkScroll(_list);

        _start = Theme.Button("Aplicar", async (_, _) => await StartAsync(), ButtonKind.Primary);
        _close = Theme.Button("Cancelar", (_, _) => { if (_running) { _cts?.Cancel(); _close!.Enabled = false; _close.Text = "Cancelando…"; } else Close(); });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _start.Margin = new Padding(0, 0, 8, 0);
        buttons.Controls.AddRange([_start, _close, _count]);
        // Selection helpers sit with the count, under the list they act on.
        var marks = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _all.Margin = _nonePick.Margin = new Padding(0, 0, 4, 0);
        marks.Controls.AddRange([_all, _nonePick]);
        bottom.Controls.AddRange([marks, _note, buttons]);
        // The note wraps inside the dialog (it overflowed the right edge before).
        void FitNote() => _note.MaximumSize = new Size(Math.Max(1, ClientSize.Width - Padding.Horizontal - bottom.Padding.Horizontal - 2), 0);
        Resize += (_, _) => FitNote();
        Shown += (_, _) => FitNote();

        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        card.Controls.Add(_list);
        Controls.AddRange([card, bottom, modeRow, top]);
        CancelButton = _close;
        Filter();
        if (preselect is { } hint && accounts.Any(a => a.UserId == hint)) _checked.Add(hint);
        Changed();
        FormClosing += (_, e) => { if (_running) { e.Cancel = true; _cts?.Cancel(); } };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    private void Filter()
    {
        string q = _search.Box.Text.Trim();
        string? group = _groups.Selected is { } g && g != AllGroups ? g : null;
        _list.Items = _accounts.Where(a => (group is null || a.GroupOrDefault == group) &&
            (q.Length == 0 || a.Username.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void Changed()
    {
        _list.Invalidate();
        int n = _checked.Count;
        _start.Text = n == 0 ? "Aplicar" : $"Aplicar em {n} conta{(n == 1 ? "" : "s")}";
        _start.Enabled = n > 0 && !_running && !_ran;
        _count.Text = $"{n} de {_accounts.Count} marcadas";
        _list.AccessibleName = $"Contas, {n} marcadas";
        _note.Text = _mode.SelectedIndex == 1
            ? "Cada conta recebe uma combinação sorteada entre os itens grátis do Roblox. Categorias e tom de pele travados ficam como no editor."
              + (_freePool.Count == 0 ? " O catálogo grátis ainda não carregou." : "")
            : "Todas as contas recebem o avatar do editor. Itens grátis que faltarem são pegos (0 Robux); itens pagos que a conta não tiver ficam de fora.";
        if (_mode.SelectedIndex == 1 && _freePool.Count == 0) _start.Enabled = false;
    }

    private async Task StartAsync()
    {
        var targets = _accounts.Where(a => _checked.Contains(a.UserId))
            .Select(a => (a, _mode.SelectedIndex == 1 ? AvatarRandomizer.Randomize(_design, _freePool, Random.Shared) : _design.Clone()))
            .ToList();
        if (targets.Count == 0) return;
        _running = true;
        _search.Enabled = _groups.Enabled = _mode.Enabled = _all.Enabled = _nonePick.Enabled = false;
        _start.Enabled = false;
        _start.Text = "Aplicando…";
        _close.Text = "Cancelar";
        _list.Items = targets.Select(t => t.a).ToList();
        foreach (var (a, _) in targets) _status[a.UserId] = (Theme.Unknown, "na fila", "");
        _list.Invalidate();
        _cts = new CancellationTokenSource();
        int done = 0, ok = 0;
        try
        {
            await _run(targets, (a, r, step) =>
            {
                if (IsDisposed) return;
                if (r is null) _status[a.UserId] = (Theme.Online, "aplicando", step);
                else
                {
                    done++;
                    if (r.Done) ok++;
                    _status[a.UserId] = Look(r);
                    _count.Text = $"{done} de {targets.Count} · {ok} aplicadas";
                }
                _list.Invalidate();
            }, _cts.Token);
        }
        finally
        {
            _running = false;
            _ran = true;
            _cts.Dispose();
            _cts = null;
            if (!IsDisposed)
            {
                _start.Text = "Concluído";
                _close.Enabled = true;
                _close.Text = "Fechar";
                _note.Kind = ok == targets.Count ? NoteKind.Ok : NoteKind.Warn;
                _note.Text = $"{ok} de {targets.Count} conta(s) com o avatar aplicado. Passe o mouse numa linha para ver o detalhe.";
            }
        }
    }

    private static (Color, string, string) Look(AvatarApplyResult r)
    {
        string acquired = r.Acquired.Count > 0 ? $"{r.Acquired.Count} item(ns) grátis pego(s)" : "";
        string skipped = r.Skipped.Count > 0 ? "fora: " + string.Join("; ", r.Skipped.Select(s => $"{s.Name} ({s.Why})")) : "";
        string detail = string.Join(" · ", new[] { acquired, skipped, r.Problem ?? "" }.Where(x => x.Length > 0));
        return r.Status switch
        {
            ApplyStatus.Applied => (Theme.Valid, "aplicado", detail),
            ApplyStatus.Partial => (Theme.Warning, "aplicado em parte", detail),
            ApplyStatus.SessionExpired => (Theme.Expired, "precisa entrar", detail),
            ApplyStatus.WrongAccount => (Theme.Expired, "sessão de outra conta", detail),
            ApplyStatus.RateLimited => (Theme.Warning, "limite do Roblox", detail),
            ApplyStatus.Cancelled => (Theme.Unknown, "não aplicado", detail),
            _ => (Theme.Expired, "falhou", detail),
        };
    }

    /// <summary>Rows with a checkbox, name and @user; during the run, the status dot + word + detail.</summary>
    private sealed class AccountChecklist : Control
    {
        private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };
        private readonly ToolTip _tip = new() { InitialDelay = 200, AutoPopDelay = 30_000 };
        private IReadOnlyList<Account> _items = [];
        private int _hover = -1, _focus = -1;
        public Func<Account, bool> IsChecked { get; set; } = _ => false;
        public Func<Account, (Color Dot, string Word, string Detail)?> StatusOf { get; set; } = _ => null;
        public event Action<Account>? Toggled;

        public AccountChecklist()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Controls.Add(_scroll);
            _scroll.ValueChanged += (_, _) => Invalidate();
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        // Keyboard: arrows move, Space marks/unmarks.
        protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_items.Count > 0 && e.KeyCode is Keys.Up or Keys.Down)
            {
                _focus = Math.Clamp(_focus + (e.KeyCode == Keys.Down ? 1 : -1), 0, _items.Count - 1);
                int y = _focus * RowH;
                int max = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
                if (y < _scroll.Value) _scroll.Value = Math.Clamp(y, 0, max);
                else if (y + RowH > _scroll.Value + ClientSize.Height) _scroll.Value = Math.Clamp(y + RowH - ClientSize.Height, 0, max);
                AccessibleName = "@" + _items[_focus].Username + (IsChecked(_items[_focus]) ? ", marcada" : ", não marcada");
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Space && _focus >= 0 && _focus < _items.Count)
            {
                Toggled?.Invoke(_items[_focus]);
                AccessibleName = "@" + _items[_focus].Username + (IsChecked(_items[_focus]) ? ", marcada" : ", não marcada");
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { if (_focus < 0 && _items.Count > 0) _focus = 0; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        public IReadOnlyList<Account> Items { get => _items; set { _items = value; _scroll.Value = 0; Relayout(); Invalidate(); } }
        private int RowH => (int)(44 * (DeviceDpi / 96f));

        private void Relayout()
        {
            int content = _items.Count * RowH;
            _scroll.LargeChange = Math.Max(1, ClientSize.Height);
            _scroll.Maximum = Math.Max(0, content - 1);
            _scroll.Enabled = content > ClientSize.Height;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!_scroll.Enabled) return;
            _scroll.Value = Math.Clamp(_scroll.Value - Math.Sign(e.Delta) * RowH * 2, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
        }

        private int IndexAt(Point p) => p.Y < 0 || p.X > ClientSize.Width - _scroll.Width ? -1 : (p.Y + _scroll.Value) / RowH is var i && i < _items.Count ? i : -1;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != _hover)
            {
                _hover = i;
                Invalidate();
                _tip.SetToolTip(this, i >= 0 && StatusOf(_items[i]) is { Detail.Length: > 0 } st ? st.Detail : null);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { if (IndexAt(e.Location) is var i and >= 0) Toggled?.Invoke(_items[i]); base.OnMouseClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "Nenhuma conta com sessão salva corresponde à busca.", Theme.Base, ClientRectangle, Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int width = ClientSize.Width - (_scroll.Enabled ? _scroll.Width : 0);
            int first = _scroll.Value / RowH, last = Math.Min(_items.Count - 1, (_scroll.Value + ClientSize.Height) / RowH);
            for (int i = first; i <= last; i++)
            {
                var a = _items[i];
                var r = new Rectangle(0, i * RowH - _scroll.Value, width, RowH);
                if (i == _hover) { using var hb = new SolidBrush(Theme.Hover); g.FillRectangle(hb, r); }
                if (i == _focus && Focused && ShowFocusCues)
                {
                    using var fp = new Pen(Theme.Focus, 2 * s) { DashStyle = DashStyle.Dot };
                    g.DrawRectangle(fp, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                }
                using (var p = new Pen(Theme.Line)) g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
                int x = (int)(12 * s);
                var status = StatusOf(a);
                if (status is null)
                {
                    var box = new RectangleF(x, r.Y + (RowH - 16 * s) / 2, 16 * s, 16 * s);
                    using var bp = Theme.Round(box, 4 * s);
                    if (IsChecked(a))
                    {
                        using (var b = new SolidBrush(Theme.Inv)) g.FillPath(b, bp);
                        using var ck = new Pen(Theme.InvInk, 1.75f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLines(ck, [new PointF(box.X + 4 * s, box.Y + 8 * s), new PointF(box.X + 7 * s, box.Y + 11 * s), new PointF(box.X + 12 * s, box.Y + 5 * s)]);
                    }
                    else { using var p = new Pen(Theme.Mix(Theme.Card, Theme.LineField, .85f), 1.5f * s); g.DrawPath(p, bp); } // ≥ 3:1
                }
                else Theme.Dot(g, x + 8 * s, r.Y + RowH / 2f, 7 * s, status.Value.Dot);
                x += (int)(28 * s);
                int h1 = TextRenderer.MeasureText("Ag", Theme.Bold).Height, h2 = TextRenderer.MeasureText("Ag", Theme.Small).Height;
                int top = r.Y + (RowH - h1 - h2) / 2;
                int nameW = Math.Max(1, width / 2 - x);
                TextRenderer.DrawText(g, a.Title, Theme.Bold, new Rectangle(x, top, nameW, h1), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "@" + a.Username, Theme.Small, new Rectangle(x, top + h1, nameW, h2), Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (status is { } st)
                {
                    var sr = new Rectangle(width / 2, top, width / 2 - (int)(10 * s), h1);
                    TextRenderer.DrawText(g, st.Word, Theme.Base, sr, Theme.Text2, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, st.Detail, Theme.Small, new Rectangle(sr.X, top + h1, sr.Width, h2), Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                else
                {
                    TextRenderer.DrawText(g, a.GroupOrDefault, Theme.Small, new Rectangle(width / 2, r.Y, width / 2 - (int)(10 * s), RowH), Theme.TextDim,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
    }
}
