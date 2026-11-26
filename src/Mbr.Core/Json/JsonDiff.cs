using System.Text.Json.Nodes;

namespace Mbr.Core.Json;

public enum DiffKind { Added, Removed, Changed }

public record JsonChange(string Path, DiffKind Kind, JsonNode? Before, JsonNode? After);

/// <summary>Structural JSON diff producing property-level changes.</summary>
public static class JsonDiff
{
    /// <summary>
    /// Arrays of scalars are compared as sets (order is rarely meaningful in Graph objects, e.g. group ID lists).
    /// Arrays of objects are matched by a key ("id", "settingDefinitionId", "@odata.type"+target) when possible, else by index.
    /// </summary>
    public static List<JsonChange> Compare(JsonNode? before, JsonNode? after)
    {
        var changes = new List<JsonChange>();
        Walk("", before, after, changes);
        return changes;
    }

    private static void Walk(string path, JsonNode? a, JsonNode? b, List<JsonChange> changes)
    {
        if (a is null && b is null) return;
        if (a is null) { changes.Add(new(path, DiffKind.Added, null, b!.DeepClone())); return; }
        if (b is null) { changes.Add(new(path, DiffKind.Removed, a.DeepClone(), null)); return; }

        switch (a, b)
        {
            case (JsonObject oa, JsonObject ob):
                foreach (var key in oa.Select(kv => kv.Key).Union(ob.Select(kv => kv.Key)).OrderBy(k => k, StringComparer.Ordinal))
                {
                    var hasA = oa.TryGetPropertyValue(key, out var va);
                    var hasB = ob.TryGetPropertyValue(key, out var vb);
                    var p = Join(path, key);
                    if (hasA && !hasB) { if (va is not null) changes.Add(new(p, DiffKind.Removed, va.DeepClone(), null)); }
                    else if (!hasA && hasB) { if (vb is not null) changes.Add(new(p, DiffKind.Added, null, vb.DeepClone())); }
                    else Walk(p, va, vb, changes);
                }
                break;
            case (JsonArray aa, JsonArray ab):
                CompareArrays(path, aa, ab, changes);
                break;
            default:
                if (!JsonNode.DeepEquals(a, b)) changes.Add(new(path, DiffKind.Changed, a.DeepClone(), b.DeepClone()));
                break;
        }
    }

    private static void CompareArrays(string path, JsonArray a, JsonArray b, List<JsonChange> changes)
    {
        if (a.All(x => x is JsonValue or null) && b.All(x => x is JsonValue or null))
        {
            var sa = a.Select(x => x?.ToJsonString() ?? "null").ToList();
            var sb = b.Select(x => x?.ToJsonString() ?? "null").ToList();
            foreach (var removed in sa.Except(sb)) changes.Add(new($"{path}[]", DiffKind.Removed, JsonNode.Parse(removed), null));
            foreach (var added in sb.Except(sa)) changes.Add(new($"{path}[]", DiffKind.Added, null, JsonNode.Parse(added)));
            return;
        }

        var keyFn = KeyFunction(a.Concat(b).OfType<JsonObject>().ToList());
        if (keyFn != null)
        {
            var ma = a.OfType<JsonObject>().GroupBy(keyFn).ToDictionary(g => g.Key, g => g.First());
            var mb = b.OfType<JsonObject>().GroupBy(keyFn).ToDictionary(g => g.Key, g => g.First());
            foreach (var k in ma.Keys.Union(mb.Keys).Order(StringComparer.Ordinal))
                Walk($"{path}[{k}]", ma.GetValueOrDefault(k), mb.GetValueOrDefault(k), changes);
            return;
        }
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            Walk($"{path}[{i}]", i < a.Count ? a[i] : null, i < b.Count ? b[i] : null, changes);
    }

    /// <summary>Finds a property that uniquely identifies the objects in an array.</summary>
    private static Func<JsonObject, string>? KeyFunction(List<JsonObject> items)
    {
        if (items.Count == 0) return null;
        string? Get(JsonObject o, string path)
        {
            JsonNode? n = o;
            foreach (var part in path.Split('.')) n = n is JsonObject x ? x[part] : null;
            return n is JsonValue v ? v.ToString() : null;
        }
        foreach (var candidate in new[] { "id", "settingInstance.settingDefinitionId", "settingDefinitionId", "target.groupId", "ruleName", "name", "displayName" })
        {
            var keys = items.Select(o => Get(o, candidate)).ToList();
            if (keys.All(k => k != null)) return o => Get(o, candidate)!;
        }
        return null;
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
