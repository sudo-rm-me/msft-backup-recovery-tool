using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Infrastructure.Demo;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 7: Azure configuration (management groups, resource groups as templates, RBAC, Policy) and redeploying it.</summary>
public class AzureTests
{
    private sealed record World(TestApp App, HttpClient Client, Mbr.Core.Model.Tenant Tenant, FakeTenantData.Seeded Seeded, string SnapshotId);

    private static readonly string WebRg = DemoAzure.Rg(DemoAzure.ProdSubscription, "rg-web-prod");

    private static async Task<World> SetUpAsync(params string[] workloads)
    {
        var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        DemoAzure.Seed(app.Arm, tenant.DirectoryId, seeded.GroupId, seeded.UserId, seeded.GroupId);
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = workloads.Length > 0 ? workloads : new[] { "azure" } });
        Assert.True(job["status"]!.GetValue<string>() == "Succeeded", job.ToJsonString());
        return new World(app, client, tenant, seeded, job["result"]!["snapshotId"]!.GetValue<string>());
    }

    private static async Task<JsonArray> ItemsAsync(World w, string type) =>
        JsonNode.Parse(await w.Client.GetStringAsync($"/api/snapshots/{w.SnapshotId}/items?type={type}"))!["items"]!.AsArray();

    private static async Task<JsonNode> ContentAsync(World w, JsonNode item) =>
        JsonNode.Parse(await w.Client.GetStringAsync($"/api/snapshots/{w.SnapshotId}/items/{item["id"]}"))!["content"]!;

    private static async Task<JsonNode> PlanAsync(World w, object request)
    {
        var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.AsObject();
        payload["snapshotId"] = w.SnapshotId;
        var created = JsonNode.Parse(await (await w.Client.PostAsJsonAsync("/api/restore/plans", payload)).Content.ReadAsStringAsync())!;
        var planJob = await w.App.WaitForJobAsync(w.Client, created["jobId"]!.GetValue<string>());
        Assert.True(planJob["status"]!.GetValue<string>() == "Succeeded", $"{planJob["error"]}");
        return JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
    }

    private static async Task<JsonNode> ApplyAsync(World w, JsonNode plan)
    {
        var id = plan["plan"]!["id"]!.GetValue<string>();
        var started = JsonNode.Parse(await (await w.Client.PostAsync($"/api/restore/plans/{id}/apply", null)).Content.ReadAsStringAsync())!;
        await w.App.WaitForJobAsync(w.Client, started["jobId"]!.GetValue<string>());
        var applied = JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{id}"))!;
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        var issues = applied["actions"]!.AsArray().Where(a => a!["verificationIssues"]!.AsArray().Count > 0).ToList();
        Assert.True(issues.Count == 0, Describe(applied));
        return applied;
    }

    private static List<JsonNode> Acts(JsonNode plan) => plan["actions"]!.AsArray().Select(a => a!).Where(a => a["kind"]!.GetValue<string>() != "Skip").ToList();

    private static string Describe(JsonNode plan) => string.Join("\n", plan["actions"]!.AsArray().Select(a =>
        $"{a!["kind"]} {a["type"]} {a["displayName"]} relink={a["isRelink"]} [{a["status"]}] {a["error"]} {a["reason"]} changes={string.Join(",", a["changes"]!.AsArray())} " +
        string.Join("; ", a["verificationIssues"]!.AsArray().Select(v => v!.ToString()))));

    [Fact]
    public async Task Backup_captures_hierarchy_templates_rbac_and_policy()
    {
        var w = await SetUpAsync();
        using var app = w.App;

        Assert.Equal(4, (await ItemsAsync(w, "azure.managementGroup")).Count);
        Assert.Equal(2, (await ItemsAsync(w, "azure.subscription")).Count);
        Assert.Equal(3, (await ItemsAsync(w, "azure.resourceGroup")).Count);
        Assert.Single(await ItemsAsync(w, "azure.roleDefinition")); // custom roles only
        Assert.Equal(4, (await ItemsAsync(w, "azure.roleAssignment")).Count);
        Assert.Single(await ItemsAsync(w, "azure.policyDefinition"));
        Assert.Single(await ItemsAsync(w, "azure.policySetDefinition"));
        Assert.Equal(2, (await ItemsAsync(w, "azure.policyAssignment")).Count);

        var rg = (await ItemsAsync(w, "azure.resourceGroup")).Single(i => i!["objectId"]!.GetValue<string>() == WebRg)!;
        Assert.Equal("rg-web-prod (Contoso Production)", rg["displayName"]!.GetValue<string>());
        var content = await ContentAsync(w, rg);
        Assert.Equal(3, content["resources"]!.AsArray().Count);
        var template = content["template"]!;
        Assert.Equal(3, template["resources"]!.AsArray().Count);
        Assert.Contains(template["resources"]!.AsArray(), r => r!["type"]!.GetValue<string>() == "Microsoft.Web/sites" && r["name"]!.GetValue<string>() == "app-contoso-web");
        Assert.Null(content["properties"]); // provisioning state is volatile

        var mg = (await ItemsAsync(w, "azure.managementGroup")).Single(i => i!["displayName"]!.GetValue<string>() == "Production")!;
        var mgContent = await ContentAsync(w, mg);
        Assert.Contains(mgContent["properties"]!["children"]!.AsArray(), c => c!["id"]!.GetValue<string>() == $"/subscriptions/{DemoAzure.ProdSubscription}");
        Assert.Null(mgContent["properties"]!["details"]!["updatedTime"]);

        var ra = (await ItemsAsync(w, "azure.roleAssignment")).Select(i => i!["displayName"]!.GetValue<string>()).ToList();
        Assert.Contains("Contoso VM Operator for All Staff on rg-web-prod (Contoso Production)", ra);
        var pa = await ContentAsync(w, (await ItemsAsync(w, "azure.policyAssignment")).Single(i => i!["displayName"]!.GetValue<string>().StartsWith("Contoso baseline"))!);
        Assert.Equal("SystemAssigned", pa["identity"]!["type"]!.GetValue<string>());
        Assert.Null(pa["identity"]!["principalId"]);
    }

    [Fact]
    public async Task Deleted_resource_group_is_redeployed_with_its_resources_and_role_assignments()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        app.Arm.ArmDelete(WebRg); // takes the resources and RG-scoped role assignments with it
        Assert.False(app.Arm.ArmExists($"{WebRg}/providers/Microsoft.Web/sites/app-contoso-web"));

        var inventory = JsonNode.Parse(await w.Client.GetStringAsync($"/api/tenants/{w.Tenant.Id}/azure/resourceGroups"))!;
        var groups = inventory["resourceGroups"]!.AsArray();
        Assert.Equal(3, groups.Count);
        var deleted = groups[0]!; // deleted groups are listed first
        Assert.Equal("deleted", deleted["status"]!.GetValue<string>());
        Assert.Equal("rg-web-prod", deleted["name"]!.GetValue<string>());
        Assert.Equal("Contoso Production", deleted["subscriptionName"]!.GetValue<string>());
        Assert.Equal(3, deleted["resourceCount"]!.GetValue<int>());
        Assert.All(groups.Skip(1), g => Assert.Equal("present", g!["status"]!.GetValue<string>()));

        var rg = (await ItemsAsync(w, "azure.resourceGroup")).Single(i => i!["objectId"]!.GetValue<string>() == WebRg)!;
        Assert.Equal(rg["id"]!.GetValue<long>(), deleted["snapshotItemId"]!.GetValue<long>());
        var plan = await PlanAsync(w, new { itemIds = new[] { rg["id"]!.GetValue<long>() } });
        var acts = Acts(plan);
        Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "azure.resourceGroup" && a["kind"]!.GetValue<string>() == "Create");
        Assert.Equal(2, acts.Count(a => a["type"]!.GetValue<string>() == "azure.roleAssignment" && a["kind"]!.GetValue<string>() == "Create" && a["isRelink"]!.GetValue<bool>()));
        Assert.DoesNotContain(acts, a => a["type"]!.GetValue<string>() is "azure.policyAssignment" or "azure.managementGroup");

        var applied = await ApplyAsync(w, plan);
        foreach (var r in new[] { "Microsoft.Storage/storageAccounts/stcontosoweb01", "Microsoft.Web/serverfarms/asp-web-prod", "Microsoft.Web/sites/app-contoso-web" })
            Assert.True(app.Arm.ArmExists($"{WebRg}/providers/{r}"), r);
        var site = app.Arm.ArmResources[$"{WebRg}/providers/Microsoft.Web/sites/app-contoso-web"];
        Assert.Equal("DOTNETCORE|10.0", site["properties"]!["siteConfig"]!["linuxFxVersion"]!.GetValue<string>());
        Assert.Equal("prod", app.Arm.ArmResources[WebRg]["tags"]!["env"]!.GetValue<string>());
        Assert.Single(app.Arm.Deployments);
        Assert.Equal(2, app.Arm.ArmResources.Keys.Count(k => k.StartsWith(WebRg + "/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase)));
        var rgAction = applied["actions"]!.AsArray().Single(a => a!["type"]!.GetValue<string>() == "azure.resourceGroup")!;
        Assert.Contains(rgAction["warnings"]!.AsArray(), x => x!.GetValue<string>().Contains("redeployed"));
    }

    [Fact]
    public async Task Changed_resources_are_reset_by_redeploying_the_template()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var storage = app.Arm.ArmResources[$"{WebRg}/providers/Microsoft.Storage/storageAccounts/stcontosoweb01"];
        storage["properties"]!["minimumTlsVersion"] = "TLS1_0";
        app.Arm.ArmResources[WebRg]["tags"]!["costCenter"] = "9999";

        var plan = await PlanAsync(w, new { types = new[] { "azure.resourceGroup" } });
        var update = Assert.Single(Acts(plan));
        Assert.Equal("Update", update["kind"]!.GetValue<string>());
        var changes = update["changes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains(changes, c => c.StartsWith("template.resources") && c.Contains("minimumTlsVersion"));
        Assert.Contains("tags.costCenter", changes);

        await ApplyAsync(w, plan);
        Assert.Equal("TLS1_2", app.Arm.ArmResources[$"{WebRg}/providers/Microsoft.Storage/storageAccounts/stcontosoweb01"]["properties"]!["minimumTlsVersion"]!.GetValue<string>());
        Assert.Equal("1001", app.Arm.ArmResources[WebRg]["tags"]!["costCenter"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleted_policy_objects_and_custom_role_are_recreated()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var baseline = $"/subscriptions/{DemoAzure.ProdSubscription}/providers/Microsoft.Authorization/policyAssignments/contoso-baseline";
        var set = $"{DemoAzure.Mg("contoso")}/providers/Microsoft.Authorization/policySetDefinitions/contoso-baseline";
        app.Arm.ArmDelete(baseline);
        app.Arm.ArmDelete(set);
        app.Arm.ArmDelete($"/subscriptions/{DemoAzure.ProdSubscription}/providers/Microsoft.Authorization/roleDefinitions/{DemoAzure.VmOperatorRole}");

        var plan = await PlanAsync(w, new { workloads = new[] { "azure" } });
        var creates = Acts(plan).Where(a => a["kind"]!.GetValue<string>() == "Create").Select(a => a["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["azure.policyAssignment", "azure.policySetDefinition", "azure.roleDefinition"], creates.Order().ToList());
        Assert.DoesNotContain(Acts(plan), a => a["kind"]!.GetValue<string>() == "Update");

        var applied = await ApplyAsync(w, plan);
        Assert.True(app.Arm.ArmExists(set));
        Assert.True(app.Arm.ArmExists(baseline));
        Assert.Equal(set, app.Arm.ArmResources[baseline]["properties"]!["policyDefinitionId"]!.GetValue<string>());
        Assert.Equal("SystemAssigned", app.Arm.ArmResources[baseline]["identity"]!["type"]!.GetValue<string>());
        var pa = applied["actions"]!.AsArray().Single(a => a!["type"]!.GetValue<string>() == "azure.policyAssignment" && a["kind"]!.GetValue<string>() == "Create")!;
        Assert.Contains(pa["warnings"]!.AsArray(), x => x!.GetValue<string>().Contains("managed identity"));
    }

    [Fact]
    public async Task Role_assignments_follow_a_recreated_group()
    {
        var w = await SetUpAsync("entra", "azure");
        using var app = w.App;
        var groupId = w.Seeded.GroupId;
        app.Graph.Collection("groups").RemoveAll(g => g["id"]!.GetValue<string>() == groupId); // purged; Azure keeps orphaned assignments

        var plan = await PlanAsync(w, new { types = new[] { "entra.group" } });
        var acts = Acts(plan);
        Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "entra.group" && a["kind"]!.GetValue<string>() == "Create");
        Assert.Equal(3, acts.Count(a => a["type"]!.GetValue<string>() == "azure.roleAssignment" && a["isRelink"]!.GetValue<bool>()));

        await ApplyAsync(w, plan);
        var newId = app.Graph.Collection("groups").Single(g => g["displayName"]!.GetValue<string>() == "All Staff")["id"]!.GetValue<string>();
        Assert.NotEqual(groupId, newId);
        var assignments = app.Arm.ArmResources.Values.Where(r => r["type"]!.GetValue<string>() == "Microsoft.Authorization/roleAssignments").ToList();
        Assert.Equal(3, assignments.Count(a => a["properties"]!["principalId"]!.GetValue<string>() == newId));
        Assert.DoesNotContain(assignments, a => a["properties"]!["principalId"]!.GetValue<string>() == groupId);
    }
}
