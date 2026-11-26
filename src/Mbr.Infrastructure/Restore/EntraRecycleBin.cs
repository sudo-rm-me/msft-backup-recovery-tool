using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Restore;

public record DeletedObject(string Id, string Kind, string DisplayName, string? UserPrincipalName, string? Mail, DateTimeOffset? DeletedDateTime,
    int? DaysLeft, long? SnapshotItemId, Guid? SnapshotId);

/// <summary>Reads the Entra recycle bin (deleted users, groups, apps, administrative units) and undeletes objects.</summary>
public sealed class EntraRecycleBin(TenantSessionFactory sessions, MbrDbContext db)
{
    public static readonly Dictionary<string, (string GraphType, string ResourceType)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = ("microsoft.graph.user", "entra.user"),
        ["group"] = ("microsoft.graph.group", "entra.group"),
        ["application"] = ("microsoft.graph.application", "entra.application"),
        ["servicePrincipal"] = ("microsoft.graph.servicePrincipal", "entra.servicePrincipal"),
        ["administrativeUnit"] = ("microsoft.graph.administrativeUnit", "entra.administrativeUnit"),
    };

    /// <summary>Entra keeps deleted objects for 30 days.</summary>
    public const int RetentionDays = 30;

    public async Task<List<DeletedObject>> ListAsync(Tenant tenant, IEnumerable<string>? kinds, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        var latest = await db.Snapshots.AsNoTracking().Where(s => s.TenantId == tenant.Id && s.Status != SnapshotStatus.Running && s.Workloads.Contains("entra"))
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var result = new List<DeletedObject>();
        foreach (var kind in kinds?.ToList() is { Count: > 0 } k ? k : Kinds.Keys.ToList())
        {
            if (!Kinds.TryGetValue(kind, out var info)) throw new ArgumentException($"Unknown kind '{kind}'. Use: {string.Join(", ", Kinds.Keys)}.");
            var items = new List<JsonObject>();
            try
            {
                await foreach (var o in graph.ListAsync($"/v1.0/directory/deletedItems/{info.GraphType}", ct)) items.Add(o);
            }
            catch (ApiException e) when (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound) { continue; }

            var ids = items.Select(i => i["id"]!.GetValue<string>()).ToList();
            var inSnapshot = latest is null ? new Dictionary<string, long>() : await db.SnapshotItems.AsNoTracking()
                .Where(i => i.SnapshotId == latest.Id && i.ResourceType == info.ResourceType && ids.Contains(i.ObjectId))
                .ToDictionaryAsync(i => i.ObjectId, i => i.Id, ct);
            foreach (var o in items)
            {
                var id = o["id"]!.GetValue<string>();
                DateTimeOffset? deleted = DateTimeOffset.TryParse(o["deletedDateTime"]?.ToString(), out var d) ? d : null;
                result.Add(new DeletedObject(id, kind, o["displayName"]?.ToString() ?? id, o["userPrincipalName"]?.ToString(), o["mail"]?.ToString(), deleted,
                    deleted is { } x ? Math.Max(0, RetentionDays - (int)(DateTimeOffset.UtcNow - x).TotalDays) : null,
                    inSnapshot.TryGetValue(id, out var itemId) ? itemId : null, inSnapshot.ContainsKey(id) ? latest!.Id : null));
            }
        }
        return result.OrderByDescending(r => r.DeletedDateTime).ToList();
    }

    public async Task<JsonObject> RestoreAsync(Tenant tenant, string id, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Restore).Graph;
        return await graph.SendAsync(HttpMethod.Post, $"/v1.0/directory/deletedItems/{Uri.EscapeDataString(id)}/restore", new JsonObject(), ct: ct) as JsonObject ?? new JsonObject();
    }
}

/// <summary>Undeletes Entra objects from the recycle bin (parameters: ids).</summary>
public sealed class EntraUndeleteJob : IJobHandler
{
    public string Type => "entra.undelete";
    public TenantRole RequiredRole => TenantRole.RestoreOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var bin = context.Services.GetRequiredService<EntraRecycleBin>();
        var audit = context.Services.GetRequiredService<AuditLog>();
        var ids = context.Parameters.GetStringList("ids");
        if (ids.Count == 0) throw new ArgumentException("ids is required.");
        var ok = new JsonArray();
        var failed = new JsonArray();
        for (var i = 0; i < ids.Count; i++)
        {
            await context.ReportProgressAsync((double)i / ids.Count, $"Restoring {ids[i]}");
            try
            {
                var restored = await bin.RestoreAsync(tenant, ids[i], ct);
                await context.InfoAsync($"Restored {restored["displayName"] ?? ids[i]} ({ids[i]})");
                ok.Add(ids[i]);
            }
            catch (ApiException e)
            {
                await context.ErrorAsync($"{ids[i]}: {e.Message}");
                failed.Add(new JsonObject { ["id"] = ids[i], ["error"] = e.Message });
            }
        }
        await audit.WriteAsync(context.Job.CreatedBy ?? "system", "entra.undelete", tenant.Id, null, new { restored = ok.Count, failed = failed.Count, ids }, ct: ct);
        var result = new JsonObject { ["restored"] = ok, ["failed"] = failed };
        return failed.Count == 0 ? JobOutcome.Success(result) : ok.Count > 0 ? JobOutcome.Partial(result) : throw new InvalidOperationException("No objects could be restored.");
    }
}
