using Mbr.Api.Auth;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Tenants;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record GdapImportRequest(List<string> TenantIds, Guid? StorageTargetId);

    static partial void MapFeatureRoutesPhase8Plus(RouteGroupBuilder api)
    {
        api.MapGet("/partner/customers", async (OperatorContext ops, PartnerCustomers partner, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            return await partner.ListAsync(ct);
        });

        api.MapPost("/partner/customers/import", async (GdapImportRequest req, OperatorContext ops, PartnerCustomers partner, AuditLog audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            if (req.TenantIds is not { Count: > 0 }) throw new ArgumentException("Choose at least one customer.");
            var added = await partner.ImportAsync(req.TenantIds, req.StorageTargetId, ct);
            foreach (var t in added)
                await audit.WriteAsync(ops.Name, "tenant.add", t.Id, t.DisplayName, new { t.DirectoryId, source = "gdap" }, Ip(ctx), ct);
            return Results.Ok(added.Select(TenantDto));
        });

        MapFeatureRoutesPhase9Plus(api);
    }

    static partial void MapFeatureRoutesPhase9Plus(RouteGroupBuilder api);
}
