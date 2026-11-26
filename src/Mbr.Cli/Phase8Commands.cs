using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase8Plus()
    {
        yield return Report();
        yield return Worker();
        foreach (var c in Phase9Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase9Plus();

    /// <summary>mbr report — protection status across tenants (or one), optionally as CSV.</summary>
    private static Command Report()
    {
        var t = new Option<string?>("--tenant", "-t") { Description = "Only this tenant (default: every tenant you can see)" };
        var csv = new Option<string?>("--csv") { Description = "Write the report as CSV to this file" };
        var problems = new Option<bool>("--problems") { Description = "Only tenants that need attention" };
        return Cmd("report", "Protection report: coverage, last backups and problems per tenant", async (pr, ct) =>
        {
            var c = Client();
            var tid = pr.GetValue(t) is { } tenant ? await c.ResolveTenantAsync(tenant, ct) : null;
            var query = tid is null ? "" : $"?tenantId={tid}";
            if (pr.GetValue(csv) is { } path)
            {
                await using (var stream = await c.DownloadAsync($"api/reports/protection{(query.Length > 0 ? query + "&" : "?")}format=csv", ct))
                await using (var file = File.Create(path))
                    await stream.CopyToAsync(file, ct);
                Console.WriteLine($"Wrote {path}");
                return;
            }
            var r = (await c.GetAsync($"api/reports/protection{query}", ct))!;
            if (WantsJson(pr)) { PrintJson(r); return; }
            var s = r["summary"]!;
            Console.WriteLine($"{s["tenants"]} tenant(s): {s["healthy"]} healthy, {s["atRisk"]} at risk, {s["unprotected"]} unprotected\n");
            var rows = new JsonArray(r["tenants"]!.AsArray()
                .Where(x => !pr.GetValue(problems) || x!["health"]!.GetValue<string>() != "Healthy")
                .Select(x => (JsonNode)new JsonObject
                {
                    ["tenant"] = x!["tenant"]!.GetValue<string>(), ["health"] = x["health"]!.GetValue<string>(),
                    ["last"] = x["lastConfigBackupAt"]?.GetValue<string>() ?? "never",
                    ["workloads"] = $"{x["workloads"]!.AsArray().Count}/{x["workloads"]!.AsArray().Count + x["workloadsNeverBackedUp"]!.AsArray().Count}",
                    ["data"] = $"{x["dataSourcesProtected"]}/{x["dataSources"]}",
                    ["problems"] = string.Join(" ", x["problems"]!.AsArray().Select(p => p!.GetValue<string>())),
                }).ToArray());
            Print(pr, rows, ("Tenant", "tenant"), ("Health", "health"), ("Last config backup", "last"), ("Workloads", "workloads"),
                ("Data protected", "data"), ("Needs attention", "problems"));
        }, t, csv, problems);
    }

    private static Command Worker() =>
        Group("worker", "Job workers",
            Cmd("list", "Workers that reported in recently and what they're running", async (pr, ct) =>
            {
                var r = (await Client().GetAsync("api/workers", ct))!;
                if (WantsJson(pr)) { PrintJson(r); return; }
                Console.WriteLine($"{r["queued"]} job(s) waiting\n");
                var rows = new JsonArray(r["workers"]!.AsArray().Select(w => (JsonNode)new JsonObject
                {
                    ["host"] = $"{w!["host"]} (pid {w["processId"]})", ["running"] = $"{w["jobs"]!.AsArray().Count}/{w["maxConcurrency"]}",
                    ["takes"] = w["jobTypes"]!.AsArray().Count == 0 ? "all" : string.Join(",", w["jobTypes"]!.AsArray().Select(x => x!.GetValue<string>())),
                    ["seen"] = w["lastSeenAt"]!.GetValue<string>(),
                }).ToArray());
                Print(pr, rows, ("Host", "host"), ("Running", "running"), ("Takes", "takes"), ("Last seen", "seen"));
            }));
}
