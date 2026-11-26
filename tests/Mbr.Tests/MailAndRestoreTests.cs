using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Model;
using Mbr.Infrastructure.Demo;
using Mbr.Infrastructure.Files;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 6: Exchange mailbox backup, and restoring mail, events and files.</summary>
public class MailAndRestoreTests
{
    private sealed record World(TestApp App, HttpClient Client, Tenant Tenant, FakeTenantData.Seeded Seeded);

    private static async Task<World> SetUpAsync()
    {
        var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        app.Graph.Collection("users").Single()["mail"] = "adele@contoso.onmicrosoft.com";
        return new World(app, client, tenant, seeded);
    }

    private static Task<JsonNode> BackupAsync(World w, string kind) => w.App.RunJobAsync(w.Client, "backup.data", w.Tenant.Id, new { kinds = new[] { kind } });

    private static async Task<JsonNode> SourceAsync(World w, string kind, string? name = null)
    {
        var sources = JsonNode.Parse(await w.Client.GetStringAsync($"/api/tenants/{w.Tenant.Id}/data/sources?kind={kind}"))!.AsArray();
        var s = name is null ? sources.Single()! : sources.Single(x => x!["displayName"]!.GetValue<string>() == name)!;
        return JsonNode.Parse(await w.Client.GetStringAsync($"/api/data/sources/{s["id"]}"))!;
    }

    private static string Container(JsonNode source, string name) =>
        source["containers"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();

    private static async Task<JsonArray> ChildrenAsync(World w, string containerId, string? parent = null, bool deleted = false) =>
        JsonNode.Parse(await w.Client.GetStringAsync(
            $"/api/data/containers/{containerId}/children?deleted={deleted}{(parent is null ? "" : $"&parent={Uri.EscapeDataString(parent)}")}"))!["items"]!.AsArray();

    private static JsonNode Named(JsonArray items, string name) => items.Single(i => i!["name"]!.GetValue<string>() == name)!;

    private static async Task<JsonNode> RestoreAsync(World w, object request)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/tenants/{w.Tenant.Id}/data/restore", request);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var jobId = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>();
        return await w.App.WaitForJobAsync(w.Client, jobId);
    }

    // ------------------------------------------------------------------ mailbox backup

    [Fact]
    public async Task Mailbox_backup_tracks_new_moved_and_deleted_messages_without_downloading_them_again()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var mb = app.Graph.AddMailbox(w.Seeded.UserId);
        mb.AddFolder("/Inbox/Projects");
        var welcome = mb.AddMessage("/Inbox", "Welcome aboard", "Glad you're here");
        var plan = mb.AddMessage("/Inbox/Projects", "Project plan", "See attached", attachments: ("plan.pdf", Encoding.UTF8.GetBytes("PDF")));
        app.Graph.Collection($"users/{w.Seeded.UserId}/contacts").Add(new JsonObject { ["id"] = "c1", ["changeKey"] = "1", ["displayName"] = "Lee Gu", ["emailAddresses"] = new JsonArray(new JsonObject { ["address"] = "lee@contoso.com" }) });

        var first = await BackupAsync(w, "Mailbox");
        Assert.Equal("Succeeded", first["status"]!.GetValue<string>());
        Assert.Equal(2, mb.MimeDownloads);
        var source = await SourceAsync(w, "Mailbox");
        Assert.Equal(["Calendar", "Contacts", "Mail"], source["containers"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        var mail = Container(source, "Mail");
        var root = await ChildrenAsync(w, mail);
        Assert.Contains(root, i => i!["name"]!.GetValue<string>() == "Inbox");
        var inbox = await ChildrenAsync(w, mail, Named(root, "Inbox")["itemId"]!.GetValue<string>());
        Assert.Equal(["Projects", "Welcome aboard"], inbox.Select(i => i!["name"]!.GetValue<string>()));
        var eml = await w.Client.GetStringAsync($"/api/data/versions/{Named(inbox, "Welcome aboard")["id"]}/content");
        Assert.Contains("Glad you're here", eml);
        Assert.Single(await ChildrenAsync(w, Container(source, "Contacts")));

        mb.AddMessage("/Inbox", "Lunch?");
        mb.Move(welcome, "/Inbox/Projects");
        mb.Delete(plan);
        var second = await BackupAsync(w, "Mailbox");
        Assert.Equal("Succeeded", second["status"]!.GetValue<string>());
        Assert.Equal(3, mb.MimeDownloads); // only the new message
        inbox = await ChildrenAsync(w, mail, Named(root, "Inbox")["itemId"]!.GetValue<string>());
        Assert.Equal(["Projects", "Lunch?"], inbox.Select(i => i!["name"]!.GetValue<string>()));
        var projects = await ChildrenAsync(w, mail, Named(inbox, "Projects")["itemId"]!.GetValue<string>(), deleted: true);
        Assert.Null(Named(projects, "Welcome aboard")["deletedAt"]);
        Assert.Equal(welcome, Named(projects, "Welcome aboard")["itemId"]!.GetValue<string>());
        Assert.NotNull(Named(projects, "Project plan")["deletedAt"]);
    }

    [Fact]
    public async Task Deleted_mail_folder_is_restored_with_its_messages_as_received_mail()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var mb = app.Graph.AddMailbox(w.Seeded.UserId);
        mb.AddFolder("/Inbox/Projects/Falcon");
        mb.AddMessage("/Inbox/Projects", "Project plan", "See attached", attachments: ("plan.pdf", Encoding.UTF8.GetBytes("%PDF-1.7 plan")));
        mb.AddMessage("/Inbox/Projects/Falcon", "Falcon kickoff", "Tuesday 10am");
        await BackupAsync(w, "Mailbox");
        mb.DeleteFolder("/Inbox/Projects");
        await BackupAsync(w, "Mailbox");

        var mail = Container(await SourceAsync(w, "Mailbox"), "Mail");
        var inboxId = Named(await ChildrenAsync(w, mail), "Inbox")["itemId"]!.GetValue<string>();
        var deleted = Named(await ChildrenAsync(w, mail, inboxId, deleted: true), "Projects");
        Assert.NotNull(deleted["deletedAt"]);

        var job = await RestoreAsync(w, new { containerId = mail, itemIds = new[] { deleted["itemId"]!.GetValue<string>() } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.Equal(4, job["result"]!["restored"]!.GetValue<int>()); // 2 folders + 2 messages

        var restoredPlan = Assert.Single(mb.In("/Inbox/Projects"));
        Assert.Equal("Project plan", restoredPlan.Subject);
        Assert.False(restoredPlan.IsDraft);
        var attachment = Assert.Single(restoredPlan.Attachments);
        Assert.Equal("plan.pdf", attachment["name"]!.GetValue<string>());
        Assert.Equal("%PDF-1.7 plan", Encoding.UTF8.GetString(Convert.FromBase64String(attachment["contentBytes"]!.GetValue<string>())));
        var kickoff = Assert.Single(mb.In("/Inbox/Projects/Falcon"));
        Assert.Equal("Falcon kickoff", kickoff.Subject);
        Assert.Equal("1", kickoff.Created!["singleValueExtendedProperties"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "Integer 0x0E07")!["value"]!.GetValue<string>());

        // Restoring again with "skip" finds the messages already there.
        var again = await RestoreAsync(w, new { containerId = mail, itemIds = new[] { deleted["itemId"]!.GetValue<string>() }, conflict = "skip" });
        Assert.Equal(2, again["result"]!["skipped"]!.GetValue<int>());
        Assert.Single(mb.In("/Inbox/Projects"));
    }

    [Fact]
    public async Task Calendar_events_come_back_without_inviting_attendees()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        app.Graph.AddMailbox(w.Seeded.UserId);
        var events = app.Graph.Collection($"users/{w.Seeded.UserId}/events");
        events.Add(new JsonObject
        {
            ["id"] = "e1", ["changeKey"] = "1", ["subject"] = "Quarterly review", ["type"] = "singleInstance",
            ["start"] = new JsonObject { ["dateTime"] = "2026-12-01T10:00:00", ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = "2026-12-01T11:00:00", ["timeZone"] = "UTC" },
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = "Agenda" },
            ["attendees"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Megan Bowen", ["address"] = "megan@contoso.com" } }),
            ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = "adele@contoso.com" } },
        });
        await BackupAsync(w, "Mailbox");
        events.Clear();
        await BackupAsync(w, "Mailbox");

        var calendar = Container(await SourceAsync(w, "Mailbox"), "Calendar");
        var item = Named(await ChildrenAsync(w, calendar, deleted: true), "Quarterly review");
        var job = await RestoreAsync(w, new { containerId = calendar, itemIds = new[] { item["itemId"]!.GetValue<string>() } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        var restored = Assert.Single(events);
        Assert.Null(restored["attendees"]);
        Assert.Null(restored["organizer"]);
        Assert.Contains("Megan Bowen", restored["body"]!["content"]!.GetValue<string>());
        Assert.Equal("2026-12-01T10:00:00", restored["start"]!["dateTime"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ file restore

    [Fact]
    public async Task Deleted_SharePoint_folder_is_restored_with_its_contents()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        app.Graph.Add("sites", new JsonObject { ["id"] = "contoso.sharepoint.com,1,2", ["displayName"] = "Finance", ["webUrl"] = "https://contoso.sharepoint.com/sites/finance" });
        app.Graph.On(HttpMethod.Get, "/v1.0/sites/getAllSites", (_, _) => InMemoryGraph.Json(new JsonObject
        {
            ["value"] = new JsonArray(app.Graph.Collection("sites").Select(s => (JsonNode)s.DeepClone()).ToArray()),
        }));
        var drive = app.Graph.AddDrive("sites/contoso.sharepoint.com,1,2", "Documents");
        drive.PutFile("/Budgets/2026/budget.xlsx", "2026 numbers");
        drive.PutFile("/Budgets/2025/budget.xlsx", "2025 numbers");
        drive.PutFile("/Budgets/notes.txt", "keep");
        drive.PutFile("/readme.txt", "hi");
        await BackupAsync(w, "SharePointSite");
        drive.Delete("/Budgets");
        await BackupAsync(w, "SharePointSite");
        Assert.False(drive.Exists("/Budgets"));

        var docs = Container(await SourceAsync(w, "SharePointSite"), "Documents");
        var budgets = Named(await ChildrenAsync(w, docs, deleted: true), "Budgets");
        var job = await RestoreAsync(w, new { containerId = docs, itemIds = new[] { budgets["itemId"]!.GetValue<string>() } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.Equal(["2025", "2026", "notes.txt"], drive.List("/Budgets"));
        Assert.Equal("2026 numbers", Encoding.UTF8.GetString(drive.Read("/Budgets/2026/budget.xlsx")));
        Assert.Equal("keep", Encoding.UTF8.GetString(drive.Read("/Budgets/notes.txt")));
    }

    [Fact]
    public async Task Name_conflicts_can_keep_both_replace_or_skip()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var drive = app.Graph.AddDrive($"users/{w.Seeded.UserId}", "OneDrive");
        drive.PutFile("/notes.txt", "original");
        await BackupAsync(w, "OneDrive");
        drive.PutFile("/notes.txt", "edited later");
        var docs = Container(await SourceAsync(w, "OneDrive"), "OneDrive");
        var notes = Named(await ChildrenAsync(w, docs), "notes.txt");
        var ids = new[] { notes["itemId"]!.GetValue<string>() };

        var skip = await RestoreAsync(w, new { containerId = docs, itemIds = ids, conflict = "skip" });
        Assert.Equal(1, skip["result"]!["skipped"]!.GetValue<int>());
        Assert.Equal("edited later", Encoding.UTF8.GetString(drive.Read("/notes.txt")));

        await RestoreAsync(w, new { containerId = docs, itemIds = ids, conflict = "rename" });
        Assert.Equal("original", Encoding.UTF8.GetString(drive.Read("/notes 1.txt")));
        Assert.Equal("edited later", Encoding.UTF8.GetString(drive.Read("/notes.txt")));

        await RestoreAsync(w, new { containerId = docs, itemIds = ids, conflict = "replace" });
        Assert.Equal("original", Encoding.UTF8.GetString(drive.Read("/notes.txt")));
    }

    [Fact]
    public async Task Files_can_be_restored_into_another_users_OneDrive_including_large_ones()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var other = app.Graph.Add("users", new JsonObject { ["userPrincipalName"] = "megan@contoso.onmicrosoft.com", ["displayName"] = "Megan Bowen" });
        var adeleDrive = app.Graph.AddDrive($"users/{w.Seeded.UserId}", "OneDrive");
        var meganDrive = app.Graph.AddDrive($"users/{other["id"]}", "OneDrive");
        var big = new byte[DriveRestorer.SimpleUploadLimit * 2 + 12345];
        new Random(7).NextBytes(big);
        adeleDrive.PutFile("/Reports/q1.docx", "Q1");
        adeleDrive.PutFile("/Reports/video.mp4", big);
        await BackupAsync(w, "OneDrive");

        var adele = await SourceAsync(w, "OneDrive", "Adele Vance");
        var megan = await SourceAsync(w, "OneDrive", "Megan Bowen");
        var container = Container(adele, "OneDrive");
        var reports = Named(await ChildrenAsync(w, container), "Reports");
        var job = await RestoreAsync(w, new
        {
            containerId = container, itemIds = new[] { reports["itemId"]!.GetValue<string>() },
            targetSourceId = megan["source"]!["id"]!.GetValue<string>(), folderName = "From Adele",
        });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.Equal("Q1", Encoding.UTF8.GetString(meganDrive.Read("/From Adele/Reports/q1.docx")));
        Assert.Equal(big, meganDrive.Read("/From Adele/Reports/video.mp4"));
        Assert.Equal(1, app.Graph.UploadSessionsCompleted);
        Assert.Equal(["Reports"], adeleDrive.List("/")); // the original is untouched
    }

    [Fact]
    public async Task Mail_cannot_be_restored_into_a_drive()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var mb = app.Graph.AddMailbox(w.Seeded.UserId);
        mb.AddMessage("/Inbox", "Hi");
        app.Graph.AddDrive($"users/{w.Seeded.UserId}", "OneDrive").PutFile("/a.txt", "a");
        await app.RunJobAsync(w.Client, "backup.data", w.Tenant.Id, new { kinds = new[] { "Mailbox", "OneDrive" } });
        var mailSource = await SourceAsync(w, "Mailbox");
        var drive = await SourceAsync(w, "OneDrive");
        var mail = Container(mailSource, "Mail");
        var inbox = Named(await ChildrenAsync(w, mail), "Inbox");
        var job = await RestoreAsync(w, new { containerId = mail, itemIds = new[] { inbox["itemId"]!.GetValue<string>() }, targetSourceId = drive["source"]!["id"]!.GetValue<string>() });
        Assert.Equal("Failed", job["status"]!.GetValue<string>());
        Assert.Contains("only be restored to a mailbox", job["error"]!.GetValue<string>());
    }
}
