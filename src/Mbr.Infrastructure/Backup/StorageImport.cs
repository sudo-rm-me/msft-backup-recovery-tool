using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Files;
using Mbr.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Backup;

/// <summary>
/// Rebuilds MBR's index from a storage target after the database was lost: tenants (from each tenant's key.json), config
/// snapshots (from their manifests) and file and mail versions (from the data journals). Only adds what's missing, so it can
/// be run again safely. Needs the same master key the backups were written with.
/// </summary>
public sealed class StorageImportService(MbrDbContext db, StorageTargetResolver resolver, ITenantContentStore content, ResourceRegistry registry)
{
    public sealed class Result
    {
        public int TenantsFound, TenantsCreated, TenantsSkipped, SnapshotsImported, SnapshotsKnown, SourcesImported, VersionsImported,
            VersionsUpdated, VersionsWithoutContent, ObjectsIndexed;
        public List<string> Warnings { get; } = new();

        public JsonObject ToJson() => new()
        {
            ["tenantsFound"] = TenantsFound, ["tenantsCreated"] = TenantsCreated, ["tenantsSkipped"] = TenantsSkipped,
            ["snapshotsImported"] = SnapshotsImported, ["snapshotsKnown"] = SnapshotsKnown, ["sourcesImported"] = SourcesImported,
            ["versionsImported"] = VersionsImported, ["versionsUpdated"] = VersionsUpdated, ["versionsWithoutContent"] = VersionsWithoutContent,
            ["objectsIndexed"] = ObjectsIndexed, ["warnings"] = new JsonArray(Warnings.Take(50).Select(w => (JsonNode)w).ToArray()),
        };
    }

    private sealed record KeyEscrow(Guid TenantId, string? DirectoryId, string? DisplayName, string? WrappedDataKey, string? WrapperId);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<Result> ImportAsync(Guid storageTargetId, Guid? onlyTenantId, IJobContext? job, CancellationToken ct)
    {
        var (target, store) = await resolver.ResolveAsync(storageTargetId, ct);
        var result = new Result();
        var escrowKeys = new List<string>();
        await foreach (var key in store.ListAsync("tenants/", ct))
            if (key.EndsWith("/key.json", StringComparison.Ordinal) && key.Count(c => c == '/') == 2) escrowKeys.Add(key);

        for (var i = 0; i < escrowKeys.Count; i++)
        {
            await using var s = await store.GetAsync(escrowKeys[i], ct);
            if (s is null) continue;
            var escrow = await JsonSerializer.DeserializeAsync<KeyEscrow>(s, Web, ct);
            if (escrow is null || escrow.TenantId == Guid.Empty || string.IsNullOrEmpty(escrow.WrappedDataKey)) continue;
            if (onlyTenantId is { } only && escrow.TenantId != only) continue;
            result.TenantsFound++;
            if (job != null) await job.ReportProgressAsync((double)i / escrowKeys.Count, escrow.DisplayName ?? escrow.TenantId.ToString());

            var tenant = await EnsureTenantAsync(escrow, target, result, job, ct);
            if (tenant is null) { result.TenantsSkipped++; continue; }
            try
            {
                var objects = await IndexObjectsAsync(tenant, target, store, result, ct);
                await ImportSnapshotsAsync(tenant, target, store, result, ct);
                await ImportDataAsync(tenant, target, store, objects, result, ct);
            }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                result.Warnings.Add($"{tenant.DisplayName}: its backups can't be decrypted with this deployment's master key ({e.Message}).");
                result.TenantsSkipped++;
            }
            if (job != null) await job.InfoAsync($"{tenant.DisplayName}: done");
        }
        foreach (var w in result.Warnings) if (job != null) await job.WarnAsync(w);
        return result;
    }

    private async Task<Tenant?> EnsureTenantAsync(KeyEscrow escrow, StorageTarget target, Result result, IJobContext? job, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == escrow.TenantId, ct);
        if (tenant is null)
        {
            if (!string.IsNullOrEmpty(escrow.DirectoryId) && await db.Tenants.AnyAsync(t => t.DirectoryId == escrow.DirectoryId, ct))
            {
                result.Warnings.Add($"{escrow.DisplayName}: a tenant with directory {escrow.DirectoryId} already exists under another ID; its old backups weren't imported.");
                return null;
            }
            tenant = new Tenant
            {
                Id = escrow.TenantId, DirectoryId = escrow.DirectoryId ?? "", DisplayName = escrow.DisplayName ?? escrow.TenantId.ToString(),
                // Pending consent: nothing runs (backups, retention) until an operator checks it.
                Status = TenantStatus.PendingConsent, WrappedDataKey = escrow.WrappedDataKey, DataKeyWrapperId = escrow.WrapperId,
                DefaultStorageTargetId = target.Id, Notes = $"Imported from storage target '{target.Name}' on {DateTimeOffset.UtcNow:yyyy-MM-dd}.",
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            result.TenantsCreated++;
            if (job != null) await job.InfoAsync($"Added tenant {tenant.DisplayName}");
            return tenant;
        }
        if (tenant.WrappedDataKey is null)
        {
            tenant.WrappedDataKey = escrow.WrappedDataKey;
            tenant.DataKeyWrapperId = escrow.WrapperId;
            await db.SaveChangesAsync(ct);
        }
        else if (tenant.WrappedDataKey != escrow.WrappedDataKey)
        {
            result.Warnings.Add($"{tenant.DisplayName}: the tenant exists with a different data key than the one in storage; skipped.");
            return null;
        }
        return tenant;
    }

    private async Task<HashSet<string>> IndexObjectsAsync(Tenant tenant, StorageTarget target, IBlobStore store, Result result, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in store.ListAsync($"tenants/{tenant.Id}/objects/", ct))
            found.Add(key[(key.LastIndexOf('/') + 1)..]);
        var known = (await db.StoredObjects.AsNoTracking().Where(o => o.TenantId == tenant.Id && o.StorageTargetId == target.Id)
            .Select(o => o.Hash).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // Sizes aren't known without reading every object; they're only used for reporting.
        foreach (var hash in found.Where(h => !known.Contains(h)))
        {
            db.StoredObjects.Add(new StoredObject { TenantId = tenant.Id, StorageTargetId = target.Id, Hash = hash });
            result.ObjectsIndexed++;
        }
        await db.SaveChangesAsync(ct);
        return found;
    }

    private async Task ImportSnapshotsAsync(Tenant tenant, StorageTarget target, IBlobStore store, Result result, CancellationToken ct)
    {
        var prefix = $"tenants/{tenant.Id}/docs/snapshots/";
        var ids = new List<Guid>();
        await foreach (var key in store.ListAsync(prefix, ct))
            if (key.EndsWith("/manifest.json", StringComparison.Ordinal) && Guid.TryParse(key[prefix.Length..].Split('/')[0], out var id)) ids.Add(id);
        foreach (var id in ids)
        {
            if (await db.Snapshots.AnyAsync(s => s.Id == id, ct)) { result.SnapshotsKnown++; continue; }
            var bytes = await content.GetDocumentAsync(tenant, target.Id, ConfigBackupService.ManifestName(id), ct);
            if (bytes is null || JsonNode.Parse(bytes) is not JsonObject m) continue;
            var items = m["items"]?.AsArray() ?? [];
            var snapshot = new Snapshot
            {
                Id = id, TenantId = tenant.Id, StorageTargetId = target.Id,
                Status = Enum.TryParse<SnapshotStatus>(m["status"]?.GetValue<string>(), out var st) ? st : SnapshotStatus.Partial,
                CreatedAt = m["createdAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow,
                CompletedAt = m["completedAt"]?.GetValue<DateTimeOffset?>(),
                Workloads = m["workloads"]?.GetValue<string>() ?? "", Label = m["label"]?.GetValue<string>(),
                ItemCount = items.Count, CreatedBy = "import",
            };
            db.Snapshots.Add(snapshot);
            foreach (var it in items.OfType<JsonObject>())
            {
                var type = it["type"]!.GetValue<string>();
                db.SnapshotItems.Add(new SnapshotItem
                {
                    SnapshotId = id, ResourceType = type, ObjectId = it["id"]!.GetValue<string>(),
                    Workload = registry.Find(type)?.Definition.Workload ?? type.Split('.')[0],
                    DisplayName = it["name"]?.GetValue<string>() ?? "", Hash = it["hash"]!.GetValue<string>(), Size = it["size"]?.GetValue<long>() ?? 0,
                });
                snapshot.TotalBytes += it["size"]?.GetValue<long>() ?? 0;
            }
            foreach (var issue in (m["issues"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                var severity = issue["severity"]?.GetValue<string>() ?? "Warning";
                db.SnapshotIssues.Add(new SnapshotIssue
                {
                    SnapshotId = id, ResourceType = issue["type"]?.GetValue<string>() ?? "", ObjectId = issue["id"]?.GetValue<string>(),
                    Severity = severity, Message = issue["message"]?.GetValue<string>() ?? "",
                });
                if (severity == "Error") snapshot.ErrorCount++; else snapshot.WarningCount++;
            }
            await db.SaveChangesAsync(ct);
            result.SnapshotsImported++;
        }
    }

    private async Task ImportDataAsync(Tenant tenant, StorageTarget target, IBlobStore store, HashSet<string> objects, Result result, CancellationToken ct)
    {
        var prefix = $"tenants/{tenant.Id}/docs/";
        var journals = new List<string>();
        await foreach (var key in store.ListAsync(prefix + DataJournal.Prefix, ct))
            if (key.Contains("/journal/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal)) journals.Add(key[prefix.Length..]);
        journals.Sort(StringComparer.Ordinal); // per source, oldest first

        var touched = new HashSet<Guid>();
        Dictionary<(Guid, string, DateTimeOffset), DataItemVersion>? known = null;
        Guid knownFor = Guid.Empty;
        foreach (var name in journals)
        {
            var bytes = await content.GetDocumentAsync(tenant, target.Id, name, ct);
            if (bytes is null) continue;
            var doc = JsonSerializer.Deserialize<DataJournal.Document>(bytes, DataJournal.Json);
            if (doc is null || doc.SchemaVersion > DataJournal.SchemaVersion) { result.Warnings.Add($"Skipped {name}: written by a newer version of MBR."); continue; }

            var src = doc.Source;
            if (!await db.DataSources.AnyAsync(s => s.Id == src.Id, ct))
            {
                db.DataSources.Add(new DataSource
                {
                    Id = src.Id, TenantId = tenant.Id, Kind = src.Kind, ExternalId = src.ExternalId, DisplayName = src.DisplayName,
                    Address = src.Address, Missing = src.Missing, LastStatus = "Imported",
                });
                result.SourcesImported++;
            }
            foreach (var c in doc.Containers)
                if (!await db.DataContainers.AnyAsync(x => x.Id == c.Id, ct))
                    // No delta link: the next backup does a full sync and carries on from there.
                    db.DataContainers.Add(new DataContainer
                    {
                        Id = c.Id, TenantId = tenant.Id, SourceId = src.Id, ExternalId = c.ExternalId, Kind = c.Kind, Name = c.Name,
                        WebUrl = c.WebUrl, RootItemId = c.RootItemId, Missing = c.Missing,
                    });
            await db.SaveChangesAsync(ct);

            if (knownFor != src.Id)
            {
                known = (await db.DataItemVersions.Where(v => v.SourceId == src.Id).ToListAsync(ct))
                    .GroupBy(v => (v.ContainerId, v.ItemId, v.CapturedAt)).ToDictionary(g => g.Key, g => g.First());
                knownFor = src.Id;
            }
            foreach (var v in doc.Versions)
            {
                if (known!.TryGetValue((v.ContainerId, v.ItemId, v.CapturedAt), out var row))
                {
                    if (row.SupersededAt != v.SupersededAt || row.DeletedAt != v.DeletedAt)
                    {
                        row.SupersededAt ??= v.SupersededAt;
                        row.DeletedAt ??= v.DeletedAt;
                        result.VersionsUpdated++;
                    }
                    continue;
                }
                // Pruned by retention since the journal was written: its content is gone, so it can't be restored.
                if (!v.IsFolder && v.Chunks.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(h => !objects.Contains(h)))
                {
                    result.VersionsWithoutContent++;
                    continue;
                }
                var added = new DataItemVersion
                {
                    TenantId = tenant.Id, SourceId = src.Id, ContainerId = v.ContainerId, ItemId = v.ItemId, ParentItemId = v.ParentItemId,
                    Name = v.Name, Path = v.Path, IsFolder = v.IsFolder, Size = v.Size, Chunks = v.Chunks, StorageTargetId = target.Id,
                    ETag = v.ETag, CTag = v.CTag, QuickXorHash = v.QuickXorHash, ContentType = v.ContentType, ModifiedAt = v.ModifiedAt,
                    ModifiedBy = v.ModifiedBy, CapturedAt = v.CapturedAt, SupersededAt = v.SupersededAt, DeletedAt = v.DeletedAt,
                    MetadataJson = v.MetadataJson,
                };
                db.DataItemVersions.Add(added);
                known[(v.ContainerId, v.ItemId, v.CapturedAt)] = added;
                result.VersionsImported++;
            }
            await db.SaveChangesAsync(ct);
            touched.Add(src.Id);
        }

        foreach (var id in touched)
        {
            var source = await db.DataSources.FirstAsync(s => s.Id == id, ct);
            var current = db.DataItemVersions.Where(v => v.SourceId == id && !v.IsFolder && v.SupersededAt == null && v.DeletedAt == null);
            source.ItemCount = await current.CountAsync(ct);
            source.TotalBytes = source.ItemCount == 0 ? 0 : await current.SumAsync(v => v.Size, ct);
            source.LastBackupAt ??= await db.DataItemVersions.Where(v => v.SourceId == id).MaxAsync(v => (DateTimeOffset?)v.CapturedAt, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// "storage.import": rebuilds the index from a storage target (parameter storageTargetId). Without a tenant it imports every
/// tenant found there (global admins); with one, only that tenant.
/// </summary>
public sealed class StorageImportJob : IJobHandler
{
    public string Type => "storage.import";
    public TenantRole RequiredRole => TenantRole.Admin;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<MbrDbContext>();
        var targetId = context.Parameters.GetGuid("storageTargetId")
            ?? context.Tenant?.DefaultStorageTargetId
            ?? (await db.StorageTargets.AsNoTracking().FirstOrDefaultAsync(s => s.IsDefault, ct))?.Id
            ?? throw new ArgumentException("Give storageTargetId.");
        var r = await context.Services.GetRequiredService<StorageImportService>().ImportAsync(targetId, context.Tenant?.Id, context, ct);
        await context.InfoAsync($"{r.TenantsFound} tenant(s) found ({r.TenantsCreated} added), {r.SnapshotsImported} snapshot(s) imported " +
                                $"({r.SnapshotsKnown} already known), {r.VersionsImported} file and mail version(s) imported");
        if (r.VersionsWithoutContent > 0)
            await context.InfoAsync($"{r.VersionsWithoutContent} version(s) were skipped: retention had already deleted their content");
        return r.Warnings.Count > 0 ? JobOutcome.Partial(r.ToJson()) : JobOutcome.Success(r.ToJson());
    }
}
