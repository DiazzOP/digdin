using System.Diagnostics;
using RbxSwitcher.App.Browser;
using RbxSwitcher.App.Platform;
using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Finance;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;
using RbxSwitcher.Core.Security;
using RbxSwitcher.Core.Sessions;
using RbxSwitcher.Core.Storage;

namespace RbxSwitcher.App;

internal sealed partial class MainForm : Form
{
    private const string AllGroups = "Todos os grupos";
    private static readonly TimeSpan FinanceRefreshInterval = TimeSpan.FromMinutes(25);

    private readonly string _dataDir;
    private readonly IKeyProtector _protector;
    private readonly RobloxApi _api = new();
    private readonly SessionChecker _checker;
    private readonly FinanceRefresher _financeRefresher;
    private readonly AccountSummary _summaryCard = new() { Visible = false };
    private readonly PlusView _plusView = new();
    private readonly PlusAccountsView _plusPage;
    private readonly Panel _contasPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Window };
    /// <summary>"Contas | Plus": a segmented control (one choice among views), not two coloured buttons.</summary>
    private readonly Segmented _tabs = new("Contas", "Plus", "Avatares", "Distribuir", "Limiteds", "Calendário");
    private bool _diagnosing;
    private (long UserId, string Text)? _plusDiagnostic;
    private readonly HashSet<long> _financeBusy = new();
    private readonly Updater _updater = new();
    private RbxSwitcher.Core.Updates.ReleaseInfo? _pendingRelease;
    private CancellationTokenSource? _financeCts;
    private readonly AvatarCache _avatars;
    private readonly BrowserHost _browser;
    private VaultFile? _vault;
    private long? _expectedSignIn;
    private CancellationTokenSource? _checkCts;
    private bool _checking, _closing;

    // sidebar
    private readonly FieldBox _searchBox = new("Buscar conta…");
    private TextBox _search => _searchBox.Box;
    /// <summary>Group filter: the app's own select (a menu with a check on the current choice), not the OS popup.</summary>
    private readonly DropdownButton _groups = new("Grupo");
    private readonly AccountList _list;
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Theme.TextDim, Font = Theme.Small };
    private readonly ContextMenuStrip _accountMenu = new();

    // browser side
    private readonly FieldBox _addressBox = new("Endereço");
    private TextBox _address => _addressBox.Box;
    private readonly ToolTip _tips = new() { InitialDelay = 300 };
    private readonly Label _current = new() { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Bold, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 8, 0) };
    private readonly Button _back, _forward, _reload, _closeSignIn;
    private readonly Panel _browserPanel = new() { Dock = DockStyle.Fill, BackColor = Theme.Window };
    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.TextDim,
        Font = new Font("Segoe UI", 11f),
        Text = "Escolha uma conta na lista à esquerda, ou clique em \"+ Adicionar conta\".",
    };
    private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Height = 30, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), BackColor = Theme.WarnSoft, ForeColor = Theme.WarnInk };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), ForeColor = Theme.TextDim, BackColor = Theme.Sidebar, Font = Theme.Small };
    private readonly ContextMenuStrip _settingsMenu = new();

    // lock screen
    private readonly Panel _lockPanel = new() { Dock = DockStyle.Fill, BackColor = Theme.Window, Visible = false };
    private readonly TextBox _lockPassword = Theme.TextBox("Senha mestra");
    private UiButton _lockButton = null!;
    private void UpdateLockButton() => _lockButton.Visible = _vault?.HasPassword == true;
    private readonly Label _lockError = new() { AutoSize = true, ForeColor = Theme.Expired };

    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 15_000 };
    private readonly System.Windows.Forms.Timer _recheckTimer = new() { Interval = 60_000 };
    /// <summary>Recomputes estimates and checks whether any accounts are due for a new read.</summary>
    private readonly System.Windows.Forms.Timer _financeTimer = new() { Interval = 30_000 };
    private DateTimeOffset _nextAutomaticFinanceScan;
    private DateTime _lastFullCheck = DateTime.MinValue;

    public MainForm(VaultFile vault, IKeyProtector protector, string dataDir)
    {
        _vault = vault;
        _protector = protector;
        _dataDir = dataDir;
        _checker = new SessionChecker(_api);
        _financeRefresher = new FinanceRefresher(_api);
        _avatars = new AvatarCache(_api);
        _list = Theme.DarkScroll(new AccountList(_avatars.Get) { Dock = DockStyle.Fill });
        _plusPage = new PlusAccountsView(_plusView) { Visible = false };
        _browser = new BrowserHost(_browserPanel, Path.Combine(dataDir, "WebView2"), vault.Settings.LiveSessions);

        Text = "RbxSwitcher — gerenciador de contas";
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Base;
        Size = new Size(1320, 840);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _back = Theme.Named(new UiButton { Kind = ButtonKind.Ghost, Icon = UiIcon.Back, Margin = new Padding(2) }, "Voltar", _tips);
        _back.Click += (_, _) => _browser.Active?.GoBack();
        _forward = Theme.Named(new UiButton { Kind = ButtonKind.Ghost, Icon = UiIcon.Forward, Margin = new Padding(2) }, "Avançar", _tips);
        _forward.Click += (_, _) => _browser.Active?.GoForward();
        _reload = Theme.Named(new UiButton { Kind = ButtonKind.Ghost, Icon = UiIcon.Refresh, Margin = new Padding(2) }, "Recarregar (F5)", _tips);
        _reload.Click += (_, _) => _browser.Active?.Reload();
        _closeSignIn = Theme.Button("Cancelar login", (_, _) => { _browser.CloseSignIn(); HideBanner(); });
        ((UiButton)_closeSignIn).Icon = UiIcon.Close; // Button field typed as Button

        BuildLayout();
        BuildMenus();
        WireEvents();
        RefreshList();
        UpdateToolbar();
    }

    // ───────────────────────────── layout ─────────────────────────────

    private void BuildLayout()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(330), BackColor = Theme.Sidebar };
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 4, 10, 6), BackColor = Theme.Sidebar };
        _searchBox.Dock = DockStyle.Fill;
        _searchBox.Margin = new Padding(0, 0, 0, 6);
        top.Controls.Add(_searchBox);
        _groups.Margin = new Padding(0);
        top.Controls.Add(_groups);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8), BackColor = Theme.Sidebar };
        bottom.Controls.Add(Theme.Button("+ Adicionar conta", async (_, _) => await StartSignInAsync(null), primary: true));
        bottom.Controls.Add(Theme.Button("Verificar sessões", async (_, _) => await CheckAllAsync()));
        _summary.Margin = new Padding(4, 8, 0, 0);
        bottom.Controls.Add(_summary);
        bottom.SetFlowBreak(bottom.Controls[1], true);

        sidebar.Controls.Add(_list);
        sidebar.Controls.Add(top);
        sidebar.Controls.Add(_summaryCard);
        sidebar.Controls.Add(bottom);

        var categories = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = Theme.Sidebar };
        _tabs.Margin = new Padding(0, 3, 0, 0);
        _tabs.SelectedIndexChanged += ShowView;
        categories.Controls.Add(_tabs);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 1, BackColor = Theme.Sidebar, Padding = new Padding(8, 5, 8, 5),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(categories, 0, 0);
        // Only while a master password is set (without one there is nothing to lock).
        _lockButton = Theme.Button("Bloquear", async (_, _) => await LockAsync(), ButtonKind.Ghost);
        header.Controls.Add(_lockButton, 1, 0);
        var settings = Theme.Button("Configurações", (_, _) => { }, ButtonKind.Ghost);
        settings.Icon = UiIcon.ChevronDown;
        settings.IconAfterText = true;
        settings.Click += (_, _) => _settingsMenu.Show(settings, new Point(0, settings.Height + 2));
        header.Controls.Add(settings, 2, 0);

        var edge = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Line };

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(46), ColumnCount = 10, RowCount = 1, BackColor = Theme.Sidebar, Padding = new Padding(6, 6, 6, 4) };
        for (int i = 0; i < toolbar.ColumnCount; i++) toolbar.ColumnStyles.Add(new ColumnStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
        _addressBox.Dock = DockStyle.Fill;
        _addressBox.Margin = new Padding(6, 2, 6, 0);
        toolbar.Controls.Add(_back, 0, 0);
        toolbar.Controls.Add(_forward, 1, 0);
        toolbar.Controls.Add(_reload, 2, 0);
        // Shortcuts are quiet (ghost): the address and the page are what matter here.
        toolbar.Controls.Add(Theme.Button("Início", (_, _) => Go("https://www.roblox.com/home"), ButtonKind.Ghost), 3, 0);
        toolbar.Controls.Add(_addressBox, 4, 0);
        toolbar.Controls.Add(Theme.Button("Perfil", (_, _) => GoUser("profile"), ButtonKind.Ghost), 5, 0);
        toolbar.Controls.Add(Theme.Button("Inventário", (_, _) => GoUser("inventory"), ButtonKind.Ghost), 6, 0);
        toolbar.Controls.Add(Theme.Button("Trades", (_, _) => Go("https://www.roblox.com/trades"), ButtonKind.Ghost), 7, 0);
        toolbar.Controls.Add(_current, 8, 0);
        toolbar.Controls.Add(_closeSignIn, 9, 0);

        _browserPanel.Controls.Add(_empty);
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        right.Controls.Add(_browserPanel);
        right.Controls.Add(toolbar);

        _contasPage.Controls.Add(right);
        _contasPage.Controls.Add(edge);
        _contasPage.Controls.Add(sidebar);
        Controls.Add(_plusPage);
        Controls.Add(_avatarsPage);
        Controls.Add(_distributePage);
        Controls.Add(_sellPage);
        Controls.Add(_calendarPage);
        Controls.Add(_contasPage);
        Controls.Add(_banner);
        _banner.Click += (_, _) => _bannerAction?.Invoke();
        Controls.Add(header);
        Controls.Add(_status);
        BuildLockPanel();
        Controls.Add(_lockPanel);
        _lockPanel.BringToFront();
        ShowCategory(plus: false);
    }

    private void BuildLockPanel()
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Window };
        box.Controls.Add(new Label { Text = "Bloqueado", Font = Theme.Headline, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 0, 0, 12) });
        box.Controls.Add(new Label { Text = "Digite a senha mestra para desbloquear suas contas.", AutoSize = true, ForeColor = Theme.TextDim });
        _lockPassword.UseSystemPasswordChar = true;
        _lockPassword.Width = LogicalToDeviceUnits(320);
        _lockPassword.Margin = new Padding(0, 10, 0, 6);
        box.Controls.Add(_lockPassword);
        box.Controls.Add(_lockError);
        box.Controls.Add(Theme.Button("Desbloquear", async (_, _) => await UnlockAsync(), primary: true));
        _lockPassword.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await UnlockAsync(); } };
        _lockPanel.Controls.Add(box);
        _lockPanel.Resize += (_, _) => box.Location = new Point((_lockPanel.Width - box.Width) / 2, (_lockPanel.Height - box.Height) / 2 - 40);
    }

    private void BuildMenus()
    {
        Theme.Apply(_accountMenu);
        _accountMenu.Opening += (_, e) =>
        {
            _accountMenu.Items.Clear();
            if (_vault is null || _list.SelectedAccount is not { } a) { e.Cancel = true; return; }
            _accountMenu.Items.Add("Abrir", null, async (_, _) => await SwitchToAsync(a));
            _accountMenu.Items.Add("Entrar de novo (renovar sessão)", null, async (_, _) => await StartSignInAsync(a));
            _accountMenu.Items.Add("Atualizar saldo e limites", null, async (_, _) => await RefreshFinanceAsync(a));
            // Full sync: starts at the newest page, ignoring the saved history and any saved cursor.
            _accountMenu.Items.Add("Reler histórico completo (30 dias)", null, async (_, _) => await RefreshFinanceAsync(a, fullHistory: true));
            _accountMenu.Items.Add(new ToolStripSeparator());
            _accountMenu.Items.Add("Apelido…", null, (_, _) => EditAlias(a));
            _accountMenu.Items.Add("Grupo…", null, (_, _) => EditGroup(a));
            _accountMenu.Items.Add("Copiar nome de usuário", null, (_, _) => Clipboard.SetText(a.Username));
            _accountMenu.Items.Add(new ToolStripSeparator());
            _accountMenu.Items.Add("Remover…", null, async (_, _) => await RemoveAsync(a)).Danger();
        };
        _list.ContextMenuStrip = _accountMenu;

        Theme.Apply(_settingsMenu);
        _settingsMenu.Opening += (_, e) =>
        {
            _settingsMenu.Items.Clear();
            if (_vault is null) { e.Cancel = true; return; }
            var s = _vault.Settings;
            // The master password is only there to be removed (the vault stays encrypted by Windows without it).
            if (_vault.HasPassword) _settingsMenu.Items.Add("Remover senha mestra…", null, (_, _) => RemovePassword());
            _settingsMenu.Items.Add($"Contas abertas ao mesmo tempo: {s.LiveSessions}…", null, (_, _) => EditNumber("Contas abertas", "Quantas contas ficam abertas em memória para troca instantânea (1–20). Mais contas = mais memória.", s.LiveSessions, 1, 20, v => { s.LiveSessions = v; _browser.LiveSessions = v; }));
            _settingsMenu.Items.Add($"Verificar sessões a cada: {s.RecheckMinutes} min…", null, (_, _) => EditNumber("Verificação", "Intervalo, em minutos, entre verificações automáticas das sessões (5–1440):", s.RecheckMinutes, 5, 1440, v => s.RecheckMinutes = v));
            _settingsMenu.Items.Add("Atualizar saldos de todas as contas", null, async (_, _) => await RefreshAllFinanceAsync());
            _settingsMenu.Items.Add("Distribuição de Limiteds…", null, (_, _) => EditDistribution());
            _settingsMenu.Items.Add(new ToolStripSeparator());
            _settingsMenu.Items.Add($"Atualizações: {(s.UpdateRepo ?? "desligadas")}…", null, (_, _) => EditUpdateRepo());
            if (s.UpdateRepo is not null)
                _settingsMenu.Items.Add("Procurar atualização agora", null, async (_, _) => await CheckForUpdateAsync(manual: true));
            if (_pendingRelease is { } rel)
                _settingsMenu.Items.Add($"Instalar versão {rel.Tag} agora", null, async (_, _) => await InstallUpdateAsync(rel));
            _settingsMenu.Items.Add("Abrir pasta de dados", null, (_, _) => Process.Start(new ProcessStartInfo(_dataDir) { UseShellExecute = true }));
            _settingsMenu.Items.Add("Como suas contas são protegidas…", null, (_, _) => ShowSecurityInfo());
        };
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => RefreshList();
        _groups.SelectionChanged += RefreshList;
        _list.AccountActivated += async a => await SwitchToAsync(a);
        _avatars.Loaded += id => _list.RefreshAccount(id);
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            string t = _address.Text.Trim();
            if (t.StartsWith('/')) t = "https://www.roblox.com" + t;
            else if (!t.Contains("://")) t = "https://" + t;
            if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && SessionTab.IsRobloxHost(u.Host))
                Go(u.AbsoluteUri);
            else
                SetStatus("Este navegador abre apenas páginas do roblox.com.");
        };
        _browser.TabCreated += tab =>
        {
            tab.SessionAppeared += async (t, s) => await OnSignedInAsync(t, s);
            tab.SessionChanged += async (t, s) => await OnSessionChangedAsync(t, s);
            tab.SessionVanished += async t => await OnSessionVanishedAsync(t);
            tab.SignOutBlocked += _ => ShowBanner("“Sair” pelo site foi bloqueado: encerraria a sessão salva desta conta. Para tirar a conta do app, use Remover (botão direito na lista).");
            tab.NavigationChanged += t => { if (t == _browser.Active) UpdateToolbar(); };
            tab.PlayerLaunchRequested += OnPlayerLaunchRequested;
            tab.TradeSignalReceived += (t, signal) => _ = OnTradeSignalAsync(t, signal);
            tab.TrialSignal += (t, eligible) =>
            {
                if (t.UserId is not { } id || _vault?.Accounts.Find(id) is not { } acc) return;
                acc.Finance ??= new RbxSwitcher.Core.Finance.AccountFinance();
                bool changed = acc.Finance.TrialEligibleFromPage != eligible;
                acc.Finance.TrialEligibleFromPage = eligible;
                acc.Finance.TrialFromPageUtc = DateTimeOffset.UtcNow;
                RequestSave();
                UpdateFinancePanel();
                if (changed) SetStatus($"@{acc.Username}: teste grátis {(eligible ? "disponível" : "indisponível")} segundo a página oficial do Plus.");
            };
            tab.PlusPageOpened += async t =>
            {
                if (t.UserId is { } id && _vault?.Accounts.Find(id) is { } acc) await RecordOfficialCallsAsync(acc, 20, automatic: true);
            };
        };
        _browser.ActiveChanged += UpdateToolbar;
        _browser.ActiveChanged += () => { if (_avatarsPage.Visible) _ = ShowAvatarsAsync(); };
        WireAvatars();
        WireLimiteds();
        WireDistribute();
        WireSelling();
        WireCalendar();
        _summaryCard.PlusClicked += () =>
        {
            ShowCategory(plus: true);
            if (ActiveAccount() is { } account) _plusPage.OpenAccount(account.UserId);
        };
        _summaryCard.RefreshRequested += async () => { if (ActiveAccount() is { } a) await RefreshFinanceAsync(a); };
        _plusPage.FiltersChanged += UpdateFinancePanel;
        _plusPage.DetailChanged += UpdatePlusDetails;
        _plusPage.RefreshAllRequested += async () => await RefreshAllFinanceAsync();
        _summaryCard.LimitDropAcknowledged += () => AcknowledgeLimitDrop(ActiveAccount());
        _plusView.LimitDropAcknowledged += () => AcknowledgeLimitDrop(PlusAccount(_plusPage.DetailId));
        _plusView.RefreshRequested += async () => { if (PlusAccount(_plusPage.DetailId) is { } a) await RefreshFinanceAsync(a); };
        _plusView.FullHistoryRequested += async () => { if (PlusAccount(_plusPage.DetailId) is { } a) await RefreshFinanceAsync(a, fullHistory: true); };
        _plusView.DiagnosticRequested += async () => { if (PlusAccount(_plusPage.DetailId) is { } a) await RunPlusDiagnosticAsync(a); };
        _plusView.RecordRequested += async () =>
        {
            if (PlusAccount(_plusPage.DetailId) is not { } account) return;
            ShowCategory(plus: false);
            if (ActiveAccount()?.UserId != account.UserId) await SwitchToAsync(account);
            if (ActiveAccount()?.UserId == account.UserId) await RecordOfficialCallsAsync(account, 60, automatic: false);
        };
        _financeTimer.Tick += (_, _) =>
        {
            UpdateFinancePanel();
            StartAutomaticFinanceRefresh();
            StartAutomaticSellRefresh();
        };

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _idleTimer.Tick += async (_, _) =>
        {
            UpdateLockButton();
            if (_vault is { HasPassword: true, Settings.AutoLockMinutes: > 0 } v && Native.IdleTime() >= TimeSpan.FromMinutes(v.Settings.AutoLockMinutes))
                await LockAsync();
        };
        _recheckTimer.Tick += async (_, _) =>
        {
            if (_vault is { } v && DateTime.UtcNow - _lastFullCheck >= TimeSpan.FromMinutes(v.Settings.RecheckMinutes))
                await CheckAllAsync();
        };
        KeyDown += async (_, e) =>
        {
            if (_lockPanel.Visible) return;
            if (_plusPage.Visible)
            {
                if (e.KeyCode == Keys.F5 && PlusAccount(_plusPage.SelectedId) is { } account)
                {
                    e.Handled = true;
                    await RefreshFinanceAsync(account);
                }
                return;
            }
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.L) { _address.Focus(); _address.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { _browser.Active?.Reload(); e.Handled = true; }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Start in the account search, not on the first toolbar button (whose focus ring looked like a pressed button).
        if (!_lockPanel.Visible) ActiveControl = _searchBox.Box;
        _idleTimer.Start();
        UpdateLockButton();
        _recheckTimer.Start();
        _financeTimer.Start();
        if (_vault is { } v)
        {
            SetStatus($"{v.Accounts.Count} contas carregadas.");
            await _avatars.LoadAsync(v.Accounts.All.Select(a => a.UserId));
            await CheckAllAsync();
            StartAutomaticFinanceRefresh();
            if (v.Settings.UpdateRepo is not null && (v.Settings.LastUpdateCheckUtc is not { } last || DateTimeOffset.UtcNow - last > TimeSpan.FromHours(6)))
                await CheckForUpdateAsync(manual: false);
        }
    }

    // ───────────────────────────── list ─────────────────────────────

    private void RefreshList()
    {
        if (_vault is null) { _list.SetAccounts([], false); UpdateFinancePanel(); return; }
        var store = _vault.Accounts;
        string? selected = _groups.Selected;
        var groups = new List<string> { AllGroups };
        groups.AddRange(store.Groups);
        if (!_groups.Items.SequenceEqual(groups)) _groups.SetItems(groups);
        if (selected is null || !groups.Contains(selected)) _groups.Selected = AllGroups;
        string? group = _groups.Selected is { } gsel && gsel != AllGroups ? gsel : null;
        var accounts = store.Search(_search.Text, group);
        _list.SetAccounts(accounts, showGroups: group is null && store.Groups.Count() > 1);
        UpdateSummary();
        UpdateFinancePanel();
    }

    private void UpdateSummary()
    {
        if (_vault is null) { _summary.Text = ""; return; }
        var all = _vault.Accounts.All;
        int valid = all.Count(a => a.Status == SessionStatus.Valid);
        int expired = all.Count(a => a.Status == SessionStatus.Expired);
        _summary.Text = $"{all.Count} contas · {valid} ativas" + (expired > 0 ? $" · {expired} expiradas" : "");
    }

    // ───────────────────────────── switching ─────────────────────────────

    private async Task SwitchToAsync(Account a)
    {
        if (_vault is null) return;
        if (!a.HasSession || a.Status == SessionStatus.Expired)
        {
            var answer = MessageBox.Show(this, $"A sessão de @{a.Username} expirou ou foi encerrada pelo Roblox.\n\nEntrar de novo nesta conta agora?",
                "Sessão expirada", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) await StartSignInAsync(a);
            return;
        }
        HideBanner();
        _list.ActiveUserId = a.UserId;
        _list.Invalidate();
        bool wasAlive = _browser.IsAlive(a.UserId);
        SetStatus(wasAlive ? $"@{a.Username}" : $"Abrindo @{a.Username}…");
        try
        {
            await _browser.ShowAccountAsync(a.UserId, a.RevealSession(), "https://www.roblox.com/home");
            a.LastUsedUtc = DateTimeOffset.UtcNow;
            RequestSave();
            SetStatus($"Usando @{a.Username}.");
            if (NeedsFinanceRefresh(a)) _ = RefreshFinanceAsync(a);
        }
        catch (Exception ex)
        {
            ShowWebViewError(ex);
        }
    }

    private async Task StartSignInAsync(Account? expected)
    {
        if (_vault is null) return;
        _expectedSignIn = expected?.UserId;
        _list.ActiveUserId = null;
        _list.Invalidate();
        _browser.CloseSignIn(); // always start from a clean, signed-out profile
        ShowBanner(expected is null
            ? "Entre na conta pelo site oficial do Roblox abaixo. A conta é salva automaticamente quando o login terminar."
            : $"Entre como @{expected.Username} pelo site oficial do Roblox abaixo.");
        try { await _browser.ShowSignInAsync(); }
        catch (Exception ex) { ShowWebViewError(ex); }
    }

    private async Task OnSignedInAsync(SessionTab tab, string session)
    {
        if (_vault is null) return;
        SetStatus("Login detectado, confirmando com o Roblox…");
        var r = await _api.GetAuthenticatedUserAsync(session);
        if (_vault is null) return;
        if (r.Outcome == AuthOutcome.Unreachable) { SetStatus("Não foi possível confirmar o login agora; tentando de novo…"); tab.RetryReport(); return; }
        if (r.Outcome != AuthOutcome.Valid || r.User is null) return; // a half-finished sign-in; keep waiting

        var store = _vault.Accounts;
        var result = store.Upsert(r.User.Id, r.User.Name, r.User.DisplayName, r.RotatedSession ?? session);
        string stored = store.Find(r.User.Id)!.RevealSession();
        SaveNow();
        await _browser.AdoptSignInAsync(tab, r.User.Id, stored);
        HideBanner();
        _list.ActiveUserId = r.User.Id;
        RefreshList();
        _ = _avatars.LoadAsync([r.User.Id]);

        string msg = result == UpsertResult.Added ? $"Conta @{r.User.Name} adicionada." : $"Sessão de @{r.User.Name} renovada (a conta já estava salva).";
        if (_expectedSignIn is { } want && want != r.User.Id && store.Find(want) is { } w)
            msg += $" Atenção: você entrou como @{r.User.Name}, não como @{w.Username}.";
        _expectedSignIn = null;
        SetStatus(msg);
        UpdateToolbar();
        if (store.Find(r.User.Id) is { } signedIn) _ = RefreshFinanceAsync(signedIn);
    }

    private async Task OnSessionChangedAsync(SessionTab tab, string fresh)
    {
        if (_vault is null || tab.UserId is not { } id) return;
        var r = await _api.GetAuthenticatedUserAsync(fresh);
        if (_vault is null || tab.UserId != id) return;
        switch (r.Outcome)
        {
            case AuthOutcome.Valid when r.User!.Id == id:
                var a = _vault.Accounts.Find(id);
                if (a is null) return;
                _vault.Accounts.Upsert(id, r.User.Name, r.User.DisplayName, fresh);
                tab.AcceptSession(fresh);
                SaveNow();
                _list.RefreshAccount(id);
                UpdateSummary();
                Log.Info($"Session for {id} renewed from the browser.");
                break;
            case AuthOutcome.Valid:
                // The site's own account switcher (or a new login) moved this tab to another account: save that one too.
                var res = _vault.Accounts.Upsert(r.User!.Id, r.User.Name, r.User.DisplayName, fresh);
                _browser.Rekey(tab, r.User.Id, fresh);
                SaveNow();
                if (_browser.Active == tab) _list.ActiveUserId = r.User.Id;
                RefreshList();
                _ = _avatars.LoadAsync([r.User.Id]);
                SetStatus($"Esta aba agora está em @{r.User.Name} ({(res == UpsertResult.Added ? "conta adicionada" : "sessão atualizada")}).");
                break;
            case AuthOutcome.Invalid:
                tab.AcceptSession(fresh); // a dead value: do not ask again
                break;
            // Unreachable: try again on the next poll.
        }
    }

    private async Task OnSessionVanishedAsync(SessionTab tab)
    {
        if (_vault is null || tab.UserId is not { } id || _vault.Accounts.Find(id) is not { } a) return;
        string saved = a.RevealSession();
        var r = await _api.GetAuthenticatedUserAsync(saved);
        if (_vault is null) return;
        SessionChecker.Apply(_vault.Accounts, new CheckResult(id, saved, r));
        _list.RefreshAccount(id);
        UpdateSummary();
        if (a.Status == SessionStatus.Expired)
            ShowBanner($"A sessão de @{a.Username} foi encerrada pelo Roblox. Clique com o botão direito na conta → Entrar de novo.");
    }

    // ───────────────────────────── checks ─────────────────────────────

    private async Task CheckAllAsync()
    {
        if (_vault is null || _checking) return;
        _checking = true;
        _checkCts = new CancellationTokenSource();
        var token = _checkCts.Token;
        var vault = _vault;
        try
        {
            var targets = vault.Accounts.All.Where(a => a.HasSession).Select(a => (a.UserId, a.RevealSession())).ToList();
            foreach (var a in vault.Accounts.All) if (a.HasSession) a.Status = SessionStatus.Checking;
            _list.Invalidate();
            SetStatus($"Verificando {targets.Count} sessões…");
            int done = 0;
            await _checker.CheckAsync(targets, r => BeginInvoke(() =>
            {
                if (_vault != vault) return; // locked meanwhile
                if (SessionChecker.Apply(vault.Accounts, r)) RequestSave();
                if (r.Auth.RotatedSession is { } fresh && vault.Accounts.Find(r.UserId)?.RevealSession() == fresh)
                    _browser.Find(r.UserId)?.ReplaceCookie(fresh);
                _list.RefreshAccount(r.UserId);
                UpdateSummary();
                done++;
                if (done % 10 == 0) SetStatus($"Verificando sessões… {done}/{targets.Count}");
            }), token);
            _lastFullCheck = DateTime.UtcNow;
            if (_vault == vault)
            {
                int expired = vault.Accounts.All.Count(a => a.Status == SessionStatus.Expired);
                SetStatus(expired == 0 ? "Todas as sessões verificadas." : $"Sessões verificadas: {expired} precisam de login de novo.");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _checking = false;
            _checkCts?.Dispose();
            _checkCts = null;
        }
    }

    // ───────────────────────────── account edits ─────────────────────────────

    private void EditAlias(Account a)
    {
        string? v = PromptDialog.Ask(this, "Apelido", $"Apelido para @{a.Username} (vazio para remover):", a.Alias ?? "");
        if (v is null) return;
        a.Alias = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        RequestSave();
        RefreshList();
    }

    private void EditGroup(Account a)
    {
        string existing = _vault is null ? "" : string.Join(", ", _vault.Accounts.Groups.Where(g => g != AccountStore.NoGroup));
        string? v = PromptDialog.Ask(this, "Grupo", $"Grupo de @{a.Username} (vazio = sem grupo):", a.Group ?? "",
            existing.Length > 0 ? "Grupos existentes: " + existing : null);
        if (v is null) return;
        a.Group = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        RequestSave();
        RefreshList();
    }

    private async Task RemoveAsync(Account a)
    {
        if (_vault is null) return;
        var answer = MessageBox.Show(this,
            $"Remover @{a.Username} deste app?\n\n" +
            "Sim — remover e também encerrar a sessão no Roblox (recomendado: qualquer cópia do login deixa de funcionar).\n" +
            "Não — só remover deste app (a sessão continua válida no Roblox até expirar).",
            "Remover conta", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) return;
        string session = a.RevealSession();
        await _browser.CloseAccountAsync(a.UserId, sync: false);
        if (answer == DialogResult.Yes && a.HasSession)
        {
            SetStatus($"Encerrando a sessão de @{a.Username} no Roblox…");
            bool ok = await _api.LogoutAsync(session);
            if (!ok) MessageBox.Show(this, "Não foi possível encerrar a sessão no Roblox agora. A conta será removida do app mesmo assim.", "Remover conta", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (_vault is null) return;
        _vault.Accounts.Remove(a.UserId);
        _avatars.Forget(a.UserId);
        if (_list.ActiveUserId == a.UserId) _list.ActiveUserId = null;
        SaveNow();
        RefreshList();
        SetStatus($"@{a.Username} removida.");
    }

    // ───────────────────────────── lock & password ─────────────────────────────

    private async Task LockAsync()
    {
        if (_vault is null) return;
        if (!_vault.HasPassword)
        {
            if (MessageBox.Show(this, "Para bloquear o app é preciso uma senha mestra. Definir agora?", "Bloquear",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                SetPassword();
            if (!_vault.HasPassword) return;
        }
        _checkCts?.Cancel();
        _financeTimer.Stop();
        _financeCts?.Cancel();
        _avatarCts?.Cancel();
        await _browser.SyncAllAsync();
        SaveNow();
        _browser.CloseAll();
        _vault.Dispose();
        _vault = null;
        _plusDiagnostic = null;
        _list.ActiveUserId = null;
        RefreshList();
        HideBanner();
        _lockError.Text = "";
        _lockPassword.Text = "";
        _lockPanel.Visible = true;
        _lockPanel.BringToFront();
        _lockPassword.Focus();
        SetStatus("Bloqueado.");
    }

    private async Task UnlockAsync()
    {
        string pw = _lockPassword.Text;
        if (pw.Length == 0) return;
        _lockError.Text = "Abrindo…";
        try
        {
            string path = Program.VaultPath(_dataDir);
            var vault = await Task.Run(() => VaultFile.Open(path, _protector, pw));
            _vault = vault;
            _lockPassword.Text = "";
            _lockPanel.Visible = false;
            _nextAutomaticFinanceScan = default;
            _financeTimer.Start();
            _browser.LiveSessions = vault.Settings.LiveSessions;
            RefreshList();
            SetStatus("Desbloqueado.");
            await _avatars.LoadAsync(vault.Accounts.All.Select(a => a.UserId));
            await CheckAllAsync();
            StartAutomaticFinanceRefresh();
        }
        catch (WrongPasswordException) { _lockError.Text = "Senha incorreta."; _lockPassword.SelectAll(); }
        catch (Exception ex) { _lockError.Text = ex.Message; Log.Error("Unlock failed", ex); }
    }

    private void SetPassword()
    {
        if (_vault is null) return;
        string? pw = PromptDialog.AskNewPassword(this, "Senha mestra",
            "A senha mestra é exigida para abrir o app e não pode ser recuperada: se esquecer, será preciso entrar de novo em cada conta.");
        if (pw is null) return;
        _vault.SetPassword(pw);
        SetStatus("Senha mestra definida.");
    }

    private void ChangePassword()
    {
        if (_vault is null) return;
        var current = PromptDialog.AskPassword(this, "Alterar senha mestra", "Senha atual:", validate: p => _vault.CheckPassword(p) ? null : "Senha incorreta.");
        if (current is null) return;
        SetPassword();
    }

    private void RemovePassword()
    {
        if (_vault is null) return;
        var current = PromptDialog.AskPassword(this, "Remover senha mestra", "Senha atual:",
            "Sem senha, o cofre continua criptografado pelo Windows (só abre na sua conta do Windows), mas qualquer programa rodando como você pode abri-lo.",
            p => _vault.CheckPassword(p) ? null : "Senha incorreta.");
        if (current is null) return;
        _vault.SetPassword(null);
        UpdateLockButton();
        SetStatus("Senha mestra removida: o app abre direto, sem bloqueio.");
    }

    private void EditNumber(string title, string label, int current, int min, int max, Action<int> apply)
    {
        string? v = PromptDialog.Ask(this, title, label, current.ToString());
        if (v is null) return;
        if (!int.TryParse(v.Trim(), out int n) || n < min || n > max)
        {
            MessageBox.Show(this, $"Digite um número entre {min} e {max}.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        apply(n);
        RequestSave();
    }

    private void ShowSecurityInfo()
    {
        MessageBox.Show(this,
            "• Você entra nas contas pelo site oficial do Roblox. O app nunca vê nem guarda senhas.\n" +
            "• O app guarda só o cookie de sessão de cada conta, num cofre criptografado (AES-256-GCM) cuja chave é protegida pelo Windows (DPAPI) e, se você definir, pela senha mestra (PBKDF2, 600 mil iterações).\n" +
            "• Cada conta abre num perfil de navegador separado e privado (InPrivate). Nada da sessão é gravado fora do cofre.\n" +
            "• Os cookies vão somente para *.roblox.com. Nenhum servidor de terceiros, nenhuma telemetria.\n" +
            "• As ferramentas de desenvolvedor do navegador ficam desligadas (golpe do “cole isto no console”).\n" +
            "• Limite: um programa malicioso rodando na sua conta do Windows enquanto o app está desbloqueado pode ler a memória. Mantenha o Windows e o antivírus ativos.",
            "Como suas contas são protegidas", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ───────────────────────────── Roblox app (Player) ─────────────────────────────

    /// <summary>roblox.com's official page where a signed-in session approves a Quick Login code shown by another device.</summary>


    /// <summary>
    /// roblox.com asked to open the Roblox app. The app keeps its own sign-in, separate from every browser:
    /// a link only signs it in if roblox.com put a one-use ticket in it. Here the person sees which account is
    /// selected, whether the link signs in, and can sign the app in through Roblox's own Quick Login.
    /// The link is opened unchanged or not at all; no session or ticket is created, copied or logged.
    /// </summary>
    private async void OnPlayerLaunchRequested(SessionTab tab, string uri, string? origin, bool userInitiated)
    {
        var link = PlayerLinks.Classify(uri);
        var handler = ProtocolHandlers.Describe(link.Kind == PlayerLinkKind.AppLink ? "roblox" : "roblox-player");
        string originHost = Uri.TryCreate(origin, UriKind.Absolute, out var o) ? o.Host : "desconhecida";
        Log.Info($"[player] pedido de abertura: {link.Summary}; origem {originHost}; clique da pessoa: {(userInitiated ? "sim" : "não")}; " +
                 $"programa do Windows: {ProtocolHandlers.Text(handler)}");

        var (allowed, reason) = PlayerLinks.MayOffer(link, origin, userInitiated);
        if (!allowed)
        {
            Log.Warn("[player] abertura recusada: " + reason);
            SetStatus("Abertura do aplicativo Roblox recusada: " + reason);
            return;
        }

        var account = tab.UserId is { } id ? _vault?.Accounts.Find(id) : null;
        var choice = PlayerLaunchDialog.Ask(this, account?.Username, link, handler);
        Log.Info("[player] escolha: " + choice);
        if (choice == PlayerLaunchChoice.Cancel) return;

        string who = account is null ? "a conta certa" : "@" + account.Username;
        if (choice == PlayerLaunchChoice.SignInAutomatic && account is not null)
        {
            try { if (await SignInAppAsync(tab, account, PlayerContinuation.CanContinue(link) ? uri : null)) return; }
            catch (Exception ex) { Log.Error("Automatic app sign-in failed", ex); }
            // The ticket was refused: Roblox's own Quick Login still works.
            if (MessageBox.Show(this, "Não foi possível entrar automaticamente. Abrir o aplicativo e entrar pelo Quick Login?",
                    "Entrar no aplicativo Roblox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            choice = PlayerLaunchChoice.OpenAndQuickLogin;
        }

        if (!Native.OpenRobloxLink(uri))
        {
            ShowBanner("O Windows não abriu o aplicativo Roblox. Verifique se ele está instalado (roblox.com/download).");
            return;
        }
        if (choice == PlayerLaunchChoice.OpenAndQuickLogin && account is not null)
            await ShowQuickLoginAsync(tab, who);
        else
        {
            ShowBanner(link.CarriesSignIn
                ? $"Aplicativo Roblox aberto com o login de uso único do site. Confira no app que a conta é {who} antes de continuar."
                : $"Aplicativo Roblox aberto. Ele usa o próprio login: confira no app que a conta é {who}. Se estiver deslogado, use \"Entrar no app (automático)\" da próxima vez.");
        }
    }

    private async Task ShowQuickLoginAsync(SessionTab tab, string who)
    {
        // Roblox's own cross-device sign-in, approved in this tab (this account). Only a page that really loads is kept.
        string? page = await tab.NavigateFirstAvailableAsync(QuickLogin.Pages);
        string steps = $"No aplicativo Roblox, escolha entrar com outro dispositivo (Quick Log In); ele mostra um código. Aprove esse código aqui como {who}. " +
                       "Se o app estiver em outra conta, saia dela no app primeiro. Depois clique em \"Enviar Robux\" de novo.";
        if (page is null)
            ShowBanner("Não consegui abrir a página do Quick Login do Roblox. " + steps.Replace("Aprove esse código aqui", "Aprove esse código em Configurações → Segurança → Quick Log In, nesta aba,"));
        else if (QuickLogin.IsSettingsFallback(page))
            ShowBanner(steps.Replace("Aprove esse código aqui", "Nesta página de Configurações → Segurança, abra Quick Log In e aprove o código"));
        else
            ShowBanner("Quick Login: " + steps.Replace("Aprove esse código aqui", "Digite esse código nesta página e confirme"));
    }

    /// <summary>
    /// Signs the Roblox app in as this account the way roblox.com's "Play" button does: a one-use ticket asked with
    /// this account's session (after confirming the session really is this account), opened as a roblox-player link
    /// with launchmode:app. The ticket only exists in memory for this call and never reaches a log.
    /// </summary>
    /// <param name="continueWith">The site's own app link (e.g. Send Robux to the player whose profile was open), handed to
    /// Windows again, unchanged, once the app is signed in. Null: only sign in.</param>
    private async Task<bool> SignInAppAsync(SessionTab tab, Account account, string? continueWith)
    {
        if (_vault is not { } vault) return false;
        SetStatus($"Preparando o login de @{account.Username} no aplicativo Roblox…");
        await tab.SyncNowAsync(); // the browser may hold a newer session than the vault
        string session = account.RevealSession();

        var who = await _api.GetAuthenticatedUserAsync(session);
        if (who.Outcome != AuthOutcome.Valid || who.User!.Id != account.UserId)
        {
            Log.Warn($"[player] login automático cancelado: sessão {(who.Outcome == AuthOutcome.Valid ? "de outra conta" : who.Outcome.ToString())}");
            ShowBanner(who.Outcome == AuthOutcome.Invalid
                ? $"A sessão de @{account.Username} expirou. Entre de novo nela antes de abrir o aplicativo."
                : $"Não foi possível confirmar que a sessão é de @{account.Username}. Nada foi enviado ao aplicativo.");
            if (who.Outcome == AuthOutcome.Invalid) { account.Status = SessionStatus.Expired; _list.RefreshAccount(account.UserId); }
            return false;
        }
        if (who.RotatedSession is { } r1 && vault.Accounts.ReplaceSessionIf(account.UserId, session, r1)) { tab.ReplaceCookie(r1); session = r1; RequestSave(); }

        var ticket = await _api.GetAuthTicketAsync(session);
        if (ticket.RotatedSession is { } r2 && vault.Accounts.ReplaceSessionIf(account.UserId, session, r2)) { tab.ReplaceCookie(r2); RequestSave(); }
        if (!ticket.Ok)
        {
            if (ticket.SessionRefused) { account.Status = SessionStatus.Expired; _list.RefreshAccount(account.UserId); }
            SetStatus("Login automático no aplicativo: " + ticket.Problem);
            return false;
        }

        string link = PlayerLinks.BuildAppSignIn(ticket.Ticket!, DateTimeOffset.UtcNow, Random.Shared.NextInt64(100_000_000, 999_999_999_999));
        bool opened = Native.OpenRobloxLink(link);
        Log.Info($"[player] login automático de @{account.Username}: {(opened ? "aplicativo aberto" : "o Windows não abriu o aplicativo")}");
        if (!opened)
        {
            ShowBanner("O Windows não abriu o aplicativo Roblox. Verifique se ele está instalado (roblox.com/download).");
            return true; // a ticket was used; asking again would not help
        }
        if (continueWith is null)
        {
            ShowBanner($"O aplicativo Roblox está entrando como @{account.Username}. Quando ele abrir, clique em \"Enviar Robux\" de novo " +
                       "e confira o nome da conta no aplicativo antes de enviar.");
            return true;
        }
        _ = ContinueInAppAsync(continueWith, account.Username, ProtocolHandlers.Describe("roblox-player").Program);
        return true;
    }

    private CancellationTokenSource? _continueCts;

    /// <summary>
    /// Waits for the Roblox app window, gives it a few seconds to finish signing in, then opens the site's original
    /// link (the Send Robux screen for the chosen player) in it. A click on the banner cancels while waiting and
    /// opens the screen again afterwards. The RbxSwitcher never confirms anything in the app.
    /// </summary>
    private async Task ContinueInAppAsync(string link, string username, string handlerProgram)
    {
        _continueCts?.Cancel();
        var cts = _continueCts = new CancellationTokenSource();
        var ct = cts.Token;
        void Reopen() { if (Native.OpenRobloxLink(link)) SetStatus("Tela de envio pedida de novo ao aplicativo Roblox."); }
        try
        {
            ShowBanner($"Entrando como @{username} no aplicativo Roblox… a tela de envio abre em seguida. Clique aqui para cancelar.", () => cts.Cancel());
            var names = new[] { "RobloxPlayerBeta", Path.GetFileNameWithoutExtension(handlerProgram) }.Where(n => n.Length > 0).Distinct().ToArray();
            var deadline = DateTime.UtcNow + PlayerContinuation.WaitForWindow;
            bool window = false;
            while (!window && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);
                window = names.Any(n => System.Diagnostics.Process.GetProcessesByName(n).Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } finally { p.Dispose(); } }));
            }
            if (!window)
            {
                Log.Warn("[player] janela do aplicativo Roblox não apareceu a tempo; tela de envio não pedida");
                ShowBanner("O aplicativo Roblox não abriu a tempo. Quando ele estiver aberto, clique aqui para abrir a tela de envio.", Reopen);
                return;
            }
            for (int left = (int)PlayerContinuation.SettleAfterWindow.TotalSeconds; left > 0; left--)
            {
                ShowBanner($"Aplicativo aberto. Abrindo a tela de envio em {left} s… Clique aqui para cancelar.", () => cts.Cancel());
                await Task.Delay(1000, ct);
            }
            bool opened = Native.OpenRobloxLink(link);
            Log.Info($"[player] tela pedida pelo site reaberta no aplicativo: {(opened ? "sim" : "o Windows não abriu")}");
            ShowBanner($"Tela de envio aberta no aplicativo. Confira a conta (@{username}) e o destinatário antes de enviar. " +
                       "Se não abriu, clique aqui.", Reopen);
            await Task.Delay(PlayerContinuation.ReopenFor, ct);
            if (_bannerAction == (Action)Reopen) HideBanner();
        }
        catch (OperationCanceledException)
        {
            if (!_closing) ShowBanner("Abertura da tela de envio cancelada. Clique aqui para abri-la mesmo assim.", Reopen);
        }
    }

    // ───────────────────────────── updates ─────────────────────────────

    private void EditUpdateRepo()
    {
        if (_vault is null) return;
        string? v = PromptDialog.Ask(this, "Atualizações", "Repositório do GitHub (dono/nome). Vazio desliga:",
            _vault.Settings.UpdateRepo ?? "",
            "O app procura novas versões nas releases desse repositório, confere o SHA-256 publicado e só instala depois que você confirmar. Nenhum dado de conta é enviado.");
        if (v is null) return;
        v = v.Trim();
        if (v.Length > 0 && !RbxSwitcher.Core.Updates.Releases.IsValidRepo(v))
        {
            MessageBox.Show(this, "Use o formato dono/nome, por exemplo: seu-usuario/rbx-switcher.", "Atualizações", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _vault.Settings.UpdateRepo = v.Length == 0 ? null : v;
        _pendingRelease = null;
        RequestSave();
        SetStatus(v.Length == 0 ? "Atualizações desligadas." : $"Atualizações pelo repositório {v}.");
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_vault?.Settings.UpdateRepo is not { } repo) return;
        try
        {
            var info = await _updater.CheckAsync(repo);
            if (_vault is null) return;
            _vault.Settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            RequestSave();
            _pendingRelease = info;
            if (info is not null)
                ShowBanner($"Nova versão {info.Tag} disponível. Para instalar, abra Configurações → Instalar versão {info.Tag} agora.");
            else if (manual)
                SetStatus($"Você já está na versão mais recente ({Updater.Current}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("[atualização] verificação falhou: " + ex.GetType().Name);
            if (manual) SetStatus("Não foi possível procurar atualização agora.");
        }
    }

    private async Task InstallUpdateAsync(RbxSwitcher.Core.Updates.ReleaseInfo release)
    {
        if (MessageBox.Show(this, $"Instalar a versão {release.Tag}?\n\nO app confere o hash do arquivo, salva tudo e reinicia sozinho.",
                "Atualização", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SetStatus($"Baixando {release.Tag}…");
        await _browser.SyncAllAsync();
        SaveNow();
        var (ok, message) = await _updater.InstallAsync(release);
        if (ok) { Close(); return; }
        MessageBox.Show(this, message, "Atualização", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        SetStatus(message);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void Go(string url) => _browser.Active?.Navigate(url);

    private void GoUser(string page)
    {
        if (_browser.Active?.UserId is { } id)
            Go($"https://www.roblox.com/users/{id}/{page}");
    }

    private void UpdateToolbar()
    {
        var tab = _browser.Active;
        _empty.Visible = tab is null;
        if (tab is null) _empty.BringToFront();
        _back.Enabled = tab?.CanGoBack ?? false;
        _forward.Enabled = tab?.CanGoForward ?? false;
        _reload.Enabled = tab is not null;
        if (!_address.Focused) _address.Text = tab?.Url ?? "";
        _closeSignIn.Visible = tab is not null && tab == _browser.SignInTab;
        _current.Text = tab switch
        {
            null => "",
            { UserId: null } => "Novo login",
            { UserId: { } id } => _vault?.Accounts.Find(id) is { } a ? "@" + a.Username : "",
        };
        if (tab?.UserId is { } uid && _list.ActiveUserId != uid) { _list.ActiveUserId = uid; _list.Invalidate(); }
        UpdateFinancePanel();
    }

    // ───────────────────────────── balance & transfer quota ─────────────────────────────

    private Account? ActiveAccount() =>
        _vault is not null && _browser.Active?.UserId is { } id ? _vault.Accounts.Find(id) : null;

    private void ShowCategory(bool plus) => ShowView(plus ? ViewPlus : ViewContas);

    private void ShowView(int view)
    {
        _contasPage.Visible = view == ViewContas;
        _plusPage.Visible = view == ViewPlus;
        _avatarsPage.Visible = view == ViewAvatares;
        _distributePage.Visible = view == ViewDistribuir;
        _sellPage.Visible = view == ViewLimiteds;
        _calendarPage.Visible = view == ViewCalendario;
        _tabs.SelectedIndex = view; // no-op when it is already there
        UpdateFinancePanel();
        if (view == ViewAvatares) _ = ShowAvatarsAsync();
        if (view == ViewDistribuir) UpdateDistribute(force: true);
        if (view == ViewLimiteds) OnLimitedsShown();
        if (view == ViewCalendario) OnCalendarShown();
    }

    private Account? PlusAccount(long? id) => id is { } userId ? _vault?.Accounts.Find(userId) : null;

    private void UpdateFinancePanel()
    {
        var now = DateTimeOffset.UtcNow;
        if (ActiveAccount() is { } account)
        {
            _summaryCard.Visible = true;
            _summaryCard.ShowLoading(_financeBusy.Contains(account.UserId));
            _summaryCard.Show(account.Username, account.Finance, now);
        }
        else _summaryCard.Visible = false;
        _plusPage.SetAccounts(_vault?.Accounts, now, _financeCts is not null);
        UpdatePlusDetails();
        UpdateDistribute();
        UpdateSelling();
        UpdateCalendar();
    }

    private void UpdatePlusDetails()
    {
        if (PlusAccount(_plusPage.DetailId) is not { } account)
        {
            _plusView.ClearDiagnostic();
            _plusView.ShowNoAccount();
            return;
        }
        _plusView.ShowBusy(_financeBusy.Contains(account.UserId), _diagnosing, account.HasSession && account.Status != SessionStatus.Expired);
        _plusView.Show(account.Username, account.Finance, DateTimeOffset.UtcNow);
        _plusView.ShowLimiteds(LimitedsDetailOf(account));
        if (_plusDiagnostic is { } report && report.UserId == account.UserId) _plusView.ShowDiagnostic(report.Text);
        else _plusView.ClearDiagnostic();
    }

    /// <summary>
    /// Records for 60 s, value-free, the JSON calls the roblox.com pages make in this account's tab, so the
    /// official source of a field (e.g. the free-trial offer) can be found instead of guessed.
    /// </summary>
    private readonly HashSet<long> _recording = new();

    /// <param name="automatic">Started because the official /plus page opened: no banner; the free-trial offer it
    /// shows is taken from the page's own responses when a recognized yes/no field is there.</param>
    private async Task RecordOfficialCallsAsync(Account a, int seconds, bool automatic)
    {
        if (!_recording.Add(a.UserId)) return;
        try { await RecordCoreAsync(a, seconds, automatic); }
        finally { _recording.Remove(a.UserId); }
    }

    private async Task RecordCoreAsync(Account a, int seconds, bool automatic)
    {
        var vault = _vault;
        if (_browser.Find(a.UserId) is not { Ready: true } tab)
        {
            SetStatus("Abra a conta no navegador do app antes de gravar.");
            return;
        }
        var calls = new List<RbxSwitcher.Core.Logging.ApiCall>();
        tab.StartApiRecording(c => BeginInvoke(() => calls.Add(c)));
        if (!automatic)
        {
            ShowCategory(plus: false);
            ShowBanner($"Gravando {seconds} s na aba de @{a.Username}: no site, abra \"Roblox Plus\" no menu à esquerda (onde aparece o Teste grátis).");
        }
        try
        {
            for (int left = seconds; left > 0; left--)
            {
                _plusView.ShowRecording(left);
                await Task.Delay(1000);
            }
        }
        finally
        {
            tab.StopApiRecording();
            _plusView.ShowRecording(0);
            if (!automatic) HideBanner();
        }
        // The official page's own answer about the free trial (yes/no only), if it used a recognized field.
        if (vault is null || _vault != vault || vault.Accounts.Find(a.UserId) is not { } acc) return;
        var verdicts = calls.Select(c => c.TrialEligible).Where(t => t is not null).Distinct().ToList();
        if (verdicts.Count == 1)
        {
            acc.Finance ??= new RbxSwitcher.Core.Finance.AccountFinance();
            acc.Finance.TrialEligibleFromPage = verdicts[0];
            acc.Finance.TrialFromPageUtc = DateTimeOffset.UtcNow;
            RequestSave();
            UpdateFinancePanel();
            _list.RefreshAccount(a.UserId);
        }
        string verdict = verdicts.Count switch
        {
            1 => verdicts[0] == true ? "teste grátis DISPONÍVEL segundo a página oficial" : "teste grátis indisponível segundo a página oficial",
            0 => "nenhum campo de teste grátis reconhecido nas respostas da página — copie este texto e envie para ajuste",
            _ => "respostas da página discordam sobre o teste grátis — nada foi alterado",
        };
        string text = $"Chamadas da página oficial — @{a.Username} — {Fmt.DayTime(DateTimeOffset.UtcNow)} (Brasília)" + Environment.NewLine +
                      "Resultado: " + verdict + Environment.NewLine +
                      RbxSwitcher.Core.Logging.ApiCalls.Format(calls);
        Log.Info("[gravação] " + text.Replace(Environment.NewLine, " | "));
        _plusDiagnostic = (a.UserId, text);
        UpdatePlusDetails();
        SetStatus($"Página do Plus de @{a.Username}: {verdict}.");
    }

    /// <summary>
    /// Reproduces the Plus read with this account's own sessions (saved and, if the tab is open, the browser's)
    /// and shows what came back: HTTP status, field names and types, and the field the parser refused.
    /// </summary>
    private async Task RunPlusDiagnosticAsync(Account a)
    {
        if (_diagnosing || !a.HasSession) return;
        var vault = _vault;
        _diagnosing = true;
        UpdateFinancePanel();
        try
        {
            string? browserSession = _browser.Find(a.UserId) is { } tab ? await tab.ReadSessionAsync() : null;
            var now = DateTimeOffset.UtcNow;
            var report = await PlusDiagnostics.RunAsync(_api, a.UserId, a.RevealSession(), browserSession, now);
            string text = PlusDiagnostics.Format(report, a.Username, now, Updater.Current.ToString());
            Log.Info(text.Replace(Environment.NewLine, " | ").Replace("\n", " | "));
            if (_vault != vault || vault?.Accounts.Find(a.UserId) is null) return;
            _plusDiagnostic = (a.UserId, text);
            SetStatus(report.Rejected is null ? "Diagnóstico: leitura do Plus aceita." : "Diagnóstico: " + report.Rejected);
        }
        catch (Exception ex)
        {
            Log.Error("Plus diagnostic failed", ex);
            if (_vault == vault && vault?.Accounts.Find(a.UserId) is not null)
                _plusDiagnostic = (a.UserId, "Diagnóstico não concluído: " + Redact.Text(ex.Message));
        }
        finally
        {
            _diagnosing = false;
            UpdateFinancePanel();
        }
    }

    private bool NeedsFinanceRefresh(Account a) =>
        NeedsFinanceRefresh(a, DateTimeOffset.UtcNow);

    /// <summary>Due every 25 min, or as soon as the pause after Roblox's request limit (HTTP 429) is over.</summary>
    private bool NeedsFinanceRefresh(Account a, DateTimeOffset now) =>
        now >= _robloxPauseUntil &&
        (a.Finance?.LastAttemptUtc is not { } last || now - last >= FinanceRefreshInterval
         || (_retryAfterLimit.TryGetValue(a.UserId, out var at) && now >= at));

    // ── Roblox request limit (HTTP 429): one pause for every read, growing while Roblox keeps refusing ──
    private DateTimeOffset _robloxPauseUntil = DateTimeOffset.MinValue;
    private int _rateLimitStreak;
    private readonly Dictionary<long, DateTimeOffset> _retryAfterLimit = new();

    private void NoteRateLimit(long id, TimeSpan? suggested)
    {
        _rateLimitStreak++;
        double seconds = Math.Max((suggested ?? TimeSpan.FromSeconds(60)).TotalSeconds, 60 * Math.Pow(2, _rateLimitStreak - 1));
        var until = DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 600));
        if (until > _robloxPauseUntil) _robloxPauseUntil = until;
        _retryAfterLimit[id] = _robloxPauseUntil;
        _nextAutomaticFinanceScan = _robloxPauseUntil;
        Log.Warn($"[saldos] HTTP 429; leituras pausadas até {Fmt.Time(_robloxPauseUntil)} (pausa {_rateLimitStreak})");
        SetStatus($"O Roblox limitou os pedidos. As leituras recomeçam sozinhas às {BrasiliaTime.ToBrasilia(_robloxPauseUntil):HH:mm}.");
    }

    private void StartAutomaticFinanceRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        if (_closing || !_financeTimer.Enabled || _vault is null || _financeCts is not null || _sellBusy || now < _nextAutomaticFinanceScan) return;
        // Check on the next tick normally, and give unexpected failures time before retrying a batch.
        var refresh = RefreshAllFinanceAsync(onlyDue: true);
        if (!refresh.IsCompleted) _nextAutomaticFinanceScan = now.AddMinutes(2);
    }

    /// <summary>
    /// Reads balance, pending, Plus, limits and history for one account. The session never leaves
    /// Core/RobloxApi; the identity is checked against the saved user id before any other read.
    /// </summary>
    private async Task RefreshFinanceAsync(Account a, CancellationToken ct = default, bool fullHistory = false)
    {
        if (_closing || ct.IsCancellationRequested || _vault is null || !a.HasSession || !_financeBusy.Add(a.UserId)) return;
        var vault = _vault;
        long id = a.UserId;
        string sent = a.RevealSession();
        UpdateFinancePanel();
        try
        {
            var r = await _financeRefresher.RefreshAsync(id, sent, a.Finance, ct, fullHistory);
            if (_closing || ct.IsCancellationRequested || _vault != vault || vault.Accounts.Find(id) is not { } acc) return;
            LimitWatch.KeepAcknowledgement(acc.Finance, r.Finance);
            acc.Finance = r.Finance;
            if (r.RateLimitedFor is { } wait) NoteRateLimit(id, wait);
            else if (r.Identity == IdentityCheck.Confirmed)
            {
                _rateLimitStreak = 0;
                _retryAfterLimit.Remove(id);
                // Limiteds of accounts in the distribution are read in the same pass (only when due).
                await RefreshInventoryAsync(acc, force: fullHistory, ct: ct);
            }
            switch (r.Identity)
            {
                case IdentityCheck.SessionExpired:
                    acc.Status = SessionStatus.Expired;
                    break;
                case IdentityCheck.WrongAccount:
                    acc.Status = SessionStatus.Expired;
                    ShowBanner($"A sessão salva de @{acc.Username} pertence a outra conta. Entre de novo nela (botão direito → Entrar de novo).");
                    break;
                case IdentityCheck.Confirmed:
                    acc.Status = SessionStatus.Valid;
                    acc.LastValidUtc = DateTimeOffset.UtcNow;
                    if (r.RotatedSession is { } fresh && vault.Accounts.ReplaceSessionIf(id, sent, fresh))
                        _browser.Find(id)?.ReplaceCookie(fresh);
                    break;
            }
            RequestSave();
            _list.RefreshAccount(id);
            UpdateSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Finance refresh failed", ex); }
        finally
        {
            _financeBusy.Remove(id);
            if (!_closing) UpdateFinancePanel();
        }
    }

    /// <summary>Refreshes eligible accounts one at a time, pausing on Roblox's request limit; cancelled on lock or close.</summary>
    private async Task RefreshAllFinanceAsync(bool onlyDue = false)
    {
        if (_closing || _vault is not { } vault || _financeCts is not null) return;
        var now = DateTimeOffset.UtcNow;
        var targets = vault.Accounts.All
            .Where(a => a.HasSession && a.Status != SessionStatus.Expired && !_financeBusy.Contains(a.UserId)
                && (!onlyDue || NeedsFinanceRefresh(a, now)))
            .ToList();
        if (targets.Count == 0) return;
        _financeCts = new CancellationTokenSource();
        UpdateFinancePanel();
        var token = _financeCts.Token;
        int done = 0;
        try
        {
            // One account at a time: the transfer history is several pages per account and Roblox answers 429
            // quickly when two accounts page at once.
            foreach (var a in targets)
            {
                if (DateTimeOffset.UtcNow < _robloxPauseUntil)
                {
                    if (onlyDue) break; // background: the rest stays due and resumes after the pause
                    while (DateTimeOffset.UtcNow < _robloxPauseUntil)
                    {
                        SetStatus($"O Roblox limitou os pedidos. Continuando às {BrasiliaTime.ToBrasilia(_robloxPauseUntil):HH:mm}… ({done}/{targets.Count})");
                        await Task.Delay(1000, token);
                    }
                }
                if (vault.Accounts.Find(a.UserId) != a || a.Status == SessionStatus.Expired
                    || (onlyDue && !NeedsFinanceRefresh(a))) continue;
                await RefreshFinanceAsync(a, token);
                token.ThrowIfCancellationRequested();
                if (!onlyDue) SetStatus($"Atualizando saldos… {++done}/{targets.Count}");
                await Task.Delay(700, token);
            }
            if (!onlyDue) SetStatus($"Atualização de saldos concluída ({done} contas processadas).");
            await EnsureMarketAsync();
            // Distribution: trades already sent, and sales of the accounts that received copies.
            await CheckSentTradesAsync();
            if (_vault is { } v2)
                foreach (var acc in v2.Accounts.All.Where(x => RoleOf(x) == Role.Destination || HasPendingRobux(x)).ToList())
                {
                    if (DateTimeOffset.UtcNow < _robloxPauseUntil) break;
                    await ReadSalesAsync(acc);
                }
            ReplanAfterChange();
        }
        catch (OperationCanceledException)
        {
            if (!onlyDue && !_closing && _vault == vault) SetStatus("Atualização de saldos interrompida.");
        }
        finally
        {
            _financeCts.Dispose();
            _financeCts = null;
            if (!_closing) UpdateFinancePanel();
        }
    }

    private Action? _bannerAction;

    /// <param name="onClick">Optional action when the banner is clicked (the banner then shows a hand cursor).</param>
    private void ShowBanner(string text, Action? onClick = null)
    {
        _banner.Text = text;
        _bannerAction = onClick;
        _banner.Cursor = onClick is null ? Cursors.Default : Cursors.Hand;
        _banner.Visible = true;
    }

    private void HideBanner()
    {
        _banner.Visible = false;
        _bannerAction = null;
    }
    private void SetStatus(string text) => _status.Text = Redact.Text(text);

    private void ShowWebViewError(Exception ex)
    {
        Log.Error("WebView2 error", ex);
        MessageBox.Show(this, "Não foi possível abrir o navegador integrado (WebView2).\n\n" + Redact.Text(ex.Message),
            "Navegador", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>"Ok, entendi" on a lower transfer limit: hidden until the limit changes again.</summary>
    private void AcknowledgeLimitDrop(Account? account)
    {
        if (account?.Finance is not { } f) return;
        LimitWatch.Acknowledge(f, DateTimeOffset.UtcNow);
        RequestSave();
        UpdateFinancePanel();
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_vault is null) return;
        try { _vault.Save(); }
        catch (Exception ex)
        {
            Log.Error("Vault save failed", ex);
            SetStatus("Erro ao salvar o cofre: " + ex.Message);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing) { base.OnFormClosing(e); return; }
        // Sync cookies from open tabs once, then close for real.
        e.Cancel = true;
        _closing = true;
        _checkCts?.Cancel();
        _financeTimer.Stop();
        _financeCts?.Cancel();
        try
        {
            if (_vault is not null)
            {
                var sync = _browser.SyncAllAsync();
                await Task.WhenAny(sync, Task.Delay(3000));
                SaveNow();
            }
        }
        catch (Exception ex) { Log.Error("Close sync failed", ex); }
        _browser.Dispose();
        _vault?.Dispose();
        _vault = null;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _saveTimer.Dispose();
            _idleTimer.Dispose();
            _recheckTimer.Dispose();
            _financeTimer.Dispose();
            _avatars.Dispose();
            DisposeAvatarImages();
            _rolimons.Dispose();
            _api.Dispose();
            _updater.Dispose();
        }
        base.Dispose(disposing);
    }
}
