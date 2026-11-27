using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Core.Restore;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Jobs;
using Mbr.Infrastructure.Restore;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record CreateRestorePlanRequest(Guid SnapshotId, Guid? TargetTenantId, List<string>? Workloads, List<string>? Types, List<long>? ItemIds, RestoreOptions? Options);
    public record ExcludeActionsRequest(List<int>? Exclude, List<int>? Include);
    public record RejectRequest(string? Reason);

    static partial void MapFeatureRoutesPhase3Plus(RouteGroupBuilder api)
    {
        MapRestore(api);
        MapFeatureRoutesPhase4Plus(api);
    }

    static partial void MapFeatureRoutesPhase4Plus(RouteGroupBuilder api);

    internal static object RestorePlanDto(RestorePlan p, string? sourceName = null, string? targetName = null) => new
    {
        p.Id, p.TenantId, p.SourceTenantId, p.SourceSnapshotId, status = p.Status.ToString(), p.CreatedBy, p.CreatedAt,
        p.ApprovedBy, p.ApprovedAt, p.PlanJobId, p.ApplyJobId, p.AppliedAt, p.CreateCount, p.UpdateCount, p.RecycleBinCount,
        p.DeleteCount, p.SkipCount, p.WarningCount, p.SucceededCount, p.FailedCount, p.Error,
        sourceTenantName = sourceName, targetTenantName = targetName, crossTenant = p.TenantId != p.SourceTenantId,
    };

    private static async Task<RestorePlan> LoadPlanAsync(MbrDbContext db, OperatorContext ops, Guid id, TenantRole role, CancellationToken ct)
    {
        var plan = await db.RestorePlans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new KeyNotFoundException("Restore plan not found.");
        await ops.EnsureTenantRoleAsync(plan.TenantId, role, ct);
        return plan;
    }

    private static async Task<Job> StartApplyAsync(RestorePlan plan, MbrDbContext db, JobQueue queue, string actor, CancellationToken ct)
    {
        plan.Status = RestorePlanStatus.Applying;
        var job = await queue.EnqueueAsync("restore.apply", plan.TenantId, new JsonObject { ["planId"] = plan.Id.ToString() }, actor, maxAttempts: 1, ct: ct);
        plan.ApplyJobId = job.Id;
        await db.SaveChangesAsync(ct);
        return job;
    }

    private static void MapRestore(RouteGroupBuilder api)
    {
        api.MapPost("/restore/plans", async (CreateRestorePlanRequest req, MbrDbContext db, OperatorContext ops, JobQueue queue, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var (snapshot, _) = await LoadSnapshotAsync(db, ops, req.SnapshotId, TenantRole.Viewer, ct);
            if (snapshot.Status == SnapshotStatus.Running) throw new InvalidOperationException("The snapshot is still being taken.");
            var targetId = req.TargetTenantId ?? snapshot.TenantId;
            var target = await LoadTenantAsync(db, ops, targetId, TenantRole.RestoreOperator, ct);
            if (target.Status != TenantStatus.Active) throw new InvalidOperationException($"Tenant '{target.DisplayName}' isn't active ({target.Status}).");
            var options = req.Options ?? new RestoreOptions();
            if (options.DeleteExtra) await ops.EnsureTenantRoleAsync(targetId, TenantRole.Admin, ct);
            _ = new DomainMap(options.DomainMap); // validates the domain names
            var request = new RestoreRequest
            {
                SnapshotId = snapshot.Id, TargetTenantId = targetId, Workloads = req.Workloads ?? [], Types = req.Types ?? [],
                ItemIds = req.ItemIds ?? [], Options = options,
            };
            var plan = new RestorePlan
            {
                TenantId = targetId, SourceTenantId = snapshot.TenantId, SourceSnapshotId = snapshot.Id, CreatedBy = ops.Name,
                RequestJson = JsonSerializer.Serialize(request, RestoreEngine.Json),
            };
            db.RestorePlans.Add(plan);
            await db.SaveChangesAsync(ct);
            var job = await queue.EnqueueAsync("restore.plan", targetId, new JsonObject { ["planId"] = plan.Id.ToString() }, ops.Name, ct: ct);
            plan.PlanJobId = job.Id;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "restore.planned", targetId, plan.Id.ToString(), new { snapshot = snapshot.Id, request.Workloads, request.Types, items = request.ItemIds.Count, options }, Ip(ctx), ct);
            return Results.Accepted($"/api/restore/plans/{plan.Id}", new { planId = plan.Id, jobId = job.Id });
        });

        api.MapGet("/tenants/{id:guid}/restore/plans", async (Guid id, int? take, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            var plans = await db.RestorePlans.AsNoTracking().Where(p => p.TenantId == id).OrderByDescending(p => p.CreatedAt)
                .Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
            var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.DisplayName, ct);
            return plans.Select(p => RestorePlanDto(p, names.GetValueOrDefault(p.SourceTenantId), names.GetValueOrDefault(p.TenantId)));
        });

        api.MapGet("/restore/plans/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.Viewer, ct);
            var names = await db.Tenants.AsNoTracking().Where(t => t.Id == plan.TenantId || t.Id == plan.SourceTenantId).ToDictionaryAsync(t => t.Id, t => t.DisplayName, ct);
            var doc = JsonSerializer.Deserialize<RestorePlanDocument>(plan.PlanJson, RestoreEngine.Json) ?? new RestorePlanDocument();
            var request = JsonSerializer.Deserialize<RestoreRequest>(plan.RequestJson, RestoreEngine.Json);
            return new { plan = RestorePlanDto(plan, names.GetValueOrDefault(plan.SourceTenantId), names.GetValueOrDefault(plan.TenantId)), request, doc.Actions, doc.Notes };
        });

        api.MapPatch("/restore/plans/{id:guid}/actions", async (Guid id, ExcludeActionsRequest req, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (plan.Status != RestorePlanStatus.Draft) throw new InvalidOperationException($"Only draft plans can be edited (this one is {plan.Status}).");
            var doc = JsonSerializer.Deserialize<RestorePlanDocument>(plan.PlanJson, RestoreEngine.Json)!;
            foreach (var a in doc.Actions)
            {
                if (req.Exclude?.Contains(a.Index) == true) a.Excluded = true;
                if (req.Include?.Contains(a.Index) == true) a.Excluded = false;
            }
            plan.PlanJson = JsonSerializer.Serialize(doc, RestoreEngine.Json);
            await db.SaveChangesAsync(ct);
            return new { included = doc.Actions.Count(a => !a.Excluded && a.Kind != RestoreActionKind.Skip), excluded = doc.Actions.Count(a => a.Excluded) };
        });

        api.MapPost("/restore/plans/{id:guid}/apply", async (Guid id, MbrDbContext db, OperatorContext ops, JobQueue queue, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (plan.Status is not (RestorePlanStatus.Draft or RestorePlanStatus.Approved))
                throw new InvalidOperationException($"The plan is {plan.Status}; only draft or approved plans can be applied.");
            var settings = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == plan.TenantId, ct);
            if (settings?.RequireRestoreApproval == true && plan.Status != RestorePlanStatus.Approved)
            {
                plan.Status = RestorePlanStatus.PendingApproval;
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(ops.Name, "restore.approvalRequested", plan.TenantId, plan.Id.ToString(), null, Ip(ctx), ct);
                return Results.Ok(new { status = plan.Status.ToString(), jobId = (Guid?)null });
            }
            var job = await StartApplyAsync(plan, db, queue, ops.Name, ct);
            await audit.WriteAsync(ops.Name, "restore.started", plan.TenantId, plan.Id.ToString(), null, Ip(ctx), ct);
            return Results.Ok(new { status = plan.Status.ToString(), jobId = (Guid?)job.Id });
        });

        api.MapPost("/restore/plans/{id:guid}/approve", async (Guid id, MbrDbContext db, OperatorContext ops, JobQueue queue, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (plan.Status != RestorePlanStatus.PendingApproval) throw new InvalidOperationException($"The plan is {plan.Status}, not waiting for approval.");
            if (string.Equals(plan.CreatedBy, ops.Name, StringComparison.OrdinalIgnoreCase))
                throw new ForbiddenException("A restore must be approved by a different operator than the one who planned it.");
            plan.ApprovedBy = ops.Name;
            plan.ApprovedAt = DateTimeOffset.UtcNow;
            plan.Status = RestorePlanStatus.Approved;
            await audit.WriteAsync(ops.Name, "restore.approved", plan.TenantId, plan.Id.ToString(), null, Ip(ctx), ct);
            var job = await StartApplyAsync(plan, db, queue, plan.CreatedBy, ct);
            return new { status = plan.Status.ToString(), jobId = job.Id };
        });

        api.MapPost("/restore/plans/{id:guid}/reject", async (Guid id, RejectRequest? req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (plan.Status is not (RestorePlanStatus.PendingApproval or RestorePlanStatus.Draft))
                throw new InvalidOperationException($"The plan is {plan.Status} and can't be rejected.");
            plan.Status = RestorePlanStatus.Rejected;
            plan.Error = string.IsNullOrWhiteSpace(req?.Reason) ? $"Rejected by {ops.Name}" : $"Rejected by {ops.Name}: {req!.Reason}";
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "restore.rejected", plan.TenantId, plan.Id.ToString(), new { req?.Reason }, Ip(ctx), ct);
            return new { status = plan.Status.ToString() };
        });

        api.MapDelete("/restore/plans/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var plan = await LoadPlanAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (plan.Status is RestorePlanStatus.Applying or RestorePlanStatus.Planning)
                throw new InvalidOperationException("The plan is being built or applied; cancel its job first.");
            db.RestorePlans.Remove(plan);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
