using System.Text.Json.Nodes;
using Mbr.Catalog.Azure;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.AzureConfig;

public record AzureResourceGroupInfo(long SnapshotItemId, string Id, string Name, string SubscriptionId, string? SubscriptionName, string? Location,
    int ResourceCount, IReadOnlyList<string> ResourceTypes, string Status);

public record AzureInventoryResult(Guid? SnapshotId, DateTimeOffset? SnapshotAt, IReadOnlyList<AzureResourceGroupInfo> ResourceGroups, string? LiveError);

/// <summary>Resource groups in the latest backup, with whether each still exists — the starting point for redeploying a deleted one.</summary>
public sealed class AzureInventory(MbrDbContext db, SnapshotReader reader, TenantSessionFactory sessions)
{
    public async Task<AzureInventoryResult> ResourceGroupsAsync(Tenant tenant, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.AsNoTracking()
            .Where(s => s.TenantId == tenant.Id && (s.Status == SnapshotStatus.Completed || s.Status == SnapshotStatus.Partial) &&
                        db.SnapshotItems.Any(i => i.SnapshotId == s.Id && i.ResourceType == "azure.resourceGroup"))
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (snapshot is null) return new AzureInventoryResult(null, null, [], null);

        var rows = await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshot.Id && i.ResourceType == "azure.resourceGroup").ToListAsync(ct);
        var subNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshot.Id && i.ResourceType == "azure.subscription").ToListAsync(ct))
            subNames[sub.ObjectId.Split('/')[^1]] = sub.DisplayName;

        // What exists now (per subscription; a subscription we can't read leaves its groups "unknown").
        HashSet<string>? live = null;
        string? liveError = null;
        try
        {
            var session = sessions.Create(tenant, AppRole.Backup);
            live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subId in rows.Select(r => Arm.SubscriptionOf(r.ObjectId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                await foreach (var rg in session.Arm.ListAsync(Arm.Url($"{subId}/resourcegroups", Arm.ResourcesApi), ct))
                    if (rg["id"]?.ToString() is { } id) live.Add(id);
        }
        catch (Exception e) when (e is ApiException or InvalidOperationException or HttpRequestException)
        {
            live = null;
            liveError = e.Message;
        }

        var result = new List<AzureResourceGroupInfo>();
        foreach (var row in rows)
        {
            var content = await reader.GetContentAsync(tenant, snapshot, row, ct);
            var resources = content["resources"] as JsonArray ?? [];
            var subId = row.ObjectId.Split('/')[2];
            result.Add(new AzureResourceGroupInfo(row.Id, row.ObjectId, content["name"]?.ToString() ?? row.ObjectId.Split('/')[^1], subId,
                subNames.GetValueOrDefault(subId), content["location"]?.ToString(), resources.Count,
                resources.Select(r => r?["type"]?.ToString()).OfType<string>().Distinct().Order().ToList(),
                live is null ? "unknown" : live.Contains(row.ObjectId) ? "present" : "deleted"));
        }
        return new AzureInventoryResult(snapshot.Id, snapshot.CreatedAt,
            result.OrderBy(r => r.Status == "deleted" ? 0 : 1).ThenBy(r => r.SubscriptionName).ThenBy(r => r.Name).ToList(), liveError);
    }
}
