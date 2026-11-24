using Microsoft.Web.WebView2.Core;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Sessions;

namespace RbxSwitcher.App.Browser;

/// <summary>
/// Owns the shared WebView2 browser and the account tabs. Switching shows a tab that is already
/// alive (instant) or opens one with the saved session (about a second). Only the most recently
/// used tabs stay alive (<see cref="LivePool{TKey}"/>); the rest are closed after their cookie
/// is synced back to the vault.
/// </summary>
internal sealed class BrowserHost : IDisposable
{
    private readonly Control _container;
    private readonly string _userDataFolder;
    private readonly Dictionary<long, SessionTab> _tabs = new();
    private readonly LivePool<long> _pool;
    private Task<CoreWebView2Environment>? _env;
    private SessionTab? _signIn;
    private long? _wanted;
    private bool _disposed;

    public SessionTab? Active { get; private set; }
    public SessionTab? SignInTab => _signIn;

    /// <summary>Called for every new tab so the owner can subscribe to its events.</summary>
    public event Action<SessionTab>? TabCreated;
    public event Action? ActiveChanged;

    public BrowserHost(Control container, string userDataFolder, int liveSessions)
    {
        _container = container;
        _userDataFolder = userDataFolder;
        _pool = new LivePool<long>(liveSessions);
    }

    public int LiveSessions
    {
        get => _pool.Capacity;
        set { _pool.Capacity = value; _ = CloseAsync(_pool.Trim()); }
    }

    public bool IsAlive(long userId) => _tabs.ContainsKey(userId);

    public SessionTab? Find(long userId) => _tabs.GetValueOrDefault(userId);

    private Task<CoreWebView2Environment> Environment()
    {
        return _env ??= Create();

        async Task<CoreWebView2Environment> Create()
        {
            // Leftovers from a previous run (InPrivate keeps sessions in memory, but the browser keeps some state here).
            try { if (Directory.Exists(_userDataFolder)) Directory.Delete(_userDataFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var options = new CoreWebView2EnvironmentOptions
            {
                // Never sign the browser into the Windows/Microsoft account: only Roblox sessions live here.
                AllowSingleSignOnUsingOSPrimaryAccount = false,
            };
            return await CoreWebView2Environment.CreateAsync(null, _userDataFolder, options);
        }
    }

    /// <summary>Shows the account's tab, opening it with <paramref name="session"/> if it is not alive.</summary>
    public async Task ShowAccountAsync(long userId, string session, string startUrl)
    {
        _wanted = userId;
        bool created = false;
        if (!_tabs.TryGetValue(userId, out var tab))
        {
            created = true;
            tab = NewTab(userId);
            _tabs[userId] = tab;
            try
            {
                await tab.InitAsync(await Environment(), session, startUrl);
            }
            catch (Exception e)
            {
                Log.Error("Could not open account tab", e);
                _tabs.Remove(userId);
                _container.Controls.Remove(tab.View);
                tab.Dispose();
                throw;
            }
        }
        if (_disposed) return;
        if (_wanted != userId)
        {
            // The person clicked another account while this one was opening: drop it rather than
            // keep a tab the live-session limit does not know about.
            if (created && tab != Active && _tabs.Remove(userId)) Dispose(tab);
            return;
        }
        Activate(tab);
        await CloseAsync(_pool.Touch(userId));
    }

    /// <summary>Opens (or shows) the sign-in tab on roblox.com/login.</summary>
    public async Task ShowSignInAsync()
    {
        _wanted = null;
        if (_signIn is null)
        {
            _signIn = NewTab(null);
            try
            {
                await _signIn.InitAsync(await Environment(), null, "https://www.roblox.com/login");
            }
            catch (Exception e)
            {
                Log.Error("Could not open sign-in tab", e);
                CloseSignIn();
                throw;
            }
        }
        if (!_disposed && _wanted is null && _signIn is not null) Activate(_signIn);
    }

    /// <summary>The sign-in tab signed into <paramref name="userId"/>: it becomes that account's tab.</summary>
    public async Task AdoptSignInAsync(SessionTab tab, long userId, string session)
    {
        if (tab != _signIn) return;
        _signIn = null;
        if (_tabs.Remove(userId, out var old))
        {
            _pool.Remove(userId);
            Dispose(old); // its session is the old one: replaced by this sign-in
        }
        tab.Bind(userId, session);
        _tabs[userId] = tab;
        await CloseAsync(_pool.Touch(userId));
    }

    /// <summary>The site inside an account tab switched to another account: re-key the tab.</summary>
    public void Rekey(SessionTab tab, long newUserId, string session)
    {
        if (tab.UserId is not { } oldId || oldId == newUserId) return;
        _tabs.Remove(oldId);
        _pool.Remove(oldId);
        if (_tabs.Remove(newUserId, out var other)) { _pool.Remove(newUserId); Dispose(other); }
        tab.Bind(newUserId, session);
        _tabs[newUserId] = tab;
        _ = CloseAsync(_pool.Touch(newUserId));
    }

    public void CloseSignIn()
    {
        if (_signIn is null) return;
        var t = _signIn;
        _signIn = null;
        Dispose(t);
    }

    public async Task CloseAccountAsync(long userId, bool sync = true)
    {
        if (!_tabs.TryGetValue(userId, out var tab)) return;
        if (sync) await tab.SyncNowAsync();
        _tabs.Remove(userId);
        _pool.Remove(userId);
        Dispose(tab);
    }

    /// <summary>Syncs every live tab's cookie back (before locking or exiting).</summary>
    public async Task SyncAllAsync()
    {
        foreach (var tab in _tabs.Values.ToList())
            await tab.SyncNowAsync();
    }

    public void CloseAll()
    {
        foreach (var tab in _tabs.Values.ToList()) Dispose(tab);
        _tabs.Clear();
        _pool.Clear();
        CloseSignIn();
        Active = null;
        ActiveChanged?.Invoke();
    }

    private SessionTab NewTab(long? userId)
    {
        var tab = new SessionTab(userId);
        tab.View.Visible = true;
        _container.Controls.Add(tab.View);
        tab.View.SendToBack();
        TabCreated?.Invoke(tab);
        return tab;
    }

    private void Activate(SessionTab tab)
    {
        tab.View.Visible = true;
        tab.View.BringToFront();
        foreach (Control c in _container.Controls)
            if (c != tab.View && c is Microsoft.Web.WebView2.WinForms.WebView2)
                c.Visible = false;
        Active = tab;
        ActiveChanged?.Invoke();
    }

    private async Task CloseAsync(IReadOnlyList<long> evicted)
    {
        foreach (long id in evicted)
        {
            if (!_tabs.TryGetValue(id, out var tab)) continue;
            if (tab == Active) { _pool.Touch(id); continue; } // never close what is on screen
            await tab.SyncNowAsync();
            if (_tabs.TryGetValue(id, out var still) && still == tab && tab != Active)
            {
                _tabs.Remove(id);
                Dispose(tab);
            }
        }
    }

    private void Dispose(SessionTab tab)
    {
        if (Active == tab)
        {
            Active = null;
            ActiveChanged?.Invoke();
        }
        _container.Controls.Remove(tab.View);
        tab.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAll();
    }
}
