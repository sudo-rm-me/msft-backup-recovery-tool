using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Restore;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Restoring into another tenant (e.g. a replacement for a lost one): domain mapping, and files and mail.</summary>
public class CrossTenantTests
{
    [Fact]
    public void Domain_map_rewrites_addresses_and_bare_domains_only()
    {
        var map = new DomainMap(new Dictionary<string, string> { ["contoso.com"] = "fabrikam.com", ["@Old.Contoso.com."] = "new.fabrikam.com" });

        Assert.Equal("alice@fabrikam.com", map.Apply("alice@Contoso.com"));
        Assert.Equal("SMTP:alice@fabrikam.com", map.Apply("SMTP:alice@contoso.com"));
        Assert.Equal("bob@new.fabrikam.com", map.Apply("bob@old.contoso.com"));
        Assert.Equal("fabrikam.com", map.Apply("contoso.com"));
        Assert.Equal("carol@contoso.com.au", map.Apply("carol@contoso.com.au")); // a different domain
        Assert.Equal("Contoso.com marketing", map.Apply("Contoso.com marketing"));  // not an address

        var doc = (JsonObject)map.Apply(JsonNode.Parse("""{"upn":"a@contoso.com","proxyAddresses":["smtp:a@contoso.com"],"n":1}"""))!;
        Assert.Equal("a@fabrikam.com", doc["upn"]!.GetValue<string>());
        Assert.Equal("smtp:a@fabrikam.com", doc["proxyAddresses"]![0]!.GetValue<string>());
        Assert.Equal(1, doc["n"]!.GetValue<int>());

        Assert.Throws<ArgumentException>(() => new DomainMap(new Dictionary<string, string> { ["contoso"] = "fabrikam.com" }));
    }

    [Fact]
    public async Task Users_are_restored_into_another_tenant_with_mapped_domains()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var source = await app.AddTenantAsync("Contoso");
        var target = await app.AddTenantAsync("Fabrikam");
        FakeTenantData.Seed(app.Graph);
        app.Graph.Add("users", new JsonObject
        {
            ["userPrincipalName"] = "lee@contoso.com", ["displayName"] = "Lee Gu", ["accountEnabled"] = true, ["mailNickname"] = "lee",
            ["mail"] = "lee@contoso.com", ["proxyAddresses"] = new JsonArray("SMTP:lee@contoso.com"),
        });
        var targetGraph = new FakeGraph();
        app.TenantGraphs[target.DirectoryId] = targetGraph;
        targetGraph.Collection("users");
        var backup = await app.RunJobAsync(client, "backup.config", source.Id, new { workloads = new[] { "entra" } });
        var snapshotId = backup["result"]!["snapshotId"]!.GetValue<string>();

        // An invalid map is refused up front.
        var bad = await client.PostAsJsonAsync("/api/restore/plans", new
        {
            snapshotId, targetTenantId = target.Id, types = new[] { "entra.user" }, options = new { domainMap = new Dictionary<string, string> { ["contoso"] = "x" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/restore/plans", new
        {
            snapshotId, targetTenantId = target.Id, types = new[] { "entra.user" },
            options = new { domainMap = new Dictionary<string, string> { ["contoso.com"] = "fabrikam.com" } },
        })).Content.ReadAsStringAsync())!;
        await app.WaitForJobAsync(client, created["jobId"]!.GetValue<string>());
        var plan = JsonNode.Parse(await client.GetStringAsync($"/api/restore/plans/{created["planId"]}"))!;
        var notes = string.Join("\n", plan["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("contoso.com → fabrikam.com", notes);
        Assert.Contains("contoso.onmicrosoft.com → fabrikam.onmicrosoft.com", notes); // automatic for cross-tenant

        var started = JsonNode.Parse(await (await client.PostAsync($"/api/restore/plans/{created["planId"]}/apply", null)).Content.ReadAsStringAsync())!;
        await app.WaitForJobAsync(client, started["jobId"]!.GetValue<string>());

        var users = targetGraph.Collection("users");
        Assert.Equal(["adele@fabrikam.onmicrosoft.com", "lee@fabrikam.com"],
            users.Select(u => u["userPrincipalName"]!.GetValue<string>()).Order());
        var lee = users.Single(u => u["displayName"]!.GetValue<string>() == "Lee Gu");
        Assert.Equal("lee@fabrikam.com", lee["mail"]!.GetValue<string>());
        Assert.Equal("SMTP:lee@fabrikam.com", lee["proxyAddresses"]![0]!.GetValue<string>());
        Assert.DoesNotContain(app.Graph.RequestLog, r => !r.StartsWith("GET")); // nothing written to the source
    }

    [Fact]
    public async Task Mail_is_restored_into_the_same_user_in_another_tenant()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var source = await app.AddTenantAsync("Contoso");
        var target = await app.AddTenantAsync("Fabrikam");
        var seeded = FakeTenantData.Seed(app.Graph);
        app.Graph.Collection("users").Single()["mail"] = "adele@contoso.onmicrosoft.com";
        var mb = app.Graph.AddMailbox(seeded.UserId);
        mb.AddFolder("/Inbox/Projects");
        mb.AddMessage("/Inbox/Projects", "Project plan", "See attached");
        await app.RunJobAsync(client, "backup.data", source.Id, new { kinds = new[] { "Mailbox" } });

        // The replacement tenant: the same person under the new tenant's domain, discovered by a backup there.
        var targetGraph = new FakeGraph();
        app.TenantGraphs[target.DirectoryId] = targetGraph;
        var newUser = targetGraph.Add("users", new JsonObject
        {
            ["userPrincipalName"] = "adele@fabrikam.onmicrosoft.com", ["displayName"] = "Adele Vance", ["mail"] = "adele@fabrikam.onmicrosoft.com", ["accountEnabled"] = true,
        });
        var targetMailbox = targetGraph.AddMailbox(newUser["id"]!.GetValue<string>());
        await app.RunJobAsync(client, "backup.data", target.Id, new { kinds = new[] { "Mailbox" } });

        var sources = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{source.Id}/data/sources?kind=Mailbox"))!.AsArray();
        var detail = JsonNode.Parse(await client.GetStringAsync($"/api/data/sources/{sources.Single()!["id"]}"))!;
        var mail = detail["containers"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Mail")!["id"]!.GetValue<string>();
        var root = JsonNode.Parse(await client.GetStringAsync($"/api/data/containers/{mail}/children"))!["items"]!.AsArray();
        var inbox = root.Single(i => i!["name"]!.GetValue<string>() == "Inbox")!["itemId"]!.GetValue<string>();

        // An unknown target tenant is refused.
        var missing = await client.PostAsJsonAsync($"/api/tenants/{source.Id}/data/restore", new { containerId = mail, itemIds = new[] { inbox }, targetTenantId = Guid.NewGuid() });
        Assert.False(missing.IsSuccessStatusCode);

        var resp = await client.PostAsJsonAsync($"/api/tenants/{source.Id}/data/restore", new { containerId = mail, itemIds = new[] { inbox }, targetTenantId = target.Id });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var accepted = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("Adele Vance", accepted["target"]!.GetValue<string>());
        var job = await app.WaitForJobAsync(client, accepted["jobId"]!.GetValue<string>());
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());

        var restored = Assert.Single(targetMailbox.In("/Inbox/Projects")); // original paths, in the new tenant
        Assert.Equal("Project plan", restored.Subject);
        Assert.DoesNotContain(app.Graph.RequestLog, r => !r.StartsWith("GET")); // nothing written to the source
    }
}
