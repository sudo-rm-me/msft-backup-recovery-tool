using System.Text.Json.Nodes;
using Mbr.Core.Catalog;
using Mbr.Core.Json;

namespace Mbr.Infrastructure.Restore;

/// <summary>
/// Re-linking: when an object comes back (recreated or undeleted), existing objects that referenced it get those references
/// added back, without reverting anything else that changed on them since the snapshot.
/// </summary>
public sealed partial class RestoreEngine
{
    /// <summary>The snapshot references <paramref name="returning"/> through a non-array property (e.g. a role assignment's principalId).</summary>
    private static bool IsAboutReturning(ResourceDefinition def, JsonObject snapshot, Func<string, string, bool> returning) =>
        def.References.Any(r => !r.Path.Contains("[*]") && JsonPath.GetStrings(snapshot, r.Path).Any(id => returning(r.Type, id)));

    /// <summary>References from <paramref name="snapshot"/> to <paramref name="returning"/> objects.</summary>
    private static bool ReferencesReturning(ResourceDefinition def, JsonObject snapshot, Func<string, string, bool> returning) =>
        def.References.Any(r => JsonPath.GetStrings(snapshot, r.Path).Any(id => returning(r.Type, id)));

    /// <summary>
    /// Live content plus the snapshot's references to returning objects: array entries (assignments, members, included groups)
    /// that mention a returning object are appended if missing; scalar references are set.
    /// </summary>
    private static JsonObject MergeRelink(ResourceDefinition def, JsonObject live, JsonObject snapshot, Func<string, string, bool> returning)
    {
        var merged = (JsonObject)live.DeepClone();
        foreach (var r in def.References)
        {
            var star = r.Path.IndexOf("[*]", StringComparison.Ordinal);
            if (star < 0)
            {
                if (JsonPath.GetStrings(snapshot, r.Path).FirstOrDefault(id => returning(r.Type, id)) is { } id) SetPath(merged, r.Path, JsonValue.Create(id));
                continue;
            }
            var arrayPath = r.Path[..star];
            var rest = r.Path[(star + 3)..].TrimStart('.');
            if (rest.Contains("[*]")) continue; // nested arrays: not re-linked automatically
            if (JsonPath.Select(snapshot, arrayPath).FirstOrDefault() is not JsonArray source) continue;
            JsonArray? target = null;
            foreach (var element in source)
            {
                var values = rest.Length == 0 ? (element is JsonValue v && v.TryGetValue<string>(out var sv) ? [sv] : []) : JsonPath.GetStrings(element, rest).ToArray();
                if (!values.Any(id => returning(r.Type, id))) continue;
                target ??= GetOrCreateArray(merged, arrayPath);
                if (!target.Any(t => JsonNode.DeepEquals(t, element))) target.Add(element?.DeepClone());
            }
        }
        return merged;
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string path)
    {
        var parts = path.Split('.');
        JsonObject node = root;
        foreach (var p in parts[..^1])
        {
            if (node[p] is not JsonObject child) node[p] = child = new JsonObject();
            node = child;
        }
        if (node[parts[^1]] is not JsonArray arr) node[parts[^1]] = arr = new JsonArray();
        return arr;
    }

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonObject node = root;
        foreach (var p in parts[..^1])
        {
            if (node[p] is not JsonObject child) node[p] = child = new JsonObject();
            node = child;
        }
        node[parts[^1]] = value;
    }
}
