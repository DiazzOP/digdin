namespace RbxSwitcher.Core.Finance;

public enum CapacityState
{
    /// <summary>All inputs fresh and complete: the number is the panel's estimate.</summary>
    Estimated,
    /// <summary>Some input is missing, stale, failed or Plus is not active: show "a conferir".</summary>
    ToVerify,
}

public sealed record MonthlyRelease(DateTimeOffset AtUtc, long Amount);

/// <summary>What the screen shows for one account. Roblox values and panel estimates are kept apart.</summary>
public sealed record FinanceView
{
    // Roblox values
    public long? Robux { get; init; }
    public long? PendingRobux { get; init; }
    public long? CapDaily { get; init; }
    public long? CapMonthly { get; init; }
    public long? PerTransferLimit { get; init; }
    public long? RestrictionDaily { get; init; }
    public long? RestrictionMonthly { get; init; }
    public PlusInfo? Plus { get; init; }

    // Panel estimates
    public long? EffectiveDaily { get; init; }
    public long? EffectiveMonthly { get; init; }
    public long? SentToday { get; init; }
    public long? Sent30Days { get; init; }
    public long? RemainingDaily { get; init; }
    public long? RemainingMonthly { get; init; }
    /// <summary>
    /// What can still be sent today: min(remaining today, remaining in 30 days). The daily cap alone would show
    /// 5,000 left today on an account whose 30-day cap is used up.
    /// </summary>
    public long? AvailableToday { get; init; }
    /// <summary>True when the 30-day cap, not the daily one, is what limits today.</summary>
    public bool TodayLimitedByMonth { get; init; }
    /// <summary>max(0, min(balance, remaining daily, remaining monthly)); null when any input is unknown.</summary>
    public long? Capacity { get; init; }
    public CapacityState State { get; init; }
    public IReadOnlyList<string> ToVerifyReasons { get; init; } = [];
    /// <summary>Fields whose last read failed or was incomplete (<see cref="FinanceField"/>): show them as "a conferir".</summary>
    public IReadOnlySet<string> FailedReads { get; init; } = new HashSet<string>();
    public DateTimeOffset NextDailyResetUtc { get; init; }
    /// <summary>When each outgoing transfer stops counting for the 30-day window (oldest first).</summary>
    public IReadOnlyList<MonthlyRelease> MonthlyReleases { get; init; } = [];

    public DateTimeOffset? LastReadUtc { get; init; }
}

/// <summary>Pure calculation of the remaining transfer quota. No I/O; "now" is a parameter.</summary>
public static class QuotaCalculator
{
    /// <summary>Shown once when Roblox refused reads with HTTP 429; the app reads again by itself after a pause.</summary>
    public const string RateLimitedReason = "o Roblox limitou os pedidos agora (HTTP 429); o app tenta de novo sozinho em instantes";

    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>The smaller of the Roblox cap and the configured restriction (when set). Zero is a valid limit.</summary>
    public static long? Effective(long? cap, long? restriction) =>
        (cap, restriction) switch
        {
            (null, null) => null,
            ({ } c, null) => c,
            (null, { } r) => r,
            ({ } c, { } r) => Math.Min(c, r),
        };

    /// <summary>Sum of outgoing amounts with CreatedUtc in [from, to] (to inclusive), never counting the future.</summary>
    private static long Sum(IEnumerable<TransferEntry> entries, DateTimeOffset fromExclusive, DateTimeOffset toInclusive, bool fromInclusive = false) =>
        entries.Where(e => (fromInclusive ? e.CreatedUtc >= fromExclusive : e.CreatedUtc > fromExclusive) && e.CreatedUtc <= toInclusive)
               .Sum(e => e.Amount);

    public static FinanceView Compute(AccountFinance f, DateTimeOffset now)
    {
        var reasons = new List<string>();

        long? effDaily = f.CapsAtUtc is null ? null : Effective(f.Caps?.Daily, f.RestrictionAtUtc is null ? null : f.Restriction?.Daily);
        long? effMonthly = f.CapsAtUtc is null ? null : Effective(f.Caps?.Monthly, f.RestrictionAtUtc is null ? null : f.Restriction?.Monthly);

        long? sentToday = null, sent30 = null;
        var releases = new List<MonthlyRelease>();
        // A ledger built with older rules may be incomplete: never use it, wait for the re-read.
        var usable = f.Ledger is { Version: TransferLedger.CurrentVersion } current ? current : null;
        if (usable is { } ledger)
        {
            var dayStart = BrasiliaTime.StartOfDay(now);
            sentToday = Sum(ledger.Entries, dayStart, now, fromInclusive: true);
            var windowStart = now - Window;
            var inWindow = ledger.Entries.Where(e => e.CreatedUtc > windowStart && e.CreatedUtc <= now).OrderBy(e => e.CreatedUtc).ToList();
            sent30 = inWindow.Sum(e => e.Amount);
            releases.AddRange(inWindow.Select(e => new MonthlyRelease(e.CreatedUtc + Window, e.Amount)));
        }

        long? remDaily = effDaily is { } d && sentToday is { } st ? Math.Max(0, d - st) : null;
        long? remMonthly = effMonthly is { } m && sent30 is { } s30 ? Math.Max(0, m - s30) : null;
        long? capacity = f.Robux is { } bal && remDaily is { } rd && remMonthly is { } rm
            ? Math.Max(0, Math.Min(bal, Math.Min(rd, rm)))
            : null;

        // Reasons to show "a conferir"
        // Same status the screen shows: "Expirado"/"Sem Plus" only from a valid response; a failed read is "a conferir" + why.
        var plus = PlusStatus.Evaluate(f, now);
        switch (plus.Label)
        {
            case PlusLabel.ToVerify: reasons.Add("Plus a conferir: " + plus.Reason); break;
            case PlusLabel.None: reasons.Add("Sem Plus"); break;
            case PlusLabel.Expired: reasons.Add("Plus expirado"); break;
        }

        // One reason per cause: a read refused by Roblox's request limit is said once, in plain words, and the
        // "with more than 30 minutes" / "error in the quota read" lines it causes are not repeated under it.
        var failed = f.FailedReads.Where(x => x != FinanceField.Plus).ToList();
        var limited = failed.Where(x => f.FailureReasons.GetValueOrDefault(x, "").Contains("limite de requisições", StringComparison.OrdinalIgnoreCase)).ToList();
        if (limited.Count > 0)
            reasons.Add(RateLimitedReason + " (" + string.Join(", ", limited.Select(FinanceField.Describe)) + ")");
        bool Failed(string field) => failed.Contains(field);

        if (f.Robux is null) { if (!Failed(FinanceField.Robux)) reasons.Add("saldo não lido"); }
        else if ((f.RobuxAtUtc is not { } ra || now - ra > MaxAge) && !Failed(FinanceField.Robux)) reasons.Add("saldo com mais de 30 minutos");

        foreach (string field in failed.Except(limited))
            reasons.Add($"{FinanceField.Describe(field)} a conferir: " + f.FailureReasons.GetValueOrDefault(field, "leitura com erro, incompleta ou em formato desconhecido"));
        if (f.QuotaReadFailed && failed.Count == 0) reasons.Add("erro na leitura da cota");
        bool limitsFailed = Failed(FinanceField.Caps) || Failed(FinanceField.Restriction);
        if (f.CapsAtUtc is null || f.RestrictionAtUtc is null) { if (!limitsFailed) reasons.Add("limites não lidos"); }
        else if ((now - f.CapsAtUtc.Value > MaxAge || now - f.RestrictionAtUtc.Value > MaxAge) && !limitsFailed) reasons.Add("limites com mais de 30 minutos");
        if (effDaily is null || effMonthly is null) { if (f.CapsAtUtc is not null) reasons.Add("Roblox não informou o teto diário ou mensal"); }

        if (usable is null) { if (!Failed(FinanceField.History)) reasons.Add("histórico de envios não cobre 30 dias"); }
        else if (now - usable.CompleteAsOfUtc > MaxAge && !Failed(FinanceField.History)) reasons.Add("histórico de envios com mais de 30 minutos");

        DateTimeOffset? lastRead = new[] { f.RobuxAtUtc, f.CapsAtUtc, usable?.CompleteAsOfUtc }.Where(x => x is not null).Max();

        return new FinanceView
        {
            Robux = f.Robux,
            PendingRobux = f.PendingRobux,
            CapDaily = f.Caps?.Daily,
            CapMonthly = f.Caps?.Monthly,
            PerTransferLimit = f.Caps?.PerTransfer,
            RestrictionDaily = f.Restriction?.Daily,
            RestrictionMonthly = f.Restriction?.Monthly,
            Plus = f.Plus,
            EffectiveDaily = effDaily,
            EffectiveMonthly = effMonthly,
            SentToday = sentToday,
            Sent30Days = sent30,
            RemainingDaily = remDaily,
            AvailableToday = remDaily is { } ad && remMonthly is { } am ? Math.Min(ad, am) : null,
            TodayLimitedByMonth = remDaily is { } bd && remMonthly is { } bm && bm < bd,
            RemainingMonthly = remMonthly,
            Capacity = capacity,
            State = reasons.Count == 0 && capacity is not null ? CapacityState.Estimated : CapacityState.ToVerify,
            ToVerifyReasons = reasons.Distinct().ToList(),
            FailedReads = f.FailedReads.ToHashSet(),
            NextDailyResetUtc = BrasiliaTime.NextMidnight(now),
            MonthlyReleases = releases,
            LastReadUtc = lastRead,
        };
    }
}
