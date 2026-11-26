using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase9Extras()
    {
        yield return M365Backup();
    }

    private static Command M365Backup()
    {
        var t = TenantOption();
        var unitArg = new Argument<string>("unit") { Description = "Protection unit: its ID, email or site name" };
        var policy = new Option<string?>("--policy") { Description = "Only units in this policy (ID)" };
        var point = new Option<string?>("--point") { Description = "Restore point ID (default: the latest)" };
        var inPlace = new Option<bool>("--in-place") { Description = "Roll the original back instead of restoring to a new location" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm an in-place restore" };

        async Task<(MbrClient, string, JsonNode)> Unit(ParseResult pr, CancellationToken ct)
        {
            var c = Client();
            var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
            var reference = pr.GetValue(unitArg)!;
            var hits = (await c.GetAsync($"api/tenants/{tid}/m365backup/units", ct))!.AsArray()
                .Where(u => u!["id"]!.ToString().Equals(reference, StringComparison.OrdinalIgnoreCase)
                            || u["displayName"]!.ToString().Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
            return hits.Count switch
            {
                1 => (c, tid, hits[0]!),
                0 => throw new CliException($"No protection unit matches '{reference}'. Run 'mbr m365backup units' to list them."),
                _ => throw new CliException($"'{reference}' matches {hits.Count} units; use the ID."),
            };
        }

        return Group("m365backup", "Microsoft 365 Backup (Microsoft's backup storage): protection and native restores",
            Cmd("status", "Whether the service is on, and its policies", async (pr, ct) =>
            {
                var c = Client();
                var r = (await c.GetAsync($"api/tenants/{await c.ResolveTenantAsync(pr.GetValue(t), ct)}/m365backup", ct))!;
                if (WantsJson(pr)) { PrintJson(r); return; }
                if (!r["available"]!.GetValue<bool>()) { Console.WriteLine(r["message"]?.ToString() ?? $"Microsoft 365 Backup is {r["status"]}."); return; }
                Console.WriteLine($"Microsoft 365 Backup is {r["status"]}.");
                Print(pr, r["policies"], ("Policy", "displayName"), ("Workload", "kind"), ("Status", "status"), ("Protected", "units"), ("Retention", "retention"), ("ID", "id"));
            }, t),

            Cmd("units", "List protected mailboxes, OneDrives and sites", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var q = pr.GetValue(policy) is { } p ? $"?policyId={Uri.EscapeDataString(p)}" : "";
                Print(pr, await c.GetAsync($"api/tenants/{tid}/m365backup/units{q}", ct),
                    ("Name", "displayName"), ("Workload", "kind"), ("Status", "status"), ("Policy", "policyId"), ("ID", "id"));
            }, t, policy),

            Cmd("points", "List restore points of a unit (last 30 days, newest first)", async (pr, ct) =>
            {
                var (c, tid, u) = await Unit(pr, ct);
                Print(pr, await c.GetAsync($"api/tenants/{tid}/m365backup/units/{Uri.EscapeDataString(u["id"]!.ToString())}/restorePoints", ct),
                    ("Protected at", "protectionDateTime"), ("Tags", "tags"), ("Expires", "expirationDateTime"), ("ID", "id"));
            }, t, unitArg),

            Cmd("restore", "Restore a unit to a restore point (new location unless --in-place)", async (pr, ct) =>
            {
                var (c, tid, u) = await Unit(pr, ct);
                var unitId = u["id"]!.ToString();
                if (pr.GetValue(inPlace) && !pr.GetValue(yes))
                    throw new CliException($"An in-place restore replaces {u["displayName"]}'s current content. Add --yes to confirm.");
                var pointId = pr.GetValue(point)
                    ?? (await c.GetAsync($"api/tenants/{tid}/m365backup/units/{Uri.EscapeDataString(unitId)}/restorePoints", ct))!.AsArray()
                        .FirstOrDefault()?["id"]?.ToString()
                    ?? throw new CliException("No restore points in the last 30 days.");
                var s = await c.PostAsync($"api/tenants/{tid}/m365backup/restore", new { unitId, restorePointId = pointId, inPlace = pr.GetValue(inPlace) }, ct);
                if (WantsJson(pr)) { PrintJson(s); return; }
                Console.WriteLine($"Restore session {s!["id"]} is {s["status"]}. Follow it with: mbr m365backup sessions");
            }, t, unitArg, point, inPlace, yes),

            Cmd("sessions", "List native restore sessions", async (pr, ct) =>
            {
                var c = Client();
                Print(pr, await c.GetAsync($"api/tenants/{await c.ResolveTenantAsync(pr.GetValue(t), ct)}/m365backup/sessions", ct),
                    ("Started", "createdAt"), ("Workload", "kind"), ("Status", "status"), ("By", "createdBy"), ("Finished", "completedAt"), ("Error", "error"));
            }, t));
    }
}
