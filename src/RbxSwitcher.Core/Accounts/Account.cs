using System.Text.Json.Serialization;
using RbxSwitcher.Core.Finance;

namespace RbxSwitcher.Core.Accounts;

public enum SessionStatus
{
    /// <summary>Not checked since the app opened.</summary>
    Unknown = 0,
    Checking = 1,
    /// <summary>Roblox accepted the saved session.</summary>
    Valid = 2,
    /// <summary>Roblox rejected it (expired, signed out elsewhere, password changed): sign in again.</summary>
    Expired = 3,
    /// <summary>Roblox could not be reached or rate-limited us; the session may still be fine.</summary>
    Unreachable = 4,
}

/// <summary>
/// One saved Roblox account. The session cookie lives only inside the encrypted vault and in
/// memory; <see cref="ToString"/> never includes it.
/// </summary>
public sealed class Account
{
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Alias { get; set; }
    public string? Group { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>.ROBLOSECURITY value. Empty after the session expired and was cleared.</summary>
    [JsonInclude]
    internal string Session { get; set; } = "";

    /// <summary>Balance, pending Robux, Plus, transfer limits and history. Lives only in the encrypted vault.</summary>
    public AccountFinance? Finance { get; set; }

    /// <summary>The account's Limiteds (copies, holds). Lives only in the encrypted vault.</summary>
    public Limiteds.AccountInventory? Inventory { get; set; }

    /// <summary>Recent item sales (for the distribution plan). Encrypted vault only.</summary>
    public Limiteds.AccountSales? Sales { get; set; }
    /// <summary>Recent store purchases and trade receipts (when held copies leave the hold).</summary>
    public Limiteds.AccountArrivals? Arrivals { get; set; }

    /// <summary>Which of the account's Limiteds are on sale and for how much (Limiteds tab). Encrypted vault only.</summary>
    public Limiteds.AccountListings? Listings { get; set; }

    /// <summary>Not persisted: recomputed by the session checker each run.</summary>
    [JsonIgnore]
    public SessionStatus Status { get; set; } = SessionStatus.Unknown;

    [JsonIgnore]
    public bool HasSession => Session.Length > 0;

    public string Title => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias!;

    public string GroupOrDefault => string.IsNullOrWhiteSpace(Group) ? AccountStore.NoGroup : Group!.Trim();

    /// <summary>The only way code outside Core reads the secret: explicit and greppable.</summary>
    public string RevealSession() => Session;

    public override string ToString() => $"{Username} ({UserId})";
}
