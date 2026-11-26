using System.Text;
using Mbr.Api.Auth;
using Mbr.Catalog;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    static partial void MapFeatureRoutesPhase7Plus(RouteGroupBuilder api)
    {
        // Protection report for every tenant the operator can see (or one tenant); CSV for spreadsheets and ticketing.
        api.MapGet("/reports/protection", async (Guid? tenantId, string? format, MbrDbContext db, OperatorContext ops, ProtectionReport report,
            ResourceRegistry registry, CancellationToken ct) =>
        {
            var visible = await ops.VisibleTenantIdsAsync(ct);
            var ids = await db.Tenants.AsNoTracking()
                .Where(t => (visible == null || visible.Contains(t.Id)) && (tenantId == null || t.Id == tenantId))
                .Select(t => t.Id).ToListAsync(ct);
            var configWorkloads = registry.Workloads.ToList();
            var rows = await report.BuildAsync(ids, configWorkloads, ct);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ProtectionReport.ToCsv(rows))).ToArray(),
                    "text/csv", $"protection-report-{DateTime.UtcNow:yyyy-MM-dd}.csv");
            return Results.Ok(new
            {
                generatedAt = DateTimeOffset.UtcNow,
                summary = new
                {
                    tenants = rows.Count, healthy = rows.Count(r => r.Health == "Healthy"), atRisk = rows.Count(r => r.Health == "AtRisk"),
                    unprotected = rows.Count(r => r.Health == "Unprotected"),
                },
                tenants = rows,
            });
        });

        // Job workers that reported in during the last few minutes, with what they're running.
        api.MapGet("/workers", async (MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            await ops.GetAsync(ct);
            var since = DateTimeOffset.UtcNow.AddMinutes(-10);
            var workers = await db.WorkerInstances.AsNoTracking().Where(w => w.LastSeenAt >= since).OrderBy(w => w.Host).ThenBy(w => w.Id).ToListAsync(ct);
            var running = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Running && j.LeaseOwner != null)
                .Select(j => new { j.Id, j.Type, j.TenantId, j.LeaseOwner, j.Progress, j.StartedAt }).ToListAsync(ct);
            var queued = await db.Jobs.AsNoTracking().CountAsync(j => j.Status == JobStatus.Queued, ct);
            return new
            {
                queued,
                workers = workers.Select(w => new
                {
                    w.Id, w.Host, w.ProcessId, w.StartedAt, w.LastSeenAt, w.MaxConcurrency, w.Version,
                    jobs = running.Where(j => j.LeaseOwner == w.Id).ToList(),
                }),
            };
        });

        MapFeatureRoutesPhase8Plus(api);
    }

    static partial void MapFeatureRoutesPhase8Plus(RouteGroupBuilder api);
}
