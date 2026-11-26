using System.Text.Json.Nodes;

namespace Mbr.Tests.Fakes;

/// <summary>Seeds a fake Graph with a small but realistic Entra + Intune configuration.</summary>
public static class FakeTenantData
{
    public record Seeded(string UserId, string GroupId, string LocationId, string CaPolicyId, string DeviceConfigId, string SettingsPolicyId, string FilterId);

    public static Seeded Seed(FakeGraph g)
    {
        var user = g.Add("users", new JsonObject
        {
            ["userPrincipalName"] = "adele@contoso.onmicrosoft.com", ["displayName"] = "Adele Vance", ["accountEnabled"] = true,
            ["mailNickname"] = "adele", ["usageLocation"] = "US",
        });
        var userId = user["id"]!.GetValue<string>();
        var group = g.Add("groups", new JsonObject
        {
            ["displayName"] = "All Staff", ["mailEnabled"] = false, ["securityEnabled"] = true, ["mailNickname"] = "allstaff",
            ["groupTypes"] = new JsonArray(),
        });
        var groupId = group["id"]!.GetValue<string>();
        g.Add($"groups/{groupId}/members", new JsonObject { ["@odata.type"] = "#microsoft.graph.user", ["id"] = userId });

        var loc = g.Add("identity/conditionalAccess/namedLocations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = "Head office", ["isTrusted"] = true,
            ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "203.0.113.0/24" }),
            ["createdDateTime"] = "2025-01-01T00:00:00Z", ["modifiedDateTime"] = "2025-01-02T00:00:00Z",
        });
        var locId = loc["id"]!.GetValue<string>();
        var ca = g.Add("identity/conditionalAccess/policies", new JsonObject
        {
            ["displayName"] = "Require MFA for all staff", ["state"] = "enabled",
            ["createdDateTime"] = "2025-01-01T00:00:00Z",
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject
                {
                    ["includeUsers"] = new JsonArray(), ["excludeUsers"] = new JsonArray(userId),
                    ["includeGroups"] = new JsonArray(groupId), ["excludeGroups"] = new JsonArray(),
                },
                ["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("All"), ["excludeLocations"] = new JsonArray(locId) },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
            },
            ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") },
        });

        var filter = g.Add("deviceManagement/assignmentFilters", new JsonObject
        {
            ["displayName"] = "Corporate Windows", ["platform"] = "windows10AndLater", ["rule"] = "(device.deviceOwnership -eq \"Corporate\")",
        });
        var filterId = filter["id"]!.GetValue<string>();
        var dc = g.Add("deviceManagement/deviceConfigurations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.windows10GeneralConfiguration", ["displayName"] = "Windows baseline",
            ["passwordRequired"] = true, ["passwordMinimumLength"] = 12, ["version"] = 3, ["roleScopeTagIds"] = new JsonArray("0"),
            ["lastModifiedDateTime"] = "2025-03-01T00:00:00Z",
        });
        var dcId = dc["id"]!.GetValue<string>();
        g.Add($"deviceManagement/deviceConfigurations/{dcId}/assignments", new JsonObject
        {
            ["id"] = $"{dcId}_{groupId}",
            ["target"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget", ["groupId"] = groupId,
                ["deviceAndAppManagementAssignmentFilterId"] = filterId, ["deviceAndAppManagementAssignmentFilterType"] = "include",
            },
        });
        var sc = g.Add("deviceManagement/configurationPolicies", new JsonObject
        {
            ["name"] = "Defender settings", ["platforms"] = "windows10", ["technologies"] = "mdm", ["settingCount"] = 1,
            ["roleScopeTagIds"] = new JsonArray("0"),
        });
        var scId = sc["id"]!.GetValue<string>();
        g.Add($"deviceManagement/configurationPolicies/{scId}/settings", new JsonObject
        {
            ["id"] = "0",
            ["settingInstance"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationChoiceSettingInstance",
                ["settingDefinitionId"] = "device_vendor_msft_policy_config_defender_allowrealtimemonitoring",
                ["choiceSettingValue"] = new JsonObject { ["value"] = "device_vendor_msft_policy_config_defender_allowrealtimemonitoring_1" },
            },
        });
        g.Collection($"deviceManagement/configurationPolicies/{scId}/assignments");

        // Singletons
        var authz = new JsonObject { ["id"] = "authorizationPolicy", ["allowInvitesFrom"] = "adminsAndGuestInviters", ["blockMsolPowerShell"] = true };
        g.On(HttpMethod.Get, "/v1.0/policies/authorizationPolicy", (_, _) => FakeGraph.Json(authz));
        g.On(HttpMethod.Patch, "/v1.0/policies/authorizationPolicy", (_, body) =>
        {
            foreach (var (k, v) in JsonNode.Parse(body!)!.AsObject()) authz[k] = v?.DeepClone();
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        });

        return new Seeded(userId, groupId, locId, ca["id"]!.GetValue<string>(), dcId, scId, filterId);
    }
}
