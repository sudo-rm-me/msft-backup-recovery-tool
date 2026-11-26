using System.CommandLine;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

/// <summary>Commands for backup, snapshots, diff and restore.</summary>
public static partial class FeatureCommands
{
    public static IEnumerable<Command> All()
    {
        yield return Backup();
        yield return Snapshot();
        foreach (var c in Later()) yield return c;
    }

    private static partial IEnumerable<Command> Later();

    private static Command Backup()
    {
        var t = TenantOption();
        var workloads = new Option<string[]>("--workload", "-w") { Description = "Workloads or resource types (default: all)", AllowMultipleArgumentsPerToken = true };
        var label = new Option<string?>("--label");
        var storage = new Option<string?>("--storage") { Description = "Storage target ID (default: the tenant's)" };
        var noWatch = new Option<bool>("--no-watch") { Description = "Return immediately instead of following the job" };
        var days = new Option<int?>("--config-days");
        var keep = new Option<int?>("--keep-min");
        var dataDays = new Option<int?>("--data-days");
        var drift = new Option<bool?>("--drift-alerts");

        return Group("backup", "Run backups and manage retention",
            Cmd("run", "Back up configuration now", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var ws = pr.GetValue(workloads)?.SelectMany(w => w.Split(',')).Where(w => w.Length > 0).ToArray();
                var job = await c.PostAsync("api/jobs", new
                {
                    type = "backup.config", tenantId = tid,
                    parameters = new { workloads = ws, label = pr.GetValue(label), storageTargetId = pr.GetValue(storage) },
                }, ct);
                Console.WriteLine($"Started job {job!["id"]}");
                if (pr.GetValue(noWatch)) return;
                var done = await WatchJobAsync(c, job["id"]!.GetValue<string>(), ct);
                if (done["result"]?["snapshotId"] is { } sid) Console.WriteLine($"Snapshot: {sid}");
            }, t, workloads, label, storage, noWatch),
            Cmd("retention", "Show or change retention settings", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var s = (await c.GetAsync($"api/tenants/{tid}/settings", ct))!.AsObject();
                if (pr.GetValue(days) is { } d) s["configRetentionDays"] = d;
                if (pr.GetValue(keep) is { } k) s["configKeepMinimum"] = k;
                if (pr.GetValue(dataDays) is { } dd) s["dataRetentionDays"] = dd;
                if (pr.GetValue(drift) is { } dr) s["driftAlerts"] = dr;
                if (pr.GetValue(days) != null || pr.GetValue(keep) != null || pr.GetValue(dataDays) != null || pr.GetValue(drift) != null)
                    s = (await c.SendAsync(HttpMethod.Put, $"api/tenants/{tid}/settings", s, ct))!.AsObject();
                PrintObject(pr, s, ("Config snapshots (days)", "configRetentionDays"), ("Always keep newest", "configKeepMinimum"),
                    ("Changed/deleted data (days)", "dataRetentionDays"), ("Drift alerts", "driftAlerts"));
            }, t, days, keep, dataDays, drift));
    }

    private static Command Snapshot()
    {
        var t = TenantOption();
        var id = new Argument<string>("snapshot") { Description = "Snapshot ID" };
        var itemId = new Argument<long>("item") { Description = "Item ID (from 'snapshot items')" };
        var type = new Option<string?>("--type");
        var search = new Option<string?>("--search");
        var output = new Option<string?>("--output", "-o") { Description = "Output file" };
        var unset = new Option<bool>("--unset");
        var labelArg = new Argument<string>("label");

        return Group("snapshot", "Browse configuration snapshots",
            Cmd("list", "List snapshots", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                Print(pr, await c.GetAsync($"api/tenants/{tid}/snapshots", ct), ("ID", "id"), ("Taken", "createdAt"), ("Status", "status"),
                    ("Items", "itemCount"), ("+", "addedCount"), ("~", "modifiedCount"), ("-", "removedCount"), ("Baseline", "isBaseline"), ("Label", "label"));
            }, t),
            Cmd("show", "Show a snapshot's types and issues", async (pr, ct) =>
            {
                var d = await Client().GetAsync($"api/snapshots/{pr.GetValue(id)}", ct);
                if (WantsJson(pr)) { PrintJson(d); return; }
                PrintObject(pr, d!["snapshot"], ("ID", "id"), ("Taken", "createdAt"), ("Status", "status"), ("Items", "itemCount"), ("Workloads", "workloads"), ("Label", "label"));
                Console.WriteLine();
                Print(pr, d["types"], ("Workload", "workload"), ("Type", "resourceType"), ("Count", "count"));
                if (d["issues"]!.AsArray().Count > 0)
                {
                    Console.WriteLine();
                    Print(pr, d["issues"], ("Severity", "severity"), ("Type", "resourceType"), ("Message", "message"));
                }
            }, id),
            Cmd("items", "List items in a snapshot", async (pr, ct) =>
            {
                var q = $"api/snapshots/{pr.GetValue(id)}/items?take=1000";
                if (pr.GetValue(type) is { } ty) q += "&type=" + Uri.EscapeDataString(ty);
                if (pr.GetValue(search) is { } s) q += "&search=" + Uri.EscapeDataString(s);
                var r = await Client().GetAsync(q, ct);
                Print(pr, r!["items"], ("Item", "id"), ("Type", "resourceType"), ("Name", "displayName"), ("Object ID", "objectId"));
            }, id, type, search),
            Cmd("get", "Print an item's saved JSON", async (pr, ct) =>
                PrintJson((await Client().GetAsync($"api/snapshots/{pr.GetValue(id)}/items/{pr.GetValue(itemId)}", ct))!["content"]), id, itemId),
            Cmd("export", "Download a snapshot as a zip of JSON files", async (pr, ct) =>
            {
                var file = pr.GetValue(output) ?? $"snapshot-{pr.GetValue(id)}.zip";
                await using var src = await Client().DownloadAsync($"api/snapshots/{pr.GetValue(id)}/export", ct);
                await using var dst = File.Create(file);
                await src.CopyToAsync(dst, ct);
                Console.WriteLine($"Saved {file}");
            }, id, output),
            Cmd("baseline", "Mark a snapshot as the baseline (drift is measured against it)", async (pr, ct) =>
            {
                await Client().SendAsync(HttpMethod.Patch, $"api/snapshots/{pr.GetValue(id)}", new { isBaseline = !pr.GetValue(unset) }, ct);
                Console.WriteLine(pr.GetValue(unset) ? "Baseline cleared." : "Baseline set.");
            }, id, unset),
            Cmd("label", "Set a snapshot's label", async (pr, ct) =>
            {
                await Client().SendAsync(HttpMethod.Patch, $"api/snapshots/{pr.GetValue(id)}", new { label = pr.GetValue(labelArg) }, ct);
                Console.WriteLine("Saved.");
            }, id, labelArg),
            Cmd("delete", "Delete a snapshot", async (pr, ct) =>
            {
                await Client().SendAsync(HttpMethod.Delete, $"api/snapshots/{pr.GetValue(id)}", null, ct);
                Console.WriteLine("Deleted.");
            }, id));
    }
}
