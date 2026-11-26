using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Backup;

public record ChangeEntry(string Type, string ObjectId, string DisplayName, string Change);

public record ConfigBackupRequest(IReadOnlyList<string>? Workloads, Guid? StorageTargetId, string? Label, string? CreatedBy);

/// <summary>Captures configuration for a tenant into an encrypted, content-addressed snapshot.</summary>
public sealed class ConfigBackupService(
    MbrDbContext db, ResourceRegistry registry, TenantSessionFactory sessions, ITenantContentStore content,
    StorageTargetResolver targets, IEnumerable<IPostBackupHook> hooks)
{
    public const int ManifestSchemaVersion = 1;

    public async Task<(Snapshot Snapshot, List<ChangeEntry> Changes)> RunAsync(Tenant tenant, ConfigBackupRequest req, IJobContext? job, CancellationToken ct)
    {
        var targetId = await targets.GetTargetIdForTenantAsync(tenant, req.StorageTargetId, ct);
        var handlers = registry.ForWorkloads(req.Workloads).ToList();
        if (handlers.Count == 0) throw new ArgumentException("No resource types match the requested workloads.");
        var settings = await db.TenantSettings.FindAsync([tenant.Id], ct) ?? new TenantSettings { TenantId = tenant.Id };

        var snapshot = new Snapshot
        {
            TenantId = tenant.Id, StorageTargetId = targetId, JobId = job?.Job.Id, Label = req.Label, CreatedBy = req.CreatedBy,
            Workloads = string.Join(",", handlers.Select(h => h.Definition.Workload).Distinct().Order()),
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        var issues = new List<SnapshotIssue>();
        Task OnIssue(CaptureIssue i)
        {
            lock (issues) issues.Add(new SnapshotIssue { SnapshotId = snapshot.Id, ResourceType = i.Type, ObjectId = i.ObjectId, Severity = i.Severity.ToString(), Message = i.Message });
            return Task.CompletedTask;
        }
        var session = sessions.Create(tenant, AppRole.Backup, OnIssue);

        var items = new List<SnapshotItem>();
        var failedTypes = new HashSet<string>();
        for (var i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            var def = h.Definition;
            if (job != null) await job.ReportProgressAsync((double)i / handlers.Count, $"{def.DisplayName}");
            var count = 0;
            try
            {
                await foreach (var item in h.CaptureAsync(session, ct))
                {
                    var bytes = CanonicalJson.ToBytes(item.Content);
                    var hash = await content.PutObjectAsync(tenant, targetId, bytes, ct);
                    items.Add(new SnapshotItem
                    {
                        SnapshotId = snapshot.Id, ResourceType = item.Type, Workload = def.Workload, ObjectId = item.ObjectId,
                        DisplayName = Truncate(item.DisplayName, 400), Hash = hash, Size = bytes.Length,
                    });
                    count++;
                }
                if (job != null) await job.InfoAsync($"{def.DisplayName}: {count} item(s)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                var (severity, message) = Classify(def, e);
                if (severity != IssueSeverity.Info) failedTypes.Add(def.Type);
                await OnIssue(new CaptureIssue(def.Type, null, severity, message));
                if (job != null) await job.LogAsync(severity == IssueSeverity.Error ? JobLogLevel.Error : severity == IssueSeverity.Warning ? JobLogLevel.Warning : JobLogLevel.Info, $"{def.DisplayName}: {message}");
            }
        }

        // Compare with the previous snapshot to record what changed.
        var capturedTypes = handlers.Select(h => h.Definition.Type).Where(t => !failedTypes.Contains(t)).ToHashSet();
        var changes = await ComputeChangesAsync(tenant.Id, snapshot.Id, items, capturedTypes, issues, ct);

        db.SnapshotItems.AddRange(items);
        db.SnapshotIssues.AddRange(issues);
        snapshot.ItemCount = items.Count;
        snapshot.TotalBytes = items.Sum(x => x.Size);
        snapshot.ErrorCount = issues.Count(x => x.Severity == nameof(IssueSeverity.Error));
        snapshot.WarningCount = issues.Count(x => x.Severity == nameof(IssueSeverity.Warning));
        snapshot.AddedCount = changes.Count(c => c.Change == "Added");
        snapshot.ModifiedCount = changes.Count(c => c.Change == "Modified");
        snapshot.RemovedCount = changes.Count(c => c.Change == "Removed");
        // Partial = some types could not be read (errors or access denied); "not available" info doesn't count.
        snapshot.Status = snapshot.ErrorCount > 0 || failedTypes.Count > 0 ? SnapshotStatus.Partial : SnapshotStatus.Completed;
        snapshot.CompletedAt = DateTimeOffset.UtcNow;
        snapshot.ExpiresAt = snapshot.CreatedAt.AddDays(settings.ConfigRetentionDays);
        await db.SaveChangesAsync(ct);

        await WriteManifestAsync(tenant, snapshot, items, issues, ct);
        foreach (var hook in hooks)
        {
            try { await hook.AfterConfigBackupAsync(tenant, snapshot, changes, job, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (job != null) await job.WarnAsync($"Post-backup step failed: {e.Message}");
            }
        }
        return (snapshot, changes);
    }

    /// <summary>
    /// The manifest makes a snapshot self-describing in storage, so backups can be restored even if the
    /// metadata database is lost (see "mbr snapshot import").
    /// </summary>
    private async Task WriteManifestAsync(Tenant tenant, Snapshot s, List<SnapshotItem> items, List<SnapshotIssue> issues, CancellationToken ct)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = ManifestSchemaVersion,
            ["snapshotId"] = s.Id.ToString(),
            ["tenantId"] = tenant.Id.ToString(),
            ["directoryId"] = tenant.DirectoryId,
            ["tenantName"] = tenant.DisplayName,
            ["createdAt"] = s.CreatedAt,
            ["completedAt"] = s.CompletedAt,
            ["status"] = s.Status.ToString(),
            ["workloads"] = s.Workloads,
            ["label"] = s.Label,
            ["toolVersion"] = typeof(ConfigBackupService).Assembly.GetName().Version?.ToString(),
            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = i.ResourceType, ["id"] = i.ObjectId, ["name"] = i.DisplayName, ["hash"] = i.Hash, ["size"] = i.Size,
            }).ToArray()),
            ["issues"] = new JsonArray(issues.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = i.ResourceType, ["id"] = i.ObjectId, ["severity"] = i.Severity, ["message"] = i.Message,
            }).ToArray()),
        };
        await content.PutDocumentAsync(tenant, s.StorageTargetId, ManifestName(s.Id), JsonSerializer.SerializeToUtf8Bytes(manifest), ct);
    }

    public static string ManifestName(Guid snapshotId) => $"snapshots/{snapshotId}/manifest.json";

    private async Task<List<ChangeEntry>> ComputeChangesAsync(Guid tenantId, Guid snapshotId, List<SnapshotItem> items,
        HashSet<string> capturedTypes, List<SnapshotIssue> issues, CancellationToken ct)
    {
        var previous = await db.Snapshots.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Id != snapshotId && (s.Status == SnapshotStatus.Completed || s.Status == SnapshotStatus.Partial))
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (previous is null) return [];
        var prevItems = await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == previous.Id && capturedTypes.Contains(i.ResourceType))
            .Select(i => new { i.ResourceType, i.ObjectId, i.DisplayName, i.Hash }).ToListAsync(ct);
        var prevMap = prevItems.ToDictionary(i => (i.ResourceType, i.ObjectId));
        var changes = new List<ChangeEntry>();
        foreach (var it in items)
        {
            if (!prevMap.Remove((it.ResourceType, it.ObjectId), out var p)) changes.Add(new(it.ResourceType, it.ObjectId, it.DisplayName, "Added"));
            else if (p.Hash != it.Hash) changes.Add(new(it.ResourceType, it.ObjectId, it.DisplayName, "Modified"));
        }
        // Objects that failed to read individually are not "removed".
        var partial = issues.Where(i => i.ObjectId != null).Select(i => (i.ResourceType, i.ObjectId!)).ToHashSet();
        foreach (var gone in prevMap.Values.Where(p => !partial.Contains((p.ResourceType, p.ObjectId))))
            changes.Add(new(gone.ResourceType, gone.ObjectId, gone.DisplayName, "Removed"));
        return changes;
    }

    public static (IssueSeverity, string) Classify(ResourceDefinition def, Exception e)
    {
        if (e is ApiException api)
        {
            var msg = api.Message;
            if (api.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                if (msg.Contains("license", StringComparison.OrdinalIgnoreCase) || msg.Contains("not applicable", StringComparison.OrdinalIgnoreCase))
                    return (IssueSeverity.Info, $"Not available in this tenant (licensing): {msg}");
                return (IssueSeverity.Warning, $"Access denied. The backup app is probably missing a permission for {def.DisplayName}. ({msg})");
            }
            if (api.StatusCode == HttpStatusCode.NotFound ||
                msg.Contains("not applicable to target tenant", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("not licensed", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("does not have", StringComparison.OrdinalIgnoreCase) && msg.Contains("license", StringComparison.OrdinalIgnoreCase))
                return (IssueSeverity.Info, $"Not available in this tenant: {msg}");
        }
        return (IssueSeverity.Error, e.Message);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class ConfigBackupJob : IJobHandler
{
    public string Type => "backup.config";
    public TenantRole RequiredRole => TenantRole.BackupOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var svc = (ConfigBackupService)context.Services.GetService(typeof(ConfigBackupService))!;
        var p = context.Parameters;
        var workloads = p.GetStringList("workloads");
        var (snapshot, changes) = await svc.RunAsync(tenant,
            new ConfigBackupRequest(workloads.Count > 0 ? workloads : null, p.GetGuid("storageTargetId"), p.GetString("label"), context.Job.CreatedBy),
            context, ct);
        await context.InfoAsync($"Snapshot {snapshot.Id}: {snapshot.ItemCount} items, {snapshot.AddedCount} added, {snapshot.ModifiedCount} modified, {snapshot.RemovedCount} removed since the previous backup");

        var result = new JsonObject
        {
            ["snapshotId"] = snapshot.Id.ToString(),
            ["items"] = snapshot.ItemCount,
            ["errors"] = snapshot.ErrorCount,
            ["warnings"] = snapshot.WarningCount,
            ["added"] = snapshot.AddedCount,
            ["modified"] = snapshot.ModifiedCount,
            ["removed"] = snapshot.RemovedCount,
            ["changes"] = JsonSerializer.SerializeToNode(changes.Take(500), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        return snapshot.Status == SnapshotStatus.Completed ? JobOutcome.Success(result) : JobOutcome.Partial(result);
    }
}

/// <summary>Runs after each configuration backup (used for drift alerts).</summary>
public interface IPostBackupHook
{
    Task AfterConfigBackupAsync(Tenant tenant, Snapshot snapshot, IReadOnlyList<ChangeEntry> changes, IJobContext? context, CancellationToken ct);
}
