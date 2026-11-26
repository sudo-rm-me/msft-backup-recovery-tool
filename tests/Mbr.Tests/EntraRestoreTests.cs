using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 4: bringing back purged Entra objects and re-linking everything that referenced them.</summary>
public class EntraRestoreTests
{
    private const string GlobalReaderRole = "f2ef992c-3afb-46b9-b7cf-a126ee74c451"; // built-in: never in a snapshot

    private sealed record World(TestApp App, HttpClient Client, Mbr.Core.Model.Tenant Tenant, FakeTenantData.Seeded Seeded, string SpId, string AppRoleId);

    private static async Task<(World World, string SnapshotId)> SetUpAsync()
    {
        var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var g = app.Graph;
        g.Add("roleManagement/directory/roleAssignments", new JsonObject
        {
            ["principalId"] = seeded.UserId, ["roleDefinitionId"] = GlobalReaderRole, ["directoryScopeId"] = "/",
        });
        g.Collection("roleManagement/directory/roleDefinitions");
        var sp = g.Add("servicePrincipals", new JsonObject
        {
            ["appId"] = "9f5a9d3e-0000-4000-8000-000000000001", ["displayName"] = "Expenses", ["accountEnabled"] = true, ["appRoleAssignmentRequired"] = true,
        });
        var spId = sp["id"]!.GetValue<string>();
        const string appRoleId = "3c0c5a59-1111-4222-8333-000000000001";
        g.Add($"servicePrincipals/{spId}/appRoleAssignedTo", new JsonObject
        {
            ["principalId"] = seeded.UserId, ["principalType"] = "User", ["appRoleId"] = appRoleId, ["resourceId"] = spId,
            ["principalDisplayName"] = "Adele Vance", ["creationTimestamp"] = "2025-01-01T00:00:00Z",
        });
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        return (new World(app, client, tenant, seeded, spId, appRoleId), job["result"]!["snapshotId"]!.GetValue<string>());
    }

    /// <summary>Simulates Entra removing a purged user's memberships and assignments.</summary>
    private static void PurgeUser(World w)
    {
        var g = w.App.Graph;
        var id = w.Seeded.UserId;
        g.Collection("users").RemoveAll(u => u["id"]!.GetValue<string>() == id);
        g.Collection($"groups/{w.Seeded.GroupId}/members").RemoveAll(m => m["id"]!.GetValue<string>() == id);
        g.Collection("roleManagement/directory/roleAssignments").RemoveAll(a => a["principalId"]!.GetValue<string>() == id);
        g.Collection($"servicePrincipals/{w.SpId}/appRoleAssignedTo").RemoveAll(a => a["principalId"]!.GetValue<string>() == id);
        var ca = g.Collection("identity/conditionalAccess/policies").Single();
        ca["conditions"]!["users"]!["excludeUsers"] = new JsonArray();
    }

    private static async Task<JsonNode> PlanAndApplyAsync(World w, string snapshotId, object request, Action<JsonNode>? inspectPlan = null)
    {
        var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.AsObject();
        payload["snapshotId"] = snapshotId;
        var created = JsonNode.Parse(await (await w.Client.PostAsJsonAsync("/api/restore/plans", payload)).Content.ReadAsStringAsync())!;
        var planJob = await w.App.WaitForJobAsync(w.Client, created["jobId"]!.GetValue<string>());
        Assert.True(planJob["status"]!.GetValue<string>() == "Succeeded", $"{planJob["error"]}");
        var plan = JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        inspectPlan?.Invoke(plan);
        var started = JsonNode.Parse(await (await w.Client.PostAsync($"/api/restore/plans/{created["planId"]}/apply", null)).Content.ReadAsStringAsync())!;
        await w.App.WaitForJobAsync(w.Client, started["jobId"]!.GetValue<string>());
        var applied = JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        return applied;
    }

    private static string Describe(JsonNode plan) => string.Join("\n", plan["actions"]!.AsArray().Select(a =>
        $"{a!["kind"]} {a["type"]} {a["displayName"]} relink={a["isRelink"]} [{a["status"]}] {a["error"]} {a["reason"]} " +
        string.Join("; ", a["verificationIssues"]!.AsArray().Select(v => v!.ToString()))));

    [Fact]
    public async Task Purged_user_is_recreated_and_memberships_roles_and_policies_are_relinked()
    {
        var (w, snapshot) = await SetUpAsync();
        using var app = w.App;
        PurgeUser(w);
        // An unrelated change since the snapshot must survive re-linking.
        var ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        ca["state"] = "disabled";

        var applied = await PlanAndApplyAsync(w, snapshot, new { types = new[] { "entra.user" } }, plan =>
        {
            var acts = plan["actions"]!.AsArray().Select(a => a!).Where(a => a["kind"]!.GetValue<string>() != "Skip").ToList();
            Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "entra.user" && a["kind"]!.GetValue<string>() == "Create");
            foreach (var t in new[] { "entra.group", "entra.conditionalAccessPolicy", "entra.servicePrincipal" })
                Assert.Contains(acts, a => a["type"]!.GetValue<string>() == t && a["kind"]!.GetValue<string>() == "Update" && a["isRelink"]!.GetValue<bool>());
            Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "entra.directoryRoleAssignment" && a["kind"]!.GetValue<string>() == "Create");
            Assert.DoesNotContain(acts, a => a["type"]!.GetValue<string>().StartsWith("intune."));
        });

        var user = app.Graph.Collection("users").Single(u => u["userPrincipalName"]!.GetValue<string>() == "adele@contoso.onmicrosoft.com");
        var newId = user["id"]!.GetValue<string>();
        Assert.NotEqual(w.Seeded.UserId, newId);
        Assert.NotNull(user["passwordProfile"]);

        Assert.Contains(app.Graph.Collection($"groups/{w.Seeded.GroupId}/members"), m => m["id"]!.GetValue<string>() == newId);
        ca = app.Graph.Collection("identity/conditionalAccess/policies").Single();
        Assert.Equal([newId], ca["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("disabled", ca["state"]!.GetValue<string>()); // re-link only adds references back
        var role = Assert.Single(app.Graph.Collection("roleManagement/directory/roleAssignments"));
        Assert.Equal(newId, role["principalId"]!.GetValue<string>());
        Assert.Equal(GlobalReaderRole, role["roleDefinitionId"]!.GetValue<string>());
        var appRole = Assert.Single(app.Graph.Collection($"servicePrincipals/{w.SpId}/appRoleAssignedTo"));
        Assert.Equal(newId, appRole["principalId"]!.GetValue<string>());
        Assert.Equal(w.SpId, appRole["resourceId"]!.GetValue<string>());
        Assert.Equal(w.AppRoleId, appRole["appRoleId"]!.GetValue<string>());

        var failedOrIssues = applied["actions"]!.AsArray().Where(a => a!["verificationIssues"]!.AsArray().Count > 0).ToList();
        Assert.True(failedOrIssues.Count == 0, Describe(applied));
    }

    [Fact]
    public async Task Purged_group_is_recreated_and_assignments_that_used_it_are_relinked()
    {
        var (w, snapshot) = await SetUpAsync();
        using var app = w.App;
        var g = app.Graph;
        g.Collection("groups").RemoveAll(x => x["id"]!.GetValue<string>() == w.Seeded.GroupId);
        var dcAssignments = g.Collection($"deviceManagement/deviceConfigurations/{w.Seeded.DeviceConfigId}/assignments");
        dcAssignments.Clear();
        var ca = g.Collection("identity/conditionalAccess/policies").Single();
        ca["conditions"]!["users"]!["includeGroups"] = new JsonArray();
        // Someone also changed the device configuration since the snapshot; re-linking must keep that.
        g.Collection("deviceManagement/deviceConfigurations").Single()["passwordMinimumLength"] = 14;

        await PlanAndApplyAsync(w, snapshot, new { types = new[] { "entra.group" } });

        var group = g.Collection("groups").Single(x => x["displayName"]!.GetValue<string>() == "All Staff");
        var newId = group["id"]!.GetValue<string>();
        Assert.NotEqual(w.Seeded.GroupId, newId);
        Assert.Contains(g.Collection($"groups/{newId}/members"), m => m["id"]!.GetValue<string>() == w.Seeded.UserId);
        Assert.Equal([newId], g.Collection("identity/conditionalAccess/policies").Single()["conditions"]!["users"]!["includeGroups"]!.AsArray().Select(x => x!.GetValue<string>()));
        var assignment = Assert.Single(g.Collection($"deviceManagement/deviceConfigurations/{w.Seeded.DeviceConfigId}/assignments"));
        Assert.Equal(newId, assignment["target"]!["groupId"]!.GetValue<string>());
        Assert.Equal(14, g.Collection("deviceManagement/deviceConfigurations").Single()["passwordMinimumLength"]!.GetValue<int>());
    }

    [Fact]
    public async Task Synced_users_are_not_recreated_in_the_cloud()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        app.Graph.Add("users", new JsonObject
        {
            ["userPrincipalName"] = "onprem@contoso.com", ["displayName"] = "On-prem user", ["accountEnabled"] = true, ["mailNickname"] = "onprem",
            ["onPremisesSyncEnabled"] = true, ["onPremisesImmutableId"] = "abc==",
        });
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra" } });
        app.Graph.Collection("users").Clear();

        var created = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/restore/plans",
            new { snapshotId = job["result"]!["snapshotId"]!.GetValue<string>(), types = new[] { "entra.user" } })).Content.ReadAsStringAsync())!;
        await app.WaitForJobAsync(client, created["jobId"]!.GetValue<string>());
        var plan = JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        var action = Assert.Single(plan["actions"]!.AsArray());
        Assert.Equal("Skip", action!["kind"]!.GetValue<string>());
        Assert.Contains("on-premises", action["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Recycle_bin_lists_deleted_objects_and_undeletes_them()
    {
        var (w, _) = await SetUpAsync();
        using var app = w.App;
        using (var http = new HttpClient(app.Graph, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com") })
        {
            (await http.DeleteAsync($"/v1.0/users/{w.Seeded.UserId}")).EnsureSuccessStatusCode();
            (await http.DeleteAsync($"/v1.0/groups/{w.Seeded.GroupId}")).EnsureSuccessStatusCode();
        }

        var deleted = JsonNode.Parse(await w.Client.GetStringAsync($"/api/tenants/{w.Tenant.Id}/entra/deleted"))!.AsArray();
        Assert.Equal(2, deleted.Count);
        var user = deleted.Single(d => d!["kind"]!.GetValue<string>() == "user")!;
        Assert.Equal("adele@contoso.onmicrosoft.com", user["userPrincipalName"]!.GetValue<string>());
        Assert.NotNull(user["snapshotItemId"]); // in the latest snapshot: can also be restored from there after a purge

        var resp = await w.Client.PostAsJsonAsync($"/api/tenants/{w.Tenant.Id}/entra/deleted/restore", new { ids = new[] { w.Seeded.UserId } });
        var job = await app.WaitForJobAsync(w.Client, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>());
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.Contains(app.Graph.Collection("users"), u => u["id"]!.GetValue<string>() == w.Seeded.UserId);
        var left = JsonNode.Parse(await w.Client.GetStringAsync($"/api/tenants/{w.Tenant.Id}/entra/deleted?kind=user"))!.AsArray();
        Assert.Empty(left);
    }
}
