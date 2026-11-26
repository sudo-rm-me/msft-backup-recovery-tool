using System.CommandLine;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase4Plus()
    {
        yield return Entra();
        foreach (var c in Phase5Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase5Plus();

    private static Command Entra()
    {
        var t = TenantOption();
        var kind = new Option<string?>("--kind", "-k") { Description = "user, group, application, servicePrincipal or administrativeUnit (default: all)" };
        var ids = new Argument<string[]>("ids") { Description = "Object IDs to undelete", Arity = ArgumentArity.OneOrMore };

        return Group("entra", "Entra ID recycle bin",
            Cmd("deleted", "List deleted objects that can still be undeleted (30 days)", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var q = $"api/tenants/{tid}/entra/deleted" + (pr.GetValue(kind) is { } k ? $"?kind={Uri.EscapeDataString(k)}" : "");
                Print(pr, await c.GetAsync(q, ct), ("ID", "id"), ("Kind", "kind"), ("Name", "displayName"), ("UPN", "userPrincipalName"),
                    ("Deleted", "deletedDateTime"), ("Days left", "daysLeft"), ("In backup", "snapshotItemId"));
            }, t, kind),

            Cmd("undelete", "Restore objects from the recycle bin (keeps their IDs and memberships)", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var r = await c.PostAsync($"api/tenants/{tid}/entra/deleted/restore", new { ids = pr.GetValue(ids) }, ct);
                var done = await WatchJobAsync(c, r!["jobId"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(done["result"]);
            }, t, ids));
    }
}
