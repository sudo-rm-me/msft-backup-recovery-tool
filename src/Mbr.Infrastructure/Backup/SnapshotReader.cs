using System.IO.Compression;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Backup;

/// <summary>Reads snapshot content back out of storage.</summary>
public sealed class SnapshotReader(MbrDbContext db, ITenantContentStore content)
{
    public async Task<JsonObject> GetContentAsync(Tenant tenant, Snapshot snapshot, SnapshotItem item, CancellationToken ct)
    {
        var bytes = await content.GetObjectAsync(tenant, snapshot.StorageTargetId, item.Hash, ct);
        return JsonNode.Parse(bytes)!.AsObject();
    }

    /// <summary>Loads all items of a snapshot (optionally filtered by type) with their content.</summary>
    public async Task<List<(SnapshotItem Item, JsonObject Content)>> LoadAsync(Tenant tenant, Snapshot snapshot, IReadOnlyCollection<string>? types, CancellationToken ct)
    {
        var q = db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshot.Id);
        if (types is { Count: > 0 }) q = q.Where(i => types.Contains(i.ResourceType));
        var items = await q.ToListAsync(ct);
        var result = new (SnapshotItem, JsonObject)[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => result[i] = (items[i], await GetContentAsync(tenant, snapshot, items[i], token)));
        return result.ToList();
    }

    /// <summary>Writes a zip with the manifest and one decrypted JSON file per object (workload/type/name__id.json).</summary>
    public async Task ExportZipAsync(Tenant tenant, Snapshot snapshot, Stream output, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = await content.GetDocumentAsync(tenant, snapshot.StorageTargetId, ConfigBackupService.ManifestName(snapshot.Id), ct);
        if (manifest != null)
        {
            await using var s = await zip.CreateEntry("manifest.json").OpenAsync(ct);
            await s.WriteAsync(manifest, ct);
        }
        var items = await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshot.Id).OrderBy(i => i.ResourceType).ToListAsync(ct);
        foreach (var item in items)
        {
            var bytes = await content.GetObjectAsync(tenant, snapshot.StorageTargetId, item.Hash, ct);
            var name = $"{item.Workload}/{item.ResourceType}/{Safe(item.DisplayName)}__{Safe(item.ObjectId)}.json";
            await using var s = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(ct);
            await s.WriteAsync(bytes, ct);
        }
    }

    private static string Safe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 80 ? clean[..80] : clean;
    }
}
