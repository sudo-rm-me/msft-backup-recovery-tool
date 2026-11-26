using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Files;

/// <summary>Point-in-time browsing of backed-up data and streaming of item content.</summary>
public sealed class DataReader(MbrDbContext db, ITenantContentStore content)
{
    /// <summary>Versions valid at <paramref name="at"/> (or current ones).</summary>
    public IQueryable<DataItemVersion> AsOf(Guid containerId, DateTimeOffset? at)
    {
        var q = db.DataItemVersions.AsNoTracking().Where(v => v.ContainerId == containerId);
        if (at is null) return q.Where(v => v.SupersededAt == null && v.DeletedAt == null);
        var t = at.Value.ToUniversalTime();
        return q.Where(v => v.CapturedAt <= t && (v.SupersededAt == null || v.SupersededAt > t) && (v.DeletedAt == null || v.DeletedAt > t));
    }

    /// <summary>The last version of each deleted item (what it looked like when it was deleted).</summary>
    public IQueryable<DataItemVersion> Deleted(Guid containerId) =>
        db.DataItemVersions.AsNoTracking().Where(v => v.ContainerId == containerId && v.SupersededAt == null && v.DeletedAt != null);

    public async Task<List<DataItemVersion>> ChildrenAsync(DataContainer c, string? parentId, DateTimeOffset? at, bool includeDeleted, CancellationToken ct)
    {
        parentId ??= c.RootItemId;
        var list = await AsOf(c.Id, at).Where(v => v.ParentItemId == parentId).ToListAsync(ct);
        if (includeDeleted && at is null)
        {
            var deleted = await Deleted(c.Id).Where(v => v.ParentItemId == parentId).ToListAsync(ct);
            // An item deleted and recreated under the same ID only shows once (current wins).
            list.AddRange(deleted.Where(d => list.All(x => x.ItemId != d.ItemId)));
        }
        return list.OrderByDescending(v => v.IsFolder).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The folders from the root down to <paramref name="itemId"/> (inclusive), as of a time.</summary>
    public async Task<List<DataItemVersion>> BreadcrumbAsync(DataContainer c, string itemId, DateTimeOffset? at, CancellationToken ct)
    {
        var chain = new List<DataItemVersion>();
        var current = itemId;
        for (var depth = 0; depth < 64 && current != null && current != c.RootItemId; depth++)
        {
            var v = await VersionAtAsync(c.Id, current, at, ct);
            if (v is null) break;
            chain.Insert(0, v);
            current = v.ParentItemId;
        }
        return chain;
    }

    /// <summary>The version of an item valid at a time; with no time, the latest (even if deleted).</summary>
    public async Task<DataItemVersion?> VersionAtAsync(Guid containerId, string itemId, DateTimeOffset? at, CancellationToken ct) =>
        at is null
            ? await db.DataItemVersions.AsNoTracking().Where(v => v.ContainerId == containerId && v.ItemId == itemId)
                .OrderByDescending(v => v.CapturedAt).FirstOrDefaultAsync(ct)
            : await AsOf(containerId, at).FirstOrDefaultAsync(v => v.ItemId == itemId, ct);

    public Task<List<DataItemVersion>> HistoryAsync(Guid containerId, string itemId, CancellationToken ct) =>
        db.DataItemVersions.AsNoTracking().Where(v => v.ContainerId == containerId && v.ItemId == itemId)
            .OrderByDescending(v => v.CapturedAt).ToListAsync(ct);

    /// <summary>Writes an item's content (decrypting each chunk) to <paramref name="output"/>.</summary>
    public async Task CopyContentAsync(Tenant tenant, DataItemVersion v, Stream output, CancellationToken ct)
    {
        foreach (var hash in v.Chunks.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = await content.GetObjectAsync(tenant, v.StorageTargetId, hash, ct);
            await output.WriteAsync(bytes, ct);
        }
    }

    public async Task<byte[]> ReadContentAsync(Tenant tenant, DataItemVersion v, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await CopyContentAsync(tenant, v, ms, ct);
        return ms.ToArray();
    }
}

/// <summary>Keeps data chunks referenced by any stored version from being garbage-collected.</summary>
public sealed class DataHashSource(MbrDbContext db) : IReferencedHashSource
{
    public async Task<HashSet<string>> GetReferencedHashesAsync(Guid tenantId, CancellationToken ct)
    {
        var set = new HashSet<string>();
        await foreach (var chunks in db.DataItemVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.Chunks != "").Select(v => v.Chunks).AsAsyncEnumerable().WithCancellation(ct))
            foreach (var h in chunks.Split(',', StringSplitOptions.RemoveEmptyEntries)) set.Add(h);
        return set;
    }
}

/// <summary>Prunes data versions older than the tenant's data retention: superseded versions and deleted items. Current versions are kept.</summary>
public sealed class DataRetention(MbrDbContext db) : IRetentionParticipant
{
    public async Task<int> PruneAsync(Tenant tenant, TenantSettings settings, DateTimeOffset now, IJobContext? job, CancellationToken ct)
    {
        var cutoff = now.AddDays(-settings.DataRetentionDays);
        var superseded = await db.DataItemVersions.Where(v => v.TenantId == tenant.Id && v.SupersededAt != null && v.SupersededAt < cutoff).ExecuteDeleteAsync(ct);
        var deleted = await db.DataItemVersions.Where(v => v.TenantId == tenant.Id && v.SupersededAt == null && v.DeletedAt != null && v.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
        if (job != null && superseded + deleted > 0)
            await job.InfoAsync($"Pruned {superseded} older file/message version(s) and {deleted} item(s) deleted more than {settings.DataRetentionDays} days ago");
        return superseded + deleted;
    }
}
