using System.Net;
using System.Text.Json.Nodes;

namespace Mbr.Core.Abstractions;

/// <summary>Which of the tool's two multi-tenant apps to authenticate as.</summary>
public enum AppRole { Backup, Restore }

/// <summary>Which Microsoft API a client talks to.</summary>
public enum ApiKind { Graph, Arm }

/// <summary>
/// Thin JSON REST client for Microsoft Graph and Azure Resource Manager. Paths are relative to the API root,
/// e.g. "/v1.0/groups" or "/beta/deviceManagement/deviceConfigurations" for Graph and
/// "/subscriptions?api-version=2022-12-01" for ARM. Absolute URLs (next/delta links) are accepted too.
/// Implementations handle auth, throttling (429/503/504 + Retry-After) and paging.
/// </summary>
public interface IApiClient
{
    ApiKind Kind { get; }
    Task<JsonNode?> GetAsync(string path, CancellationToken ct = default);
    /// <summary>GET that returns null on 404 instead of throwing.</summary>
    Task<JsonNode?> TryGetAsync(string path, CancellationToken ct = default);
    /// <summary>Enumerates every item in a collection, following next links.</summary>
    IAsyncEnumerable<JsonObject> ListAsync(string path, CancellationToken ct = default);
    /// <summary>Runs a delta query to completion; returns changed items and the new delta link.</summary>
    Task<DeltaResult> DeltaAsync(string pathOrDeltaLink, CancellationToken ct = default);
    Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null,
        IDictionary<string, string>? headers = null, CancellationToken ct = default);
    /// <summary>Downloads binary content (follows redirects). Caller disposes the stream.</summary>
    Task<Stream> DownloadAsync(string path, CancellationToken ct = default);
    /// <summary>The absolute URL for an API path (for raw requests).</summary>
    string AbsoluteUrl(string path);
    /// <summary>Sends a raw request with auth and retry. Caller disposes the response.</summary>
    Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct = default);
}

public record DeltaResult(IReadOnlyList<JsonObject> Items, string? DeltaLink);

public interface IApiClientFactory
{
    /// <param name="directoryId">Entra tenant ID of the customer tenant.</param>
    IApiClient Create(string directoryId, ApiKind kind, AppRole role);
}

public interface ITokenProvider
{
    Task<string> GetTokenAsync(string directoryId, AppRole role, string scope, CancellationToken ct = default);
    /// <summary>True when credentials for the given app are configured.</summary>
    bool IsConfigured(AppRole role);
    string? GetClientId(AppRole role);
}

public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
    public string? ResponseBody { get; }

    public ApiException(HttpStatusCode status, string? code, string message, string? body = null)
        : base($"{(int)status} {code}: {message}")
    {
        StatusCode = status;
        ErrorCode = code;
        ResponseBody = body;
    }
}
