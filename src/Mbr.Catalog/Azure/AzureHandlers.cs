using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;

namespace Mbr.Catalog.Azure;

/// <summary>All Azure (ARM) resource handlers.</summary>
public static class AzureHandlers
{
    public static IEnumerable<IResourceHandler> All() =>
    [
        new ManagementGroupHandler(), new SubscriptionHandler(), new ResourceGroupHandler(), new RoleDefinitionHandler(),
        new RoleAssignmentHandler(), new PolicyDefinitionHandler(), new PolicySetDefinitionHandler(), new PolicyAssignmentHandler(),
    ];

    internal static ResourceDefinition Def(string type, string displayName, string listPath, Action<ResourceDefinition>? configure = null)
    {
        var d = new ResourceDefinition
        {
            Type = type, Workload = "azure", DisplayName = displayName, Api = "arm", ListPath = listPath,
            // ARM objects are identified by their resource ID; names are only unique within a scope, so never match on them.
            IdProperty = "id", NameProperty = "displayName", MatchKeys = ["id"],
        };
        configure?.Invoke(d);
        return d;
    }
}

/// <summary>Base for ARM handlers: list raw objects, clean them, optionally enrich each one (templates, children).</summary>
public abstract class ArmHandler : IResourceHandler
{
    public abstract ResourceDefinition Definition { get; }
    protected abstract string ApiVersion { get; }
    protected abstract IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, CancellationToken ct);
    protected virtual JsonObject Normalize(JsonObject raw) => Arm.Clean(raw);
    protected virtual string ItemUrl(string id) => Arm.Url(id, ApiVersion);
    protected virtual Task<JsonObject> EnrichAsync(ITenantSession s, JsonObject content, CancellationToken ct) => Task.FromResult(content);
    protected virtual Task<string> DisplayNameAsync(ITenantSession s, JsonObject content, CancellationToken ct) =>
        Task.FromResult(content["properties"]?["displayName"]?.ToString() ?? content["name"]?.ToString() ?? content["id"]!.ToString());
    protected virtual int Parallelism => 4;

    public async IAsyncEnumerable<CapturedItem> CaptureAsync(ITenantSession s, [EnumeratorCancellation] CancellationToken ct)
    {
        var raws = new List<JsonObject>();
        await foreach (var r in ListRawAsync(s, ct)) raws.Add(r);
        var results = new CapturedItem?[raws.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, raws.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) => results[i] = await CaptureOneAsync(s, Normalize(raws[i]), token));
        foreach (var r in results)
            if (r != null) yield return r;
    }

    public async Task<CapturedItem?> CaptureByIdAsync(ITenantSession s, string id, CancellationToken ct) =>
        await s.Arm.TryGetAsync(ItemUrl(id), ct) is JsonObject raw ? await CaptureOneAsync(s, Normalize(raw), ct) : null;

    public async Task<List<CapturedItem>> ListAsync(ITenantSession s, CancellationToken ct)
    {
        var list = new List<CapturedItem>();
        await foreach (var raw in ListRawAsync(s, ct))
        {
            var c = Normalize(raw);
            list.Add(new CapturedItem(Definition.Type, c["id"]!.ToString(), await DisplayNameAsync(s, c, ct), c));
        }
        return list;
    }

    private async Task<CapturedItem?> CaptureOneAsync(ITenantSession s, JsonObject content, CancellationToken ct)
    {
        var id = content["id"]!.ToString();
        try
        {
            content = await EnrichAsync(s, content, ct);
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // removed meanwhile
        }
        catch (ApiException e)
        {
            await s.ReportIssueAsync(new CaptureIssue(Definition.Type, id, IssueSeverity.Warning, $"Could not read {Arm.LastSegment(id)} completely: {e.Message}"));
        }
        return new CapturedItem(Definition.Type, id, await DisplayNameAsync(s, content, ct), content);
    }

    /// <summary>Lists a collection at every scope, keeping objects that belong to that scope (listings also return inherited ones).</summary>
    protected static async IAsyncEnumerable<JsonObject> AtScopesAsync(ITenantSession s, string provider, string api, string? subscriptionFilter,
        string? managementGroupFilter, Func<JsonObject, string, bool> belongs, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, isMg) in await Arm.ScopesAsync(s, ct))
        {
            var items = new List<JsonObject>();
            try
            {
                await foreach (var o in s.Arm.ListAsync(Arm.Url($"{scope}/providers/{provider}", api, isMg ? managementGroupFilter : subscriptionFilter), ct))
                    items.Add(o);
            }
            catch (ApiException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                await s.ReportIssueAsync(new CaptureIssue(provider, scope, IssueSeverity.Warning, $"Couldn't list {provider.Split('/')[^1]} at {scope}: {e.Message}"));
                continue;
            }
            foreach (var o in items)
                if (o["id"]?.ToString() is { } id && belongs(o, scope) && seen.Add(id))
                    yield return o;
        }
    }

    protected static string? Scope(JsonObject o) => o["properties"]?["scope"]?.ToString();
}

// ------------------------------------------------------------------------------------------------ management groups

public sealed class ManagementGroupHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.managementGroup", "Management groups",
        "providers/Microsoft.Management/managementGroups", d =>
        {
            d.References = [new ReferenceDefinition { Path = "properties.details.parent.id", Type = "azure.managementGroup", KeepUnknown = true }];
            d.Notes = "Includes the hierarchy and which subscriptions sit in each group.";
        });

    protected override string ApiVersion => Arm.ManagementGroupsApi;
    protected override string ItemUrl(string id) => Arm.Url(id, ApiVersion, "$expand=children");

    protected override async IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var mg in await Arm.ManagementGroupsAsync(s, ct)) yield return mg;
    }

    protected override JsonObject Normalize(JsonObject raw)
    {
        var o = Arm.Clean(raw, "properties.details.version", "properties.details.updatedTime", "properties.details.updatedBy", "properties.details.path",
            "properties.details.managementGroupAncestors", "properties.details.managementGroupAncestorsChain", "properties.details.parentNameChain",
            "properties.details.parent.displayName", "properties.details.parent.name");
        if (o["properties"]?["children"] is JsonArray children)
            o["properties"]!["children"] = new JsonArray(children.OfType<JsonObject>()
                .Select(c => (JsonNode)new JsonObject { ["id"] = c["id"]?.DeepClone(), ["type"] = c["type"]?.DeepClone(), ["displayName"] = c["displayName"]?.DeepClone() })
                .OrderBy(c => c["id"]?.ToString(), StringComparer.OrdinalIgnoreCase).ToArray());
        return o;
    }

    protected override async Task<JsonObject> EnrichAsync(ITenantSession s, JsonObject content, CancellationToken ct) =>
        content["properties"]?["children"] is JsonArray
            ? content
            : await s.Arm.GetAsync(ItemUrl(content["id"]!.ToString()), ct) is JsonObject full ? Normalize(full) : content;
}

// ------------------------------------------------------------------------------------------------ subscriptions

public sealed class SubscriptionHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.subscription", "Subscriptions", "subscriptions", d =>
    {
        d.DependsOn = ["azure.managementGroup"];
        d.Restore = new RestoreDefinition { Supported = false, Notes = "Subscriptions are captured for reference; they can't be recreated by a restore." };
    });

    protected override string ApiVersion => Arm.SubscriptionsApi;

    protected override async IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var sub in await Arm.SubscriptionsAsync(s, ct)) yield return sub;
    }

    protected override JsonObject Normalize(JsonObject raw)
    {
        var o = Arm.Clean(raw, "authorizationSource", "managedByTenants", "tenantId");
        o["id"] = $"/subscriptions/{o["subscriptionId"]}";
        return o;
    }

    protected override Task<string> DisplayNameAsync(ITenantSession s, JsonObject content, CancellationToken ct) =>
        Task.FromResult(content["displayName"]?.ToString() ?? content["subscriptionId"]!.ToString());
}

// ------------------------------------------------------------------------------------------------ resource groups

/// <summary>
/// Resource groups with an ARM template of everything in them (exportTemplate). Restoring redeploys the template (incremental),
/// which brings back deleted resources and resets changed ones.
/// </summary>
public sealed class ResourceGroupHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.resourceGroup", "Resource groups", "subscriptions/{subscriptionId}/resourcegroups", d =>
    {
        d.DependsOn = ["azure.subscription"];
        d.Restore = new RestoreDefinition
        {
            ReadOnly = ["resources"],
            Notes = "Resources are redeployed from the template exported at backup time (incremental: nothing is deleted). Secrets such as passwords " +
                    "and keys are never exported, so resources that need them may fail to deploy; data inside resources (blobs, databases) isn't part of the template.",
        };
        d.Notes = "Captures the group and an exported ARM template of its resources.";
    });

    protected override string ApiVersion => Arm.ResourcesApi;
    protected override int Parallelism => 3;

    protected override async IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var sub in await Arm.SubscriptionsAsync(s, ct))
            await foreach (var rg in s.Arm.ListAsync(Arm.Url($"{sub["id"]}/resourcegroups", ApiVersion), ct))
                yield return rg;
    }

    protected override JsonObject Normalize(JsonObject raw)
    {
        var o = Arm.Clean(raw);
        if (o["properties"] is JsonObject { Count: 0 }) o.Remove("properties");
        return o;
    }

    protected override async Task<string> DisplayNameAsync(ITenantSession s, JsonObject content, CancellationToken ct) =>
        await Arm.DescribeScopeAsync(s, content["id"]!.ToString(), ct);

    protected override async Task<JsonObject> EnrichAsync(ITenantSession s, JsonObject content, CancellationToken ct)
    {
        var id = content["id"]!.ToString();
        var resources = new List<JsonObject>();
        await foreach (var r in s.Arm.ListAsync(Arm.Url($"{id}/resources", ApiVersion), ct))
            resources.Add(new JsonObject { ["id"] = r["id"]?.DeepClone(), ["type"] = r["type"]?.DeepClone(), ["name"] = r["name"]?.DeepClone(), ["location"] = r["location"]?.DeepClone() });
        content["resources"] = new JsonArray(resources.OrderBy(r => r["id"]?.ToString(), StringComparer.OrdinalIgnoreCase).Select(r => (JsonNode)r).ToArray());
        if (resources.Count == 0)
        {
            content["template"] = null;
            return content;
        }

        var export = await Arm.SendAsync(s.Arm, HttpMethod.Post, Arm.Url($"{id}/exportTemplate", ApiVersion),
            new JsonObject { ["resources"] = new JsonArray("*"), ["options"] = "SkipAllParameterization" }, ct);
        content["template"] = export?["template"]?.DeepClone();
        if (export?["error"] is JsonObject err)
        {
            var details = err["details"] is JsonArray d ? string.Join("; ", d.Select(x => x?["message"]?.ToString()).Where(x => x != null).Take(5)) : err["message"]?.ToString();
            await s.ReportIssueAsync(new CaptureIssue(Definition.Type, id, IssueSeverity.Warning,
                $"Some resources in {content["name"]} couldn't be exported to the template and won't be redeployed: {details}"));
        }
        return content;
    }
}

// ------------------------------------------------------------------------------------------------ RBAC

public sealed class RoleDefinitionHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.roleDefinition", "Custom roles (Azure RBAC)",
        "{scope}/providers/Microsoft.Authorization/roleDefinitions", d => d.DependsOn = ["azure.managementGroup"]);

    protected override string ApiVersion => Arm.AuthorizationApi;

    protected override async IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, [EnumeratorCancellation] CancellationToken ct)
    {
        // A custom role assignable in several subscriptions is listed under each of them: keep it once (by its GUID).
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in AtScopesAsync(s, "Microsoft.Authorization/roleDefinitions", ApiVersion, "$filter=type eq 'CustomRole'", "$filter=type eq 'CustomRole'",
                           (o, _) => o["properties"]?["type"]?.ToString() == "CustomRole", ct))
            if (names.Add(r["name"]?.ToString() ?? ""))
                yield return r;
    }

    protected override Task<string> DisplayNameAsync(ITenantSession s, JsonObject content, CancellationToken ct) =>
        Task.FromResult(content["properties"]?["roleName"]?.ToString() ?? content["name"]!.ToString());
}

public sealed class RoleAssignmentHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.roleAssignment", "Role assignments (Azure RBAC)",
        "{scope}/providers/Microsoft.Authorization/roleAssignments", d =>
        {
            d.References =
            [
                new ReferenceDefinition { Path = "properties.principalId", Type = "directoryObject" },
                new ReferenceDefinition { Path = "properties.roleDefinitionId", Type = "azure.roleDefinition", KeepUnknown = true },
                new ReferenceDefinition { Path = "properties.scope", Type = "azure.resourceGroup", KeepUnknown = true },
                new ReferenceDefinition { Path = "properties.scope", Type = "azure.managementGroup", KeepUnknown = true },
            ];
            d.DependsOn = ["azure.resourceGroup", "azure.roleDefinition", "azure.managementGroup", "entra.group", "entra.user", "entra.servicePrincipal"];
            d.Restore = new RestoreDefinition { UpdateMethod = "PUT", ReadOnly = ["properties.scope"] };
            d.Notes = "Assignments at management group, subscription, resource group and resource scope. Inherited assignments are captured at their own scope.";
        });

    protected override string ApiVersion => Arm.AuthorizationApi;

    protected override IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, CancellationToken ct) =>
        AtScopesAsync(s, "Microsoft.Authorization/roleAssignments", ApiVersion, null, "$filter=atScope()",
            (o, scope) => Scope(o) is { } sc && (scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                ? Arm.IsAtOrBelow(sc, scope) : sc.Equals(scope, StringComparison.OrdinalIgnoreCase)), ct);

    private static readonly ConditionalWeakTable<ITenantSession, ConcurrentDictionary<string, Task<string?>>> Names = new();

    protected override async Task<string> DisplayNameAsync(ITenantSession s, JsonObject content, CancellationToken ct)
    {
        var p = content["properties"];
        var role = p?["roleDefinitionId"]?.ToString() is { } rd ? await RoleNameAsync(s, rd, ct) : null;
        var principal = p?["principalId"]?.ToString() is { } pid ? await PrincipalNameAsync(s, pid, ct) : null;
        var scope = p?["scope"]?.ToString() is { } sc ? await Arm.DescribeScopeAsync(s, sc, ct) : "?";
        return $"{role ?? "Role"} for {principal ?? p?["principalId"]?.ToString() ?? "?"} on {scope}";
    }

    private static Task<string?> RoleNameAsync(ITenantSession s, string roleDefinitionId, CancellationToken ct) =>
        Names.GetOrCreateValue(s).GetOrAdd("role:" + Arm.LastSegment(roleDefinitionId).ToLowerInvariant(), async _ =>
        {
            try { return (await s.Arm.TryGetAsync(Arm.Url(roleDefinitionId, Arm.AuthorizationApi), ct))?["properties"]?["roleName"]?.ToString(); }
            catch (ApiException) { return null; }
        });

    private static Task<string?> PrincipalNameAsync(ITenantSession s, string principalId, CancellationToken ct) =>
        Names.GetOrCreateValue(s).GetOrAdd("principal:" + principalId, async _ =>
        {
            try
            {
                var o = await s.Graph.TryGetAsync($"/v1.0/directoryObjects/{Uri.EscapeDataString(principalId)}", ct);
                return o is null ? "a deleted principal" : o["displayName"]?.ToString() ?? o["userPrincipalName"]?.ToString();
            }
            catch (ApiException) { return null; }
        });
}

// ------------------------------------------------------------------------------------------------ Azure Policy

public sealed class PolicyDefinitionHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.policyDefinition", "Policy definitions (custom)",
        "{scope}/providers/Microsoft.Authorization/policyDefinitions", d => d.DependsOn = ["azure.managementGroup"]);

    protected override string ApiVersion => Arm.PolicyApi;

    protected override IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, CancellationToken ct) =>
        AtScopesAsync(s, "Microsoft.Authorization/policyDefinitions", ApiVersion, "$filter=policyType eq 'Custom'", "$filter=policyType eq 'Custom'",
            (o, scope) => o["properties"]?["policyType"]?.ToString() == "Custom" && Arm.IsAtOrBelow(o["id"]!.ToString(), scope), ct);
}

public sealed class PolicySetDefinitionHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.policySetDefinition", "Policy initiatives (custom)",
        "{scope}/providers/Microsoft.Authorization/policySetDefinitions", d =>
        {
            d.References = [new ReferenceDefinition { Path = "properties.policyDefinitions[*].policyDefinitionId", Type = "azure.policyDefinition", KeepUnknown = true }];
            d.DependsOn = ["azure.policyDefinition"];
        });

    protected override string ApiVersion => Arm.PolicyApi;

    protected override IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, CancellationToken ct) =>
        AtScopesAsync(s, "Microsoft.Authorization/policySetDefinitions", ApiVersion, "$filter=policyType eq 'Custom'", "$filter=policyType eq 'Custom'",
            (o, scope) => o["properties"]?["policyType"]?.ToString() == "Custom" && Arm.IsAtOrBelow(o["id"]!.ToString(), scope), ct);
}

public sealed class PolicyAssignmentHandler : ArmHandler
{
    public override ResourceDefinition Definition { get; } = AzureHandlers.Def("azure.policyAssignment", "Policy assignments",
        "{scope}/providers/Microsoft.Authorization/policyAssignments", d =>
        {
            d.References =
            [
                new ReferenceDefinition { Path = "properties.policyDefinitionId", Type = "azure.policyDefinition", KeepUnknown = true },
                new ReferenceDefinition { Path = "properties.policyDefinitionId", Type = "azure.policySetDefinition", KeepUnknown = true },
                new ReferenceDefinition { Path = "properties.scope", Type = "azure.resourceGroup", KeepUnknown = true },
                new ReferenceDefinition { Path = "properties.scope", Type = "azure.managementGroup", KeepUnknown = true },
            ];
            d.DependsOn = ["azure.resourceGroup", "azure.managementGroup", "azure.policyDefinition", "azure.policySetDefinition"];
            d.Restore = new RestoreDefinition { UpdateMethod = "PUT", ReadOnly = ["properties.scope"] };
        });

    protected override string ApiVersion => Arm.PolicyApi;

    protected override IAsyncEnumerable<JsonObject> ListRawAsync(ITenantSession s, CancellationToken ct) =>
        AtScopesAsync(s, "Microsoft.Authorization/policyAssignments", ApiVersion, null, "$filter=atScope()",
            (o, scope) => Scope(o) is { } sc && (scope.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                ? Arm.IsAtOrBelow(sc, scope) : sc.Equals(scope, StringComparison.OrdinalIgnoreCase)), ct);

    protected override JsonObject Normalize(JsonObject raw)
    {
        // The managed identity's principal changes whenever it's recreated; keep only its type (and user-assigned identities).
        var o = Arm.Clean(raw, "identity.principalId", "identity.tenantId");
        if (o["identity"]?["userAssignedIdentities"] is JsonObject uai)
            foreach (var (k, _) in uai.ToList()) uai[k] = new JsonObject();
        return o;
    }
}
