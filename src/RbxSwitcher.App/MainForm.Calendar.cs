using RbxSwitcher.App.UI;
using RbxSwitcher.Core.Accounts;
using RbxSwitcher.Core.Limiteds;
using RbxSwitcher.Core.Logging;

namespace RbxSwitcher.App;

/// <summary>
/// The "Calendário" category: when the Robux of the accounts' Limited sales should fall (Roblox keeps them pending
/// for a while after each sale), day by day and per account. Read-only: it reads each account's sales and learns, from
/// the sales it sees going from pending to credited, how long each account's Robux stay pending.
/// </summary>
internal sealed partial class MainForm
{
    private const int ViewCalendario = 5;

    private readonly CalendarView _calendarPage = new() { Visible = false };
    private bool _calendarBusy;

    private void WireCalendar()
    {
        _calendarPage.RefreshRequested += () => _ = RefreshCalendarSalesAsync(force: true);
        // Item pictures: Roblox's public thumbnails, in the cache the other tabs use (key "a" + asset id).
        _calendarPage.ThumbOf = assetId => _thumbs.GetValueOrDefault("a" + assetId);
        _calendarPage.NeedThumbs += items => _ = LoadThumbsAsync(
            [.. items.DistinctBy(i => i.AssetId).Select(i => new RbxSwitcher.Core.Avatars.AvatarItem(RbxSwitcher.Core.Avatars.ItemKind.Asset, i.AssetId, i.Name, 0, 0, null, null, false))]);
    }

    private void UpdateCalendar()
    {
        if (_vault is null || !_calendarPage.Visible) return;
        var inputs = _vault.Accounts.All.Select(a => new CalendarInput(a.UserId, a.Username, a.Sales, a.Finance?.PendingRobux)).ToList();
        _calendarPage.SetState(RobuxCalendar.Build(inputs, DateTimeOffset.UtcNow), _calendarBusy,
            neverRead: _vault.Accounts.All.All(a => a.Sales?.ReadUtc is null));
    }

    private void OnCalendarShown()
    {
        UpdateCalendar();
        // Sales not read for a while: read them again in the background (each account at most every 25 min).
        if (_vault is not null && CalendarAccounts().Any(a => a.Sales?.LastAttemptUtc is not { } at || DateTimeOffset.UtcNow - at >= SalesInterval))
            _ = RefreshCalendarSalesAsync(force: false);
    }

    /// <summary>Accounts whose sales matter for the calendar: with Limiteds, with Robux still pending, or read before.</summary>
    private List<Account> CalendarAccounts() => _vault!.Accounts.All
        .Where(a => a.HasSession && a.Status != SessionStatus.Expired && (a.Inventory?.Copies.Count > 0 || HasPendingRobux(a) || a.Sales is not null))
        .ToList();

    /// <summary>Reads the sales of those accounts, one at a time (respects the pause after Roblox's request limit).</summary>
    private async Task RefreshCalendarSalesAsync(bool force)
    {
        if (_vault is not { } vault || _calendarBusy) return;
        _calendarBusy = true;
        UpdateCalendar();
        try
        {
            var accounts = CalendarAccounts();
            int i = 0;
            foreach (var a in accounts)
            {
                if (_closing || _vault != vault) return;
                await WaitRobloxPauseAsync("as leituras de vendas");
                bool due = force || a.Sales?.LastAttemptUtc is not { } at || DateTimeOffset.UtcNow - at >= SalesInterval;
                i++;
                if (!due) continue;
                SetStatus($"Calendário: lendo as vendas de @{a.Username} ({i}/{accounts.Count})…");
                await ReadSalesAsync(a, force: true);
                await Task.Delay(300);
            }
            if (_vault == vault) SetStatus($"Calendário: vendas lidas ({Fmt.Count(accounts.Count, "conta", "contas")}).");
        }
        catch (Exception ex) { Log.Error("Calendar sales refresh failed", ex); }
        finally
        {
            _calendarBusy = false;
            if (!_closing) UpdateCalendar();
        }
    }
}
