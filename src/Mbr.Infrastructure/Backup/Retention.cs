using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mbr.Infrastructure.Backup;

/// <summary>Supplies the content hashes still referenced by a feature area, so garbage collection never deletes them.</summary>
public interface IReferencedHashSource
{
    Task<HashSet<string>> GetReferencedHashesAsync(Guid tenantId, CancellationToken ct);
}

public sealed class SnapshotHashSource(MbrDbContext db) : IReferencedHashSource
{
    public async Task<HashSet<string>> GetReferencedHashesAsync(Guid tenantId, CancellationToken ct) =>
        (await db.SnapshotItems.Where(i => db.Snapshots.Any(s => s.Id == i.SnapshotId && s.TenantId == tenantId))
            .Select(i => i.Hash).Distinct().ToListAsync(ct)).ToHashSet();
}

public sealed class RetentionService(MbrDbContext db, ITenantContentStore content, IEnumerable<IReferencedHashSource> hashSources,
    IEnumerable<IRetentionParticipant> participants)
{
    public record Result(int SnapshotsDeleted, int ObjectsDeleted, long BytesFreed, int DeleteFailures, int DataVersionsPruned);

    public async Task<Result> RunAsync(Tenant tenant, IJobContext? job, CancellationToken ct, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var settings = await db.TenantSettings.FindAsync([tenant.Id], ct) ?? new TenantSettings { TenantId = tenant.Id };

        // 1. Expired configuration snapshots (baselines and the newest N are always kept).
        var snapshots = await db.Snapshots.Where(s => s.TenantId == tenant.Id && s.Status != SnapshotStatus.Running)
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var cutoff = at.AddDays(-settings.ConfigRetentionDays);
        var expired = snapshots.Skip(Math.Max(0, settings.ConfigKeepMinimum)).Where(s => !s.IsBaseline && s.CreatedAt < cutoff).ToList();
        foreach (var s in expired) await DeleteSnapshotAsync(tenant, s, ct);
        if (job != null && expired.Count > 0) await job.InfoAsync($"Pruned {expired.Count} snapshot(s) older than {settings.ConfigRetentionDays} days");

        // 2. Expired data versions (files, mail) are pruned by their own feature areas.
        var pruned = 0;
        foreach (var p in participants) pruned += await p.PruneAsync(tenant, settings, at, job, ct);

        // 3. Garbage-collect objects nothing references any more.
        var referenced = new HashSet<string>();
        foreach (var src in hashSources) referenced.UnionWith(await src.GetReferencedHashesAsync(tenant.Id, ct));
        var orphans = await db.StoredObjects.AsNoTracking().Where(o => o.TenantId == tenant.Id).ToListAsync(ct);
        orphans = orphans.Where(o => !referenced.Contains(o.Hash)).ToList();
        int deleted = 0, failures = 0;
        long freed = 0;
        foreach (var o in orphans)
        {
            try
            {
                await content.DeleteObjectAsync(tenant, o.StorageTargetId, o.Hash, ct);
                deleted++;
                freed += o.StoredSize;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failures++; // most often S3 Object Lock: retried on the next run
            }
        }
        if (job != null)
        {
            await job.InfoAsync($"Deleted {deleted} unreferenced object(s), {freed / 1024.0 / 1024.0:0.0} MB freed");
            if (failures > 0) await job.WarnAsync($"{failures} object(s) could not be deleted yet (S3 Object Lock); they'll be retried");
        }
        return new Result(expired.Count, deleted, freed, failures, pruned);
    }

    public async Task DeleteSnapshotAsync(Tenant tenant, Snapshot s, CancellationToken ct)
    {
        await db.SnapshotItems.Where(i => i.SnapshotId == s.Id).ExecuteDeleteAsync(ct);
        await db.SnapshotIssues.Where(i => i.SnapshotId == s.Id).ExecuteDeleteAsync(ct);
        db.Snapshots.Remove(s);
        await db.SaveChangesAsync(ct);
        try { await content.DeleteDocumentAsync(tenant, s.StorageTargetId, ConfigBackupService.ManifestName(s.Id), ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* Object Lock: the manifest stays until its lock expires */ }
    }
}

/// <summary>Feature areas with their own versioned data (files, mail) prune it during retention runs.</summary>
public interface IRetentionParticipant
{
    Task<int> PruneAsync(Tenant tenant, TenantSettings settings, DateTimeOffset now, IJobContext? job, CancellationToken ct);
}

public sealed class RetentionJob : IJobHandler
{
    public string Type => "maintenance.retention";
    public TenantRole RequiredRole => TenantRole.Admin;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var svc = context.Services.GetRequiredService<RetentionService>();
        var r = await svc.RunAsync(context.RequireTenant(), context, ct);
        return JobOutcome.Success(new JsonObject
        {
            ["snapshotsDeleted"] = r.SnapshotsDeleted, ["objectsDeleted"] = r.ObjectsDeleted,
            ["bytesFreed"] = r.BytesFreed, ["deleteFailures"] = r.DeleteFailures, ["dataVersionsPruned"] = r.DataVersionsPruned,
        });
    }
}

/// <summary>Once a day (on one host), queues a retention job for every active tenant.</summary>
public sealed class MaintenanceScheduler(IServiceScopeFactory scopes, LeaseService leases, ILogger<MaintenanceScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A fresh owner each time means the lease can only be taken again after it expires (~once a day).
                if (await leases.TryAcquireAsync("maintenance-daily", Guid.NewGuid().ToString(), TimeSpan.FromHours(23), stoppingToken))
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
                    var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
                    foreach (var t in await db.Tenants.Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(stoppingToken))
                        await queue.EnqueueAsync("maintenance.retention", t, null, "system", ct: stoppingToken);
                    log.LogInformation("Queued daily retention jobs");
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogError(e, "Maintenance scheduling failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
