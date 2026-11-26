using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Files;

/// <summary>Finds the data sources (OneDrives, SharePoint sites, mailboxes) in a tenant and keeps the registry current.</summary>
public sealed class DataDiscoveryService(MbrDbContext db, TenantSessionFactory sessions, IEnumerable<IDataSourceDiscoverer> discoverers)
{
    public record Result(int Found, int Added, int Missing);

    public async Task<Result> DiscoverAsync(Tenant tenant, IReadOnlyCollection<DataSourceKind>? kinds, IJobContext? job, CancellationToken ct)
    {
        var session = sessions.Create(tenant, AppRole.Backup);
        int found = 0, added = 0, missing = 0;
        foreach (var d in discoverers.Where(d => kinds is null || kinds.Count == 0 || kinds.Contains(d.Kind)))
        {
            if (job != null) await job.InfoAsync($"Discovering {d.Kind} sources");
            List<DiscoveredSource> list;
            try { list = await d.DiscoverAsync(session, ct); }
            catch (ApiException e)
            {
                if (job != null) await job.WarnAsync($"{d.Kind}: discovery failed: {e.Message}");
                continue;
            }
            var existing = await db.DataSources.Where(s => s.TenantId == tenant.Id && s.Kind == d.Kind).ToDictionaryAsync(s => s.ExternalId, ct);
            foreach (var src in list)
            {
                found++;
                if (existing.Remove(src.ExternalId, out var row))
                {
                    row.DisplayName = src.DisplayName;
                    row.Address = src.Address;
                    row.Missing = false;
                }
                else
                {
                    db.DataSources.Add(new DataSource { TenantId = tenant.Id, Kind = d.Kind, ExternalId = src.ExternalId, DisplayName = src.DisplayName, Address = src.Address });
                    added++;
                }
            }
            foreach (var gone in existing.Values.Where(s => !s.Missing))
            {
                gone.Missing = true;
                missing++;
            }
            await db.SaveChangesAsync(ct);
            if (job != null) await job.InfoAsync($"{d.Kind}: {list.Count} found");
        }
        return new Result(found, added, missing);
    }
}

public record DiscoveredSource(string ExternalId, string DisplayName, string? Address);

/// <summary>Lists the sources of one kind in a tenant.</summary>
public interface IDataSourceDiscoverer
{
    DataSourceKind Kind { get; }
    Task<List<DiscoveredSource>> DiscoverAsync(TenantSession session, CancellationToken ct);
}

public sealed class OneDriveDiscoverer : IDataSourceDiscoverer
{
    public DataSourceKind Kind => DataSourceKind.OneDrive;

    public async Task<List<DiscoveredSource>> DiscoverAsync(TenantSession session, CancellationToken ct)
    {
        var users = new List<JsonObject>();
        await foreach (var u in session.Graph.ListAsync("/v1.0/users?$select=id,displayName,userPrincipalName,userType&$top=999", ct))
            if (u["userType"]?.ToString() != "Guest") users.Add(u);
        var result = new List<DiscoveredSource>();
        // Only users whose OneDrive has been provisioned have a drive.
        await Parallel.ForEachAsync(users, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (u, token) =>
        {
            var id = u["id"]!.GetValue<string>();
            var drive = await session.Graph.TryGetAsync($"/v1.0/users/{id}/drive?$select=id", token);
            if (drive is null) return;
            lock (result) result.Add(new DiscoveredSource(id, u["displayName"]?.ToString() ?? id, u["userPrincipalName"]?.ToString()));
        });
        return result.OrderBy(r => r.DisplayName).ToList();
    }
}

public sealed class SharePointDiscoverer : IDataSourceDiscoverer
{
    public DataSourceKind Kind => DataSourceKind.SharePointSite;

    public async Task<List<DiscoveredSource>> DiscoverAsync(TenantSession session, CancellationToken ct)
    {
        var result = new List<DiscoveredSource>();
        await foreach (var s in session.Graph.ListAsync("/v1.0/sites/getAllSites?$select=id,displayName,name,webUrl&$top=999", ct))
        {
            var url = s["webUrl"]?.ToString() ?? "";
            if (url.Contains("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase)) continue; // OneDrives are their own sources
            result.Add(new DiscoveredSource(s["id"]!.GetValue<string>(), s["displayName"]?.ToString() ?? s["name"]?.ToString() ?? url, url));
        }
        return result.OrderBy(r => r.DisplayName).ToList();
    }
}

public sealed class DataDiscoveryJob : IJobHandler
{
    public string Type => "data.discover";
    public TenantRole RequiredRole => TenantRole.BackupOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var kinds = context.Parameters.GetStringList("kinds").Select(k => Enum.Parse<DataSourceKind>(k, true)).ToList();
        var r = await context.Services.GetRequiredService<DataDiscoveryService>().DiscoverAsync(tenant, kinds, context, ct);
        await context.InfoAsync($"{r.Found} source(s) found, {r.Added} new, {r.Missing} no longer present");
        return JobOutcome.Success(new JsonObject { ["found"] = r.Found, ["added"] = r.Added, ["missing"] = r.Missing });
    }
}
