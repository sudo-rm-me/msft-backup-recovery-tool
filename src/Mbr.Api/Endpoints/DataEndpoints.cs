using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Files;
using Mbr.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record UpdateDataSourceRequest(bool? Enabled);

    static partial void MapFeatureRoutesPhase5Plus(RouteGroupBuilder api)
    {
        MapData(api);
        MapFeatureRoutesPhase6Plus(api);
    }

    static partial void MapFeatureRoutesPhase6Plus(RouteGroupBuilder api);

    internal static object DataSourceDto(DataSource s) => new
    {
        s.Id, s.TenantId, kind = s.Kind.ToString(), s.ExternalId, s.DisplayName, s.Address, s.Enabled, s.Missing, s.DiscoveredAt,
        s.LastBackupAt, s.LastStatus, s.LastError, s.ItemCount, s.TotalBytes,
    };

    internal static object VersionDto(DataItemVersion v) => new
    {
        v.Id, v.SourceId, v.ContainerId, v.ItemId, v.ParentItemId, v.Name, v.Path, v.IsFolder, v.Size, v.ContentType, v.ModifiedAt, v.ModifiedBy,
        v.CapturedAt, v.SupersededAt, v.DeletedAt, chunkCount = v.Chunks.Length == 0 ? 0 : v.Chunks.Count(ch => ch == ',') + 1,
    };

    private static async Task<(DataContainer Container, DataSource Source)> LoadContainerAsync(MbrDbContext db, OperatorContext ops, Guid id, TenantRole role, CancellationToken ct)
    {
        var c = await db.DataContainers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Container not found.");
        await ops.EnsureTenantRoleAsync(c.TenantId, role, ct);
        var s = await db.DataSources.AsNoTracking().FirstAsync(x => x.Id == c.SourceId, ct);
        return (c, s);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? "item" : clean.Length > 120 ? clean[..120] : clean;
    }

    private static void MapData(RouteGroupBuilder api)
    {
        api.MapPost("/tenants/{id:guid}/data/restore", async (Guid id, DataRestoreRequest req, MbrDbContext db, OperatorContext ops, JobQueue queue,
            AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (req.ItemIds is not { Count: > 0 }) throw new ArgumentException("Select at least one item to restore.");
            var container = await db.DataContainers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ContainerId && c.TenantId == id, ct)
                            ?? throw new KeyNotFoundException("Container not found.");
            if (req.TargetSourceId is { } t && !await db.DataSources.AnyAsync(s => s.Id == t && s.TenantId == id, ct))
                throw new ArgumentException("The target must be a source in the same tenant.");
            var parameters = System.Text.Json.JsonSerializer.SerializeToNode(req, DataRestoreJob.Json)!.AsObject();
            var job = await queue.EnqueueAsync("restore.data", id, parameters, ops.Name, maxAttempts: 1, ct: ct);
            await audit.WriteAsync(ops.Name, "data.restoreRequested", id, job.Id.ToString(),
                new { container = container.Name, items = req.ItemIds.Count, req.At, req.TargetSourceId, req.Location, req.Conflict }, Ip(ctx), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
        });

        api.MapGet("/tenants/{id:guid}/data/sources", async (Guid id, string? kind, string? search, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            var q = db.DataSources.AsNoTracking().Where(s => s.TenantId == id);
            if (Enum.TryParse<DataSourceKind>(kind, true, out var k)) q = q.Where(s => s.Kind == k);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(s => s.DisplayName.ToLower().Contains(search.ToLower()) || (s.Address != null && s.Address.ToLower().Contains(search.ToLower())));
            var list = await q.OrderBy(s => s.Kind).ThenBy(s => s.DisplayName).Take(5000).ToListAsync(ct);
            return list.Select(DataSourceDto);
        });

        api.MapGet("/data/sources/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var s = await db.DataSources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(s.TenantId, TenantRole.Viewer, ct);
            var containers = await db.DataContainers.AsNoTracking().Where(c => c.SourceId == id).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.Kind, c.WebUrl, c.LastSyncAt, c.Missing, c.RootItemId, pendingRetries = c.RetryItemIds != "", resuming = c.ResumeLink != null })
                .ToListAsync(ct);
            var first = await db.DataItemVersions.AsNoTracking().Where(v => v.SourceId == id).MinAsync(v => (DateTimeOffset?)v.CapturedAt, ct);
            return new { source = DataSourceDto(s), containers, protectedSince = first };
        });

        api.MapPatch("/data/sources/{id:guid}", async (Guid id, UpdateDataSourceRequest req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var s = await db.DataSources.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(s.TenantId, TenantRole.BackupOperator, ct);
            if (req.Enabled is { } e) s.Enabled = e;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "data.sourceUpdated", s.TenantId, s.Id.ToString(), req, Ip(ctx), ct);
            return DataSourceDto(s);
        });

        api.MapGet("/data/containers/{id:guid}/children", async (Guid id, string? parent, DateTimeOffset? at, bool? deleted, MbrDbContext db, OperatorContext ops,
            DataReader reader, CancellationToken ct) =>
        {
            var (c, _) = await LoadContainerAsync(db, ops, id, TenantRole.Viewer, ct);
            var items = await reader.ChildrenAsync(c, string.IsNullOrEmpty(parent) ? null : parent, at, deleted ?? false, ct);
            var crumbs = string.IsNullOrEmpty(parent) ? [] : await reader.BreadcrumbAsync(c, parent, at, ct);
            return new
            {
                items = items.Select(VersionDto),
                breadcrumb = crumbs.Select(v => new { v.ItemId, v.Name, v.DeletedAt }),
            };
        });

        api.MapGet("/data/containers/{id:guid}/items/{itemId}/history", async (Guid id, string itemId, MbrDbContext db, OperatorContext ops, DataReader reader, CancellationToken ct) =>
        {
            await LoadContainerAsync(db, ops, id, TenantRole.Viewer, ct);
            return (await reader.HistoryAsync(id, itemId, ct)).Select(VersionDto);
        });

        api.MapGet("/data/versions/{id:long}", async (long id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var v = await db.DataItemVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(v.TenantId, TenantRole.Viewer, ct);
            return VersionDto(v);
        });

        // Downloading backed-up content hands out tenant data: restore operators only, and audited.
        api.MapGet("/data/versions/{id:long}/content", async (long id, MbrDbContext db, OperatorContext ops, DataReader reader, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var v = await db.DataItemVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(v.TenantId, TenantRole.RestoreOperator, ct);
            if (v.IsFolder) throw new ArgumentException("Folders have no content; restore the folder instead.");
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == v.TenantId, ct);
            await audit.WriteAsync(ops.Name, "data.downloaded", v.TenantId, $"{v.SourceId}/{v.ItemId}", new { v.Name, v.Path, version = v.Id, v.Size }, Ip(ctx), ct);
            var fileName = v.ContentType switch
            {
                "message/rfc822" => SafeFileName(v.Name) + ".eml",
                "application/json" => SafeFileName(v.Name) + ".json",
                _ => v.Name,
            };
            return Results.Stream(async output => await reader.CopyContentAsync(tenant, v, output, ct), v.ContentType ?? "application/octet-stream", fileName);
        });

        api.MapGet("/tenants/{id:guid}/data/search", async (Guid id, string q, Guid? sourceId, bool? deleted, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            if (string.IsNullOrWhiteSpace(q) || q.Length < 2) throw new ArgumentException("Search for at least 2 characters.");
            var term = q.ToLower();
            var query = db.DataItemVersions.AsNoTracking().Where(v => v.TenantId == id && v.SupersededAt == null && v.Name.ToLower().Contains(term));
            if (sourceId is { } sid) query = query.Where(v => v.SourceId == sid);
            query = deleted == true ? query.Where(v => v.DeletedAt != null) : deleted == false ? query.Where(v => v.DeletedAt == null) : query;
            var hits = await query.OrderByDescending(v => v.CapturedAt).Take(200).ToListAsync(ct);
            var sourceIds = hits.Select(h => h.SourceId).Distinct().ToList();
            var names = await db.DataSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
            return hits.Select(h => new { item = VersionDto(h), sourceName = names.GetValueOrDefault(h.SourceId) });
        });
    }
}
