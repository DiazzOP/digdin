using System.Text.Json;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>One item of one side of a trade, as Roblox lists it.</summary>
/// <param name="InstanceId">The new collectible instance id (v2 trades), when Roblox gives it.</param>
public sealed record TradeItem(long? UserAssetId, long AssetId, string Name, int? Serial, long? Rap, string? InstanceId = null);

public sealed record TradeOffer(long UserId, IReadOnlyList<TradeItem> Items, long Robux);

public sealed record TradeDetail(long Id, string Status, DateTimeOffset? CreatedUtc, IReadOnlyList<TradeOffer> Offers)
{
    public bool IsCompleted => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "Declined", "Expired", "Countered", "RejectedDueToError", "Inactive" };
    public bool IsClosedWithoutTrade => ClosedStatuses.Contains(Status);
}

/// <summary>A row of a trade list (inbound, outbound, completed, inactive).</summary>
public sealed record TradeSummary(long Id, long PartnerId, DateTimeOffset? CreatedUtc, string Status);

/// <summary>
/// Trade responses, v1 ("offers": [{user, userAssets, robux}]) and v2 ("participantAOffer"/"participantBOffer" with
/// "items"). Value-free FormatException reasons.
/// </summary>
public static class TradeParsers
{
    public static TradeDetail Detail(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || Long(r, "id") is not { } id) throw new FormatException("trade: id ausente");
        string status = Str(r, "status") ?? Str(r, "tradeStatus") ?? "";
        DateTimeOffset? created = Str(r, "created") is { } c && DateTimeOffset.TryParse(c, out var ct) ? ct : null;
        var offers = new List<TradeOffer>();
        if (r.TryGetProperty("offers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var o in arr.EnumerateArray()) offers.Add(Offer(o, "userAssets"));
        else
            foreach (var name in new[] { "participantAOffer", "participantBOffer" })
                if (r.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object) offers.Add(Offer(o, "items"));
        if (offers.Count != 2) throw new FormatException("trade: não vieram os dois lados da troca");
        return new TradeDetail(id, status, created, offers);
    }

    private static TradeOffer Offer(JsonElement o, string itemsField)
    {
        long? user = o.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Long(u, "id")
            : o.TryGetProperty("participant", out var p) && p.ValueKind == JsonValueKind.Object ? Long(p, "id") ?? Long(p, "userId") : Long(o, "userId");
        if (user is not { } uid) throw new FormatException("trade: lado sem usuário");
        var items = new List<TradeItem>();
        if (o.TryGetProperty(itemsField, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                long? asset = Long(e, "assetId")
                    ?? (e.TryGetProperty("itemTarget", out var t) && t.ValueKind == JsonValueKind.Object ? Long(t, "targetId") : null);
                if (asset is not > 0) throw new FormatException("trade: item sem assetId");
                // v1: "id" is the userAssetId; v2: explicit userAssetId (if any) plus the instance id.
                long? uaid = itemsField == "userAssets" ? Long(e, "id") : Long(e, "userAssetId");
                items.Add(new TradeItem(uaid is > 0 ? uaid : null, asset.Value, Str(e, "name") ?? Str(e, "itemName") ?? "",
                    (int?)Long(e, "serialNumber"), Long(e, "recentAveragePrice"), Str(e, "collectibleItemInstanceId")));
            }
        long robux = Long(o, "robux") ?? 0;
        return new TradeOffer(uid, items, robux);
    }

    public static (List<TradeSummary> Items, string? Next) List(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new FormatException("trades: campo data ausente");
        var list = new List<TradeSummary>();
        foreach (var e in data.EnumerateArray())
        {
            if (Long(e, "id") is not { } id) continue;
            long partner = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Long(u, "id") ?? 0 : 0;
            DateTimeOffset? created = Str(e, "created") is { } c && DateTimeOffset.TryParse(c, out var ct) ? ct : null;
            list.Add(new TradeSummary(id, partner, created, Str(e, "status") ?? ""));
        }
        return (list, Str(r, "nextPageCursor"));
    }

    internal static long? Long(JsonElement e, string f) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(f, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s) ? s : null
            : null;

    internal static string? Str(JsonElement e, string f) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum VerifyOutcome
{
    /// <summary>Both sides as planned (Smalls may have been swapped for other Smalls).</summary>
    Matches,
    /// <summary>Something is missing, extra or not identifiable: the operator has to look.</summary>
    NeedsCheck,
    /// <summary>Not a trade between this step's two accounts.</summary>
    WrongAccounts,
}

/// <param name="FromSource">What the source really sent (identified copies only).</param>
/// <param name="Notes">Substitutions and doubts, in Portuguese.</param>
/// <param name="Limiteds">The step's Limiteds as they really went (another copy of the same item, when that is what was sent).</param>
public sealed record VerifyResult(VerifyOutcome Outcome, IReadOnlyList<CopyRef> FromSource, IReadOnlyList<CopyRef> FromDestination, IReadOnlyList<string> Notes,
                                 IReadOnlyList<CopyRef>? Limiteds = null);

/// <summary>
/// Compares a Roblox trade with a plan step. Copies are matched by userAssetId; without it, by asset + serial; a copy that
/// cannot be told apart is "a conferir". Another Small instead of the planned one is accepted when it classifies as a
/// Small and Roblox gives the copy's id (the Desk's rule).
/// </summary>
public static class TradeVerifier
{
    public static VerifyResult Verify(PlanStep step, TradeDetail t, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket>? market)
    {
        var notes = new List<string>();
        var src = t.Offers.FirstOrDefault(o => o.UserId == step.SourceId);
        var dst = t.Offers.FirstOrDefault(o => o.UserId == step.DestinationId);
        if (src is null || dst is null)
            return new(VerifyOutcome.WrongAccounts, [], [], [$"a trade {t.Id} não é entre @{step.SourceName} e @{step.DestinationName}"]);

        bool problem = false;
        var srcLeft = src.Items.ToList();
        var actualOf = new CopyRef?[step.Limiteds.Count];
        // 1. Each planned copy that the trade identifies (by id, or by a serial/item only it matches).
        var ambiguous = new HashSet<int>();
        for (int i = 0; i < step.Limiteds.Count; i++)
        {
            var planned = step.Limiteds[i];
            if (Find(srcLeft, planned, out bool same) is { } hit) { srcLeft.Remove(hit); actualOf[i] = Actual(planned, hit); }
            else if (same) ambiguous.Add(i);
        }
        // 2. The rest: copies of the same item are the same lot (the trade page cannot always tell copies apart).
        for (int i = 0; i < step.Limiteds.Count; i++)
        {
            if (actualOf[i] is not null) continue;
            var planned = step.Limiteds[i];
            // Another copy Roblox names: the plan follows the real one.
            if (srcLeft.FirstOrDefault(x => x.AssetId == planned.AssetId && x.UserAssetId is > 0) is { } other)
            {
                srcLeft.Remove(other);
                actualOf[i] = Actual(planned, other);
                notes.Add($"outra cópia de {planned.Name} na trade: o plano previa {planned.Label}, foi {Label(other)}");
                continue;
            }
            // Copies Roblox does not name (v2 trades): as many as the plan has of that item, so they are the planned ones.
            var unnamed = srcLeft.Where(x => x.AssetId == planned.AssetId && x.UserAssetId is null && (planned.Serial is null || x.Serial is null || x.Serial == planned.Serial)).ToList();
            int wanted = Enumerable.Range(0, step.Limiteds.Count).Count(j => actualOf[j] is null && step.Limiteds[j].AssetId == planned.AssetId);
            if (unnamed.Count > 0 && unnamed.Count <= wanted)
            {
                srcLeft.Remove(unnamed[0]);
                actualOf[i] = Actual(planned, unnamed[0]);
                continue;
            }
            problem = true;
            notes.Add(ambiguous.Contains(i) || unnamed.Count > 1 ? $"{planned.Label}: há mais cópias iguais na trade do que no plano e o Roblox não deu o id — a conferir"
                                                                 : $"{planned.Label} não está na trade");
        }
        var limiteds = actualOf.OfType<CopyRef>().ToList();
        var fromSource = new List<CopyRef>(limiteds);
        // Whatever else the source sent: a Small (planned or a substitute) is fine; anything else needs a look.
        bool sentSmall = SmallSide(srcLeft, step.SourceSmall, fromSource, notes, s, market,
            $"Small da origem trocado", $"item não planejado saiu de @{step.SourceName}", ref problem);
        if (step.SourceSmall is { } plannedSrcSmall && !sentSmall)
            notes.Add($"a origem não mandou o Small previsto ({plannedSrcSmall.Label})");

        var fromDestination = new List<CopyRef>();
        SmallSide(dst.Items.ToList(), step.DestinationSmall, fromDestination, notes, s, market,
            $"Small de @{step.DestinationName} trocado", $"item inesperado saiu de @{step.DestinationName}", ref problem);
        if (dst.Items.Count == 0) notes.Add($"@{step.DestinationName} não mandou Small");
        if (src.Robux != 0 || dst.Robux != 0) { problem = true; notes.Add("a trade tem Robux, o plano não previa"); }
        return new(problem ? VerifyOutcome.NeedsCheck : VerifyOutcome.Matches, fromSource, fromDestination, notes, limiteds);
    }

    /// <summary>
    /// The Smalls of one side: the planned Small (by id, or by asset + serial when Roblox gives no id) or another Small
    /// with an id is accepted; anything else needs a look. Returns whether a Small went.
    /// </summary>
    private static bool SmallSide(List<TradeItem> items, CopyRef? planned, List<CopyRef> into, List<string> notes, DistributionSettings s,
                                  IReadOnlyDictionary<long, ItemMarket>? market, string swapped, string unexpected, ref bool problem)
    {
        bool any = false;
        // The planned Small, or another copy of the same item (the page cannot tell copies apart): the same Small.
        var hit = planned is null ? null
            : Find(items, planned, out _) ?? items.FirstOrDefault(i => i.AssetId == planned.AssetId && i.UserAssetId is > 0);
        if (planned is not null && hit is not null && IsSmall(hit, s, market))
        {
            items.Remove(hit);
            into.Add(planned with { UserAssetId = hit.UserAssetId ?? planned.UserAssetId, Serial = hit.Serial ?? planned.Serial });
            any = true;
        }
        foreach (var item in items)
        {
            if (IsSmall(item, s, market) && item.UserAssetId is not null)
            {
                into.Add(ToRef(item));
                any = true;
                if (planned is not null) notes.Add($"{swapped}: o plano previa {planned.Label}, a trade levou {Label(item)}");
            }
            else { problem = true; notes.Add($"{unexpected}: {Label(item)}"); }
        }
        return any;
    }

    /// <summary>The planned copy as it really went: Roblox's id and serial when it gives them.</summary>
    private static CopyRef Actual(CopyRef planned, TradeItem hit) =>
        planned with { UserAssetId = hit.UserAssetId ?? planned.UserAssetId, Serial = hit.Serial ?? planned.Serial };

    private static TradeItem? Find(List<TradeItem> items, CopyRef c, out bool ambiguous)
    {
        ambiguous = false;
        var byId = items.FirstOrDefault(i => i.UserAssetId == c.UserAssetId);
        if (byId is not null) return byId;
        // Without the id: same item, and never a copy whose serial says it is another one.
        var same = items.Where(i => i.AssetId == c.AssetId && i.UserAssetId is null && (c.Serial is null || i.Serial is null || i.Serial == c.Serial)).ToList();
        if (c.Serial is { } serial && same.Where(i => i.Serial == serial).ToList() is { Count: 1 } bySerial) return bySerial[0];
        if (same.Count == 1) return same[0];
        ambiguous = same.Count > 1;
        return null;
    }

    private static bool IsSmall(TradeItem i, DistributionSettings s, IReadOnlyDictionary<long, ItemMarket>? market) =>
        Valuation.IsSmallBy(s.SmallRapLimit, market?.GetValueOrDefault(i.AssetId) is { } m ? [m.Rap, m.RolimonsValue, i.Rap] : [i.Rap]);

    private static CopyRef ToRef(TradeItem i) => new(i.UserAssetId ?? 0, i.AssetId, i.Name, i.Serial);
    private static string Label(TradeItem i) => i.Name + (i.Serial is { } s ? $" #{s}" : "");
}

/// <summary>Applies what Roblox said about a step's trade: state, history, and the copies moving between inventories.</summary>
public static class PlanUpdates
{
    public static void ApplyTrade(PlanStep step, TradeDetail t, VerifyResult v, IReadOnlyList<AccountSnapshot> accounts, DateTimeOffset now)
    {
        if (v.Outcome == VerifyOutcome.WrongAccounts)
        {
            // Keep whatever trade was already linked: a wrong id must not unlink the real one.
            step.Attention = v.Notes[0] + ". Confira o ID.";
            step.Log(now, $"trade {t.Id} recusada na conferência: {v.Notes[0]}");
            return;
        }
        step.TradeId = t.Id;
        step.ActualFromSource = [.. v.FromSource];
        step.ActualFromDestination = [.. v.FromDestination];
        string notes = v.Notes.Count > 0 ? " · " + string.Join("; ", v.Notes) : "";
        if (t.IsClosedWithoutTrade)
        {
            step.State = StepState.Declined;
            step.Attention = null;
            step.Log(now, $"trade {t.Id} {StatusWord(t.Status)}: as cópias voltam para o plano{notes}");
            return;
        }
        if (v.Outcome == VerifyOutcome.NeedsCheck)
        {
            step.State = StepState.NeedsCheck;
            step.Attention = $"A trade {t.Id} não bate com a etapa: " + string.Join("; ", v.Notes) + ".";
            step.Log(now, $"trade {t.Id} ({StatusWord(t.Status)}) em conferência{notes}");
            return;
        }
        if (!t.IsCompleted)
        {
            step.State = StepState.Sent;
            step.Attention = null;
            step.Log(now, $"trade {t.Id} enviada, aguardando no Roblox ({StatusWord(t.Status)}){notes}");
            return;
        }
        step.State = StepState.Completed;
        step.CompletedUtc = now;
        // The copies that really went (sales and the destination's stock follow these).
        if (v.Limiteds is { Count: > 0 } real && real.Count == step.Limiteds.Count) step.Limiteds = [.. real];
        step.Attention = null;
        step.Log(now, $"trade {t.Id} concluída e conferida{notes}");
        // Move the copies now (marked on hold, as Roblox holds traded items); the next real read replaces this.
        var src = accounts.FirstOrDefault(a => a.UserId == step.SourceId)?.Inventory;
        var dst = accounts.FirstOrDefault(a => a.UserId == step.DestinationId)?.Inventory;
        Move(src, dst, v.FromSource, now);
        Move(dst, src, v.FromDestination, now);
    }

    private static void Move(AccountInventory? from, AccountInventory? to, IEnumerable<CopyRef> copies, DateTimeOffset now)
    {
        foreach (var c in copies.Where(c => c.UserAssetId > 0))
        {
            var old = from?.Copies.FirstOrDefault(x => x.UserAssetId == c.UserAssetId);
            from?.Copies.RemoveAll(x => x.UserAssetId == c.UserAssetId);
            if (to is not null && to.Copies.All(x => x.UserAssetId != c.UserAssetId))
                to.Copies.Add(new LimitedCopy(c.UserAssetId, c.AssetId, c.Name, c.Serial, old?.Rap, OnHold: true, SeenUtc: now));
        }
        if (from is not null) from.AdjustedUtc = now;
        if (to is not null) to.AdjustedUtc = now;
    }

    public static string StatusWord(string status) => status switch
    {
        "Completed" => "concluída",
        "Declined" => "recusada",
        "Expired" => "expirada",
        "Countered" => "contraproposta",
        "Open" or "Pending" or "Outbound" or "Inbound" => "aberta",
        "Inactive" => "inativa",
        _ => status.ToLowerInvariant(),
    };

    /// <summary>
    /// The most likely trade for a step among a list: between the two accounts, created after the step, newest first —
    /// never a trade already tied to another step of the plan (a finished trade of an earlier step is not this one).
    /// </summary>
    public static IEnumerable<TradeSummary> Candidates(PlanStep step, IEnumerable<TradeSummary> trades, DistributionPlan? plan = null)
    {
        var linked = plan?.Steps.Where(x => x.Id != step.Id && x.TradeId is not null).Select(x => x.TradeId!.Value).ToHashSet() ?? [];
        return trades.Where(t => t.PartnerId == step.DestinationId && !linked.Contains(t.Id) && (t.CreatedUtc is not { } c || c >= step.CreatedUtc.AddHours(-1)))
                     .DistinctBy(t => t.Id).OrderByDescending(t => t.CreatedUtc ?? DateTimeOffset.MinValue);
    }

    /// <summary>The other step a trade is already tied to, if any.</summary>
    public static PlanStep? LinkedElsewhere(DistributionPlan plan, PlanStep step, long tradeId) =>
        plan.Steps.FirstOrDefault(x => x.Id != step.Id && x.TradeId == tradeId);
}
