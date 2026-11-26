using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>
/// In-memory stand-in for Microsoft Graph / ARM. Collections are keyed by path without the version prefix
/// (e.g. "deviceManagement/deviceConfigurations"). Supports list with paging, get, create, update, delete,
/// and custom route overrides for anything else.
/// </summary>
public partial class InMemoryGraph : HttpMessageHandler
{
    public ConcurrentDictionary<string, List<JsonObject>> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string?, HttpResponseMessage> Handle)> Routes { get; } = new();
    public ConcurrentQueue<string> RequestLog { get; } = new();
    public int PageSize { get; set; } = 100;
    private int _nextId = 1;

    public List<JsonObject> Collection(string path) => Collections.GetOrAdd(Normalize(path), _ => new List<JsonObject>());

    public JsonObject Add(string path, JsonObject item)
    {
        item["id"] ??= NewId();
        lock (Collection(path)) Collection(path).Add(item);
        return item;
    }

    public string NewId() => $"00000000-0000-0000-0000-{Interlocked.Increment(ref _nextId):D12}";

    public void On(HttpMethod method, string pathStartsWith, Func<HttpRequestMessage, string?, HttpResponseMessage> handle) =>
        Routes.Insert(0, (r => r.Method == method && Normalize(r.RequestUri!.AbsolutePath).StartsWith(Normalize(pathStartsWith), StringComparison.OrdinalIgnoreCase), handle));

    public static string Normalize(string path)
    {
        var p = Uri.UnescapeDataString(path.Split('?')[0]).Trim('/');
        foreach (var v in new[] { "v1.0/", "beta/" })
            if (p.StartsWith(v, StringComparison.OrdinalIgnoreCase)) p = p[v.Length..];
        return p;
    }

    public static HttpResponseMessage Json(JsonNode? body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var body = bytes is null ? null : Encoding.UTF8.GetString(bytes);
        RequestLog.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
        foreach (var (match, handle) in Routes.ToList())
            if (match(request)) return handle(request, body);

        var path = Normalize(request.RequestUri!.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (HandleArm(request, path, body) is { } arm) return arm;
        if (HandleUploads(request, bytes) is { } upload) return upload;
        if (HandleMailboxes(request, path, body) is { } mailbox) return mailbox;
        if (HandleDrives(request, path, body, bytes) is { } drive) return drive;
        if (HandleSpecial(request.Method, path, body) is { } special) return special;

        if (Collections.TryGetValue(path, out var items))
        {
            if (request.Method == HttpMethod.Get)
            {
                var skip = int.TryParse(query["$skiptoken"], out var s) ? s : 0;
                List<JsonObject> snapshot;
                lock (items) snapshot = items.ToList();
                var page = new JsonArray(snapshot.Skip(skip).Take(PageSize).Select(i => (JsonNode)i.DeepClone()).ToArray());
                var result = new JsonObject { ["value"] = page };
                if (skip + PageSize < snapshot.Count)
                    result["@odata.nextLink"] = $"{request.RequestUri.GetLeftPart(UriPartial.Path)}?$skiptoken={skip + PageSize}";
                return Json(result);
            }
            if (request.Method == HttpMethod.Post)
            {
                var item = JsonNode.Parse(body ?? "{}")!.AsObject();
                item["id"] = NewId();
                DistributeChildren(path, item);
                lock (items) items.Add(item);
                return Json(item.DeepClone(), HttpStatusCode.Created);
            }
        }

        // Item routes: {collection}/{id}
        var slash = path.LastIndexOf('/');
        if (slash > 0 && Collections.TryGetValue(path[..slash], out var parent))
        {
            var id = path[(slash + 1)..];
            JsonObject? existing;
            lock (parent) existing = parent.FirstOrDefault(i => i["id"]?.GetValue<string>() == id);
            if (existing is null) return Error(HttpStatusCode.NotFound, "ResourceNotFound", $"{path} not found");
            if (request.Method == HttpMethod.Get) return Json(existing.DeepClone());
            if (request.Method == HttpMethod.Patch)
            {
                var patch = JsonNode.Parse(body ?? "{}")!.AsObject();
                DistributeChildren(path[..slash], patch, id);
                lock (parent)
                    foreach (var (k, v) in patch) existing[k] = v?.DeepClone();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Put)
            {
                var replacement = JsonNode.Parse(body ?? "{}")!.AsObject();
                replacement["id"] = id;
                DistributeChildren(path[..slash], replacement, id);
                lock (parent) { parent.Remove(existing); parent.Add(replacement); }
                return Json(replacement.DeepClone());
            }
            if (request.Method == HttpMethod.Delete)
            {
                lock (parent) parent.Remove(existing);
                if (RecycleBinCollections.Contains(path[..slash]))
                {
                    existing["deletedDateTime"] = DateTimeOffset.UtcNow.ToString("o");
                    lock (_deleted) _deleted[id] = (path[..slash], existing);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        // Child collection that was never registered: {collection}/{id}/{property} returns the inline array, if any.
        var parts = path.Split('/');
        if (request.Method == HttpMethod.Get && parts.Length >= 3 &&
            Collections.TryGetValue(string.Join('/', parts[..^2]), out var owner))
        {
            JsonObject? item;
            lock (owner) item = owner.FirstOrDefault(i => i["id"]?.GetValue<string>() == parts[^2]);
            if (item?[parts[^1]] is JsonArray inline) return Json(new JsonObject { ["value"] = inline.DeepClone() });
        }
        return Error(HttpStatusCode.NotFound, "ResourceNotFound", $"No fake for {request.Method} {path}");
    }
}

