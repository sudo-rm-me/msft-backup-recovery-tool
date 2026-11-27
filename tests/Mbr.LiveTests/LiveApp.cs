using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.LiveTests;

/// <summary>
/// The full API and worker, on a temporary SQLite database and local storage, talking to a real test tenant.
/// The apps come from the usual configuration (environment variables such as Mbr__Apps__Backup__ClientId); the tenant
/// from MBR_LIVE_TENANT.
/// </summary>
public sealed class LiveApp : WebApplicationFactory<Program>
{
    public static string Tenant => Environment.GetEnvironmentVariable("MBR_LIVE_TENANT") is { Length: > 0 } t
        ? t
        : throw new InvalidOperationException(
            "Live tests need a test tenant: set MBR_LIVE_TENANT (domain or directory ID) and the backup and restore apps " +
            "(Mbr__Apps__Backup__ClientId, Mbr__Apps__Backup__CertificatePath, and the same for Restore). See docs/testing.md.");

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mbr-live", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:Mode", "Development");
        builder.UseSetting("Mbr:Demo:Enabled", "false");
        builder.UseSetting("Mbr:DataDirectory", DataDir);
        builder.UseSetting("Mbr:Worker:PollSeconds", "1");
        builder.UseSetting("Mbr:Database:ConnectionString", $"Data Source={Path.Combine(DataDir, "mbr.db")}");
    }

    /// <summary>Adds the test tenant and returns its MBR ID and directory ID.</summary>
    public async Task<(Guid Id, string DirectoryId)> AddTenantAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/tenants", new { domain = Tenant, displayName = "MBR live tests" });
        if (!resp.IsSuccessStatusCode) throw new Exception($"Adding {Tenant} failed: {await resp.Content.ReadAsStringAsync()}");
        var t = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return (Guid.Parse(t["id"]!.GetValue<string>()), t["directoryId"]!.GetValue<string>());
    }

    public async Task<JsonNode> RunJobAsync(HttpClient client, string type, Guid tenantId, object parameters, TimeSpan timeout)
    {
        var resp = await client.PostAsJsonAsync("/api/jobs", new { type, tenantId, parameters });
        resp.EnsureSuccessStatusCode();
        var id = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        return await WaitForJobAsync(client, id, timeout);
    }

    public async Task<JsonNode> WaitForJobAsync(HttpClient client, string jobId, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var job = JsonNode.Parse(await client.GetStringAsync($"/api/jobs/{jobId}"))!;
            if (job["status"]!.GetValue<string>() is "Succeeded" or "PartiallySucceeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Job {jobId} did not finish in {timeout}:\n{await client.GetStringAsync($"/api/jobs/{jobId}/logs")}");
    }

    /// <summary>An HttpClient for Microsoft Graph in the test tenant, signed in as the restore app (which can write).</summary>
    public async Task<HttpClient> GraphAsync(string directoryId)
    {
        var token = await Services.GetRequiredService<ITokenProvider>()
            .GetTokenAsync(directoryId, AppRole.Restore, "https://graph.microsoft.com/.default");
        var http = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }
}
