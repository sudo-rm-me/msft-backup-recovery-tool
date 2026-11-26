using System.Net;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Files;

/// <summary>
/// OneDrive / SharePoint document library backup using Graph delta queries.
/// <list type="bullet">
/// <item>Each page of the delta is committed with its continuation link, so an interrupted run resumes where it stopped.</item>
/// <item>Files are downloaded only when their content tag (cTag) changes; renames and moves are metadata-only versions.</item>
/// <item>Content is stored as encrypted, deduplicated chunks.</item>
/// <item>Items that couldn't be read are retried on the next run; an expired delta token triggers a full resync.</item>
/// </list>
/// </summary>
public sealed class DriveBackup(MbrDbContext db, TenantSessionFactory sessions, ITenantContentStore content) : IDataSourceBackup
{
    public const int ChunkSize = 8 * 1024 * 1024;
    public int DownloadParallelism { get; init; } = 4;

    public bool Handles(DataSourceKind kind) => kind is DataSourceKind.OneDrive or DataSourceKind.SharePointSite;

    public async Task<SourceBackupResult> BackupAsync(Tenant tenant, DataSource source, Guid storageTargetId, IJobContext? job, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        var drives = new List<JsonObject>();
        if (source.Kind == DataSourceKind.OneDrive)
        {
            if (await graph.TryGetAsync($"/v1.0/users/{source.ExternalId}/drive?$select=id,name,webUrl", ct) is JsonObject d) drives.Add(d);
        }
        else
        {
            await foreach (var d in graph.ListAsync($"/v1.0/sites/{source.ExternalId}/drives?$select=id,name,webUrl,driveType", ct)) drives.Add(d);
        }

        var containers = await db.DataContainers.Where(c => c.SourceId == source.Id).ToDictionaryAsync(c => c.ExternalId, ct);
        foreach (var d in drives)
        {
            var id = d["id"]!.GetValue<string>();
            if (!containers.TryGetValue(id, out var c))
            {
                c = new DataContainer { TenantId = tenant.Id, SourceId = source.Id, ExternalId = id };
                db.DataContainers.Add(c);
                containers[id] = c;
            }
            c.Name = d["name"]?.ToString() ?? "Documents";
            c.WebUrl = d["webUrl"]?.ToString();
            c.Missing = false;
        }
        foreach (var gone in containers.Values.Where(c => drives.All(d => d["id"]!.GetValue<string>() != c.ExternalId))) gone.Missing = true;
        await db.SaveChangesAsync(ct);

        var total = SourceBackupResult.Empty;
        foreach (var c in containers.Values.Where(c => !c.Missing))
            total += await SyncAsync(tenant, source, c, graph, storageTargetId, job, ct);
        return total;
    }

    private sealed class SyncState
    {
        public int NewVersions, Deleted, Failed, Unchanged;
        public long Bytes;
        public HashSet<string>? Seen;
        public readonly HashSet<string> Retry = new();
        /// <summary>Full paths of folders touched in this run (itemId → "/A/B").</summary>
        public readonly Dictionary<string, string> FolderPaths = new();
    }

    private async Task<SourceBackupResult> SyncAsync(Tenant tenant, DataSource source, DataContainer c, IApiClient graph, Guid targetId,
        IJobContext? job, CancellationToken ct)
    {
        var st = new SyncState();
        var drivePath = $"/v1.0/drives/{Uri.EscapeDataString(c.ExternalId)}";

        // Items that failed last time.
        var retry = c.RetryItemIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (retry.Length > 0)
        {
            var items = new List<JsonObject>();
            foreach (var id in retry)
            {
                var item = await graph.TryGetAsync($"{drivePath}/items/{Uri.EscapeDataString(id)}", ct) as JsonObject;
                items.Add(item ?? new JsonObject { ["id"] = id, ["deleted"] = new JsonObject() });
            }
            await ProcessPageAsync(tenant, source, c, graph, drivePath, targetId, items, st, ct);
        }

        bool full;
        string? next;
        var resumed = c.ResumeLink != null;
        if (c.ResumeLink != null) { next = c.ResumeLink; full = c.ResumeIsFull; }
        else if (c.DeltaLink != null) { next = c.DeltaLink; full = false; }
        else { next = $"{drivePath}/root/delta"; full = true; }
        if (full && !resumed) st.Seen = new HashSet<string>();
        if (job != null) await job.InfoAsync($"{source.DisplayName} / {c.Name}: {(resumed ? "resuming" : full ? "full sync" : "incremental sync")}");

        string? deltaLink = null;
        var pages = 0;
        while (next != null)
        {
            JsonObject page;
            try
            {
                page = await graph.GetAsync(next, ct) as JsonObject ?? new JsonObject();
            }
            catch (ApiException e) when (e.StatusCode == HttpStatusCode.Gone || e.ErrorCode is "resyncRequired")
            {
                if (job != null) await job.WarnAsync($"{c.Name}: the change token expired; running a full resync");
                c.DeltaLink = c.ResumeLink = null;
                next = $"{drivePath}/root/delta";
                full = true;
                resumed = false;
                st.Seen = new HashSet<string>();
                continue;
            }
            var items = (page["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            await ProcessPageAsync(tenant, source, c, graph, drivePath, targetId, items, st, ct);
            next = page["@odata.nextLink"]?.GetValue<string>();
            deltaLink = page["@odata.deltaLink"]?.GetValue<string>() ?? deltaLink;
            c.ResumeLink = next;
            c.ResumeIsFull = full;
            c.RetryItemIds = string.Join(",", st.Retry);
            await db.SaveChangesAsync(ct);
            if (job != null && ++pages % 5 == 0) await job.InfoAsync($"{c.Name}: {st.NewVersions} new version(s) so far");
        }

        // A complete full enumeration also tells us what was deleted while we weren't looking.
        if (full && st.Seen != null)
        {
            var now = DateTimeOffset.UtcNow;
            var current = await db.DataItemVersions.Where(v => v.ContainerId == c.Id && v.SupersededAt == null && v.DeletedAt == null).ToListAsync(ct);
            foreach (var v in current.Where(v => !st.Seen.Contains(v.ItemId)))
            {
                v.DeletedAt = now;
                st.Deleted++;
            }
        }
        c.DeltaLink = deltaLink ?? c.DeltaLink;
        c.ResumeLink = null;
        c.ResumeIsFull = false;
        c.RetryItemIds = string.Join(",", st.Retry);
        c.LastSyncAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new SourceBackupResult(st.NewVersions, st.Bytes, st.Deleted, st.Failed, st.Unchanged);
    }

    private static bool IsFolder(JsonObject item) => item["folder"] != null || item["package"] != null;

    private async Task ProcessPageAsync(Tenant tenant, DataSource source, DataContainer c, IApiClient graph, string drivePath, Guid targetId,
        List<JsonObject> items, SyncState st, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = items.Select(i => i["id"]!.GetValue<string>()).Distinct().ToList();
        var current = (await db.DataItemVersions
                .Where(v => v.ContainerId == c.Id && ids.Contains(v.ItemId) && v.SupersededAt == null && v.DeletedAt == null).ToListAsync(ct))
            .GroupBy(v => v.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CapturedAt).First());

        // Decide what each item needs; downloads run in parallel afterwards.
        var plan = new List<(JsonObject Item, DataItemVersion? Current, bool Download)>();
        foreach (var item in items)
        {
            var id = item["id"]!.GetValue<string>();
            if (item["root"] != null)
            {
                c.RootItemId = id;
                st.FolderPaths[id] = "";
                continue;
            }
            st.Seen?.Add(id);
            current.TryGetValue(id, out var cur);
            if (item["deleted"] != null)
            {
                if (cur != null) { cur.DeletedAt = now; st.Deleted++; }
                st.Retry.Remove(id);
                continue;
            }
            var name = item["name"]?.ToString() ?? id;
            var parent = item["parentReference"]?["id"]?.ToString();
            if (IsFolder(item))
            {
                if (cur is { IsFolder: true } && cur.Name == name && cur.ParentItemId == parent) { st.Unchanged++; continue; }
                plan.Add((item, cur, false));
                continue;
            }
            var eTag = item["eTag"]?.ToString();
            var cTag = item["cTag"]?.ToString();
            if (cur != null && !cur.IsFolder && cur.ETag == eTag && eTag != null) { st.Unchanged++; st.Retry.Remove(id); continue; }
            var sameContent = cur != null && !cur.IsFolder && cTag != null && cur.CTag == cTag;
            plan.Add((item, cur, !sameContent));
        }

        var downloads = new Dictionary<string, (string Chunks, long Size)>();
        await Parallel.ForEachAsync(plan.Where(p => p.Download), new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = ct },
            async (p, token) =>
            {
                var id = p.Item["id"]!.GetValue<string>();
                try
                {
                    var r = await DownloadAsync(tenant, graph, $"{drivePath}/items/{Uri.EscapeDataString(id)}/content", targetId, token);
                    lock (downloads) downloads[id] = r;
                }
                catch (Exception e) when (e is ApiException or IOException or HttpRequestException)
                {
                    lock (st) { st.Failed++; st.Retry.Add(id); }
                }
            });

        foreach (var (item, cur, download) in plan)
        {
            var id = item["id"]!.GetValue<string>();
            string chunks;
            long size;
            if (IsFolder(item)) { chunks = ""; size = 0; }
            else if (!download) { chunks = cur!.Chunks; size = item["size"]?.GetValue<long>() ?? cur.Size; }
            else if (downloads.TryGetValue(id, out var d)) { (chunks, size) = d; st.Bytes += size; }
            else continue; // failed: retried next run, the current version stays current

            if (cur != null) cur.SupersededAt = now;
            var parent = item["parentReference"]?["id"]?.ToString();
            var name = item["name"]?.ToString() ?? id;
            var path = await FolderPathAsync(c, parent, st, ct);
            var version = new DataItemVersion
            {
                TenantId = tenant.Id, SourceId = source.Id, ContainerId = c.Id, ItemId = id, ParentItemId = parent, Name = name,
                Path = path, IsFolder = IsFolder(item), Size = size, Chunks = chunks, StorageTargetId = targetId,
                ETag = item["eTag"]?.ToString(), CTag = item["cTag"]?.ToString(),
                QuickXorHash = item["file"]?["hashes"]?["quickXorHash"]?.ToString(), ContentType = item["file"]?["mimeType"]?.ToString(),
                ModifiedAt = DateTimeOffset.TryParse(item["lastModifiedDateTime"]?.ToString(), out var m) ? m : null,
                ModifiedBy = item["lastModifiedBy"]?["user"]?["email"]?.ToString() ?? item["lastModifiedBy"]?["user"]?["displayName"]?.ToString(),
                CapturedAt = now,
            };
            db.DataItemVersions.Add(version);
            if (version.IsFolder) st.FolderPaths[id] = (path == "/" ? "" : path) + "/" + name;
            st.NewVersions++;
            st.Retry.Remove(id);
        }
    }

    /// <summary>The display path of the folder an item lives in ("/" for the root).</summary>
    private async Task<string> FolderPathAsync(DataContainer c, string? parentId, SyncState st, CancellationToken ct)
    {
        if (parentId is null || parentId == c.RootItemId) return "/";
        if (st.FolderPaths.TryGetValue(parentId, out var p)) return p.Length == 0 ? "/" : p;
        var parent = await db.DataItemVersions.AsNoTracking()
            .Where(v => v.ContainerId == c.Id && v.ItemId == parentId && v.IsFolder)
            .OrderByDescending(v => v.CapturedAt).FirstOrDefaultAsync(ct)
            ?? db.DataItemVersions.Local.Where(v => v.ContainerId == c.Id && v.ItemId == parentId).MaxBy(v => v.CapturedAt);
        var full = parent is null ? "" : (parent.Path == "/" ? "" : parent.Path) + "/" + parent.Name;
        st.FolderPaths[parentId] = full;
        return full.Length == 0 ? "/" : full;
    }

    /// <summary>Streams a file into encrypted content-store chunks.</summary>
    private async Task<(string Chunks, long Size)> DownloadAsync(Tenant tenant, IApiClient graph, string path, Guid targetId, CancellationToken ct)
    {
        await using var stream = await graph.DownloadAsync(path, ct);
        var hashes = new List<string>();
        var buffer = new byte[ChunkSize];
        long size = 0;
        while (true)
        {
            var filled = 0;
            int read;
            while (filled < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(filled), ct)) > 0) filled += read;
            if (filled == 0) break;
            hashes.Add(await content.PutObjectAsync(tenant, targetId, buffer.AsMemory(0, filled), ct));
            size += filled;
            if (filled < buffer.Length) break;
        }
        return (string.Join(",", hashes), size);
    }
}
