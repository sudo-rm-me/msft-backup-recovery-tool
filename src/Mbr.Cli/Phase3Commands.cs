using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase3Plus()
    {
        yield return Restore();
        foreach (var c in Phase4Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase4Plus();

    private static async Task<string> ResolveSnapshotIdAsync(MbrClient c, string tenantId, string reference, CancellationToken ct)
    {
        if (Guid.TryParse(reference, out _)) return reference;
        var list = (await c.GetAsync($"api/tenants/{tenantId}/snapshots", ct))!.AsArray()
            .Where(s => s!["status"]!.GetValue<string>() is "Completed" or "Partial").ToList();
        var pick = reference.ToLowerInvariant() switch
        {
            "latest" => list.FirstOrDefault(),
            "previous" => list.Skip(1).FirstOrDefault(),
            "baseline" => list.FirstOrDefault(s => s!["isBaseline"]!.GetValue<bool>()),
            _ => throw new CliException($"Unknown snapshot '{reference}'. Use an ID, latest, previous or baseline."),
        };
        return pick?["id"]!.GetValue<string>() ?? throw new CliException($"No {reference} snapshot found.");
    }

    private static void PrintPlan(JsonNode detail, bool all)
    {
        var p = detail["plan"]!;
        Console.WriteLine($"Plan {p["id"]}  [{p["status"]}]  {(p["crossTenant"]!.GetValue<bool>() ? $"{p["sourceTenantName"]} -> {p["targetTenantName"]}" : $"in place: {p["targetTenantName"]}")}");
        Console.WriteLine($"  {p["createCount"]} create, {p["updateCount"]} update, {p["recycleBinCount"]} undelete, {p["deleteCount"]} delete, {p["skipCount"]} unchanged");
        if (p["appliedAt"] is not null) Console.WriteLine($"  Applied {p["appliedAt"]}: {p["succeededCount"]} succeeded, {p["failedCount"]} failed");
        if (p["error"] is { } err) Console.WriteLine($"  {err}");
        foreach (var n in detail["notes"]?.AsArray() ?? []) Console.WriteLine($"  Note: {n}");
        Console.WriteLine();
        foreach (var a in detail["actions"]!.AsArray().Select(a => a!))
        {
            var kind = a["kind"]!.GetValue<string>();
            if (kind == "Skip" && !all) continue;
            var excluded = a["excluded"]!.GetValue<bool>() ? " (excluded)" : "";
            var status = p["appliedAt"] is null || kind == "Skip" ? "" : $" [{a["status"]}]";
            var dep = a["isDependency"]!.GetValue<bool>() ? " (dependency)" : a["isRelink"]?.GetValue<bool>() == true ? " (re-link)" : "";
            Console.WriteLine($" {a["index"],4}  {kind,-10} {a["type"],-40} {a["displayName"]}{dep}{excluded}{status}");
            if (a["error"] is { } e) Console.WriteLine($"         error: {e}");
            foreach (var w in a["warnings"]!.AsArray()) Console.WriteLine($"         warning: {w}");
            foreach (var v in a["verificationIssues"]!.AsArray()) Console.WriteLine($"         differs after restore: {v}");
        }
    }

    private static async Task<JsonNode> WaitForPlanAsync(MbrClient c, string planId, string jobId, CancellationToken ct)
    {
        var done = await WatchJobAsync(c, jobId, ct);
        if (done["status"]!.GetValue<string>() == "Failed") throw new CliException($"Failed: {done["error"]}");
        return (await c.GetAsync($"api/restore/plans/{planId}", ct))!;
    }

    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected) throw new CliException($"{question} Pass --yes to confirm non-interactively.");
        Console.Write($"{question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static async Task ApplyPlanAsync(MbrClient c, string planId, bool yes, bool json, CancellationToken ct)
    {
        var detail = (await c.GetAsync($"api/restore/plans/{planId}", ct))!;
        var count = detail["actions"]!.AsArray().Count(a => a!["kind"]!.GetValue<string>() != "Skip" && !a["excluded"]!.GetValue<bool>());
        if (count == 0) { Console.WriteLine("Nothing to apply."); return; }
        if (!yes && !Confirm($"Apply {count} change(s) to {detail["plan"]!["targetTenantName"]}?")) { Console.WriteLine("Cancelled."); return; }
        var r = (await c.PostAsync($"api/restore/plans/{planId}/apply", new { }, ct))!;
        if (r["jobId"] is null)
        {
            Console.WriteLine($"Plan is {r["status"]}: another restore operator must approve it (mbr restore approve {planId}).");
            return;
        }
        var final = await WaitForPlanAsync(c, planId, r["jobId"]!.GetValue<string>(), ct);
        if (json) PrintJson(final); else PrintPlan(final, false);
    }

    private static Command Restore()
    {
        var t = TenantOption();
        var snapshot = new Option<string>("--snapshot", "-s") { Description = "Snapshot ID, latest, previous or baseline (default: latest)" };
        var into = new Option<string?>("--into") { Description = "Restore into another tenant (clone/migrate). Default: the snapshot's tenant" };
        var workloads = new Option<string[]>("--workload", "-w") { Description = "Only these workloads (repeatable)", AllowMultipleArgumentsPerToken = true };
        var types = new Option<string[]>("--type") { Description = "Only these resource types (repeatable)", AllowMultipleArgumentsPerToken = true };
        var items = new Option<long[]>("--item") { Description = "Only these snapshot item IDs (repeatable)", AllowMultipleArgumentsPerToken = true };
        var keepChanged = new Option<bool>("--keep-changed") { Description = "Don't revert objects that changed since the snapshot; only recreate missing ones" };
        var noDeps = new Option<bool>("--no-dependencies") { Description = "Don't pull in referenced objects (groups, filters, ...)" };
        var noAssign = new Option<bool>("--no-assignments") { Description = "Don't restore assignments and memberships" };
        var noRecycle = new Option<bool>("--no-recycle-bin") { Description = "Recreate deleted directory objects instead of undeleting them" };
        var noRelink = new Option<bool>("--no-relink") { Description = "Don't re-add restored users/groups to the groups, roles, policies and assignments that referenced them" };
        var reportOnly = new Option<bool>("--report-only") { Description = "Restore Conditional Access policies in report-only mode" };
        var mirror = new Option<bool>("--mirror") { Description = "Delete objects of the selected types that aren't in the snapshot (admins)" };
        var suffix = new Option<string?>("--suffix") { Description = "Append to the names of newly created objects" };
        var stopOnError = new Option<bool>("--stop-on-error") { Description = "Stop at the first failed action" };
        var apply = new Option<bool>("--apply") { Description = "Apply right after planning" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };
        var all = new Option<bool>("--all", "-a") { Description = "Also list unchanged objects" };
        var planArg = new Argument<string>("plan") { Description = "Restore plan ID" };
        var reason = new Option<string?>("--reason") { Description = "Why the plan is rejected" };
        var exclude = new Option<int[]>("--exclude") { Description = "Action numbers to leave out", AllowMultipleArgumentsPerToken = true };
        var include = new Option<int[]>("--include") { Description = "Action numbers to put back", AllowMultipleArgumentsPerToken = true };

        return Group("restore", "Plan, review and apply configuration restores",
            Cmd("plan", "Build a restore plan (reads only; shows what would change)", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var sid = await ResolveSnapshotIdAsync(c, tid, pr.GetValue(snapshot) ?? "latest", ct);
                string? target = pr.GetValue(into) is { } i ? await c.ResolveTenantAsync(i, ct) : null;
                var body = new
                {
                    snapshotId = sid, targetTenantId = target,
                    workloads = pr.GetValue(workloads), types = pr.GetValue(types), itemIds = pr.GetValue(items),
                    options = new
                    {
                        overwriteChanged = !pr.GetValue(keepChanged), includeDependencies = !pr.GetValue(noDeps), includeAssignments = !pr.GetValue(noAssign),
                        useRecycleBin = !pr.GetValue(noRecycle), relinkReferences = !pr.GetValue(noRelink), conditionalAccessReportOnly = pr.GetValue(reportOnly), deleteExtra = pr.GetValue(mirror),
                        nameSuffix = pr.GetValue(suffix), stopOnError = pr.GetValue(stopOnError),
                    },
                };
                var r = (await c.PostAsync("api/restore/plans", body, ct))!;
                var planId = r["planId"]!.GetValue<string>();
                var detail = await WaitForPlanAsync(c, planId, r["jobId"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(detail); else PrintPlan(detail, pr.GetValue(all));
                if (pr.GetValue(apply)) await ApplyPlanAsync(c, planId, pr.GetValue(yes), WantsJson(pr), ct);
                else if (!WantsJson(pr)) Console.WriteLine($"\nReview, then run: mbr restore apply {planId}");
            }, t, snapshot, into, workloads, types, items, keepChanged, noDeps, noAssign, noRecycle, noRelink, reportOnly, mirror, suffix, stopOnError, apply, yes, all),

            Cmd("list", "List restore plans for a tenant", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                Print(pr, await c.GetAsync($"api/tenants/{tid}/restore/plans", ct),
                    ("ID", "id"), ("Created", "createdAt"), ("Status", "status"), ("Create", "createCount"), ("Update", "updateCount"),
                    ("Undelete", "recycleBinCount"), ("Delete", "deleteCount"), ("Failed", "failedCount"), ("By", "createdBy"));
            }, t),

            Cmd("show", "Show a plan's actions (and results once applied)", async (pr, ct) =>
            {
                var d = (await Client().GetAsync($"api/restore/plans/{pr.GetValue(planArg)}", ct))!;
                if (WantsJson(pr)) PrintJson(d); else PrintPlan(d, pr.GetValue(all));
            }, planArg, all),

            Cmd("edit", "Exclude or include actions in a draft plan", async (pr, ct) =>
            {
                var r = await Client().SendAsync(HttpMethod.Patch, $"api/restore/plans/{pr.GetValue(planArg)}/actions",
                    new { exclude = pr.GetValue(exclude), include = pr.GetValue(include) }, ct);
                Console.WriteLine($"{r!["included"]} action(s) will run, {r["excluded"]} excluded.");
            }, planArg, exclude, include),

            Cmd("apply", "Apply a reviewed plan", async (pr, ct) =>
                await ApplyPlanAsync(Client(), pr.GetValue(planArg)!, pr.GetValue(yes), WantsJson(pr), ct), planArg, yes),

            Cmd("approve", "Approve a plan that is waiting for a second operator (it then runs)", async (pr, ct) =>
            {
                var c = Client();
                var id = pr.GetValue(planArg)!;
                var r = (await c.PostAsync($"api/restore/plans/{id}/approve", new { }, ct))!;
                var final = await WaitForPlanAsync(c, id, r["jobId"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(final); else PrintPlan(final, false);
            }, planArg),

            Cmd("reject", "Reject a plan", async (pr, ct) =>
            {
                await Client().PostAsync($"api/restore/plans/{pr.GetValue(planArg)}/reject", new { reason = pr.GetValue(reason) }, ct);
                Console.WriteLine("Rejected.");
            }, planArg, reason));
    }
}
