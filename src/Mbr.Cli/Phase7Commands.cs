using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase7Plus()
    {
        yield return Azure();
        foreach (var c in Phase8Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase8Plus();

    private static Command Azure()
    {
        var t = TenantOption();
        var deleted = new Option<bool>("--deleted") { Description = "Only resource groups that no longer exist" };
        var groups = new Argument<string[]>("groups") { Description = "Resource group names or IDs", Arity = ArgumentArity.OneOrMore };
        var subscription = new Option<string?>("--subscription") { Description = "Subscription name or ID (when a name exists in several)" };
        var apply = new Option<bool>("--apply") { Description = "Apply right after planning" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };

        return Group("azure", "Azure resource groups, RBAC and Policy",
            Cmd("rgs", "List resource groups in the latest backup and whether they still exist", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var inv = (await c.GetAsync($"api/tenants/{tid}/azure/resourceGroups", ct))!;
                if (inv["liveError"] is { } err) Console.Error.WriteLine($"Couldn't check which groups still exist: {err}");
                var rows = new JsonArray(inv["resourceGroups"]!.AsArray()
                    .Where(r => !pr.GetValue(deleted) || r!["status"]!.GetValue<string>() == "deleted").Select(r => r!.DeepClone()).ToArray());
                Print(pr, rows, ("Name", "name"), ("Subscription", "subscriptionName"), ("Location", "location"), ("Resources", "resourceCount"),
                    ("Now", "status"), ("Item", "snapshotItemId"));
            }, t, deleted),

            Cmd("redeploy", "Plan recreating resource groups (and their resources, role and policy assignments) from the latest backup", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var inv = (await c.GetAsync($"api/tenants/{tid}/azure/resourceGroups", ct))!;
                if (inv["snapshotId"] is null) throw new CliException("No backup includes Azure resource groups yet.");
                var all = inv["resourceGroups"]!.AsArray().Select(r => r!).ToList();
                var sub = pr.GetValue(subscription);
                var itemIds = new List<long>();
                foreach (var g in pr.GetValue(groups)!)
                {
                    var matches = all.Where(r => r["id"]!.GetValue<string>().Equals(g, StringComparison.OrdinalIgnoreCase) ||
                                                 r["name"]!.GetValue<string>().Equals(g, StringComparison.OrdinalIgnoreCase))
                        .Where(r => sub is null || r["subscriptionId"]!.GetValue<string>().Equals(sub, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(r["subscriptionName"]?.GetValue<string>(), sub, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) throw new CliException($"'{g}' isn't in the latest backup.");
                    if (matches.Count > 1) throw new CliException($"'{g}' is in several subscriptions; pass --subscription.");
                    itemIds.Add(matches[0]["snapshotItemId"]!.GetValue<long>());
                }
                var r = (await c.PostAsync("api/restore/plans", new { snapshotId = inv["snapshotId"]!.GetValue<string>(), itemIds }, ct))!;
                var planId = r["planId"]!.GetValue<string>();
                var detail = await WaitForPlanAsync(c, planId, r["jobId"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(detail); else PrintPlan(detail, false);
                if (pr.GetValue(apply)) await ApplyPlanAsync(c, planId, pr.GetValue(yes), WantsJson(pr), ct);
                else if (!WantsJson(pr)) Console.WriteLine($"\nReview, then run: mbr restore apply {planId}");
            }, t, groups, subscription, apply, yes));
    }
}
