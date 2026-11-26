using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Core.Permissions;
using Mbr.Infrastructure;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Jobs;
using Mbr.Infrastructure.Security;
using Mbr.Infrastructure.Storage;
using Mbr.Infrastructure.Tenants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public static void MapMbrApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        MapAuth(app, api);
        MapTenants(app, api);
        MapStorage(api);
        MapAppSettings(api);
        MapJobs(api);
        MapSchedules(api);
        MapAdmin(api);
        MapFeatures(api);
    }

    static partial void MapFeatureRoutes(RouteGroupBuilder api);
    private static void MapFeatures(RouteGroupBuilder api) => MapFeatureRoutes(api);

    internal static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    // ---------------------------------------------------------------- auth / me / setup
    private static void MapAuth(WebApplication app, RouteGroupBuilder api)
    {
        app.MapGet("/api/auth/config", (IOptions<AuthSettings> auth) =>
        {
            var a = auth.Value;
            return a.IsDevelopment
                ? Results.Ok(new { mode = "Development" })
                : Results.Ok(new
                {
                    mode = "Entra",
                    clientId = a.Entra.SpaClientId,
                    authority = $"{a.Entra.Instance.TrimEnd('/')}/{a.Entra.TenantId}",
                    scopes = new[] { a.Entra.Scope },
                });
        }).AllowAnonymous();

        api.MapGet("/me", async (OperatorContext ops, CancellationToken ct) =>
        {
            var op = await ops.GetAsync(ct);
            return new { op.Id, op.DisplayName, op.UserPrincipalName, op.IsGlobalAdmin, roles = op.Roles.Select(r => new { r.TenantId, r.Role }) };
        });

        api.MapGet("/setup", (ITokenProvider tokens, IServiceProvider sp) =>
        {
            using var scope = sp.CreateScope();
            var onboarding = scope.ServiceProvider.GetRequiredService<TenantOnboardingService>();
            return new
            {
                backupApp = new { configured = tokens.IsConfigured(AppRole.Backup), clientId = tokens.GetClientId(AppRole.Backup) },
                restoreApp = new { configured = tokens.IsConfigured(AppRole.Restore), clientId = tokens.GetClientId(AppRole.Restore) },
                consentRedirectUri = onboarding.RedirectUri,
                permissions = PermissionCatalog.All,
            };
        });
    }

    // ---------------------------------------------------------------- tenants + onboarding
    public record AddTenantRequest(string Domain, string? DisplayName, Guid? StorageTargetId);
    public record UpdateTenantRequest(string? DisplayName, Guid? StorageTargetId, TenantStatus? Status, string? Notes);

    internal static object TenantDto(Tenant t) => new
    {
        t.Id, t.DirectoryId, t.DefaultDomain, t.DisplayName, t.Status, t.CreatedAt, t.BackupConsentedAt, t.RestoreConsentedAt,
        t.LastPermissionCheckAt, t.DefaultStorageTargetId, t.Notes,
        lastPermissionCheck = t.LastPermissionCheckJson is null ? null : JsonNode.Parse(t.LastPermissionCheckJson),
    };

    internal static async Task<Tenant> LoadTenantAsync(MbrDbContext db, OperatorContext ops, Guid id, TenantRole role, CancellationToken ct)
    {
        await ops.EnsureTenantRoleAsync(id, role, ct);
        return await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("Tenant not found.");
    }

    private static void MapTenants(WebApplication app, RouteGroupBuilder api)
    {
        api.MapGet("/tenants", async (MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
        {
            var visible = await ops.VisibleTenantIdsAsync(ct);
            var list = await db.Tenants.AsNoTracking().OrderBy(t => t.DisplayName).ToListAsync(ct);
            return list.Where(t => visible is null || visible.Contains(t.Id)).Select(TenantDto);
        });

        api.MapPost("/tenants", async (AddTenantRequest req, TenantOnboardingService svc, OperatorContext ops, AuditLog audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            if (string.IsNullOrWhiteSpace(req.Domain)) throw new ArgumentException("Domain or tenant ID is required.");
            var tenant = await svc.AddTenantAsync(req.Domain, req.DisplayName, req.StorageTargetId, ct);
            await audit.WriteAsync(ops.Name, "tenant.add", tenant.Id, tenant.DisplayName, new { tenant.DirectoryId }, Ip(ctx), ct);
            return Results.Created($"/api/tenants/{tenant.Id}", TenantDto(tenant));
        });

        api.MapGet("/tenants/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, CancellationToken ct) =>
            TenantDto(await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct)));

        api.MapPatch("/tenants/{id:guid}", async (Guid id, UpdateTenantRequest req, MbrDbContext db, OperatorContext ops,
            AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            var t = await LoadTenantAsync(db, ops, id, TenantRole.Admin, ct);
            if (req.DisplayName is { Length: > 0 }) t.DisplayName = req.DisplayName;
            if (req.StorageTargetId is { } st)
            {
                if (!await db.StorageTargets.AnyAsync(s => s.Id == st, ct)) throw new ArgumentException("Unknown storage target.");
                t.DefaultStorageTargetId = st;
            }
            if (req.Status is { } s) t.Status = s;
            if (req.Notes != null) t.Notes = req.Notes;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "tenant.update", id, t.DisplayName, req, Ip(ctx), ct);
            return TenantDto(t);
        });

        api.MapDelete("/tenants/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Tenant not found.");
            // Backups are kept in storage; the tenant is disabled rather than deleted so its data stays recoverable.
            t.Status = TenantStatus.Disabled;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "tenant.disable", id, t.DisplayName, null, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapGet("/tenants/{id:guid}/consent-urls", async (Guid id, MbrDbContext db, OperatorContext ops,
            TenantOnboardingService svc, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var t = await LoadTenantAsync(db, ops, id, TenantRole.Admin, ct);
            var protector = dp.CreateProtector("consent").ToTimeLimitedDataProtector();
            string State(AppRole r) => protector.Protect($"{t.Id}|{r}", TimeSpan.FromHours(24));
            return new
            {
                backup = svc.GetConsentUrl(t, AppRole.Backup, State(AppRole.Backup)),
                restore = svc.GetConsentUrl(t, AppRole.Restore, State(AppRole.Restore)),
                redirectUri = svc.RedirectUri,
            };
        });

        // Microsoft redirects the consenting admin's browser here, so this endpoint is anonymous; the state is signed.
        app.MapGet("/api/onboarding/consent-callback", async (HttpContext ctx, MbrDbContext db, JobQueue queue,
            AuditLog audit, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var q = ctx.Request.Query;
            string payload;
            try { payload = dp.CreateProtector("consent").ToTimeLimitedDataProtector().Unprotect(q["state"].ToString()); }
            catch { return Results.BadRequest("Invalid or expired consent state."); }
            var parts = payload.Split('|');
            var tenantId = Guid.Parse(parts[0]);
            var role = Enum.Parse<AppRole>(parts[1]);
            var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId, ct);
            if (t is null) return Results.NotFound();

            if (!string.Equals(q["admin_consent"], "True", StringComparison.OrdinalIgnoreCase))
            {
                await audit.WriteAsync("consent-callback", "tenant.consent.failed", t.Id, role.ToString(),
                    new { error = q["error"].ToString(), description = q["error_description"].ToString() }, Ip(ctx), ct);
                return Results.Redirect($"/tenants/{t.Id}?consent=failed&app={role}&error={Uri.EscapeDataString(q["error"].ToString())}");
            }
            if (!string.Equals(q["tenant"], t.DirectoryId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Consent was granted in a different tenant than expected.");

            if (role == AppRole.Backup) t.BackupConsentedAt = DateTimeOffset.UtcNow; else t.RestoreConsentedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("consent-callback", "tenant.consent.granted", t.Id, role.ToString(), null, Ip(ctx), ct);
            // Role assignments and consent take a little while to propagate; check after a short delay.
            var job = await queue.EnqueueAsync("tenant.permissionCheck", t.Id, null, "consent-callback", ct: ct);
            job.NotBefore = DateTimeOffset.UtcNow.AddSeconds(20);
            await db.SaveChangesAsync(ct);
            return Results.Redirect($"/tenants/{t.Id}?consent=ok&app={role}");
        }).AllowAnonymous();

        api.MapPost("/tenants/{id:guid}/permission-check", async (Guid id, MbrDbContext db, OperatorContext ops,
            TenantOnboardingService svc, CancellationToken ct) =>
        {
            var t = await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            return await svc.CheckPermissionsAsync(t, ct);
        });
    }

    // ---------------------------------------------------------------- storage targets
    public record StorageTargetRequest(string Name, StorageKind Kind, JsonObject? Settings, string? Secret, bool? IsDefault);

    private static object StorageDto(StorageTarget s) => new
    {
        s.Id, s.Name, s.Kind, settings = JsonNode.Parse(s.SettingsJson), hasSecret = s.EncryptedSecret != null, s.IsDefault, s.CreatedAt,
    };

    private static string ValidateSettings(StorageKind kind, JsonObject? settings)
    {
        var json = (settings ?? new JsonObject()).ToJsonString();
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        switch (kind)
        {
            case StorageKind.Local:
                if (Mbr.Infrastructure.Hosting.AzureHosting.InContainerApps)
                    throw new ArgumentException("Local disk targets aren't available in Azure Container Apps: the container's disk is lost on restart. Use S3.");
                return StorageSettings.Serialize(JsonSerializer.Deserialize<LocalStorageSettings>(json, o)!);
            case StorageKind.S3:
                var s3 = JsonSerializer.Deserialize<S3StorageSettings>(json, o)!;
                if (string.IsNullOrWhiteSpace(s3.Bucket)) throw new ArgumentException("S3 bucket is required.");
                if (s3.Credentials is not ("AzureFederation" or "Default" or "Static" or "Profile"))
                    throw new ArgumentException("S3 credentials must be AzureFederation, Default, Static or Profile.");
                if (s3.Credentials == "AzureFederation" && (string.IsNullOrWhiteSpace(s3.RoleArn) || string.IsNullOrWhiteSpace(s3.WebIdentityAudience)))
                    throw new ArgumentException("AzureFederation credentials need the IAM role ARN and the web identity audience.");
                if (s3.ObjectLockMode.ToUpperInvariant() is not ("GOVERNANCE" or "COMPLIANCE")) throw new ArgumentException("Object lock mode must be GOVERNANCE or COMPLIANCE.");
                return StorageSettings.Serialize(s3);
            default:
                throw new ArgumentException("Unknown storage kind.");
        }
    }

    private static void MapStorage(RouteGroupBuilder api)
    {
        api.MapGet("/storage-targets", async (MbrDbContext db, CancellationToken ct) =>
            (await db.StorageTargets.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Select(StorageDto));

        api.MapPost("/storage-targets", async (StorageTargetRequest req, MbrDbContext db, OperatorContext ops, SecretProtector secrets,
            AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name is required.");
            var target = new StorageTarget
            {
                Name = req.Name.Trim(), Kind = req.Kind, SettingsJson = ValidateSettings(req.Kind, req.Settings),
                EncryptedSecret = string.IsNullOrEmpty(req.Secret) ? null : await secrets.ProtectAsync(req.Secret, ct),
                IsDefault = req.IsDefault ?? false,
            };
            if (target.IsDefault) await db.StorageTargets.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct);
            db.StorageTargets.Add(target);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "storage.add", null, target.Name, new { target.Kind }, Ip(ctx), ct);
            return Results.Created($"/api/storage-targets/{target.Id}", StorageDto(target));
        });

        api.MapPut("/storage-targets/{id:guid}", async (Guid id, StorageTargetRequest req, MbrDbContext db, OperatorContext ops,
            SecretProtector secrets, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var t = await db.StorageTargets.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
            if (req.Kind != t.Kind) throw new ArgumentException("The kind of an existing storage target can't change.");
            t.Name = req.Name.Trim();
            t.SettingsJson = ValidateSettings(req.Kind, req.Settings);
            if (req.Secret != null) t.EncryptedSecret = req.Secret.Length == 0 ? null : await secrets.ProtectAsync(req.Secret, ct);
            if (req.IsDefault == true && !t.IsDefault)
            {
                await db.StorageTargets.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false), ct);
                t.IsDefault = true;
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "storage.update", null, t.Name, null, Ip(ctx), ct);
            return StorageDto(t);
        });

        api.MapDelete("/storage-targets/{id:guid}", async (Guid id, MbrDbContext db, OperatorContext ops, AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var t = await db.StorageTargets.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
            if (await db.StoredObjects.AnyAsync(o => o.StorageTargetId == id, ct) || await db.Tenants.AnyAsync(x => x.DefaultStorageTargetId == id, ct))
                throw new InvalidOperationException("This storage target holds backups or is a tenant's default; it can't be removed.");
            db.StorageTargets.Remove(t);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(ops.Name, "storage.delete", null, t.Name, null, Ip(ctx), ct);
            return Results.NoContent();
        });

        api.MapPost("/storage-targets/{id:guid}/test", async (Guid id, MbrDbContext db, StorageTargetResolver resolver, CancellationToken ct) =>
        {
            var t = await db.StorageTargets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
            var steps = new List<object>();
            var key = $"_health/{Guid.NewGuid():N}";
            var ok = true;
            async Task Step(string name, Func<Task> f, bool required = true)
            {
                if (!ok) return;
                try { await f(); steps.Add(new { name, ok = true }); }
                catch (Exception e)
                {
                    steps.Add(new { name, ok = false, error = e.Message });
                    if (required) ok = false;
                }
            }
            IBlobStore? store = null;
            await Step("connect", () => { store = resolver.Get(t); return Task.CompletedTask; });
            await Step("write", () => store!.PutAsync(key, new MemoryStream("ok"u8.ToArray()), ct));
            await Step("read", async () =>
            {
                await using var s = await store!.GetAsync(key, ct) ?? throw new IOException("Object written but not readable.");
            });
            // Delete may be blocked by object lock/immutability, which is expected and fine.
            await Step("delete", () => store!.DeleteAsync(key, ct), required: false);
            return new { ok, steps };
        });
    }
}
