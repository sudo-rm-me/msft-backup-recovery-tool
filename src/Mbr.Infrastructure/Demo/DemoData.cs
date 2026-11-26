using System.Net;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>Sample configuration for the demo tenant. Later phases add data for their workloads here.</summary>
public static partial class DemoData
{
    public static void Seed(InMemoryGraph g, InMemoryGraph arm)
    {
        SeedEntraAndIntune(g);
        SeedMore(g, arm);
    }

    static partial void SeedMore(InMemoryGraph g, InMemoryGraph arm);

    private static string Id(JsonObject o) => o["id"]!.GetValue<string>();

    private static void SeedEntraAndIntune(InMemoryGraph g)
    {
        var people = new[] { ("adele", "Adele Vance", "Retail Manager"), ("alex", "Alex Wilber", "Marketing Assistant"), ("megan", "Megan Bowen", "Marketing Manager"), ("nestor", "Nestor Wilke", "Director"), ("patti", "Patti Fernandez", "President") };
        var users = people.Select(p => g.Add("users", new JsonObject
        {
            ["userPrincipalName"] = $"{p.Item1}@contoso-demo.onmicrosoft.com", ["displayName"] = p.Item2, ["jobTitle"] = p.Item3,
            ["accountEnabled"] = true, ["mailNickname"] = p.Item1, ["usageLocation"] = "US", ["userType"] = "Member",
        })).ToList();
        var breakGlass = g.Add("users", new JsonObject
        {
            ["userPrincipalName"] = "breakglass@contoso-demo.onmicrosoft.com", ["displayName"] = "Break Glass", ["accountEnabled"] = true, ["mailNickname"] = "breakglass",
        });

        JsonObject Group(string name, string nick, IEnumerable<JsonObject> members)
        {
            var grp = g.Add("groups", new JsonObject
            {
                ["displayName"] = name, ["mailNickname"] = nick, ["mailEnabled"] = false, ["securityEnabled"] = true, ["groupTypes"] = new JsonArray(),
            });
            g.Collection($"groups/{Id(grp)}/owners");
            foreach (var m in members) g.Add($"groups/{Id(grp)}/members", new JsonObject { ["@odata.type"] = "#microsoft.graph.user", ["id"] = Id(m) });
            return grp;
        }
        var allStaff = Group("All Staff", "allstaff", users);
        var marketing = Group("Marketing", "marketing", users.Where(u => u["jobTitle"]!.ToString().Contains("Marketing")));
        var pilot = Group("Intune Pilot Devices", "intunepilot", users.Take(2));

        var office = g.Add("identity/conditionalAccess/namedLocations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = "Head office", ["isTrusted"] = true,
            ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "203.0.113.0/24" }),
        });
        g.Add("identity/conditionalAccess/namedLocations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.countryNamedLocation", ["displayName"] = "Blocked countries",
            ["countriesAndRegions"] = new JsonArray("KP", "IR"), ["includeUnknownCountriesAndRegions"] = false,
        });
        g.Add("identity/conditionalAccess/policies", new JsonObject
        {
            ["displayName"] = "CA001: Require MFA for all users", ["state"] = "enabled",
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All"), ["excludeUsers"] = new JsonArray(Id(breakGlass)), ["includeGroups"] = new JsonArray(), ["excludeGroups"] = new JsonArray() },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["clientAppTypes"] = new JsonArray("all"),
            },
            ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") },
        });
        g.Add("identity/conditionalAccess/policies", new JsonObject
        {
            ["displayName"] = "CA002: Block legacy authentication", ["state"] = "enabled",
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All"), ["excludeUsers"] = new JsonArray(Id(breakGlass)), ["includeGroups"] = new JsonArray(), ["excludeGroups"] = new JsonArray() },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["clientAppTypes"] = new JsonArray("exchangeActiveSync", "other"),
            },
            ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("block") },
        });
        g.Add("identity/conditionalAccess/policies", new JsonObject
        {
            ["displayName"] = "CA003: Marketing outside office needs compliant device", ["state"] = "enabledForReportingButNotEnforced",
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject { ["includeUsers"] = new JsonArray(), ["excludeUsers"] = new JsonArray(), ["includeGroups"] = new JsonArray(Id(marketing)), ["excludeGroups"] = new JsonArray() },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
                ["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("All"), ["excludeLocations"] = new JsonArray(Id(office)) },
            },
            ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("compliantDevice") },
        });

        var authz = new JsonObject { ["id"] = "authorizationPolicy", ["displayName"] = "Authorization Policy", ["allowInvitesFrom"] = "adminsAndGuestInviters", ["blockMsolPowerShell"] = true,
            ["defaultUserRolePermissions"] = new JsonObject { ["allowedToCreateApps"] = false, ["allowedToCreateSecurityGroups"] = false } };
        Singleton(g, "/v1.0/policies/authorizationPolicy", authz);
        Singleton(g, "/v1.0/policies/identitySecurityDefaultsEnforcementPolicy", new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000005", ["displayName"] = "Security Defaults", ["isEnabled"] = false });

        var app = g.Add("applications", new JsonObject { ["displayName"] = "Contoso HR Portal", ["signInAudience"] = "AzureADMyOrg", ["appId"] = "aaaaaaaa-0000-0000-0000-000000000001",
            ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://hr.contoso.com/signin") } });

        // Intune
        var filter = g.Add("deviceManagement/assignmentFilters", new JsonObject { ["displayName"] = "Corporate Windows devices", ["platform"] = "windows10AndLater", ["rule"] = "(device.deviceOwnership -eq \"Corporate\")", ["roleScopeTags"] = new JsonArray("0") });
        var dc = g.Add("deviceManagement/deviceConfigurations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.windows10GeneralConfiguration", ["displayName"] = "Windows - Device restrictions",
            ["passwordRequired"] = true, ["passwordMinimumLength"] = 12, ["passwordRequiredType"] = "alphanumeric", ["screenCaptureBlocked"] = false, ["roleScopeTagIds"] = new JsonArray("0"),
        });
        Assign(g, $"deviceManagement/deviceConfigurations/{Id(dc)}/assignments", Id(allStaff), Id(filter));
        var wifi = g.Add("deviceManagement/deviceConfigurations", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.windowsWifiConfiguration", ["displayName"] = "Windows - Office Wi-Fi", ["ssid"] = "CONTOSO-CORP",
            ["wifiSecurityType"] = "wpa2Personal", ["preSharedKey"] = null, ["connectAutomatically"] = true, ["roleScopeTagIds"] = new JsonArray("0"),
        });
        Assign(g, $"deviceManagement/deviceConfigurations/{Id(wifi)}/assignments", Id(allStaff), null);
        var sc = g.Add("deviceManagement/configurationPolicies", new JsonObject
        {
            ["name"] = "Windows - Defender Antivirus", ["description"] = "Real-time protection and cloud protection", ["platforms"] = "windows10", ["technologies"] = "mdm,microsoftSense", ["roleScopeTagIds"] = new JsonArray("0"),
        });
        g.Add($"deviceManagement/configurationPolicies/{Id(sc)}/settings", new JsonObject
        {
            ["id"] = "0", ["settingInstance"] = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationChoiceSettingInstance",
                ["settingDefinitionId"] = "device_vendor_msft_policy_config_defender_allowrealtimemonitoring",
                ["choiceSettingValue"] = new JsonObject { ["value"] = "device_vendor_msft_policy_config_defender_allowrealtimemonitoring_1", ["children"] = new JsonArray() },
            },
        });
        Assign(g, $"deviceManagement/configurationPolicies/{Id(sc)}/assignments", Id(pilot), null);
        var comp = g.Add("deviceManagement/deviceCompliancePolicies", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.windows10CompliancePolicy", ["displayName"] = "Windows - Compliance", ["bitLockerEnabled"] = true, ["secureBootEnabled"] = true,
            ["osMinimumVersion"] = "10.0.19045", ["roleScopeTagIds"] = new JsonArray("0"),
            ["scheduledActionsForRule"] = new JsonArray(new JsonObject { ["ruleName"] = "PasswordRequired", ["scheduledActionConfigurations"] = new JsonArray(new JsonObject { ["actionType"] = "block", ["gracePeriodHours"] = 24 }) }),
        });
        Assign(g, $"deviceManagement/deviceCompliancePolicies/{Id(comp)}/assignments", Id(allStaff), null);
        var script = g.Add("deviceManagement/deviceManagementScripts", new JsonObject
        {
            ["displayName"] = "Set time zone", ["fileName"] = "Set-TimeZone.ps1", ["runAsAccount"] = "system", ["enforceSignatureCheck"] = false,
            ["scriptContent"] = Convert.ToBase64String("Set-TimeZone -Id 'Pacific Standard Time'"u8.ToArray()), ["roleScopeTagIds"] = new JsonArray("0"),
        });
        Assign(g, $"deviceManagement/deviceManagementScripts/{Id(script)}/assignments", Id(pilot), null);
        _ = app;
    }

    private static void Assign(InMemoryGraph g, string path, string groupId, string? filterId)
    {
        var target = new JsonObject { ["@odata.type"] = "#microsoft.graph.groupAssignmentTarget", ["groupId"] = groupId };
        if (filterId != null)
        {
            target["deviceAndAppManagementAssignmentFilterId"] = filterId;
            target["deviceAndAppManagementAssignmentFilterType"] = "include";
        }
        g.Add(path, new JsonObject { ["target"] = target });
    }

    /// <summary>Serves a singleton object with GET and PATCH.</summary>
    public static void Singleton(InMemoryGraph g, string path, JsonObject obj)
    {
        g.On(HttpMethod.Get, path, (_, _) => InMemoryGraph.Json(obj));
        g.On(HttpMethod.Patch, path, (_, body) =>
        {
            lock (obj)
                foreach (var (k, v) in JsonNode.Parse(body ?? "{}")!.AsObject()) obj[k] = v?.DeepClone();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
    }
}
