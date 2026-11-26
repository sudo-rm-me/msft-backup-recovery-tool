using System.Text.Json.Nodes;
using Cronos;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mbr.Infrastructure.Jobs;

public static class CronHelper
{
    /// <summary>Friendly presets offered in the UI and CLI.</summary>
    public static readonly IReadOnlyDictionary<string, string> Presets = new Dictionary<string, string>
    {
        ["hourly"] = "0 * * * *",
        ["every-4-hours"] = "0 */4 * * *",
        ["daily"] = "0 2 * * *",
        ["twice-daily"] = "0 2,14 * * *",
        ["weekly"] = "0 3 * * 0",
        ["monthly"] = "0 4 1 * *",
    };

    public static string Normalize(string cronOrPreset) =>
        Presets.TryGetValue(cronOrPreset.Trim().ToLowerInvariant(), out var c) ? c : cronOrPreset.Trim();

    public static bool TryValidate(string cron, string timeZone, out string? error)
    {
        try
        {
            CronExpression.Parse(Normalize(cron));
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public static DateTimeOffset? Next(string cron, string timeZone, DateTimeOffset from) =>
        CronExpression.Parse(Normalize(cron)).GetNextOccurrence(from, TimeZoneInfo.FindSystemTimeZoneById(timeZone));

    public static IEnumerable<DateTimeOffset> NextN(string cron, string timeZone, DateTimeOffset from, int n)
    {
        var t = from;
        for (var i = 0; i < n; i++)
        {
            var next = Next(cron, timeZone, t);
            if (next is null) yield break;
            yield return next.Value;
            t = next.Value;
        }
    }
}

/// <summary>Database lease so only one host runs a singleton loop at a time.</summary>
public sealed class LeaseService(IServiceScopeFactory scopes)
{
    public async Task<bool> TryAcquireAsync(string name, string owner, TimeSpan duration, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lease = await db.Leases.FirstOrDefaultAsync(l => l.Name == name, ct);
        try
        {
            if (lease is null)
            {
                db.Leases.Add(new Lease { Name = name, Owner = owner, ExpiresAt = now + duration });
            }
            else
            {
                if (lease.Owner != owner && lease.ExpiresAt > now) return false;
                lease.Owner = owner;
                lease.ExpiresAt = now + duration;
                lease.Version = Guid.NewGuid();
            }
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) { return false; }
    }
}

/// <summary>Enqueues jobs for due schedules. Skips a run if the previous run of that schedule is still active.</summary>
public sealed class SchedulerService(IServiceScopeFactory scopes, LeaseService leases, ILogger<SchedulerService> log) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await leases.TryAcquireAsync("scheduler", _owner, TimeSpan.FromSeconds(90), stoppingToken))
                    await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Scheduler tick failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();

        // Initialise next-run times for new or edited schedules.
        foreach (var s in await db.Schedules.Where(s => s.Enabled && s.NextRunAt == null).ToListAsync(ct))
            s.NextRunAt = CronHelper.Next(s.Cron, s.TimeZone, now);
        await db.SaveChangesAsync(ct);

        var due = await db.Schedules.Where(s => s.Enabled && s.NextRunAt <= now).ToListAsync(ct);
        foreach (var s in due)
        {
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == s.TenantId, ct);
            var previousActive = s.LastJobId is { } last &&
                await db.Jobs.AnyAsync(j => j.Id == last && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
            if (tenant is null || tenant.Status == TenantStatus.Disabled)
                log.LogInformation("Skipping schedule {Schedule}: tenant missing or disabled", s.Name);
            else if (previousActive)
                log.LogWarning("Skipping schedule {Schedule}: previous run {Job} is still active", s.Name, s.LastJobId);
            else
            {
                var job = await queue.EnqueueAsync(s.JobType, s.TenantId, JsonNode.Parse(s.ParametersJson) as JsonObject,
                    $"schedule:{s.Name}", s.Id, ct: ct);
                s.LastJobId = job.Id;
                s.LastRunAt = now;
                log.LogInformation("Schedule {Schedule} enqueued job {Job}", s.Name, job.Id);
            }
            s.NextRunAt = CronHelper.Next(s.Cron, s.TimeZone, now);
        }
        await db.SaveChangesAsync(ct);
    }
}
