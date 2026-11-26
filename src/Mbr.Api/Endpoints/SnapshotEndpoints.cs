using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Catalog;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    static partial void MapFeatureRoutes(RouteGroupBuilder api)
    {
        MapCatalog(api);
        MapSnapshots(api);
        MapFeatureRoutesPhase2Plus(api);
    }

    static partial void MapFeatureRoutesPhase2Plus(RouteGroupBuilder api);

    internal static object SnapshotDto(Snapshot s) => new
    {
        s.Id, s.TenantId, s.StorageTargetId, s.JobId, s.Status, s.CreatedAt, s.CompletedAt, workloads = s.Workloads.Split(',', StringSplitOptions.RemoveEmptyEntries),
        s.ItemCount, s.ErrorCount, s.WarningCount, s.TotalBytes, s.AddedCount, s.ModifiedCount, s.RemovedCount, s.Label, s.IsBaseline,
        s.ExpiresAt, s.CreatedBy,
    };

    internal static async Task<(Snapshot Snapshot, Tenant Tenant)> LoadSnapshotAsync(MbrDbContext db, OperatorContext ops, Guid id, TenantRole role, CancellationToken ct)
    {
        var s = await db.Snapshots.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Snapshot not found.");
        var t = await LoadTenantAsync(db, ops, s.TenantId, role, ct);
        return (s, t);
    }

    public record SnapshotUpdate(string? Label, bool? IsBaseline);
    public record TenantSettingsDto(int ConfigRetentionDays, int ConfigKeepMinimum, int DataRetentionDays, bool DriftAlerts, bool RequireRestoreApproval = false);

    private static void MapCatalog(RouteGroupBuilder api)
    {
        api.MapGet("/catalog", (ResourceRegistry registry) => registry.Ordered().Select(h => new
        {
            h.Definition.Type, h.Definition.Workload, h.Definition.DisplayName, restorable = h.Definition.Restore.Supported,
            h.Definition.Notes, restoreNotes = h.Definition.Restore.Notes, h.Definition.DependsOn,
        }));

        api.MapGet("/tenants/{id:guid}/settings", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            var s = await db.TenantSettings.FindAsync([id], ct) ?? new TenantSettings { TenantId = id };
            return new TenantSettingsDto(s.ConfigRetentionDays, s.ConfigKeepMinimum, s.DataRetentionDays, s.DriftAlerts, s.RequireRestoreApproval);
        });

        api.MapPut("/tenants/{id:guid}/settings", async (Guid id, TenantSettingsDto req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Admin, ct);
            if (req.ConfigRetentionDays < 1 || req.DataRetentionDays < 1 || req.ConfigKeepMinimum < 1)
                throw new ArgumentException("Retention values must be at least 1.");
            var s = await db.TenantSettings.FindAsync([id], ct);
            if (s is null) db.TenantSettings.Add(s = new TenantSettings { TenantId = id });
            s.ConfigRetentionDays = req.ConfigRetentionDays;
            s.ConfigKeepMinimum = req.ConfigKeepMinimum;
            s.DataRetentionDays = req.DataRetentionDays;
            s.DriftAlerts = req.DriftAlerts;
            s.RequireRestoreApproval = req.RequireRestoreApproval;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "tenant.settings", id, null, req, Ip(ctx), ct);
            return req;
        });
    }

    private static void MapSnapshots(RouteGroupBuilder api)
    {
        api.MapGet("/tenants/{id:guid}/snapshots", async (Guid id, int? take, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            var list = await db.Snapshots.AsNoTracking().Where(s => s.TenantId == id).OrderByDescending(s => s.CreatedAt)
                .Take(Math.Clamp(take ?? 200, 1, 2000)).ToListAsync(ct);
            return list.Select(SnapshotDto);
        });

        api.MapGet("/snapshots/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var (s, _) = await LoadSnapshotAsync(db, ops, id, TenantRole.Viewer, ct);
            var byType = await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == id)
                .GroupBy(i => new { i.Workload, i.ResourceType }).Select(g => new { g.Key.Workload, g.Key.ResourceType, count = g.Count() }).ToListAsync(ct);
            var issues = await db.SnapshotIssues.AsNoTracking().Where(i => i.SnapshotId == id)
                .Select(i => new { i.ResourceType, i.ObjectId, i.Severity, i.Message }).ToListAsync(ct);
            return new { snapshot = SnapshotDto(s), types = byType.OrderBy(t => t.Workload).ThenBy(t => t.ResourceType), issues };
        });

        api.MapGet("/snapshots/{id:guid}/items", async (Guid id, string? type, string? search, int? skip, int? take, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadSnapshotAsync(db, ops, id, TenantRole.Viewer, ct);
            var q = db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == id);
            if (!string.IsNullOrEmpty(type)) q = q.Where(i => i.ResourceType == type);
            if (!string.IsNullOrEmpty(search)) q = q.Where(i => i.DisplayName.ToLower().Contains(search.ToLower()) || i.ObjectId == search);
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(i => i.ResourceType).ThenBy(i => i.DisplayName).Skip(skip ?? 0).Take(Math.Clamp(take ?? 200, 1, 1000))
                .Select(i => new { i.Id, i.ResourceType, i.Workload, i.ObjectId, i.DisplayName, i.Hash, i.Size }).ToListAsync(ct);
            return new { total, items };
        });

        api.MapGet("/snapshots/{id:guid}/items/{itemId:long}", async (Guid id, long itemId, MbrDbContext db, OperatorContext ops, SnapshotReader reader, CancellationToken ct) =>
        {
            var (s, t) = await LoadSnapshotAsync(db, ops, id, TenantRole.Viewer, ct);
            var item = await db.SnapshotItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SnapshotId == id, ct) ?? throw new KeyNotFoundException();
            var content = await reader.GetContentAsync(t, s, item, ct);
            return new { item.Id, item.ResourceType, item.Workload, item.ObjectId, item.DisplayName, item.Hash, item.Size, content };
        });

        // History of one object across snapshots: one entry per distinct version.
        api.MapGet("/tenants/{id:guid}/objects/{type}/{objectId}/history", async (Guid id, string type, string objectId, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            var rows = await (from i in db.SnapshotItems.AsNoTracking()
                              join s in db.Snapshots.AsNoTracking() on i.SnapshotId equals s.Id
                              where s.TenantId == id && i.ResourceType == type && i.ObjectId == objectId
                              orderby s.CreatedAt
                              select new { snapshotId = s.Id, s.CreatedAt, itemId = i.Id, i.Hash, i.DisplayName }).ToListAsync(ct);
            var versions = rows.Where((r, idx) => idx == 0 || rows[idx - 1].Hash != r.Hash).Reverse();
            return versions;
        });

        api.MapPatch("/snapshots/{id:guid}", async (Guid id, SnapshotUpdate req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var (s, t) = await LoadSnapshotAsync(db, ops, id, TenantRole.BackupOperator, ct);
            if (req.Label != null) s.Label = req.Label.Length == 0 ? null : req.Label;
            if (req.IsBaseline is { } b)
            {
                if (b) await db.Snapshots.Where(x => x.TenantId == t.Id && x.IsBaseline).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsBaseline, false), ct);
                s.IsBaseline = b;
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "snapshot.update", t.Id, id.ToString(), req, Ip(ctx), ct);
            return SnapshotDto(s);
        });

        api.MapDelete("/snapshots/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, RetentionService retention, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var (s, t) = await LoadSnapshotAsync(db, ops, id, TenantRole.Admin, ct);
            if (s.Status == SnapshotStatus.Running) throw new InvalidOperationException("The snapshot is still being written.");
            await retention.DeleteSnapshotAsync(t, s, ct);
            await audit.WriteAsync(ops.Name, "snapshot.delete", t.Id, id.ToString(), null, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapGet("/snapshots/{id:guid}/export", async (Guid id, MbrDbContext db, OperatorContext ops, SnapshotReader reader, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var (s, t) = await LoadSnapshotAsync(db, ops, id, TenantRole.BackupOperator, ct);
            await audit.WriteAsync(ops.Name, "snapshot.export", t.Id, id.ToString(), null, Ip(ctx), ct);
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Safe(t.DisplayName)}-{s.CreatedAt:yyyyMMdd-HHmm}.zip\"";
            // Build in memory then copy: ZipArchive needs a seekable stream for some entries.
            using var ms = new MemoryStream();
            await reader.ExportZipAsync(t, s, ms, ct);
            ms.Position = 0;
            await ms.CopyToAsync(ctx.Response.Body, ct);
        });
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
