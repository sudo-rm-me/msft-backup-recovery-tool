using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Later()
    {
        yield return Diff();
        foreach (var c in Phase3Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase3Plus();

    private static Command Diff()
    {
        var t = TenantOption();
        var from = new Option<string>("--from") { Description = "Snapshot ID, baseline, latest or previous (default: baseline)" };
        var to = new Option<string>("--to") { Description = "Snapshot ID, latest, previous or live (default: latest)" };
        var types = new Option<string?>("--type") { Description = "Comma-separated types or workloads" };
        var details = new Option<bool>("--details", "-d") { Description = "Show property-level changes" };

        return Cmd("diff", "Compare two snapshots, or a snapshot with the live tenant", async (pr, ct) =>
        {
            var c = Client();
            var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
            var f = pr.GetValue(from) ?? "baseline";
            var target = pr.GetValue(to) ?? "latest";
            if (target == "live")
            {
                var parameters = new JsonObject();
                if (Guid.TryParse(f, out _)) parameters["against"] = f;
                else if (f != "baseline") throw new CliException("With --to live, --from must be 'baseline' or a snapshot ID.");
                var job = await c.PostAsync("api/jobs", new { type = "config.drift", tenantId = tid, parameters }, ct);
                var done = await WatchJobAsync(c, job!["id"]!.GetValue<string>(), ct);
                if (done["status"]!.GetValue<string>() != "Succeeded") return;
                f = done["result"]!["from"]!.GetValue<string>();
                target = done["result"]!["to"]!.GetValue<string>();
            }
            var q = $"api/diff?from={f}&to={target}&tenantId={tid}";
            if (pr.GetValue(types) is { } ty) q += "&types=" + Uri.EscapeDataString(ty);
            var r = await c.GetAsync(q, ct);
            if (WantsJson(pr)) { PrintJson(r); return; }
            var s = r!["summary"]!;
            Console.WriteLine($"{r["from"]!["createdAt"]} -> {r["to"]!["createdAt"]}: {s["added"]} added, {s["modified"]} modified, {s["removed"]} removed, {s["unchanged"]} unchanged");
            Console.WriteLine();
            foreach (var e in s["entries"]!.AsArray())
            {
                var sym = e!["change"]!.GetValue<string>() switch { "Added" => "+", "Removed" => "-", _ => "~" };
                Console.WriteLine($" {sym} {e["type"],-40} {e["displayName"]}");
                if (!pr.GetValue(details) || sym != "~") continue;
                var d = await c.GetAsync($"api/diff/item?fromItem={e["fromItemId"]}&toItem={e["toItemId"]}", ct);
                foreach (var ch in d!["changes"]!.AsArray())
                    Console.WriteLine($"      {ch!["path"]}: {Short(ch["before"])} -> {Short(ch["after"])}");
            }
        }, t, from, to, types, details);
    }

    private static string Short(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "(none)";
        return s.Length > 80 ? s[..77] + "..." : s;
    }
}
