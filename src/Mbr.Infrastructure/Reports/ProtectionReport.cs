using System.Globalization;
using System.Text;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Reports;

public record WorkloadProtection(string Workload, int Items, DateTimeOffset? LastCapturedAt);

public record TenantProtection(
    Guid TenantId, string Tenant, string Status,
    DateTimeOffset? LastConfigBackupAt, string? LastConfigBackupStatus, int LastConfigItems, int LastConfigWarnings, int LastConfigErrors,
    IReadOnlyList<WorkloadProtection> Workloads, IReadOnlyList<string> WorkloadsNeverBackedUp,
    int DataSources, int DataSourcesProtected, int DataSourcesFailing, DateTimeOffset? OldestDataBackupAt, long DataBytes,
    int FailedJobs7Days, int Restores30Days, DateTimeOffset? NextScheduledBackupAt, string Health, IReadOnlyList<string> Problems);

/// <summary>
/// Protection status per tenant: what was backed up when, what's failing, and what isn't covered at all. Used by the
/// reports page, CSV export and `mbr report`.
/// </summary>
public sealed class ProtectionReport(MbrDbContext db)
{
    /// <summary>Config backups older than this make a tenant "at risk".</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(36);

    public async Task<List<TenantProtection>> BuildAsync(IReadOnlyCollection<Guid> tenantIds, IReadOnlyCollection<string> allWorkloads, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).OrderBy(t => t.DisplayName).ToListAsync(ct);
        var snapshots = await db.Snapshots.AsNoTracking().Where(s => tenantIds.Contains(s.TenantId) && s.Status != SnapshotStatus.Running).ToListAsync(ct);
        var sources = await db.DataSources.AsNoTracking().Where(s => tenantIds.Contains(s.TenantId) && !s.Missing).ToListAsync(ct);
        var weekAgo = now.AddDays(-7);
        var failed = (await db.Jobs.AsNoTracking().Where(j => j.TenantId != null && tenantIds.Contains(j.TenantId.Value) &&
                                                             j.Status == JobStatus.Failed && j.CompletedAt >= weekAgo)
            .Select(j => j.TenantId!.Value).ToListAsync(ct)).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var monthAgo = now.AddDays(-30);
        var restores = (await db.RestorePlans.AsNoTracking().Where(p => tenantIds.Contains(p.TenantId) && p.AppliedAt >= monthAgo)
            .Select(p => p.TenantId).ToListAsync(ct)).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var schedules = await db.Schedules.AsNoTracking().Where(s => tenantIds.Contains(s.TenantId) && s.Enabled && s.JobType == "backup.config").ToListAsync(ct);

        // Items per workload in each tenant's latest completed snapshot that included that workload.
        var latestIds = snapshots.Where(s => s.Status is SnapshotStatus.Completed or SnapshotStatus.Partial)
            .GroupBy(s => s.TenantId).SelectMany(g => g.OrderByDescending(s => s.CreatedAt).Take(20)).Select(s => s.Id).ToList();
        var counts = await db.SnapshotItems.AsNoTracking().Where(i => latestIds.Contains(i.SnapshotId))
            .GroupBy(i => new { i.SnapshotId, i.Workload }).Select(g => new { g.Key.SnapshotId, g.Key.Workload, Count = g.Count() }).ToListAsync(ct);

        var result = new List<TenantProtection>();
        foreach (var t in tenants)
        {
            var mine = snapshots.Where(s => s.TenantId == t.Id).OrderByDescending(s => s.CreatedAt).ToList();
            var last = mine.FirstOrDefault();
            var workloads = new List<WorkloadProtection>();
            foreach (var w in allWorkloads)
            {
                var snap = mine.FirstOrDefault(s => s.Status is SnapshotStatus.Completed or SnapshotStatus.Partial &&
                                                    s.Workloads.Split(',').Contains(w) && latestIds.Contains(s.Id));
                if (snap is null) continue;
                workloads.Add(new WorkloadProtection(w, counts.FirstOrDefault(c => c.SnapshotId == snap.Id && c.Workload == w)?.Count ?? 0, snap.CreatedAt));
            }
            var never = allWorkloads.Except(workloads.Select(w => w.Workload)).ToList();
            var tSources = sources.Where(s => s.TenantId == t.Id).ToList();
            var protectedSources = tSources.Where(s => s.Enabled).ToList();
            var failing = protectedSources.Count(s => s.LastStatus is "Failed" || s.LastError != null && s.LastStatus != "Succeeded");
            var next = schedules.Where(s => s.TenantId == t.Id).Select(s => s.NextRunAt).Where(n => n != null).Min();

            var problems = new List<string>();
            if (t.Status != TenantStatus.Active) problems.Add($"Tenant is {t.Status}.");
            if (last is null) problems.Add("Never backed up.");
            else if (now - last.CreatedAt > Stale) problems.Add($"Last configuration backup is {(int)(now - last.CreatedAt).TotalHours} hours old.");
            if (last?.Status == SnapshotStatus.Failed) problems.Add("The last configuration backup failed.");
            if (next is null) problems.Add("No configuration backup is scheduled.");
            if (failing > 0) problems.Add($"{failing} data source(s) failed their last backup.");
            var unbacked = protectedSources.Count(s => s.LastBackupAt is null);
            if (unbacked > 0) problems.Add($"{unbacked} protected data source(s) haven't been backed up yet.");
            if (failed.GetValueOrDefault(t.Id) > 0) problems.Add($"{failed[t.Id]} job(s) failed in the last 7 days.");
            var health = last is null || last.Status == SnapshotStatus.Failed || t.Status != TenantStatus.Active ? "Unprotected"
                : problems.Count > 0 ? "AtRisk" : "Healthy";

            result.Add(new TenantProtection(t.Id, t.DisplayName, t.Status.ToString(),
                last?.CreatedAt, last?.Status.ToString(), last?.ItemCount ?? 0, last?.WarningCount ?? 0, last?.ErrorCount ?? 0,
                workloads, never, tSources.Count, protectedSources.Count, failing,
                protectedSources.Where(s => s.LastBackupAt != null).Select(s => s.LastBackupAt).Min(), tSources.Sum(s => s.TotalBytes),
                failed.GetValueOrDefault(t.Id), restores.GetValueOrDefault(t.Id), next, health, problems));
        }
        return result;
    }

    public static string ToCsv(IEnumerable<TenantProtection> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tenant,Health,Status,Last config backup (UTC),Last backup status,Items,Warnings,Errors,Workloads covered,Workloads not covered," +
                      "Data sources,Protected,Failing,Oldest data backup (UTC),Data GB,Failed jobs (7d),Restores (30d),Next scheduled backup (UTC),Problems");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new object?[]
            {
                r.Tenant, r.Health, r.Status, Date(r.LastConfigBackupAt), r.LastConfigBackupStatus, r.LastConfigItems, r.LastConfigWarnings, r.LastConfigErrors,
                string.Join(" ", r.Workloads.Select(w => w.Workload)), string.Join(" ", r.WorkloadsNeverBackedUp),
                r.DataSources, r.DataSourcesProtected, r.DataSourcesFailing, Date(r.OldestDataBackupAt),
                (r.DataBytes / 1024d / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture), r.FailedJobs7Days, r.Restores30Days,
                Date(r.NextScheduledBackupAt), string.Join(" ", r.Problems),
            }.Select(Csv)));
        return sb.ToString();
    }

    private static string? Date(DateTimeOffset? d) => d?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Csv(object? v)
    {
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        // Neutralize spreadsheet formulas, then quote.
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
