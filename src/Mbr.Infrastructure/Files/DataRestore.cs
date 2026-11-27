using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Files;

/// <summary>What to restore (files, folders, messages, events, contacts) and where.</summary>
public sealed class DataRestoreRequest
{
    public Guid ContainerId { get; set; }
    /// <summary>Items (file/folder/message IDs) to restore; folders include everything under them.</summary>
    public List<string> ItemIds { get; set; } = new();
    /// <summary>Restore the items as they were at this time. Default: the latest backup (for deleted items: just before deletion).</summary>
    public DateTimeOffset? At { get; set; }
    /// <summary>Restore into another source (another user's OneDrive or mailbox, another site). Default: the original source.</summary>
    public Guid? TargetSourceId { get; set; }
    /// <summary>
    /// Restore into another tenant (e.g. a replacement for a lost one). Without <see cref="TargetSourceId"/>, the target is the
    /// source there with the same address (after <see cref="DomainMap"/>) or, for sites, the same name.
    /// </summary>
    public Guid? TargetTenantId { get; set; }
    /// <summary>Old → new domains, used to find the target user in another tenant. See RestoreOptions.DomainMap.</summary>
    public Dictionary<string, string>? DomainMap { get; set; }
    /// <summary>
    /// "original": back where they were (missing folders are recreated); into another tenant, the same paths in the target.
    /// "folder": into a new folder, keeping the structure. Restores into someone else's data in the same tenant always use a folder.
    /// </summary>
    public string Location { get; set; } = "original";
    public string? FolderName { get; set; }
    /// <summary>When an item with the same name exists: "rename" (keep both), "replace" or "skip".</summary>
    public string Conflict { get; set; } = "rename";
}

/// <summary>One item to restore, with the folder (relative to the destination of its selection root) it goes into.</summary>
public sealed record RestoreEntry(DataItemVersion Version, DataItemVersion Root, string RelativeDir);

public sealed class DataRestoreResult
{
    public int Restored;
    public int Skipped;
    public int Failed;
    public long Bytes;
    public List<string> Errors { get; } = new();
}

/// <summary>Writes items back for one kind of container.</summary>
public interface IDataRestorer
{
    bool Handles(string containerKind);
    Task RestoreAsync(DataRestoreContext ctx, CancellationToken ct);
}

public sealed class DataRestoreContext
{
    /// <summary>The tenant the backup belongs to (content is read with its keys).</summary>
    public required Tenant Tenant { get; init; }
    /// <summary>The tenant written to: <see cref="Tenant"/>, or another one for cross-tenant restores.</summary>
    public required Tenant TargetTenant { get; init; }
    public required DataSource Source { get; init; }
    public required DataContainer Container { get; init; }
    public required DataSource Target { get; init; }
    public required DataRestoreRequest Request { get; init; }
    public required List<RestoreEntry> Entries { get; init; }
    public required IApiClient Graph { get; init; }
    public required DataReader Reader { get; init; }
    public IJobContext? Job { get; init; }
    public DataRestoreResult Result { get; } = new();
    public bool SameSource => Source.Id == Target.Id;
    public bool CrossTenant => Tenant.Id != TargetTenant.Id;
    /// <summary>Into a separate folder: asked for, or always when restoring into someone else's data in the same tenant.</summary>
    public bool IntoFolder => Request.Location == "folder" || (!SameSource && !CrossTenant);
    public string FolderName { get; set; } = "";

    /// <summary>Destination folder path for an entry ("/Restored 2026-11-26/Reports/Q1").</summary>
    public string DestinationDir(RestoreEntry e)
    {
        var basePath = IntoFolder ? "/" + FolderName + (e.Root.Path == "/" ? "" : e.Root.Path) : e.Root.Path;
        return Combine(basePath, e.RelativeDir);
    }

    public static string Combine(string a, string b) =>
        "/" + string.Join("/", (a + "/" + b).Split('/', StringSplitOptions.RemoveEmptyEntries));

    public async Task LogAsync(string message) { if (Job != null) await Job.InfoAsync(message); }

    public async Task FailAsync(RestoreEntry e, string error)
    {
        Result.Failed++;
        lock (Result.Errors) Result.Errors.Add($"{e.Version.Name}: {error}");
        if (Job != null) await Job.ErrorAsync($"{e.Version.Path.TrimEnd('/')}/{e.Version.Name}: {error}");
    }
}

/// <summary>No source in the target tenant matches; discovery there, a domain map or an explicit target may fix it.</summary>
public sealed class RestoreTargetNotFoundException(string message) : ArgumentException(message);

public sealed class DataRestoreService(MbrDbContext db, DataReader reader, Backup.TenantSessionFactory sessions, IEnumerable<IDataRestorer> restorers,
    DataDiscoveryService discovery)
{
    /// <summary>Expands the selection: each item as of the chosen time, folders with everything under them.</summary>
    public async Task<List<RestoreEntry>> ExpandAsync(DataContainer c, IEnumerable<string> itemIds, DateTimeOffset? at, CancellationToken ct)
    {
        var entries = new List<RestoreEntry>();
        foreach (var id in itemIds.Distinct())
        {
            var v = await reader.VersionAtAsync(c.Id, id, at, ct) ?? throw new KeyNotFoundException($"Item {id} isn't in the backup{(at is null ? "" : $" as of {at:u}")}.");
            // A deleted item with no time given comes back as it was just before it was deleted.
            var when = at ?? (v.DeletedAt is { } d ? d.AddMilliseconds(-1) : null);
            if (when != null && at == null) v = await reader.VersionAtAsync(c.Id, id, when, ct) ?? v;
            entries.Add(new RestoreEntry(v, v, ""));
            if (!v.IsFolder) continue;
            var queue = new Queue<(string ItemId, string Rel)>();
            queue.Enqueue((v.ItemId, v.Name));
            while (queue.Count > 0)
            {
                var (folder, rel) = queue.Dequeue();
                var children = await reader.ChildrenAsync(c, folder, when, false, ct);
                foreach (var child in children)
                {
                    entries.Add(new RestoreEntry(child, v, rel));
                    if (child.IsFolder) queue.Enqueue((child.ItemId, rel + "/" + child.Name));
                }
            }
        }
        return entries;
    }

    public async Task<DataRestoreResult> RunAsync(Tenant tenant, DataRestoreRequest req, IJobContext? job, CancellationToken ct)
    {
        var c = await db.DataContainers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ContainerId && x.TenantId == tenant.Id, ct)
                ?? throw new KeyNotFoundException("Container not found.");
        var source = await db.DataSources.AsNoTracking().FirstAsync(s => s.Id == c.SourceId, ct);
        var (targetTenant, target) = await ResolveTargetAsync(tenant, source, req, ct, discover: job);
        if (req.Conflict is not ("rename" or "replace" or "skip")) throw new ArgumentException("Conflict must be rename, replace or skip.");
        var restorer = restorers.FirstOrDefault(r => r.Handles(c.Kind)) ?? throw new InvalidOperationException($"Restoring {c.Kind} isn't supported.");

        var entries = await ExpandAsync(c, req.ItemIds, req.At, ct);
        var ctx = new DataRestoreContext
        {
            Tenant = tenant, TargetTenant = targetTenant, Source = source, Container = c, Target = target, Request = req, Entries = entries,
            Graph = sessions.Create(targetTenant, AppRole.Restore).Graph, Reader = reader, Job = job,
            FolderName = string.IsNullOrWhiteSpace(req.FolderName) ? $"Restored {DateTimeOffset.UtcNow:yyyy-MM-dd HHmm}" : req.FolderName.Trim(),
        };
        if (job != null)
            await job.InfoAsync($"Restoring {entries.Count} item(s) from {source.DisplayName} to {(ctx.SameSource ? "the same place" : target.DisplayName)}" +
                                (ctx.CrossTenant ? $" in {targetTenant.DisplayName}" : "") +
                                (ctx.IntoFolder ? $", into folder '{ctx.FolderName}'" : ", to their original location") + $"; on name conflicts: {req.Conflict}");
        await restorer.RestoreAsync(ctx, ct);
        return ctx.Result;
    }

    /// <summary>
    /// Where a restore goes: the chosen source, or for another tenant the source there with the same (domain-mapped) address,
    /// or for sites the same URL on the target's SharePoint host, else the same name. Throws when it's ambiguous or missing, so
    /// nothing is written to the wrong place. With <paramref name="discover"/>, a missing match triggers a discovery in the
    /// target tenant first (new users and sites).
    /// </summary>
    public async Task<(Tenant Tenant, DataSource Source)> ResolveTargetAsync(Tenant tenant, DataSource source, DataRestoreRequest req,
        CancellationToken ct, IJobContext? discover = null)
    {
        try { return await ResolveOnceAsync(tenant, source, req, ct); }
        catch (RestoreTargetNotFoundException) when (discover != null && req.TargetTenantId is { } t && t != tenant.Id && req.TargetSourceId is null)
        {
            var targetTenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t, ct);
            await discover.InfoAsync($"No match for {source.DisplayName} in {targetTenant.DisplayName} yet: discovering its {source.Kind} sources");
            await discovery.DiscoverAsync(targetTenant, [source.Kind], discover, ct);
            return await ResolveOnceAsync(tenant, source, req, ct);
        }
    }

    private async Task<(Tenant Tenant, DataSource Source)> ResolveOnceAsync(Tenant tenant, DataSource source, DataRestoreRequest req, CancellationToken ct)
    {
        var targetTenant = req.TargetTenantId is { } ttid && ttid != tenant.Id
            ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ttid, ct) ?? throw new KeyNotFoundException("Target tenant not found.")
            : tenant;
        DataSource target;
        if (req.TargetSourceId is { } tid && tid != source.Id)
            target = await db.DataSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == tid && s.TenantId == targetTenant.Id, ct)
                     ?? throw new KeyNotFoundException(targetTenant.Id == tenant.Id ? "Target not found." : $"Target not found in {targetTenant.DisplayName}.");
        else if (targetTenant.Id == tenant.Id)
            target = source;
        else
        {
            var candidates = await db.DataSources.AsNoTracking().Where(s => s.TenantId == targetTenant.Id && s.Kind == source.Kind && !s.Missing).ToListAsync(ct);
            List<DataSource> matches;
            if (source.Kind == DataSourceKind.SharePointSite)
            {
                // Same path on the target's SharePoint host (contoso.sharepoint.com/sites/x → fabrikam.sharepoint.com/sites/x), else the same name.
                var mapped = MapSiteUrl(source.Address, candidates);
                matches = mapped is null ? [] : candidates.Where(s => SameUrl(s.Address, mapped)).ToList();
                if (matches.Count == 0)
                    matches = candidates.Where(s => string.Equals(s.DisplayName, source.DisplayName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                var domains = Restore.RestoreEngine.EffectiveDomainMap(req.DomainMap, tenant, targetTenant);
                matches = candidates.Where(s => source.Address is { Length: > 0 } a && string.Equals(s.Address, domains.Apply(a), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            target = matches.Count switch
            {
                1 => matches[0],
                0 => throw new RestoreTargetNotFoundException(source.Kind == DataSourceKind.SharePointSite
                    ? $"No site matching {source.Address ?? source.DisplayName} in {targetTenant.DisplayName}. Create the site there (or choose another), then try again."
                    : $"No {(source.Kind == DataSourceKind.Mailbox ? "mailbox" : "OneDrive")} for {source.Address ?? source.DisplayName} in {targetTenant.DisplayName}. " +
                      "Map the domain, restore the user there first (a config restore of entra.user; their OneDrive and mailbox are " +
                      "provisioned once they're licensed), or choose the target."),
                _ => throw new ArgumentException($"Several sources in {targetTenant.DisplayName} match {source.DisplayName}; choose the target."),
            };
        }
        if (target.Kind == DataSourceKind.Mailbox != (source.Kind == DataSourceKind.Mailbox))
            throw new ArgumentException("Mail, calendar and contacts can only be restored to a mailbox, and files only to OneDrive or SharePoint.");
        return (targetTenant, target);
    }

    /// <summary>The site's URL on the SharePoint host most of the target's sites use; null when either isn't known.</summary>
    private static string? MapSiteUrl(string? address, List<DataSource> targetSites)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) return null;
        var host = targetSites.Select(s => Uri.TryCreate(s.Address, UriKind.Absolute, out var u) ? u.Host : null)
            .Where(h => h != null && !h.Contains("-my.", StringComparison.OrdinalIgnoreCase))
            .GroupBy(h => h!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return host is null ? null : new UriBuilder(url) { Host = host }.Uri.ToString();
    }

    private static bool SameUrl(string? a, string b) =>
        a is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>"restore.data": restores files, folders, mail, events or contacts. Parameters: a <see cref="DataRestoreRequest"/>.</summary>
public sealed class DataRestoreJob : IJobHandler
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Type => "restore.data";
    public TenantRole RequiredRole => TenantRole.RestoreOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var req = context.Parameters.Deserialize<DataRestoreRequest>(Json) ?? throw new ArgumentException("Missing restore request.");
        var svc = context.Services.GetRequiredService<DataRestoreService>();
        var r = await svc.RunAsync(tenant, req, context, ct);
        await context.Services.GetRequiredService<AuditLog>().WriteAsync(context.Job.CreatedBy ?? "system", "data.restored", tenant.Id,
            req.ContainerId.ToString(), new { req.ItemIds, req.At, req.TargetTenantId, req.TargetSourceId, req.Location, req.Conflict, r.Restored, r.Skipped, r.Failed }, ct: ct);
        await context.InfoAsync($"{r.Restored} restored, {r.Skipped} skipped (already there), {r.Failed} failed");
        var result = new JsonObject
        {
            ["restored"] = r.Restored, ["skipped"] = r.Skipped, ["failed"] = r.Failed, ["bytes"] = r.Bytes,
            ["errors"] = new JsonArray(r.Errors.Take(50).Select(e => (JsonNode)e).ToArray()),
        };
        if (r.Failed > 0 && r.Restored == 0 && r.Skipped == 0) throw new InvalidOperationException($"Nothing could be restored: {r.Errors.FirstOrDefault()}");
        return r.Failed > 0 ? JobOutcome.Partial(result) : JobOutcome.Success(result);
    }
}
