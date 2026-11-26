using System.Text.Json.Nodes;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Jobs;

public sealed class JobQueue(MbrDbContext db, IEnumerable<IJobHandler> handlers)
{
    public IJobHandler? FindHandler(string type) => handlers.FirstOrDefault(h => h.Type == type);
    public IEnumerable<IJobHandler> Handlers => handlers;

    public async Task<Job> EnqueueAsync(string type, Guid? tenantId, JsonObject? parameters, string? createdBy,
        Guid? scheduleId = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        if (FindHandler(type) is null) throw new ArgumentException($"Unknown job type '{type}'.");
        var job = new Job
        {
            Type = type, TenantId = tenantId, ParametersJson = (parameters ?? new JsonObject()).ToJsonString(),
            CreatedBy = createdBy, ScheduleId = scheduleId, MaxAttempts = maxAttempts,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<bool> RequestCancelAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return false;
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        else if (job.Status == JobStatus.Running) job.CancelRequested = true;
        else return false;
        job.Version = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static bool IsTerminal(JobStatus s) => s is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled;
}
