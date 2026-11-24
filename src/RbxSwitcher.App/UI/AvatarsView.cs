using System.Drawing.Drawing2D;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Avatars;

namespace RbxSwitcher.App.UI;

/// <summary>
/// The "Avatares" category: pick hair, accessories, head, clothes and body from Roblox's free items and the
/// items the open account owns, choose a skin tone, randomize, save designs and apply them to the open account
/// or to many accounts. This view only edits a design; MainForm does every Roblox call.
/// </summary>
internal sealed class AvatarsView : Panel
{
    private const string SourceAll = "Tudo", SourceFree = "Grátis do Roblox", SourceOwned = "Da conta";

    private AvatarDesign _design = new();
    private AvatarCategory _category = AvatarCategory.Hair;
    private IReadOnlyList<AvatarItem> _free = [];
    private IReadOnlyList<AvatarItem> _owned = [];
    private HashSet<string> _ownedKeys = [];
    private Account? _account;
    private List<AvatarPreset> _presets = [];
    private bool _busy;

    // center
    private readonly Chip[] _categoryChips;
    private readonly FieldBox _searchBox = new("Buscar item…");
    private readonly DropdownButton _source = new("Mostrar");
    private readonly Chip _keep = new("Manter o atual"), _none = new("Nenhum");
    private readonly ItemGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly Label _catalogStatus = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 9, 8, 0) };
    private readonly UiButton _refreshCatalog;
    /// <summary>Shown above the grid when the free catalog did not load: why, in plain words, and a retry.</summary>
    private readonly NoteLabel _catalogNote = new() { Kind = NoteKind.Warn, Margin = new Padding(0, 0, 8, 0) };
    private readonly FlowLayoutPanel _catalogBar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Theme.Window, Padding = new Padding(0, 0, 0, 10), Visible = false };
    private bool _catalogLoading;

    // right column
    private readonly Label _who = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 4, 0, 0) };
    private readonly DropdownButton _accountPick = new("Conta");
    private List<(long Id, string Label)> _choices = [];
    private bool _settingChoices;
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Field, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label _footNote = new() { AutoSize = true, Font = Theme.Fine, ForeColor = Theme.TextDim, Margin = new Padding(0, 6, 0, 0),
        Text = "Itens grátis que a conta não tiver são pegos antes (0 Robux). Itens pagos que ela não tiver ficam de fora." };
    private readonly FlowLayoutPanel _footer = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(14, 12, 14, 14) };
    private readonly FlowLayoutPanel _scrollArea = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Sidebar, Padding = new Padding(14, 16, 10, 12) };
    private readonly FlowLayoutPanel _slots = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 8) };
    private readonly Segmented _type = new("Manter", "R15", "R6");
    private readonly SwatchRow _skin = new();
    private readonly Chip _skinLock = new("Travar tom");
    private readonly UiButton _apply, _applyMany, _randomize, _clear, _save;
    private readonly NoteLabel _result = new() { Kind = NoteKind.Info, Visible = false, Font = Theme.Small, Padding = new Padding(10, 7, 10, 7) };
    private readonly FieldBox _presetName = new("Nome do avatar");
    private readonly Label _presetsTitle = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 8) };
    private readonly FlowLayoutPanel _presetList = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
    private readonly ContextMenuStrip _presetMenu = new();
    private readonly CardStack _avatarCard, _presetsCard;
    private readonly ToolTip _tip = new() { InitialDelay = 300, AutoPopDelay = 15_000 };

    public Func<string, Image?> ThumbOf { get => _grid.ThumbOf; set => _grid.ThumbOf = value; }
    public event Action<IReadOnlyList<AvatarItem>>? NeedThumbs;
    public event Action<AvatarDesign>? ApplyActiveRequested;
    public event Action<AvatarDesign>? ApplyManyRequested;
    public event Action? CatalogRefreshRequested;
    public event Action<string, AvatarDesign>? PresetSaveRequested;
    public event Action<AvatarPreset>? PresetDeleteRequested;
    /// <summary>Open another account (it becomes the active one, as in Contas).</summary>
    public event Action<long>? AccountChosen;

    public AvatarDesign Design => _design;

    public AvatarsView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Window;

        // ── Center: categories, filters, grid ──
        _categoryChips = AvatarCategories.All.Select(def =>
        {
            var chip = new Chip(def.Name) { Margin = new Padding(0, 0, 4, 6) };
            chip.Click += (_, _) => SelectCategory(def.Category);
            return chip;
        }).ToArray();
        var cats = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Window, Padding = new Padding(0, 0, 0, 4) };
        cats.Controls.AddRange(_categoryChips);

        _searchBox.Width = LogicalToDeviceUnits(220);
        _searchBox.Margin = new Padding(0, 0, 8, 6);
        _searchBox.Box.TextChanged += (_, _) => RefreshGrid();
        _source.SetItems([SourceAll, SourceFree, SourceOwned]);
        _source.Margin = new Padding(0, 2, 12, 6);
        _source.SelectionChanged += RefreshGrid;
        _keep.Margin = new Padding(0, 2, 4, 6);
        _none.Margin = new Padding(0, 2, 4, 6);
        _keep.Outline = _none.Outline = true;
        _keep.Click += (_, _) => SetMode(SlotMode.Keep);
        _none.Click += (_, _) => SetMode(SlotMode.Empty);
        _tip.SetToolTip(_keep, "Esta categoria fica como está em cada conta.");
        _tip.SetToolTip(_none, "Tira tudo desta categoria do avatar.");
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Window };
        var modeCaption = new Label { Text = "Nesta categoria:", AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(4, 9, 6, 0) };
        filters.Controls.AddRange([_searchBox, _source, modeCaption, _keep, _none]);

        var retry = Theme.Button("Tentar de novo", (_, _) => CatalogRefreshRequested?.Invoke());
        retry.Margin = new Padding(0, 4, 0, 0);
        _catalogBar.Controls.AddRange([_catalogNote, retry]);

        _refreshCatalog = Theme.Button("Atualizar catálogo", (_, _) => CatalogRefreshRequested?.Invoke(), ButtonKind.Ghost);
        var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, BackColor = Theme.Window, Padding = new Padding(0, 6, 0, 0) };
        status.Controls.AddRange([_catalogStatus, _refreshCatalog]);

        // Tag only the exception (an item the account already has); "grátis" on every tile was noise.
        _grid.TagOf = i => _ownedKeys.Contains(i.Key) ? "da conta" : null;
        _grid.IsSelected = i => _design.Slot(_category) is { Mode: SlotMode.Items } s && s.Items.Any(r => r.Key == i.Key);
        _grid.ItemClicked += item =>
        {
            if (item.Category is not { } c) return;
            AvatarSelection.Toggle(_design.Slot(c), item);
            RefreshState();
        };
        _grid.NeedThumbs += items => NeedThumbs?.Invoke(items);

        var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 12, 12), BackColor = Theme.Window };
        center.Controls.AddRange([_grid, _catalogBar, status, filters, cats,
            new Label { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Text = "Avatares", Font = Theme.Headline, ForeColor = Theme.Text }]);

        // ── Right: the account, the selection, skin, actions, saved avatars ──
        _picture.Size = new Size(LogicalToDeviceUnits(60), LogicalToDeviceUnits(60));
        _picture.Paint += (_, e) =>
        {
            if (_picture.Image is not null) return;
            TextRenderer.DrawText(e.Graphics, "sem\nimagem", Theme.Fine, _picture.ClientRectangle, Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        };
        _picture.AccessibleName = "Avatar atual da conta";
        _type.SelectedIndexChanged += i => { _design.AvatarType = i switch { 1 => "R15", 2 => "R6", _ => null }; RefreshState(); };
        _skin.Changed += hex => { _design.SkinHex = hex; RefreshState(); };
        _skinLock.Click += (_, _) => { _design.SkinLocked = !_design.SkinLocked; RefreshState(); };
        _tip.SetToolTip(_skinLock, "\"Sortear\" não muda o tom de pele travado.");
        _randomize = Theme.Button("Sortear tudo", (_, _) => Randomize());
        _tip.SetToolTip(_randomize, "Sorteia um item em cada categoria não travada e um tom de pele.");
        _clear = Theme.Button("Limpar", (_, _) => { _design = new AvatarDesign(); RefreshState(); }, ButtonKind.Ghost);
        _tip.SetToolTip(_clear, "Volta tudo para \"manter o atual\".");
        _apply = Theme.Button("Aplicar na conta aberta", (_, _) => ApplyActiveRequested?.Invoke(_design.Clone()), ButtonKind.Primary, large: true);
        _applyMany = Theme.Button("Aplicar em várias contas…", (_, _) => ApplyManyRequested?.Invoke(_design.Clone()));

        var actions = Row(_randomize, _clear);
        _avatarCard = new CardStack();
        _accountPick.Margin = new Padding(0, 0, 0, 10);
        _accountPick.SelectionChanged += () =>
        {
            if (_settingChoices) return;
            if (_choices.FirstOrDefault(c => c.Label == _accountPick.Selected) is { Id: > 0 } c) AccountChosen?.Invoke(c.Id);
        };
        _tip.SetToolTip(_accountPick, "Abre outra conta (a mesma que fica ativa na aba Contas).");
        // Header: the account's picture beside who it is (the picker names the account; no repeated title).
        var who = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
        _accountPick.Margin = Padding.Empty;
        who.Controls.AddRange([_accountPick, _who]);
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 10) };
        head.Controls.AddRange([_picture, who]);
        _skinLock.Outline = true;
        var skinHead = Row(Caption("Tom de pele"), _skinLock);
        _avatarCard.Controls.AddRange([head, Caption("Seleção"), _slots, Caption("Tipo de corpo"), _type, skinHead, _skin]);
        _type.Margin = new Padding(0, 0, 0, 8);
        _skin.Margin = new Padding(0, 0, 0, 4);
        _skinLock.Margin = new Padding(8, 0, 0, 0);
        // The actions stay at the bottom of the column, always visible (they fell below the screen at 1366×768).
        _apply.Margin = new Padding(0, 0, 0, 6);
        _applyMany.Margin = Padding.Empty;
        _result.Margin = new Padding(0, 8, 0, 0);
        actions.BackColor = Theme.Sidebar;
        actions.Margin = new Padding(0, 0, 0, 8);
        _footer.Controls.AddRange([actions, _apply, _applyMany, _footNote, _result]);
        _footer.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, _footer.Width, 0); };

        _save = Theme.Button("Salvar", (_, _) => SavePreset());
        _save.Margin = new Padding(6, 2, 0, 0);
        _presetName.Width = LogicalToDeviceUnits(170);
        _presetName.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SavePreset(); e.SuppressKeyPress = true; } };
        _presetsCard = new CardStack();
        _presetsCard.Controls.AddRange([_presetsTitle, Row(_presetName, _save), _presetList]);
        Theme.Apply(_presetMenu);

        Theme.DarkScroll(_scrollArea);
        _scrollArea.Controls.AddRange([_avatarCard, _presetsCard]);
        var right = new Panel { Dock = DockStyle.Right, Width = LogicalToDeviceUnits(330), BackColor = Theme.Sidebar, Padding = new Padding(1, 0, 0, 0) };
        right.Controls.AddRange([_scrollArea, _footer]);
        right.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, 0, right.Height); };
        right.Resize += (_, _) => SizeRight();

        Controls.AddRange([center, right]);
        SizeRight();
        SelectCategory(AvatarCategory.Hair);
        SetAccount(null, null, false);
        SetPresets([]);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Margin = new Padding(0, 4, 0, 4) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 4) };
        foreach (var c in controls) { if (c.Margin == new Padding(3)) c.Margin = new Padding(0, 0, 6, 0); }
        row.Controls.AddRange(controls);
        return row;
    }

    private void SizeRight()
    {
        int width = _scrollArea.ClientSize.Width - _scrollArea.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (var card in new[] { _avatarCard, _presetsCard }) card.MinimumSize = card.MaximumSize = new Size(Math.Max(1, width), 0);
        int inner = Math.Max(1, width - _avatarCard.Padding.Horizontal);
        foreach (var l in _avatarCard.Controls.OfType<Label>()) l.MaximumSize = new Size(inner, 0);
        int foot = Math.Max(1, _footer.ClientSize.Width - _footer.Padding.Horizontal);
        _apply.MinimumSize = _applyMany.MinimumSize = new Size(foot, 0);
        _footNote.MaximumSize = _result.MaximumSize = new Size(foot, 0);
        _slots.MinimumSize = _slots.MaximumSize = new Size(inner, 0);
        foreach (Control row in _slots.Controls) row.Width = inner;
        _presetName.Width = Math.Max(80, inner - _save.Width - 8);
        foreach (Control row in _presetList.Controls) row.Width = inner;
    }

    // ───────────── data in ─────────────

    /// <param name="problem">Why the free catalog is missing, in plain words (null when it loaded).</param>
    public void SetCatalog(IReadOnlyList<AvatarItem> free, string status, bool loading, string? problem = null)
    {
        _free = free;
        _catalogLoading = loading;
        _catalogStatus.Text = status;
        _refreshCatalog.Enabled = !loading;
        _catalogNote.Text = problem is null ? "" : "O catálogo grátis do Roblox não carregou: " + problem + ".";
        _catalogBar.Visible = problem is not null && !loading;
        RefreshGrid();
    }

    public void SetAccount(Account? account, IReadOnlyList<AvatarItem>? owned, bool loadingOwned)
    {
        _account = account;
        _owned = owned ?? [];
        _ownedKeys = _owned.Select(i => i.Key).ToHashSet();
        _who.Text = account is null ? "Nenhuma conta aberta" : loadingOwned ? "lendo os itens da conta…" : $"{_owned.Count} itens da conta";
        _apply.Text = account is null ? "Aplicar na conta aberta" : $"Aplicar em @{account.Username}";
        _apply.Enabled = account is not null && !_busy;
        if (account is null) _picture.Image = null;
        RefreshGrid();
    }

    public void SetAvatarImage(Image? image) => _picture.Image = image;

    public void SetAccountChoices(List<(long Id, string Label)> choices, long? active)
    {
        _settingChoices = true;
        try
        {
            // Without an open account the first entry is a placeholder, so any real account can be picked.
            string? current = choices.FirstOrDefault(c => c.Id == active).Label;
            _choices = choices;
            _accountPick.SetItems(current is null ? ["escolher…", .. choices.Select(c => c.Label)] : choices.Select(c => c.Label));
            _accountPick.Selected = current ?? "escolher…";
        }
        finally { _settingChoices = false; }
    }

    public void ThumbsLoaded() => _grid.Invalidate();

    public void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        _apply.Enabled = _account is not null && !busy;
        _applyMany.Enabled = !busy;
        if (text is not null) ShowResult(text, NoteKind.Info);
    }

    public void ShowResult(string text, NoteKind kind)
    {
        _result.Kind = kind;
        _result.Text = text;
        _result.Visible = text.Length > 0;
    }

    public void SetPresets(List<AvatarPreset> presets)
    {
        _presets = presets;
        _presetsTitle.Text = $"Avatares salvos ({presets.Count})";
        _presetList.SuspendLayout();
        foreach (Control c in _presetList.Controls) c.Dispose();
        _presetList.Controls.Clear();
        if (presets.Count == 0)
            _presetList.Controls.Add(new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextDim, Text = "Nenhum avatar salvo. Monte um e salve.", Margin = new Padding(0, 6, 0, 0) });
        foreach (var p in presets.OrderByDescending(p => p.SavedUtc))
        {
            var row = new PresetRow(p) { Width = _presetList.MinimumSize.Width > 0 ? _presetList.MinimumSize.Width : LogicalToDeviceUnits(260) };
            row.Click += (_, _) => Load(p);
            row.MenuRequested += pt =>
            {
                _presetMenu.Items.Clear();
                _presetMenu.Items.Add("Carregar no editor", null, (_, _) => Load(p));
                _presetMenu.Items.Add("Aplicar na conta aberta", null, (_, _) => { Load(p); ApplyActiveRequested?.Invoke(_design.Clone()); }).Enabled = _account is not null && !_busy;
                _presetMenu.Items.Add("Aplicar em várias contas…", null, (_, _) => { Load(p); ApplyManyRequested?.Invoke(_design.Clone()); }).Enabled = !_busy;
                _presetMenu.Items.Add(new ToolStripSeparator());
                _presetMenu.Items.Add("Excluir", null, (_, _) => PresetDeleteRequested?.Invoke(p)).Danger();
                _presetMenu.Show(row, pt);
            };
            _presetList.Controls.Add(row);
        }
        _presetList.ResumeLayout();
        SizeRight();
    }

    // ───────────── editing ─────────────

    private void Load(AvatarPreset p)
    {
        _design = p.Design.Clone();
        _presetName.Box.Text = p.Name;
        RefreshState();
    }

    private void SavePreset()
    {
        string name = _presetName.Box.Text.Trim();
        if (name.Length == 0) { _presetName.Box.Focus(); ShowResult("Dê um nome ao avatar para salvar.", NoteKind.Warn); return; }
        if (_design.ChangesNothing) { ShowResult("Escolha ao menos um item, um tom de pele ou um tipo de corpo antes de salvar.", NoteKind.Warn); return; }
        PresetSaveRequested?.Invoke(name, _design.Clone());
    }

    private void Randomize()
    {
        var pool = _free.Concat(_owned.Where(o => !_free.Any(f => f.Key == o.Key))).ToList();
        if (pool.Count == 0) { ShowResult("O catálogo ainda não foi carregado.", NoteKind.Warn); return; }
        _design = AvatarRandomizer.Randomize(_design, pool, Random.Shared);
        RefreshState();
    }

    private void SetMode(SlotMode mode)
    {
        var slot = _design.Slot(_category);
        slot.Mode = mode;
        slot.Items.Clear();
        RefreshState();
    }

    private void SelectCategory(AvatarCategory c)
    {
        _category = c;
        _searchBox.Box.Text = "";
        RefreshGrid();
    }

    /// <summary>Items of the current category for the chosen source, free entry preferred when both exist.</summary>
    private List<AvatarItem> Source(AvatarCategory? only)
    {
        IEnumerable<AvatarItem> items = _source.Selected switch
        {
            SourceFree => _free,
            SourceOwned => _owned.Select(o => _free.FirstOrDefault(f => f.Key == o.Key) ?? o),
            _ => _free.Concat(_owned.Where(o => !_free.Any(f => f.Key == o.Key))),
        };
        return items.Where(i => only is null || i.Category == only).ToList();
    }

    private void RefreshGrid()
    {
        foreach (var (chip, def) in _categoryChips.Zip(AvatarCategories.All))
        {
            chip.On = def.Category == _category;
            int count = Source(def.Category).Count;
            if (chip.Count != count) { chip.Count = count; chip.Resize_(); }
            chip.Invalidate();
        }
        string q = _searchBox.Box.Text.Trim();
        var items = Source(_category).Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => _ownedKeys.Contains(i.Key)).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _grid.EmptyText = _catalogLoading && _free.Count == 0 ? "Carregando o catálogo do Roblox…"
            : q.Length > 0 ? "Nenhum item com esse nome."
            : _free.Count == 0 ? "Nenhum item para mostrar enquanto o catálogo não carregar."
            : "Nenhum item nesta categoria.";
        _grid.Items = items;
        RefreshState();
    }

    private void RefreshState()
    {
        var slot = _design.Slot(_category);
        _keep.On = slot.Mode == SlotMode.Keep;
        _none.On = slot.Mode == SlotMode.Empty;
        _grid.Invalidate();

        _slots.SuspendLayout();
        while (_slots.Controls.Count < AvatarCategories.All.Count)
        {
            var def = AvatarCategories.All[_slots.Controls.Count];
            var row = new SlotRow(def.Name);
            row.Clicked += () => SelectCategory(def.Category);
            row.LockClicked += () => { _design.Slot(def.Category).Locked = !_design.Slot(def.Category).Locked; RefreshState(); };
            _slots.Controls.Add(row);
        }
        for (int i = 0; i < AvatarCategories.All.Count; i++)
        {
            var def = AvatarCategories.All[i];
            var s = _design.Slot(def.Category);
            string value = s.Mode switch
            {
                SlotMode.Empty => "nenhum",
                SlotMode.Items => string.Join(", ", s.Items.Select(r => r.Name)),
                _ => "manter o atual",
            };
            ((SlotRow)_slots.Controls[i]).Set(value, s.Mode == SlotMode.Items, s.Locked, def.Category == _category);
        }
        _slots.ResumeLayout();

        _type.SelectedIndex = _design.AvatarType switch { "R15" => 1, "R6" => 2, _ => 0 };
        _skin.Selected = _design.SkinHex;
        _skinLock.On = _design.SkinLocked;
    }

    // ───────────── small controls ─────────────

    /// <summary>"Cabelo — Wavy Middle Part   [cadeado]": click selects the category, the padlock locks it for "Sortear".</summary>
    private sealed class SlotRow : Control
    {
        private readonly string _name;
        private string _value = "";
        private bool _chosen, _locked, _current, _hover;
        public event Action? Clicked, LockClicked;

        public SlotRow(string name)
        {
            _name = name;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleDescription = "Enter abre a categoria; Espaço trava ou destrava para o sorteio";
            Height = (int)(30 * (DeviceDpi / 96f));
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
        }

        public void Set(string value, bool chosen, bool locked, bool current)
        {
            (_value, _chosen, _locked, _current) = (value, chosen, locked, current);
            AccessibleName = $"{_name}: {value}" + (locked ? ", travado" : "");
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Clicked?.Invoke(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { LockClicked?.Invoke(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        private Rectangle LockRect => new(Width - Height, 0, Height, Height);

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (LockRect.Contains(e.Location)) LockClicked?.Invoke(); else Clicked?.Invoke();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            if (_current || _hover)
            {
                using var path = Theme.Round(new RectangleF(0, 1, Width - 1, Height - 2), Theme.RSm * s);
                using var b = new SolidBrush(_current ? Theme.Selected : Theme.Hover);
                g.FillPath(b, path);
            }
            if (Focused && ShowFocusCues)
            {
                using var fp = Theme.Round(new RectangleF(1, 2, Width - 3, Height - 4), Theme.RSm * s);
                using var pen = new Pen(Theme.Focus, 2 * s);
                g.DrawPath(pen, fp);
            }
            int x = (int)(8 * s), nameW = (int)(96 * s);
            TextRenderer.DrawText(g, _name, Theme.Small, new Rectangle(x, 0, nameW, Height), Theme.Text2, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var vr = new Rectangle(x + nameW, 0, Math.Max(1, LockRect.X - x - nameW), Height);
            TextRenderer.DrawText(g, _value, Theme.Small, vr, _chosen ? Theme.Text : Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            DrawLock(g, LockRect, _locked, s);
        }

        private static void DrawLock(Graphics g, Rectangle r, bool locked, float s)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f + 2 * s;
            var body = new RectangleF(cx - 6 * s, cy - 3 * s, 12 * s, 9 * s);
            var ink = locked ? Theme.Text : Theme.TextDim;
            using var p = new Pen(ink, 1.5f * s);
            using (var path = Theme.Round(body, 2 * s))
            {
                if (locked) { using var b = new SolidBrush(ink); g.FillPath(b, path); }
                else g.DrawPath(p, path);
            }
            // Shackle: closed when locked, lifted to the side when not.
            float dx = locked ? 0 : 4 * s;
            g.DrawArc(p, cx - 4 * s + dx, cy - 10 * s, 8 * s, 10 * s, 180, 180);
            g.DrawLine(p, cx + 4 * s + dx, cy - 5 * s, cx + 4 * s + dx, cy - 3 * s);
            if (locked) g.DrawLine(p, cx - 4 * s, cy - 5 * s, cx - 4 * s, cy - 3 * s);
        }
    }

    /// <summary>Skin tone circles; the first one ("—") keeps the account's colours. Selected: inverse ring.</summary>
    private sealed class SwatchRow : Control
    {
        private string? _selected;
        public event Action<string?>? Changed;

        public SwatchRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.RadioButton;
            AccessibleName = "Tom de pele: manter";
            AccessibleDescription = "Setas para a esquerda e a direita escolhem o tom";
            float s = DeviceDpi / 96f;
            Size = new Size((int)((SkinTones.All.Count + 1) * 26 * s), (int)(30 * s)); // fits the 270 px card
            Cursor = Cursors.Hand;
            var tip = new ToolTip();
            MouseMove += (_, e) =>
            {
                int i = IndexAt(e.Location);
                string text = i == 0 ? "Manter o tom atual" : i > 0 ? SkinTones.All[i - 1].Name : "";
                if (tip.GetToolTip(this) != text) tip.SetToolTip(this, text);
            };
        }

        public string? Selected { get => _selected; set { _selected = value; Describe(); Invalidate(); } }

        private int Index => _selected is null ? 0 : SkinTones.All.ToList().FindIndex(t => t.Hex.Equals(_selected, StringComparison.OrdinalIgnoreCase)) + 1;

        private void Describe() => AccessibleName = "Tom de pele: " + (Index == 0 ? "manter" : SkinTones.All[Index - 1].Name);

        protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Left or Keys.Right)
            {
                int i = Math.Clamp(Index + (e.KeyCode == Keys.Right ? 1 : -1), 0, SkinTones.All.Count);
                _selected = i == 0 ? null : SkinTones.All[i - 1].Hex;
                Describe();
                Invalidate();
                Changed?.Invoke(_selected);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        private int IndexAt(Point p) => p.X / (int)(26 * (DeviceDpi / 96f)) is var i && i <= SkinTones.All.Count ? i : -1;

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i < 0) return;
            _selected = i == 0 ? null : SkinTones.All[i - 1].Hex;
            Invalidate();
            Changed?.Invoke(_selected);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f, cell = 26 * s, d = 19 * s;
            for (int i = 0; i <= SkinTones.All.Count; i++)
            {
                var r = new RectangleF(i * cell + (cell - d) / 2, (Height - d) / 2, d, d);
                bool on = i == 0 ? _selected is null : string.Equals(SkinTones.All[i - 1].Hex, _selected, StringComparison.OrdinalIgnoreCase);
                if (i == 0)
                {
                    using var p = new Pen(Theme.Dim, 1.5f * s);
                    g.DrawEllipse(p, r);
                    g.DrawLine(p, r.Left + 5 * s, r.Bottom - 5 * s, r.Right - 5 * s, r.Top + 5 * s);
                }
                else
                {
                    string hex = SkinTones.All[i - 1].Hex;
                    using var b = new SolidBrush(Color.FromArgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16)));
                    g.FillEllipse(b, r);
                }
                if (on)
                {
                    using var ring = new Pen(Focused && ShowFocusCues ? Theme.Focus : Theme.Inv, 2 * s) { DashStyle = Focused && ShowFocusCues ? DashStyle.Dot : DashStyle.Solid };
                    g.DrawEllipse(ring, RectangleF.Inflate(r, 3 * s, 3 * s));
                }
            }
        }
    }

    /// <summary>A saved avatar: name and how many categories it sets. Click loads it; right click: menu.</summary>
    private sealed class PresetRow : Control
    {
        private readonly AvatarPreset _preset;
        private bool _hover;
        public event Action<Point>? MenuRequested;

        public PresetRow(AvatarPreset preset)
        {
            _preset = preset;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Avatar salvo: " + preset.Name;
            AccessibleDescription = "Enter carrega no editor; tecla de menu abre as opções";
            Height = (int)(40 * (DeviceDpi / 96f));
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(e.Location);
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) { MenuRequested?.Invoke(new Point(8, Height)); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { _hover = true; Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _hover = false; Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.BackOf(Parent));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            if (_hover)
            {
                using var path = Theme.Round(new RectangleF(0, 1, Width - 1, Height - 2), Theme.RSm * s);
                using var b = new SolidBrush(Theme.Hover);
                g.FillPath(b, path);
            }
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            int x = (int)(8 * s);
            int set = _preset.Design.Slots.Values.Count(v => v.Mode != SlotMode.Keep);
            string meta = $"{set} categoria(s)" + (_preset.Design.SkinHex is not null ? " · tom de pele" : "") + $" · {Fmt.Day(_preset.SavedUtc)}";
            int h1 = TextRenderer.MeasureText("Ag", Theme.Bold).Height;
            int top = (Height - h1 - TextRenderer.MeasureText("Ag", Theme.Fine).Height) / 2;
            TextRenderer.DrawText(g, _preset.Name, Theme.Bold, new Rectangle(x, top, Width - 2 * x, h1), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, meta, Theme.Fine, new Rectangle(x, top + h1, Width - 2 * x, Height - top - h1), Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
