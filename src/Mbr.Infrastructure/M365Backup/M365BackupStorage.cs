using System.Net;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.M365Backup;

public record M365Policy(string Id, string DisplayName, string Kind, string Status, int Units, string? Retention);
public record M365Unit(string Id, string PolicyId, string Kind, string Status, string ResourceId, string DisplayName);
public record M365RestorePoint(string Id, DateTimeOffset ProtectionDateTime, DateTimeOffset? ExpirationDateTime, string? Tags);
public record M365Session(string Id, string Kind, string Status, DateTimeOffset? CreatedAt, DateTimeOffset? CompletedAt, string? CreatedBy, string? Error);
public record M365Status(bool Available, string Status, string? Message, IReadOnlyList<M365Policy> Policies, IReadOnlyList<M365Session> Sessions);

/// <summary>
/// Microsoft 365 Backup (Backup Storage): for tenants that license it, shows what Microsoft's own backup protects and runs
/// native restores (fast, full-fidelity, whole mailbox / OneDrive / site to a point in time) through Graph solutions/backupRestore.
/// It complements this tool's own item-level backups rather than replacing them.
/// </summary>
public sealed class M365BackupStorage(Backup.TenantSessionFactory sessions, MbrDbContext db)
{
    private const string Root = "/v1.0/solutions/backupRestore";

    public async Task<M365Status> StatusAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        JsonNode? service;
        try { service = await graph.GetAsync(Root, ct); }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return new M365Status(false, "unavailable",
                e.StatusCode == HttpStatusCode.Forbidden
                    ? "The backup app needs BackupRestore-Configuration.Read.All to read Microsoft 365 Backup."
                    : "Microsoft 365 Backup isn't set up in this tenant (it's a pay-as-you-go service enabled in the Microsoft 365 admin center).", [], []);
        }
        var status = service?["serviceStatus"]?["status"]?.ToString() ?? "unknown";
        var reason = service?["serviceStatus"]?["disableReason"]?.ToString();
        var units = await UnitsAsync(tenant, null, ct);
        var policies = new List<M365Policy>();
        await foreach (var p in graph.ListAsync($"{Root}/protectionPolicies", ct))
        {
            var id = p["id"]!.ToString();
            policies.Add(new M365Policy(id, p["displayName"]?.ToString() ?? id, KindOf(p["@odata.type"]?.ToString()), p["status"]?.ToString() ?? "unknown",
                units.Count(u => u.PolicyId == id), p["retentionSettings"]?.AsArray().FirstOrDefault()?["period"]?.ToString()));
        }
        return new M365Status(status is "enabled" or "protectionChangeLocked" or "restoreLocked", status,
            status == "disabled" ? $"Microsoft 365 Backup is disabled{(reason is null or "none" ? "" : $" ({reason})")}." : null,
            policies, await SessionsAsync(tenant, ct));
    }

    public async Task<List<M365Unit>> UnitsAsync(Tenant tenant, string? policyId, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        var names = await db.DataSources.AsNoTracking().Where(s => s.TenantId == tenant.Id)
            .Select(s => new { s.ExternalId, s.Address, s.DisplayName }).ToListAsync(ct);
        var units = new List<M365Unit>();
        await foreach (var u in graph.ListAsync($"{Root}/protectionUnits", ct))
        {
            if (policyId != null && u["policyId"]?.ToString() != policyId) continue;
            var resource = u["directoryObjectId"]?.ToString() ?? u["siteId"]?.ToString() ?? "";
            var name = u["email"]?.ToString() ?? u["siteName"]?.ToString() ?? u["siteWebUrl"]?.ToString()
                       ?? names.FirstOrDefault(n => n.ExternalId.Equals(resource, StringComparison.OrdinalIgnoreCase) || n.ExternalId.StartsWith(resource + ","))?.DisplayName
                       ?? resource;
            units.Add(new M365Unit(u["id"]!.ToString(), u["policyId"]?.ToString() ?? "", KindOf(u["@odata.type"]?.ToString()),
                u["status"]?.ToString() ?? "unknown", resource, name));
        }
        return units.OrderBy(u => u.Kind).ThenBy(u => u.DisplayName).ToList();
    }

    /// <summary>Restore points for one protection unit, newest first (default: the last 30 days).</summary>
    public async Task<List<M365RestorePoint>> RestorePointsAsync(Tenant tenant, string unitId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Restore).Graph;
        var body = new JsonObject
        {
            ["protectionUnitIds"] = new JsonArray(unitId),
            ["protectionTimePeriod"] = new JsonObject
            {
                ["startDateTime"] = (from ?? DateTimeOffset.UtcNow.AddDays(-30)).ToString("o"), ["endDateTime"] = (to ?? DateTimeOffset.UtcNow).ToString("o"),
            },
            ["restorePointPreference"] = "latest",
        };
        var result = await graph.SendAsync(HttpMethod.Post, $"{Root}/restorePoints/search", body, ct: ct);
        return (result?["searchResults"] as JsonArray ?? [])
            .Select(r => r?["restorePoint"]).OfType<JsonObject>()
            .Select(p => new M365RestorePoint(p["id"]!.ToString(), DateTimeOffset.Parse(p["protectionDateTime"]!.ToString()),
                DateTimeOffset.TryParse(p["expirationDateTime"]?.ToString(), out var exp) ? exp : null, p["tags"]?.ToString()))
            .OrderByDescending(p => p.ProtectionDateTime).ToList();
    }

    /// <summary>Starts a native restore of one protection unit to a restore point ("new" location or "inPlace").</summary>
    public async Task<M365Session> RestoreAsync(Tenant tenant, string unitId, string restorePointId, bool inPlace, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Restore).Graph;
        var unit = await graph.GetAsync($"{Root}/protectionUnits/{Uri.EscapeDataString(unitId)}", ct) ?? throw new KeyNotFoundException("Protection unit not found.");
        var kind = KindOf(unit["@odata.type"]?.ToString());
        var (collection, artifacts) = kind switch
        {
            "exchange" => ("exchangeRestoreSessions", "mailboxRestoreArtifacts"),
            "oneDrive" => ("oneDriveForBusinessRestoreSessions", "driveRestoreArtifacts"),
            "sharePoint" => ("sharePointRestoreSessions", "siteRestoreArtifacts"),
            _ => throw new InvalidOperationException($"Unsupported protection unit type {unit["@odata.type"]}."),
        };
        var session = await graph.SendAsync(HttpMethod.Post, $"{Root}/{collection}", new JsonObject
        {
            [artifacts] = new JsonArray(new JsonObject
            {
                ["restorePoint"] = new JsonObject { ["id"] = restorePointId },
                ["destinationType"] = inPlace ? "inPlace" : "new",
            }),
        }, ct: ct) ?? throw new InvalidOperationException("Creating the restore session returned nothing.");
        var id = session["id"]!.ToString();
        await graph.SendAsync(HttpMethod.Post, $"{Root}/restoreSessions/{Uri.EscapeDataString(id)}/activate", new JsonObject(), ct: ct);
        return (await SessionsAsync(tenant, ct)).FirstOrDefault(s => s.Id == id) ?? Session(session);
    }

    public async Task<List<M365Session>> SessionsAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = sessions.Create(tenant, AppRole.Backup).Graph;
        var list = new List<M365Session>();
        try
        {
            await foreach (var s in graph.ListAsync($"{Root}/restoreSessions", ct)) list.Add(Session(s));
        }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { }
        return list.OrderByDescending(s => s.CreatedAt).Take(50).ToList();
    }

    private static M365Session Session(JsonNode s) => new(s["id"]!.ToString(), KindOf(s["@odata.type"]?.ToString()), s["status"]?.ToString() ?? "unknown",
        DateTimeOffset.TryParse(s["createdDateTime"]?.ToString(), out var c) ? c : null,
        DateTimeOffset.TryParse(s["completedDateTime"]?.ToString(), out var d) ? d : null,
        s["createdBy"]?["user"]?["displayName"]?.ToString() ?? s["createdBy"]?["application"]?["displayName"]?.ToString(),
        s["error"]?["message"]?.ToString());

    /// <summary>"exchange", "oneDrive" or "sharePoint" from an @odata.type such as #microsoft.graph.mailboxProtectionUnit.</summary>
    public static string KindOf(string? odataType)
    {
        var t = (odataType ?? "").ToLowerInvariant();
        return t.Contains("mailbox") || t.Contains("exchange") ? "exchange"
            : t.Contains("drive") || t.Contains("onedrive") ? "oneDrive"
            : t.Contains("site") || t.Contains("sharepoint") ? "sharePoint" : "unknown";
    }
}
