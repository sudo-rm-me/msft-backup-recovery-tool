using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Jobs;

/// <summary>A no-op job used to verify the worker end to end ("steps" parameter, default 5).</summary>
public sealed class DiagnosticsJob : IJobHandler
{
    public string Type => "system.diagnostics";
    public TenantRole RequiredRole => TenantRole.Viewer;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var steps = context.Parameters["steps"]?.GetValue<int>() ?? 5;
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            await context.InfoAsync($"Step {i} of {steps}");
            await context.ReportProgressAsync((double)i / steps, $"Step {i} of {steps}");
            await Task.Delay(TimeSpan.FromMilliseconds(context.Parameters["delayMs"]?.GetValue<int>() ?? 1000), ct);
        }
        return JobOutcome.Success(new JsonObject { ["steps"] = steps, ["host"] = Environment.MachineName });
    }
}

public sealed class PermissionCheckJob : IJobHandler
{
    public string Type => "tenant.permissionCheck";
    public TenantRole RequiredRole => TenantRole.Viewer;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var svc = context.Services.GetRequiredService<TenantOnboardingService>();
        var result = await svc.CheckPermissionsAsync(tenant, ct);
        foreach (var app in new[] { result.Backup, result.Restore })
            await context.InfoAsync(app.TokenAcquired
                ? $"{app.App} app: {app.Granted.Count} permissions granted, {app.Missing.Count} missing"
                : $"{app.App} app: {app.Error}");
        var node = JsonSerializer.SerializeToNode(result, TenantOnboardingService.Json);
        return result.Backup.TokenAcquired && result.Backup.Missing.Count == 0 ? JobOutcome.Success(node) : JobOutcome.Partial(node);
    }
}
