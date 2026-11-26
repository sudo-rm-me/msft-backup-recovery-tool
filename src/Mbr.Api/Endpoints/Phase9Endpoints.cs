using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.M365Backup;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record M365RestoreRequest(string UnitId, string RestorePointId, bool InPlace);

    static partial void MapFeatureRoutesPhase9Plus(RouteGroupBuilder api)
    {
        // ---- Microsoft 365 Backup (Backup Storage)
        api.MapGet("/tenants/{id:guid}/m365backup", async (Guid id, MbrDbContext db, OperatorContext ops, M365BackupStorage m365, CancellationToken ct) =>
            await m365.StatusAsync(await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct), ct));

        api.MapGet("/tenants/{id:guid}/m365backup/units", async (Guid id, string? policyId, MbrDbContext db, OperatorContext ops, M365BackupStorage m365, CancellationToken ct) =>
            await m365.UnitsAsync(await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct), policyId, ct));

        api.MapGet("/tenants/{id:guid}/m365backup/units/{unitId}/restorePoints", async (Guid id, string unitId, DateTimeOffset? from, DateTimeOffset? to,
            MbrDbContext db, OperatorContext ops, M365BackupStorage m365, CancellationToken ct) =>
            await m365.RestorePointsAsync(await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct), unitId, from, to, ct));

        api.MapPost("/tenants/{id:guid}/m365backup/restore", async (Guid id, M365RestoreRequest req, MbrDbContext db, OperatorContext ops, M365BackupStorage m365,
            AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var tenant = await LoadTenantAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (string.IsNullOrEmpty(req.UnitId) || string.IsNullOrEmpty(req.RestorePointId)) throw new ArgumentException("Choose a protection unit and a restore point.");
            var session = await m365.RestoreAsync(tenant, req.UnitId, req.RestorePointId, req.InPlace, ct);
            await audit.WriteAsync(ops.Name, "m365backup.restore", tenant.Id, req.UnitId, new { req.RestorePointId, req.InPlace, session = session.Id }, Ip(ctx), ct);
            return session;
        });

        api.MapGet("/tenants/{id:guid}/m365backup/sessions", async (Guid id, MbrDbContext db, OperatorContext ops, M365BackupStorage m365, CancellationToken ct) =>
            await m365.SessionsAsync(await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct), ct));

    }
}
