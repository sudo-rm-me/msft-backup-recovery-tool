using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Mbr.Core.Model;
using Mbr.Tests.Fakes;

namespace Mbr.Tests;

/// <summary>Phase 9: protection report, worker fleet and per-tenant job limits.</summary>
public class ReportingTests
{
    [Fact]
    public async Task Protection_report_flags_unprotected_and_at_risk_tenants_and_exports_csv()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var backedUp = await app.AddTenantAsync("Contoso");
        var never = await app.AddTenantAsync("Fabrikam");
        FakeTenantData.Seed(app.Graph);
        await app.RunJobAsync(client, "backup.config", backedUp.Id, new { workloads = new[] { "entra", "intune" } });

        var report = JsonNode.Parse(await client.GetStringAsync("/api/reports/protection"))!;
        Assert.Equal(2, report["summary"]!["tenants"]!.GetValue<int>());
        var rows = report["tenants"]!.AsArray();
        var contoso = rows.Single(r => r!["tenant"]!.GetValue<string>() == "Contoso")!;
        Assert.Equal("AtRisk", contoso["health"]!.GetValue<string>()); // backed up, but nothing is scheduled
        Assert.Contains(contoso["problems"]!.AsArray(), p => p!.GetValue<string>().Contains("scheduled"));
        Assert.Equal(["entra", "intune"], contoso["workloads"]!.AsArray().Select(w => w!["workload"]!.GetValue<string>()).Order().ToList());
        Assert.Contains("azure", contoso["workloadsNeverBackedUp"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.True(contoso["workloads"]![0]!["items"]!.GetValue<int>() > 0);
        var fabrikam = rows.Single(r => r!["tenant"]!.GetValue<string>() == "Fabrikam")!;
        Assert.Equal("Unprotected", fabrikam["health"]!.GetValue<string>());

        var one = JsonNode.Parse(await client.GetStringAsync($"/api/reports/protection?tenantId={never.Id}"))!;
        Assert.Single(one["tenants"]!.AsArray());

        var csv = await client.GetAsync("/api/reports/protection?format=csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        var lines = text.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Tenant,Health,", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("Fabrikam,Unprotected,"));
    }

    [Fact]
    public async Task Workers_report_in_and_one_tenant_never_gets_more_than_its_share()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var busy = await app.AddTenantAsync("Busy");
        var other = await app.AddTenantAsync("Other");

        // Four slow jobs for one tenant and one for another: with MaxJobsPerTenant = 2 the third and fourth wait,
        // and the other tenant's job still starts right away.
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
            ids.Add((await app.StartJobAsync(client, "system.diagnostics", busy.Id, new { steps = 3, delayMs = 700 }))["id"]!.GetValue<string>());
        var otherJob = (await app.StartJobAsync(client, "system.diagnostics", other.Id, new { steps = 1, delayMs = 100 }))["id"]!.GetValue<string>();

        var maxBusy = 0;
        var otherStartedWhileBusyQueued = false;
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var jobs = app.Db(db => db.Jobs.Where(j => j.TenantId == busy.Id || j.TenantId == other.Id).ToList());
            maxBusy = Math.Max(maxBusy, jobs.Count(j => j.TenantId == busy.Id && j.Status == JobStatus.Running));
            if (jobs.Any(j => j.TenantId == other.Id && j.Status != JobStatus.Queued) && jobs.Any(j => j.TenantId == busy.Id && j.Status == JobStatus.Queued))
                otherStartedWhileBusyQueued = true;
            if (jobs.All(j => j.Status == JobStatus.Succeeded)) break;
            await Task.Delay(100);
        }
        Assert.Equal(2, maxBusy);
        Assert.True(otherStartedWhileBusyQueued);
        foreach (var id in ids.Append(otherJob))
            Assert.Equal("Succeeded", (await app.WaitForJobAsync(client, id))["status"]!.GetValue<string>());

        var workers = JsonNode.Parse(await client.GetStringAsync("/api/workers"))!;
        var w = Assert.Single(workers["workers"]!.AsArray())!;
        Assert.Equal(Environment.MachineName, w["host"]!.GetValue<string>());
        Assert.Equal(0, workers["queued"]!.GetValue<int>());
    }
}

/// <summary>Phase 9: importing customers from GDAP relationships.</summary>
public class GdapTests
{
    [Fact]
    public async Task Customers_are_listed_from_gdap_relationships_and_imported_once()
    {
        using var app = new TestApp(configure: b => b.UseSetting("Mbr:Partner:TenantId", "aaaaaaaa-0000-4000-8000-000000000001"));
        var client = app.CreateClient();
        Mbr.Infrastructure.Demo.DemoData.SeedGdap(app.Graph);

        var customers = JsonNode.Parse(await client.GetStringAsync("/api/partner/customers"))!.AsArray();
        Assert.Equal(2, customers.Count);
        var northwind = customers.Single(c => c!["displayName"]!.GetValue<string>() == "Northwind Traders")!;
        Assert.True(northwind["active"]!.GetValue<bool>());
        Assert.True(northwind["canGrantConsent"]!.GetValue<bool>()); // has Privileged Role Administrator
        Assert.Contains("Exchange Administrator", northwind["relationships"]![0]!["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        var tailspin = customers.Single(c => c!["displayName"]!.GetValue<string>() == "Tailspin Toys")!;
        Assert.False(tailspin["canGrantConsent"]!.GetValue<bool>());

        var ids = customers.Select(c => c!["tenantId"]!.GetValue<string>()).ToArray();
        var resp = await client.PostAsJsonAsync("/api/partner/customers/import", new { tenantIds = ids });
        resp.EnsureSuccessStatusCode();
        var added = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(2, added.Count);
        Assert.Contains(added, t => t!["defaultDomain"]!.GetValue<string>() == "northwindtraders.onmicrosoft.com");

        // Importing again adds nothing; the list now shows them as onboarded.
        var again = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/partner/customers/import", new { tenantIds = ids })).Content.ReadAsStringAsync())!.AsArray();
        Assert.Empty(again);
        customers = JsonNode.Parse(await client.GetStringAsync("/api/partner/customers"))!.AsArray();
        Assert.All(customers, c => Assert.NotNull(c!["onboardedTenantId"]));

        var bad = await client.PostAsJsonAsync("/api/partner/customers/import", new { tenantIds = new[] { Guid.NewGuid().ToString() } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
