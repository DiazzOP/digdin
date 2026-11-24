using System.Text.Json;

namespace RbxSwitcher.Core.Limiteds;

/// <summary>
/// Response shapes for the Limited readings. Missing essentials throw FormatException with a Portuguese,
/// value-free reason; optional fields are null when absent.
/// </summary>
public static class LimitedParsers
{
    /// <summary>inventory.roblox.com/v1/users/{id}/assets/collectibles</summary>
    public static (List<LimitedCopy> Copies, int Ambiguous, string? Next) CollectiblesPage(JsonElement r)
    {
        if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("Limiteds: campo data ausente");
        var copies = new List<LimitedCopy>();
        int ambiguous = 0;
        foreach (var e in data.EnumerateArray())
        {
            long? asset = Long(e, "assetId");
            if (asset is not > 0) throw new FormatException("Limiteds: cópia sem assetId");
            if (Long(e, "userAssetId") is not { } uaid || uaid <= 0) { ambiguous++; continue; }
            copies.Add(new LimitedCopy(uaid, asset.Value, Str(e, "name") ?? "", (int?)Long(e, "serialNumber"), Long(e, "recentAveragePrice"),
                e.TryGetProperty("isOnHold", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False ? h.GetBoolean() : null));
        }
        return (copies, ambiguous, Str(r, "nextPageCursor"));
    }

    /// <summary>economy.roblox.com/v1/assets/{id}/resale-data: RAP and sales per day over the last 30 days.</summary>
    public static (long? Rap, double? SalesPerDay) ResaleData(JsonElement r, DateTimeOffset now)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("revenda: resposta inesperada");
        long? rap = Long(r, "recentAveragePrice");
        double? perDay = null;
        if (r.TryGetProperty("volumeDataPoints", out var vol) && vol.ValueKind == JsonValueKind.Array)
        {
            double total = 0;
            foreach (var p in vol.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number && vv.TryGetDouble(out double v)
                    && Str(p, "date") is { } d && DateTimeOffset.TryParse(d, out var at) && now - at <= TimeSpan.FromDays(30))
                    total += v;
            perDay = Math.Round(total / 30.0, 2);
        }
        return (rap, perDay);
    }

    /// <summary>
    /// api.rolimons.com/items/v2/itemdetails: {"items": {"assetId": [name, acronym, rap, value, default_value, demand, trend,
    /// projected, hyped, rare]}}. Value and demand are -1 when Rolimons has none.
    /// </summary>
    public static Dictionary<long, (string Name, long? Rap, long? Value, int? Demand)> RolimonsItems(JsonElement r)
    {
        if (!r.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            throw new FormatException("Rolimons: campo items ausente");
        var map = new Dictionary<long, (string, long?, long?, int?)>();
        foreach (var p in items.EnumerateObject())
        {
            if (!long.TryParse(p.Name, out long id) || p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() < 6) continue;
            var a = p.Value;
            long? At(int i) => a[i].ValueKind == JsonValueKind.Number && a[i].TryGetInt64(out long v) && v >= 0 ? v : null;
            map[id] = (a[0].ValueKind == JsonValueKind.String ? a[0].GetString() ?? "" : "", At(2), At(3), (int?)At(5));
        }
        return map;
    }

    /// <summary>
    /// api.rolimons.com/players/v1/playerassets/{id}: "playerAssets": {"assetId": [userAssetId, …]} and "holds": [userAssetId, …].
    /// </summary>
    public static (Dictionary<long, long> AssetOf, List<long> Holds) RolimonsPlayer(JsonElement r)
    {
        var assetOf = new Dictionary<long, long>();
        if (r.TryGetProperty("playerAssets", out var pa) && pa.ValueKind == JsonValueKind.Object)
            foreach (var p in pa.EnumerateObject())
                if (long.TryParse(p.Name, out long asset) && p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var u in p.Value.EnumerateArray()) if (u.TryGetInt64(out long uaid)) assetOf[uaid] = asset;
        return (assetOf, RolimonsHolds(r));
    }

    /// <summary>api.rolimons.com/players/v1/playerassets/{id}: the userAssetIds Rolimons lists as on hold.</summary>
    public static List<long> RolimonsHolds(JsonElement r)
    {
        var holds = new List<long>();
        if (r.TryGetProperty("holds", out var h) && h.ValueKind == JsonValueKind.Array)
            foreach (var x in h.EnumerateArray()) if (x.TryGetInt64(out long v)) holds.Add(v);
        return holds;
    }

    private static long? Long(JsonElement e, string f) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(f, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long s) ? s : null
            : null;

    private static string? Str(JsonElement e, string f) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
