using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Diff;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    static partial void MapFeatureRoutesPhase2Plus(RouteGroupBuilder api)
    {
        MapDiff(api);
        MapFeatureRoutesPhase3Plus(api);
    }

    static partial void MapFeatureRoutesPhase3Plus(RouteGroupBuilder api);

    /// <summary>Resolves a snapshot ID or the keywords "baseline" / "latest" (which need a tenant).</summary>
    internal static async Task<Snapshot> ResolveSnapshotAsync(MbrDbContext db, OperatorContext ops, string reference, Guid? tenantId, CancellationToken ct)
    {
        Snapshot? s;
        if (Guid.TryParse(reference, out var id)) s = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        else
        {
            if (tenantId is null) throw new ArgumentException($"'{reference}' needs a tenantId.");
            var q = db.Snapshots.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != SnapshotStatus.Running);
            s = reference.ToLowerInvariant() switch
            {
                "baseline" => await q.FirstOrDefaultAsync(x => x.IsBaseline, ct),
                "latest" => await q.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct),
                "previous" => await q.OrderByDescending(x => x.CreatedAt).Skip(1).FirstOrDefaultAsync(ct),
                _ => throw new ArgumentException($"Unknown snapshot reference '{reference}'. Use an ID, baseline, latest or previous."),
            };
        }
        if (s is null) throw new KeyNotFoundException($"Snapshot '{reference}' not found.");
        await ops.EnsureTenantRoleAsync(s.TenantId, TenantRole.Viewer, ct);
        return s;
    }

    private static void MapDiff(RouteGroupBuilder api)
    {
        api.MapGet("/diff", async (string from, string to, Guid? tenantId, string? types, MbrDbContext db, OperatorContext ops, DiffService diff, CancellationToken ct) =>
        {
            var a = await ResolveSnapshotAsync(db, ops, from, tenantId, ct);
            var b = await ResolveSnapshotAsync(db, ops, to, tenantId, ct);
            var typeList = types?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var summary = await diff.CompareAsync(a, b, typeList, ct);
            return new { from = SnapshotDto(a), to = SnapshotDto(b), summary };
        });

        api.MapGet("/diff/item", async (long? fromItem, long? toItem, MbrDbContext db, OperatorContext ops, DiffService diff, CancellationToken ct) =>
        {
            if (fromItem is null && toItem is null) throw new ArgumentException("fromItem or toItem is required.");
            async Task<(SnapshotItem?, Snapshot?, Tenant?)> Load(long? itemId)
            {
                if (itemId is null) return (null, null, null);
                var item = await db.SnapshotItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new KeyNotFoundException();
                var (s, t) = await LoadSnapshotAsync(db, ops, item.SnapshotId, TenantRole.Viewer, ct);
                return (item, s, t);
            }
            var (ia, sa, ta) = await Load(fromItem);
            var (ib, sb, tb) = await Load(toItem);
            var r = await diff.CompareItemsAsync(ta, sa, ia, tb, sb, ib, ct);
            return new
            {
                before = r.Before, after = r.After,
                changes = r.Changes.Select(c => new { c.Path, kind = c.Kind.ToString(), c.Before, c.After }),
                displayName = ib?.DisplayName ?? ia?.DisplayName, type = ib?.ResourceType ?? ia?.ResourceType,
            };
        });
    }
}
