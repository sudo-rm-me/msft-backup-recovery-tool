using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    static partial void SeedPhase7Plus(InMemoryGraph g, InMemoryGraph arm)
    {
        var groups = g.Collection("groups");
        var users = g.Collection("users");
        DemoAzure.Seed(arm, DemoMode.DirectoryId, groups[0]["id"]!.ToString(), users[0]["id"]!.ToString(), groups.Count > 1 ? groups[1]["id"]!.ToString() : groups[0]["id"]!.ToString());
        SeedPhase8Plus(g, arm);
    }

    static partial void SeedPhase8Plus(InMemoryGraph g, InMemoryGraph arm);
}

/// <summary>A small Azure estate: management groups, two subscriptions, resource groups with resources, RBAC and Policy.</summary>
public static class DemoAzure
{
    public const string ProdSubscription = "c0a1b2c3-0000-4000-8000-000000000001";
    public const string DevSubscription = "c0a1b2c3-0000-4000-8000-000000000002";
    public const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    public const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    public const string VmOperatorRole = "5a7f2d9c-1111-4e2a-9c3b-000000000001";

    public static string Rg(string sub, string name) => $"/subscriptions/{sub}/resourceGroups/{name}";
    public static string Mg(string name) => $"/providers/Microsoft.Management/managementGroups/{name}";

    public static void Seed(InMemoryGraph arm, string tenantId, string adminsGroupId, string userId, string opsGroupId)
    {
        arm.Add("subscriptions", new JsonObject { ["subscriptionId"] = ProdSubscription, ["displayName"] = "Contoso Production", ["state"] = "Enabled", ["tenantId"] = tenantId });
        arm.Add("subscriptions", new JsonObject { ["subscriptionId"] = DevSubscription, ["displayName"] = "Contoso Dev/Test", ["state"] = "Enabled", ["tenantId"] = tenantId });

        // Management groups: Tenant Root Group > Contoso > Production / Development
        MgPut(arm, tenantId, "Tenant Root Group", null);
        MgPut(arm, "contoso", "Contoso", Mg(tenantId));
        MgPut(arm, "contoso-prod", "Production", Mg("contoso"));
        MgPut(arm, "contoso-dev", "Development", Mg("contoso"));
        arm.SubscriptionParents[ProdSubscription] = Mg("contoso-prod");
        arm.SubscriptionParents[DevSubscription] = Mg("contoso-dev");

        foreach (var sub in new[] { ProdSubscription, DevSubscription })
            foreach (var (id, name) in new[] { (Owner, "Owner"), (Contributor, "Contributor"), (Reader, "Reader") })
                arm.ArmPut($"/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions/{id}", new JsonObject
                {
                    ["properties"] = new JsonObject { ["roleName"] = name, ["type"] = "BuiltInRole", ["description"] = $"Built-in {name} role" },
                });
        arm.ArmPut($"/subscriptions/{ProdSubscription}/providers/Microsoft.Authorization/roleDefinitions/{VmOperatorRole}", new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleName"] = "Contoso VM Operator", ["type"] = "CustomRole", ["description"] = "Start, stop and restart virtual machines.",
                ["permissions"] = new JsonArray(new JsonObject
                {
                    ["actions"] = new JsonArray("Microsoft.Compute/virtualMachines/read", "Microsoft.Compute/virtualMachines/start/action",
                        "Microsoft.Compute/virtualMachines/restart/action", "Microsoft.Compute/virtualMachines/deallocate/action"),
                    ["notActions"] = new JsonArray(), ["dataActions"] = new JsonArray(), ["notDataActions"] = new JsonArray(),
                }),
                ["assignableScopes"] = new JsonArray($"/subscriptions/{ProdSubscription}"),
            },
        });

        // Resource groups and resources
        var web = Rg(ProdSubscription, "rg-web-prod");
        RgPut(arm, web, "westeurope", ("env", "prod"), ("costCenter", "1001"));
        arm.ArmPut($"{web}/providers/Microsoft.Storage/storageAccounts/stcontosoweb01", new JsonObject
        {
            ["location"] = "westeurope", ["kind"] = "StorageV2", ["sku"] = new JsonObject { ["name"] = "Standard_ZRS" },
            ["tags"] = new JsonObject { ["env"] = "prod" },
            ["properties"] = new JsonObject { ["minimumTlsVersion"] = "TLS1_2", ["supportsHttpsTrafficOnly"] = true, ["allowBlobPublicAccess"] = false },
        });
        arm.ArmPut($"{web}/providers/Microsoft.Web/serverfarms/asp-web-prod", new JsonObject
        {
            ["location"] = "westeurope", ["kind"] = "linux", ["sku"] = new JsonObject { ["name"] = "P1v3", ["tier"] = "PremiumV3" },
            ["properties"] = new JsonObject { ["reserved"] = true },
        });
        arm.ArmPut($"{web}/providers/Microsoft.Web/sites/app-contoso-web", new JsonObject
        {
            ["location"] = "westeurope", ["kind"] = "app,linux",
            ["properties"] = new JsonObject
            {
                ["serverFarmId"] = $"{web}/providers/Microsoft.Web/serverfarms/asp-web-prod", ["httpsOnly"] = true,
                ["siteConfig"] = new JsonObject { ["linuxFxVersion"] = "DOTNETCORE|10.0", ["minTlsVersion"] = "1.2", ["ftpsState"] = "Disabled" },
            },
        });

        var net = Rg(ProdSubscription, "rg-network-prod");
        RgPut(arm, net, "westeurope", ("env", "prod"), ("costCenter", "1000"));
        arm.ArmPut($"{net}/providers/Microsoft.Network/virtualNetworks/vnet-prod", new JsonObject
        {
            ["location"] = "westeurope",
            ["properties"] = new JsonObject
            {
                ["addressSpace"] = new JsonObject { ["addressPrefixes"] = new JsonArray("10.10.0.0/16") },
                ["subnets"] = new JsonArray(
                    new JsonObject { ["name"] = "snet-web", ["properties"] = new JsonObject { ["addressPrefix"] = "10.10.1.0/24" } },
                    new JsonObject { ["name"] = "snet-data", ["properties"] = new JsonObject { ["addressPrefix"] = "10.10.2.0/24" } }),
            },
        });
        arm.ArmPut($"{net}/providers/Microsoft.Network/networkSecurityGroups/nsg-web", new JsonObject
        {
            ["location"] = "westeurope",
            ["properties"] = new JsonObject
            {
                ["securityRules"] = new JsonArray(new JsonObject
                {
                    ["name"] = "allow-https",
                    ["properties"] = new JsonObject
                    {
                        ["priority"] = 100, ["direction"] = "Inbound", ["access"] = "Allow", ["protocol"] = "Tcp",
                        ["sourceAddressPrefix"] = "*", ["sourcePortRange"] = "*", ["destinationAddressPrefix"] = "*", ["destinationPortRange"] = "443",
                    },
                }),
            },
        });

        var dev = Rg(DevSubscription, "rg-sandbox");
        RgPut(arm, dev, "northeurope", ("env", "dev"));
        arm.ArmPut($"{dev}/providers/Microsoft.Storage/storageAccounts/stcontosodev01", new JsonObject
        {
            ["location"] = "northeurope", ["kind"] = "StorageV2", ["sku"] = new JsonObject { ["name"] = "Standard_LRS" },
            ["properties"] = new JsonObject { ["minimumTlsVersion"] = "TLS1_2" },
        });

        // RBAC
        RoleAssignment(arm, $"/subscriptions/{ProdSubscription}", "a0000000-0000-4000-8000-000000000001", Contributor, adminsGroupId, "Group");
        RoleAssignment(arm, web, "a0000000-0000-4000-8000-000000000002", Reader, userId, "User");
        RoleAssignment(arm, web, "a0000000-0000-4000-8000-000000000003", VmOperatorRole, opsGroupId, "Group");
        RoleAssignment(arm, Mg("contoso"), "a0000000-0000-4000-8000-000000000004", Reader, adminsGroupId, "Group", subscriptionForRole: null);

        // Policy
        var tagPolicy = $"{Mg("contoso")}/providers/Microsoft.Authorization/policyDefinitions/require-costcenter-tag";
        arm.ArmPut(tagPolicy, new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["policyType"] = "Custom", ["mode"] = "Indexed", ["displayName"] = "Require a costCenter tag on resource groups",
                ["description"] = "Denies resource groups without a costCenter tag.",
                ["metadata"] = new JsonObject { ["category"] = "Tags", ["createdBy"] = "00000000-0000-0000-0000-000000000000", ["createdOn"] = "2026-01-01T00:00:00Z" },
                ["parameters"] = new JsonObject(),
                ["policyRule"] = new JsonObject
                {
                    ["if"] = new JsonObject
                    {
                        ["allOf"] = new JsonArray(
                            new JsonObject { ["field"] = "type", ["equals"] = "Microsoft.Resources/subscriptions/resourceGroups" },
                            new JsonObject { ["field"] = "tags['costCenter']", ["exists"] = "false" }),
                    },
                    ["then"] = new JsonObject { ["effect"] = "deny" },
                },
            },
        });
        var baseline = $"{Mg("contoso")}/providers/Microsoft.Authorization/policySetDefinitions/contoso-baseline";
        arm.ArmPut(baseline, new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["policyType"] = "Custom", ["displayName"] = "Contoso baseline", ["description"] = "Tagging and allowed locations.",
                ["metadata"] = new JsonObject { ["category"] = "General" },
                ["parameters"] = new JsonObject(),
                ["policyDefinitions"] = new JsonArray(
                    new JsonObject { ["policyDefinitionReferenceId"] = "costCenterTag", ["policyDefinitionId"] = tagPolicy, ["parameters"] = new JsonObject() },
                    new JsonObject
                    {
                        ["policyDefinitionReferenceId"] = "allowedLocations", ["policyDefinitionId"] = "/providers/Microsoft.Authorization/policyDefinitions/e56962a6-4747-49cd-b67b-bf8b01975c4c",
                        ["parameters"] = new JsonObject { ["listOfAllowedLocations"] = new JsonObject { ["value"] = new JsonArray("westeurope", "northeurope") } },
                    }),
            },
        });
        arm.ArmPut($"/subscriptions/{ProdSubscription}/providers/Microsoft.Authorization/policyAssignments/contoso-baseline", new JsonObject
        {
            ["location"] = "westeurope",
            ["identity"] = new JsonObject { ["type"] = "SystemAssigned", ["principalId"] = "9f000000-0000-4000-8000-000000000001", ["tenantId"] = tenantId },
            ["properties"] = new JsonObject
            {
                ["displayName"] = "Contoso baseline (production)", ["policyDefinitionId"] = baseline, ["enforcementMode"] = "Default",
                ["parameters"] = new JsonObject(), ["notScopes"] = new JsonArray(),
            },
        });
        arm.ArmPut($"{dev}/providers/Microsoft.Authorization/policyAssignments/sandbox-tags", new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = "Require costCenter tag (sandbox, audit only)", ["policyDefinitionId"] = tagPolicy, ["enforcementMode"] = "DoNotEnforce",
                ["parameters"] = new JsonObject(),
            },
        });
    }

    private static void MgPut(InMemoryGraph arm, string name, string displayName, string? parent) =>
        arm.ArmPut(Mg(name), new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["displayName"] = displayName,
                ["details"] = new JsonObject { ["parent"] = parent is null ? null : new JsonObject { ["id"] = parent }, ["version"] = 1, ["updatedTime"] = "2026-01-01T00:00:00Z" },
            },
        });

    private static void RgPut(InMemoryGraph arm, string id, string location, params (string Key, string Value)[] tags)
    {
        var t = new JsonObject();
        foreach (var (k, v) in tags) t[k] = v;
        arm.ArmPut(id, new JsonObject { ["location"] = location, ["tags"] = t, ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" } });
    }

    private static void RoleAssignment(InMemoryGraph arm, string scope, string name, string role, string principalId, string principalType,
        string? subscriptionForRole = ProdSubscription) =>
        arm.ArmPut($"{scope}/providers/Microsoft.Authorization/roleAssignments/{name}", new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleDefinitionId"] = subscriptionForRole is null
                    ? $"/providers/Microsoft.Authorization/roleDefinitions/{role}"
                    : $"/subscriptions/{subscriptionForRole}/providers/Microsoft.Authorization/roleDefinitions/{role}",
                ["principalId"] = principalId, ["principalType"] = principalType,
                ["createdOn"] = "2026-01-01T00:00:00Z", ["updatedOn"] = "2026-01-01T00:00:00Z",
            },
        });
}
