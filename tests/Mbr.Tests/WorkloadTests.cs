using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Infrastructure.Demo;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 8: Exchange, Defender, Purview and Teams (PowerShell) plus Teams and SharePoint settings (Graph).</summary>
public class WorkloadTests
{
    private static readonly string[] Workloads = ["exchange", "defender", "purview", "teams", "sharepoint"];

    private sealed record World(TestApp App, HttpClient Client, Mbr.Core.Model.Tenant Tenant, string SnapshotId, JsonNode Backup);

    private static async Task<World> SetUpAsync()
    {
        var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        FakeTenantData.Seed(app.Graph);
        DemoWorkloads.SeedGraph(app.Graph);
        DemoWorkloads.SeedPowerShell(app.PowerShell, tenant.DefaultDomain);
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = Workloads });
        Assert.True(job["status"]!.GetValue<string>() == "Succeeded", job.ToJsonString());
        return new World(app, client, tenant, job["result"]!["snapshotId"]!.GetValue<string>(), job);
    }

    private static async Task<JsonArray> ItemsAsync(World w, string type) =>
        JsonNode.Parse(await w.Client.GetStringAsync($"/api/snapshots/{w.SnapshotId}/items?type={type}"))!["items"]!.AsArray();

    private static async Task<JsonNode> ContentAsync(World w, JsonNode item) =>
        JsonNode.Parse(await w.Client.GetStringAsync($"/api/snapshots/{w.SnapshotId}/items/{item["id"]}"))!["content"]!;

    private static async Task<JsonNode> PlanAndApplyAsync(World w, object request, Action<List<JsonNode>>? inspect = null, Action<List<JsonNode>>? inspectAll = null)
    {
        var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.AsObject();
        payload["snapshotId"] = w.SnapshotId;
        var created = JsonNode.Parse(await (await w.Client.PostAsJsonAsync("/api/restore/plans", payload)).Content.ReadAsStringAsync())!;
        var planJob = await w.App.WaitForJobAsync(w.Client, created["jobId"]!.GetValue<string>());
        Assert.True(planJob["status"]!.GetValue<string>() == "Succeeded", $"{planJob["error"]}");
        var plan = JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        inspect?.Invoke(plan["actions"]!.AsArray().Select(a => a!).Where(a => a["kind"]!.GetValue<string>() != "Skip").ToList());
        inspectAll?.Invoke(plan["actions"]!.AsArray().Select(a => a!).ToList());
        var started = JsonNode.Parse(await (await w.Client.PostAsync($"/api/restore/plans/{created["planId"]}/apply", null)).Content.ReadAsStringAsync())!;
        await w.App.WaitForJobAsync(w.Client, started["jobId"]!.GetValue<string>());
        var applied = JsonNode.Parse(await w.Client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        Assert.True(applied["plan"]!["status"]!.GetValue<string>() == "Applied", Describe(applied));
        var issues = applied["actions"]!.AsArray().Where(a => a!["verificationIssues"]!.AsArray().Count > 0).ToList();
        Assert.True(issues.Count == 0, Describe(applied));
        return applied;
    }

    private static async Task<JsonNode?> GraphAsync(FakeGraph g, HttpMethod method, string path, JsonNode? body)
    {
        using var http = new HttpClient(g, disposeHandler: false) { BaseAddress = new Uri("https://graph.microsoft.com") };
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
    }

    private static string Describe(JsonNode plan) => string.Join("\n", plan["actions"]!.AsArray().Select(a =>
        $"{a!["kind"]} {a["type"]} {a["displayName"]} [{a["status"]}] {a["error"]} {a["reason"]} changes={string.Join(",", a["changes"]!.AsArray())} " +
        string.Join("; ", a["verificationIssues"]!.AsArray().Select(v => v!.ToString()))));

    [Fact]
    public async Task Backup_reads_each_powershell_module_once_and_captures_every_workload()
    {
        var w = await SetUpAsync();
        using var app = w.App;

        // One connected session per module, not one per type.
        var gets = app.PowerShell.Log.Where(l => l.Contains(" Get-")).ToList();
        Assert.Equal(1, gets.Count(l => l == "exo Get-TransportRule"));
        Assert.Equal(1, gets.Count(l => l == "ipps Get-DlpComplianceRule"));

        Assert.Equal(2, (await ItemsAsync(w, "exchange.transportRule")).Count);
        Assert.Single(await ItemsAsync(w, "exchange.organizationConfig"));
        Assert.Equal(2, (await ItemsAsync(w, "defender.antiSpamPolicy")).Count);
        Assert.Single(await ItemsAsync(w, "purview.dlpRule"));
        Assert.Equal(2, (await ItemsAsync(w, "teams.meetingPolicy")).Count);
        Assert.Single(await ItemsAsync(w, "teams.federationConfiguration"));
        Assert.Single(await ItemsAsync(w, "teams.team")); // the test tenant has one group
        Assert.Single(await ItemsAsync(w, "sharepoint.tenantSettings"));

        var rule = (await ItemsAsync(w, "exchange.transportRule")).Single(i => i!["displayName"]!.GetValue<string>() == "External sender warning")!;
        var content = await ContentAsync(w, rule);
        Assert.Equal("[EXTERNAL] ", content["PrependSubject"]!.GetValue<string>());
        Assert.Null(content["Identity"]?["RunspaceId"]);
        var team = await ContentAsync(w, (await ItemsAsync(w, "teams.team"))[0]!);
        Assert.Equal(3, team["channels"]!.AsArray().Count);
        var dkim = await ContentAsync(w, (await ItemsAsync(w, "exchange.dkimSigningConfig"))[0]!);
        Assert.Null(dkim["Selector1PublicKey"]);
    }

    [Fact]
    public async Task A_cmdlet_that_fails_is_reported_and_the_rest_is_still_captured()
    {
        var app = new TestApp();
        using var _ = app;
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        DemoWorkloads.SeedPowerShell(app.PowerShell, tenant.DefaultDomain);
        app.PowerShell.Failing["SafeLinksPolicy"] = "The term 'Get-SafeLinksPolicy' is not recognized (Defender for Office 365 isn't licensed).";
        var job = await app.RunJobAsync(client, "backup.config", tenant.Id, new { workloads = new[] { "defender" } });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.True(job["result"]!["warnings"]!.GetValue<int>() >= 1);
        var snapshot = job["result"]!["snapshotId"]!.GetValue<string>();
        var items = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{snapshot}/items?type=defender.antiPhishPolicy"))!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Deleted_mail_flow_rule_dlp_rule_and_safe_links_are_recreated()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var ps = app.PowerShell;
        ps.Objects("exo", "TransportRule").RemoveAll(r => r["Name"]!.GetValue<string>() == "Block executable attachments");
        ps.Objects("ipps", "DlpComplianceRule").Clear();
        ps.Objects("exo", "SafeLinksRule").Clear();
        ps.Objects("exo", "SafeLinksPolicy").Clear();

        await PlanAndApplyAsync(w, new { workloads = new[] { "exchange", "defender", "purview" } }, acts =>
        {
            Assert.All(acts, a => Assert.Equal("Create", a["kind"]!.GetValue<string>()));
            Assert.Equal(["defender.safeLinksPolicy", "defender.safeLinksRule", "exchange.transportRule", "purview.dlpRule"],
                acts.Select(a => a["type"]!.GetValue<string>()).Order().ToList());
        });

        var rule = ps.Find("exo", "TransportRule", "Block executable attachments")!;
        Assert.Equal(["exe", "js", "vbs", "scr"], rule["AttachmentExtensionMatchesWords"]!.AsArray().Select(x => x!.GetValue<string>()));
        var dlp = Assert.Single(ps.Objects("ipps", "DlpComplianceRule"));
        Assert.Equal("U.S. Financial Data", dlp["ParentPolicyName"]!.GetValue<string>()); // sent as -Policy
        Assert.Contains("exo create SafeLinksRule ", ps.Log);
        // The policy is created before the rule that applies it.
        var log = ps.Log.ToList();
        Assert.True(log.IndexOf("exo create SafeLinksPolicy ") < log.IndexOf("exo create SafeLinksRule "));
    }

    [Fact]
    public async Task Changed_settings_are_reset_with_only_the_changed_parameters()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var ps = app.PowerShell;
        ps.Objects("exo", "OrganizationConfig")[0]["FocusedInboxOn"] = false;
        ps.Find("exo", "HostedContentFilterPolicy", "Default")!["BulkThreshold"] = 9;
        ps.Objects("teams", "CsTenantFederationConfiguration")[0]["AllowedDomains"] = new JsonArray("fabrikam.com");

        await PlanAndApplyAsync(w, new { workloads = new[] { "exchange", "defender", "teams" } }, acts =>
        {
            Assert.All(acts, a => Assert.Equal("Update", a["kind"]!.GetValue<string>()));
            Assert.Equal(3, acts.Count);
        });

        Assert.True(ps.Objects("exo", "OrganizationConfig")[0]["FocusedInboxOn"]!.GetValue<bool>());
        Assert.Equal(7, ps.Find("exo", "HostedContentFilterPolicy", "Default")!["BulkThreshold"]!.GetValue<int>());
        Assert.Equal(2, ps.Objects("teams", "CsTenantFederationConfiguration")[0]["AllowedDomains"]!.AsArray().Count);
        Assert.Contains("exo update OrganizationConfig ", ps.Log);
    }

    [Fact]
    public async Task Deleted_teams_policy_is_recreated_but_global_and_default_policies_are_never_created()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var ps = app.PowerShell;
        ps.Objects("teams", "CsTeamsMeetingPolicy").Clear(); // Global can't really be removed, but the plan must not try to create it
        ps.Objects("exo", "HostedContentFilterPolicy").RemoveAll(p => p["Name"]!.GetValue<string>() == "Default");

        await PlanAndApplyAsync(w, new { types = new[] { "teams.meetingPolicy", "defender.antiSpamPolicy" } }, inspectAll: actions =>
        {
            var global = actions.Single(a => a["displayName"]!.GetValue<string>() == "Global");
            Assert.Equal("Skip", global["kind"]!.GetValue<string>());
            Assert.Contains("Global policy", global["reason"]!.GetValue<string>());
            var spamDefault = actions.Single(a => a["type"]!.GetValue<string>() == "defender.antiSpamPolicy" && a["displayName"]!.GetValue<string>() == "Default");
            Assert.Equal("Skip", spamDefault["kind"]!.GetValue<string>());
            Assert.Equal("Create", actions.Single(a => a["displayName"]!.GetValue<string>() == "Tag:Executives")["kind"]!.GetValue<string>());
        });
        var exec = ps.Find("teams", "CsTeamsMeetingPolicy", "Tag:Executives");
        Assert.NotNull(exec);
        Assert.False(exec!["AllowCloudRecording"]!.GetValue<bool>());
        Assert.Contains("teams create CsTeamsMeetingPolicy ", ps.Log);
    }

    [Fact]
    public async Task Team_settings_channels_and_sharepoint_settings_are_restored()
    {
        var w = await SetUpAsync();
        using var app = w.App;
        var g = app.Graph;
        var team = g.Collection("teams")[0];
        var teamId = team["id"]!.GetValue<string>();
        team["funSettings"]!["allowGiphy"] = false;
        g.Collection($"teams/{teamId}/channels").RemoveAll(c => c["displayName"]!.GetValue<string>() == "Projects");
        await GraphAsync(g, HttpMethod.Patch, "/v1.0/admin/sharepoint/settings", new JsonObject { ["sharingCapability"] = "externalUserAndGuestSharing" });

        await PlanAndApplyAsync(w, new { workloads = new[] { "teams", "sharepoint" } }, acts =>
        {
            Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "teams.team" && a["kind"]!.GetValue<string>() == "Update");
            Assert.Contains(acts, a => a["type"]!.GetValue<string>() == "sharepoint.tenantSettings" && a["kind"]!.GetValue<string>() == "Update");
        });

        Assert.True(g.Collection("teams")[0]["funSettings"]!["allowGiphy"]!.GetValue<bool>());
        Assert.Contains(g.Collection($"teams/{teamId}/channels"), c => c["displayName"]!.GetValue<string>() == "Projects");
        Assert.Equal(3, g.Collection($"teams/{teamId}/channels").Count);
        var sp = await GraphAsync(g, HttpMethod.Get, "/v1.0/admin/sharepoint/settings", null);
        Assert.Equal("externalUserSharingOnly", sp!["sharingCapability"]!.GetValue<string>());
    }
}
