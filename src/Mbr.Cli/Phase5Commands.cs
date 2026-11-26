using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static partial class FeatureCommands
{
    private static partial IEnumerable<Command> Phase5Plus()
    {
        yield return Data();
        foreach (var c in Phase6Plus()) yield return c;
    }

    private static partial IEnumerable<Command> Phase6Plus();

    /// <summary>Finds a data source by ID, display name or address (UPN / site URL).</summary>
    private static async Task<JsonNode> ResolveSourceAsync(MbrClient c, string tenantId, string reference, CancellationToken ct)
    {
        var all = (await c.GetAsync($"api/tenants/{tenantId}/data/sources", ct))!.AsArray().Select(s => s!).ToList();
        bool Eq(JsonNode? n) => n?.ToString().Equals(reference, StringComparison.OrdinalIgnoreCase) == true;
        var hits = all.Where(s => Eq(s["id"]) || Eq(s["displayName"]) || Eq(s["address"]) || Eq(s["externalId"])).ToList();
        return hits.Count switch
        {
            1 => hits[0],
            0 => throw new CliException($"No data source matches '{reference}'. Run 'mbr data sources' to list them."),
            _ => throw new CliException($"'{reference}' matches {hits.Count} sources; use the ID."),
        };
    }

    /// <summary>Walks a "/folder/sub" path from the container root, as of a time.</summary>
    private static async Task<(string ContainerId, string? ParentId, JsonNode? Item)> ResolvePathAsync(MbrClient c, JsonNode source, string? library, string path,
        string? at, CancellationToken ct)
    {
        var detail = (await c.GetAsync($"api/data/sources/{source["id"]}", ct))!;
        var containers = detail["containers"]!.AsArray().Select(x => x!).ToList();
        var container = library is null
            ? containers.FirstOrDefault(x => !x["missing"]!.GetValue<bool>() && x["kind"]?.ToString() is "drive" or "mail") ?? containers.FirstOrDefault()
            : containers.FirstOrDefault(x => x["name"]!.ToString().Equals(library, StringComparison.OrdinalIgnoreCase));
        if (container is null) throw new CliException(library is null ? "Nothing has been backed up for this source yet." : $"No library named '{library}'.");
        var cid = container["id"]!.GetValue<string>();
        string? parent = null;
        JsonNode? item = null;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var items = await ChildrenAsync(c, cid, parent, at, true, ct);
            item = items.FirstOrDefault(i => i!["name"]!.ToString().Equals(part, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CliException($"'{part}' not found{(at is null ? "" : $" as of {at}")}.");
            parent = item!["itemId"]!.GetValue<string>();
        }
        return (cid, parent, item);
    }

    private static async Task<JsonArray> ChildrenAsync(MbrClient c, string containerId, string? parent, string? at, bool deleted, CancellationToken ct)
    {
        var q = $"api/data/containers/{containerId}/children?deleted={(deleted && at is null).ToString().ToLowerInvariant()}";
        if (parent != null) q += $"&parent={Uri.EscapeDataString(parent)}";
        if (at != null) q += $"&at={Uri.EscapeDataString(DateTimeOffset.Parse(at).ToUniversalTime().ToString("o"))}";
        return (await c.GetAsync(q, ct))!["items"]!.AsArray();
    }

    private static string Size(JsonNode? n)
    {
        var b = n?.GetValue<long>() ?? 0;
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    private static Command Data()
    {
        var t = TenantOption();
        var kind = new Option<string?>("--kind", "-k") { Description = "OneDrive, SharePointSite or Mailbox" };
        var sourceArg = new Argument<string>("source") { Description = "Source ID, name, UPN or site URL" };
        var sources = new Option<string[]>("--source", "-s") { Description = "Only these sources (repeatable)", AllowMultipleArgumentsPerToken = true };
        var pathArg = new Argument<string>("path") { Description = "Path inside the library, e.g. /Documents/Reports", DefaultValueFactory = _ => "/" };
        var library = new Option<string?>("--library", "-l") { Description = "Document library (SharePoint sites with more than one)" };
        var at = new Option<string?>("--at") { Description = "Show the content as it was at this time (e.g. 2026-11-20T09:00)" };
        var deleted = new Option<bool>("--deleted") { Description = "Include deleted items" };
        var version = new Option<long?>("--version") { Description = "Version ID (from 'data versions'); default: the one valid at --at, or the latest" };
        var output = new Option<string?>("--output", "-o") { Description = "File to write (default: the item's name in the current folder)" };
        var query = new Argument<string>("query") { Description = "Part of a file or message name" };

        return Group("data", "OneDrive, SharePoint and mailbox data protection",
            DataRestoreCommand(),
            Cmd("sources", "List data sources", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var q = $"api/tenants/{tid}/data/sources" + (pr.GetValue(kind) is { } k ? $"?kind={k}" : "");
                Print(pr, await c.GetAsync(q, ct), ("ID", "id"), ("Kind", "kind"), ("Name", "displayName"), ("Address", "address"), ("Enabled", "enabled"),
                    ("Last backup", "lastBackupAt"), ("Status", "lastStatus"), ("Items", "itemCount"), ("Bytes", "totalBytes"));
            }, t, kind),

            Cmd("discover", "Find OneDrives and SharePoint sites", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var p = new JsonObject();
                if (pr.GetValue(kind) is { } k) p["kinds"] = new JsonArray(k);
                var job = await c.PostAsync("api/jobs", new { type = "data.discover", tenantId = tid, parameters = p }, ct);
                await WatchJobAsync(c, job!["id"]!.GetValue<string>(), ct);
            }, t, kind),

            Cmd("enable", "Protect a source", async (pr, ct) =>
            {
                var c = Client();
                var s = await ResolveSourceAsync(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), pr.GetValue(sourceArg)!, ct);
                await c.SendAsync(HttpMethod.Patch, $"api/data/sources/{s["id"]}", new { enabled = true }, ct);
                Console.WriteLine($"{s["displayName"]} will be backed up.");
            }, t, sourceArg),

            Cmd("disable", "Stop backing up a source (existing backups are kept)", async (pr, ct) =>
            {
                var c = Client();
                var s = await ResolveSourceAsync(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), pr.GetValue(sourceArg)!, ct);
                await c.SendAsync(HttpMethod.Patch, $"api/data/sources/{s["id"]}", new { enabled = false }, ct);
                Console.WriteLine($"{s["displayName"]} will no longer be backed up.");
            }, t, sourceArg),

            Cmd("backup", "Back up enabled sources now (or the given ones)", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var p = new JsonObject();
                var ids = new JsonArray();
                foreach (var s in pr.GetValue(sources) ?? []) ids.Add((await ResolveSourceAsync(c, tid, s, ct))["id"]!.GetValue<string>());
                if (ids.Count > 0) p["sourceIds"] = ids;
                if (pr.GetValue(kind) is { } k) p["kinds"] = new JsonArray(k);
                var job = await c.PostAsync("api/jobs", new { type = "backup.data", tenantId = tid, parameters = p }, ct);
                var done = await WatchJobAsync(c, job!["id"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(done["result"]);
            }, t, sources, kind),

            Cmd("ls", "List a folder (optionally as it was at a point in time)", async (pr, ct) =>
            {
                var c = Client();
                var s = await ResolveSourceAsync(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), pr.GetValue(sourceArg)!, ct);
                var (cid, parent, item) = await ResolvePathAsync(c, s, pr.GetValue(library), pr.GetValue(pathArg)!, pr.GetValue(at), ct);
                if (item?["isFolder"]?.GetValue<bool>() == false) { PrintObject(pr, item, ("Name", "name"), ("Path", "path"), ("Size", "size"), ("Modified", "modifiedAt"), ("Version", "id")); return; }
                var items = await ChildrenAsync(c, cid, parent, pr.GetValue(at), pr.GetValue(deleted), ct);
                if (WantsJson(pr)) { PrintJson(items); return; }
                foreach (var i in items)
                {
                    var folder = i!["isFolder"]!.GetValue<bool>();
                    var del = i["deletedAt"] is null ? "" : $"  (deleted {i["deletedAt"]})";
                    Console.WriteLine($"{(folder ? "d" : "-")} {(folder ? "" : Size(i["size"])),10}  {i["modifiedAt"]?.ToString() ?? "",-28}  {i["name"]}{(folder ? "/" : "")}{del}");
                }
            }, t, sourceArg, pathArg, library, at, deleted),

            Cmd("versions", "List the backed-up versions of a file", async (pr, ct) =>
            {
                var c = Client();
                var s = await ResolveSourceAsync(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), pr.GetValue(sourceArg)!, ct);
                var (cid, _, item) = await ResolvePathAsync(c, s, pr.GetValue(library), pr.GetValue(pathArg)!, null, ct);
                if (item is null) throw new CliException("Give the path of a file.");
                Print(pr, await c.GetAsync($"api/data/containers/{cid}/items/{Uri.EscapeDataString(item["itemId"]!.ToString())}/history", ct),
                    ("Version", "id"), ("Backed up", "capturedAt"), ("Name", "name"), ("Size", "size"), ("Modified", "modifiedAt"), ("By", "modifiedBy"), ("Deleted", "deletedAt"));
            }, t, sourceArg, pathArg, library),

            Cmd("get", "Download a backed-up file", async (pr, ct) =>
            {
                var c = Client();
                long id;
                string name;
                if (pr.GetValue(version) is { } v)
                {
                    id = v;
                    name = (await c.GetAsync($"api/data/versions/{v}", ct))!["name"]!.ToString();
                }
                else
                {
                    var s = await ResolveSourceAsync(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), pr.GetValue(sourceArg)!, ct);
                    var (_, _, item) = await ResolvePathAsync(c, s, pr.GetValue(library), pr.GetValue(pathArg)!, pr.GetValue(at), ct);
                    if (item is null || item["isFolder"]!.GetValue<bool>()) throw new CliException("Give the path of a file.");
                    id = item["id"]!.GetValue<long>();
                    name = item["name"]!.ToString();
                }
                var target = pr.GetValue(output) ?? Path.GetFileName(name);
                await using (var src = await c.DownloadAsync($"api/data/versions/{id}/content", ct))
                await using (var dst = File.Create(target))
                    await src.CopyToAsync(dst, ct);
                Console.WriteLine($"Saved {target} ({Size(JsonValue.Create(new FileInfo(target).Length))})");
            }, t, sourceArg, pathArg, library, at, version, output),

            Cmd("search", "Find files by name across all sources (including deleted)", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var r = (await c.GetAsync($"api/tenants/{tid}/data/search?q={Uri.EscapeDataString(pr.GetValue(query)!)}", ct))!.AsArray();
                if (WantsJson(pr)) { PrintJson(r); return; }
                foreach (var h in r)
                {
                    var i = h!["item"]!;
                    Console.WriteLine($"{h["sourceName"]}  {i["path"]}/{i["name"]}{(i["deletedAt"] is null ? "" : "  (deleted)")}  [version {i["id"]}]".Replace("//", "/"));
                }
            }, t, query));
    }
}
