using System.Security.Claims;
using System.Text.Encodings.Web;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mbr.Api.Auth;

public class ForbiddenException(string message) : Exception(message);

public class AuthSettings
{
    /// <summary>"Development" (no sign-in, local only) or "Entra".</summary>
    public string Mode { get; set; } = "Development";
    public EntraAuthSettings Entra { get; set; } = new();
    /// <summary>UPNs or object IDs that become global admins on first sign-in.</summary>
    public List<string> BootstrapAdmins { get; set; } = new();
    public bool IsDevelopment => Mode.Equals("Development", StringComparison.OrdinalIgnoreCase);
}

public class EntraAuthSettings
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string? TenantId { get; set; }
    /// <summary>Client ID of the API app registration.</summary>
    public string? ClientId { get; set; }
    public string? Audience { get; set; }
    /// <summary>Client ID of the SPA/CLI public client registration.</summary>
    public string? SpaClientId { get; set; }
    /// <summary>Delegated scope the SPA/CLI requests, e.g. api://{clientId}/access_as_user.</summary>
    public string? Scope { get; set; }
}

/// <summary>Signs every request in as a local developer. Refused outside the Development environment.</summary>
public class DevelopmentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("oid", "00000000-0000-0000-0000-00000000dev0"),
            new Claim("preferred_username", "developer@localhost"),
            new Claim("name", "Local developer"),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Resolves the signed-in operator (auto-provisioned on first sign-in) and checks per-tenant roles.</summary>
public class OperatorContext(IHttpContextAccessor http, MbrDbContext db, IOptions<AuthSettings> auth)
{
    private Operator? _operator;

    public string ObjectId => Claim("oid") ?? Claim("http://schemas.microsoft.com/identity/claims/objectidentifier")
        ?? throw new ForbiddenException("No object ID in token.");

    public string Name => Claim("preferred_username") ?? Claim("upn") ?? Claim("name") ?? ObjectId;

    private string? Claim(string type) => http.HttpContext?.User.FindFirst(type)?.Value;

    public async Task<Operator> GetAsync(CancellationToken ct = default)
    {
        if (_operator != null) return _operator;
        var oid = ObjectId;
        var op = await db.Operators.Include(o => o.Roles).FirstOrDefaultAsync(o => o.ObjectId == oid, ct);
        if (op is null)
        {
            var upn = Name;
            var bootstrap = auth.Value.IsDevelopment ||
                            auth.Value.BootstrapAdmins.Any(b => b.Equals(upn, StringComparison.OrdinalIgnoreCase) || b.Equals(oid, StringComparison.OrdinalIgnoreCase));
            op = new Operator
            {
                ObjectId = oid, UserPrincipalName = upn, DisplayName = Claim("name") ?? upn, IsGlobalAdmin = bootstrap,
            };
            db.Operators.Add(op);
        }
        if (op.LastSeenAt is null || op.LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(-5)) op.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return _operator = op;
    }

    public async Task<TenantRole?> GetRoleAsync(Guid tenantId, CancellationToken ct = default)
    {
        var op = await GetAsync(ct);
        if (op.IsGlobalAdmin) return TenantRole.Admin;
        var roles = op.Roles.Where(r => r.TenantId == null || r.TenantId == tenantId).Select(r => r.Role).ToList();
        return roles.Count == 0 ? null : roles.Max();
    }

    public async Task EnsureTenantRoleAsync(Guid tenantId, TenantRole required, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(tenantId, ct);
        if (role is null || role < required)
            throw new ForbiddenException($"This action needs the {required} role on this tenant.");
    }

    public async Task EnsureGlobalAdminAsync(CancellationToken ct = default)
    {
        if (!(await GetAsync(ct)).IsGlobalAdmin) throw new ForbiddenException("This action needs a global admin.");
    }

    /// <summary>Tenant IDs the operator can see; null means all tenants.</summary>
    public async Task<HashSet<Guid>?> VisibleTenantIdsAsync(CancellationToken ct = default)
    {
        var op = await GetAsync(ct);
        if (op.IsGlobalAdmin || op.Roles.Any(r => r.TenantId == null)) return null;
        return op.Roles.Where(r => r.TenantId != null).Select(r => r.TenantId!.Value).ToHashSet();
    }
}
