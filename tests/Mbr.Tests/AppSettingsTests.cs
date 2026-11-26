using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Infrastructure;
using Mbr.Infrastructure.Settings;
using Mbr.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mbr.Tests;

public class AppSettingsTests
{
    private const string Partner = "Mbr:Partner:TenantId";
    private const string Webhooks = "Mbr:Notifications:Webhooks";

    private static JsonNode Setting(JsonArray all, string key) => all.First(s => s!["key"]!.GetValue<string>() == key)!;

    private static async Task<JsonArray> Put(HttpClient client, object values)
    {
        var resp = await client.PutAsJsonAsync("/api/app-settings", new { values });
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsArray();
    }

    [Fact]
    public async Task A_setting_changed_in_the_app_overrides_the_deployment_until_it_is_reset()
    {
        var deployment = Guid.NewGuid().ToString();
        var inApp = Guid.NewGuid().ToString();
        using var app = new TestApp(b => b.UseSetting(Partner, deployment));
        var client = app.CreateClient();
        MbrOptions Options() => app.Services.GetRequiredService<IOptions<MbrOptions>>().Value;

        var all = JsonNode.Parse(await client.GetStringAsync("/api/app-settings"))!.AsArray();
        Assert.Equal(deployment, Setting(all, Partner)["value"]!.GetValue<string>());
        Assert.False(Setting(all, Partner)["overridden"]!.GetValue<bool>());

        all = await Put(client, new Dictionary<string, object?> { [Partner] = inApp, ["Mbr:Worker:MaxJobsPerTenant"] = 5 });
        Assert.True(Setting(all, Partner)["overridden"]!.GetValue<bool>());
        Assert.Equal(deployment, Setting(all, Partner)["deploymentValue"]!.GetValue<string>());
        Assert.Equal(inApp, Options().Partner.TenantId);
        Assert.Equal(5, Options().Worker.MaxJobsPerTenant);

        // Survives a restart of the app (it's in the database).
        Options().Partner.TenantId = "stale";
        await app.Services.GetRequiredService<RuntimeSettings>().ReloadAsync();
        Assert.Equal(inApp, Options().Partner.TenantId);

        await Put(client, new Dictionary<string, object?> { [Partner] = null });
        Assert.Equal(deployment, Options().Partner.TenantId);
        Assert.Equal(5, Options().Worker.MaxJobsPerTenant);
        Assert.Contains(app.Db(db => db.AuditEntries.ToList()), a => a.Action == "settings.update");
    }

    [Fact]
    public async Task Secrets_are_stored_encrypted_and_never_returned()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var url = "https://prod-00.westeurope.logic.azure.com/workflows/abc?sig=secret";

        var all = await Put(client, new Dictionary<string, object?> { [Webhooks] = new[] { url, " ", url } });
        var view = Setting(all, Webhooks);
        Assert.Null(view["value"]);
        Assert.True(view["isSet"]!.GetValue<bool>());
        Assert.DoesNotContain("sig=secret", all.ToJsonString());

        Assert.Equal([url], app.Services.GetRequiredService<IOptions<MbrOptions>>().Value.Notifications.Webhooks);
        var row = Assert.Single(app.Db(db => db.AppSettings.Where(s => s.Key == Webhooks).ToList()));
        Assert.True(row.Encrypted);
        Assert.DoesNotContain("sig=secret", row.Value);
    }

    [Fact]
    public async Task Invalid_values_are_rejected_and_nothing_is_saved()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var resp = await client.PutAsJsonAsync("/api/app-settings", new
        {
            values = new Dictionary<string, object?> { ["Mbr:Worker:MaxConcurrency"] = 8, [Webhooks] = new[] { "http://not-https.example" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(app.Db(db => db.AppSettings.Where(s => !s.Key.StartsWith("_")).ToList()));

        resp = await client.PutAsJsonAsync("/api/app-settings", new { values = new Dictionary<string, object?> { ["Mbr:Database:ConnectionString"] = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        resp = await client.PutAsJsonAsync("/api/app-settings", new { values = new Dictionary<string, object?> { ["Mbr:Worker:MaxConcurrency"] = 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Edits_to_the_deployment_storage_target_in_the_app_are_kept()
    {
        using var app = new TestApp(b =>
        {
            b.UseSetting("Mbr:Storage:DefaultS3:Bucket", "contoso-mbr-backups");
            b.UseSetting("Mbr:Storage:DefaultS3:Region", "eu-west-1");
            b.UseSetting("Mbr:Storage:DefaultS3:Credentials", "Default");
        });
        var client = app.CreateClient();
        var t = Assert.Single(JsonNode.Parse(await client.GetStringAsync("/api/storage-targets"))!.AsArray())!;
        var settings = t["settings"]!.AsObject();
        settings["prefix"] = "edited-in-app";
        (await client.PutAsJsonAsync($"/api/storage-targets/{t["id"]}", new { name = t["name"]!.GetValue<string>(), kind = "S3", settings })).EnsureSuccessStatusCode();

        // A later start with different configuration leaves the app's version alone.
        app.Services.GetRequiredService<IOptions<MbrOptions>>().Value.Storage.DefaultS3.RoleArn = "arn:aws:iam::123456789012:role/mbr-backup";
        await app.Services.InitializeMbrDatabaseAsync();
        var stored = Assert.Single(app.Db(db => db.StorageTargets.ToList()));
        Assert.Contains("edited-in-app", stored.SettingsJson);
        Assert.DoesNotContain("arn:aws:iam", stored.SettingsJson);
    }
}
