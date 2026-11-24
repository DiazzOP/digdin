using System.Text.Json;
using System.Text.RegularExpressions;

namespace RbxSwitcher.Core.Logging;

/// <summary>
/// Describes a JSON response by field names and types only, never values: "subscriptions:array(2),
/// subscriptions[].productType:string, …". Used to log what Roblox sent when a parser fails, so a
/// format mismatch can be found without recording cookies, ids, names or amounts.
/// </summary>
public static partial class JsonShape
{
    private const int MaxDepth = 6;
    private const int MaxEntries = 80;

    // Keys that look like data (ids, hashes, very long names) are replaced, so no value leaks through a key.
    [GeneratedRegex(@"^[0-9]+$|^[0-9a-fA-F-]{16,}$")]
    private static partial Regex DataLikeKey();

    public static string Describe(JsonElement root)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, "", 0, seen);
        string text = string.Join(", ", seen.Take(MaxEntries));
        return seen.Count > MaxEntries ? text + $", … (+{seen.Count - MaxEntries})" : text;
    }

    private static void Walk(JsonElement e, string path, int depth, SortedSet<string> seen)
    {
        string here = path.Length == 0 ? "(raiz)" : path;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                seen.Add($"{here}:object");
                if (depth >= MaxDepth) return;
                foreach (var p in e.EnumerateObject())
                {
                    string key = p.Name.Length > 40 || DataLikeKey().IsMatch(p.Name) ? "<chave>" : p.Name;
                    Walk(p.Value, path.Length == 0 ? key : path + "." + key, depth + 1, seen);
                }
                break;
            case JsonValueKind.Array:
                seen.Add($"{here}:array({e.GetArrayLength()})");
                if (depth >= MaxDepth) return;
                foreach (var item in e.EnumerateArray())
                    Walk(item, path + "[]", depth + 1, seen); // union of the fields of every item
                break;
            case JsonValueKind.String: seen.Add($"{here}:string"); break;
            case JsonValueKind.Number: seen.Add($"{here}:number"); break;
            case JsonValueKind.True:
            case JsonValueKind.False: seen.Add($"{here}:bool"); break;
            case JsonValueKind.Null: seen.Add($"{here}:null"); break;
        }
    }
}
