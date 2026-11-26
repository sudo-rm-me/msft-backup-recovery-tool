using System.Text.Json.Nodes;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Jobs;

/// <summary>
/// Claims queued jobs from the database with a lease, runs them, renews the lease while running and
/// re-queues failed jobs with backoff. Several workers (in different processes) can share one database.
/// </summary>
public sealed class JobWorker(IServiceScopeFactory scopes, IOptions<MbrOptions> options, ILogger<JobWorker> log) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"[..48];
    private readonly Dictionary<Guid, (Task Task, CancellationTokenSource Cts)> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Worker;
        log.LogInformation("Job worker {Owner} started (concurrency {Max})", _owner, o.MaxConcurrency);
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(15))
                {
                    await HeartbeatAsync(stoppingToken);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
                foreach (var done in _running.Where(r => r.Value.Task.IsCompleted).Select(r => r.Key).ToList())
                {
                    _running[done].Cts.Dispose();
                    _running.Remove(done);
                }
                await RenewLeasesAndCheckCancelAsync(stoppingToken);
                while (_running.Count < o.MaxConcurrency && await ClaimAsync(stoppingToken) is { } job)
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _running[job.Id] = (Task.Run(() => RunAsync(job.Id, cts.Token), CancellationToken.None), cts);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Job worker loop error");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(o.PollSeconds), stoppingToken); } catch (OperationCanceledException) { }
        }
        await Task.WhenAll(_running.Values.Select(r => r.Task));
        try { await HeartbeatAsync(CancellationToken.None, stopping: true); } catch { /* best effort */ }
    }

    /// <summary>Records this worker in the fleet list (and removes workers not seen for a day).</summary>
    private async Task HeartbeatAsync(CancellationToken ct, bool stopping = false)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var row = await db.WorkerInstances.FirstOrDefaultAsync(w => w.Id == _owner, ct);
        if (stopping)
        {
            if (row != null) db.WorkerInstances.Remove(row);
        }
        else
        {
            if (row is null)
                db.WorkerInstances.Add(row = new WorkerInstance
                {
                    Id = _owner, Host = Environment.MachineName, ProcessId = Environment.ProcessId,
                    Version = typeof(JobWorker).Assembly.GetName().Version?.ToString(),
                });
            row.LastSeenAt = DateTimeOffset.UtcNow;
            row.RunningJobs = _running.Count(r => !r.Value.Task.IsCompleted);
            row.MaxConcurrency = options.Value.Worker.MaxConcurrency;
            var stale = DateTimeOffset.UtcNow.AddDays(-1);
            db.WorkerInstances.RemoveRange(await db.WorkerInstances.Where(w => w.LastSeenAt < stale).ToListAsync(ct));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Job?> ClaimAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var now = DateTimeOffset.UtcNow;
        var o = options.Value.Worker;
        var q = db.Jobs.Where(j => (j.Status == JobStatus.Queued && j.NotBefore <= now) ||
                                   (j.Status == JobStatus.Running && j.LeaseExpiresAt < now));
        var candidates = await q.OrderBy(j => j.CreatedAt).Take(25).ToListAsync(ct);
        // Jobs running per tenant across every worker (live leases only).
        var perTenant = o.MaxJobsPerTenant <= 0 ? new Dictionary<Guid, int>() : (await db.Jobs
                .Where(j => j.Status == JobStatus.Running && j.LeaseExpiresAt >= now && j.TenantId != null && !j.Type.StartsWith("restore."))
                .Select(j => j.TenantId!.Value).ToListAsync(ct))
            .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        foreach (var job in candidates)
        {
            if (_running.ContainsKey(job.Id)) continue;
            if (o.MaxJobsPerTenant > 0 && job.TenantId is { } tid && !job.Type.StartsWith("restore.") &&
                perTenant.GetValueOrDefault(tid) >= o.MaxJobsPerTenant)
                continue; // this tenant is busy; leave it for later so other tenants get a turn
            var resumed = job.Status == JobStatus.Running;
            job.Status = JobStatus.Running;
            job.LeaseOwner = _owner;
            job.LeaseExpiresAt = now.AddSeconds(options.Value.Worker.LeaseSeconds);
            job.StartedAt ??= now;
            job.Attempt++;
            job.Version = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync(ct);
                if (resumed) log.LogWarning("Resuming job {Job} whose lease expired", job.Id);
                return job;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // another worker claimed it
            }
        }
        return null;
    }

    private async Task RenewLeasesAndCheckCancelAsync(CancellationToken ct)
    {
        if (_running.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var ids = _running.Keys.ToList();
        var jobs = await db.Jobs.Where(j => ids.Contains(j.Id)).ToListAsync(ct);
        foreach (var job in jobs)
        {
            if (job.CancelRequested) _running[job.Id].Cts.Cancel();
            if (job.Status == JobStatus.Running && job.LeaseOwner == _owner)
            {
                job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.Worker.LeaseSeconds);
                job.Version = Guid.NewGuid();
            }
        }
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
    }

    private async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<MbrDbContext>();
        var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct);
        var tenant = job.TenantId is { } tid ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, ct) : null;
        var handler = sp.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        var context = new JobContext(job, tenant, sp, scopes);

        JobStatus status;
        string? error = null;
        JsonNode? result = null;
        try
        {
            if (handler is null) throw new InvalidOperationException($"No handler for job type '{job.Type}'.");
            await context.InfoAsync(job.Attempt > 1 ? $"Starting attempt {job.Attempt}" : "Starting");
            var outcome = await handler.ExecuteAsync(context, ct);
            status = outcome.Status;
            result = outcome.Result;
            await context.InfoAsync($"Finished: {status}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = JobStatus.Cancelled;
            await SafeLog(context, JobLogLevel.Warning, "Cancelled");
        }
        catch (Exception e)
        {
            status = JobStatus.Failed;
            error = e.Message;
            log.LogError(e, "Job {Job} ({Type}) failed", job.Id, job.Type);
            await SafeLog(context, JobLogLevel.Error, $"Failed: {e.Message}");
        }

        await using var final = scopes.CreateAsyncScope();
        var fdb = final.ServiceProvider.GetRequiredService<MbrDbContext>();
        var retry = false;
        Job row = null!;
        for (var i = 0; ; i++)
        {
            fdb.ChangeTracker.Clear();
            row = await fdb.Jobs.FirstAsync(j => j.Id == jobId, CancellationToken.None);
            retry = status == JobStatus.Failed && row.Attempt < row.MaxAttempts && !row.CancelRequested;
            if (retry)
            {
                row.Status = JobStatus.Queued;
                row.NotBefore = DateTimeOffset.UtcNow.AddSeconds(30 * Math.Pow(2, row.Attempt - 1));
                row.Error = error;
            }
            else
            {
                row.Status = status;
                row.CompletedAt = DateTimeOffset.UtcNow;
                row.Error = error;
                row.ResultJson = result?.ToJsonString();
                if (status is JobStatus.Succeeded or JobStatus.PartiallySucceeded) row.Progress = 1;
            }
            row.LeaseOwner = null;
            row.LeaseExpiresAt = null;
            row.Version = Guid.NewGuid();
            try { await fdb.SaveChangesAsync(CancellationToken.None); break; }
            catch (DbUpdateConcurrencyException) when (i < 5) { /* lease renewal raced us; retry */ }
        }
        if (retry) await SafeLog(context, JobLogLevel.Warning, $"Will retry after {row.NotBefore:u}");

        if (!retry && status is JobStatus.Failed or JobStatus.PartiallySucceeded)
        {
            var notifier = final.ServiceProvider.GetService<Notifications.INotifier>();
            if (notifier != null)
                await notifier.NotifyAsync(new Notifications.Notification(
                    status == JobStatus.Failed ? "Job failed" : "Job finished with errors",
                    $"{row.Type} for tenant {tenant?.DisplayName ?? "-"}: {error ?? status.ToString()}", tenant?.Id, row.Id));
        }
    }

    private static async Task SafeLog(JobContext c, JobLogLevel level, string m)
    {
        try { await c.LogAsync(level, m); } catch { /* ignore */ }
    }
}

internal sealed class JobContext : IJobContext
{
    private readonly IServiceScopeFactory _scopes;
    private DateTimeOffset _lastProgressWrite = DateTimeOffset.MinValue;

    public JobContext(Job job, Tenant? tenant, IServiceProvider services, IServiceScopeFactory scopes)
    {
        Job = job;
        Tenant = tenant;
        Services = services;
        _scopes = scopes;
        Parameters = JsonNode.Parse(job.ParametersJson) as JsonObject ?? new JsonObject();
        Checkpoint = job.CheckpointJson is null ? null : JsonNode.Parse(job.CheckpointJson);
    }

    public Job Job { get; }
    public Tenant? Tenant { get; }
    public JsonObject Parameters { get; }
    public IServiceProvider Services { get; }
    public JsonNode? Checkpoint { get; private set; }

    public async Task ReportProgressAsync(double fraction, string? message = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastProgressWrite < TimeSpan.FromSeconds(1) && fraction < 1) return;
        _lastProgressWrite = now;
        var p = Math.Clamp(fraction, 0, 1);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        if (message is null)
            await db.Jobs.Where(j => j.Id == Job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Progress, p));
        else
            await db.Jobs.Where(j => j.Id == Job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Progress, p).SetProperty(j => j.ProgressMessage, message));
    }

    public async Task LogAsync(JobLogLevel level, string message)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        db.JobLogs.Add(new JobLog { JobId = Job.Id, Level = level, Message = message.Length > 4000 ? message[..4000] : message });
        await db.SaveChangesAsync();
    }

    public async Task SaveCheckpointAsync(JsonNode checkpoint)
    {
        Checkpoint = checkpoint;
        var json = checkpoint.ToJsonString();
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        await db.Jobs.Where(j => j.Id == Job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.CheckpointJson, json));
    }
}
