using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RbxSwitcher.App.Platform;
using RbxSwitcher.Core.Logging;
using RbxSwitcher.Core.Roblox;

namespace RbxSwitcher.App.Browser;

/// <summary>
/// One account's browser: a WebView2 control on its own InPrivate profile. Each tab gets a
/// unique profile name, so cookies, storage and cache of one account can never be seen by
/// another tab. InPrivate means the browser itself writes nothing of the session to disk:
/// the only persistent copy is the one in the encrypted vault.
///
/// A tab without a user id is a sign-in tab: the person signs in on roblox.com itself
/// (password, 2-step, captcha all handled by Roblox), and the tab reports the session cookie
/// once Roblox sets it.
/// </summary>
internal sealed class SessionTab : IDisposable
{
    private static readonly string[] SignOutPaths = ["/v1/logout", "/v2/logout"];

    private readonly System.Windows.Forms.Timer _poll = new();
    private string _knownSession = "";
    private bool _disposed, _reading;

    public WebView2 View { get; }
    public long? UserId { get; private set; }
    private bool _ready;
    public bool Ready => _ready && !_disposed;
    public string Url => Ready && View.CoreWebView2 is { } c ? c.Source : "";
    public bool CanGoBack => Ready && View.CoreWebView2.CanGoBack;
    public bool CanGoForward => Ready && View.CoreWebView2.CanGoForward;

    /// <summary>Sign-in tab only: Roblox set a session cookie (not yet verified).</summary>
    public event Action<SessionTab, string>? SessionAppeared;
    /// <summary>Account tab: the cookie in the browser changed (Roblox rotated it, or the site switched account).</summary>
    public event Action<SessionTab, string>? SessionChanged;
    /// <summary>Account tab: the session cookie disappeared from the browser.</summary>
    public event Action<SessionTab>? SessionVanished;
    /// <summary>A "Log out" on the site was stopped, because it would end the saved session too.</summary>
    public event Action<SessionTab>? SignOutBlocked;
    public event Action<SessionTab>? NavigationChanged;
    /// <summary>
    /// roblox.com asked to open the Roblox app (roblox-player: / roblox://). The default WebView2 prompt is
    /// replaced by the app's own confirmation, which names the account; the link itself is never changed.
    /// Arguments: tab, link exactly as the site made it, initiating origin, user-initiated.
    /// </summary>
    public event Action<SessionTab, string, string?, bool>? PlayerLaunchRequested;

    /// <summary>The official Roblox Plus page (roblox.com/plus, also /pt/plus etc.) finished loading in this tab.</summary>
    public event Action<SessionTab>? PlusPageOpened;
    /// <summary>While the tab is on the Plus page, a roblox.com response said yes/no about the free trial.</summary>
    public event Action<SessionTab, bool>? TrialSignal;

    private bool _checkingOffer;

    /// <summary>
    /// The official Plus page itself shows the free-trial offer ("Try it for free", "free trial ends on …"). When it
    /// does, that is reported as a yes. Only a yes/nothing comes back from the page; no page text is kept.
    /// The page fills in after loading, so it is looked at a few times.
    /// </summary>
    private async Task CheckTrialOfferOnPageAsync()
    {
        if (_checkingOffer || !Ready) return;
        _checkingOffer = true;
        const string script = """
            (() => {
              const t = ((document.body && document.body.innerText) || '').toLowerCase();
              const offer = [/try it for free/, /free trial ends on/, /start (your )?free trial/,
                             /experimente gr[aá]tis/, /teste gr[aá]tis/, /avalia[cç][aã]o gratuita/,
                             /per[ií]odo de teste gratuito/, /teste gratuito de/, /prueba gratis/];
              return offer.some(r => r.test(t));
            })()
            """;
        try
        {
            for (int attempt = 0; attempt < 8 && Ready && IsPlusPage(View.CoreWebView2.Source); attempt++)
            {
                await Task.Delay(1500);
                if (!Ready || !IsPlusPage(View.CoreWebView2.Source)) break;
                string result = await View.CoreWebView2.ExecuteScriptAsync(script);
                if (result == "true")
                {
                    Core.Logging.Log.Info("[plus] a página oficial mostra a oferta de teste grátis");
                    TrialSignal?.Invoke(this, true);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
        finally { _checkingOffer = false; }
    }

    public static bool IsPlusPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && IsRobloxHost(u.Host) &&
        System.Text.RegularExpressions.Regex.IsMatch(u.AbsolutePath, @"^(/[a-z]{2}(-[a-z]{2})?)?/plus/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private Action<Core.Logging.ApiCall>? _apiSink;
    private bool _apiHooked;

    /// <summary>
    /// Records, value-free, the JSON calls roblox.com pages make in this tab (see <see cref="Core.Logging.ApiCalls"/>).
    /// Used to learn which official endpoint a page uses; nothing is recorded when not started.
    /// </summary>
    public void StartApiRecording(Action<Core.Logging.ApiCall> sink)
    {
        if (!Ready) return;
        _apiSink = sink;
        HookResponses();
    }

    /// <summary>Hooked once per tab; responses are only read while recording or while on the Plus page.</summary>
    private void HookResponses()
    {
        if (_apiHooked || !Ready) return;
        View.CoreWebView2.WebResourceResponseReceived += OnApiResponse;
        _apiHooked = true;
    }

    public void StopApiRecording() => _apiSink = null;

    /// <summary>A trade was sent or accepted on roblox.com in this tab (by the person; see <see cref="Core.Limiteds.TradeSignals"/>).</summary>
    public event Action<SessionTab, Core.Limiteds.TradeSignal>? TradeSignalReceived;

    private async Task CheckTradeSignalAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            int status = e.Response.StatusCode;
            if (status is < 200 or >= 300) return;
            string page = View.CoreWebView2.Source;
            string? body = null;
            using (var stream = await e.Response.GetContentAsync())
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    var buffer = new char[65_536];
                    int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                    body = new string(buffer, 0, read); // only the trade id is read from it
                }
            if (!_disposed && Core.Limiteds.TradeSignals.From(e.Request.Method, e.Request.Uri, status, body, page) is { } signal)
                TradeSignalReceived?.Invoke(this, signal);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException or IOException) { }
    }

    private async void OnApiResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var sink = _apiSink;
        if (_disposed || !Ready) return;
        if (Core.Limiteds.TradeSignals.IsTradeCall(e.Request.Method, e.Request.Uri)) { await CheckTradeSignalAsync(e); return; }
        bool onPlusPage = IsPlusPage(View.CoreWebView2.Source);
        if (sink is null && !onPlusPage) return;
        try
        {
            string uri = e.Request.Uri;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !IsRobloxHost(u.Host)) return;
            var headers = e.Response.Headers;
            string? type = headers.Contains("Content-Type") ? headers.GetHeader("Content-Type") : null;
            if (type is null || !type.Contains("json", StringComparison.OrdinalIgnoreCase)) return;
            string method = e.Request.Method;
            int status = e.Response.StatusCode;
            string? body = null;
            using (var stream = await e.Response.GetContentAsync())
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    var buffer = new char[512 * 1024 + 1];
                    int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                    body = read > 512 * 1024 ? null : new string(buffer, 0, read); // too big: path and status only
                }
            if (Core.Logging.ApiCalls.From(method, uri, status, type, body) is not { } call) return;
            if (onPlusPage && call.TrialEligible is { } eligible) TrialSignal?.Invoke(this, eligible);
            if (_apiSink is { } live) live(call);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException or IOException) { }
    }

    public SessionTab(long? userId)
    {
        UserId = userId;
        View = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = UI.Theme.Window,
        };
        _poll.Tick += async (_, _) => await PollCookieAsync();
    }

    public async Task InitAsync(CoreWebView2Environment env, string? session, string startUrl)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "t" + Guid.NewGuid().ToString("N"); // unique: never shared with another tab
        options.IsInPrivateModeEnabled = true;
        await View.EnsureCoreWebView2Async(env, options);
        if (_disposed) return;
        var core = View.CoreWebView2;

        var s = core.Settings;
        s.AreDevToolsEnabled = false;          // blocks the "paste this in the console" scam and casual cookie reading
        s.IsPasswordAutosaveEnabled = false;   // passwords are never kept, by us or by the browser
        s.IsGeneralAutofillEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        s.IsStatusBarEnabled = true;
        s.AreDefaultContextMenusEnabled = true;

        if (session is not null)
        {
            var cookie = core.CookieManager.CreateCookie(RobloxApi.CookieName, session, ".roblox.com", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true;          // page scripts cannot read it
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            core.CookieManager.AddOrUpdateCookie(cookie);
            _knownSession = session;
        }

        core.AddWebResourceRequestedFilter("https://auth.roblox.com/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => OnRequest(env, e);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindow;
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            // Nothing leaves the app without the person confirming in RbxSwitcher's dialog.
            e.Cancel = true;
            string uri = e.Uri, origin = e.InitiatingOrigin;
            bool userInitiated = e.IsUserInitiated;
            View.BeginInvoke(() => PlayerLaunchRequested?.Invoke(this, uri, origin, userInitiated));
        };
        core.SourceChanged += (_, _) =>
        {
            NavigationChanged?.Invoke(this);
            // In-site navigation (clicking "Roblox Plus" in the site menu) changes the address without a new page load.
            if (IsPlusPage(core.Source)) _ = CheckTrialOfferOnPageAsync();
        };
        core.HistoryChanged += (_, _) => NavigationChanged?.Invoke(this);
        core.NavigationCompleted += async (_, e) =>
        {
            NavigationChanged?.Invoke(this);
            if (e.IsSuccess && IsPlusPage(core.Source)) { PlusPageOpened?.Invoke(this); _ = CheckTrialOfferOnPageAsync(); }
            await PollCookieAsync();
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Warn($"WebView2 process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                try { core.Reload(); } catch { }
        };

        _ready = true;
        HookResponses(); // from the first page, so the Plus page's own calls during loading are seen
        // Sign-in tabs check often (to catch the moment Roblox signs you in); account tabs just keep the vault in sync.
        _poll.Interval = UserId is null ? 1000 : 20_000;
        _poll.Start();
        core.Navigate(startUrl);
    }

    /// <summary>Turns a sign-in tab into the tab of the account it signed into.</summary>
    public void Bind(long userId, string session)
    {
        UserId = userId;
        _knownSession = session;
        _poll.Interval = 20_000;
    }

    /// <summary>The account's tab adopted a new session (rotation accepted by the app).</summary>
    public void AcceptSession(string session) => _knownSession = session;

    /// <summary>Sign-in tab: report the current cookie again on the next poll (the last check could not reach Roblox).</summary>
    public void RetryReport() => _knownSession = "";

    /// <summary>Puts a newer session (rotated by Roblox outside the browser) into this tab's profile.</summary>
    public void ReplaceCookie(string session)
    {
        if (!Ready || _disposed || !RobloxApi.LooksLikeSession(session)) return;
        var cm = View.CoreWebView2.CookieManager;
        var cookie = cm.CreateCookie(RobloxApi.CookieName, session, ".roblox.com", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
        cm.AddOrUpdateCookie(cookie);
        _knownSession = session;
    }

    /// <summary>
    /// Opens the first page that loads without an HTTP error (404 etc.); returns it, or null if none did.
    /// Only https://*.roblox.com pages, as with <see cref="Navigate"/>.
    /// </summary>
    public async Task<string?> NavigateFirstAvailableAsync(IReadOnlyList<string> urls)
    {
        foreach (string url in urls)
        {
            if (!Ready || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsRobloxHost(u.Host) || u.Scheme != Uri.UriSchemeHttps) continue;
            var done = new TaskCompletionSource<(bool Ok, int Status)>();
            void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult((e.IsSuccess, e.HttpStatusCode));
            View.CoreWebView2.NavigationCompleted += OnDone;
            try
            {
                View.CoreWebView2.Navigate(u.AbsoluteUri);
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                var (ok, status) = finished == done.Task ? done.Task.Result : (false, 0);
                Core.Logging.Log.Info($"[quick login] {u.AbsolutePath}: {(finished == done.Task ? "HTTP " + status : "sem resposta em 20 s")}");
                if (finished == done.Task && Core.Roblox.QuickLogin.Loaded(ok, status)) return url;
            }
            finally
            {
                if (!_disposed && Ready) View.CoreWebView2.NavigationCompleted -= OnDone;
            }
        }
        return null;
    }

    /// <summary>
    /// Opens a roblox.com page and waits for THAT navigation to finish (false: error, timeout or not allowed). Other
    /// navigations are ignored: a tab just created is still loading its start page, and that load, cancelled by this
    /// one, reports "finished, not successful" first.
    /// </summary>
    public async Task<bool> NavigateAndWaitAsync(string url, TimeSpan timeout)
    {
        if (!Ready || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsRobloxHost(u.Host) || u.Scheme != Uri.UriSchemeHttps) return false;
        var done = new TaskCompletionSource<bool>();
        ulong? mine = null;
        void OnStart(object? s, CoreWebView2NavigationStartingEventArgs e)
        {
            // Ours: the first navigation to this address (a redirect, e.g. to /pt/…, keeps the same id).
            if (mine is null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var started) &&
                string.Equals(started.Host, u.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(started.AbsolutePath.TrimEnd('/'), u.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                mine = e.NavigationId;
        }
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (mine is { } id && e.NavigationId == id) done.TrySetResult(e.IsSuccess);
        }
        var core = View.CoreWebView2;
        core.NavigationStarting += OnStart;
        core.NavigationCompleted += OnDone;
        try
        {
            core.Navigate(u.AbsoluteUri);
            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
            return finished == done.Task && done.Task.Result;
        }
        finally
        {
            if (!_disposed && Ready) { core.NavigationStarting -= OnStart; core.NavigationCompleted -= OnDone; }
        }
    }

    /// <summary>
    /// Runs a script in the page and returns its JSON-encoded result; null if the tab is gone or the page refused it.
    /// Only for roblox.com pages (the tab never leaves them).
    /// </summary>
    public async Task<string?> EvaluateAsync(string script)
    {
        if (!Ready || !Uri.TryCreate(Url, UriKind.Absolute, out var u) || !IsRobloxHost(u.Host)) return null;
        try { return await View.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { return null; }
    }

    public void Navigate(string url)
    {
        if (Ready && Uri.TryCreate(url, UriKind.Absolute, out var u) && IsRobloxHost(u.Host) && u.Scheme == Uri.UriSchemeHttps)
            View.CoreWebView2.Navigate(u.AbsoluteUri);
    }

    public void GoBack() { if (CanGoBack) View.CoreWebView2.GoBack(); }
    public void GoForward() { if (CanGoForward) View.CoreWebView2.GoForward(); }
    public void Reload() { if (Ready) View.CoreWebView2.Reload(); }

    /// <summary>Reads the current session cookie from this tab's profile (UI thread).</summary>
    public async Task<string?> ReadSessionAsync()
    {
        if (!Ready || _disposed) return null;
        try
        {
            var cookies = await View.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            return cookies.FirstOrDefault(c => c.Name == RobloxApi.CookieName && c.Value.Length > 0)?.Value;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Called by the host before closing the tab, so a rotation that happened in the browser is not lost.</summary>
    public Task SyncNowAsync() => PollCookieAsync();

    private async Task PollCookieAsync()
    {
        if (_reading || _disposed || !Ready) return;
        _reading = true;
        try
        {
            string? now = await ReadSessionAsync();
            if (_disposed) return;
            if (UserId is null)
            {
                if (now is not null && now != _knownSession && RobloxApi.LooksLikeSession(now))
                {
                    _knownSession = now; // report each new value once
                    SessionAppeared?.Invoke(this, now);
                }
            }
            else if (now is null)
            {
                if (_knownSession.Length > 0)
                {
                    _knownSession = ""; // report once; a new sign-in in this tab shows up as SessionChanged
                    SessionVanished?.Invoke(this);
                }
            }
            else if (now != _knownSession && RobloxApi.LooksLikeSession(now))
            {
                SessionChanged?.Invoke(this, now);
            }
        }
        finally { _reading = false; }
    }

    private void OnRequest(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        // Only saved accounts are protected; a sign-in tab may sign out freely.
        if (UserId is null) return;
        if (!e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u)) return;
        if (!SignOutPaths.Contains(u.AbsolutePath.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)) return;
        e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: application/json");
        View.BeginInvoke(() => SignOutBlocked?.Invoke(this));
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) { e.Cancel = true; return; }
        switch (u.Scheme)
        {
            case "https" when IsRobloxHost(u.Host):
                return;
            case "https":
                // Other sites open in the person's normal browser, without this account's session.
                e.Cancel = true;
                Native.OpenExternal(u);
                return;
            case "http" when IsRobloxHost(u.Host):
                e.Cancel = true;
                View.CoreWebView2.Navigate("https" + u.AbsoluteUri[4..]);
                return;
            case "about":
            case "roblox-player": // "Play": WebView2 asks the person before opening the Roblox app
            case "roblox":
                return;
            default:
                e.Cancel = true; // file:, data:, javascript:, other apps' protocols
                return;
        }
    }

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // no pop-up windows: they would escape the tab's controls
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u))
        {
            if (u.Scheme == Uri.UriSchemeHttps && IsRobloxHost(u.Host)) View.CoreWebView2.Navigate(u.AbsoluteUri);
            else if (u.Scheme is "roblox" or "roblox-player")
            {
                // window.open("roblox://…"): same confirmation as a normal app link, never dropped silently.
                string origin = Uri.TryCreate(View.CoreWebView2.Source, UriKind.Absolute, out var src) ? src.GetLeftPart(UriPartial.Authority) : "";
                string uri = e.Uri;
                bool clicked = e.IsUserInitiated;
                View.BeginInvoke(() => PlayerLaunchRequested?.Invoke(this, uri, origin, clicked));
            }
            else Native.OpenExternal(u);
        }
    }

    public static bool IsRobloxHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _poll.Dispose();
        _knownSession = "";
        View.Dispose(); // closes the InPrivate profile: its cookies are gone with it
    }
}
