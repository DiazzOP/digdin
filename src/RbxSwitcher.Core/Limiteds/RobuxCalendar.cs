using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// A pending sale seen credited: its Robux fell between the read that still had it pending and the first read that did
/// not. Roblox does not say when a pending sale will be credited, so these sightings are how the app learns it.
/// </summary>
public sealed record PendingRelease(string SaleId, DateTimeOffset SaleUtc, DateTimeOffset SeenPendingUtc, DateTimeOffset SeenCreditedUtc,
                                    long Amount, long AssetId, string Name)
{
    /// <summary>The sale was still pending this long after it was made.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan Low => SeenPendingUtc - SaleUtc;
    /// <summary>The sale was credited at most this long after it was made.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan High => SeenCreditedUtc - SaleUtc;
    /// <summary>The two reads were close together, so the moment it fell is well known.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Precise => SeenCreditedUtc - SeenPendingUtc <= RobuxCalendar.PreciseWindow;
}

public enum HoldBasis
{
    /// <summary>Releases of this account the app saw.</summary>
    AccountHistory,
    /// <summary>This account's current sales: the pending ones are younger than the credited ones.</summary>
    AccountSales,
    /// <summary>Releases the app saw in every account.</summary>
    AllHistory,
    /// <summary>Nothing seen yet.</summary>
    Default,
}

/// <summary>How long Robux from one account's sales stay pending: the figure used and where it came from.</summary>
/// <param name="Low">Shortest wait seen (or that fits the reads), when known.</param>
/// <param name="High">Longest wait seen (or that fits the reads); Roblox's own limit (30 days) when nothing is known.</param>
public sealed record HoldEstimate(TimeSpan Typical, TimeSpan? Low, TimeSpan High, HoldBasis Basis, int Samples);

/// <summary>One sale on the calendar: expected to fall (pending) or seen falling (credited).</summary>
/// <param name="ExpectedUtc">Pending: when it should fall. Credited: when the app saw it credited.</param>
/// <param name="Late">Pending and already past the expected moment (shown on today).</param>
public sealed record CalendarEntry(long AccountId, string AccountName, string SaleId, long AssetId, string Name, long Amount, DateTimeOffset SaleUtc,
                                   DateTimeOffset ExpectedUtc, DateTimeOffset? EarliestUtc, DateTimeOffset LatestUtc, bool Fell, bool Late);

/// <param name="Expected">Robux expected to fall that day (pending sales).</param>
/// <param name="Fell">Robux seen falling that day.</param>
public sealed record CalendarDay(DateOnly Day, long Expected, int ExpectedCount, long Fell, int FellCount, IReadOnlyList<CalendarEntry> Entries);

/// <summary>What the calendar cannot place for an account.</summary>
public enum PendingGap
{
    None,
    /// <summary>Its sales were never read.</summary>
    NotRead,
    /// <summary>Roblox's summary has more pending Robux than its Limited sales (another source, or an older sale): no date.</summary>
    Unplaced,
    /// <summary>Roblox's summary has less than its pending sales: one of them already fell (the next read confirms).</summary>
    SummaryLower,
}

/// <param name="PendingTotal">The account's pending Robux in Roblox's summary (all sources), when read.</param>
/// <param name="FromSales">Pending Robux of the item sales the app read.</param>
/// <param name="Note">What the calendar cannot place for this account, in Portuguese.</param>
/// <param name="Gap">The same as <paramref name="Note"/>, as a kind (Unplaced: PendingTotal − FromSales Robux).</param>
public sealed record AccountPendingView(long AccountId, string AccountName, long? PendingTotal, long FromSales, int PendingSales, HoldEstimate Hold,
                                        CalendarEntry? Next, string? Note, PendingGap Gap = PendingGap.None);

public sealed record RobuxCalendarView(IReadOnlyDictionary<DateOnly, CalendarDay> Days, IReadOnlyList<AccountPendingView> Accounts,
                                       long PendingFromSales, int PendingCount, long DueToday, long DueNext7Days, DateOnly Today,
                                       HoldEstimate Overall, DateTimeOffset? OldestRead);

/// <param name="PendingRobux">The account's pending Robux in Roblox's summary (finance read).</param>
public sealed record CalendarInput(long AccountId, string AccountName, AccountSales? Sales, long? PendingRobux);

/// <summary>
/// When the Robux of the accounts' Limited sales should fall. Roblox keeps a sale's Robux pending for a while (it says
/// "up to 30 days"; in practice it varies per account) and its records only say whether a sale is still pending. So the
/// wait is learned: each sale seen going from pending to credited is a sighting; an account's own sightings come first,
/// then what its current sales show (pending ones younger than credited ones), then every account's sightings, and
/// only without any of that a default of <see cref="DefaultHold"/>.
/// </summary>
public static class RobuxCalendar
{
    /// <summary>Two reads this close bracket the moment a sale fell well enough.</summary>
    public static readonly TimeSpan PreciseWindow = TimeSpan.FromHours(6);
    /// <summary>Without any sighting: a week (what most accounts see; Roblox's limit is 30 days).</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromDays(7);
    /// <summary>Roblox's own limit for pending Robux.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromDays(30);
    /// <summary>Sales are read this far back so every pending one is in the list.</summary>
    public static readonly TimeSpan ReadWindow = MaxHold + TimeSpan.FromDays(1);
    private const int KeepReleases = 150;
    private static readonly TimeSpan KeepReleasesFor = TimeSpan.FromDays(120);

    /// <summary>
    /// Applies a new read of an account's sales: pending sales of the last read that are now credited become sightings,
    /// then the records are replaced.
    /// </summary>
    public static void ApplyRead(AccountSales sales, IReadOnlyList<SaleRecord> fresh, DateTimeOffset now)
    {
        if (sales.ReadUtc is { } before && before < now)
        {
            var byId = fresh.DistinctBy(r => r.Id).ToDictionary(r => r.Id);
            var seen = sales.Releases.Select(r => r.SaleId).ToHashSet();
            foreach (var old in sales.Records.Where(r => r.Pending && !seen.Contains(r.Id)))
                if (byId.TryGetValue(old.Id, out var f) && !f.Pending)
                    sales.Releases.Add(new PendingRelease(old.Id, old.CreatedUtc, before, now, f.Amount, f.AssetId, f.Name));
        }
        sales.Releases = sales.Releases.Where(r => now - r.SeenCreditedUtc <= KeepReleasesFor)
            .OrderByDescending(r => r.SeenCreditedUtc).Take(KeepReleases).ToList();
        sales.Records = [.. fresh];
        sales.ReadUtc = now;
        sales.Problem = null;
    }

    /// <summary>The wait for one account (<paramref name="own"/>), with every account's sales as a fallback.</summary>
    public static HoldEstimate EstimateHold(AccountSales? own, IEnumerable<AccountSales?> all)
    {
        if (FromHistory(own?.Releases ?? [], HoldBasis.AccountHistory) is { } mine) return mine;
        var base_ = FromHistory(all.SelectMany(s => s?.Releases ?? []), HoldBasis.AllHistory)
                    ?? new HoldEstimate(DefaultHold, null, MaxHold, HoldBasis.Default, 0);
        // The account's own sales narrow it: still pending after `lo`, credited by `hi`.
        if (FromSales(own) is { } b)
        {
            var typical = base_.Typical < b.Lo ? b.Hi : base_.Typical > b.Hi ? b.Hi : base_.Typical;
            return new HoldEstimate(typical, b.Lo, b.Hi, HoldBasis.AccountSales, b.Count);
        }
        return base_;
    }

    private static HoldEstimate? FromHistory(IEnumerable<PendingRelease> releases, HoldBasis basis)
    {
        var all = releases.DistinctBy(r => r.SaleId).ToList();
        var precise = all.Where(r => r.Precise).ToList();
        var use = precise.Count >= 2 ? precise : all;
        if (use.Count < 2) return null;
        var highs = use.Select(r => r.High).Order().ToList();
        // The upper middle: Robux usually fall by then (a calendar that is a little late is safer than an early one).
        return new HoldEstimate(highs[highs.Count / 2], use.Min(r => r.Low), use.Max(r => r.High), basis, use.Count);
    }

    /// <summary>
    /// From one read of an account's sales: every pending sale is younger than every credited one, so the wait is
    /// longer than the oldest pending sale and at most the youngest credited one. Mixed ages: nothing can be said.
    /// </summary>
    private static (TimeSpan Lo, TimeSpan Hi, int Count)? FromSales(AccountSales? s)
    {
        if (s?.ReadUtc is not { } read) return null;
        var pending = s.Records.Where(r => r.Pending).Select(r => read - r.CreatedUtc).ToList();
        var credited = s.Records.Where(r => !r.Pending).Select(r => read - r.CreatedUtc).ToList();
        if (pending.Count == 0 || credited.Count == 0) return null;
        TimeSpan lo = pending.Max(), hi = credited.Min();
        if (hi <= lo || hi > MaxHold) return null;
        return (lo, hi, pending.Count + credited.Count);
    }

    public static RobuxCalendarView Build(IReadOnlyList<CalendarInput> accounts, DateTimeOffset now)
    {
        var today = Day(now);
        var allSales = accounts.Select(a => a.Sales).ToList();
        var entries = new List<CalendarEntry>();
        var views = new List<AccountPendingView>();
        foreach (var a in accounts)
        {
            var hold = EstimateHold(a.Sales, allSales);
            var pending = a.Sales?.Records.Where(r => r.Pending).DistinctBy(r => r.Id).ToList() ?? [];
            var mine = new List<CalendarEntry>();
            foreach (var p in pending)
            {
                var expected = p.CreatedUtc + hold.Typical;
                bool late = expected < now;
                // Past the estimate: it can fall any time until Roblox's own limit.
                mine.Add(new CalendarEntry(a.AccountId, a.AccountName, p.Id, p.AssetId, p.Name, p.Amount, p.CreatedUtc,
                    late ? now : expected, hold.Low is { } lo ? p.CreatedUtc + lo : null, p.CreatedUtc + (late ? MaxHold : hold.High), false, late));
            }
            foreach (var r in a.Sales?.Releases ?? [])
                mine.Add(new CalendarEntry(a.AccountId, a.AccountName, r.SaleId, r.AssetId, r.Name, r.Amount, r.SaleUtc,
                    r.SeenCreditedUtc, r.SeenPendingUtc, r.SeenCreditedUtc, true, false));
            entries.AddRange(mine);

            long fromSales = pending.Sum(p => p.Amount);
            bool relevant = pending.Count > 0 || a.PendingRobux is > 0 || (a.Sales?.Releases.Any(r => now - r.SeenCreditedUtc <= ReadWindow) ?? false);
            if (!relevant) continue;
            string? note = a.Sales is null ? "vendas ainda não lidas: os pendentes desta conta ficam sem data"
                : a.PendingRobux is { } total && total > fromSales
                    ? $"{Fmt(total - fromSales)} pendentes sem venda de Limited na lista (game pass, outra fonte ou venda de mais de 31 dias): sem data"
                : a.PendingRobux is { } t2 && t2 < fromSales
                    ? "o resumo do Roblox tem menos pendentes que as vendas: alguma já caiu (a próxima leitura confirma)"
                : null;
            var gap = a.Sales is null ? PendingGap.NotRead
                : a.PendingRobux is { } t3 && t3 > fromSales ? PendingGap.Unplaced
                : a.PendingRobux is { } t4 && t4 < fromSales ? PendingGap.SummaryLower
                : PendingGap.None;
            var next = mine.Where(e => !e.Fell).OrderBy(e => e.ExpectedUtc).FirstOrDefault();
            views.Add(new AccountPendingView(a.AccountId, a.AccountName, a.PendingRobux, fromSales, pending.Count, hold, next, note, gap));
        }

        var days = entries.GroupBy(e => Day(e.ExpectedUtc)).ToDictionary(g => g.Key, g =>
        {
            var list = g.OrderBy(e => e.Fell).ThenBy(e => e.ExpectedUtc).ThenBy(e => e.AccountName, StringComparer.OrdinalIgnoreCase).ToList();
            return new CalendarDay(g.Key, list.Where(e => !e.Fell).Sum(e => e.Amount), list.Count(e => !e.Fell),
                                   list.Where(e => e.Fell).Sum(e => e.Amount), list.Count(e => e.Fell), list);
        });
        var due = entries.Where(e => !e.Fell).ToList();
        var overall = FromHistory(allSales.SelectMany(s => s?.Releases ?? []), HoldBasis.AllHistory)
                      ?? new HoldEstimate(DefaultHold, null, MaxHold, HoldBasis.Default, 0);
        var reads = accounts.Where(a => a.PendingRobux is > 0 || a.Sales?.Records.Any(r => r.Pending) == true)
            .Select(a => a.Sales?.ReadUtc).ToList();
        return new RobuxCalendarView(days, views.OrderBy(v => v.Next?.ExpectedUtc ?? DateTimeOffset.MaxValue).ThenBy(v => v.AccountName, StringComparer.OrdinalIgnoreCase).ToList(),
            due.Sum(e => e.Amount), due.Count, due.Where(e => Day(e.ExpectedUtc) == today).Sum(e => e.Amount),
            due.Where(e => Day(e.ExpectedUtc) is var d && d >= today && d <= today.AddDays(6)).Sum(e => e.Amount), today, overall,
            reads.Count == 0 || reads.Any(r => r is null) ? null : reads.Min());
    }

    /// <summary>The civil day in Brasília.</summary>
    public static DateOnly Day(DateTimeOffset utc) => DateOnly.FromDateTime(BrasiliaTime.ToBrasilia(utc).DateTime);

    private static string Fmt(long n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
}
