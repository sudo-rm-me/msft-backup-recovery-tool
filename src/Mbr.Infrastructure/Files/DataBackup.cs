using System.Net;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Files;

public sealed record SourceBackupResult(int NewVersions, long BytesStored, int Deleted, int Failed, int Unchanged)
{
    public static SourceBackupResult operator +(SourceBackupResult a, SourceBackupResult b) =>
        new(a.NewVersions + b.NewVersions, a.BytesStored + b.BytesStored, a.Deleted + b.Deleted, a.Failed + b.Failed, a.Unchanged + b.Unchanged);
    public static readonly SourceBackupResult Empty = new(0, 0, 0, 0, 0);
}

/// <summary>Backs up one kind of data source (drives, mailboxes).</summary>
public interface IDataSourceBackup
{
    bool Handles(DataSourceKind kind);
    Task<SourceBackupResult> BackupAsync(Tenant tenant, DataSource source, Guid storageTargetId, IJobContext? job, CancellationToken ct);
}

/// <summary>
/// Runs data backups for a tenant: every enabled source (or the selected ones), updating each source's status and totals.
/// </summary>
public sealed class DataBackupService(MbrDbContext db, IEnumerable<IDataSourceBackup> backups, DataDiscoveryService discovery, StorageTargetResolver targets)
{
    public async Task<(SourceBackupResult Total, int Sources, int FailedSources)> RunAsync(Tenant tenant, IReadOnlyCollection<Guid>? sourceIds,
        IReadOnlyCollection<DataSourceKind>? kinds, Guid? storageTargetId, IJobContext? job, CancellationToken ct)
    {
        var targetId = await targets.GetTargetIdForTenantAsync(tenant, storageTargetId, ct);
        var kindSet = kinds is { Count: > 0 } ? kinds : null;
        if (!await db.DataSources.AnyAsync(s => s.TenantId == tenant.Id && (kindSet == null || kindSet.Contains(s.Kind)), ct))
        {
            if (job != null) await job.InfoAsync("No data sources known yet: running discovery first");
            await discovery.DiscoverAsync(tenant, kindSet?.ToList(), job, ct);
        }
        var q = db.DataSources.Where(s => s.TenantId == tenant.Id && !s.Missing);
        q = sourceIds is { Count: > 0 } ? q.Where(s => sourceIds.Contains(s.Id)) : q.Where(s => s.Enabled);
        if (kindSet != null) q = q.Where(s => kindSet.Contains(s.Kind));
        var sources = await q.OrderBy(s => s.Kind).ThenBy(s => s.DisplayName).ToListAsync(ct);

        var total = SourceBackupResult.Empty;
        var failedSources = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            if (job != null) await job.ReportProgressAsync((double)i / Math.Max(1, sources.Count), $"{src.Kind}: {src.DisplayName}");
            var handler = backups.FirstOrDefault(b => b.Handles(src.Kind));
            if (handler is null) continue;
            try
            {
                var r = await handler.BackupAsync(tenant, src, targetId, job, ct);
                total += r;
                src.LastStatus = r.Failed > 0 ? "Partial" : "Succeeded";
                src.LastError = r.Failed > 0 ? $"{r.Failed} item(s) couldn't be read; they'll be retried" : null;
                if (job != null)
                    await job.InfoAsync($"{src.DisplayName}: {r.NewVersions} new version(s), {r.BytesStored / 1024.0 / 1024.0:0.0} MB stored, {r.Deleted} deleted, {r.Unchanged} unchanged{(r.Failed > 0 ? $", {r.Failed} failed" : "")}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failedSources++;
                src.LastStatus = "Failed";
                src.LastError = e.Message;
                if (job != null) await job.ErrorAsync($"{src.DisplayName}: {e.Message}");
            }
            src.LastBackupAt = DateTimeOffset.UtcNow;
            var stats = await db.DataItemVersions.Where(v => v.SourceId == src.Id && !v.IsFolder && v.SupersededAt == null && v.DeletedAt == null)
                .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(v => v.Size) }).FirstOrDefaultAsync(ct);
            src.ItemCount = stats?.Count ?? 0;
            src.TotalBytes = stats?.Bytes ?? 0;
            await db.SaveChangesAsync(ct);
        }
        return (total, sources.Count, failedSources);
    }
}

/// <summary>"backup.data": backs up OneDrive/SharePoint (and later mailbox) data. Parameters: sourceIds, kinds, storageTargetId.</summary>
public sealed class DataBackupJob : IJobHandler
{
    public string Type => "backup.data";
    public TenantRole RequiredRole => TenantRole.BackupOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var p = context.Parameters;
        var sourceIds = p.GetStringList("sourceIds").Select(Guid.Parse).ToList();
        var kinds = p.GetStringList("kinds").Select(k => Enum.Parse<DataSourceKind>(k, true)).ToList();
        var svc = context.Services.GetRequiredService<DataBackupService>();
        var (r, count, failed) = await svc.RunAsync(tenant, sourceIds, kinds, p.GetGuid("storageTargetId"), context, ct);
        var result = new JsonObject
        {
            ["sources"] = count, ["failedSources"] = failed, ["newVersions"] = r.NewVersions, ["bytesStored"] = r.BytesStored,
            ["deleted"] = r.Deleted, ["failedItems"] = r.Failed, ["unchanged"] = r.Unchanged,
        };
        if (count > 0 && failed == count) throw new InvalidOperationException("Every data source failed; see the log.");
        return failed > 0 || r.Failed > 0 ? JobOutcome.Partial(result) : JobOutcome.Success(result);
    }
}
