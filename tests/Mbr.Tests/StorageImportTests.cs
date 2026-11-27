using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Losing the database: a new deployment with the same master key and storage rebuilds its index from storage.</summary>
public class StorageImportTests
{
    private static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    [Fact]
    public async Task Tenants_snapshots_and_files_are_rebuilt_from_storage_after_the_database_is_lost()
    {
        using var before = new TestApp();
        var client = before.CreateClient();
        var tenant = await before.AddTenantAsync();
        var seeded = FakeTenantData.Seed(before.Graph);
        var drive = before.Graph.AddDrive($"users/{seeded.UserId}", "OneDrive");
        drive.PutFile("/Reports/q1.txt", "first quarter");
        var snapshot = await before.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "entra", "intune" } });
        var snapshotId = snapshot["result"]!["snapshotId"]!.GetValue<string>();
        await before.RunJobAsync(client, "backup.data", tenant.Id, new { kinds = new[] { "OneDrive" } });
        drive.PutFile("/Reports/q1.txt", "first quarter, revised");
        drive.PutFile("/Reports/q2.txt", "second quarter");
        await before.RunJobAsync(client, "backup.data", tenant.Id, new { kinds = new[] { "OneDrive" } });
        var expectedItems = before.Db(db => db.SnapshotItems.Count(i => i.SnapshotId == Guid.Parse(snapshotId)));
        var expectedVersions = before.Db(db => db.DataItemVersions.Count(v => v.TenantId == tenant.Id));

        // A new deployment: same master key and storage, empty database.
        using var after = new TestApp();
        Directory.CreateDirectory(after.DataDir);
        File.Copy(Path.Combine(before.DataDir, "master.key"), Path.Combine(after.DataDir, "master.key"));
        CopyDir(Path.Combine(before.DataDir, "storage"), Path.Combine(after.DataDir, "storage"));
        var c2 = after.CreateClient();
        Assert.Empty(after.Db(db => db.Tenants.ToList()));
        var target = JsonNode.Parse(await c2.GetStringAsync("/api/storage-targets"))!.AsArray().Single()!["id"]!.GetValue<string>();

        var resp = await c2.PostAsync($"/api/storage-targets/{target}/import", null);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var job = await after.WaitForJobAsync(c2, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>());
        Assert.True(job["status"]!.GetValue<string>() == "Succeeded", job.ToJsonString());
        Assert.Equal(1, job["result"]!["tenantsCreated"]!.GetValue<int>());

        // The tenant comes back with its ID and directory, waiting for an operator to check it.
        var imported = after.Db(db => db.Tenants.Single());
        Assert.Equal(tenant.Id, imported.Id);
        Assert.Equal(tenant.DirectoryId, imported.DirectoryId);
        Assert.Equal(Mbr.Core.Model.TenantStatus.PendingConsent, imported.Status);

        // Config: the snapshot and its content are readable again.
        var detail = JsonNode.Parse(await c2.GetStringAsync($"/api/snapshots/{snapshotId}"))!;
        Assert.Equal("Completed", detail["snapshot"]!["status"]!.GetValue<string>());
        Assert.Equal(expectedItems, after.Db(db => db.SnapshotItems.Count(i => i.SnapshotId == Guid.Parse(snapshotId))));
        var group = JsonNode.Parse(await c2.GetStringAsync($"/api/snapshots/{snapshotId}/items?type=entra.group"))!["items"]![0]!;
        var groupContent = JsonNode.Parse(await c2.GetStringAsync($"/api/snapshots/{snapshotId}/items/{group["id"]}"))!["content"]!;
        Assert.Equal("All Staff", groupContent["displayName"]!.GetValue<string>());

        // Files: every version, including the superseded one, with its content.
        Assert.Equal(expectedVersions, after.Db(db => db.DataItemVersions.Count(v => v.TenantId == tenant.Id)));
        var q1 = after.Db(db => db.DataItemVersions.Where(v => v.Name == "q1.txt").OrderBy(v => v.CapturedAt).ToList());
        Assert.Equal(2, q1.Count);
        Assert.NotNull(q1[0].SupersededAt);
        Assert.Null(q1[1].SupersededAt);
        Assert.Equal("first quarter", await c2.GetStringAsync($"/api/data/versions/{q1[0].Id}/content"));
        Assert.Equal("first quarter, revised", await c2.GetStringAsync($"/api/data/versions/{q1[1].Id}/content"));
        var source = after.Db(db => db.DataSources.Single());
        Assert.Equal(2, source.ItemCount);

        // Running it again adds nothing.
        var again = await after.RunJobAsync(c2, "storage.import", null, new { storageTargetId = target });
        Assert.Equal(0, again["result"]!["snapshotsImported"]!.GetValue<int>());
        Assert.Equal(0, again["result"]!["versionsImported"]!.GetValue<int>());
        Assert.Equal(1, again["result"]!["snapshotsKnown"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_tenant_import_only_looks_at_that_tenant_and_unknown_targets_are_refused()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var target = JsonNode.Parse(await client.GetStringAsync("/api/storage-targets"))!.AsArray().Single()!["id"]!.GetValue<string>();
        var tenant = await app.AddTenantAsync();
        // Nothing has been written for this tenant yet, so there's nothing of it in storage.
        var job = await app.RunJobAsync(client, "storage.import", tenant.Id, new { storageTargetId = target });
        Assert.Equal(0, job["result"]!["tenantsFound"]!.GetValue<int>());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.PostAsync($"/api/storage-targets/{Guid.NewGuid()}/import", null)).StatusCode);
    }
}
