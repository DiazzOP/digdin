namespace RbxSwitcher.Core.Finance;

/// <summary>One Robux transfer this account SENT (Sender, negative amount). Amount is stored as a positive value.</summary>
public sealed class TransferEntry
{
    /// <summary>Roblox's stable transaction id (unique per account).</summary>
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public long Amount { get; set; }
}

/// <summary>
/// A complete view of the account's outgoing transfers: every outgoing transfer in
/// (<see cref="CompleteAsOfUtc"/> − 30 days, <see cref="CompleteAsOfUtc"/>] is in <see cref="Entries"/>.
/// Only replaced by another complete crawl; a failed or partial crawl never touches it.
/// </summary>
public sealed class TransferLedger
{
    /// <summary>
    /// Rules the ledger was built with. A ledger from older rules is discarded and the 30 days are read
    /// again from scratch (an earlier parser could have stopped after the first page).
    /// </summary>
    public const int CurrentVersion = 3;
    public int Version { get; set; }

    /// <summary>The response field used as the stable per-transaction id (verified unique when chosen).</summary>
    public string? IdField { get; set; }

    public List<TransferEntry> Entries { get; set; } = [];
    public DateTimeOffset CompleteAsOfUtc { get; set; }
}

/// <summary>What the last history read did, to compare with another tool at the same moment. No ids, no names.</summary>
public sealed class HistoryReadInfo
{
    public DateTimeOffset AtUtc { get; set; }
    public int Pages { get; set; }
    public int Records { get; set; }
    public int Outgoing { get; set; }
    public int Incoming { get; set; }
    public DateTimeOffset? NewestUtc { get; set; }
    public DateTimeOffset? OldestUtc { get; set; }
    /// <summary>Outgoing records as received, before deduplication.</summary>
    public int OutgoingRaw { get; set; }
    /// <summary>Identical records seen twice (same id, same date and amount) and counted once.</summary>
    public int Duplicates { get; set; }
    /// <summary>Field used as the stable transaction id.</summary>
    public string? IdField { get; set; }
    /// <summary>Why the read stopped (window covered, last page, previous history reached, paused, error).</summary>
    public string Stop { get; set; } = "";
    /// <summary>True when this read started from the newest page with no previous history (a full 30-day read).</summary>
    public bool FullRead { get; set; }
}

/// <summary>A crawl that was paused (page budget, error, 429): resumed on the next refresh.</summary>
public sealed class LedgerCheckpoint
{
    /// <summary>The time the crawl started reading from the newest page.</summary>
    public DateTimeOffset AnchorUtc { get; set; }
    /// <summary>Cursor of the next (older) page to read, and the request parameter that carries it.</summary>
    public string? Cursor { get; set; }
    public string CursorParam { get; set; } = "cursor";
    public int Version { get; set; } = TransferLedger.CurrentVersion;
    /// <summary>
    /// Only a crawl that started at the newest page (no cursor) can prove the 30-day window: an old page
    /// reached from somebody else's cursor proves nothing about what is newer.
    /// </summary>
    public bool StartedFromTop { get; set; }
    public string? IdField { get; set; }
    public int OutgoingRaw { get; set; }
    public int Duplicates { get; set; }
    /// <summary>Running totals of this crawl, for <see cref="HistoryReadInfo"/>.</summary>
    public int Records { get; set; }
    public int Incoming { get; set; }
    public DateTimeOffset? NewestUtc { get; set; }
    public DateTimeOffset? OldestUtc { get; set; }
    /// <summary>Outgoing transfers read so far in this crawl.</summary>
    public List<TransferEntry> Entries { get; set; } = [];
    /// <summary>The ledger this crawl extends (null = crawl back the full 30 days).</summary>
    public DateTimeOffset? BaseCompleteAsOfUtc { get; set; }
    public int PagesRead { get; set; }
}

public sealed class PlusInfo
{
    /// <summary>True while the subscription is valid (expiration, or next renewal, in the future).</summary>
    public bool Active { get; set; }
    public string? Status { get; set; }
    /// <summary>End of validity (expirationTimestampMs).</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }
    /// <summary>Next renewal date, when Roblox reports one (the site's "Renova …").</summary>
    public DateTimeOffset? RenewsUtc { get; set; }
    /// <summary>True: Roblox confirmed it renews. False: confirmed it does not (cancelled). Null: not reported.</summary>
    public bool? WillRenew { get; set; }
    /// <summary>Only true when Roblox explicitly reports a trial period.</summary>
    public bool Trial { get; set; }
    /// <summary>A valid response, every page read, and no Plus entry at all ("Sem Plus").</summary>
    public bool NoSubscription { get; set; }

    /// <summary>True only when Roblox's response says the account may start a free trial; null = not reported.</summary>
    public bool? TrialEligible { get; set; }

    /// <summary>Auxiliary details Roblox did not report usably (e.g. "nextRenewalTimestampMs veio vazio (0)"). Names only.</summary>
    public List<string> Notes { get; set; } = [];
}

/// <summary>Values Roblox returns. Null = Roblox returned no value for that field.</summary>
public sealed class TransferCaps
{
    public long? Daily { get; set; }
    public long? Monthly { get; set; }
    public long? PerTransfer { get; set; }
}

/// <summary>Limits the account owner configured in settings (robuxTransferLimits.currentValue). Null = none.</summary>
public sealed class TransferRestriction
{
    public long? Daily { get; set; }
    public long? Monthly { get; set; }
}

/// <summary>
/// Everything read about an account's Robux, persisted inside the encrypted vault. Each value keeps
/// the time it was read; a failed read keeps the previous value and time.
/// </summary>
public sealed class AccountFinance
{
    public long? Robux { get; set; }
    public DateTimeOffset? RobuxAtUtc { get; set; }

    public long? PendingRobux { get; set; }
    public DateTimeOffset? PendingAtUtc { get; set; }

    public PlusInfo? Plus { get; set; }
    public DateTimeOffset? PlusAtUtc { get; set; }

    public TransferCaps? Caps { get; set; }
    public DateTimeOffset? CapsAtUtc { get; set; }

    /// <summary>Null with <see cref="RestrictionAtUtc"/> set = read OK, no restriction configured.</summary>
    public TransferRestriction? Restriction { get; set; }
    public DateTimeOffset? RestrictionAtUtc { get; set; }

    public TransferLedger? Ledger { get; set; }
    public LedgerCheckpoint? Checkpoint { get; set; }
    public HistoryReadInfo? LastHistoryRead { get; set; }

    /// <summary>Free-trial eligibility seen in the official Plus page's own responses (roblox.com/plus), and when.</summary>
    public bool? TrialEligibleFromPage { get; set; }
    public DateTimeOffset? TrialFromPageUtc { get; set; }

    /// <summary>Effective limits that went down and were not acknowledged yet (see <see cref="LimitWatch"/>).</summary>
    public List<LimitDrop> LimitDrops { get; set; } = [];
    /// <summary>When the person last clicked "Ok, entendi" on a limit drop.</summary>
    public DateTimeOffset? LimitDropsSeenUtc { get; set; }

    /// <summary>The last refresh attempt (successful or not).</summary>
    public DateTimeOffset? LastAttemptUtc { get; set; }
    /// <summary>Problems in the last attempt, in Portuguese, for the screen. Empty = all reads succeeded.</summary>
    public List<string> LastErrors { get; set; } = [];
    /// <summary>True when the last attempt could not read caps, restriction or the full history.</summary>
    public bool QuotaReadFailed { get; set; }

    /// <summary>
    /// Reads that failed, came back incomplete or in an unknown format in the last attempt
    /// (<see cref="FinanceField"/> keys). The previous value is kept, but shown as "a conferir".
    /// </summary>
    public List<string> FailedReads { get; set; } = [];

    /// <summary>Why each failed read failed (field key → short reason without values), for the screen.</summary>
    public Dictionary<string, string> FailureReasons { get; set; } = [];

    public AccountFinance Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AccountFinance>(json)!;
    }
}
