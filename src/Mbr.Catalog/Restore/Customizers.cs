using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Restore;

namespace Mbr.Catalog.Restore;

/// <summary>Customizers for types whose Graph write semantics need special handling.</summary>
public static class RestoreCustomizers
{
    public static IEnumerable<IRestoreCustomizer> All() =>
    [
        new ConditionalAccessCustomizer(), new UserCustomizer(), new GroupCustomizer(), new RoleEligibilityCustomizer(),
        new RoleAssignmentCustomizer(), new EndpointSecurityIntentCustomizer(), new AdminTemplateCustomizer(),
        new MobileAppCustomizer(), new EnrollmentConfigurationCustomizer(), new ServicePrincipalCustomizer(),
        new ApplicationCustomizer(),
    ];
}

/// <summary>Lets the executor skip a child collection for some objects (e.g. members of dynamic groups).</summary>
public interface IChildFilter
{
    bool SkipChild(JsonObject content, string childName);
}

public sealed class ConditionalAccessCustomizer : IRestoreCustomizer
{
    public string Type => "entra.conditionalAccessPolicy";

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        // Graph returns the full authentication strength object but only accepts a reference by ID.
        if (body["grantControls"] is JsonObject gc && gc["authenticationStrength"] is JsonObject strength)
            gc["authenticationStrength"] = strength["id"] is { } id ? new JsonObject { ["id"] = id.DeepClone() } : null;
    }
}

public sealed class UserCustomizer : IRestoreCustomizer
{
    public string Type => "entra.user";

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        if (!isCreate) return;
        body["passwordProfile"] = new JsonObject
        {
            ["forceChangePasswordNextSignIn"] = true,
            ["password"] = RandomPassword(),
        };
        _ = ctx.WarnAsync($"{body["userPrincipalName"]} was recreated with a random password; reset it before the user signs in.");
    }

    public async Task AfterWriteAsync(IRestoreContext ctx, string targetId, JsonObject content, bool created, CancellationToken ct)
    {
        if (!created || content["assignedLicenses"] is not JsonArray { Count: > 0 } licenses) return;
        try
        {
            var add = new JsonArray(licenses.OfType<JsonObject>().Select(l => (JsonNode)new JsonObject
            {
                ["skuId"] = l["skuId"]?.DeepClone(), ["disabledPlans"] = l["disabledPlans"]?.DeepClone() ?? new JsonArray(),
            }).ToArray());
            await ctx.Session.Graph.SendAsync(HttpMethod.Post, $"/v1.0/users/{targetId}/assignLicense",
                new JsonObject { ["addLicenses"] = add, ["removeLicenses"] = new JsonArray() }, ct: ct);
        }
        catch (ApiException e)
        {
            await ctx.WarnAsync($"Licenses for {content["userPrincipalName"]} could not be assigned: {e.Message}");
        }
    }

    private static string RandomPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*?";
        return "Mbr-" + new string(RandomNumberGenerator.GetItems<char>(chars, 20)) + "9a!";
    }
}

public sealed class GroupCustomizer : IRestoreCustomizer, IChildFilter
{
    public string Type => "entra.group";

    public bool SkipChild(JsonObject content, string childName) =>
        childName == "members" && content["membershipRule"] is JsonValue v && !string.IsNullOrEmpty(v.ToString());

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        if (body["membershipRule"] is null || string.IsNullOrEmpty(body["membershipRule"]?.ToString()))
        {
            body.Remove("membershipRule");
            body.Remove("membershipRuleProcessingState");
        }
        body.Remove("resourceProvisioningOptions"); // only settable when creating Teams-enabled groups through the Teams API
    }
}

public sealed class RoleAssignmentCustomizer : IRestoreCustomizer
{
    public string Type => "entra.directoryRoleAssignment";

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        foreach (var k in body.Select(kv => kv.Key).ToList())
            if (k is not ("principalId" or "roleDefinitionId" or "directoryScopeId" or "appScopeId")) body.Remove(k);
        if (body["appScopeId"] is null) body.Remove("appScopeId");
    }
}

public sealed class RoleEligibilityCustomizer : IRestoreCustomizer
{
    public string Type => "entra.roleEligibilitySchedule";

    public async Task<string?> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct)
    {
        var schedule = body["scheduleInfo"]?.DeepClone() as JsonObject ?? new JsonObject();
        schedule["startDateTime"] = DateTimeOffset.UtcNow.ToString("o");
        if (schedule["expiration"] is JsonObject exp && exp["endDateTime"] is JsonValue end &&
            DateTimeOffset.TryParse(end.ToString(), out var e) && e < DateTimeOffset.UtcNow.AddDays(1))
        {
            schedule["expiration"] = new JsonObject { ["type"] = "noExpiration" };
            await ctx.WarnAsync("The saved eligibility had already expired; it was restored without an end date. Adjust it in PIM if needed.");
        }
        var request = new JsonObject
        {
            ["action"] = "adminAssign",
            ["principalId"] = body["principalId"]?.DeepClone(),
            ["roleDefinitionId"] = body["roleDefinitionId"]?.DeepClone(),
            ["directoryScopeId"] = body["directoryScopeId"]?.DeepClone() ?? "/",
            ["justification"] = "Restored by MBR",
            ["scheduleInfo"] = schedule,
        };
        var resp = await ctx.Session.Graph.SendAsync(HttpMethod.Post, "/v1.0/roleManagement/directory/roleEligibilityScheduleRequests", request, ct: ct);
        return resp?["targetScheduleId"]?.GetValue<string>() ?? resp?["id"]?.GetValue<string>();
    }
}

public sealed class EndpointSecurityIntentCustomizer : IRestoreCustomizer
{
    public string Type => "intune.endpointSecurityIntent";

    private static JsonArray Settings(JsonObject content) =>
        new(((content["settings"] as JsonArray) ?? new JsonArray()).OfType<JsonObject>().Select(s =>
        {
            var c = (JsonObject)s.DeepClone();
            c.Remove("id");
            return (JsonNode)c;
        }).ToArray());

    public async Task<string?> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct)
    {
        var templateId = body["templateId"]?.GetValue<string>() ?? throw new InvalidOperationException("The saved intent has no templateId.");
        var request = new JsonObject
        {
            ["displayName"] = body["displayName"]?.DeepClone(),
            ["description"] = body["description"]?.DeepClone(),
            ["roleScopeTagIds"] = body["roleScopeTagIds"]?.DeepClone(),
            ["settingsDelta"] = Settings(body),
        };
        var resp = await ctx.Session.Graph.SendAsync(HttpMethod.Post, $"/beta/deviceManagement/templates/{templateId}/createInstance", request, ct: ct);
        return resp?["id"]?.GetValue<string>();
    }

    public async Task<bool> UpdateAsync(IRestoreContext ctx, string targetId, JsonObject body, CancellationToken ct)
    {
        await ctx.Session.Graph.SendAsync(HttpMethod.Patch, $"/beta/deviceManagement/intents/{targetId}", new JsonObject
        {
            ["displayName"] = body["displayName"]?.DeepClone(),
            ["description"] = body["description"]?.DeepClone(),
            ["roleScopeTagIds"] = body["roleScopeTagIds"]?.DeepClone(),
        }, ct: ct);
        await ctx.Session.Graph.SendAsync(HttpMethod.Post, $"/beta/deviceManagement/intents/{targetId}/updateSettings",
            new JsonObject { ["settings"] = Settings(body) }, ct: ct);
        return true;
    }

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate) { }
}

/// <summary>Administrative templates: the configuration is created first, then its definition values are written.</summary>
public sealed class AdminTemplateCustomizer : IRestoreCustomizer
{
    public string Type => "intune.groupPolicyConfiguration";
    private const string Base = "https://graph.microsoft.com/beta/deviceManagement/groupPolicyDefinitions";

    public async Task AfterWriteAsync(IRestoreContext ctx, string targetId, JsonObject content, bool created, CancellationToken ct)
    {
        var values = (content["definitionValues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var deleted = new JsonArray();
        if (!created)
            await foreach (var existing in ctx.Session.Graph.ListAsync($"/beta/deviceManagement/groupPolicyConfigurations/{targetId}/definitionValues", ct))
                deleted.Add(existing["id"]!.GetValue<string>());

        var added = new JsonArray();
        foreach (var v in values)
        {
            var defId = v["definition"]?["id"]?.GetValue<string>();
            if (defId is null) continue;
            var presentations = new JsonArray();
            foreach (var p in (v["presentationValues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var pv = (JsonObject)p.DeepClone();
                var presId = pv["presentation"]?["id"]?.GetValue<string>();
                pv.Remove("presentation");
                pv.Remove("id");
                pv.Remove("lastModifiedDateTime");
                pv.Remove("createdDateTime");
                if (presId != null) pv["presentation@odata.bind"] = $"{Base}('{defId}')/presentations('{presId}')";
                presentations.Add(pv);
            }
            added.Add(new JsonObject
            {
                ["enabled"] = v["enabled"]?.DeepClone(),
                ["definition@odata.bind"] = $"{Base}('{defId}')",
                ["presentationValues"] = presentations,
            });
        }
        await ctx.Session.Graph.SendAsync(HttpMethod.Post, $"/beta/deviceManagement/groupPolicyConfigurations/{targetId}/updateDefinitionValues",
            new JsonObject { ["added"] = added, ["updated"] = new JsonArray(), ["deletedIds"] = deleted }, ct: ct);
    }
}

public sealed class MobileAppCustomizer : IRestoreCustomizer
{
    public string Type => "intune.mobileApp";

    private static readonly HashSet<string> Recreatable = new(StringComparer.OrdinalIgnoreCase)
    {
        "#microsoft.graph.webApp", "#microsoft.graph.iosStoreApp", "#microsoft.graph.androidStoreApp", "#microsoft.graph.managedIOSStoreApp",
        "#microsoft.graph.managedAndroidStoreApp", "#microsoft.graph.winGetApp", "#microsoft.graph.windowsWebApp", "#microsoft.graph.macOSOfficeSuiteApp",
        "#microsoft.graph.officeSuiteApp", "#microsoft.graph.windowsMicrosoftEdgeApp", "#microsoft.graph.macOSMicrosoftEdgeApp",
        "#microsoft.graph.macOSMicrosoftDefenderApp", "#microsoft.graph.microsoftStoreForBusinessApp",
    };

    public bool CanCreate(JsonObject content, out string? reason)
    {
        var type = content["@odata.type"]?.GetValue<string>() ?? "";
        if (Recreatable.Contains(type)) { reason = null; return true; }
        reason = $"{type.Replace("#microsoft.graph.", "")} apps include a binary that can't be downloaded from Intune. Re-upload the app, then run the restore again to bring back its assignments.";
        return false;
    }
}

public sealed class EnrollmentConfigurationCustomizer : IRestoreCustomizer
{
    public string Type => "intune.enrollmentConfiguration";

    public bool CanCreate(JsonObject content, out string? reason)
    {
        if (content["priority"] is JsonValue p && p.ToString() == "0")
        {
            reason = "Default enrollment configurations exist in every tenant and are only updated.";
            return false;
        }
        reason = null;
        return true;
    }
}

public sealed class ServicePrincipalCustomizer : IRestoreCustomizer
{
    public string Type => "entra.servicePrincipal";
    private static readonly HashSet<string> MicrosoftTenants = ["f8cdef31-a31e-4b4a-93e4-5f571e91255a", "72f988bf-86f1-41af-91ab-2d7cd011db47"];

    public bool CanCreate(JsonObject content, out string? reason)
    {
        if (MicrosoftTenants.Contains(content["appOwnerOrganizationId"]?.ToString() ?? ""))
        {
            reason = "First-party Microsoft service principals are created by Microsoft when needed.";
            return false;
        }
        reason = null;
        return true;
    }

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        var keep = new HashSet<string> { "accountEnabled", "appRoleAssignmentRequired", "tags", "notes", "preferredSingleSignOnMode", "loginUrl", "notificationEmailAddresses", "description" };
        if (isCreate) keep.Add("appId");
        foreach (var k in body.Select(kv => kv.Key).ToList()) if (!keep.Contains(k)) body.Remove(k);
    }
}

public sealed class ApplicationCustomizer : IRestoreCustomizer
{
    public string Type => "entra.application";

    public void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate)
    {
        // Server-managed or tenant-specific properties that can't be written.
        foreach (var k in new[] { "appId", "publisherDomain", "identifierUris", "certification", "verifiedPublisher", "servicePrincipalLockConfiguration" })
            body.Remove(k);
        if (isCreate) _ = ctx.WarnAsync($"App '{body["displayName"]}' was recreated with a new client ID; add credentials and update any configuration that used the old ID.");
    }
}
