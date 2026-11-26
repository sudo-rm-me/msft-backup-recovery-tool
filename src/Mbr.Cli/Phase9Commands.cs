using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase9Plus()
    {
        yield return Partner();
        foreach (var c in Phase9Extras()) yield return c;
    }

    /// <summary>Later Phase 9 commands (Microsoft 365 Backup Storage).</summary>
    private static partial IEnumerable<Command> Phase9Extras();

    private static Command Partner()
    {
        var ids = new Argument<string[]>("tenantIds") { Description = "Customer tenant IDs to add", Arity = ArgumentArity.ZeroOrMore };
        var all = new Option<bool>("--all") { Description = "Add every customer that isn't added yet" };

        return Group("partner", "GDAP customers of your partner tenant",
            Cmd("customers", "List customers from your GDAP relationships", async (pr, ct) =>
            {
                var r = (await Client().GetAsync("api/partner/customers", ct))!;
                if (WantsJson(pr)) { PrintJson(r); return; }
                var rows = new JsonArray(r.AsArray().Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c!["displayName"]!.GetValue<string>(), ["tenantId"] = c["tenantId"]!.GetValue<string>(),
                    ["status"] = c["active"]!.GetValue<bool>() ? "active" : c["relationships"]![0]?["status"]?.GetValue<string>(),
                    ["expires"] = c["earliestExpiry"]?.GetValue<string>(),
                    ["consent"] = c["canGrantConsent"]!.GetValue<bool>() ? "you can grant" : "customer admin",
                    ["added"] = c["onboardedTenantId"] is null ? "" : "yes",
                }).ToArray());
                Print(pr, rows, ("Customer", "name"), ("Tenant ID", "tenantId"), ("GDAP", "status"), ("Expires", "expires"), ("Consent", "consent"), ("Added", "added"));
            }),

            Cmd("import", "Add GDAP customers as tenants (they then need admin consent)", async (pr, ct) =>
            {
                var c = Client();
                var chosen = pr.GetValue(ids)!.ToList();
                if (pr.GetValue(all))
                    chosen = (await c.GetAsync("api/partner/customers", ct))!.AsArray()
                        .Where(x => x!["onboardedTenantId"] is null).Select(x => x!["tenantId"]!.GetValue<string>()).ToList();
                if (chosen.Count == 0) throw new CliException("Give customer tenant IDs, or --all.");
                var added = (await c.PostAsync("api/partner/customers/import", new { tenantIds = chosen }, ct))!;
                if (WantsJson(pr)) { PrintJson(added); return; }
                Console.WriteLine($"Added {added.AsArray().Count} tenant(s). Send each its consent links: mbr tenant consent <tenant>");
            }, ids, all));
    }
}
