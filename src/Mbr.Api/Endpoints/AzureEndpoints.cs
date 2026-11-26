using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.AzureConfig;
using Mbr.Infrastructure.Data;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    static partial void MapFeatureRoutesPhase6Plus(RouteGroupBuilder api)
    {
        // Resource groups in the latest backup and whether each still exists (redeploying uses the normal restore plan flow).
        api.MapGet("/tenants/{id:guid}/azure/resourceGroups", async (Guid id, MbrDbContext db, OperatorContext ops, AzureInventory inventory, CancellationToken ct) =>
        {
            var tenant = await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            return await inventory.ResourceGroupsAsync(tenant, ct);
        });
        MapFeatureRoutesPhase7Plus(api);
    }

    static partial void MapFeatureRoutesPhase7Plus(RouteGroupBuilder api);
}
