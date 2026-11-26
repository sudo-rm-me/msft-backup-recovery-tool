using System.Net;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Tenants;

public record GdapRelationship(string Id, string Name, string Status, DateTimeOffset? EndsAt, bool AutoExtends, IReadOnlyList<string> Roles);

public record PartnerCustomer(string TenantId, string DisplayName, IReadOnlyList<GdapRelationship> Relationships, bool Active,
    bool CanGrantConsent, DateTimeOffset? EarliestExpiry, Guid? OnboardedTenantId);

/// <summary>
/// GDAP (granular delegated admin privileges): lists the partner's customers from its delegated admin relationships so they
/// can be onboarded in bulk. Backups still use the apps' own consent in each customer tenant; a GDAP relationship with a
/// role that can grant admin consent lets the partner's admins give that consent without a customer Global Admin.
/// </summary>
public sealed class PartnerCustomers(IApiClientFactory apis, IOptions<MbrOptions> options, MbrDbContext db)
{
    /// <summary>Entra built-in role template IDs commonly used in GDAP relationships.</summary>
    public static readonly IReadOnlyDictionary<string, string> RoleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator",
        ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["f2ef992c-3afb-46b9-b7cf-a126ee74c451"] = "Global Reader",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Exchange Administrator",
        ["3a2c62db-5318-420d-8d74-23affee5d9d5"] = "Intune Administrator",
        ["69091246-20e8-4a56-aa4d-066075b2a7a8"] = "Teams Administrator",
        ["17315797-102d-40b4-93e0-432062caca18"] = "Compliance Administrator",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["5d6b6bb7-de71-4623-b4af-96380a352509"] = "Security Reader",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "SharePoint Administrator",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["fdd7a751-b60b-444a-984c-02652fe8fa1c"] = "Groups Administrator",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Helpdesk Administrator",
        ["b0f54661-2d74-4c50-afa3-1ec803f12efe"] = "Billing Administrator",
    };

    /// <summary>Roles that can consent to application permissions on Microsoft Graph.</summary>
    private static readonly HashSet<string> ConsentRoles = new(StringComparer.OrdinalIgnoreCase)
        { "62e90394-69f5-4237-9190-012177145e10", "e8611ab8-c189-46e8-94e1-60213ab1f814" };

    private string PartnerTenantId => options.Value.Partner.TenantId is { Length: > 0 } id ? id
        : throw new InvalidOperationException("Set Mbr:Partner:TenantId to your own (partner) tenant ID to read GDAP relationships.");

    public async Task<List<PartnerCustomer>> ListAsync(CancellationToken ct)
    {
        var graph = apis.Create(PartnerTenantId, ApiKind.Graph, AppRole.Backup);
        var raw = new List<JsonObject>();
        try
        {
            await foreach (var r in graph.ListAsync("/v1.0/tenantRelationships/delegatedAdminRelationships", ct)) raw.Add(r);
        }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("The backup app can't read GDAP relationships: grant it DelegatedAdminRelationship.Read.All in your partner tenant.", e);
        }

        var onboarded = await db.Tenants.AsNoTracking().Select(t => new { t.Id, t.DirectoryId }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return raw
            .Where(r => r["customer"]?["tenantId"]?.ToString() is { Length: > 0 })
            .GroupBy(r => r["customer"]!["tenantId"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rels = g.Select(r =>
                {
                    var roles = (r["accessDetails"]?["unifiedRoles"] as JsonArray ?? [])
                        .Select(x => x?["roleDefinitionId"]?.ToString()).OfType<string>().ToList();
                    return (Rel: new GdapRelationship(r["id"]!.ToString(), r["displayName"]?.ToString() ?? "", r["status"]?.ToString() ?? "unknown",
                        DateTimeOffset.TryParse(r["endDateTime"]?.ToString(), out var end) ? end : null,
                        r["autoExtendDuration"]?.ToString() is { } ext && ext != "PT0S",
                        roles.Select(id => RoleNames.GetValueOrDefault(id, id)).Order().ToList()), RoleIds: roles);
                }).OrderBy(x => x.Rel.Status != "active").ThenBy(x => x.Rel.Name).ToList();
                var active = rels.Where(x => x.Rel.Status == "active" && (x.Rel.EndsAt is null || x.Rel.EndsAt > now)).ToList();
                return new PartnerCustomer(g.Key, g.First()["customer"]?["displayName"]?.ToString() ?? g.Key, rels.Select(x => x.Rel).ToList(),
                    active.Count > 0, active.Any(x => x.RoleIds.Any(ConsentRoles.Contains)),
                    active.Select(x => x.Rel.EndsAt).Where(e => e != null).Min(),
                    onboarded.FirstOrDefault(t => t.DirectoryId.Equals(g.Key, StringComparison.OrdinalIgnoreCase))?.Id);
            })
            .OrderBy(c => c.OnboardedTenantId != null).ThenBy(c => c.DisplayName).ToList();
    }

    /// <summary>Adds GDAP customers as tenants (pending consent). Customers already added are skipped.</summary>
    public async Task<List<Tenant>> ImportAsync(IReadOnlyCollection<string> tenantIds, Guid? storageTargetId, CancellationToken ct)
    {
        var customers = await ListAsync(ct);
        var graph = apis.Create(PartnerTenantId, ApiKind.Graph, AppRole.Backup);
        var added = new List<Tenant>();
        foreach (var id in tenantIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var c = customers.FirstOrDefault(x => x.TenantId.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"{id} isn't a customer in any of your GDAP relationships.");
            if (c.OnboardedTenantId != null || added.Any(t => t.DirectoryId == c.TenantId)) continue;
            string? domain = null, name = null;
            try
            {
                var info = await graph.GetAsync($"/v1.0/tenantRelationships/findTenantInformationByTenantId(tenantId='{Uri.EscapeDataString(c.TenantId)}')", ct);
                domain = info?["defaultDomainName"]?.ToString();
                name = info?["displayName"]?.ToString();
            }
            catch (ApiException) { /* the relationship's customer name is enough */ }
            var tenant = new Tenant
            {
                DirectoryId = c.TenantId, DisplayName = name ?? c.DisplayName, DefaultDomain = domain ?? "", DefaultStorageTargetId = storageTargetId,
                Notes = $"Imported from GDAP relationship '{c.Relationships.FirstOrDefault()?.Name}'.",
            };
            db.Tenants.Add(tenant);
            added.Add(tenant);
        }
        await db.SaveChangesAsync(ct);
        return added;
    }
}
