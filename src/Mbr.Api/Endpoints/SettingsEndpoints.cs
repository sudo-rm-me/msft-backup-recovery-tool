using System.Text.Json.Nodes;
using Mbr.Api.Auth;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Settings;

namespace Mbr.Api.Endpoints;

public static partial class ApiRoutes
{
    // ---------------------------------------------------------------- deployment-wide settings changed in the app
    private static void MapAppSettings(RouteGroupBuilder api)
    {
        api.MapGet("/app-settings", async (RuntimeSettings settings, OperatorContext ops, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            return settings.Describe();
        });

        // { "values": { "<key>": <value>, "<key>": null } } — null puts the deployment's value back.
        api.MapPut("/app-settings", async (AppSettingsRequest req, RuntimeSettings settings, OperatorContext ops, AuditLog audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            await ops.EnsureGlobalAdminAsync(ct);
            var changed = await settings.SaveAsync(req.Values ?? [], ops.Name, ct);
            if (changed.Count > 0)
                await audit.WriteAsync(ops.Name, "settings.update", null, string.Join(", ", changed),
                    new { reset = req.Values!.Where(v => v.Value is null).Select(v => v.Key) }, Ip(ctx), ct);
            return settings.Describe();
        });
    }
}

public record AppSettingsRequest(Dictionary<string, JsonNode?>? Values);
