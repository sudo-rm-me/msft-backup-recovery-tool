using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mbr.Tests.Fakes;

/// <summary>The full API (with in-process worker) running on a temp SQLite DB and local storage, talking to fake Graph/ARM.</summary>
public sealed class TestApp(Action<IWebHostBuilder>? configure = null) : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mbr-tests", Guid.NewGuid().ToString("N"));
    public FakeGraph Graph { get; } = new();
    public FakeGraph Arm { get; } = new();
    public Mbr.Infrastructure.Demo.InMemoryPowerShell PowerShell { get; } = new();
    /// <summary>Graph fakes for specific tenants (by directory ID); others use <see cref="Graph"/>.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, FakeGraph> TenantGraphs { get; } = new();
    public FakeTokenProvider Tokens { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Auth:Mode", "Development");
        builder.UseSetting("Mbr:Demo:Enabled", "false");
        configure?.Invoke(builder);
        builder.UseSetting("Mbr:DataDirectory", DataDir);
        builder.UseSetting("Mbr:Worker:PollSeconds", "1");
        builder.UseSetting("Mbr:Database:ConnectionString", $"Data Source={Path.Combine(DataDir, "mbr.db")}");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITokenProvider>();
            services.AddSingleton<ITokenProvider>(Tokens);
            services.RemoveAll<IApiClientFactory>();
            services.AddSingleton<IApiClientFactory>(new FakeApiClientFactory(Graph, Arm, TenantGraphs));
            services.RemoveAll<Mbr.Infrastructure.Backup.IPowerShellRunnerFactory>();
            services.AddSingleton<Mbr.Infrastructure.Backup.IPowerShellRunnerFactory>(new Mbr.Infrastructure.Demo.InMemoryPowerShellFactory(null, PowerShell));
            services.RemoveAll<Mbr.Infrastructure.Notifications.INotifier>();
            services.AddSingleton<Mbr.Infrastructure.Notifications.INotifier>(Notifier);
        });
    }

    public async Task<Tenant> AddTenantAsync(string name = "Contoso")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var t = new Tenant { DirectoryId = Guid.NewGuid().ToString(), DisplayName = name, DefaultDomain = $"{name.ToLowerInvariant()}.onmicrosoft.com", Status = TenantStatus.Active };
        db.Tenants.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    public T Db<T>(Func<MbrDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<MbrDbContext>());
    }

    public async Task<JsonNode> StartJobAsync(HttpClient client, string type, Guid? tenantId, object? parameters = null)
    {
        var resp = await client.PostAsJsonAsync("/api/jobs", new { type, tenantId, parameters = parameters ?? new { } });
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    }

    public async Task<JsonNode> WaitForJobAsync(HttpClient client, string jobId, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < until)
        {
            var job = JsonNode.Parse(await client.GetStringAsync($"/api/jobs/{jobId}"))!;
            var status = job["status"]!.GetValue<string>();
            if (status is "Succeeded" or "PartiallySucceeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(250);
        }
        var last = await client.GetStringAsync($"/api/jobs/{jobId}");
        var logs = await client.GetStringAsync($"/api/jobs/{jobId}/logs");
        throw new TimeoutException($"Job {jobId} did not finish: {last}\n{logs}");
    }

    public async Task<JsonNode> RunJobAsync(HttpClient client, string type, Guid? tenantId, object? parameters = null)
    {
        var job = await StartJobAsync(client, type, tenantId, parameters);
        var done = await WaitForJobAsync(client, job["id"]!.GetValue<string>());
        if (done["status"]!.GetValue<string>() == "Failed")
        {
            var logs = await client.GetStringAsync($"/api/jobs/{job["id"]}/logs");
            throw new Exception($"Job {type} failed: {done["error"]}\n{logs}");
        }
        return done;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
    }
}


public sealed class RecordingNotifier : Mbr.Infrastructure.Notifications.INotifier
{
    public System.Collections.Concurrent.ConcurrentQueue<Mbr.Infrastructure.Notifications.Notification> Sent { get; } = new();
    public Task NotifyAsync(Mbr.Infrastructure.Notifications.Notification n, CancellationToken ct = default)
    {
        Sent.Enqueue(n);
        return Task.CompletedTask;
    }
}
