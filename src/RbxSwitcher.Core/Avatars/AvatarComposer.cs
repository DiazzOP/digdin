namespace RbxSwitcher.Core.Avatars;

/// <summary>One asset of the list to wear: where it came from and how strongly it was asked for.</summary>
/// <param name="Priority">3 = chosen item, 2 = piece of a chosen head bundle, 1 = piece of another chosen bundle, 0 = kept.</param>
/// <param name="From">The chosen item (asset or bundle) it belongs to; null when it is something the account already wore.</param>
public sealed record WearEntry(long Id, int AssetType, int Priority, ItemRef? From);

/// <param name="AssetIds">The full list to send to set-wearing-assets (it replaces everything the account wears).</param>
/// <param name="Skipped">Chosen items left out, with the reason (Portuguese).</param>
/// <param name="Entries">The same list with each asset's type, priority and origin (used to trim it if Roblox refuses).</param>
public sealed record ComposeResult(IReadOnlyList<long> AssetIds, IReadOnlyList<(ItemRef Item, string Why)> Skipped, IReadOnlyList<WearEntry> Entries);

/// <summary>
/// Turns a design into the exact list of assets to wear, starting from what the account wears now:
/// categories set to "keep" (and everything the builder does not handle, like animations and emotes) stay;
/// "empty" categories come off; chosen items replace their category. Bundles put on their assets.
/// One-of-a-kind slots (head, face, shirt, body parts…) keep a single asset, and no type goes over Roblox's limit
/// (<see cref="AvatarRules"/>): chosen item > head bundle piece > other bundle piece > kept, then the latest.
/// </summary>
public static class AvatarComposer
{
    public const string OverLimit = "o Roblox não deixa vestir mais itens deste tipo juntos";

    public static ComposeResult Compose(CurrentAvatar current, AvatarDesign design,
                                        IReadOnlyDictionary<string, AvatarItem> catalog, IReadOnlySet<string>? unavailable = null,
                                        AvatarRules? rules = null)
    {
        rules ??= AvatarRules.Default;
        var skipped = new List<(ItemRef, string)>();
        // Per category: the chosen items that can actually be worn.
        var wearable = new Dictionary<AvatarCategory, List<AvatarItem>>();
        foreach (var def in AvatarCategories.All)
        {
            var slot = design.Slot(def.Category);
            if (slot.Mode != SlotMode.Items) continue;
            var list = new List<AvatarItem>();
            foreach (var r in slot.Items)
            {
                if (unavailable?.Contains(r.Key) == true) { skipped.Add((r, "a conta não possui e o item não é grátis")); continue; }
                if (!catalog.TryGetValue(r.Key, out var item)) { skipped.Add((r, "item não encontrado no catálogo")); continue; }
                if (item.Kind == ItemKind.Bundle && item.BundleAssets is not { Count: > 0 }) { skipped.Add((r, "o Roblox não informou as peças do pacote")); continue; }
                list.Add(item);
            }
            wearable[def.Category] = list;
        }

        var picks = new List<WearEntry>();
        foreach (var a in current.Assets)
        {
            var cat = AvatarCategories.OfAssetType(a.AssetType);
            if (cat is null) { picks.Add(new(a.Id, a.AssetType, 0, null)); continue; }
            var slot = design.Slot(cat.Value);
            bool replaced = slot.Mode == SlotMode.Empty || (slot.Mode == SlotMode.Items && wearable[cat.Value].Count > 0);
            if (!replaced) picks.Add(new(a.Id, a.AssetType, 0, null));
        }
        foreach (var (cat, items) in wearable)
            foreach (var item in items)
            {
                var from = ItemRef.From(item);
                if (item.Kind == ItemKind.Asset) picks.Add(new(item.Id, item.AssetType, 3, from));
                else foreach (var (id, type) in item.BundleAssets!) picks.Add(new(id, type, cat == AvatarCategory.Head ? 2 : 1, from));
            }

        // Each slot key (head and dynamic head share one) keeps at most its limit: highest priority, then the latest.
        var keep = new HashSet<int>(); // indexes in picks
        foreach (var group in picks.Select((p, i) => (P: p, I: i)).GroupBy(x => SlotKey(x.P.AssetType)))
        {
            int max = rules.Max(group.Key == AvatarCategories.Head ? AvatarCategories.Head : group.First().P.AssetType);
            if (AvatarCategories.SingleSlot(group.First().P.AssetType)) max = Math.Min(max, 1);
            foreach (var x in group.OrderByDescending(x => x.P.Priority).ThenByDescending(x => x.I).Take(max)) keep.Add(x.I);
        }

        var result = new List<long>();
        var entries = new List<WearEntry>();
        var seen = new HashSet<long>();
        for (int i = 0; i < picks.Count; i++)
        {
            var p = picks[i];
            if (!keep.Contains(i))
            {
                // A chosen item that did not fit is reported; bundle pieces and kept items give way silently.
                if (p.Priority == 3 && p.From is { } f && !skipped.Any(x => x.Item1.Key == f.Key)) skipped.Add((f, OverLimit));
                continue;
            }
            if (seen.Add(p.Id)) { result.Add(p.Id); entries.Add(p); }
        }
        return new ComposeResult(result, skipped, entries);
    }

    /// <summary>
    /// After a "LimitExceeded" refusal: the entry to take off. Roblox names an asset id (or a type) in FieldData; the
    /// weakest entry of that type goes (kept first, then bundle pieces, then the oldest chosen item). Null when the
    /// refusal does not say which.
    /// </summary>
    public static WearEntry? LimitCulprit(IReadOnlyList<WearEntry> entries, IEnumerable<(string Message, string? Field, string? Data)> errors)
    {
        foreach (var e in errors.Where(e => e.Message.Equals("LimitExceeded", StringComparison.OrdinalIgnoreCase)))
        {
            if (!long.TryParse(e.Data, out long n)) continue;
            int? type = entries.FirstOrDefault(x => x.Id == n)?.AssetType ?? (entries.Any(x => x.AssetType == n) ? (int)n : null);
            if (type is not { } t) continue;
            var same = entries.Select((x, i) => (X: x, I: i)).Where(x => SlotKey(x.X.AssetType) == SlotKey(t)).ToList();
            if (same.Count < 2) continue; // nothing to choose between: removing the only one would not be a fix
            return same.OrderBy(x => x.X.Priority).ThenBy(x => x.I).First().X;
        }
        return null;
    }

    private static int SlotKey(int assetType) => assetType == AvatarCategories.DynamicHead ? AvatarCategories.Head : assetType;
}

/// <summary>Selection rules of the builder grid, shared by the screen and the randomizer.</summary>
public static class AvatarSelection
{
    /// <summary>Selects or unselects an item in its category: one per single slot, at most the category's limit.</summary>
    public static void Toggle(SlotChoice slot, AvatarItem item)
    {
        var def = item.Category is { } c ? AvatarCategories.Def(c) : null;
        if (def is null) return;
        var existing = slot.Items.FindIndex(r => r.Key == item.Key);
        if (existing >= 0)
        {
            slot.Items.RemoveAt(existing);
            if (slot.Items.Count == 0) slot.Mode = SlotMode.Keep;
            return;
        }
        if (slot.Mode != SlotMode.Items) slot.Items.Clear();
        if (item.Kind == ItemKind.Asset && AvatarCategories.SingleSlot(item.AssetType))
            slot.Items.RemoveAll(r => r.Kind == ItemKind.Asset && SameSlot(r.AssetType, item.AssetType));
        if (item.Kind == ItemKind.Bundle) slot.Items.RemoveAll(r => r.Kind == ItemKind.Bundle);
        while (slot.Items.Count >= def.MaxItems) slot.Items.RemoveAt(0);
        slot.Items.Add(ItemRef.From(item));
        slot.Mode = SlotMode.Items;
    }

    private static bool SameSlot(int a, int b) =>
        a == b || (a is AvatarCategories.Head or AvatarCategories.DynamicHead && b is AvatarCategories.Head or AvatarCategories.DynamicHead);
}

/// <summary>"Sortear tudo": a random pick in every unlocked category, from the items given (free ones in batches).</summary>
public static class AvatarRandomizer
{
    public static AvatarDesign Randomize(AvatarDesign from, IReadOnlyList<AvatarItem> pool, Random rng)
    {
        var d = from.Clone();
        foreach (var def in AvatarCategories.All)
        {
            var slot = d.Slot(def.Category);
            if (slot.Locked) continue;
            var items = pool.Where(i => i.Category == def.Category && (i.Kind == ItemKind.Asset || i.BundleAssets is { Count: > 0 })).ToList();
            if (items.Count == 0) continue;
            int count = def.Category switch
            {
                AvatarCategory.Accessories => rng.Next(0, 3),       // 0 to 2 accessories
                AvatarCategory.TShirt => rng.Next(0, 10) < 4 ? 1 : 0, // a T-shirt 40 % of the time
                _ => 1,
            };
            var fresh = new SlotChoice { Locked = false, Mode = SlotMode.Keep };
            foreach (var item in items.OrderBy(_ => rng.Next()))
            {
                if (fresh.Items.Count >= count) break;
                if (def.Category == AvatarCategory.Accessories && fresh.Items.Any(r => r.AssetType == item.AssetType)) continue;
                AvatarSelection.Toggle(fresh, item);
            }
            if (fresh.Items.Count == 0 && def.Category is AvatarCategory.Accessories or AvatarCategory.TShirt) fresh.Mode = SlotMode.Empty;
            d.Slots[def.Category] = fresh;
        }
        if (!d.SkinLocked) d.SkinHex = SkinTones.All[rng.Next(SkinTones.All.Count)].Hex;
        return d;
    }
}
