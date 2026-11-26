using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Mbr.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Api;

public sealed class ApiEndpoints
{
    public string Graph { get; set; } = "https://graph.microsoft.com";
    public string Arm { get; set; } = "https://management.azure.com";
    public string Authority { get; set; } = "https://login.microsoftonline.com";

    public string BaseFor(ApiKind kind) => kind == ApiKind.Graph ? Graph : Arm;
    public string ScopeFor(ApiKind kind) => kind == ApiKind.Graph ? "https://graph.microsoft.com/.default" : "https://management.azure.com/.default";
}

/// <summary>App-only tokens for the backup/restore apps in any customer tenant (certificate preferred).</summary>
public sealed class AppTokenProvider(IOptions<MbrOptions> options, ILogger<AppTokenProvider> log) : ITokenProvider
{
    private readonly ConcurrentDictionary<(string, AppRole), TokenCredential> _credentials = new();
    private X509Certificate2? _backupCert, _restoreCert;

    private AppCredentialOptions Opts(AppRole role) => role == AppRole.Backup ? options.Value.Apps.Backup : options.Value.Apps.Restore;

    public bool IsConfigured(AppRole role) => Opts(role).IsConfigured;
    public string? GetClientId(AppRole role) => Opts(role).ClientId;

    public async Task<string> GetTokenAsync(string directoryId, AppRole role, string scope, CancellationToken ct = default)
    {
        var cred = _credentials.GetOrAdd((directoryId, role), k => CreateCredential(k.Item1, k.Item2));
        var token = await cred.GetTokenAsync(new TokenRequestContext([scope]), ct);
        return token.Token;
    }

    private TokenCredential CreateCredential(string directoryId, AppRole role)
    {
        var o = Opts(role);
        if (!o.IsConfigured)
            throw new InvalidOperationException($"The {role} app is not configured (Mbr:Apps:{role}). See docs/setup.md.");
        var cert = LoadCertificate(role, o);
        if (cert is not null) return new ClientCertificateCredential(directoryId, o.ClientId, cert);
        log.LogWarning("The {Role} app is using a client secret. Use a certificate in production.", role);
        return new ClientSecretCredential(directoryId, o.ClientId, o.ClientSecret);
    }

    private X509Certificate2? LoadCertificate(AppRole role, AppCredentialOptions o)
    {
        ref var cached = ref role == AppRole.Backup ? ref _backupCert : ref _restoreCert;
        if (cached is not null) return cached;
        if (!string.IsNullOrWhiteSpace(o.CertificatePath))
            return cached = X509CertificateLoader.LoadPkcs12FromFile(o.CertificatePath, o.CertificatePassword);
        if (!string.IsNullOrWhiteSpace(o.CertificateThumbprint))
        {
            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, o.CertificateThumbprint, validOnly: false);
                if (found.Count > 0) return cached = found[0];
            }
            throw new InvalidOperationException($"Certificate {o.CertificateThumbprint} not found in the certificate store.");
        }
        return null;
    }
}

public sealed class ApiClientFactory(IHttpClientFactory http, ITokenProvider tokens, IOptions<MbrOptions> options,
    ApiEndpoints endpoints, ILoggerFactory logs) : IApiClientFactory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TenantGates = new();

    public IApiClient Create(string directoryId, ApiKind kind, AppRole role)
    {
        // Keyed by the limit too, so a limit changed in the app applies to new clients straight away.
        var limit = Math.Max(1, options.Value.Worker.MaxRequestsPerTenant);
        var gate = TenantGates.GetOrAdd($"{directoryId}|{limit}", _ => new SemaphoreSlim(limit));
        return new ApiClient(http.CreateClient("mbr-api"), kind, endpoints.BaseFor(kind),
            ct => tokens.GetTokenAsync(directoryId, role, endpoints.ScopeFor(kind), ct), gate, logs.CreateLogger<ApiClient>());
    }
}

public sealed class ApiClient(HttpClient http, ApiKind kind, string baseUrl, Func<CancellationToken, Task<string>> getToken,
    SemaphoreSlim gate, ILogger log) : IApiClient
{
    private const int MaxAttempts = 8;
    public ApiKind Kind => kind;
    /// <summary>Injected delay for tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public string AbsoluteUrl(string path) => Url(path);

    private string Url(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default) => await SendAsync(HttpMethod.Get, path, null, null, ct);

    public async Task<JsonNode?> TryGetAsync(string path, CancellationToken ct = default)
    {
        try { return await GetAsync(path, ct); }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async IAsyncEnumerable<JsonObject> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? next = path;
        while (next != null)
        {
            var page = await GetAsync(next, ct) as JsonObject;
            if (page is null) yield break;
            if (page["value"] is JsonArray arr)
                foreach (var item in arr)
                    if (item is JsonObject o) yield return (JsonObject)o.DeepClone();
            next = NextLink(page);
        }
    }

    public async Task<DeltaResult> DeltaAsync(string pathOrDeltaLink, CancellationToken ct = default)
    {
        var items = new List<JsonObject>();
        string? next = pathOrDeltaLink, delta = null;
        while (next != null)
        {
            var page = await GetAsync(next, ct) as JsonObject ?? throw new ApiException(HttpStatusCode.NoContent, null, "Empty delta page");
            if (page["value"] is JsonArray arr)
                items.AddRange(arr.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
            next = NextLink(page);
            delta = page["@odata.deltaLink"]?.GetValue<string>() ?? delta;
        }
        return new DeltaResult(items, delta);
    }

    private string? NextLink(JsonObject page) =>
        (kind == ApiKind.Graph ? page["@odata.nextLink"] : page["nextLink"])?.GetValue<string>();

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null,
        IDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var json = body?.ToJsonString();
        using var resp = await SendRawAsync(() =>
        {
            var req = new HttpRequestMessage(method, Url(path));
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (headers != null)
                foreach (var (k, v) in headers)
                    if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
            return req;
        }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw ToException(resp.StatusCode, text);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw ToException(resp.StatusCode, text);
        }
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            TimeSpan? wait = null;
            await gate.WaitAsync(ct);
            try
            {
                using var req = requestFactory();
                // Pre-authorized URLs on other hosts (upload sessions) must not carry the token.
                if (req.RequestUri is null || !req.RequestUri.IsAbsoluteUri || IsApiHost(req.RequestUri))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(ct));
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!IsTransient(resp.StatusCode) || attempt >= MaxAttempts) return resp;
                wait = RetryAfter(resp) ?? Backoff(attempt);
                log.LogInformation("{Status} from {Url}; retrying in {Wait}s (attempt {Attempt})",
                    (int)resp.StatusCode, req.RequestUri, wait.Value.TotalSeconds, attempt);
                resp.Dispose();
            }
            catch (HttpRequestException e) when (attempt < MaxAttempts)
            {
                resp?.Dispose();
                wait = Backoff(attempt);
                log.LogInformation(e, "Network error; retrying in {Wait}s", wait.Value.TotalSeconds);
            }
            finally { gate.Release(); }
            await Delay(wait!.Value, ct);
        }
    }

    private bool IsApiHost(Uri uri) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && string.Equals(b.Host, uri.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(HttpStatusCode s) =>
        s is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway;

    private static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        if (r.Headers.RetryAfter?.Delta is { } d) return d;
        if (r.Headers.RetryAfter?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt - 1))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));

    private static ApiException ToException(HttpStatusCode status, string body)
    {
        string? code = null, message = null;
        try
        {
            var err = JsonNode.Parse(body)?["error"];
            code = err?["code"]?.GetValue<string>();
            message = err?["message"]?.GetValue<string>();
        }
        catch { /* not JSON */ }
        return new ApiException(status, code, message ?? (body.Length > 500 ? body[..500] : body), body);
    }
}
