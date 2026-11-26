using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>
/// A small Azure Resource Manager: resources keyed by resource ID, resource groups, management groups (with subscription placement),
/// RBAC and Policy objects, exportTemplate, and template deployments that create the template's resources.
/// Subscriptions themselves stay in the plain "subscriptions" collection.
/// </summary>
public partial class InMemoryGraph
{
    public ConcurrentDictionary<string, JsonObject> ArmResources { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Subscription ID → management group ID it sits in.</summary>
    public ConcurrentDictionary<string, string> SubscriptionParents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deployments { get; } = new();

    /// <summary>Creates or replaces an ARM resource (id, name and type are derived from the ID).</summary>
    public JsonObject ArmPut(string id, JsonObject resource)
    {
        id = "/" + id.Trim('/');
        resource["id"] = id;
        resource["name"] = id[(id.LastIndexOf('/') + 1)..];
        resource["type"] = ArmType(id);
        if (resource["type"]!.ToString() is "Microsoft.Authorization/roleAssignments" or "Microsoft.Authorization/policyAssignments" &&
            resource["properties"] is JsonObject p)
            p["scope"] = id[..id.IndexOf("/providers/Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase)] is { Length: > 0 } sc ? sc : "/";
        ArmResources[id] = resource;
        return resource;
    }

    /// <summary>Deletes a resource and everything below it (deleting a resource group deletes its resources).</summary>
    public void ArmDelete(string id)
    {
        id = "/" + id.Trim('/');
        foreach (var k in ArmResources.Keys.Where(k => k.Equals(id, StringComparison.OrdinalIgnoreCase) || k.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            ArmResources.TryRemove(k, out _);
    }

    public bool ArmExists(string id) => ArmResources.ContainsKey("/" + id.Trim('/'));

    /// <summary>"Microsoft.Storage/storageAccounts" for a storage account ID, "Microsoft.Network/virtualNetworks/subnets" for a subnet.</summary>
    public static string ArmType(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = Array.FindLastIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (last < 0) return parts.Length == 4 ? "Microsoft.Resources/resourceGroups" : parts.Length == 2 ? "Microsoft.Resources/subscriptions" : "unknown";
        var rest = parts[(last + 1)..];
        var types = new List<string> { rest[0] };
        for (var i = 1; i < rest.Length; i += 2) types.Add(rest[i]);
        return string.Join('/', types);
    }

    private static readonly string[] HiddenFromResources = ["Microsoft.Authorization/", "Microsoft.Resources/deployments"];

    private HttpResponseMessage? HandleArm(HttpRequestMessage request, string path, string? body)
    {
        if (!request.RequestUri!.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)) return null;
        var id = "/" + path;
        var parts = path.Split('/');
        var method = request.Method;

        // Subscriptions live in the plain collection.
        if (path.Equals("subscriptions", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts is ["subscriptions", var subId] && method == HttpMethod.Get)
        {
            var sub = Collection("subscriptions").FirstOrDefault(s => s["subscriptionId"]?.ToString() == subId);
            if (sub is null) return Error(HttpStatusCode.NotFound, "SubscriptionNotFound", $"Subscription {subId} not found");
            var copy = (JsonObject)sub.DeepClone();
            copy["id"] = $"/subscriptions/{subId}";
            return Json(copy);
        }

        if (method == HttpMethod.Get)
        {
            if (ArmResources.TryGetValue(id, out var existing))
            {
                var copy = (JsonObject)existing.DeepClone();
                if (copy["type"]?.ToString() == "Microsoft.Management/managementGroups" && request.RequestUri.Query.Contains("children", StringComparison.OrdinalIgnoreCase))
                    copy["properties"]!["children"] = ManagementGroupChildren(id);
                return Json(copy);
            }
            if (!IsArmCollection(parts)) return Error(HttpStatusCode.NotFound, "ResourceNotFound", $"The resource '{id}' was not found.");
            return Json(new JsonObject { ["value"] = new JsonArray(ArmList(id).Select(r => (JsonNode)r.DeepClone()).ToArray()) });
        }

        if (method == HttpMethod.Post && path.EndsWith("/exportTemplate", StringComparison.OrdinalIgnoreCase))
        {
            var rg = id[..^"/exportTemplate".Length];
            if (!ArmResources.ContainsKey(rg)) return Error(HttpStatusCode.NotFound, "ResourceGroupNotFound", $"Resource group {rg} not found");
            return Json(new JsonObject { ["template"] = ExportTemplate(rg) });
        }

        if (method == HttpMethod.Put)
        {
            // Subscription placement: PUT /providers/Microsoft.Management/managementGroups/{mg}/subscriptions/{sub}
            if (parts.Length == 6 && parts[2].Equals("managementGroups", StringComparison.OrdinalIgnoreCase) && parts[4].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                SubscriptionParents[parts[5]] = "/" + string.Join('/', parts[..4]);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            var resource = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body)!.AsObject();
            var rg = ResourceGroupOf(id);
            if (rg != null && !rg.Equals(id, StringComparison.OrdinalIgnoreCase) && !ArmResources.ContainsKey(rg))
                return Error(HttpStatusCode.NotFound, "ResourceGroupNotFound", $"Resource group '{rg.Split('/')[^1]}' could not be found.");
            var type = ArmType(id);
            if (type == "Microsoft.Authorization/roleAssignments" && RoleAssignmentConflict(id, resource) is { } conflict) return conflict;
            if (type == "Microsoft.Resources/deployments") return Deploy(id, rg!, resource);
            var created = !ArmResources.ContainsKey(id);
            if (type == "Microsoft.Resources/resourceGroups") resource["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" };
            if (type == "Microsoft.Management/managementGroups" && resource["properties"] is JsonObject mp)
            {
                mp["details"] ??= new JsonObject();
                mp.Remove("children");
            }
            var stored = ArmPut(id, resource);
            return Json(stored.DeepClone(), created ? HttpStatusCode.Created : HttpStatusCode.OK);
        }

        if (method == HttpMethod.Delete)
        {
            if (!ArmResources.ContainsKey(id)) return new HttpResponseMessage(HttpStatusCode.NoContent);
            ArmDelete(id);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return null;
    }

    /// <summary>A path ending in a type segment (a collection) rather than a name.</summary>
    private static bool IsArmCollection(string[] parts)
    {
        var last = Array.FindLastIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
        return last >= 0 ? (parts.Length - last - 2) % 2 == 1 : parts.Length % 2 == 1;
    }

    private static string? ResourceGroupOf(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 && parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            ? "/" + string.Join('/', parts[..4]) : null;
    }

    private IEnumerable<JsonObject> ArmList(string collection)
    {
        var parts = collection.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var all = ArmResources.Values.OrderBy(r => r["id"]!.ToString(), StringComparer.OrdinalIgnoreCase).ToList();
        // {rg}/resources: top-level resources in the group
        if (parts.Length == 5 && parts[^1].Equals("resources", StringComparison.OrdinalIgnoreCase))
        {
            var rg = "/" + string.Join('/', parts[..4]);
            return all.Where(r => r["id"]!.ToString().StartsWith(rg + "/providers/", StringComparison.OrdinalIgnoreCase) &&
                                  r["id"]!.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 8 &&
                                  !HiddenFromResources.Any(h => r["type"]!.ToString().StartsWith(h, StringComparison.OrdinalIgnoreCase)));
        }
        // {scope}/providers/Microsoft.Authorization/{kind}: everything of that kind at or below the scope
        var auth = Array.FindIndex(parts, p => p.Equals("Microsoft.Authorization", StringComparison.OrdinalIgnoreCase));
        if (auth > 0 && auth == parts.Length - 2)
        {
            var scope = "/" + string.Join('/', parts[..(auth - 1)]);
            var type = "Microsoft.Authorization/" + parts[^1];
            return all.Where(r => r["type"]!.ToString().Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                  (scope == "/" || r["id"]!.ToString().StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)));
        }
        if (parts.Length >= 1 && parts[^1].Equals("operations", StringComparison.OrdinalIgnoreCase)) return [];
        // Plain collection: direct children.
        return all.Where(r =>
        {
            var rid = r["id"]!.ToString();
            var slash = rid.LastIndexOf('/');
            return rid[..slash].Equals(collection, StringComparison.OrdinalIgnoreCase);
        });
    }

    private JsonArray ManagementGroupChildren(string mgId)
    {
        var children = new JsonArray();
        foreach (var mg in ArmResources.Values.Where(r => r["type"]?.ToString() == "Microsoft.Management/managementGroups" &&
                                                          string.Equals(r["properties"]?["details"]?["parent"]?["id"]?.ToString(), mgId, StringComparison.OrdinalIgnoreCase)))
            children.Add(new JsonObject { ["id"] = mg["id"]!.ToString(), ["type"] = "Microsoft.Management/managementGroups", ["displayName"] = mg["properties"]?["displayName"]?.ToString() });
        foreach (var (sub, parent) in SubscriptionParents.Where(p => p.Value.Equals(mgId, StringComparison.OrdinalIgnoreCase)))
            children.Add(new JsonObject
            {
                ["id"] = $"/subscriptions/{sub}", ["type"] = "/subscriptions",
                ["displayName"] = Collection("subscriptions").FirstOrDefault(s => s["subscriptionId"]?.ToString() == sub)?["displayName"]?.ToString(),
            });
        return children;
    }

    private HttpResponseMessage? RoleAssignmentConflict(string id, JsonObject body)
    {
        var principal = body["properties"]?["principalId"]?.ToString();
        var role = body["properties"]?["roleDefinitionId"]?.ToString()?.Split('/')[^1];
        if (ArmResources.TryGetValue(id, out var existing) &&
            !string.Equals(existing["properties"]?["principalId"]?.ToString(), principal, StringComparison.OrdinalIgnoreCase))
            return Error(HttpStatusCode.BadRequest, "RoleAssignmentUpdateNotPermitted", "Tenant ID, application ID, principal ID, and scope are not allowed to be updated.");
        var scope = id[..id.IndexOf("/providers/Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase)];
        var dupe = ArmResources.Values.FirstOrDefault(r => r["type"]?.ToString() == "Microsoft.Authorization/roleAssignments" &&
            !r["id"]!.ToString().Equals(id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r["properties"]?["scope"]?.ToString(), scope, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r["properties"]?["principalId"]?.ToString(), principal, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r["properties"]?["roleDefinitionId"]?.ToString()?.Split('/')[^1], role, StringComparison.OrdinalIgnoreCase));
        return dupe is null ? null : Error(HttpStatusCode.Conflict, "RoleAssignmentExists", "The role assignment already exists.");
    }

    private JsonObject ExportTemplate(string rg)
    {
        var resources = new JsonArray();
        foreach (var r in ArmResources.Values
                     .Where(r => r["id"]!.ToString().StartsWith(rg + "/providers/", StringComparison.OrdinalIgnoreCase) &&
                                 !HiddenFromResources.Any(h => r["type"]!.ToString().StartsWith(h, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(r => r["id"]!.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var parts = r["id"]!.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries)[5..]; // namespace, type, name, child type, child name...
            var names = new List<string>();
            for (var i = 2; i < parts.Length; i += 2) names.Add(parts[i]);
            var t = new JsonObject { ["type"] = r["type"]!.ToString(), ["apiVersion"] = "2023-01-01", ["name"] = string.Join('/', names) };
            foreach (var k in new[] { "location", "tags", "sku", "kind", "properties" })
                if (r[k] is { } v) t[k] = v.DeepClone();
            resources.Add(t);
        }
        return new JsonObject
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", ["contentVersion"] = "1.0.0.0",
            ["parameters"] = new JsonObject(), ["variables"] = new JsonObject(), ["resources"] = resources,
        };
    }

    private HttpResponseMessage Deploy(string id, string rg, JsonObject deployment)
    {
        var template = deployment["properties"]?["template"] as JsonObject;
        foreach (var r in (template?["resources"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = r["type"]!.ToString().Split('/');
            var names = r["name"]!.ToString().Split('/');
            var segs = new List<string> { type[0] };
            for (var i = 0; i < names.Length; i++) { segs.Add(type[i + 1]); segs.Add(names[i]); }
            var rid = $"{rg}/providers/{string.Join('/', segs)}";
            var res = new JsonObject();
            foreach (var k in new[] { "location", "tags", "sku", "kind", "properties" })
                if (r[k] is { } v) res[k] = v.DeepClone();
            ArmPut(rid, res);
        }
        lock (Deployments) Deployments.Add(id);
        var stored = ArmPut(id, new JsonObject
        {
            ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded", ["mode"] = deployment["properties"]?["mode"]?.DeepClone() },
        });
        return Json(stored.DeepClone(), HttpStatusCode.Created);
    }
}
