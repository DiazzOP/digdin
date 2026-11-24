namespace RbxSwitcher.Core.Limiteds;

public enum DistributionRole { None, Source, Destination }

/// <summary>Where a step is. Only the operator sends trades; the app reads Roblox to know what happened.</summary>
public enum StepState
{
    /// <summary>Suggested by the planner; recalculated freely while nobody acted on it.</summary>
    Suggested,
    /// <summary>"Abrir trade" was used: the operator is putting it together in Roblox. Kept and re-checked, never recalculated.</summary>
    Opened,
    /// <summary>A trade id is linked and the trade is open/pending in Roblox.</summary>
    Sent,
    /// <summary>Roblox shows the trade completed and both sides matched (Small substitutions accepted).</summary>
    Completed,
    /// <summary>Something does not match or seems done outside the plan: needs the operator to look.</summary>
    NeedsCheck,
    /// <summary>The trade was declined, expired or countered: the copies are free again.</summary>
    Declined,
    /// <summary>Dropped by the operator.</summary>
    Discarded,
}

/// <summary>A copy as the plan knows it (enough to find it again and to show it).</summary>
public sealed record CopyRef(long UserAssetId, long AssetId, string Name, int? Serial, long? Net = null, bool OnHold = false)
{
    public static CopyRef From(ValuedCopy v) => new(v.Copy.UserAssetId, v.Copy.AssetId, v.Copy.Name, v.Copy.Serial, v.Net, v.OnHold);
    public static CopyRef From(LimitedCopy c) => new(c.UserAssetId, c.AssetId, c.Name, c.Serial);
    public string Label => Name + (Serial is { } s ? $" #{s}" : "");
}

public sealed record PlanEvent(DateTimeOffset AtUtc, string Text);

/// <summary>
/// One trade between a source and a destination: up to 3 Limiteds and an optional Small from the source, one Small from
/// the destination (each side keeps the Small it receives and can reuse it after the holding).
/// </summary>
public sealed class PlanStep
{
    public const int MaxLimiteds = 3;

    public string Id { get; set; } = "";
    public long SourceId { get; set; }
    public string SourceName { get; set; } = "";
    public long DestinationId { get; set; }
    public string DestinationName { get; set; } = "";
    public List<CopyRef> Limiteds { get; set; } = [];
    public CopyRef? SourceSmall { get; set; }
    public CopyRef? DestinationSmall { get; set; }
    /// <summary>The destination's Small is still on hold: the step waits for it to be released.</summary>
    public bool WaitsForSmall { get; set; }
    /// <summary>A Limited of the step is still on hold at the source: planned ahead, opened only after the release.</summary>
    public bool WaitsForRelease { get; set; }
    public StepState State { get; set; } = StepState.Suggested;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? OpenedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public long? TradeId { get; set; }
    /// <summary>Why each choice was made, in Portuguese (shown under the step).</summary>
    public List<string> Why { get; set; } = [];
    /// <summary>What needs the operator's attention (NeedsCheck), or what changed.</summary>
    public string? Attention { get; set; }
    public List<PlanEvent> History { get; set; } = [];

    /// <summary>What really went in the trade, once verified (may differ from the plan for Smalls).</summary>
    public List<CopyRef>? ActualFromSource { get; set; }
    public List<CopyRef>? ActualFromDestination { get; set; }

    public long Net => Limiteds.Sum(c => c.Net ?? 0);

    /// <summary>Holds its copies and counts as an incoming lot for the destination.</summary>
    public bool Commits => State is StepState.Suggested or StepState.Opened or StepState.Sent or StepState.NeedsCheck;

    public bool IsOpenForWork => State is StepState.Suggested or StepState.Opened or StepState.Sent or StepState.NeedsCheck;

    public IEnumerable<CopyRef> AllCopies() =>
        Limiteds.Concat(new[] { SourceSmall, DestinationSmall }.OfType<CopyRef>());

    /// <summary>Adds to the history; the same text twice in a row (the same check read again) is kept once.</summary>
    public void Log(DateTimeOffset at, string text)
    {
        if (History.Count > 0 && History[^1].Text == text) return;
        History.Add(new PlanEvent(at, text));
    }
}

/// <summary>A source copy the planner did not place, and why.</summary>
/// <param name="Dismissed">Left out because the operator discarded its suggestion ("Voltar ao plano" brings it back).</param>
public sealed record Exclusion(CopyRef Copy, long SourceId, string SourceName, string Why, bool Dismissed = false);

/// <summary>
/// A copy whose suggestion the operator discarded: it is not suggested to that destination again (null: to none) until
/// brought back; another destination can still get it.
/// </summary>
public sealed record DismissedCopy(long UserAssetId, DateTimeOffset AtUtc, long? DestinationId = null, string? DestinationName = null);

/// <summary>The distribution plan. Lives in the encrypted vault.</summary>
public sealed class DistributionPlan
{
    public List<PlanStep> Steps { get; set; } = [];
    public List<Exclusion> Exclusions { get; set; } = [];
    public DateTimeOffset? BuiltUtc { get; set; }
    public int NextNumber { get; set; } = 1;
    /// <summary>Copies the operator took out of the suggestions (discarded): new plans leave them out.</summary>
    public List<DismissedCopy> Dismissed { get; set; } = [];

    /// <summary>Discarding a suggestion: its Limiteds are not suggested to that destination again (the Smalls stay usable).</summary>
    public void Dismiss(PlanStep step, DateTimeOffset now)
    {
        foreach (var c in step.Limiteds.Where(c => Dismissed.All(d => d.UserAssetId != c.UserAssetId || d.DestinationId != step.DestinationId)))
            Dismissed.Add(new DismissedCopy(c.UserAssetId, now, step.DestinationId, step.DestinationName));
    }

    public bool Restore(long userAssetId) => Dismissed.RemoveAll(d => d.UserAssetId == userAssetId) > 0;

    public string NewId() => "E" + NextNumber++.ToString("000");
}
