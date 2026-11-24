namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// One physical copy of a Limited in an account. <see cref="UserAssetId"/> identifies the copy (counted once);
/// a copy without it cannot be told apart from another and is "a conferir".
/// </summary>
/// <param name="FromRolimons">Listed by Rolimons but not by Roblox's collectibles list (new UGC Limiteds are not in it).</param>
/// <param name="SeenUtc">When the app first saw this copy in the account (null: it was already there at the first read).</param>
public sealed record LimitedCopy(long UserAssetId, long AssetId, string Name, int? Serial, long? Rap, bool? OnHold, bool FromRolimons = false,
                                 DateTimeOffset? SeenUtc = null)
{
    public string Key => UserAssetId > 0 ? "u" + UserAssetId : "";
}

/// <summary>Everything read about one account's Limiteds. Lives in the encrypted vault, like the finance data.</summary>
public sealed class AccountInventory
{
    public List<LimitedCopy> Copies { get; set; } = [];
    /// <summary>Copies Roblox listed without a usable id (counted apart, never valued).</summary>
    public int Ambiguous { get; set; }
    /// <summary>userAssetIds Rolimons reports as on hold (cross-check when Roblox does not say).</summary>
    public List<long> HoldsFromRolimons { get; set; } = [];
    public DateTimeOffset? ReadUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    /// <summary>Every page read: the list is the whole inventory, not a part of it.</summary>
    public bool Complete { get; set; }
    public string? Problem { get; set; }
    /// <summary>Why Rolimons' list of the account could not be read (copies only Rolimons sees may be missing).</summary>
    public string? RolimonsProblem { get; set; }
    /// <summary>When a verified trade moved copies in or out before the next real read.</summary>
    public DateTimeOffset? AdjustedUtc { get; set; }

    public bool IsOnHold(LimitedCopy c) => c.OnHold ?? HoldsFromRolimons.Contains(c.UserAssetId);
}

/// <summary>Public market data of one Limited (Roblox resale data and Rolimons).</summary>
/// <param name="RapFromRolimons">The RAP is Rolimons' (it wins over Roblox's resale data, which can be stale for migrated items).</param>
public sealed record ItemMarket(long AssetId, string? Name, long? Rap, long? RolimonsValue, int? Demand, double? SalesPerDay, DateTimeOffset AtUtc,
                                bool RapFromRolimons = false);

/// <summary>Distribution settings, configured from zero in RbxSwitcher. Stored in the vault.</summary>
public sealed class DistributionSettings
{
    /// <summary>Marketplace fee taken from each sale, in percent (Roblox: 30).</summary>
    public int FeePercent { get; set; } = 30;
    /// <summary>Net ceiling per destination account; 0 = no ceiling (only the 30-day quota limits).</summary>
    public long DefaultTargetNet { get; set; }
    public Dictionary<long, long> TargetNetByAccount { get; set; } = [];
    /// <summary>A copy whose RAP is at most this is a Small (support item for trades).</summary>
    public long SmallRapLimit { get; set; } = 1_000;
    /// <summary>Smalls each account keeps for its own trades.</summary>
    public int MinSmallsReserve { get; set; } = 1;
    /// <summary>Days from the trade until the sale's Robux arrive (holding + time to sell).</summary>
    public int DaysToSale { get; set; } = 7;
    /// <summary>Plan against the space the account will have when the sale pays (true) or only today's space (false).</summary>
    public bool UseSpaceAtSale { get; set; } = true;
    /// <summary>Use the Rolimons value instead of RAP when Rolimons has one.</summary>
    public bool PreferRolimonsValue { get; set; }
    /// <summary>Source accounts (AstraFlip withdrawals land only here: any non-Small Limited in them came from AstraFlip).</summary>
    public List<long> SourceAccounts { get; set; } = [];
    /// <summary>Group whose accounts receive the lots; null = every account that is not a source.</summary>
    public string? DestinationGroup { get; set; }
    /// <summary>Accounts chosen one by one to receive the lots; empty = every account of <see cref="DestinationGroup"/>.</summary>
    public List<long> DestinationAccounts { get; set; } = [];
    /// <summary>"Abrir trade" also selects the step's items on the trade page (never sends: the person reviews and sends).</summary>
    public bool PreselectTradeItems { get; set; } = true;
    /// <summary>After the person sends a planned trade (and it checks out), switch to the destination and open the trade there to be accepted.</summary>
    public bool OpenDestinationAfterSend { get; set; } = true;
    /// <summary>Limiteds tab: the suggested price is this many Robux under the cheapest other seller.</summary>
    public int SellUndercut { get; set; } = 1;
    /// <summary>Limiteds tab: never suggest under this share of the RAP (0 = no floor).</summary>
    public int SellFloorPercent { get; set; } = 80;
    /// <summary>Manual sale price per asset id (wins over RAP and Rolimons).</summary>
    public Dictionary<long, long> ManualPrice { get; set; } = [];

    public long TargetNet(long userId) => TargetNetByAccount.TryGetValue(userId, out var t) ? t : DefaultTargetNet;
}
