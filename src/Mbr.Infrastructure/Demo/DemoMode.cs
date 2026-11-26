using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Core.Permissions;
using Mbr.Infrastructure.Api;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mbr.Infrastructure.Demo;

/// <summary>
/// Development-only demo: adds a simulated tenant ("Contoso (demo)") whose Graph and ARM calls are served by an
/// in-memory fake, so every screen can be tried without a real tenant. Enable with Mbr:Demo:Enabled=true.
/// The simulated data resets when the process restarts.
/// </summary>
public static class DemoMode
{
    public const string DirectoryId = "11111111-2222-3333-4444-555555555555";

    public static IServiceCollection AddMbrDemo(this IServiceCollection services)
    {
        var graph = new InMemoryGraph();
        var arm = new InMemoryGraph();
        DemoData.Seed(graph, arm);
        // Every catalog collection exists (empty unless seeded), like a real tenant with nothing configured.
        foreach (var d in Mbr.Catalog.CatalogLoader.All.Where(d => d.Api == "graph" && !d.Singleton))
            graph.Collection(d.ListPath);
        var powerShell = new InMemoryPowerShell();
        DemoData.SeedPowerShell(powerShell);
        services.AddSingleton(new DemoBackends(graph, arm));
        // The demo tenant doubles as the partner tenant whose GDAP customers can be imported.
        services.PostConfigure<MbrOptions>(o => o.Partner.TenantId ??= DirectoryId);
        services.AddSingleton(powerShell);
        services.Decorate<Mbr.Infrastructure.Backup.IPowerShellRunnerFactory>((inner, _) => new InMemoryPowerShellFactory(inner, powerShell, t => t.DirectoryId == DirectoryId));
        services.Decorate<IApiClientFactory>((inner, sp) => new DemoApiClientFactory(inner, sp.GetRequiredService<DemoBackends>()));
        services.Decorate<ITokenProvider>((inner, _) => new DemoTokenProvider(inner));
        return services;
    }

    public static async Task EnsureDemoTenantAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        if (await db.Tenants.AnyAsync(t => t.DirectoryId == DirectoryId)) return;
        db.Tenants.Add(new Tenant
        {
            DirectoryId = DirectoryId, DisplayName = "Contoso (demo)", DefaultDomain = "contoso-demo.onmicrosoft.com",
            Status = TenantStatus.Active, BackupConsentedAt = DateTimeOffset.UtcNow, RestoreConsentedAt = DateTimeOffset.UtcNow,
            Notes = "Simulated tenant served by an in-memory Microsoft Graph. Data resets when the server restarts.",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Replaces a registered singleton with a decorator around it.</summary>
    private static void Decorate<T>(this IServiceCollection services, Func<T, IServiceProvider, T> decorate) where T : class
    {
        var existing = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(existing);
        services.AddSingleton(sp =>
        {
            var inner = existing.ImplementationInstance as T
                        ?? (existing.ImplementationFactory?.Invoke(sp) as T)
                        ?? (T)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!);
            return decorate(inner, sp);
        });
    }
}

public sealed record DemoBackends(InMemoryGraph Graph, InMemoryGraph Arm);

public sealed class DemoApiClientFactory(IApiClientFactory inner, DemoBackends demo) : IApiClientFactory
{
    public IApiClient Create(string directoryId, ApiKind kind, AppRole role)
    {
        if (directoryId != DemoMode.DirectoryId) return inner.Create(directoryId, kind, role);
        var handler = kind == ApiKind.Graph ? demo.Graph : demo.Arm;
        return new ApiClient(new HttpClient(handler, disposeHandler: false), kind,
            kind == ApiKind.Graph ? "https://graph.microsoft.com" : "https://management.azure.com",
            _ => Task.FromResult("demo"), new SemaphoreSlim(8), NullLogger.Instance);
    }
}

public sealed class DemoTokenProvider(ITokenProvider inner) : ITokenProvider
{
    public Task<string> GetTokenAsync(string directoryId, AppRole role, string scope, CancellationToken ct = default)
    {
        if (directoryId != DemoMode.DirectoryId) return inner.GetTokenAsync(directoryId, role, scope, ct);
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var roles = new JsonArray(PermissionCatalog.For(role).Select(p => (JsonNode)p.Name).Distinct().ToArray());
        return Task.FromResult($"{B64("{\"alg\":\"none\"}")}.{B64(new JsonObject { ["roles"] = roles }.ToJsonString())}.demo");
    }

    public bool IsConfigured(AppRole role) => true;
    public string? GetClientId(AppRole role) => inner.GetClientId(role) ?? "00000000-0000-0000-0000-00000000demo";
}
