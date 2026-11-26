using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Infrastructure.Demo;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 9: Microsoft 365 Backup Storage integration.</summary>
public class AdvancedTests
{
    [Fact]
    public async Task M365_backup_shows_policies_units_and_restore_points_and_runs_native_restores()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var seeded = FakeTenantData.Seed(app.Graph);
        DemoM365Backup.Seed(app.Graph, [(seeded.UserId, "adele@contoso.com")], [(seeded.UserId, "adele@contoso.com")],
            [("contoso.sharepoint.com,1111,2222", "Finance")]);

        var status = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/m365backup"))!;
        Assert.True(status["available"]!.GetValue<bool>());
        var policies = status["policies"]!.AsArray();
        Assert.Equal(3, policies.Count);
        Assert.All(policies, p => Assert.Equal(1, p!["units"]!.GetValue<int>()));
        Assert.Equal(["exchange", "oneDrive", "sharePoint"], policies.Select(p => p!["kind"]!.GetValue<string>()).Order().ToList());

        var units = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/m365backup/units"))!.AsArray();
        var mailbox = units.Single(u => u!["kind"]!.GetValue<string>() == "exchange")!;
        Assert.Equal("adele@contoso.com", mailbox["displayName"]!.GetValue<string>());
        Assert.Equal("Finance", units.Single(u => u!["kind"]!.GetValue<string>() == "sharePoint")!["displayName"]!.GetValue<string>());

        var unitId = mailbox["id"]!.GetValue<string>();
        var points = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/m365backup/units/{unitId}/restorePoints"))!.AsArray();
        Assert.Equal(3, points.Count);
        Assert.True(points[0]!["protectionDateTime"]!.GetValue<DateTimeOffset>() > points[1]!["protectionDateTime"]!.GetValue<DateTimeOffset>());

        var resp = await client.PostAsJsonAsync($"/api/tenants/{tenant.Id}/m365backup/restore",
            new { unitId, restorePointId = points[1]!["id"]!.GetValue<string>(), inPlace = false });
        resp.EnsureSuccessStatusCode();
        var session = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("exchange", session["kind"]!.GetValue<string>());
        Assert.Equal("completed", session["status"]!.GetValue<string>());
        var stored = app.Graph.Collection("solutions/backupRestore/restoreSessions").Single();
        Assert.Equal("new", stored["mailboxRestoreArtifacts"]![0]!["destinationType"]!.GetValue<string>());
        Assert.Equal(points[1]!["id"]!.GetValue<string>(), stored["mailboxRestoreArtifacts"]![0]!["restorePoint"]!["id"]!.GetValue<string>());
        Assert.Contains(app.Db(db => db.AuditEntries.ToList()), a => a.Action == "m365backup.restore");

        // A tenant without the service is reported as such rather than failing.
        var other = await app.AddTenantAsync("Fabrikam");
        app.TenantGraphs[other.DirectoryId] = new FakeGraph();
        var none = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{other.Id}/m365backup"))!;
        Assert.False(none["available"]!.GetValue<bool>());
        Assert.Contains("isn't set up", none["message"]!.GetValue<string>());
    }
}
