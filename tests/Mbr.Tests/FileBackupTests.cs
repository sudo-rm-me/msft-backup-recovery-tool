using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Demo;
using Mbr.Infrastructure.Files;
using Mbr.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Tests;

/// <summary>Phase 5: OneDrive / SharePoint file backup.</summary>
public class FileBackupTests
{
    private sealed record World(TestApp App, HttpClient Client, Tenant Tenant, FakeDrive Drive, string UserId);

    private static async Task<World> SetUpAsync()
    {
        var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        var drive = app.Graph.AddDrive($"users/{seeded.UserId}", "OneDrive");
        drive.PutFile("/Reports/q1.docx", "Q1 numbers v1");
        drive.PutFile("/notes.txt", "hello");
        return new World(app, client, tenant, drive, seeded.UserId);
    }

    private static Task<JsonNode> BackupAsync(World w) => w.App.RunJobAsync(w.Client, "backup.data", w.Tenant.Id, new { kinds = new[] { "OneDrive" } });

    private static async Task<(string SourceId, string ContainerId)> SourceAsync(World w)
    {
        var sources = JsonNode.Parse(await w.Client.GetStringAsync($"/api/tenants/{w.Tenant.Id}/data/sources"))!.AsArray();
        var source = Assert.Single(sources)!;
        var detail = JsonNode.Parse(await w.Client.GetStringAsync($"/api/data/sources/{source["id"]}"))!;
        return (source["id"]!.GetValue<string>(), detail["containers"]![0]!["id"]!.GetValue<string>());
    }

    private static async Task<JsonArray> ChildrenAsync(World w, string containerId, string? parent = null, DateTimeOffset? at = null, bool deleted = false)
    {
        var q = $"/api/data/containers/{containerId}/children?deleted={deleted}";
        if (parent != null) q += $"&parent={parent}";
        if (at != null) q += $"&at={Uri.EscapeDataString(at.Value.ToString("o"))}";
        return JsonNode.Parse(await w.Client.GetStringAsync(q))!["items"]!.AsArray();
    }

    private static async Task<string> ContentAsync(World w, JsonNode version) =>
        await w.Client.GetStringAsync($"/api/data/versions/{version["id"]}/content");

    private static JsonNode Named(JsonArray items, string name) => items.Single(i => i!["name"]!.GetValue<string>() == name)!;

    [Fact]
    public async Task Full_then_incremental_backup_keeps_versions_and_point_in_time_views()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var first = await BackupAsync(w);
        Assert.Equal("Succeeded", first["status"]!.GetValue<string>());
        Assert.Equal(2, w.Drive.Downloads);
        var (sourceId, containerId) = await SourceAsync(w);
        var root = await ChildrenAsync(w, containerId);
        Assert.Equal(["Reports", "notes.txt"], root.Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal("hello", await ContentAsync(w, Named(root, "notes.txt")));
        var reports = await ChildrenAsync(w, containerId, Named(root, "Reports")["itemId"]!.GetValue<string>());
        Assert.Equal("/Reports", Named(reports, "q1.docx")["path"]!.GetValue<string>());
        var afterFirst = DateTimeOffset.UtcNow;
        await Task.Delay(50);

        // Change content, rename, add.
        w.Drive.PutFile("/notes.txt", "hello again");
        w.Drive.Rename("/Reports/q1.docx", "q1-final.docx");
        w.Drive.PutFile("/Reports/q2.docx", "Q2 numbers");
        var second = await BackupAsync(w);
        Assert.Equal("Succeeded", second["status"]!.GetValue<string>());
        Assert.Equal(4, w.Drive.Downloads); // notes + q2; the rename is metadata-only

        root = await ChildrenAsync(w, containerId);
        Assert.Equal("hello again", await ContentAsync(w, Named(root, "notes.txt")));
        reports = await ChildrenAsync(w, containerId, Named(root, "Reports")["itemId"]!.GetValue<string>());
        Assert.Equal(["q1-final.docx", "q2.docx"], reports.Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal("Q1 numbers v1", await ContentAsync(w, Named(reports, "q1-final.docx")));

        // As of the first backup: old content and old name.
        var then = await ChildrenAsync(w, containerId, at: afterFirst);
        Assert.Equal("hello", await ContentAsync(w, Named(then, "notes.txt")));
        var thenReports = await ChildrenAsync(w, containerId, Named(then, "Reports")["itemId"]!.GetValue<string>(), afterFirst);
        Assert.Equal(["q1.docx"], thenReports.Select(i => i!["name"]!.GetValue<string>()));

        var notesId = Named(root, "notes.txt")["itemId"]!.GetValue<string>();
        var history = JsonNode.Parse(await w.Client.GetStringAsync($"/api/data/containers/{containerId}/items/{notesId}/history"))!.AsArray();
        Assert.Equal(2, history.Count);

        var source = JsonNode.Parse(await w.Client.GetStringAsync($"/api/data/sources/{sourceId}"))!["source"]!;
        Assert.Equal(3, source["itemCount"]!.GetValue<long>());
        Assert.Equal("Succeeded", source["lastStatus"]!.GetValue<string>());
        Assert.Contains(app.Db(db => db.AuditEntries.Select(a => a.Action).ToList()), a => a == "data.downloaded");
    }

    [Fact]
    public async Task Deleted_folder_shows_as_deleted_and_is_browsable_as_of_before()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        await BackupAsync(w);
        var (_, containerId) = await SourceAsync(w);
        var before = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        w.Drive.Delete("/Reports");
        await BackupAsync(w);

        var root = await ChildrenAsync(w, containerId);
        Assert.Equal(["notes.txt"], root.Select(i => i!["name"]!.GetValue<string>()));
        var withDeleted = await ChildrenAsync(w, containerId, deleted: true);
        Assert.NotNull(Named(withDeleted, "Reports")["deletedAt"]);
        var then = await ChildrenAsync(w, containerId, at: before);
        var reports = Named(then, "Reports");
        var inside = await ChildrenAsync(w, containerId, reports["itemId"]!.GetValue<string>(), before);
        Assert.Equal("Q1 numbers v1", await ContentAsync(w, Named(inside, "q1.docx")));
    }

    [Fact]
    public async Task Interrupted_sync_resumes_without_downloading_again()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        for (var i = 0; i < 6; i++) w.Drive.PutFile($"/bulk/file{i}.txt", $"content {i}");
        app.Graph.PageSize = 3;
        var failSecondPage = true;
        app.Graph.Routes.Insert(0, (r => failSecondPage && r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/root/delta") && r.RequestUri.Query.Contains("skip=3"),
            (_, _) => { failSecondPage = false; return InMemoryGraph.Error(HttpStatusCode.InternalServerError, "generalException", "boom"); }));

        // First run is interrupted on the second page (run directly, so the job queue's automatic retry doesn't hide it).
        using (var scope = app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<DataBackupService>();
            var tenant = scope.ServiceProvider.GetRequiredService<Mbr.Infrastructure.Data.MbrDbContext>().Tenants.Single(t => t.Id == w.Tenant.Id);
            var (_, _, failedSources) = await svc.RunAsync(tenant, null, [DataSourceKind.OneDrive], null, null, CancellationToken.None);
            Assert.Equal(1, failedSources);
        }
        Assert.Equal(1, w.Drive.Downloads); // root, Reports, q1.docx were on the first page
        Assert.NotNull(app.Db(db => db.DataContainers.Single().ResumeLink));

        var second = await BackupAsync(w);
        Assert.Equal("Succeeded", second["status"]!.GetValue<string>());
        Assert.Equal(8, w.Drive.Downloads); // every file exactly once
        Assert.Null(app.Db(db => db.DataContainers.Single().ResumeLink));
        Assert.Equal(8, app.Db(db => db.DataItemVersions.Count(v => !v.IsFolder && v.SupersededAt == null && v.DeletedAt == null)));
    }

    [Fact]
    public async Task Unreadable_files_are_retried_on_the_next_run()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var locked = w.Drive.IdOf("/notes.txt");
        w.Drive.FailDownload = id => id == locked;
        var first = await BackupAsync(w);
        Assert.Equal("PartiallySucceeded", first["status"]!.GetValue<string>());
        Assert.Equal(1, first["result"]!["failedItems"]!.GetValue<int>());

        w.Drive.FailDownload = _ => false;
        var second = await BackupAsync(w); // nothing changed in the drive, but the locked file is retried
        Assert.Equal("Succeeded", second["status"]!.GetValue<string>());
        var (_, containerId) = await SourceAsync(w);
        Assert.Equal("hello", await ContentAsync(w, Named(await ChildrenAsync(w, containerId), "notes.txt")));
    }

    [Fact]
    public async Task Expired_delta_token_triggers_full_resync_that_detects_deletions()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        await BackupAsync(w);
        w.Drive.Delete("/notes.txt");
        w.Drive.MinimumToken = long.MaxValue; // every saved token is now expired
        var downloads = w.Drive.Downloads;
        var r = await BackupAsync(w);
        Assert.Equal("Succeeded", r["status"]!.GetValue<string>());
        Assert.Equal(downloads, w.Drive.Downloads); // unchanged files aren't downloaded again
        var (_, containerId) = await SourceAsync(w);
        Assert.DoesNotContain(await ChildrenAsync(w, containerId), i => i!["name"]!.GetValue<string>() == "notes.txt");
        Assert.Contains(await ChildrenAsync(w, containerId, deleted: true), i => i!["name"]!.GetValue<string>() == "notes.txt");
    }

    [Fact]
    public async Task Large_files_are_chunked_and_identical_content_is_stored_once()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var big = new byte[DriveBackup.ChunkSize + 1000];
        new Random(42).NextBytes(big);
        w.Drive.PutFile("/big.bin", big);
        w.Drive.PutFile("/copy-of-big.bin", big);
        await BackupAsync(w);
        var versions = app.Db(db => db.DataItemVersions.Where(v => v.Name.EndsWith(".bin")).ToList());
        Assert.All(versions, v => Assert.Equal(2, v.Chunks.Split(',').Length));
        Assert.Equal(versions[0].Chunks, versions[1].Chunks);
        var (_, containerId) = await SourceAsync(w);
        var bytes = await w.Client.GetByteArrayAsync($"/api/data/versions/{versions[0].Id}/content");
        Assert.Equal(big, bytes);
        var chunkHashes = versions[0].Chunks.Split(',');
        Assert.Equal(2, app.Db(db => db.StoredObjects.Count(o => chunkHashes.Contains(o.Hash))));
    }

    [Fact]
    public async Task Retention_prunes_old_versions_and_their_chunks_but_keeps_current_ones()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        await BackupAsync(w);
        w.Drive.PutFile("/notes.txt", "second version");
        await BackupAsync(w);
        var oldChunk = app.Db(db => db.DataItemVersions.Single(v => v.Name == "notes.txt" && v.SupersededAt != null).Chunks);

        using (var scope = app.Services.CreateScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
            var tenant = scope.ServiceProvider.GetRequiredService<Mbr.Infrastructure.Data.MbrDbContext>().Tenants.Single(t => t.Id == w.Tenant.Id);
            var result = await retention.RunAsync(tenant, null, CancellationToken.None, DateTimeOffset.UtcNow.AddDays(31));
            Assert.Equal(1, result.DataVersionsPruned);
        }
        Assert.Equal(0, app.Db(db => db.DataItemVersions.Count(v => v.SupersededAt != null)));
        Assert.False(app.Db(db => db.StoredObjects.Any(o => o.Hash == oldChunk)));
        var (_, containerId) = await SourceAsync(w);
        Assert.Equal("second version", await ContentAsync(w, Named(await ChildrenAsync(w, containerId), "notes.txt")));
    }

    [Fact]
    public async Task SharePoint_sites_are_discovered_with_all_their_libraries()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        app.Graph.Add("sites", new JsonObject { ["id"] = "contoso.sharepoint.com,1,2", ["displayName"] = "Finance", ["webUrl"] = "https://contoso.sharepoint.com/sites/finance" });
        app.Graph.Add("sites", new JsonObject { ["id"] = "contoso-my.sharepoint.com,3,4", ["displayName"] = "Adele", ["webUrl"] = "https://contoso-my.sharepoint.com/personal/adele" });
        app.Graph.On(HttpMethod.Get, "/v1.0/sites/getAllSites", (_, _) => InMemoryGraph.Json(new JsonObject
        {
            ["value"] = new JsonArray(app.Graph.Collection("sites").Select(s => (JsonNode)s.DeepClone()).ToArray()),
        }));
        app.Graph.AddDrive("sites/contoso.sharepoint.com,1,2", "Documents").PutFile("/budget.xlsx", "1,2,3");
        app.Graph.AddDrive("sites/contoso.sharepoint.com,1,2", "Contracts").PutFile("/acme.pdf", "signed");
        app.Graph.Collection("users");

        var job = await app.RunJobAsync(client, "backup.data", tenant.Id, new { kinds = new[] { "SharePointSite" } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        var sources = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/data/sources"))!.AsArray();
        var site = Assert.Single(sources)!; // the personal site is skipped
        Assert.Equal("Finance", site["displayName"]!.GetValue<string>());
        var detail = JsonNode.Parse(await client.GetStringAsync($"/api/data/sources/{site["id"]}"))!;
        Assert.Equal(["Contracts", "Documents"], detail["containers"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(2, site["itemCount"]!.GetValue<long>());
        var search = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/data/search?q=acme"))!.AsArray();
        Assert.Equal("acme.pdf", search.Single()!["item"]!["name"]!.GetValue<string>());
    }
}
