using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure;
using Mbr.Infrastructure.Api;
using Mbr.Infrastructure.Jobs;
using Mbr.Infrastructure.Security;
using Mbr.Infrastructure.Storage;
using Mbr.Infrastructure.Tenants;
using Mbr.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mbr.Tests;

public class ApiClientTests
{
    private static ApiClient Client(FakeGraph g) =>
        new(new HttpClient(g), ApiKind.Graph, "https://graph.microsoft.com", _ => Task.FromResult("t"), new SemaphoreSlim(4), NullLogger.Instance)
        { Delay = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task List_follows_next_links()
    {
        var g = new FakeGraph { PageSize = 3 };
        for (var i = 0; i < 10; i++) g.Add("groups", new JsonObject { ["displayName"] = $"g{i}" });
        var items = await Client(g).ListAsync("/v1.0/groups").ToListAsync();
        Assert.Equal(10, items.Count);
        Assert.Equal(4, g.RequestLog.Count);
    }

    [Fact]
    public async Task Retries_throttled_requests_honouring_retry_after()
    {
        var g = new FakeGraph();
        var calls = 0;
        g.On(HttpMethod.Get, "/v1.0/organization", (_, _) =>
        {
            if (++calls < 3)
            {
                var r = FakeGraph.Error((HttpStatusCode)429, "TooManyRequests", "slow down");
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return r;
            }
            return FakeGraph.Json(new JsonObject { ["value"] = new JsonArray() });
        });
        var waits = new List<TimeSpan>();
        var client = Client(g);
        client.Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; };
        await client.GetAsync("/v1.0/organization");
        Assert.Equal(3, calls);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(1), w));
    }

    [Fact]
    public async Task Errors_carry_graph_error_code()
    {
        var g = new FakeGraph();
        g.On(HttpMethod.Get, "/v1.0/me", (_, _) => FakeGraph.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "nope"));
        var e = await Assert.ThrowsAsync<ApiException>(() => Client(g).GetAsync("/v1.0/me"));
        Assert.Equal("Authorization_RequestDenied", e.ErrorCode);
        Assert.Null(await Client(g).TryGetAsync("/v1.0/doesnotexist"));
    }

    [Fact]
    public async Task Delta_returns_items_and_delta_link()
    {
        var g = new FakeGraph();
        g.On(HttpMethod.Get, "/v1.0/users/delta", (r, _) => r.RequestUri!.Query.Contains("page=2")
            ? FakeGraph.Json(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = "2" }), ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/users/delta?token=abc" })
            : FakeGraph.Json(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = "1" }), ["@odata.nextLink"] = "https://graph.microsoft.com/v1.0/users/delta?page=2" }));
        var r = await Client(g).DeltaAsync("/v1.0/users/delta");
        Assert.Equal(2, r.Items.Count);
        Assert.EndsWith("token=abc", r.DeltaLink);
    }
}

public class CryptoAndStorageTests
{
    [Fact]
    public void Aead_round_trips_and_detects_tampering()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var data = "hello"u8.ToArray();
        var enc = Aead.Encrypt(key, data);
        Assert.Equal(data, Aead.Decrypt(key, enc));
        enc[^1] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => Aead.Decrypt(key, enc));
    }

    [Fact]
    public async Task Local_blob_store_rejects_path_traversal()
    {
        var store = new LocalBlobStore(Path.Combine(Path.GetTempPath(), "mbr-tests", Guid.NewGuid().ToString("N")));
        await store.PutAsync("a/b.txt", new MemoryStream("x"u8.ToArray()));
        Assert.True(await store.ExistsAsync("a/b.txt"));
        Assert.Equal(["a/b.txt"], await store.ListAsync("a/").ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("../escape.txt", new MemoryStream()));
    }

    [Fact]
    public async Task Content_store_encrypts_and_deduplicates()
    {
        using var app = new TestApp();
        _ = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var store = app.Services.GetRequiredService<ITenantContentStore>();
        var targetId = app.Db(db => db.StorageTargets.First().Id);
        var content = "{\"secret\":\"config\"}"u8.ToArray();

        var h1 = await store.PutObjectAsync(tenant, targetId, content);
        var h2 = await store.PutObjectAsync(tenant, targetId, content);
        Assert.Equal(h1, h2);
        Assert.Equal(1, app.Db(db => db.StoredObjects.Count()));
        Assert.Equal(content, await store.GetObjectAsync(tenant, targetId, h1));

        var file = Directory.EnumerateFiles(Path.Combine(app.DataDir, "storage"), h1, SearchOption.AllDirectories).Single();
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));

        // The wrapped data key is kept next to the backups, and unwraps (with the master key) to the key that encrypted them.
        var escrow = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(app.DataDir, "storage", TenantContentStore.KeyEscrowKey(tenant.Id))))!;
        Assert.Equal(tenant.DirectoryId, escrow["directoryId"]!.GetValue<string>());
        var wrapper = app.Services.GetRequiredService<IKeyWrapper>();
        var key = await wrapper.UnwrapAsync(Convert.FromBase64String(escrow["wrappedDataKey"]!.GetValue<string>()));
        Assert.Equal(content, Aead.Decrypt(key, await File.ReadAllBytesAsync(file)));
    }
}

public class JobAndScheduleTests
{
    [Fact]
    public async Task Diagnostics_job_runs_to_completion_with_logs()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var job = await app.RunJobAsync(client, "system.diagnostics", tenant.Id, new { steps = 3, delayMs = 10 });
        Assert.Equal("Succeeded", job["status"]!.GetValue<string>());
        Assert.Equal(1.0, job["progress"]!.GetValue<double>());
        var logs = JsonNode.Parse(await client.GetStringAsync($"/api/jobs/{job["id"]}/logs"))!.AsArray();
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>() == "Step 3 of 3");
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var job = await app.StartJobAsync(client, "system.diagnostics", tenant.Id, new { steps = 100, delayMs = 200 });
        var id = job["id"]!.GetValue<string>();
        for (var i = 0; i < 40 && JsonNode.Parse(await client.GetStringAsync($"/api/jobs/{id}"))!["status"]!.GetValue<string>() != "Running"; i++)
            await Task.Delay(250);
        (await client.PostAsync($"/api/jobs/{id}/cancel", null)).EnsureSuccessStatusCode();
        var done = await app.WaitForJobAsync(client, id);
        Assert.Equal("Cancelled", done["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scheduler_enqueues_due_schedules_and_skips_overlaps()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var resp = await client.PostAsJsonAsync($"/api/tenants/{tenant.Id}/schedules",
            new { name = "nightly", jobType = "system.diagnostics", cron = "daily", timeZone = "UTC", parameters = new { steps = 50, delayMs = 200 } });
        resp.EnsureSuccessStatusCode();
        var schedule = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("0 2 * * *", schedule["cron"]!.GetValue<string>());

        var scheduler = app.Services.GetRequiredService<SchedulerService>();
        var future = DateTimeOffset.UtcNow.AddDays(2);
        await scheduler.TickAsync(future, CancellationToken.None);
        await scheduler.TickAsync(future.AddDays(1), CancellationToken.None); // previous run still active → skipped
        Assert.Equal(1, app.Db(db => db.Jobs.Count(j => j.ScheduleId == Guid.Parse(schedule["id"]!.GetValue<string>()))));
        Assert.True(app.Db(db => db.Schedules.Single().NextRunAt) > future);
    }

    [Fact]
    public void Cron_presets_and_time_zones()
    {
        Assert.True(CronHelper.TryValidate("weekly", "UTC", out _));
        Assert.False(CronHelper.TryValidate("not a cron", "UTC", out var err));
        Assert.NotNull(err);
        var next = CronHelper.Next("0 2 * * *", "UTC", new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 2, 0, 0, TimeSpan.Zero), next);
    }
}

public class OnboardingTests
{
    [Fact]
    public async Task Permission_check_reports_missing_permissions()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        app.Tokens.Roles = role => role == AppRole.Backup ? ["Directory.Read.All", "Policy.Read.All"] : [];
        app.Arm.Add("subscriptions", new JsonObject { ["subscriptionId"] = "sub1", ["displayName"] = "Prod", ["state"] = "Enabled" });

        var resp = await client.PostAsync($"/api/tenants/{tenant.Id}/permission-check", null);
        resp.EnsureSuccessStatusCode();
        var r = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.True(r["backup"]!["tokenAcquired"]!.GetValue<bool>());
        Assert.Contains(r["backup"]!["missing"]!.AsArray(), m => m!["permission"]!.GetValue<string>() == "DeviceManagementConfiguration.Read.All");
        Assert.DoesNotContain(r["backup"]!["missing"]!.AsArray(), m => m!["permission"]!.GetValue<string>() == "Directory.Read.All");
        Assert.Single(r["azure"]!["subscriptions"]!.AsArray());
    }

    [Fact]
    public void ReadWrite_permission_satisfies_read()
    {
        var jwt = FakeTokenProvider.MakeJwt(["Files.ReadWrite.All"]);
        Assert.Contains("Files.ReadWrite.All", TenantOnboardingService.ReadRoles(jwt));
    }

    [Fact]
    public async Task Consent_urls_are_signed_and_storage_test_works()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var tenant = await app.AddTenantAsync();
        var urls = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{tenant.Id}/consent-urls"))!;
        Assert.Contains($"/{tenant.DirectoryId}/adminconsent", urls["backup"]!.GetValue<string>());
        // The signing keys live in the database, so a callback can land on any replica or a later revision.
        Assert.NotEmpty(app.Db(db => db.DataProtectionKeys.ToList()));

        var bad = await client.GetAsync("/api/onboarding/consent-callback?admin_consent=True&state=forged");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var targets = JsonNode.Parse(await client.GetStringAsync("/api/storage-targets"))!.AsArray();
        var test = JsonNode.Parse(await (await client.PostAsync($"/api/storage-targets/{targets[0]!["id"]}/test", null)).Content.ReadAsStringAsync())!;
        Assert.True(test["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task S3_storage_target_validation()
    {
        using var app = new TestApp();
        var client = app.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/storage-targets", new { name = "s3", kind = "S3", settings = new { region = "eu-west-1" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var ok = await client.PostAsJsonAsync("/api/storage-targets", new
        {
            name = "s3", kind = "S3", settings = new { bucket = "backups", region = "eu-west-1", credentials = "Static", accessKeyId = "AKIA" }, secret = "s3cr3t",
        });
        ok.EnsureSuccessStatusCode();
        var dto = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        Assert.True(dto["hasSecret"]!.GetValue<bool>());
        Assert.DoesNotContain("s3cr3t", dto.ToJsonString());
        Assert.NotEqual("s3cr3t", app.Db(db => db.StorageTargets.Single(s => s.Name == "s3").EncryptedSecret));
    }
}

public class DeploymentTests
{
    private static string Terraform
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(dir, "Mbr.sln"))) dir = Path.GetDirectoryName(dir)!;
            return Path.Combine(dir, "deploy", "terraform");
        }
    }

    private static List<string> Names(string folder, string kind) =>
        Directory.GetFiles(folder, "*.tf")
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), $@"(?m)^{kind} ""([a-z0-9_]+)""").Select(m => m.Groups[1].Value))
            .Order().ToList();

    [Fact]
    public void Standalone_terraform_passes_every_module_variable_and_output_through()
    {
        var module = Path.Combine(Terraform, "modules", "mbr");
        var moduleVars = Names(module, "variable");
        Assert.Equal(moduleVars.Append("subscription_id").Order().ToList(), Names(Terraform, "variable"));
        Assert.Equal(Names(module, "output"), Names(Terraform, "output"));
        var call = File.ReadAllText(Path.Combine(Terraform, "main.tf"));
        foreach (var v in moduleVars) Assert.Matches($@"(?m)^\s+{v}\s+= var\.{v}$", call);
        // A module must not configure providers: the calling repository does.
        foreach (var f in Directory.GetFiles(module, "*.tf")) Assert.DoesNotMatch(@"(?m)^provider\s", File.ReadAllText(f));
    }

    [Fact]
    public void Terraform_requests_exactly_the_catalog_permissions()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Terraform, "modules", "mbr", "permissions.json")))!;
        foreach (var role in new[] { AppRole.Backup, AppRole.Restore })
            foreach (var (api, key) in new[] { (Mbr.Core.Permissions.PermissionCatalog.Graph, "graph"), (Mbr.Core.Permissions.PermissionCatalog.Exchange, "exchange") })
            {
                var expected = Mbr.Core.Permissions.PermissionCatalog.All.Where(p => p.App == role && p.Api == api).Select(p => p.Name).Distinct().Order().ToList();
                var actual = json[role.ToString().ToLowerInvariant()]![key]!.AsArray().Select(n => n!.GetValue<string>()).Order().ToList();
                Assert.Equal(expected, actual);
            }
    }

    [Theory]
    [InlineData("https://kv-mbr.vault.azure.net/secrets/mbr-backup", "https://kv-mbr.vault.azure.net/", "mbr-backup", null)]
    [InlineData("https://kv-mbr.vault.azure.net/secrets/mbr-backup/0123abcd", "https://kv-mbr.vault.azure.net/", "mbr-backup", "0123abcd")]
    public void Key_vault_certificate_secret_uris_are_parsed(string uri, string vault, string name, string? version)
    {
        var parsed = Mbr.Infrastructure.Hosting.AzureHosting.ParseSecretUri(uri);
        Assert.Equal(new Uri(vault), parsed.Vault);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(version, parsed.Version);
    }

    [Fact]
    public void Certificate_uris_are_rejected_with_a_hint()
    {
        var e = Assert.Throws<ArgumentException>(() => Mbr.Infrastructure.Hosting.AzureHosting.ParseSecretUri("https://kv-mbr.vault.azure.net/certificates/mbr-backup"));
        Assert.Contains("secret URI", e.Message);
    }

    [Fact]
    public async Task Deployment_s3_bucket_becomes_the_default_target_and_follows_configuration()
    {
        using var app = new TestApp(b =>
        {
            b.UseSetting("Mbr:Storage:DefaultS3:Bucket", "contoso-mbr-backups");
            b.UseSetting("Mbr:Storage:DefaultS3:Region", "eu-west-1");
            b.UseSetting("Mbr:Storage:DefaultS3:Credentials", "AzureFederation");
            b.UseSetting("Mbr:Storage:DefaultS3:WebIdentityAudience", "api://11111111-2222-3333-4444-555555555555");
        });
        var targets = JsonNode.Parse(await app.CreateClient().GetStringAsync("/api/storage-targets"))!.AsArray();
        var t = Assert.Single(targets)!;
        Assert.Equal("S3 (deployment)", t["name"]!.GetValue<string>());
        Assert.True(t["isDefault"]!.GetValue<bool>());
        Assert.Equal("contoso-mbr-backups", t["settings"]!["bucket"]!.GetValue<string>());
        Assert.Equal("AzureFederation", t["settings"]!["credentials"]!.GetValue<string>());

        // The role ARN arrives later (second Terraform apply): the next start updates the target instead of adding one.
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Mbr.Infrastructure.MbrOptions>>().Value;
        options.Storage.DefaultS3.RoleArn = "arn:aws:iam::123456789012:role/mbr-backup";
        await app.Services.InitializeMbrDatabaseAsync();
        var stored = app.Db(db => db.StorageTargets.ToList());
        Assert.Contains("arn:aws:iam::123456789012:role/mbr-backup", Assert.Single(stored).SettingsJson);
    }

    [Fact]
    public async Task Health_endpoint_reports_ok_without_sign_in()
    {
        using var app = new TestApp();
        var resp = await app.CreateClient().GetAsync("/healthz");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("ok", await resp.Content.ReadAsStringAsync());
    }
}
