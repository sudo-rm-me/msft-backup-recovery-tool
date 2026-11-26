using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>Graph behaviours beyond plain CRUD: recycle bin, directoryObjects lookup, /assign and $ref.</summary>
public partial class InMemoryGraph
{
    /// <summary>Collections whose deleted items go to the directory recycle bin.</summary>
    public HashSet<string> RecycleBinCollections { get; } = new(StringComparer.OrdinalIgnoreCase)
        { "users", "groups", "applications", "directory/administrativeUnits" };

    private readonly Dictionary<string, (string Collection, JsonObject Item)> _deleted = new();
    private static readonly string[] DirectoryCollections = ["users", "groups", "servicePrincipals", "applications", "directory/administrativeUnits"];

    /// <summary>Puts an object straight into the recycle bin (for demo/test data).</summary>
    public void AddDeleted(string collection, JsonObject item, DateTimeOffset deletedAt)
    {
        item["id"] ??= NewId();
        item["deletedDateTime"] = deletedAt.ToString("o");
        lock (_deleted) _deleted[item["id"]!.GetValue<string>()] = (Normalize(collection), item);
    }

    /// <summary>Removes a deleted object from the recycle bin (simulates a purge).</summary>
    public void Purge(string id) { lock (_deleted) _deleted.Remove(id); }

    /// <summary>Moves a write's arrays into existing child collections ({collection}/{id}/{property}).</summary>
    private void DistributeChildren(string collection, JsonObject body, string? id = null)
    {
        id ??= body["id"]?.GetValue<string>();
        foreach (var (k, v) in body.ToList())
        {
            if (v is not JsonArray arr || !Collections.TryGetValue($"{collection}/{id}/{k}", out var child)) continue;
            lock (child) { child.Clear(); child.AddRange(arr.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone())); }
            body.Remove(k);
        }
    }

    private JsonObject? FindDirectoryObject(string id) => FindDirectoryObjectWithType(id)?.Item;

    private (JsonObject Item, string ODataType)? FindDirectoryObjectWithType(string id)
    {
        foreach (var c in DirectoryCollections)
            if (Collections.TryGetValue(c, out var list))
                lock (list)
                    if (list.FirstOrDefault(i => i["id"]?.GetValue<string>() == id) is { } found)
                        return (found, found["@odata.type"]?.ToString() ?? "#microsoft.graph." + c.Split('/')[^1].TrimEnd('s'));
        return null;
    }

    private static string CollectionForType(string odataType) => odataType switch
    {
        "user" => "users", "group" => "groups", "application" => "applications", "administrativeUnit" => "directory/administrativeUnits", var x => x,
    };

    private HttpResponseMessage? HandleSpecial(HttpMethod method, string path, string? body)
    {
        var parts = path.Split('/');

        if (method == HttpMethod.Get && parts is ["directoryObjects", var oid])
            return FindDirectoryObject(oid) is { } o ? Json(o.DeepClone()) : Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{oid}' does not exist.");

        if (parts is ["directory", "deletedItems", _, ..])
        {
            lock (_deleted)
            {
                var key = parts[2];
                if (method == HttpMethod.Get && parts.Length == 3 && key.StartsWith("microsoft.graph.", StringComparison.OrdinalIgnoreCase))
                {
                    var collection = CollectionForType(key["microsoft.graph.".Length..]);
                    return Json(new JsonObject
                    {
                        ["value"] = new JsonArray(_deleted.Values.Where(d => d.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase))
                            .Select(d => (JsonNode)d.Item.DeepClone()).ToArray()),
                    });
                }
                if (!_deleted.TryGetValue(key, out var entry))
                    return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{key}' does not exist.");
                if (method == HttpMethod.Get && parts.Length == 3) return Json(entry.Item.DeepClone());
                if (method == HttpMethod.Delete && parts.Length == 3)
                {
                    _deleted.Remove(key);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (method == HttpMethod.Post && parts is [.., "restore"])
                {
                    _deleted.Remove(key);
                    entry.Item.Remove("deletedDateTime");
                    var list = Collection(entry.Collection);
                    lock (list) list.Add(entry.Item);
                    return Json(entry.Item.DeepClone());
                }
            }
        }

        // {item}/assign replaces {item}/assignments
        if (method == HttpMethod.Post && parts.Length >= 3 && parts[^1] == "assign")
        {
            var itemPath = string.Join('/', parts[..^1]);
            var assignments = JsonNode.Parse(body ?? "{}")!.AsObject().Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() ?? new JsonArray();
            var list = Collection(itemPath + "/assignments");
            lock (list)
            {
                list.Clear();
                foreach (var a in assignments.OfType<JsonObject>())
                {
                    var copy = (JsonObject)a.DeepClone();
                    copy["id"] ??= NewId();
                    list.Add(copy);
                }
            }
            return Json(new JsonObject { ["value"] = new JsonArray() });
        }

        // {item}/{relation}/$ref (add) and {item}/{relation}/{id}/$ref (remove)
        if (parts.Length >= 3 && parts[^1] == "$ref")
        {
            if (method == HttpMethod.Post)
            {
                var odataId = JsonNode.Parse(body ?? "{}")?["@odata.id"]?.GetValue<string>() ?? "";
                var refId = odataId.TrimEnd('/').Split('/')[^1];
                var target = FindDirectoryObjectWithType(refId);
                if (target is null) return Error(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Resource '{refId}' does not exist.");
                var list = Collection(string.Join('/', parts[..^1]));
                lock (list)
                {
                    if (list.Any(m => m["id"]?.GetValue<string>() == refId))
                        return Error(HttpStatusCode.BadRequest, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.");
                    list.Add(new JsonObject { ["@odata.type"] = target.Value.ODataType, ["id"] = refId });
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == HttpMethod.Delete && parts.Length >= 4)
            {
                var list = Collection(string.Join('/', parts[..^2]));
                lock (list) list.RemoveAll(m => m["id"]?.GetValue<string>() == parts[^2]);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        return null;
    }
}
