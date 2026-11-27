using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase6Plus()
    {
        // "mbr data restore" is added to the data group (see DataRestoreCommand); later phases add top-level commands here.
        foreach (var c in Phase7Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase7Plus();

    /// <summary>mbr data restore &lt;source&gt; &lt;path&gt;... — restores files, folders, mail folders or messages.</summary>
    internal static Command DataRestoreCommand()
    {
        var t = TenantOption();
        var sourceArg = new Argument<string>("source") { Description = "Source ID, name, UPN or site URL" };
        var paths = new Argument<string[]>("paths") { Description = "Paths to restore, e.g. /Reports or /Inbox/Projects (folders include their content)", Arity = ArgumentArity.OneOrMore };
        var library = new Option<string?>("--library", "-l") { Description = "Library / container (e.g. Documents, Mail, Calendar, Contacts)" };
        var at = new Option<string?>("--at") { Description = "Restore as of this time (default: latest; deleted items: just before deletion)" };
        var to = new Option<string?>("--to") { Description = "Restore into another source (another user's OneDrive or mailbox, another site)" };
        var into = new Option<string?>("--into") { Description = "Restore into another tenant (default target: the same user or site there)" };
        var mapDomain = MapDomainOption();
        var folder = new Option<string?>("--into-folder") { Description = "Restore into a new folder with this name (keeps the structure)" };
        var conflict = new Option<string>("--conflict") { Description = "rename (keep both), replace or skip", DefaultValueFactory = _ => "rename" };
        conflict.AcceptOnlyFromAmong("rename", "replace", "skip");

        return Cmd("restore", "Restore files, folders or mail from a backup", async (pr, ct) =>
        {
            var c = Client();
            var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
            var source = await ResolveSourceAsync(c, tid, pr.GetValue(sourceArg)!, ct);
            string? containerId = null;
            var ids = new JsonArray();
            foreach (var p in pr.GetValue(paths)!)
            {
                var (cid, _, item) = await ResolvePathAsync(c, source, pr.GetValue(library), p, pr.GetValue(at), ct);
                if (item is null) throw new CliException("Give a path below the root (e.g. /Documents), not the root itself.");
                if (containerId != null && containerId != cid) throw new CliException("All paths must be in the same library.");
                containerId = cid;
                ids.Add(item["itemId"]!.GetValue<string>());
            }
            var targetTenant = pr.GetValue(into) is { } i ? await c.ResolveTenantAsync(i, ct) : null;
            var target = pr.GetValue(to) is { } other ? (await ResolveSourceAsync(c, targetTenant ?? tid, other, ct))["id"]!.GetValue<string>() : null;
            var body = new JsonObject
            {
                ["containerId"] = containerId, ["itemIds"] = ids, ["conflict"] = pr.GetValue(conflict),
                ["location"] = pr.GetValue(folder) != null || (target != null && targetTenant == null) ? "folder" : "original",
            };
            if (targetTenant != null) body["targetTenantId"] = targetTenant;
            if (ParseDomainMap(pr.GetValue(mapDomain)) is { } domains)
                body["domainMap"] = System.Text.Json.JsonSerializer.SerializeToNode(domains);
            if (pr.GetValue(folder) is { } f) body["folderName"] = f;
            if (target != null) body["targetSourceId"] = target;
            if (pr.GetValue(at) is { } a) body["at"] = DateTimeOffset.Parse(a).ToUniversalTime().ToString("o");
            var r = await c.PostAsync($"api/tenants/{tid}/data/restore", body, ct);
            var done = await WatchJobAsync(c, r!["jobId"]!.GetValue<string>(), ct);
            if (WantsJson(pr)) PrintJson(done["result"]);
        }, t, sourceArg, paths, library, at, to, into, mapDomain, folder, conflict);
    }
}
