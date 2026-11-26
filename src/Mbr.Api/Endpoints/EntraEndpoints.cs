using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Core.Model;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Jobs;
using Mbr.Infrastructure.Restore;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    public record UndeleteRequest(List<string> Ids);

    static partial void MapFeatureRoutesPhase4Plus(RouteGroupBuilder api)
    {
        MapEntraRecycleBin(api);
        MapFeatureRoutesPhase5Plus(api);
    }

    static partial void MapFeatureRoutesPhase5Plus(RouteGroupBuilder api);

    private static void MapEntraRecycleBin(RouteGroupBuilder api)
    {
        api.MapGet("/tenants/{id:guid}/entra/deleted", async (Guid id, string? kind, MbrDbContext db, OperatorContext ops, EntraRecycleBin bin, CancellationToken ct) =>
        {
            var tenant = await LoadTenantAsync(db, ops, id, TenantRole.Viewer, ct);
            return await bin.ListAsync(tenant, kind?.Split(',', StringSplitOptions.RemoveEmptyEntries), ct);
        });

        api.MapPost("/tenants/{id:guid}/entra/deleted/restore", async (Guid id, UndeleteRequest req, MbrDbContext db, OperatorContext ops, JobQueue queue,
            AuditLog audit, HttpContext ctx, CancellationToken ct) =>
        {
            await LoadTenantAsync(db, ops, id, TenantRole.RestoreOperator, ct);
            if (req.Ids is not { Count: > 0 }) throw new ArgumentException("Select at least one object.");
            var job = await queue.EnqueueAsync("entra.undelete", id, new JsonObject { ["ids"] = new JsonArray(req.Ids.Select(i => (JsonNode)i).ToArray()) },
                ops.Name, maxAttempts: 1, ct: ct);
            await audit.WriteAsync(ops.Name, "entra.undeleteRequested", id, job.Id.ToString(), new { req.Ids }, Ip(ctx), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
        });
    }
}
