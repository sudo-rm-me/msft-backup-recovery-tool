using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Core.Permissions;
using Mbr.Infrastructure.Api;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Tenants;

public record AppPermissionStatus(
    AppRole App, bool Configured, bool TokenAcquired, string? Error,
    IReadOnlyList<string> Granted, IReadOnlyList<PermissionGap> Missing);

public record PermissionGap(string Workload, string Permission, string Reason);

public record AzureAccessStatus(bool TokenAcquired, string? Error, IReadOnlyList<AzureSubscriptionInfo> Subscriptions);

public record AzureSubscriptionInfo(string SubscriptionId, string DisplayName, string State);

public record PermissionCheckResult(
    DateTimeOffset CheckedAt, AppPermissionStatus Backup, AppPermissionStatus Restore, AzureAccessStatus Azure)
{
    /// <summary>Workloads the backup app can fully back up.</summary>
    public IEnumerable<string> ReadyWorkloads =>
        PermissionCatalog.For(AppRole.Backup, PermissionCatalog.Graph).Select(p => p.Workload).Distinct()
            .Where(w => !Backup.Missing.Any(m => m.Workload == w));
}

public sealed class TenantOnboardingService(
    IHttpClientFactory http, ITokenProvider tokens, IApiClientFactory apis, ApiEndpoints endpoints,
    IOptions<MbrOptions> options, MbrDbContext db)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves a domain (contoso.onmicrosoft.com) or GUID to the Entra directory ID using OIDC discovery.</summary>
    public async Task<string> ResolveDirectoryIdAsync(string domainOrId, CancellationToken ct = default)
    {
        var input = domainOrId.Trim();
        using var client = http.CreateClient("mbr-anon");
        var url = $"{endpoints.Authority.TrimEnd('/')}/{Uri.EscapeDataString(input)}/v2.0/.well-known/openid-configuration";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ArgumentException($"'{input}' is not a known Microsoft Entra tenant.");
        var doc = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var issuer = doc?["issuer"]?.GetValue<string>() ?? throw new ArgumentException("Tenant discovery returned no issuer.");
        // issuer looks like https://login.microsoftonline.com/{tenantId}/v2.0
        var parts = new Uri(issuer).AbsolutePath.Trim('/').Split('/');
        if (parts.Length == 0 || !Guid.TryParse(parts[0], out var tid))
            throw new ArgumentException("Could not read the tenant ID from discovery.");
        return tid.ToString();
    }

    public async Task<Tenant> AddTenantAsync(string domainOrId, string? displayName, Guid? storageTargetId, CancellationToken ct = default)
    {
        var directoryId = await ResolveDirectoryIdAsync(domainOrId, ct);
        if (await db.Tenants.AnyAsync(t => t.DirectoryId == directoryId, ct))
            throw new InvalidOperationException("This tenant has already been added.");
        var tenant = new Tenant
        {
            DirectoryId = directoryId,
            DefaultDomain = Guid.TryParse(domainOrId, out _) ? "" : domainOrId.Trim().ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? domainOrId.Trim() : displayName.Trim(),
            DefaultStorageTargetId = storageTargetId,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant;
    }

    public string RedirectUri => $"{options.Value.PublicBaseUrl.TrimEnd('/')}/api/onboarding/consent-callback";

    /// <summary>Admin-consent URL a Global Admin of the customer tenant opens to grant the app's permissions.</summary>
    public string? GetConsentUrl(Tenant tenant, AppRole role, string state)
    {
        var clientId = tokens.GetClientId(role);
        if (string.IsNullOrEmpty(clientId)) return null;
        return $"{endpoints.Authority.TrimEnd('/')}/{tenant.DirectoryId}/adminconsent" +
               $"?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={Uri.EscapeDataString(state)}";
    }

    public async Task<PermissionCheckResult> CheckPermissionsAsync(Tenant tenant, CancellationToken ct = default)
    {
        var backup = await CheckAppAsync(tenant, AppRole.Backup, ct);
        var restore = await CheckAppAsync(tenant, AppRole.Restore, ct);
        var azure = await CheckAzureAsync(tenant, ct);
        var result = new PermissionCheckResult(DateTimeOffset.UtcNow, backup, restore, azure);

        var row = await db.Tenants.FirstAsync(t => t.Id == tenant.Id, ct);
        row.LastPermissionCheckAt = result.CheckedAt;
        row.LastPermissionCheckJson = JsonSerializer.Serialize(result, Json);
        if (backup.TokenAcquired && row.Status == TenantStatus.PendingConsent)
        {
            row.Status = TenantStatus.Active;
            row.BackupConsentedAt ??= DateTimeOffset.UtcNow;
        }
        if (restore.TokenAcquired) row.RestoreConsentedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<AppPermissionStatus> CheckAppAsync(Tenant tenant, AppRole role, CancellationToken ct)
    {
        var required = PermissionCatalog.For(role, PermissionCatalog.Graph).ToList();
        var allMissing = required.Select(p => new PermissionGap(p.Workload, p.Name, p.Reason)).ToList();
        if (!tokens.IsConfigured(role))
            return new AppPermissionStatus(role, false, false, $"The {role} app is not configured on this deployment.", [], allMissing);
        try
        {
            var token = await tokens.GetTokenAsync(tenant.DirectoryId, role, endpoints.ScopeFor(ApiKind.Graph), ct);
            var granted = ReadRoles(token);
            var missing = required.Where(p => !granted.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                                              && !ImpliedBy(p.Name, granted))
                .Select(p => new PermissionGap(p.Workload, p.Name, p.Reason)).ToList();
            return new AppPermissionStatus(role, true, true, null, granted, missing);
        }
        catch (Exception e)
        {
            return new AppPermissionStatus(role, true, false, Summarize(e), [], allMissing);
        }
    }

    /// <summary>A ReadWrite permission satisfies the matching Read permission.</summary>
    private static bool ImpliedBy(string permission, IReadOnlyList<string> granted) =>
        permission.Contains(".Read.") &&
        granted.Contains(permission.Replace(".Read.", ".ReadWrite."), StringComparer.OrdinalIgnoreCase);

    private async Task<AzureAccessStatus> CheckAzureAsync(Tenant tenant, CancellationToken ct)
    {
        if (!tokens.IsConfigured(AppRole.Backup)) return new AzureAccessStatus(false, "Backup app not configured.", []);
        try
        {
            var arm = apis.Create(tenant.DirectoryId, ApiKind.Arm, AppRole.Backup);
            var subs = new List<AzureSubscriptionInfo>();
            await foreach (var s in arm.ListAsync("/subscriptions?api-version=2022-12-01", ct))
                subs.Add(new AzureSubscriptionInfo(s["subscriptionId"]?.GetValue<string>() ?? "",
                    s["displayName"]?.GetValue<string>() ?? "", s["state"]?.GetValue<string>() ?? ""));
            return new AzureAccessStatus(true, subs.Count == 0 ? "No subscriptions visible. Assign the Reader role to the backup app's service principal." : null, subs);
        }
        catch (Exception e)
        {
            return new AzureAccessStatus(false, Summarize(e), []);
        }
    }

    public static IReadOnlyList<string> ReadRoles(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return [];
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return node?["roles"] is JsonArray roles ? roles.Select(r => r!.GetValue<string>()).ToList() : [];
    }

    private static string Summarize(Exception e)
    {
        var m = e.Message;
        if (m.Contains("AADSTS7000229") || m.Contains("AADSTS650051") || m.Contains("AADSTS700016"))
            return "The app has not been consented in this tenant yet. Open the admin-consent link.";
        return m.Length > 400 ? m[..400] : m;
    }
}
