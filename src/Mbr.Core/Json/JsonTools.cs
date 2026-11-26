using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mbr.Core.Json;

/// <summary>
/// Minimal path language over JSON: dot-separated property names, with "[*]" to fan out over arrays.
/// Examples: "conditions.users.includeGroups[*]", "assignments[*].target.groupId", "roleScopeTagIds[*]".
/// </summary>
public static class JsonPath
{
    private static IEnumerable<(string Name, bool Fanout)> Parse(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.EndsWith("[*]") ? (p[..^3], true) : (p, false));

    /// <summary>All scalar string values at the path.</summary>
    public static IEnumerable<string> GetStrings(JsonNode? root, string path) =>
        Select(root, path).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!);

    public static IEnumerable<JsonNode> Select(JsonNode? root, string path)
    {
        IEnumerable<JsonNode?> current = [root];
        foreach (var (name, fanout) in Parse(path))
        {
            current = current.SelectMany<JsonNode?, JsonNode?>(n =>
            {
                var child = n is JsonObject o && o.TryGetPropertyValue(name, out var c) ? c : null;
                if (!fanout) return [child];
                return child is JsonArray a ? a : [];
            });
        }
        return current.Where(n => n != null)!;
    }

    /// <summary>Replaces every string value at the path using the mapper (return null to remove array entries / set property null).</summary>
    public static int Rewrite(JsonNode? root, string path, Func<string, string?> map)
    {
        var parts = Parse(path).ToList();
        return RewriteAt(root, parts, 0, map);
    }

    private static int RewriteAt(JsonNode? node, List<(string Name, bool Fanout)> parts, int i, Func<string, string?> map)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(parts[i].Name, out var child) || child is null) return 0;
        var last = i == parts.Count - 1;
        if (parts[i].Fanout)
        {
            if (child is not JsonArray arr) return 0;
            if (!last) return arr.Sum(el => RewriteAt(el, parts, i + 1, map));
            var count = 0;
            for (var k = arr.Count - 1; k >= 0; k--)
            {
                if (arr[k] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    var mapped = map(s);
                    if (mapped is null) arr.RemoveAt(k);
                    else if (mapped != s) arr[k] = mapped;
                    count++;
                }
            }
            return count;
        }
        if (!last) return RewriteAt(child, parts, i + 1, map);
        if (child is JsonValue cv && cv.TryGetValue<string>(out var str))
        {
            var mapped = map(str);
            if (mapped != str) o[parts[i].Name] = mapped;
            return 1;
        }
        return 0;
    }

    /// <summary>Removes the property at the path (supports [*] for nested arrays of objects).</summary>
    public static void Remove(JsonNode? root, string path)
    {
        var parts = Parse(path).ToList();
        var parents = Select(root, string.Join('.', parts.Take(parts.Count - 1).Select(p => p.Name + (p.Fanout ? "[*]" : ""))));
        if (parts.Count == 1) parents = root is null ? [] : [root];
        foreach (var p in parents.OfType<JsonObject>()) p.Remove(parts[^1].Name);
    }
}

public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns a deep copy with object keys sorted (ordinal) so equal content serializes identically.</summary>
    public static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    public static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(Sort(node)!.ToJsonString(Indented));

    public static JsonNode? Parse(byte[] bytes) => JsonNode.Parse(bytes);
}
