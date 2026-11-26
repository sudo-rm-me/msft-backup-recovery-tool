using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;

namespace Mbr.Cli;

public class CliConfig
{
    public string Server { get; set; } = "http://localhost:5180";
    public string? Mode { get; set; }
    public string? ClientId { get; set; }
    public string? Authority { get; set; }
    public string[]? Scopes { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mbr");
    private static string FilePath => Path.Combine(Dir, "config.json");

    public static CliConfig Load()
    {
        var cfg = File.Exists(FilePath) ? JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(FilePath)) ?? new() : new CliConfig();
        var env = Environment.GetEnvironmentVariable("MBR_SERVER");
        if (!string.IsNullOrEmpty(env)) cfg.Server = env;
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class CliException(string message) : Exception(message);

/// <summary>HTTP client for the MBR REST API. Signs in with MSAL (device code) when the server uses Entra auth.</summary>
public sealed class MbrClient
{
    private readonly CliConfig _cfg;
    private readonly HttpClient _http;
    private IPublicClientApplication? _msal;

    public MbrClient(CliConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { BaseAddress = new Uri(cfg.Server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
    }

    private IPublicClientApplication Msal()
    {
        if (_msal != null) return _msal;
        if (string.IsNullOrEmpty(_cfg.ClientId) || string.IsNullOrEmpty(_cfg.Authority))
            throw new CliException("Not signed in. Run: mbr login --server <url>");
        _msal = PublicClientApplicationBuilder.Create(_cfg.ClientId).WithAuthority(_cfg.Authority).WithDefaultRedirectUri().Build();
        var cachePath = Path.Combine(CliConfig.Dir, "msal.cache");
        _msal.UserTokenCache.SetBeforeAccess(a =>
        {
            if (File.Exists(cachePath)) a.TokenCache.DeserializeMsalV3(File.ReadAllBytes(cachePath));
        });
        _msal.UserTokenCache.SetAfterAccess(a =>
        {
            if (!a.HasStateChanged) return;
            Directory.CreateDirectory(CliConfig.Dir);
            File.WriteAllBytes(cachePath, a.TokenCache.SerializeMsalV3());
        });
        return _msal;
    }

    public async Task LoginAsync(CancellationToken ct)
    {
        var config = JsonNode.Parse(await _http.GetStringAsync("api/auth/config", ct))!;
        _cfg.Mode = config["mode"]!.GetValue<string>();
        if (_cfg.Mode == "Entra")
        {
            _cfg.ClientId = config["clientId"]?.GetValue<string>();
            _cfg.Authority = config["authority"]?.GetValue<string>();
            _cfg.Scopes = config["scopes"]?.AsArray().Select(s => s!.GetValue<string>()).ToArray();
            _cfg.Save();
            var result = await Msal().AcquireTokenWithDeviceCode(_cfg.Scopes, code =>
            {
                Console.WriteLine(code.Message);
                return Task.CompletedTask;
            }).ExecuteAsync(ct);
            Console.WriteLine($"Signed in as {result.Account.Username}");
        }
        else
        {
            _cfg.Save();
            Console.WriteLine("Server uses Development auth (no sign-in needed).");
        }
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (_cfg.Mode != "Entra") return null;
        var app = Msal();
        var account = (await app.GetAccountsAsync()).FirstOrDefault() ?? throw new CliException("Not signed in. Run: mbr login");
        try { return (await app.AcquireTokenSilent(_cfg.Scopes, account).ExecuteAsync(ct)).AccessToken; }
        catch (MsalUiRequiredException) { throw new CliException("Your sign-in has expired. Run: mbr login"); }
    }

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (await TokenAsync(ct) is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string detail = text;
            try { detail = JsonNode.Parse(text)?["detail"]?.GetValue<string>() ?? text; } catch { }
            throw new CliException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {detail}");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
        if (await TokenAsync(ct) is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new CliException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonNode?> PostAsync(string path, object? body = null, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body ?? new { }, ct);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves --tenant (ID, directory ID, domain or display name) to the tool's tenant ID.</summary>
    public async Task<string> ResolveTenantAsync(string? tenant, CancellationToken ct)
    {
        tenant ??= Environment.GetEnvironmentVariable("MBR_TENANT");
        if (string.IsNullOrWhiteSpace(tenant)) throw new CliException("--tenant is required (or set MBR_TENANT).");
        var list = (await GetAsync("api/tenants", ct))!.AsArray();
        var t = tenant.Trim();
        var match = list.FirstOrDefault(x =>
            Eq(x?["id"], t) || Eq(x?["directoryId"], t) || Eq(x?["defaultDomain"], t) || Eq(x?["displayName"], t))
            ?? list.FirstOrDefault(x => x?["defaultDomain"]?.GetValue<string>().StartsWith(t + ".", StringComparison.OrdinalIgnoreCase) == true);
        return match?["id"]?.GetValue<string>() ?? throw new CliException($"No tenant matches '{tenant}'. Run: mbr tenant list");

        static bool Eq(JsonNode? n, string v) => n?.GetValue<string>() is { } s && s.Equals(v, StringComparison.OrdinalIgnoreCase);
    }
}
