using System.Net;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Restore;

namespace Mbr.Catalog.Azure;

/// <summary>Writes ARM objects back with PUT to their resource ID (ARM has no POST-to-collection create).</summary>
public class ArmPutCustomizer(string type, string apiVersion) : IRestoreCustomizer
{
    public string Type => type;
    protected string ApiVersion => apiVersion;

    /// <summary>Request body for the PUT: the object without its identity fields and anything only this tool stores.</summary>
    protected virtual JsonObject ArmBody(JsonObject body)
    {
        var b = (JsonObject)body.DeepClone();
        foreach (var p in new[] { "id", "name", "type" }) b.Remove(p);
        return b;
    }

    public async Task<string?> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct)
    {
        var id = body["id"]?.ToString() ?? throw new InvalidOperationException("The snapshot has no resource ID.");
        await EnsureSubscriptionAsync(ctx, id, ct);
        return await PutAsync(ctx, id, body, created: true, ct);
    }

    public async Task<bool> UpdateAsync(IRestoreContext ctx, string targetId, JsonObject body, CancellationToken ct)
    {
        await PutAsync(ctx, targetId, body, created: false, ct);
        return true;
    }

    public virtual async Task<bool> DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct)
    {
        await Arm.SendAsync(ctx.Session.Arm, HttpMethod.Delete, Arm.Url(targetId, ApiVersion), null, ct);
        return true;
    }

    protected virtual async Task<string> PutAsync(IRestoreContext ctx, string id, JsonObject body, bool created, CancellationToken ct)
    {
        await Arm.SendAsync(ctx.Session.Arm, HttpMethod.Put, Arm.Url(id, ApiVersion), ArmBody(body), ct);
        return id;
    }

    /// <summary>Azure objects live in a subscription; they can only go back into one this tenant still has.</summary>
    protected static async Task EnsureSubscriptionAsync(IRestoreContext ctx, string id, CancellationToken ct)
    {
        if (Arm.SubscriptionOf(id) is not { } sub) return;
        var subs = await Arm.SubscriptionsAsync(ctx.Session, ct);
        if (!subs.Any(s => s["id"]!.ToString().Equals(sub, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Subscription {sub.Split('/')[^1]} isn't available in the target tenant (Azure objects are restored into the subscription they came from).");
    }

    public static IEnumerable<IRestoreCustomizer> All() =>
    [
        new ManagementGroupCustomizer(), new ResourceGroupCustomizer(), new ArmPutCustomizer("azure.roleDefinition", Arm.AuthorizationApi),
        new RoleAssignmentCustomizer(), new ArmPutCustomizer("azure.policyDefinition", Arm.PolicyApi),
        new ArmPutCustomizer("azure.policySetDefinition", Arm.PolicyApi), new PolicyAssignmentCustomizer(),
    ];
}

public sealed class ManagementGroupCustomizer() : ArmPutCustomizer("azure.managementGroup", Arm.ManagementGroupsApi), IRestoreCustomizer
{
    public bool CanCreate(JsonObject content, out string? reason)
    {
        reason = content["properties"]?["details"]?["parent"]?["id"] is null ? "The tenant root group can't be recreated." : null;
        return reason is null;
    }

    protected override JsonObject ArmBody(JsonObject body) => new()
    {
        ["properties"] = new JsonObject
        {
            ["displayName"] = body["properties"]?["displayName"]?.DeepClone(),
            ["details"] = body["properties"]?["details"]?["parent"]?["id"] is { } parent
                ? new JsonObject { ["parent"] = new JsonObject { ["id"] = parent.DeepClone() } } : null,
        },
    };

    protected override async Task<string> PutAsync(IRestoreContext ctx, string id, JsonObject body, bool created, CancellationToken ct)
    {
        await Arm.SendAsync(ctx.Session.Arm, HttpMethod.Put, Arm.Url(id, ApiVersion), ArmBody(body), ct);
        // Put subscriptions back in this group (moving them from wherever they are now).
        foreach (var child in body["properties"]?["children"] as JsonArray ?? [])
        {
            if (child?["id"]?.ToString() is not { } cid || !cid.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await Arm.SendAsync(ctx.Session.Arm, HttpMethod.Put, Arm.Url($"{id}/subscriptions/{cid.Split('/')[^1]}", ApiVersion), null, ct);
            }
            catch (ApiException e)
            {
                await ctx.WarnAsync($"Subscription {child["displayName"] ?? cid} couldn't be moved into this group: {e.Message}");
            }
        }
        return id;
    }

    public override Task<bool> DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct) =>
        throw new InvalidOperationException("Management groups are never deleted by a restore; remove it in the Azure portal if it's no longer needed.");
}

public sealed class ResourceGroupCustomizer() : ArmPutCustomizer("azure.resourceGroup", Arm.ResourcesApi), IRestoreCustomizer
{
    public bool CanCreate(JsonObject content, out string? reason)
    {
        reason = content["managedBy"] is JsonValue m ? $"This group is managed by {m} and is recreated by that service; restore the service instead." : null;
        return reason is null;
    }

    protected override JsonObject ArmBody(JsonObject body)
    {
        var b = new JsonObject { ["location"] = body["location"]?.DeepClone() };
        if (body["tags"] is JsonObject tags) b["tags"] = tags.DeepClone();
        if (body["managedBy"] is { } mb) b["managedBy"] = mb.DeepClone();
        return b;
    }

    public async Task AfterWriteAsync(IRestoreContext ctx, string targetId, JsonObject content, bool created, CancellationToken ct)
    {
        if (content["template"] is not JsonObject template || template["resources"] is not JsonArray { Count: > 0 } resources) return;
        var name = $"mbr-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var url = Arm.Url($"{targetId}/providers/Microsoft.Resources/deployments/{name}", Arm.ResourcesApi);
        var deployment = await Arm.SendAsync(ctx.Session.Arm, HttpMethod.Put, url, new JsonObject
        {
            ["properties"] = new JsonObject { ["mode"] = "Incremental", ["template"] = template.DeepClone(), ["parameters"] = new JsonObject() },
        }, ct);
        var state = deployment?["properties"]?["provisioningState"]?.ToString();
        if (state is "Failed" or "Canceled")
        {
            var reason = deployment?["properties"]?["error"]?["message"]?.ToString();
            var details = await FailedOperationsAsync(ctx, targetId, name, ct);
            throw new InvalidOperationException($"Deployment {name} {state!.ToLowerInvariant()}: {reason}{(details.Length > 0 ? " " + details : "")}");
        }
        await ctx.WarnAsync($"{resources.Count} resource(s) redeployed with deployment {name}. Data inside them (blobs, databases, VM disks) isn't restored by a template.");
    }

    private static async Task<string> FailedOperationsAsync(IRestoreContext ctx, string rg, string name, CancellationToken ct)
    {
        try
        {
            var failed = new List<string>();
            await foreach (var op in ctx.Session.Arm.ListAsync(Arm.Url($"{rg}/providers/Microsoft.Resources/deployments/{name}/operations", Arm.ResourcesApi), ct))
                if (op["properties"]?["provisioningState"]?.ToString() == "Failed")
                    failed.Add($"{op["properties"]?["targetResource"]?["resourceName"]}: {op["properties"]?["statusMessage"]?["error"]?["message"] ?? op["properties"]?["statusMessage"]}");
            return string.Join("; ", failed.Take(5));
        }
        catch (ApiException) { return ""; }
    }

    public override Task<bool> DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct) =>
        throw new InvalidOperationException("Resource groups are never deleted by a restore; delete it in Azure if it's no longer needed.");
}

public sealed class RoleAssignmentCustomizer() : ArmPutCustomizer("azure.roleAssignment", Arm.AuthorizationApi)
{
    protected override JsonObject ArmBody(JsonObject body)
    {
        var p = body["properties"] as JsonObject ?? new JsonObject();
        var props = new JsonObject();
        foreach (var k in new[] { "roleDefinitionId", "principalId", "principalType", "description", "condition", "conditionVersion", "delegatedManagedIdentityResourceId" })
            if (p[k] is { } v) props[k] = v.DeepClone();
        return new JsonObject { ["properties"] = props };
    }

    protected override async Task<string> PutAsync(IRestoreContext ctx, string id, JsonObject body, bool created, CancellationToken ct)
    {
        var armBody = ArmBody(body);
        if (armBody["properties"]?["principalId"] is null)
            throw new InvalidOperationException("The principal this role was assigned to no longer exists in the directory.");
        var arm = ctx.Session.Arm;
        if (!created && await arm.TryGetAsync(Arm.Url(id, ApiVersion), ct) is { } live &&
            !string.Equals(live["properties"]?["principalId"]?.ToString(), armBody["properties"]!["principalId"]!.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            // Role assignments can't change principal in place (the old one was deleted and recreated with a new ID): replace it.
            await Arm.SendAsync(arm, HttpMethod.Delete, Arm.Url(id, ApiVersion), null, ct);
        }
        try
        {
            await Arm.SendAsync(arm, HttpMethod.Put, Arm.Url(id, ApiVersion), armBody, ct);
            return id;
        }
        catch (ApiException e) when (e.ErrorCode == "RoleAssignmentExists")
        {
            // The same role is already assigned to the same principal at this scope under another ID: that one counts.
            var scope = id[..id.IndexOf("/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase)];
            await foreach (var ra in arm.ListAsync(Arm.Url($"{scope}/providers/Microsoft.Authorization/roleAssignments", ApiVersion, "$filter=atScope()"), ct))
                if (string.Equals(ra["properties"]?["principalId"]?.ToString(), armBody["properties"]!["principalId"]!.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Arm.LastSegment(ra["properties"]?["roleDefinitionId"]?.ToString() ?? ""), Arm.LastSegment(armBody["properties"]!["roleDefinitionId"]!.ToString()), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ra["properties"]?["scope"]?.ToString(), scope, StringComparison.OrdinalIgnoreCase))
                    return ra["id"]!.ToString();
            throw;
        }
    }
}

public sealed class PolicyAssignmentCustomizer() : ArmPutCustomizer("azure.policyAssignment", Arm.PolicyApi)
{
    protected override JsonObject ArmBody(JsonObject body)
    {
        var b = base.ArmBody(body);
        if (b["properties"] is JsonObject p) p.Remove("scope");
        if (b["identity"] is JsonObject { } identity && identity["type"]?.ToString() is "None" or null) b.Remove("identity");
        return b;
    }

    protected override async Task<string> PutAsync(IRestoreContext ctx, string id, JsonObject body, bool created, CancellationToken ct)
    {
        await base.PutAsync(ctx, id, body, created, ct);
        if (created && body["identity"]?["type"]?.ToString() is { } t && t.Contains("SystemAssigned"))
            await ctx.WarnAsync("Recreated with a new managed identity: grant it the roles its remediation tasks need again.");
        return id;
    }
}
