using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Json;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

public class JsonDiffTests
{
    [Fact]
    public void Reports_property_changes_additions_and_removals()
    {
        var a = JsonNode.Parse("""{"name":"x","len":12,"old":true,"nested":{"v":1}}""");
        var b = JsonNode.Parse("""{"name":"x","len":14,"new":"y","nested":{"v":2}}""");
        var changes = JsonDiff.Compare(a, b);
        Assert.Contains(changes, c => c.Path == "len" && c.Kind == DiffKind.Changed);
        Assert.Contains(changes, c => c.Path == "old" && c.Kind == DiffKind.Removed);
        Assert.Contains(changes, c => c.Path == "new" && c.Kind == DiffKind.Added);
        Assert.Contains(changes, c => c.Path == "nested.v" && c.Kind == DiffKind.Changed);
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void Scalar_arrays_compare_as_sets_and_object_arrays_by_key()
    {
        var a = JsonNode.Parse("""{"ids":["1","2"],"assignments":[{"target":{"groupId":"g1"},"mode":"include"},{"target":{"groupId":"g2"},"mode":"include"}]}""");
        var b = JsonNode.Parse("""{"ids":["2","1","3"],"assignments":[{"target":{"groupId":"g2"},"mode":"exclude"},{"target":{"groupId":"g1"},"mode":"include"}]}""");
        var changes = JsonDiff.Compare(a, b);
        Assert.Contains(changes, c => c.Path == "ids[]" && c.Kind == DiffKind.Added && c.After!.GetValue<string>() == "3");
        Assert.Contains(changes, c => c.Path == "assignments[g2].mode" && c.Kind == DiffKind.Changed);
        Assert.Equal(2, changes.Count);
    }
}

public class DiffAndDriftTests
{
    [Fact]
    public async Task Diff_between_snapshots_lists_changes_and_item_detail()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var first = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        app.Graph.Collection("deviceManagement/deviceConfigurations")[0]["passwordMinimumLength"] = 16;
        app.Graph.Collection("identity/conditionalAccess/namedLocations").Clear();
        var second = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });

        // The second snapshot only has Intune: Entra types must not show as removed.
        var diff = JsonNode.Parse(await client.GetStringAsync($"/api/diff?from={first["result"]!["snapshotId"]}&to={second["result"]!["snapshotId"]}"))!;
        var entries = diff["summary"]!["entries"]!.AsArray();
        var entry = Assert.Single(entries);
        Assert.Equal("Modified", entry!["change"]!.GetValue<string>());
        Assert.Equal(seeded.DeviceConfigId, entry["toObjectId"]!.GetValue<string>());

        var item = JsonNode.Parse(await client.GetStringAsync($"/api/diff/item?fromItem={entry["fromItemId"]}&toItem={entry["toItemId"]}"))!;
        var change = Assert.Single(item["changes"]!.AsArray());
        Assert.Equal("passwordMinimumLength", change!["path"]!.GetValue<string>());
        Assert.Equal(12, change["before"]!.GetValue<int>());
        Assert.Equal(16, change["after"]!.GetValue<int>());
    }

    [Fact]
    public async Task Keywords_and_cross_tenant_comparison_match_by_name()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var a = await app.AddTenantAsync("Contoso");
        var b = await app.AddTenantAsync("Fabrikam");
        FakeTenantData.Seed(app.Graph); // both tenants read the same fake → same names, different snapshot IDs
        var sa = await app.RunJobAsync(client, "backup.config", a.Id, new { workloads = new[] { "intune" } });
        app.Graph.Collection("deviceManagement/assignmentFilters").Add(new JsonObject { ["id"] = "f-extra", ["displayName"] = "Only in Fabrikam", ["platform"] = "iOS", ["rule"] = "x" });
        var sb = await app.RunJobAsync(client, "backup.config", b.Id, new { workloads = new[] { "intune" } });

        var diff = JsonNode.Parse(await client.GetStringAsync($"/api/diff?from={sa["result"]!["snapshotId"]}&to={sb["result"]!["snapshotId"]}"))!["summary"]!;
        Assert.True(diff["crossTenant"]!.GetValue<bool>());
        var added = Assert.Single(diff["entries"]!.AsArray());
        Assert.Equal("Only in Fabrikam", added!["displayName"]!.GetValue<string>());

        var latest = JsonNode.Parse(await client.GetStringAsync($"/api/diff?from=latest&to=latest&tenantId={a.Id}"))!["summary"]!;
        Assert.Equal(0, latest["entries"]!.AsArray().Count);
    }

    [Fact]
    public async Task Drift_check_compares_live_with_baseline_and_changes_alert()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        var baseline = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        (await client.PatchAsJsonAsync($"/api/snapshots/{baseline["result"]!["snapshotId"]}", new { isBaseline = true })).EnsureSuccessStatusCode();
        Assert.Empty(app.Notifier.Sent); // first backup: nothing to compare with

        app.Graph.Collection("identity/conditionalAccess/policies")[0]["state"] = "disabled";
        var drift = await app.RunJobAsync(client, "config.drift", tenant.Id);
        Assert.Equal(1, drift["result"]!["modified"]!.GetValue<int>());

        var alert = Assert.Single(app.Notifier.Sent);
        Assert.Contains("Require MFA for all staff", alert.Message);
        Assert.Contains("Deviation from baseline: 1", alert.Message);
    }
}
