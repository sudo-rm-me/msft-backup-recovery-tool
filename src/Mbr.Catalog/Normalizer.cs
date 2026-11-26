using System.Text.Json.Nodes;
using Mbr.Core.Catalog;
using Mbr.Core.Json;

namespace Mbr.Catalog;

/// <summary>Strips read-only and volatile properties so snapshots diff cleanly and can be sent back on restore.</summary>
public static class Normalizer
{
    /// <summary>Removed from every object at the top level.</summary>
    public static readonly string[] DefaultStrip =
    [
        "id", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime", "createdTime", "lastModifiedTime",
        "version", "deletedDateTime", "renewedDateTime", "@odata.context", "@odata.etag", "@odata.nextLink",
    ];

    public static JsonObject Normalize(JsonObject raw, ResourceDefinition def)
    {
        var o = (JsonObject)raw.DeepClone();
        foreach (var p in DefaultStrip) o.Remove(p);
        foreach (var p in def.Strip) JsonPath.Remove(o, p);
        RemoveODataAnnotations(o, keepType: true);
        return o;
    }

    public static JsonObject NormalizeChild(JsonObject raw, ChildDefinition child)
    {
        var o = (JsonObject)raw.DeepClone();
        foreach (var p in child.Strip) JsonPath.Remove(o, p);
        RemoveODataAnnotations(o, keepType: true);
        return o;
    }

    /// <summary>Removes "@odata.*" annotations and "*@odata.*" navigation links recursively, keeping @odata.type (needed to create polymorphic objects).</summary>
    public static void RemoveODataAnnotations(JsonNode? node, bool keepType)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    var isAnnotation = key.Contains("@odata.") && !(keepType && key == "@odata.type");
                    if (isAnnotation) o.Remove(key);
                    else RemoveODataAnnotations(o[key], keepType);
                }
                break;
            case JsonArray a:
                foreach (var el in a) RemoveODataAnnotations(el, keepType);
                break;
        }
    }

    public static string DisplayNameOf(JsonObject raw, ResourceDefinition def, string id)
    {
        foreach (var key in new[] { def.NameProperty, "displayName", "name", "userPrincipalName" })
            if (raw[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        return id;
    }
}
