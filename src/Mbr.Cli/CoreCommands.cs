using System.CommandLine;
using System.Text.Json.Nodes;
using static Mbr.Cli.Cli;

namespace Mbr.Cli;

public static class CoreCommands
{
    public static Command Login()
    {
        var server = new Option<string?>("--server") { Description = "Server URL, e.g. https://mbr.contoso.com" };
        return Cmd("login", "Sign in to an MBR server", async (pr, ct) =>
        {
            var cfg = CliConfig.Load();
            if (pr.GetValue(server) is { } s) cfg.Server = s;
            await new MbrClient(cfg).LoginAsync(ct);
        }, server);
    }

    public static Command Tenant()
    {
        var t = TenantOption();
        var domain = new Argument<string>("domain") { Description = "Tenant domain (contoso.onmicrosoft.com) or directory ID" };
        var name = new Option<string?>("--name") { Description = "Display name" };
        var storage = new Option<string?>("--storage") { Description = "Default storage target ID" };

        return Group("tenant", "Manage customer tenants",
            Cmd("list", "List tenants", async (pr, ct) =>
                Print(pr, await Client().GetAsync("api/tenants", ct),
                    ("ID", "id"), ("Name", "displayName"), ("Domain", "defaultDomain"), ("Status", "status"), ("Directory ID", "directoryId"))),
            Cmd("add", "Add a tenant and print its admin-consent links", async (pr, ct) =>
            {
                var c = Client();
                var tenant = await c.PostAsync("api/tenants", new { domain = pr.GetValue(domain), displayName = pr.GetValue(name), storageTargetId = pr.GetValue(storage) }, ct);
                PrintObject(pr, tenant, ("ID", "id"), ("Name", "displayName"), ("Directory ID", "directoryId"), ("Status", "status"));
                await PrintConsent(c, tenant!["id"]!.GetValue<string>(), ct);
            }, domain, name, storage),
            Cmd("show", "Show a tenant", async (pr, ct) =>
            {
                var c = Client();
                var id = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                PrintObject(pr, await c.GetAsync($"api/tenants/{id}", ct), ("ID", "id"), ("Name", "displayName"), ("Domain", "defaultDomain"),
                    ("Directory ID", "directoryId"), ("Status", "status"), ("Backup consent", "backupConsentedAt"),
                    ("Restore consent", "restoreConsentedAt"), ("Last check", "lastPermissionCheckAt"), ("Storage", "defaultStorageTargetId"));
            }, t),
            Cmd("consent", "Print admin-consent links for a tenant", async (pr, ct) =>
            {
                var c = Client();
                await PrintConsent(c, await c.ResolveTenantAsync(pr.GetValue(t), ct), ct);
            }, t),
            Cmd("check", "Check which permissions the apps have in a tenant", async (pr, ct) =>
            {
                var c = Client();
                var id = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var r = await c.PostAsync($"api/tenants/{id}/permission-check", null, ct);
                if (WantsJson(pr)) { PrintJson(r); return; }
                foreach (var app in new[] { "backup", "restore" })
                {
                    var a = r![app]!;
                    Console.WriteLine($"{app} app: {(a["tokenAcquired"]!.GetValue<bool>() ? "connected" : "NOT connected - " + a["error"])}");
                    foreach (var m in a["missing"]!.AsArray()) Console.WriteLine($"   missing {m!["permission"]} ({m["workload"]}): {m["reason"]}");
                }
                var az = r!["azure"]!;
                Console.WriteLine($"azure: {az["subscriptions"]!.AsArray().Count} subscription(s) visible{(az["error"] is { } e ? " - " + e : "")}");
            }, t));
    }

    private static async Task PrintConsent(MbrClient c, string tenantId, CancellationToken ct)
    {
        var urls = await c.GetAsync($"api/tenants/{tenantId}/consent-urls", ct);
        Console.WriteLine();
        Console.WriteLine("A Global Admin of the customer tenant must open these links:");
        Console.WriteLine($"  Backup app : {urls?["backup"] ?? "(backup app not configured on server)"}");
        Console.WriteLine($"  Restore app: {urls?["restore"] ?? "(restore app not configured on server)"}");
    }

    public static Command Storage()
    {
        var id = new Argument<string>("id");
        var name = new Option<string>("--name") { Required = true };
        var kind = new Option<string>("--kind") { Description = "S3 or Local", Required = true };
        var settings = new Option<string[]>("--set") { Description = "Setting key=value (e.g. bucket=my-backups region=eu-west-1)", AllowMultipleArgumentsPerToken = true };
        var secret = new Option<string?>("--secret") { Description = "Secret (S3 secret key or Azure connection string). Read from MBR_STORAGE_SECRET if omitted." };
        var isDefault = new Option<bool>("--default") { Description = "Make this the default storage target" };

        return Group("storage", "Manage storage targets (S3, or local disk in development)",
            Cmd("list", "List storage targets", async (pr, ct) =>
                Print(pr, await Client().GetAsync("api/storage-targets", ct), ("ID", "id"), ("Name", "name"), ("Kind", "kind"), ("Default", "isDefault"), ("Settings", "settings"))),
            Cmd("add", "Add a storage target", async (pr, ct) =>
            {
                var s = await Client().PostAsync("api/storage-targets", new
                {
                    name = pr.GetValue(name), kind = pr.GetValue(kind), settings = ParseParams(pr.GetValue(settings)),
                    secret = pr.GetValue(secret) ?? Environment.GetEnvironmentVariable("MBR_STORAGE_SECRET"), isDefault = pr.GetValue(isDefault),
                }, ct);
                PrintObject(pr, s, ("ID", "id"), ("Name", "name"), ("Kind", "kind"));
            }, name, kind, settings, secret, isDefault),
            Cmd("test", "Write, read and delete a test object", async (pr, ct) =>
            {
                var r = await Client().PostAsync($"api/storage-targets/{pr.GetValue(id)}/test", null, ct);
                if (WantsJson(pr)) { PrintJson(r); return; }
                foreach (var s in r!["steps"]!.AsArray()) Console.WriteLine($"{s!["name"],-8} {(s["ok"]!.GetValue<bool>() ? "ok" : "FAILED " + s["error"])}");
                Console.WriteLine(r["ok"]!.GetValue<bool>() ? "Storage target is usable." : "Storage target is NOT usable.");
            }, id),
            Cmd("import", "Rebuild tenants, snapshots and file and mail versions from what's in the target (after losing the database)", async (pr, ct) =>
            {
                var c = Client();
                var r = await c.PostAsync($"api/storage-targets/{pr.GetValue(id)}/import", null, ct);
                var done = await WatchJobAsync(c, r!["jobId"]!.GetValue<string>(), ct);
                if (WantsJson(pr)) PrintJson(done["result"]);
            }, id));
    }

    public static Command Job()
    {
        var t = TenantOption();
        var id = new Argument<string>("id");
        var type = new Argument<string>("type") { Description = "Job type (see: mbr job types)" };
        var param = new Option<string[]>("--param", "-p") { Description = "Parameter key=value", AllowMultipleArgumentsPerToken = true };
        var watch = new Option<bool>("--watch", "-w") { Description = "Follow the job until it finishes" };
        var status = new Option<string?>("--status");

        return Group("job", "Run and inspect jobs",
            Cmd("list", "List recent jobs", async (pr, ct) =>
            {
                var c = Client();
                var q = "api/jobs?take=50";
                if (pr.GetValue(t) is { } tn) q += "&tenantId=" + await c.ResolveTenantAsync(tn, ct);
                if (pr.GetValue(status) is { } s) q += "&status=" + s;
                Print(pr, await c.GetAsync(q, ct), ("ID", "id"), ("Type", "type"), ("Status", "status"), ("Progress", "progress"), ("Created", "createdAt"), ("By", "createdBy"));
            }, t, status),
            Cmd("types", "List job types", async (pr, ct) => Print(pr, await Client().GetAsync("api/jobs/types", ct), ("Type", "type"), ("Required role", "requiredRole"))),
            Cmd("show", "Show a job", async (pr, ct) =>
                PrintObject(pr, await Client().GetAsync($"api/jobs/{pr.GetValue(id)}", ct), ("ID", "id"), ("Type", "type"), ("Status", "status"),
                    ("Progress", "progress"), ("Message", "progressMessage"), ("Created", "createdAt"), ("Completed", "completedAt"), ("Error", "error"), ("Result", "result")), id),
            Cmd("watch", "Follow a job's log until it finishes", async (pr, ct) => await WatchJobAsync(Client(), pr.GetValue(id)!, ct), id),
            Cmd("cancel", "Cancel a job", async (pr, ct) =>
            {
                await Client().PostAsync($"api/jobs/{pr.GetValue(id)}/cancel", null, ct);
                Console.WriteLine("Cancellation requested.");
            }, id),
            Cmd("start", "Start a job of any type", async (pr, ct) =>
            {
                var c = Client();
                string? tenantId = pr.GetValue(t) is { } tn ? await c.ResolveTenantAsync(tn, ct) : null;
                var job = await c.PostAsync("api/jobs", new { type = pr.GetValue(type), tenantId, parameters = ParseParams(pr.GetValue(param)) }, ct);
                Console.WriteLine($"Started job {job!["id"]}");
                if (pr.GetValue(watch)) await WatchJobAsync(c, job["id"]!.GetValue<string>(), ct);
            }, type, t, param, watch));
    }

    public static Command Schedule()
    {
        var t = TenantOption();
        var id = new Argument<string>("id");
        var name = new Option<string>("--name") { Required = true };
        var jobType = new Option<string>("--job-type") { Description = "Job type, e.g. backup.config", Required = true };
        var cron = new Option<string>("--cron") { Description = "Cron expression or preset (hourly, every-4-hours, daily, twice-daily, weekly, monthly)", Required = true };
        var tz = new Option<string?>("--timezone") { Description = "IANA or Windows time zone (default UTC)" };
        var param = new Option<string[]>("--param", "-p") { Description = "Job parameter key=value", AllowMultipleArgumentsPerToken = true };
        var disabled = new Option<bool>("--disabled");

        return Group("schedule", "Manage scheduled jobs",
            Cmd("list", "List schedules", async (pr, ct) =>
            {
                var c = Client();
                var q = "api/schedules";
                if (pr.GetValue(t) is { } tn) q += "?tenantId=" + await c.ResolveTenantAsync(tn, ct);
                Print(pr, await c.GetAsync(q, ct), ("ID", "id"), ("Name", "name"), ("Job", "jobType"), ("Cron", "cron"), ("TZ", "timeZone"),
                    ("Enabled", "enabled"), ("Next run", "nextRunAt"), ("Last run", "lastRunAt"));
            }, t),
            Cmd("add", "Add a schedule", async (pr, ct) =>
            {
                var c = Client();
                var tid = await c.ResolveTenantAsync(pr.GetValue(t), ct);
                var s = await c.PostAsync($"api/tenants/{tid}/schedules", new
                {
                    name = pr.GetValue(name), jobType = pr.GetValue(jobType), cron = pr.GetValue(cron), timeZone = pr.GetValue(tz),
                    parameters = ParseParams(pr.GetValue(param)), enabled = !pr.GetValue(disabled),
                }, ct);
                PrintObject(pr, s, ("ID", "id"), ("Name", "name"), ("Cron", "cron"), ("Next run", "nextRunAt"));
            }, t, name, jobType, cron, tz, param, disabled),
            Cmd("preview", "Show the next run times of a cron expression", async (pr, ct) =>
            {
                var r = await Client().PostAsync("api/schedules/preview", new { cron = pr.GetValue(cron), timeZone = pr.GetValue(tz) }, ct);
                if (WantsJson(pr)) { PrintJson(r); return; }
                Console.WriteLine($"cron: {r!["cron"]}");
                foreach (var n in r["next"]!.AsArray()) Console.WriteLine("  " + DateTimeOffset.Parse(n!.GetValue<string>()).ToLocalTime());
            }, cron, tz),
            Cmd("run", "Run a schedule now", async (pr, ct) =>
            {
                var job = await Client().PostAsync($"api/schedules/{pr.GetValue(id)}/run", null, ct);
                Console.WriteLine($"Started job {job!["id"]}");
            }, id),
            Cmd("delete", "Delete a schedule", async (pr, ct) =>
            {
                await Client().SendAsync(HttpMethod.Delete, $"api/schedules/{pr.GetValue(id)}", null, ct);
                Console.WriteLine("Deleted.");
            }, id));
    }
}
