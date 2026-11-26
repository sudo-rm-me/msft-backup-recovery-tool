using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record StartJobRequest(string Type, Guid? TenantId, JsonObject? Parameters);
    public record ScheduleRequest(string Name, string JobType, JsonObject? Parameters, string Cron, string? TimeZone, bool? Enabled);
    public record CronPreviewRequest(string Cron, string? TimeZone);

    internal static object JobDto(Job j) => new
    {
        j.Id, j.TenantId, j.Type, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt, j.CreatedBy, j.ScheduleId,
        j.Attempt, j.MaxAttempts, j.Progress, j.ProgressMessage, j.Error, j.CancelRequested, j.NotBefore,
        parameters = JsonNode.Parse(j.ParametersJson),
        result = j.ResultJson is null ? null : JsonNode.Parse(j.ResultJson),
    };

    private static void MapJobs(RouteGroupBuilder api)
    {
        api.MapGet("/jobs", async (Guid? tenantId, string? status, string? type, int? take, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var visible = await ops.VisibleTenantIdsAsync(ct);
            var q = db.Jobs.AsNoTracking().AsQueryable();
            if (tenantId is { } tid) q = q.Where(j => j.TenantId == tid);
            if (Enum.TryParse<JobStatus>(status, true, out var st)) q = q.Where(j => j.Status == st);
            if (!string.IsNullOrEmpty(type)) q = q.Where(j => j.Type == type);
            var jobs = await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
            return jobs.Where(j => visible is null || (j.TenantId is { } t && visible.Contains(t))).Select(JobDto);
        });

        api.MapGet("/jobs/types", (JobQueue q) => q.Handlers.Select(h => new { h.Type, h.RequiredRole }).OrderBy(h => h.Type));

        api.MapGet("/jobs/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            if (j.TenantId is { } t) await ops.EnsureTenantRoleAsync(t, TenantRole.Viewer, ct); else await ops.EnsureGlobalAdminAsync(ct);
            return JobDto(j);
        });

        api.MapGet("/jobs/{id:guid}/logs", async (Guid id, long? after, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            if (j.TenantId is { } t) await ops.EnsureTenantRoleAsync(t, TenantRole.Viewer, ct); else await ops.EnsureGlobalAdminAsync(ct);
            var from = after ?? 0;
            return await db.JobLogs.AsNoTracking().Where(l => l.JobId == id && l.Id > from).OrderBy(l => l.Id).Take(1000)
                .Select(l => new { l.Id, l.At, l.Level, l.Message }).ToListAsync(ct);
        });

        api.MapPost("/jobs", async (StartJobRequest req, JobQueue queue, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var handler = queue.FindHandler(req.Type) ?? throw new ArgumentException($"Unknown job type '{req.Type}'.");
            if (req.TenantId is { } t) await ops.EnsureTenantRoleAsync(t, handler.RequiredRole, ct); else await ops.EnsureGlobalAdminAsync(ct);
            var job = await queue.EnqueueAsync(req.Type, req.TenantId, req.Parameters, ops.Name, ct: ct);
            await audit.WriteAsync(ops.Name, "job.start", req.TenantId, req.Type, new { job.Id, req.Parameters }, Ip(ctx), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto(job));
        });

        api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, MbrDbContext db, JobQueue queue, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            if (j.TenantId is { } t) await ops.EnsureTenantRoleAsync(t, TenantRole.BackupOperator, ct); else await ops.EnsureGlobalAdminAsync(ct);
            var ok = await queue.RequestCancelAsync(id, ct);
            await audit.WriteAsync(ops.Name, "job.cancel", j.TenantId, j.Type, new { id }, Ip(ctx), ct);
            return ok ? Results.Accepted() : Results.Conflict(new { title = "Job is not running or queued." });
        });
    }

    private static object ScheduleDto(Schedule s) => new
    {
        s.Id, s.TenantId, s.Name, s.JobType, parameters = JsonNode.Parse(s.ParametersJson), s.Cron, s.TimeZone, s.Enabled,
        s.NextRunAt, s.LastRunAt, s.LastJobId, s.CreatedAt, s.CreatedBy,
    };

    private static void MapSchedules(RouteGroupBuilder api)
    {
        api.MapGet("/schedules/presets", () => CronHelper.Presets.Select(p => new { name = p.Key, cron = p.Value }));

        api.MapPost("/schedules/preview", (CronPreviewRequest req) =>
        {
            var tz = req.TimeZone ?? "UTC";
            if (!CronHelper.TryValidate(req.Cron, tz, out var err)) throw new ArgumentException(err);
            return new { cron = CronHelper.Normalize(req.Cron), next = CronHelper.NextN(req.Cron, tz, DateTimeOffset.UtcNow, 5) };
        });

        api.MapGet("/schedules", async (Guid? tenantId, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var visible = await ops.VisibleTenantIdsAsync(ct);
            var q = db.Schedules.AsNoTracking().AsQueryable();
            if (tenantId is { } tid) q = q.Where(s => s.TenantId == tid);
            var list = await q.OrderBy(s => s.Name).ToListAsync(ct);
            return list.Where(s => visible is null || visible.Contains(s.TenantId)).Select(ScheduleDto);
        });

        api.MapPost("/tenants/{tenantId:guid}/schedules", async (Guid tenantId, ScheduleRequest req, MbrDbContext db, JobQueue queue,
            OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureTenantRoleAsync(tenantId, TenantRole.BackupOperator, ct);
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) throw new KeyNotFoundException("Tenant not found.");
            var s = new Schedule { TenantId = tenantId, CreatedBy = ops.Name };
            ApplySchedule(s, req, queue);
            await ops.EnsureTenantRoleAsync(tenantId, queue.FindHandler(s.JobType)!.RequiredRole, ct);
            db.Schedules.Add(s);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "schedule.add", tenantId, s.Name, req, Ip(ctx), ct);
            return Results.Created($"/api/schedules/{s.Id}", ScheduleDto(s));
        });

        api.MapPut("/schedules/{id:guid}", async (Guid id, ScheduleRequest req, MbrDbContext db, JobQueue queue,
            OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(s.TenantId, TenantRole.BackupOperator, ct);
            ApplySchedule(s, req, queue);
            await ops.EnsureTenantRoleAsync(s.TenantId, queue.FindHandler(s.JobType)!.RequiredRole, ct);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "schedule.update", s.TenantId, s.Name, req, Ip(ctx), ct);
            return ScheduleDto(s);
        });

        api.MapDelete("/schedules/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(s.TenantId, TenantRole.BackupOperator, ct);
            db.Schedules.Remove(s);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "schedule.delete", s.TenantId, s.Name, null, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapPost("/schedules/{id:guid}/run", async (Guid id, MbrDbContext db, JobQueue queue, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
            await ops.EnsureTenantRoleAsync(s.TenantId, queue.FindHandler(s.JobType)?.RequiredRole ?? TenantRole.Admin, ct);
            var job = await queue.EnqueueAsync(s.JobType, s.TenantId, JsonNode.Parse(s.ParametersJson) as JsonObject, ops.Name, s.Id, ct: ct);
            s.LastJobId = job.Id;
            s.LastRunAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "schedule.runNow", s.TenantId, s.Name, new { job.Id }, Ip(ctx), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto(job));
        });
    }

    private static void ApplySchedule(Schedule s, ScheduleRequest req, JobQueue queue)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name is required.");
        if (queue.FindHandler(req.JobType) is null) throw new ArgumentException($"Unknown job type '{req.JobType}'.");
        var tz = string.IsNullOrWhiteSpace(req.TimeZone) ? "UTC" : req.TimeZone;
        if (!CronHelper.TryValidate(req.Cron, tz, out var err)) throw new ArgumentException($"Invalid schedule: {err}");
        s.Name = req.Name.Trim();
        s.JobType = req.JobType;
        s.ParametersJson = (req.Parameters ?? new JsonObject()).ToJsonString();
        s.Cron = CronHelper.Normalize(req.Cron);
        s.TimeZone = tz;
        s.Enabled = req.Enabled ?? true;
        s.NextRunAt = s.Enabled ? CronHelper.Next(s.Cron, s.TimeZone, DateTimeOffset.UtcNow) : null;
    }

    // ---------------------------------------------------------------- operators + audit
    public record OperatorRequest(string ObjectId, string UserPrincipalName, string? DisplayName, bool IsGlobalAdmin);
    public record OperatorRoleRequest(Guid? TenantId, TenantRole Role);

    private static object OperatorDto(Operator o) => new
    {
        o.Id, o.ObjectId, o.UserPrincipalName, o.DisplayName, o.IsGlobalAdmin, o.CreatedAt, o.LastSeenAt,
        roles = o.Roles.Select(r => new { r.Id, r.TenantId, r.Role }),
    };

    private static void MapAdmin(RouteGroupBuilder api)
    {
        api.MapGet("/operators", async (MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            return (await db.Operators.Include(o => o.Roles).AsNoTracking().OrderBy(o => o.UserPrincipalName).ToListAsync(ct)).Select(OperatorDto);
        });

        api.MapPost("/operators", async (OperatorRequest req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            if (!Guid.TryParse(req.ObjectId, out _)) throw new ArgumentException("ObjectId must be the user's Entra object ID.");
            if (await db.Operators.AnyAsync(o => o.ObjectId == req.ObjectId, ct)) throw new InvalidOperationException("Operator already exists.");
            var op = new Operator { ObjectId = req.ObjectId, UserPrincipalName = req.UserPrincipalName, DisplayName = req.DisplayName ?? req.UserPrincipalName, IsGlobalAdmin = req.IsGlobalAdmin };
            db.Operators.Add(op);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "operator.add", null, req.UserPrincipalName, new { req.IsGlobalAdmin }, Ip(ctx), ct);
            return Results.Created($"/api/operators/{op.Id}", OperatorDto(op));
        });

        api.MapPatch("/operators/{id:guid}", async (Guid id, OperatorRequest req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var op = await db.Operators.Include(o => o.Roles).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new KeyNotFoundException();
            if (op.Id == (await ops.GetAsync(ct)).Id && !req.IsGlobalAdmin) throw new InvalidOperationException("You can't remove your own global admin rights.");
            op.IsGlobalAdmin = req.IsGlobalAdmin;
            if (!string.IsNullOrWhiteSpace(req.DisplayName)) op.DisplayName = req.DisplayName;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "operator.update", null, op.UserPrincipalName, new { req.IsGlobalAdmin }, Ip(ctx), ct);
            return OperatorDto(op);
        });

        api.MapDelete("/operators/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var op = await db.Operators.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new KeyNotFoundException();
            if (op.Id == (await ops.GetAsync(ct)).Id) throw new InvalidOperationException("You can't remove yourself.");
            db.Operators.Remove(op);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "operator.delete", null, op.UserPrincipalName, null, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapPost("/operators/{id:guid}/roles", async (Guid id, OperatorRoleRequest req, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var op = await db.Operators.Include(o => o.Roles).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new KeyNotFoundException();
            op.Roles.RemoveAll(r => r.TenantId == req.TenantId);
            op.Roles.Add(new OperatorTenantRole { OperatorId = op.Id, TenantId = req.TenantId, Role = req.Role });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "operator.role.set", req.TenantId, op.UserPrincipalName, new { req.Role }, Ip(ctx), ct);
            return OperatorDto(op);
        });

        api.MapDelete("/operators/{id:guid}/roles/{roleId:guid}", async (Guid id, Guid roleId, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var role = await db.OperatorTenantRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.OperatorId == id, ct) ?? throw new KeyNotFoundException();
            db.OperatorTenantRoles.Remove(role);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "operator.role.remove", role.TenantId, id.ToString(), new { role.Role }, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapGet("/audit", async (Guid? tenantId, string? action, int? take, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            if (tenantId is { } t) await ops.EnsureTenantRoleAsync(t, TenantRole.Admin, ct); else await ops.EnsureGlobalAdminAsync(ct);
            var q = db.AuditEntries.AsNoTracking().AsQueryable();
            if (tenantId is { } tid) q = q.Where(a => a.TenantId == tid);
            if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action.StartsWith(action));
            var list = await q.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync(ct);
            return list.Select(a => new { a.Id, a.At, a.Actor, a.TenantId, a.Action, a.Target, details = a.DetailsJson is null ? null : JsonNode.Parse(a.DetailsJson), a.IpAddress });
        });
    }
}
