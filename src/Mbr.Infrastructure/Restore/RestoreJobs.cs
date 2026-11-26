using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Core.Restore;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Restore;

public sealed class RestorePlanJob : IJobHandler
{
    public string Type => "restore.plan";
    public TenantRole RequiredRole => TenantRole.RestoreOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<MbrDbContext>();
        var engine = context.Services.GetRequiredService<RestoreEngine>();
        var planId = context.Parameters.GetGuid("planId") ?? throw new ArgumentException("planId is required.");
        var plan = await db.RestorePlans.FirstAsync(p => p.Id == planId, ct);
        var req = JsonSerializer.Deserialize<RestoreRequest>(plan.RequestJson, RestoreEngine.Json)!;
        try
        {
            await engine.BuildPlanAsync(plan, req, context, ct);
            plan.Status = RestorePlanStatus.Draft;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            plan.Status = RestorePlanStatus.Failed;
            plan.Error = e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        await context.InfoAsync($"Plan ready: {plan.CreateCount} to create, {plan.UpdateCount} to update, {plan.RecycleBinCount} from the recycle bin, {plan.DeleteCount} to delete, {plan.SkipCount} unchanged/skipped");
        return JobOutcome.Success(new JsonObject
        {
            ["planId"] = plan.Id.ToString(), ["create"] = plan.CreateCount, ["update"] = plan.UpdateCount,
            ["recycleBin"] = plan.RecycleBinCount, ["delete"] = plan.DeleteCount, ["skip"] = plan.SkipCount,
        });
    }
}

public sealed class RestoreApplyJob : IJobHandler
{
    public string Type => "restore.apply";
    public TenantRole RequiredRole => TenantRole.RestoreOperator;

    public async Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<MbrDbContext>();
        var engine = context.Services.GetRequiredService<RestoreEngine>();
        var planId = context.Parameters.GetGuid("planId") ?? throw new ArgumentException("planId is required.");
        var plan = await db.RestorePlans.FirstAsync(p => p.Id == planId, ct);
        if (plan.Status is not (RestorePlanStatus.Applying))
            throw new InvalidOperationException($"Plan is {plan.Status}; it must be approved/started through the API.");
        try
        {
            await engine.ApplyAsync(plan, context, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            plan.Status = RestorePlanStatus.Failed;
            plan.Error = e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        var audit = context.Services.GetRequiredService<AuditLog>();
        await audit.WriteAsync(context.Job.CreatedBy ?? "system", "restore.applied", plan.TenantId, plan.Id.ToString(),
            new { plan.Status, plan.SucceededCount, plan.FailedCount }, ct: ct);
        if (plan.FailedCount > 0)
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == plan.TenantId, ct);
            await context.Services.GetRequiredService<INotifier>().NotifyAsync(new Notification(
                "Restore finished with errors", $"{plan.FailedCount} of {plan.SucceededCount + plan.FailedCount} restore actions failed in {tenant.DisplayName}.",
                plan.TenantId, context.Job.Id), ct);
        }
        var result = new JsonObject { ["planId"] = plan.Id.ToString(), ["succeeded"] = plan.SucceededCount, ["failed"] = plan.FailedCount, ["status"] = plan.Status.ToString() };
        return plan.FailedCount == 0 ? JobOutcome.Success(result) : JobOutcome.Partial(result);
    }
}
