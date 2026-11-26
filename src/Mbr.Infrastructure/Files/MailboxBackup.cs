using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Files;

public sealed class MailboxDiscoverer : IDataSourceDiscoverer
{
    public DataSourceKind Kind => DataSourceKind.Mailbox;

    public async Task<List<DiscoveredSource>> DiscoverAsync(TenantSession session, CancellationToken ct)
    {
        var users = new List<JsonObject>();
        await foreach (var u in session.Graph.ListAsync("/v1.0/users?$select=id,displayName,userPrincipalName,mail,userType&$top=999", ct))
            if (!string.IsNullOrEmpty(u["mail"]?.ToString()) && u["userType"]?.ToString() != "Guest") users.Add(u);
        var result = new List<DiscoveredSource>();
        // Users with an Exchange Online mailbox (unlicensed / on-premises mailboxes return an error).
        await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (u, token) =>
        {
            var id = u["id"]!.GetValue<string>();
            try
            {
                if (await session.Graph.TryGetAsync($"/v1.0/users/{id}/mailFolders/inbox?$select=id", token) is null) return;
            }
            catch (ApiException) { return; }
            lock (result) result.Add(new DiscoveredSource(id, u["displayName"]?.ToString() ?? id, u["mail"]?.ToString()));
        });
        return result.OrderBy(r => r.DisplayName).ToList();
    }
}

/// <summary>
/// Exchange Online mailbox backup: mail (per-folder delta, immutable IDs, messages stored as MIME), calendar and contacts
/// (full scan with change keys, stored as JSON).
/// </summary>
public sealed class MailboxBackup(MbrDbContext db, TenantSessionFactory sessions, ITenantContentStore content) : IDataSourceBackup
{
    /// <summary>Immutable IDs keep a message's ID when it moves between folders.</summary>
    public static readonly Dictionary<string, string> Headers = new() { ["Prefer"] = "IdType=\"ImmutableId\", odata.maxpagesize=100" };

    private const string MessageSelect = "id,subject,from,toRecipients,receivedDateTime,lastModifiedDateTime,changeKey,hasAttachments,isDraft,parentFolderId,internetMessageId";

    public bool Handles(DataSourceKind kind) => kind == DataSourceKind.Mailbox;

    private sealed class MailState
    {
        public Dictionary<string, string> DeltaLinks { get; set; } = new();
        public Dictionary<string, string> ResumeLinks { get; set; } = new();
    }

    private sealed class Counters
    {
        public int New, Deleted, Failed, Unchanged;
        public long Bytes;
        public SourceBackupResult ToResult() => new(New, Bytes, Deleted, Failed, Unchanged);
    }

    public async Task<SourceBackupResult> BackupAsync(Tenant tenant, DataSource source, Guid storageTargetId, IJobContext? job, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        var user = $"/v1.0/users/{source.ExternalId}";
        var containers = await db.DataContainers.Where(c => c.SourceId == source.Id).ToDictionaryAsync(c => c.ExternalId, ct);
        DataContainer Container(string kind, string name)
        {
            if (!containers.TryGetValue(kind, out var c))
            {
                c = new DataContainer { TenantId = tenant.Id, SourceId = source.Id, ExternalId = kind, Kind = kind, Name = name };
                db.DataContainers.Add(c);
                containers[kind] = c;
            }
            return c;
        }
        var mail = Container("mail", "Mail");
        var calendar = Container("calendar", "Calendar");
        var contacts = Container("contacts", "Contacts");
        calendar.RootItemId ??= "calendar";
        contacts.RootItemId ??= "contacts";
        await db.SaveChangesAsync(ct);

        var counters = new Counters();
        await SyncMailAsync(tenant, source, mail, graph, user, storageTargetId, counters, job, ct);
        await SyncFlatAsync(tenant, source, calendar, graph,
            $"{user}/events?$select=id,changeKey,subject,body,start,end,location,locations,attendees,organizer,isAllDay,recurrence,showAs,sensitivity,importance,categories,isReminderOn,reminderMinutesBeforeStart,type,isOnlineMeeting,lastModifiedDateTime",
            e => e["subject"]?.ToString() ?? "(no subject)", e => DateTimeOffset.TryParse(e["start"]?["dateTime"]?.ToString() + "Z", out var d) ? d : null,
            "application/json", storageTargetId, counters, ct);
        await SyncFlatAsync(tenant, source, contacts, graph, $"{user}/contacts",
            c => c["displayName"]?.ToString() ?? c["emailAddresses"]?[0]?["address"]?.ToString() ?? "(no name)",
            c => DateTimeOffset.TryParse(c["lastModifiedDateTime"]?.ToString(), out var d) ? d : null,
            "application/json", storageTargetId, counters, ct);
        return counters.ToResult();
    }

    // ------------------------------------------------------------------ mail

    private async Task SyncMailAsync(Tenant tenant, DataSource source, DataContainer c, IApiClient graph, string user, Guid targetId,
        Counters n, IJobContext? job, CancellationToken ct)
    {
        var state = string.IsNullOrEmpty(c.StateJson) ? new MailState() : JsonSerializer.Deserialize<MailState>(c.StateJson)!;
        var now = DateTimeOffset.UtcNow;
        var root = await graph.SendAsync(HttpMethod.Get, $"{user}/mailFolders/msgfolderroot?$select=id", null, Headers, ct);
        c.RootItemId = root?["id"]?.ToString() ?? c.RootItemId;

        // Folders: the full hierarchy every run (it's small), compared with what we have.
        var folders = new List<JsonObject>();
        string? next = $"{user}/mailFolders/delta?$select=id,displayName,parentFolderId";
        while (next != null)
        {
            var page = await graph.SendAsync(HttpMethod.Get, next, null, Headers, ct) as JsonObject ?? new JsonObject();
            folders.AddRange((page["value"] as JsonArray ?? []).OfType<JsonObject>().Where(f => f["@removed"] is null));
            next = page["@odata.nextLink"]?.ToString();
        }
        var currentFolders = await db.DataItemVersions.Where(v => v.ContainerId == c.Id && v.IsFolder && v.SupersededAt == null && v.DeletedAt == null)
            .ToDictionaryAsync(v => v.ItemId, ct);
        var byId = folders.ToDictionary(f => f["id"]!.ToString());
        string PathOf(string? parentId)
        {
            var parts = new List<string>();
            for (var i = 0; i < 32 && parentId != null && parentId != c.RootItemId && byId.TryGetValue(parentId, out var p); i++)
            {
                parts.Insert(0, p["displayName"]!.ToString());
                parentId = p["parentFolderId"]?.ToString();
            }
            return "/" + string.Join("/", parts);
        }
        foreach (var f in folders)
        {
            var id = f["id"]!.ToString();
            var name = f["displayName"]?.ToString() ?? id;
            var parent = f["parentFolderId"]?.ToString();
            if (currentFolders.Remove(id, out var cur) && cur.Name == name && cur.ParentItemId == parent) continue;
            if (cur != null) cur.SupersededAt = now;
            db.DataItemVersions.Add(new DataItemVersion
            {
                TenantId = tenant.Id, SourceId = source.Id, ContainerId = c.Id, ItemId = id, ParentItemId = parent, Name = name,
                Path = PathOf(parent), IsFolder = true, StorageTargetId = targetId, CapturedAt = now,
            });
            n.New++;
        }
        // Folders that are gone (hard-deleted): they and their messages are deleted.
        foreach (var gone in currentFolders.Values)
        {
            gone.DeletedAt = now;
            n.Deleted++;
            await db.DataItemVersions.Where(v => v.ContainerId == c.Id && v.ParentItemId == gone.ItemId && v.SupersededAt == null && v.DeletedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.DeletedAt, now), ct);
            state.DeltaLinks.Remove(gone.ItemId);
            state.ResumeLinks.Remove(gone.ItemId);
        }
        await db.SaveChangesAsync(ct);

        // Removals are applied after every folder is synced: a moved message shows up as removed from one folder and added to
        // another, in whichever order the folders are read.
        var removals = new List<(string ItemId, string FolderId)>();
        foreach (var f in folders)
        {
            var fid = f["id"]!.ToString();
            var path = PathOf(fid);
            next = state.ResumeLinks.GetValueOrDefault(fid) ?? state.DeltaLinks.GetValueOrDefault(fid)
                   ?? $"{user}/mailFolders/{Uri.EscapeDataString(fid)}/messages/delta?$select={MessageSelect}";
            string? delta = null;
            while (next != null)
            {
                JsonObject page;
                try { page = await graph.SendAsync(HttpMethod.Get, next, null, Headers, ct) as JsonObject ?? new JsonObject(); }
                catch (ApiException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound || e.ErrorCode is "SyncStateNotFound" or "SyncStateInvalid")
                {
                    if (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode is not ("SyncStateNotFound" or "SyncStateInvalid")) break; // folder vanished meanwhile
                    state.DeltaLinks.Remove(fid);
                    state.ResumeLinks.Remove(fid);
                    next = $"{user}/mailFolders/{Uri.EscapeDataString(fid)}/messages/delta?$select={MessageSelect}";
                    continue;
                }
                await ProcessMessagesAsync(tenant, source, c, graph, user, fid, path, targetId,
                    (page["value"] as JsonArray ?? []).OfType<JsonObject>().ToList(), removals, n, ct);
                next = page["@odata.nextLink"]?.ToString();
                delta = page["@odata.deltaLink"]?.ToString() ?? delta;
                if (next != null) state.ResumeLinks[fid] = next; else state.ResumeLinks.Remove(fid);
                c.StateJson = JsonSerializer.Serialize(state);
                await db.SaveChangesAsync(ct);
            }
            if (delta != null) state.DeltaLinks[fid] = delta;
        }
        await db.SaveChangesAsync(ct);
        var removedIds = removals.Select(r => r.ItemId).Distinct().ToList();
        var stillCurrent = await db.DataItemVersions
            .Where(v => v.ContainerId == c.Id && removedIds.Contains(v.ItemId) && v.SupersededAt == null && v.DeletedAt == null).ToListAsync(ct);
        foreach (var v in stillCurrent.Where(v => removals.Contains((v.ItemId, v.ParentItemId ?? ""))))
        {
            v.DeletedAt = now;
            n.Deleted++;
        }
        c.StateJson = JsonSerializer.Serialize(state);
        c.LastSyncAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (job != null) await job.InfoAsync($"{source.DisplayName}: mail synced ({folders.Count} folders)");
    }

    private async Task ProcessMessagesAsync(Tenant tenant, DataSource source, DataContainer c, IApiClient graph, string user, string folderId, string path,
        Guid targetId, List<JsonObject> messages, List<(string ItemId, string FolderId)> removals, Counters n, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = messages.Select(m => m["id"]!.ToString()).Distinct().ToList();
        var current = (await db.DataItemVersions.Where(v => v.ContainerId == c.Id && ids.Contains(v.ItemId) && v.SupersededAt == null && v.DeletedAt == null)
            .ToListAsync(ct)).GroupBy(v => v.ItemId).ToDictionary(g => g.Key, g => g.First());
        var toDownload = new List<JsonObject>();
        foreach (var m in messages)
        {
            var id = m["id"]!.ToString();
            current.TryGetValue(id, out var cur);
            if (m["@removed"] != null)
            {
                removals.Add((id, folderId));
                continue;
            }
            if (cur != null && cur.ETag == m["changeKey"]?.ToString() && cur.ParentItemId == folderId) { n.Unchanged++; continue; }
            var isDraft = m["isDraft"]?.GetValue<bool>() == true;
            if (cur != null && !isDraft)
            {
                // Sent/received mail doesn't change: a new version for the move/flags, same content.
                cur.SupersededAt = now;
                db.DataItemVersions.Add(MessageVersion(tenant, source, c, m, folderId, path, cur.Chunks, cur.Size, targetId, now));
                n.New++;
                continue;
            }
            toDownload.Add(m);
        }

        var downloaded = new Dictionary<string, (string Chunks, long Size)>();
        await Parallel.ForEachAsync(toDownload, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (m, token) =>
        {
            var id = m["id"]!.ToString();
            try
            {
                await using var s = await graph.DownloadAsync($"{user}/messages/{Uri.EscapeDataString(id)}/$value", token);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, token);
                var hashes = new List<string>();
                var bytes = ms.ToArray();
                for (var off = 0; off < bytes.Length; off += DriveBackup.ChunkSize)
                    hashes.Add(await content.PutObjectAsync(tenant, targetId, bytes.AsMemory(off, Math.Min(DriveBackup.ChunkSize, bytes.Length - off)), token));
                lock (downloaded) downloaded[id] = (string.Join(",", hashes), bytes.LongLength);
            }
            catch (Exception e) when (e is ApiException or IOException or HttpRequestException)
            {
                Interlocked.Increment(ref n.Failed);
            }
        });
        foreach (var m in toDownload)
        {
            var id = m["id"]!.ToString();
            if (!downloaded.TryGetValue(id, out var d)) continue;
            if (current.TryGetValue(id, out var cur)) cur.SupersededAt = now;
            db.DataItemVersions.Add(MessageVersion(tenant, source, c, m, folderId, path, d.Chunks, d.Size, targetId, now));
            n.New++;
            n.Bytes += d.Size;
        }
    }

    private static DataItemVersion MessageVersion(Tenant tenant, DataSource source, DataContainer c, JsonObject m, string folderId, string path,
        string chunks, long size, Guid targetId, DateTimeOffset now) => new()
    {
        TenantId = tenant.Id, SourceId = source.Id, ContainerId = c.Id, ItemId = m["id"]!.ToString(), ParentItemId = folderId,
        Name = string.IsNullOrWhiteSpace(m["subject"]?.ToString()) ? "(no subject)" : m["subject"]!.ToString(), Path = path,
        Size = size, Chunks = chunks, StorageTargetId = targetId, ETag = m["changeKey"]?.ToString(), ContentType = "message/rfc822",
        ModifiedAt = DateTimeOffset.TryParse(m["receivedDateTime"]?.ToString(), out var r) ? r : null,
        ModifiedBy = m["from"]?["emailAddress"]?["address"]?.ToString(),
        CapturedAt = now,
        MetadataJson = new JsonObject
        {
            ["from"] = m["from"]?["emailAddress"]?.DeepClone(), ["to"] = new JsonArray((m["toRecipients"] as JsonArray ?? []).Select(t => t?["emailAddress"]?["address"]?.DeepClone()).ToArray()),
            ["hasAttachments"] = m["hasAttachments"]?.DeepClone(), ["isDraft"] = m["isDraft"]?.DeepClone(), ["internetMessageId"] = m["internetMessageId"]?.DeepClone(),
        }.ToJsonString(),
    };

    // ------------------------------------------------------------------ calendar / contacts

    private async Task SyncFlatAsync(Tenant tenant, DataSource source, DataContainer c, IApiClient graph, string listPath, Func<JsonObject, string> name,
        Func<JsonObject, DateTimeOffset?> modified, string contentType, Guid targetId, Counters n, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<JsonObject>();
        try
        {
            string? next = listPath;
            while (next != null)
            {
                var page = await graph.SendAsync(HttpMethod.Get, next, null, Headers, ct) as JsonObject ?? new JsonObject();
                items.AddRange((page["value"] as JsonArray ?? []).OfType<JsonObject>());
                next = page["@odata.nextLink"]?.ToString();
            }
        }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return; // no calendar / contacts (e.g. shared mailbox without them)
        }
        var current = await db.DataItemVersions.Where(v => v.ContainerId == c.Id && v.SupersededAt == null && v.DeletedAt == null).ToDictionaryAsync(v => v.ItemId, ct);
        foreach (var item in items)
        {
            var id = item["id"]!.ToString();
            var changeKey = item["changeKey"]?.ToString();
            if (current.Remove(id, out var cur) && cur.ETag == changeKey && changeKey != null) { n.Unchanged++; continue; }
            var clean = (JsonObject)item.DeepClone();
            foreach (var k in clean.Select(kv => kv.Key).Where(k => k.StartsWith("@odata")).ToList()) clean.Remove(k);
            var bytes = CanonicalJson.ToBytes(clean);
            var hash = await content.PutObjectAsync(tenant, targetId, bytes, ct);
            if (cur != null) cur.SupersededAt = now;
            db.DataItemVersions.Add(new DataItemVersion
            {
                TenantId = tenant.Id, SourceId = source.Id, ContainerId = c.Id, ItemId = id, ParentItemId = c.RootItemId, Name = name(item), Path = "/",
                Size = bytes.Length, Chunks = hash, StorageTargetId = targetId, ETag = changeKey, ContentType = contentType, ModifiedAt = modified(item),
                CapturedAt = now,
            });
            n.New++;
            n.Bytes += bytes.Length;
        }
        foreach (var gone in current.Values) { gone.DeletedAt = now; n.Deleted++; }
        c.LastSyncAt = now;
        await db.SaveChangesAsync(ct);
    }
}
