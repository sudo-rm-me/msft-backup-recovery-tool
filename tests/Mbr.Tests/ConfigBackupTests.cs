using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Tests;

public class CatalogTests
{
    [Fact]
    public void Catalog_loads_and_orders_dependencies_first()
    {
        var registry = new ResourceRegistry([]);
        var order = registry.Ordered().Select(h => h.Definition.Type).ToList();
        Assert.Contains("intune.deviceConfiguration", order);
        Assert.True(order.IndexOf("entra.group") < order.IndexOf("entra.conditionalAccessPolicy"));
        Assert.True(order.IndexOf("entra.namedLocation") < order.IndexOf("entra.conditionalAccessPolicy"));
        Assert.True(order.IndexOf("intune.assignmentFilter") < order.IndexOf("intune.deviceConfiguration"));
        Assert.True(order.IndexOf("entra.user") < order.IndexOf("entra.group"));
    }

    [Fact]
    public void Every_reference_and_dependency_points_at_a_known_type()
    {
        var registry = new ResourceRegistry([]);
        var types = registry.Handlers.Select(h => h.Definition.Type).ToHashSet();
        foreach (var d in registry.Handlers.Select(h => h.Definition))
        {
            foreach (var dep in d.DependsOn) Assert.True(types.Contains(dep), $"{d.Type} depends on unknown {dep}");
            foreach (var r in d.References) Assert.True(r.Type == "directoryObject" || types.Contains(r.Type), $"{d.Type} references unknown {r.Type}");
        }
    }

    [Fact]
    public void JsonPath_reads_and_rewrites_nested_arrays()
    {
        var o = JsonNode.Parse("""{"a":{"ids":["1","2","All"]},"list":[{"t":{"g":"1"}},{"t":{"g":"3"}}]}""")!;
        Assert.Equal(["1", "2", "All"], JsonPath.GetStrings(o, "a.ids[*]"));
        Assert.Equal(["1", "3"], JsonPath.GetStrings(o, "list[*].t.g"));
        JsonPath.Rewrite(o, "a.ids[*]", s => s == "2" ? null : s == "1" ? "X" : s);
        JsonPath.Rewrite(o, "list[*].t.g", s => s + "!");
        Assert.Equal("""{"a":{"ids":["X","All"]},"list":[{"t":{"g":"1!"}},{"t":{"g":"3!"}}]}""", o.ToJsonString());
    }

    [Fact]
    public void Canonical_json_is_order_independent()
    {
        var a = JsonNode.Parse("""{"b":1,"a":{"y":2,"x":1}}""")!;
        var b = JsonNode.Parse("""{"a":{"x":1,"y":2},"b":1}""")!;
        Assert.Equal(CanonicalJson.ToBytes(a), CanonicalJson.ToBytes(b));
    }
}

public class ConfigBackupTests
{
    [Fact]
    public async Task Backup_captures_normalized_objects_with_children()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);

        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        var snapshotId = job["result"]!["snapshotId"]!.GetValue<string>();

        var detail = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}"))!;
        Assert.Equal("Completed", detail["snapshot"]!["status"]!.GetValue<string>());
        var types = detail["types"]!.AsArray().Select(t => t!["resourceType"]!.GetValue<string>()).ToList();
        Assert.Contains("entra.conditionalAccessPolicy", types);
        Assert.Contains("intune.settingsCatalogPolicy", types);
        Assert.Contains("entra.authorizationPolicy", types);

        var items = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items?type=intune.deviceConfiguration"))!;
        var itemId = items["items"]![0]!["id"]!.GetValue<long>();
        var item = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items/{itemId}"))!;
        var content = item["content"]!;
        Assert.Equal(seeded.DeviceConfigId, item["objectId"]!.GetValue<string>());
        Assert.Null(content["id"]);
        Assert.Null(content["version"]);
        Assert.Null(content["lastModifiedDateTime"]);
        Assert.Equal("#microsoft.graph.windows10GeneralConfiguration", content["@odata.type"]!.GetValue<string>());
        var assignment = content["assignments"]![0]!;
        Assert.Null(assignment["id"]);
        Assert.Equal(seeded.GroupId, assignment["target"]!["groupId"]!.GetValue<string>());

        var sc = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items?type=intune.settingsCatalogPolicy"))!["items"]![0]!;
        Assert.Equal("Defender settings", sc["displayName"]!.GetValue<string>());
        var scContent = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items/{sc["id"]}"))!["content"]!;
        Assert.Single(scContent["settings"]!.AsArray());
        Assert.Null(scContent["settingCount"]);

        var members = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items?type=entra.group"))!["items"]![0]!;
        var groupContent = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshotId}/items/{members["id"]}"))!["content"]!;
        Assert.Equal(seeded.UserId, groupContent["members"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Second_backup_detects_changes_and_dedups_storage()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        var objectsAfterFirst = app.Db(db => db.StoredObjects.Count());

        var unchanged = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        Assert.Equal(0, unchanged["result"]!["modified"]!.GetValue<int>());
        Assert.Equal(objectsAfterFirst, app.Db(db => db.StoredObjects.Count()));

        app.Graph.Collection("deviceManagement/deviceConfigurations")[0]["passwordMinimumLength"] = 14;
        app.Graph.Add("deviceManagement/assignmentFilters", new JsonObject { ["displayName"] = "New filter", ["platform"] = "iOS", ["rule"] = "x" });
        app.Graph.Collection("deviceManagement/configurationPolicies").Clear();

        var changed = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        var r = changed["result"]!;
        Assert.Equal(1, r["added"]!.GetValue<int>());
        Assert.Equal(1, r["modified"]!.GetValue<int>());
        Assert.Equal(1, r["removed"]!.GetValue<int>());
        Assert.Contains(r["changes"]!.AsArray(), c => c!["objectId"]!.GetValue<string>() == seeded.DeviceConfigId && c["change"]!.GetValue<string>() == "Modified");

        var history = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/objects/intune.deviceConfiguration/{seeded.DeviceConfigId}/history"))!.AsArray();
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task Access_denied_makes_snapshot_partial_and_unlicensed_is_informational()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        app.Graph.On(HttpMethod.Get, "/beta/deviceManagement/deviceConfigurations", (_, _) =>
            FakeGraph.Error(HttpStatusCode.Forbidden, "Forbidden", "Application is not authorized to perform this operation."));
        app.Graph.On(HttpMethod.Get, "/beta/deviceManagement/windowsDriverUpdateProfiles", (_, _) =>
            FakeGraph.Error(HttpStatusCode.BadRequest, "BadRequest", "Request not applicable to target tenant."));

        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        Assert.Equal("PartiallySucceeded", job["status"]!.GetValue<string>());
        var detail = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{job["result"]!["snapshotId"]}"))!;
        var issues = detail["issues"]!.AsArray();
        Assert.Contains(issues, i => i!["resourceType"]!.GetValue<string>() == "intune.deviceConfiguration" && i["severity"]!.GetValue<string>() == "Warning");
        Assert.Contains(issues, i => i!["resourceType"]!.GetValue<string>() == "intune.driverUpdateProfile" && i["severity"]!.GetValue<string>() == "Info");
    }

    [Fact]
    public async Task Export_produces_zip_with_manifest_and_decrypted_json()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra" } });
        var bytes = await client.GetByteArrayAsync($"/api/snapshots/{job["result"]!["snapshotId"]}/export");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        var ca = zip.Entries.First(e => e.FullName.StartsWith("entra/entra.conditionalAccessPolicy/"));
        using var reader = new StreamReader(ca.Open());
        Assert.Contains("Require MFA", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Retention_prunes_old_snapshots_and_garbage_collects()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        (await client.PutAsJsonAsync($"/api/tenants/{tenant.Id}/settings", new { configRetentionDays = 1, configKeepMinimum = 1, dataRetentionDays = 30, driftAlerts = false })).EnsureSuccessStatusCode();

        await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        app.Graph.Collection("deviceManagement/deviceConfigurations")[0]["passwordMinimumLength"] = 20;
        var second = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        Assert.Equal(2, app.Db(db => db.Snapshots.Count()));

        using var scope = app.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        var t = app.Db(db => db.Tenants.Single());
        var r = await retention.RunAsync(t, null, CancellationToken.None, DateTimeOffset.UtcNow.AddDays(5));
        Assert.Equal(1, r.SnapshotsDeleted);
        Assert.Equal(1, r.ObjectsDeleted); // only the old version of the modified profile is unreferenced
        Assert.Equal(second["result"]!["snapshotId"]!.GetValue<string>(), app.Db(db => db.Snapshots.Single().Id.ToString()));
    }

    [Fact]
    public async Task Baseline_snapshots_survive_retention()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        (await client.PutAsJsonAsync($"/api/tenants/{tenant.Id}/settings", new { configRetentionDays = 1, configKeepMinimum = 1, dataRetentionDays = 30, driftAlerts = false })).EnsureSuccessStatusCode();
        var first = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });
        (await client.PatchAsJsonAsync($"/api/snapshots/{first["result"]!["snapshotId"]}", new { isBaseline = true })).EnsureSuccessStatusCode();
        await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "intune" } });

        using var scope = app.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(app.Db(db => db.Tenants.Single()), null, CancellationToken.None, DateTimeOffset.UtcNow.AddDays(5));
        Assert.Equal(0, r.SnapshotsDeleted);
    }
}
