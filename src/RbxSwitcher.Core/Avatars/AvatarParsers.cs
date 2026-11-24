using System.Text.Json;

namespace RbxSwitcher.Core.Avatars;

public sealed record BundleInfo(long Id, string Name, int BundleType, IReadOnlyList<long> AssetIds, long? ProductId, bool Free, string? CollectibleItemId = null);

public enum PurchaseOutcome { Acquired, AlreadyOwned, Refused }

/// <summary>
/// Response shapes of the avatar, catalog and inventory endpoints. Tolerant where Roblox is known to vary
/// (numbers vs names for item and bundle types); a missing essential field throws FormatException with a
/// Portuguese reason that never contains a value from the response.
/// </summary>
public static class AvatarParsers
{
    public const long RobloxCreatorId = 1;

    // ── Catalog search (catalog.roblox.com v2/v1 search/items/details) ──

    public static (List<AvatarItem> Items, string? Next) SearchPage(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("busca do catálogo: campo data ausente");
        var items = new List<AvatarItem>();
        foreach (var e in data.EnumerateArray())
            if (SearchItem(e) is { } it) items.Add(it);
        return (items, Str(r, "nextPageCursor"));
    }

    /// <summary>One search result; null when it is not something the builder can use or it is not free from Roblox.</summary>
    public static AvatarItem? SearchItem(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Long(e, "id") is not { } id || id <= 0) return null;
        var kind = KindOf(e, "itemType");
        if (kind is null) return null;
        string name = Str(e, "name") ?? "";
        int assetType = (int)(Long(e, "assetType") ?? 0);
        int bundleType = BundleTypeOf(e, "bundleType");
        long? productId = Long(e, "productId") is > 0 and var p ? p : null;
        string? collectible = Str(e, "collectibleItemId") is { Length: > 0 } c ? c : null;
        bool free = IsFreeFromRoblox(e, productId, collectible);
        var item = new AvatarItem(kind.Value, id, name, kind == ItemKind.Asset ? assetType : 0, kind == ItemKind.Bundle ? bundleType : 0,
                                  productId, collectible, free);
        return item.Category is null ? null : item;
    }

    private static bool IsFreeFromRoblox(JsonElement e, long? productId, string? collectible)
    {
        bool byRoblox = Long(e, "creatorTargetId") == RobloxCreatorId ||
                        string.Equals(Str(e, "creatorName"), "Roblox", StringComparison.OrdinalIgnoreCase);
        string? status = Str(e, "priceStatus");
        bool zero = Long(e, "price") == 0 || string.Equals(status, "Free", StringComparison.OrdinalIgnoreCase);
        bool offSale = string.Equals(status, "Off Sale", StringComparison.OrdinalIgnoreCase) || e.TryGetProperty("isOffSale", out var os) && os.ValueKind == JsonValueKind.True;
        bool limited = false;
        if (e.TryGetProperty("itemRestrictions", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var x in rs.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is "Limited" or "LimitedUnique") limited = true;
        if (Long(e, "totalQuantity") is > 0) limited = true;
        return byRoblox && zero && !offSale && !limited && (productId is not null || collectible is not null);
    }

    // ── Bundles (catalog.roblox.com/v1/bundles/details?bundleIds=) ──

    public static List<BundleInfo> BundleDetails(JsonElement r)
    {
        var arr = r.ValueKind == JsonValueKind.Array ? r : r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : throw new FormatException("pacotes: resposta não é uma lista");
        var list = new List<BundleInfo>();
        foreach (var e in arr.EnumerateArray())
        {
            if (Long(e, "id") is not { } id) continue;
            var assets = new List<long>();
            if (e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    if (KindOf(it, "type") == ItemKind.Asset && Long(it, "id") is { } aid) assets.Add(aid);
            long? productId = null;
            bool free = false;
            if (e.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                productId = Long(p, "id");
                bool isFree = p.TryGetProperty("isFree", out var f) && f.ValueKind == JsonValueKind.True;
                bool forSale = !p.TryGetProperty("isForSale", out var fs) || fs.ValueKind != JsonValueKind.False;
                free = forSale && (isFree || Long(p, "priceInRobux") == 0);
            }
            bool byRoblox = e.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.Object && Long(cr, "id") == RobloxCreatorId;
            string? cid = Str(e, "collectibleItemId") ?? (e.TryGetProperty("collectibleItemDetail", out var cd) && cd.ValueKind == JsonValueKind.Object ? Str(cd, "collectibleItemId") : null);
            list.Add(new BundleInfo(id, Str(e, "name") ?? "", BundleTypeOf(e, "bundleType"), assets, productId, free && byRoblox, cid is { Length: > 0 } ? cid : null));
        }
        return list;
    }

    // ── Asset types of many assets (catalog.roblox.com/v1/catalog/items/details) ──

    public static Dictionary<long, int> ItemAssetTypes(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("detalhes de itens: campo data ausente");
        var map = new Dictionary<long, int>();
        foreach (var e in data.EnumerateArray())
            if (Long(e, "id") is { } id && Long(e, "assetType") is { } t) map[id] = (int)t;
        return map;
    }

    // ── Inventory (inventory.roblox.com/v2/users/{id}/inventory/{assetType}) ──

    public static (List<(long Id, string Name)> Items, string? Next) InventoryPage(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("inventário: campo data ausente");
        var items = new List<(long, string)>();
        foreach (var e in data.EnumerateArray())
            if ((Long(e, "assetId") ?? Long(e, "id")) is { } id && id > 0)
                items.Add((id, Str(e, "assetName") ?? Str(e, "name") ?? ""));
        return (items, Str(r, "nextPageCursor"));
    }

    /// <summary>catalog.roblox.com/v1/users/{id}/bundles</summary>
    public static (List<(long Id, string Name, int BundleType)> Items, string? Next) OwnedBundlesPage(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("pacotes da conta: campo data ausente");
        var items = new List<(long, string, int)>();
        foreach (var e in data.EnumerateArray())
            if (Long(e, "id") is { } id && id > 0)
                items.Add((id, Str(e, "name") ?? "", BundleTypeOf(e, "bundleType")));
        return (items, Str(r, "nextPageCursor"));
    }

    /// <summary>collectibleItemId of one item in catalog/items/details (null when it has none).</summary>
    /// <summary>Every item of a catalog details answer: its Marketplace id, or null when it has none.</summary>
    public static Dictionary<long, string?> CollectibleIds(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("detalhes de itens: campo data ausente");
        var map = new Dictionary<long, string?>();
        foreach (var e in data.EnumerateArray())
            if (Long(e, "id") is { } id) map[id] = Str(e, "collectibleItemId") is { Length: > 0 } cid ? cid : null;
        return map;
    }

    public static string? CollectibleIdOf(JsonElement r, long id)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("detalhes de itens: campo data ausente");
        foreach (var e in data.EnumerateArray())
            if (Long(e, "id") == id && Str(e, "collectibleItemId") is { Length: > 0 } cid) return cid;
        return null;
    }

    public static bool IsOwned(JsonElement r) => r.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("posse do item: resposta não é verdadeiro/falso"),
    };

    // ── Current avatar (avatar.roblox.com/v1/avatar) ──

    public static CurrentAvatar Avatar(JsonElement r)
    {
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new FormatException("avatar atual: campo assets ausente");
        var list = new List<WornAsset>();
        foreach (var a in assets.EnumerateArray())
        {
            if (Long(a, "id") is not { } id) throw new FormatException("avatar atual: item sem id");
            int type = a.TryGetProperty("assetType", out var t) && t.ValueKind == JsonValueKind.Object ? (int)(Long(t, "id") ?? 0) : (int)(Long(a, "assetType") ?? 0);
            if (type == 0) throw new FormatException("avatar atual: item sem tipo");
            list.Add(new WornAsset(id, type, Str(a, "name") ?? ""));
        }
        return new CurrentAvatar(list, Str(r, "playerAvatarType"));
    }

    /// <summary>avatar-rules: "wearableAssetTypes": [{"maxNumber": 3, "id": 8, "name": "Hat"}, …].</summary>
    public static AvatarRules Rules(JsonElement r)
    {
        if (!r.TryGetProperty("wearableAssetTypes", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("regras do avatar: campo wearableAssetTypes ausente");
        var max = new Dictionary<int, int>();
        foreach (var t in list.EnumerateArray())
            if (Long(t, "id") is { } id and > 0 && Long(t, "maxNumber") is { } n and > 0) max[(int)id] = (int)n;
        if (max.Count == 0) throw new FormatException("regras do avatar: nenhum tipo com limite");
        return new AvatarRules(max);
    }

    /// <summary>
    /// The "ValidationErrors" of a refused set-wearing-assets, wherever Roblox puts them: at the top, or as a JSON text
    /// inside errors[].message (as in {"errors":[{"code":0,"message":"{\"ValidationErrors\":[…]}"}]}).
    /// </summary>
    public static IReadOnlyList<(string Message, string? Field, string? Data)> WearValidation(JsonElement r)
    {
        var found = new List<(string, string?, string?)>();
        Walk(r, 0);
        return found;

        void Walk(JsonElement e, int depth)
        {
            if (depth > 4) return;
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, depth + 1); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name.Equals("ValidationErrors", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in p.Value.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.Object)
                            found.Add((StrAny(v, "Message", "message") ?? "", StrAny(v, "FieldName", "fieldName"), StrAny(v, "FieldData", "fieldData")));
                }
                else if (p.Name.Equals("errors", StringComparison.OrdinalIgnoreCase)) Walk(p.Value, depth + 1);
                else if (p.Name.Equals("message", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                         && p.Value.GetString() is { } text && text.TrimStart().StartsWith('{') && text.Length < 16_384)
                {
                    try { using var inner = JsonDocument.Parse(text); Walk(inner.RootElement.Clone(), depth + 1); }
                    catch (JsonException) { }
                }
            }
        }

        static string? StrAny(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v))
                    return v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
            return null;
        }
    }

    /// <summary>A short Portuguese reason for validation errors (no ids).</summary>
    public static string DescribeValidation(IReadOnlyList<(string Message, string? Field, string? Data)> errors) =>
        string.Join("; ", errors.Select(e => e.Message switch
        {
            "LimitExceeded" => "itens demais de um mesmo tipo (o Roblox limita quantos de cada tipo dá para vestir juntos)",
            "NotOwned" or "AssetNotOwned" => "a conta não possui um dos itens",
            "InvalidAssetType" or "InvalidAsset" => "um dos itens não pode ser vestido",
            { Length: > 0 } m => m,
            _ => "combinação recusada",
        }).Distinct());

    // ── Writes ──

    public static IReadOnlyList<long> InvalidAssetIds(JsonElement r)
    {
        var ids = new List<long>();
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("invalidAssetIds", out var inv) && inv.ValueKind == JsonValueKind.Array)
            foreach (var x in inv.EnumerateArray()) if (x.TryGetInt64(out long v)) ids.Add(v);
        return ids;
    }

    /// <summary>economy purchases/products and marketplace-sales purchase-item responses.</summary>
    public static (PurchaseOutcome Outcome, string? Reason) Purchase(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("compra: resposta inesperada");
        bool purchased = r.TryGetProperty("purchased", out var p) && p.ValueKind == JsonValueKind.True;
        string? reason = Str(r, "reason") ?? Str(r, "purchaseResult");
        string? error = Str(r, "errorMsg") ?? Str(r, "errorMessage");
        string all = (reason ?? "") + " " + (error ?? "");
        if (all.Contains("AlreadyOwned", StringComparison.OrdinalIgnoreCase) || all.Contains("already own", StringComparison.OrdinalIgnoreCase))
            return (PurchaseOutcome.AlreadyOwned, null);
        if (purchased) return (PurchaseOutcome.Acquired, null);
        string why = reason is { Length: > 0 and < 60 } && reason.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '_')
            ? reason : "o Roblox não concluiu";
        return (PurchaseOutcome.Refused, why);
    }

    /// <summary>marketplace-items/v1/items/details: the product id of a collectible item and its current price.</summary>
    public static (string ProductId, long? Price)? CollectibleProduct(JsonElement r, string collectibleItemId)
    {
        var arr = r.ValueKind == JsonValueKind.Array ? r : r.TryGetProperty("data", out var d) ? d : default;
        if (arr.ValueKind != JsonValueKind.Array) throw new FormatException("item colecionável: resposta não é uma lista");
        foreach (var e in arr.EnumerateArray())
            if (string.Equals(Str(e, "collectibleItemId"), collectibleItemId, StringComparison.OrdinalIgnoreCase) &&
                Str(e, "collectibleProductId") is { Length: > 0 } pid)
                return (pid, Long(e, "price"));
        return null;
    }

    // ── helpers ──

    internal static ItemKind? KindOf(JsonElement e, string field) =>
        !e.TryGetProperty(field, out var v) ? null
        : v.ValueKind == JsonValueKind.Number ? (v.TryGetInt32(out int n) ? n switch { 1 => ItemKind.Asset, 2 => ItemKind.Bundle, _ => null } : null)
        : v.ValueKind == JsonValueKind.String ? v.GetString() switch
        {
            "Asset" or "asset" => ItemKind.Asset,
            "Bundle" or "bundle" => ItemKind.Bundle,
            _ => null,
        }
        : null;

    internal static int BundleTypeOf(JsonElement e, string field)
    {
        if (!e.TryGetProperty(field, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        return v.ValueKind == JsonValueKind.String ? v.GetString() switch
        {
            "BodyParts" => 1, "AvatarAnimations" => 2, "Shoes" => 3, "DynamicHead" => 4, "DynamicHeadAvatar" => 5, _ => 0,
        } : 0;
    }

    internal static long? Long(JsonElement e, string field)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s)) return s;
        return null;
    }

    internal static string? Str(JsonElement e, string field) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
