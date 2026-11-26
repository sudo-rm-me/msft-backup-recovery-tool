using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Json;

namespace Mbr.Catalog.Azure;

/// <summary>Azure Resource Manager helpers: API versions, scopes (subscriptions and management groups), long-running operations.</summary>
public static class Arm
{
    public const string ResourcesApi = "2021-04-01";
    public const string SubscriptionsApi = "2022-12-01";
    public const string AuthorizationApi = "2022-04-01";
    public const string PolicyApi = "2023-04-01";
    public const string ManagementGroupsApi = "2021-04-01";

    /// <summary>Wait between polls of long-running operations (tests replace it).</summary>
    public static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    public static TimeSpan OperationTimeout { get; set; } = TimeSpan.FromHours(2);

    public static string Url(string id, string apiVersion, string? query = null) =>
        id + (id.Contains('?') ? "&" : "?") + "api-version=" + apiVersion + (string.IsNullOrEmpty(query) ? "" : "&" + query);

    public static string LastSegment(string id) => id.TrimEnd('/')[(id.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>"/subscriptions/{id}" for any resource ID below a subscription, else null.</summary>
    public static string? SubscriptionOf(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ? $"/subscriptions/{parts[1]}" : null;
    }

    public static bool IsAtOrBelow(string id, string scope) =>
        id.Equals(scope, StringComparison.OrdinalIgnoreCase) || id.StartsWith(scope.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- scopes (cached per session)

    private static readonly ConditionalWeakTable<ITenantSession, Task<List<JsonObject>>> Subscriptions = new();
    private static readonly ConditionalWeakTable<ITenantSession, Task<List<JsonObject>>> ManagementGroups = new();

    /// <summary>Subscriptions of this tenant that the app can see (disabled and deleted ones are left out).</summary>
    public static Task<List<JsonObject>> SubscriptionsAsync(ITenantSession s, CancellationToken ct) =>
        Subscriptions.GetValue(s, _ => LoadSubscriptionsAsync(s, ct));

    private static async Task<List<JsonObject>> LoadSubscriptionsAsync(ITenantSession s, CancellationToken ct)
    {
        var list = new List<JsonObject>();
        await foreach (var sub in s.Arm.ListAsync(Url("/subscriptions", SubscriptionsApi), ct))
        {
            var tenant = sub["tenantId"]?.ToString();
            var state = sub["state"]?.ToString() ?? "Enabled";
            if (tenant != null && !tenant.Equals(s.DirectoryId, StringComparison.OrdinalIgnoreCase)) continue; // guest access to another tenant
            if (state is "Disabled" or "Deleted") continue;
            sub["id"] = $"/subscriptions/{sub["subscriptionId"]}";
            list.Add(sub);
        }
        return list;
    }

    /// <summary>Management groups (empty when the app has no access to the hierarchy).</summary>
    public static Task<List<JsonObject>> ManagementGroupsAsync(ITenantSession s, CancellationToken ct) =>
        ManagementGroups.GetValue(s, _ => LoadManagementGroupsAsync(s, ct));

    private static async Task<List<JsonObject>> LoadManagementGroupsAsync(ITenantSession s, CancellationToken ct)
    {
        var list = new List<JsonObject>();
        try
        {
            await foreach (var mg in s.Arm.ListAsync(Url("/providers/Microsoft.Management/managementGroups", ManagementGroupsApi), ct)) list.Add(mg);
        }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            await s.ReportIssueAsync(new CaptureIssue("azure.managementGroup", null, IssueSeverity.Info,
                "Management groups aren't readable (grant Reader on the tenant root group to include them)."));
        }
        return list;
    }

    /// <summary>Scopes that own RBAC and Policy objects: management groups, then subscriptions.</summary>
    public static async Task<List<(string Id, bool IsManagementGroup)>> ScopesAsync(ITenantSession s, CancellationToken ct)
    {
        var scopes = (await ManagementGroupsAsync(s, ct)).Select(m => (m["id"]!.ToString(), true)).ToList();
        scopes.AddRange((await SubscriptionsAsync(s, ct)).Select(x => (x["id"]!.ToString(), false)));
        return scopes;
    }

    /// <summary>"subscription Contoso Production", "management group Contoso", "rg-web" and so on, for display names.</summary>
    public static async Task<string> DescribeScopeAsync(ITenantSession s, string scope, CancellationToken ct)
    {
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "the tenant";
        if (parts.Length >= 4 && parts[0].Equals("providers", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("managementGroups", StringComparison.OrdinalIgnoreCase))
        {
            var mg = parts[3];
            var found = (await ManagementGroupsAsync(s, ct)).FirstOrDefault(m => m["name"]?.ToString() == mg);
            return $"management group {found?["properties"]?["displayName"] ?? mg}";
        }
        var sub = (await SubscriptionsAsync(s, ct)).FirstOrDefault(x => x["subscriptionId"]?.ToString() == parts.ElementAtOrDefault(1));
        var subName = sub?["displayName"]?.ToString() ?? parts.ElementAtOrDefault(1);
        if (parts.Length == 2) return $"subscription {subName}";
        if (parts.Length == 4) return $"{parts[3]} ({subName})";
        return $"{parts[^1]} in {parts.ElementAtOrDefault(3)}";
    }

    // ---------------------------------------------------------------- content

    private static readonly string[] Volatile = ["etag", "systemData"];
    private static readonly string[] VolatileProperties = ["provisioningState", "createdOn", "updatedOn", "createdBy", "updatedBy"];

    /// <summary>Copy of an ARM object without server-maintained values (etags, timestamps, provisioning state).</summary>
    public static JsonObject Clean(JsonObject raw, params string[] strip)
    {
        var o = (JsonObject)raw.DeepClone();
        foreach (var p in Volatile) o.Remove(p);
        if (o["properties"] is JsonObject props)
        {
            foreach (var p in VolatileProperties) props.Remove(p);
            if (props["metadata"] is JsonObject meta)
                foreach (var p in VolatileProperties) meta.Remove(p);
        }
        foreach (var p in strip) JsonPath.Remove(o, p);
        return o;
    }

    // ---------------------------------------------------------------- writes and long-running operations

    /// <summary>
    /// Sends a write and waits for it to finish (202 + Location, Azure-AsyncOperation, or a resource whose provisioning state is
    /// still running). Returns the final resource (PUT) or the operation result (POST).
    /// </summary>
    public static async Task<JsonNode?> SendAsync(IApiClient arm, HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        var (status, node, asyncOp, location) = await RawAsync(arm, method, url, body, ct);
        var deadline = DateTimeOffset.UtcNow + OperationTimeout;
        if (asyncOp != null && status is HttpStatusCode.Created or HttpStatusCode.Accepted)
        {
            while (true)
            {
                var (_, op, _, _) = await RawAsync(arm, HttpMethod.Get, asyncOp, null, ct);
                var state = op?["status"]?.ToString();
                if (state is "Succeeded") break;
                if (state is "Failed" or "Canceled")
                    throw new ApiException(HttpStatusCode.Conflict, op?["error"]?["code"]?.ToString() ?? state, op?["error"]?["message"]?.ToString() ?? $"The operation {state.ToLowerInvariant()}.");
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The Azure operation didn't finish in time.");
                await Delay(TimeSpan.FromSeconds(5), ct);
            }
            return method == HttpMethod.Put ? await arm.TryGetAsync(url, ct) : location != null ? (await RawAsync(arm, HttpMethod.Get, location, null, ct)).Body : node;
        }
        if (status == HttpStatusCode.Accepted && location != null)
        {
            while (true)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The Azure operation didn't finish in time.");
                await Delay(TimeSpan.FromSeconds(5), ct);
                var (s, result, _, next) = await RawAsync(arm, HttpMethod.Get, location, null, ct);
                if (s != HttpStatusCode.Accepted) return method == HttpMethod.Put ? await arm.TryGetAsync(url, ct) : result;
                location = next ?? location;
            }
        }
        if (method == HttpMethod.Put && node?["properties"]?["provisioningState"]?.ToString() is { } ps && !IsTerminal(ps))
        {
            while (true)
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The Azure operation didn't finish in time.");
                await Delay(TimeSpan.FromSeconds(5), ct);
                node = await arm.GetAsync(url, ct);
                var state = node?["properties"]?["provisioningState"]?.ToString();
                if (state is null || IsTerminal(state))
                {
                    if (state is "Failed" or "Canceled") throw new ApiException(HttpStatusCode.Conflict, state, $"Provisioning {state.ToLowerInvariant()}.");
                    break;
                }
            }
        }
        return node;
    }

    public static bool IsTerminal(string provisioningState) => provisioningState is "Succeeded" or "Failed" or "Canceled";

    private static async Task<(HttpStatusCode Status, JsonNode? Body, string? AsyncOperation, string? Location)> RawAsync(
        IApiClient arm, HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var resp = await arm.SendRawAsync(() =>
        {
            var req = new HttpRequestMessage(method, arm.AbsoluteUrl(url));
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return req;
        }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if (!resp.IsSuccessStatusCode)
            throw new ApiException(resp.StatusCode, node?["error"]?["code"]?.ToString(), node?["error"]?["message"]?.ToString() ?? resp.ReasonPhrase ?? "Request failed", text);
        var asyncOp = resp.Headers.TryGetValues("Azure-AsyncOperation", out var a) ? a.FirstOrDefault() : null;
        return (resp.StatusCode, node, asyncOp, resp.Headers.Location?.ToString());
    }
}
