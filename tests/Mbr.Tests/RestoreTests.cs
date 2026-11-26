using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Model;
using Mbr.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Tests;

public class RestoreTests
{
    private static async Task<string> BackupAsync(TestApp app, HttpClient client, Tenant tenant)
    {
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        return job["result"]!["snapshotId"]!.GetValue<string>();
    }

    private static async Task<JsonNode> PlanAsync(TestApp app, HttpClient client, string snapshotId, object? body = null)
    {
        var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(body ?? new { }))!.AsObject();
        payload["snapshotId"] = snapshotId;
        var resp = await client.PostAsJsonAsync("/api/restore/plans", payload);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var created = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var job = await app.WaitForJobAsync(client, created["jobId"]!.GetValue<string>());
        var plan = JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        Assert.True(job["status"]!.GetValue<string>() == "Succeeded",
            $"Plan job {job["status"]}: {job["error"]}\n{await client.GetStringAsync($"/api/jobs/{job["id"]}/logs")}");
        return plan;
    }

    private static async Task<JsonNode> ApplyAsync(TestApp app, HttpClient client, JsonNode plan)
    {
        var id = plan["plan"]!["id"]!.GetValue<string>();
        var resp = await client.PostAsync($"/api/restore/plans/{id}/apply", null);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var started = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        await app.WaitForJobAsync(client, started["jobId"]!.GetValue<string>());
        return JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{id}"))!;
    }

    private static IEnumerable<JsonNode> Actions(JsonNode plan, string? kind = null) =>
        plan["actions"]!.AsArray().Select(a => a!).Where(a => kind is null || a["kind"]!.GetValue<string>() == kind);

    private static string Describe(JsonNode plan) => string.Join("\n", Actions(plan).Select(a =>
        $"{a["kind"]} {a["type"]} {a["displayName"]} [{a["status"]}] {a["error"]} {a["reason"]} {string.Join("; ", a["verificationIssues"]?.AsArray().Select(v => v!.ToString()) ?? [])}"));

    [Fact]
    public async Task Unchanged_tenant_plans_no_changes()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);

        var plan = await PlanAsync(app, client, snapshot);
        Assert.True(Actions(plan).All(a => a["kind"]!.GetValue<string>() == "Skip"), Describe(plan));
        Assert.Equal(0, plan["plan"]!["createCount"]!.GetValue<int>() + plan["plan"]!["updateCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Deleted_policy_is_recreated_with_its_assignments()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);

        app.Graph.Collection("deviceManagement/deviceConfigurations").Clear();

        var plan = await PlanAsync(app, client, snapshot, new { types = new[] { "intune.deviceConfiguration" } });
        var create = Assert.Single(Actions(plan, "Create"));
        Assert.Equal("Windows baseline", create["displayName"]!.GetValue<string>());

        var applied = await ApplyAsync(app, client, plan);
        Assert.Equal("Applied", applied["plan"]!["status"]!.GetValue<string>());
        var action = Actions(applied, "Create").Single();
        Assert.Equal("Succeeded", action["status"]!.GetValue<string>());
        Assert.Empty(action["verificationIssues"]!.AsArray());

        var recreated = Assert.Single(app.Graph.Collection("deviceManagement/deviceConfigurations"));
        Assert.Equal(12, recreated["passwordMinimumLength"]!.GetValue<int>());
        Assert.Null(recreated["lastModifiedDateTime"]);
        var newId = recreated["id"]!.GetValue<string>();
        Assert.NotEqual(seeded.DeviceConfigId, newId);
        var assignment = Assert.Single(app.Graph.Collection($"deviceManagement/deviceConfigurations/{newId}/assignments"));
        Assert.Equal(seeded.GroupId, assignment["target"]!["groupId"]!.GetValue<string>());
        Assert.Equal(seeded.FilterId, assignment["target"]!["deviceAndAppManagementAssignmentFilterId"]!.GetValue<string>());

        // The mapping is remembered, so the next restore treats the new object as the same policy.
        Assert.Equal(newId, app.Db(db => db.IdMappings.Single(m => m.ResourceType == "intune.deviceConfiguration").TargetId));
        var again = await PlanAsync(app, client, snapshot, new { types = new[] { "intune.deviceConfiguration" } });
        Assert.True(Actions(again).All(a => a["kind"]!.GetValue<string>() == "Skip"), Describe(again));
    }

    [Fact]
    public async Task Changed_policy_is_reverted_and_conditional_access_can_come_back_report_only()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);

        var ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        ca["state"] = "disabled";
        ca["conditions"]!["users"]!["excludeUsers"] = new JsonArray();
        var settings = app.Graph.Collection($"deviceManagement/configurationPolicies/{seeded.SettingsPolicyId}/settings");
        settings[0]["settingInstance"]!["choiceSettingValue"]!["value"] = "device_vendor_msft_policy_config_defender_allowrealtimemonitoring_0";

        var plan = await PlanAsync(app, client, snapshot, new { options = new { conditionalAccessReportOnly = true } });
        var updates = Actions(plan, "Update").ToList();
        Assert.Equal(["entra.conditionalAccessPolicy", "intune.settingsCatalogPolicy"], updates.Select(u => u["type"]!.GetValue<string>()).Order());
        Assert.Contains(updates[0]["changes"]!.AsArray(), c => c!.GetValue<string>().StartsWith("conditions.users.excludeUsers"));

        var applied = await ApplyAsync(app, client, plan);
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        Assert.Equal("enabledForReportingButNotEnforced", ca["state"]!.GetValue<string>());
        Assert.Equal([seeded.UserId], ca["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        settings = app.Graph.Collection($"deviceManagement/configurationPolicies/{seeded.SettingsPolicyId}/settings");
        Assert.Equal("device_vendor_msft_policy_config_defender_allowrealtimemonitoring_1",
            settings.Single()["settingInstance"]!["choiceSettingValue"]!["value"]!.GetValue<string>());
        Assert.Contains("PUT /beta/deviceManagement/configurationPolicies/" + seeded.SettingsPolicyId, app.Graph.RequestLog);
    }

    [Fact]
    public async Task Deleted_group_comes_back_from_the_recycle_bin_with_its_members()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);

        await DeleteThroughGraphAsync(app, $"/v1.0/groups/{seeded.GroupId}");
        app.Graph.Collection($"groups/{seeded.GroupId}/members").Clear();

        var plan = await PlanAsync(app, client, snapshot, new { types = new[] { "entra.group" } });
        var action = Assert.Single(Actions(plan, "RecycleBin"));
        Assert.Equal(seeded.GroupId, action["targetObjectId"]!.GetValue<string>());

        var applied = await ApplyAsync(app, client, plan);
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        Assert.Contains(app.Graph.Collection("groups"), g => g["id"]!.GetValue<string>() == seeded.GroupId);
        Assert.Contains(app.Graph.Collection($"groups/{seeded.GroupId}/members"), m => m["id"]!.GetValue<string>() == seeded.UserId);
    }

    [Fact]
    public async Task Purged_group_is_recreated_and_policies_follow_the_new_id()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);

        await DeleteThroughGraphAsync(app, $"/v1.0/groups/{seeded.GroupId}");
        app.Graph.Purge(seeded.GroupId);
        // The CA policy lost its group reference when the group was deleted.
        var ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        ca["conditions"]!["users"]!["includeGroups"] = new JsonArray();

        var plan = await PlanAsync(app, client, snapshot, new { types = new[] { "entra.conditionalAccessPolicy" } });
        var create = Assert.Single(Actions(plan, "Create"));
        Assert.Equal("entra.group", create["type"]!.GetValue<string>());
        Assert.True(create["isDependency"]!.GetValue<bool>());
        // The CA policy, plus anything still pointing at the old group ID (the device configuration's assignment), which is re-linked.
        var updates = Actions(plan, "Update").Select(a => a["type"]!.GetValue<string>()).Order().ToList();
        Assert.Equal(["entra.conditionalAccessPolicy", "intune.deviceConfiguration"], updates);

        var applied = await ApplyAsync(app, client, plan);
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        var group = app.Graph.Collection("groups").Single(g => g["displayName"]!.GetValue<string>() == "All Staff");
        var newGroupId = group["id"]!.GetValue<string>();
        Assert.NotEqual(seeded.GroupId, newGroupId);
        ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        Assert.Equal([newGroupId], ca["conditions"]!["users"]!["includeGroups"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains(app.Graph.Collection($"groups/{newGroupId}/members"), m => m["id"]!.GetValue<string>() == seeded.UserId);
        var assignment = Assert.Single(app.Graph.Collection($"deviceManagement/deviceConfigurations/{seeded.DeviceConfigId}/assignments"));
        Assert.Equal(newGroupId, assignment["target"]!["groupId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cross_tenant_restore_matches_existing_objects_and_remaps_references()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var source = await app.AddTenantAsync("Contoso");
        var target = await app.AddTenantAsync("Fabrikam");
        var seeded = FakeTenantData.Seed(app.Graph);
        var targetGraph = new FakeGraph();
        app.TenantGraphs[target.DirectoryId] = targetGraph;
        var targetGroup = targetGraph.Add("groups", new JsonObject
        {
            ["displayName"] = "All Staff", ["mailEnabled"] = false, ["securityEnabled"] = true, ["mailNickname"] = "allstaff", ["groupTypes"] = new JsonArray(),
        });
        targetGraph.Collection("deviceManagement/assignmentFilters");
        targetGraph.Collection("deviceManagement/deviceConfigurations");
        targetGraph.Collection("deviceManagement/roleScopeTags");
        var snapshot = await BackupAsync(app, client, source);

        var plan = await PlanAsync(app, client, snapshot, new
        {
            targetTenantId = target.Id, types = new[] { "intune.deviceConfiguration" },
            options = new { includeDependencies = true },
        });
        Assert.True(plan["plan"]!["crossTenant"]!.GetValue<bool>());
        var creates = Actions(plan, "Create").Select(a => a["type"]!.GetValue<string>()).ToList();
        Assert.Contains("intune.assignmentFilter", creates);
        Assert.Contains("intune.deviceConfiguration", creates);
        Assert.DoesNotContain("entra.group", creates); // matched by name

        var applied = await ApplyAsync(app, client, plan);
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() is "Applied" or "PartiallyApplied", Describe(applied));
        Assert.Empty(app.Graph.RequestLog.Where(r => !r.StartsWith("GET")));
        var filter = Assert.Single(targetGraph.Collection("deviceManagement/assignmentFilters"));
        var config = Assert.Single(targetGraph.Collection("deviceManagement/deviceConfigurations"));
        var assignment = Assert.Single(targetGraph.Collection($"deviceManagement/deviceConfigurations/{config["id"]}/assignments"));
        Assert.Equal(targetGroup["id"]!.GetValue<string>(), assignment["target"]!["groupId"]!.GetValue<string>());
        Assert.Equal(filter["id"]!.GetValue<string>(), assignment["target"]!["deviceAndAppManagementAssignmentFilterId"]!.GetValue<string>());
        Assert.Equal(2, app.Db(db => db.IdMappings.Count(m => m.TargetTenantId == target.Id && m.ResourceType != "entra.group")));
    }

    [Fact]
    public async Task Approval_is_required_from_a_different_operator_when_enabled()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);
        (await client.PutAsJsonAsync($"/api/tenants/{tenant.Id}/settings", new
        {
            configRetentionDays = 90, configKeepMinimum = 7, dataRetentionDays = 30, driftAlerts = true, requireRestoreApproval = true,
        })).EnsureSuccessStatusCode();
        app.Graph.Collection("deviceManagement/deviceConfigurations").Clear();

        var plan = await PlanAsync(app, client, snapshot, new { types = new[] { "intune.deviceConfiguration" } });
        var id = plan["plan"]!["id"]!.GetValue<string>();
        var apply = JsonNode.Parse(await (await client.PostAsync($"/api/restore/plans/{id}/apply", null)).Content.ReadAsStringAsync())!;
        Assert.Equal("PendingApproval", apply["status"]!.GetValue<string>());
        Assert.Empty(app.Graph.Collection("deviceManagement/deviceConfigurations"));

        var self = await client.PostAsync($"/api/restore/plans/{id}/approve", null);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);

        // Someone else planned it: now the local developer can approve.
        app.Db(db => db.RestorePlans.Where(p => p.Id == Guid.Parse(id)).ExecuteUpdate(s => s.SetProperty(p => p.CreatedBy, "someone-else@contoso.com")));
        var approved = JsonNode.Parse(await (await client.PostAsync($"/api/restore/plans/{id}/approve", null)).Content.ReadAsStringAsync())!;
        await app.WaitForJobAsync(client, approved["jobId"]!.GetValue<string>());
        Assert.Single(app.Graph.Collection("deviceManagement/deviceConfigurations"));
        var final = JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{id}"))!;
        Assert.Equal("developer@localhost", final["plan"]!["approvedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Excluded_actions_are_not_applied()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        var snapshot = await BackupAsync(app, client, tenant);
        app.Graph.Collection("deviceManagement/deviceConfigurations").Clear();
        app.Graph.Collection("identity/conditionalAccess/namedLocations").Clear();

        var plan = await PlanAsync(app, client, snapshot, new { workloads = new[] { "intune" } });
        var create = Actions(plan, "Create").Single(a => a["type"]!.GetValue<string>() == "intune.deviceConfiguration");
        var id = plan["plan"]!["id"]!.GetValue<string>();
        (await client.PatchAsJsonAsync($"/api/restore/plans/{id}/actions", new { exclude = new[] { create["index"]!.GetValue<int>() } })).EnsureSuccessStatusCode();
        var applied = await ApplyAsync(app, client, JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{id}"))!);
        Assert.Empty(app.Graph.Collection("deviceManagement/deviceConfigurations"));
        Assert.Empty(app.Graph.Collection("identity/conditionalAccess/namedLocations")); // other workload: not selected
        Assert.Equal("Excluded", Actions(applied).Single(a => a["index"]!.GetValue<int>() == create["index"]!.GetValue<int>())["status"]!.GetValue<string>());
    }

    private static async Task DeleteThroughGraphAsync(TestApp app, string path)
    {
        using var http = new HttpClient(app.Graph, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com") };
        (await http.DeleteAsync(path)).EnsureSuccessStatusCode();
    }
}
