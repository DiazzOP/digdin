using System.Text.Json.Serialization;

namespace RbxSwitcher.Core.Avatars;

public enum ItemKind { Asset, Bundle }

/// <summary>The groups the avatar builder shows (Portuguese names in <see cref="AvatarCategories"/>).</summary>
public enum AvatarCategory { Hair, Accessories, Head, Shirt, Pants, TShirt, Body }

/// <summary>A catalog item the builder can show and wear. Public catalog data only.</summary>
/// <param name="AssetType">Roblox asset type id (assets only; 0 for bundles).</param>
/// <param name="BundleType">Roblox bundle type id (bundles only; 0 for assets).</param>
/// <param name="Free">Made by Roblox, on sale for exactly 0 Robux, not limited. Only these are ever acquired.</param>
/// <param name="BundleAssets">For bundles: the assets it puts on (id → asset type, 0 when Roblox did not say).</param>
public sealed record AvatarItem(ItemKind Kind, long Id, string Name, int AssetType, int BundleType,
                                long? ProductId, string? CollectibleItemId, bool Free,
                                IReadOnlyDictionary<long, int>? BundleAssets = null)
{
    public string Key => (Kind == ItemKind.Bundle ? "b" : "a") + Id;
    public AvatarCategory? Category => AvatarCategories.Of(this);
}

public sealed record AvatarCategoryDef(AvatarCategory Category, string Name, IReadOnlyList<int> AssetTypes,
                                       IReadOnlyList<int> BundleTypes, int MaxItems);

public static class AvatarCategories
{
    // Roblox AssetType ids.
    public const int TShirt = 2, Hat = 8, Shirt = 11, Pants = 12, Head = 17, Face = 18;
    public const int Torso = 27, RightArm = 28, LeftArm = 29, LeftLeg = 30, RightLeg = 31;
    public const int HairAccessory = 41, FaceAccessory = 42, NeckAccessory = 43, ShoulderAccessory = 44,
                     FrontAccessory = 45, BackAccessory = 46, WaistAccessory = 47;
    public const int TShirtAccessory = 64, ShirtAccessory = 65, PantsAccessory = 66, JacketAccessory = 67,
                     SweaterAccessory = 68, ShortsAccessory = 69, LeftShoe = 70, RightShoe = 71, DressSkirt = 72,
                     EyebrowAccessory = 76, EyelashAccessory = 77, DynamicHead = 79;
    // Roblox BundleType ids.
    public const int BodyPartsBundle = 1, DynamicHeadBundle = 4;

    public static readonly IReadOnlyList<AvatarCategoryDef> All =
    [
        new(AvatarCategory.Hair, "Cabelo", [HairAccessory], [], 1),
        new(AvatarCategory.Accessories, "Acessórios",
            [Hat, FaceAccessory, NeckAccessory, ShoulderAccessory, FrontAccessory, BackAccessory, WaistAccessory, LeftShoe, RightShoe, EyebrowAccessory, EyelashAccessory], [], 4),
        new(AvatarCategory.Head, "Cabeça e rosto", [Head, Face, DynamicHead], [DynamicHeadBundle], 2),
        new(AvatarCategory.Shirt, "Camisa", [Shirt, ShirtAccessory, JacketAccessory, SweaterAccessory, TShirtAccessory], [], 2),
        new(AvatarCategory.Pants, "Calça", [Pants, PantsAccessory, ShortsAccessory, DressSkirt], [], 2),
        new(AvatarCategory.TShirt, "Camiseta", [TShirt], [], 1),
        new(AvatarCategory.Body, "Corpo", [Torso, RightArm, LeftArm, LeftLeg, RightLeg], [BodyPartsBundle], 1),
    ];

    public static AvatarCategoryDef Def(AvatarCategory c) => All.First(d => d.Category == c);

    public static AvatarCategory? OfAssetType(int assetType) => All.FirstOrDefault(d => d.AssetTypes.Contains(assetType))?.Category;

    public static AvatarCategory? Of(AvatarItem item) => item.Kind == ItemKind.Bundle
        ? All.FirstOrDefault(d => d.BundleTypes.Contains(item.BundleType))?.Category
        : OfAssetType(item.AssetType);

    /// <summary>Types an avatar wears only one of at a time (a new one replaces the old one).</summary>
    public static bool SingleSlot(int assetType) => assetType is TShirt or Shirt or Pants or Head or Face or DynamicHead
        or Torso or RightArm or LeftArm or LeftLeg or RightLeg or (>= 48 and <= 57) or 78;
}

/// <summary>
/// How many assets of each type Roblox lets an avatar wear at once (avatar.roblox.com/v1/avatar-rules). Types not
/// listed have no limit here. <see cref="Default"/> is used when the rules cannot be read: only limits known for sure.
/// </summary>
public sealed record AvatarRules(IReadOnlyDictionary<int, int> MaxPerType)
{
    public static readonly AvatarRules Default = new(new Dictionary<int, int>
    {
        [AvatarCategories.Hat] = 3,
        [AvatarCategories.EyebrowAccessory] = 1, [AvatarCategories.EyelashAccessory] = 1,
        [AvatarCategories.LeftShoe] = 1, [AvatarCategories.RightShoe] = 1,
    });

    /// <summary>The limit for a type: the rules' own, else 1 for one-of-a-kind slots, else none.</summary>
    public int Max(int assetType) =>
        MaxPerType.TryGetValue(assetType, out int m) && m > 0 ? m
        : Default.MaxPerType.TryGetValue(assetType, out int d) ? d
        : AvatarCategories.SingleSlot(assetType) ? 1 : int.MaxValue;
}

/// <summary>Skin tones offered by the builder: Roblox colour (hex) and the matching BrickColor for the older API.</summary>
public sealed record SkinTone(string Name, string Hex, int BrickColorId);

public static class SkinTones
{
    public static readonly IReadOnlyList<SkinTone> All =
    [
        new("Pêssego claro", "FFCC99", 1030),
        new("Laranja claro", "EAB892", 125),
        new("Nougat", "CC8E69", 18),
        new("Nougat escuro", "AE7A59", 128),
        new("Laranja escuro", "A05F35", 38),
        new("Marrom", "7C5C46", 217),
        new("Marrom avermelhado", "694028", 192),
        new("Amarelo clássico", "F5CD30", 24),
    ];

    public static SkinTone? Find(string? hex) => hex is null ? null : All.FirstOrDefault(t => t.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase));
}

public enum SlotMode
{
    /// <summary>Leave whatever the account wears in this category.</summary>
    Keep,
    /// <summary>Take everything of this category off.</summary>
    Empty,
    /// <summary>Wear the chosen items (they replace what the account wears in this category).</summary>
    Items,
}

/// <summary>A chosen item, as saved in a design (enough to wear it later; the catalog fills in the rest).</summary>
public sealed record ItemRef(ItemKind Kind, long Id, string Name, int AssetType = 0, int BundleType = 0)
{
    public string Key => (Kind == ItemKind.Bundle ? "b" : "a") + Id;
    public static ItemRef From(AvatarItem i) => new(i.Kind, i.Id, i.Name, i.AssetType, i.BundleType);
}

public sealed class SlotChoice
{
    public SlotMode Mode { get; set; } = SlotMode.Keep;
    public List<ItemRef> Items { get; set; } = [];
    /// <summary>"Sortear" leaves a locked category as it is.</summary>
    public bool Locked { get; set; }

    public SlotChoice Clone() => new() { Mode = Mode, Items = [.. Items], Locked = Locked };
}

/// <summary>What the builder will put on an account. Categories not listed are kept.</summary>
public sealed class AvatarDesign
{
    public Dictionary<AvatarCategory, SlotChoice> Slots { get; set; } = AvatarCategories.All.ToDictionary(d => d.Category, _ => new SlotChoice());
    /// <summary>Skin tone hex (from <see cref="SkinTones"/>); null = keep the account's colours.</summary>
    public string? SkinHex { get; set; }
    public bool SkinLocked { get; set; }
    /// <summary>"R15" or "R6"; null = keep.</summary>
    public string? AvatarType { get; set; }

    public SlotChoice Slot(AvatarCategory c)
    {
        if (!Slots.TryGetValue(c, out var s)) Slots[c] = s = new SlotChoice();
        return s;
    }

    public AvatarDesign Clone() => new()
    {
        Slots = Slots.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        SkinHex = SkinHex, SkinLocked = SkinLocked, AvatarType = AvatarType,
    };

    [JsonIgnore]
    public bool ChangesNothing => SkinHex is null && AvatarType is null && Slots.Values.All(s => s.Mode == SlotMode.Keep);
}

/// <summary>A design saved by name. Lives in the encrypted vault.</summary>
public sealed class AvatarPreset
{
    public string Name { get; set; } = "";
    public DateTimeOffset SavedUtc { get; set; }
    public AvatarDesign Design { get; set; } = new();
}

/// <summary>What an account wears now (from avatar.roblox.com/v1/avatar).</summary>
public sealed record WornAsset(long Id, int AssetType, string Name);
public sealed record CurrentAvatar(IReadOnlyList<WornAsset> Assets, string? AvatarType);
