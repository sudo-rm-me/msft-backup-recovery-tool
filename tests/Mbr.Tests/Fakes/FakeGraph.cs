using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Infrastructure.Api;
using Mbr.Infrastructure.Demo;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mbr.Tests.Fakes;

/// <summary>Test alias for the in-memory Graph used by demo mode.</summary>
public sealed class FakeGraph : InMemoryGraph;

public sealed class FakeTokenProvider : ITokenProvider
{
    public Task<string> GetTokenAsync(string directoryId, AppRole role, string scope, CancellationToken ct = default) => Task.FromResult(MakeJwt(Roles(role)));
    public bool IsConfigured(AppRole role) => true;
    public string? GetClientId(AppRole role) => role == AppRole.Backup ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222";

    public Func<AppRole, string[]> Roles { get; set; } = role => Mbr.Core.Permissions.PermissionCatalog.For(role).Select(p => p.Name).ToArray();

    public static string MakeJwt(string[] roles)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject { ["roles"] = new JsonArray(roles.Select(r => (JsonNode)r).ToArray()) };
        return $"{B64("{\"alg\":\"none\"}")}.{B64(payload.ToJsonString())}.sig";
    }
}

/// <summary>Creates real <see cref="ApiClient"/>s talking to fake Graph/ARM handlers, with no retry delays.</summary>
public sealed class FakeApiClientFactory(FakeGraph graph, FakeGraph arm, IReadOnlyDictionary<string, FakeGraph>? perTenant = null) : IApiClientFactory
{
    public IApiClient Create(string directoryId, ApiKind kind, AppRole role)
    {
        var handler = kind == ApiKind.Graph ? perTenant?.GetValueOrDefault(directoryId) ?? graph : arm;
        return new ApiClient(new HttpClient(handler, disposeHandler: false), kind,
            kind == ApiKind.Graph ? "https://graph.microsoft.com" : "https://management.azure.com",
            _ => Task.FromResult("token"), new SemaphoreSlim(8), NullLogger.Instance)
        { Delay = (_, _) => Task.CompletedTask };
    }
}

