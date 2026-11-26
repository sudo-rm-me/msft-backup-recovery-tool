using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Diff;

public record DiffEntry(string Type, string Workload, string Change, string? FromObjectId, string? ToObjectId, string DisplayName,
    long? FromItemId, long? ToItemId);

public record DiffSummary(Guid FromSnapshotId, Guid ToSnapshotId, bool CrossTenant, int Added, int Removed, int Modified, int Unchanged,
    IReadOnlyList<DiffEntry> Entries);

public record ItemDiff(JsonObject? Before, JsonObject? After, IReadOnlyList<JsonChange> Changes);

/// <summary>Compares two snapshots (of the same or different tenants).</summary>
public sealed class DiffService(MbrDbContext db, ResourceRegistry registry, SnapshotReader reader)
{
    private record Row(long Id, string ResourceType, string Workload, string ObjectId, string DisplayName, string Hash);

    public async Task<DiffSummary> CompareAsync(Snapshot from, Snapshot to, IReadOnlyCollection<string>? types, CancellationToken ct)
    {
        var a = await LoadRowsAsync(from.Id, types, ct);
        var b = await LoadRowsAsync(to.Id, types, ct);
        // Only compare types captured in both snapshots, so a snapshot of fewer workloads doesn't show everything as removed.
        var fromTypes = await TypesCapturedAsync(from.Id, ct);
        var toTypes = await TypesCapturedAsync(to.Id, ct);
        var comparable = fromTypes.Intersect(toTypes).ToHashSet();
        a = a.Where(r => comparable.Contains(r.ResourceType)).ToList();
        b = b.Where(r => comparable.Contains(r.ResourceType)).ToList();

        var crossTenant = from.TenantId != to.TenantId;
        var entries = new List<DiffEntry>();
        var unchanged = 0;
        foreach (var group in a.Concat(b).GroupBy(r => r.ResourceType))
        {
            var type = group.Key;
            var left = a.Where(r => r.ResourceType == type).ToList();
            var right = b.Where(r => r.ResourceType == type).ToList();
            var matches = crossTenant ? await MatchByKeysAsync(from, to, type, left, right, ct) : MatchById(left, right);
            foreach (var (l, r) in matches)
            {
                if (l != null && r != null)
                {
                    if (l.Hash == r.Hash) { unchanged++; continue; }
                    entries.Add(new(type, l.Workload, "Modified", l.ObjectId, r.ObjectId, r.DisplayName, l.Id, r.Id));
                }
                else if (l != null) entries.Add(new(type, l.Workload, "Removed", l.ObjectId, null, l.DisplayName, l.Id, null));
                else entries.Add(new(type, r!.Workload, "Added", null, r.ObjectId, r.DisplayName, null, r.Id));
            }
        }
        entries = entries.OrderBy(e => e.Workload).ThenBy(e => e.Type).ThenBy(e => e.DisplayName).ToList();
        return new DiffSummary(from.Id, to.Id, crossTenant, entries.Count(e => e.Change == "Added"), entries.Count(e => e.Change == "Removed"),
            entries.Count(e => e.Change == "Modified"), unchanged, entries);
    }

    public async Task<ItemDiff> CompareItemsAsync(Tenant? fromTenant, Snapshot? from, SnapshotItem? a, Tenant? toTenant, Snapshot? to, SnapshotItem? b, CancellationToken ct)
    {
        var before = a is null ? null : await reader.GetContentAsync(fromTenant!, from!, a, ct);
        var after = b is null ? null : await reader.GetContentAsync(toTenant!, to!, b, ct);
        return new ItemDiff(before, after, JsonDiff.Compare(before, after));
    }

    private async Task<HashSet<string>> TypesCapturedAsync(Guid snapshotId, CancellationToken ct)
    {
        // A type counts as captured unless it failed (Warning/Error with no object ID).
        var present = await db.SnapshotItems.Where(i => i.SnapshotId == snapshotId).Select(i => i.ResourceType).Distinct().ToListAsync(ct);
        var failed = await db.SnapshotIssues.Where(i => i.SnapshotId == snapshotId && i.ObjectId == null && i.Severity != "Info").Select(i => i.ResourceType).Distinct().ToListAsync(ct);
        var snapshot = await db.Snapshots.AsNoTracking().FirstAsync(s => s.Id == snapshotId, ct);
        var workloads = snapshot.Workloads.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var all = registry.Handlers.Where(h => workloads.Contains(h.Definition.Workload)).Select(h => h.Definition.Type);
        return all.Concat(present).Except(failed).ToHashSet();
    }

    private async Task<List<Row>> LoadRowsAsync(Guid snapshotId, IReadOnlyCollection<string>? types, CancellationToken ct)
    {
        var q = db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId);
        if (types is { Count: > 0 }) q = q.Where(i => types.Contains(i.ResourceType) || types.Contains(i.Workload));
        return await q.Select(i => new Row(i.Id, i.ResourceType, i.Workload, i.ObjectId, i.DisplayName, i.Hash)).ToListAsync(ct);
    }

    private static List<(Row?, Row?)> MatchById(List<Row> left, List<Row> right)
    {
        var r = right.ToDictionary(x => x.ObjectId);
        var result = new List<(Row?, Row?)>();
        foreach (var l in left) result.Add((l, r.Remove(l.ObjectId, out var m) ? m : null));
        result.AddRange(r.Values.Select(x => ((Row?)null, (Row?)x)));
        return result;
    }

    /// <summary>Cross-tenant: object IDs differ, so match on the type's match keys (e.g. displayName).</summary>
    private async Task<List<(Row?, Row?)>> MatchByKeysAsync(Snapshot from, Snapshot to, string type, List<Row> left, List<Row> right, CancellationToken ct)
    {
        var def = registry.Find(type)?.Definition;
        var keys = def?.MatchKeys ?? ["displayName"];
        if (keys.SequenceEqual(["displayName"]))
            return MatchOn(left, right, x => x.DisplayName.ToLowerInvariant());
        var fromTenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == from.TenantId, ct);
        var toTenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == to.TenantId, ct);
        var lk = new Dictionary<long, string>();
        foreach (var l in left) lk[l.Id] = await KeyOfAsync(fromTenant, from, l, keys, ct);
        foreach (var r in right) lk[r.Id] = await KeyOfAsync(toTenant, to, r, keys, ct);
        return MatchOn(left, right, x => lk[x.Id]);
    }

    private async Task<string> KeyOfAsync(Tenant t, Snapshot s, Row row, List<string> keys, CancellationToken ct)
    {
        var item = await db.SnapshotItems.AsNoTracking().FirstAsync(i => i.Id == row.Id, ct);
        var content = await reader.GetContentAsync(t, s, item, ct);
        return string.Join("|", keys.Select(k => JsonPath.GetStrings(content, k).FirstOrDefault()?.ToLowerInvariant() ?? (k == "displayName" ? row.DisplayName.ToLowerInvariant() : "")));
    }

    private static List<(Row?, Row?)> MatchOn(List<Row> left, List<Row> right, Func<Row, string> key)
    {
        var r = right.GroupBy(key).ToDictionary(g => g.Key, g => new Queue<Row>(g));
        var result = new List<(Row?, Row?)>();
        foreach (var l in left)
            result.Add((l, r.TryGetValue(key(l), out var q) && q.Count > 0 ? q.Dequeue() : null));
        result.AddRange(r.Values.SelectMany(q => q).Select(x => ((Row?)null, (Row?)x)));
        return result;
    }
}

/// <summary>After each backup: if anything changed, alert (with the deviation from the baseline, if one is set).</summary>
public sealed class DriftAlertHook(MbrDbContext db, DiffService diff, INotifier notifier) : IPostBackupHook
{
    public async Task AfterConfigBackupAsync(Tenant tenant, Snapshot snapshot, IReadOnlyList<ChangeEntry> changes, IJobContext? context, CancellationToken ct)
    {
        var settings = await db.TenantSettings.FindAsync([tenant.Id], ct) ?? new TenantSettings { TenantId = tenant.Id };
        var baseline = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant.Id && s.IsBaseline && s.Id != snapshot.Id, ct);
        DiffSummary? vsBaseline = null;
        if (baseline != null)
        {
            vsBaseline = await diff.CompareAsync(baseline, snapshot, null, ct);
            if (context != null) await context.InfoAsync($"Drift from baseline ({baseline.CreatedAt:u}): {vsBaseline.Added} added, {vsBaseline.Modified} modified, {vsBaseline.Removed} removed");
        }
        if (!settings.DriftAlerts || changes.Count == 0) return;
        // The very first backup has nothing to compare with, so ComputeChanges returns nothing: no alert.
        var lines = changes.Take(15).Select(c => $"• {c.Change}: {c.DisplayName} ({c.Type})");
        var more = changes.Count > 15 ? $"\n…and {changes.Count - 15} more" : "";
        var baselineLine = vsBaseline is null ? "" : $"\nDeviation from baseline: {vsBaseline.Added + vsBaseline.Modified + vsBaseline.Removed} object(s).";
        await notifier.NotifyAsync(new Notification(
            $"Configuration changed in {tenant.DisplayName}",
            $"{changes.Count} change(s) since the previous backup:\n{string.Join("\n", lines)}{more}{baselineLine}",
            tenant.Id, context?.Job.Id, "info"), ct);
    }
}

public sealed class DriftCheckJob : IJobHandler
{
    public string Type => "config.drift";
    public Core.Model.TenantRole RequiredRole => Core.Model.TenantRole.BackupOperator;

    /// <summary>Takes a fresh snapshot ("live") and compares it with a reference snapshot (default: the baseline).</summary>
    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var sp = context.Services;
        var db = (MbrDbContext)sp.GetService(typeof(MbrDbContext))!;
        var backup = (ConfigBackupService)sp.GetService(typeof(ConfigBackupService))!;
        var diff = (DiffService)sp.GetService(typeof(DiffService))!;

        var againstId = context.Parameters.GetGuid("against");
        var against = againstId is { } aid
            ? await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == aid && s.TenantId == tenant.Id, ct)
            : await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenant.Id && s.IsBaseline, ct);
        if (against is null) throw new InvalidOperationException("No reference snapshot: set a baseline or pass 'against'.");

        var workloads = context.Parameters.GetStringList("workloads");
        if (workloads.Count == 0) workloads = against.Workloads.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var (live, _) = await backup.RunAsync(tenant, new ConfigBackupRequest(workloads, null, "Live comparison", context.Job.CreatedBy), context, ct);
        var summary = await diff.CompareAsync(against, live, null, ct);
        await context.InfoAsync($"Compared with {against.CreatedAt:u}: {summary.Added} added, {summary.Modified} modified, {summary.Removed} removed, {summary.Unchanged} unchanged");
        return JobOutcome.Success(new JsonObject
        {
            ["from"] = against.Id.ToString(), ["to"] = live.Id.ToString(),
            ["added"] = summary.Added, ["modified"] = summary.Modified, ["removed"] = summary.Removed, ["unchanged"] = summary.Unchanged,
        });
    }
}
