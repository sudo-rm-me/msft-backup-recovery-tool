using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Files;

/// <summary>Restores files and folders into OneDrive / SharePoint document libraries.</summary>
public sealed class DriveRestorer(ITenantContentStore content) : IDataRestorer
{
    /// <summary>Files up to this size use a single PUT; larger ones an upload session.</summary>
    public const int SimpleUploadLimit = 4 * 1024 * 1024;
    /// <summary>Upload session fragments must be multiples of 320 KiB.</summary>
    public const int FragmentSize = 16 * 327_680;

    public bool Handles(string containerKind) => containerKind == "drive";

    public async Task RestoreAsync(DataRestoreContext ctx, CancellationToken ct)
    {
        var driveId = await TargetDriveAsync(ctx, ct);
        var drive = $"/v1.0/drives/{Uri.EscapeDataString(driveId)}";
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = await ctx.Graph.GetAsync($"{drive}/root?$select=id", ct);
        folders["/"] = root!["id"]!.ToString();

        // Parents before children.
        foreach (var e in ctx.Entries.OrderBy(e => e.RelativeDir.Count(ch => ch == '/') + (e.RelativeDir.Length > 0 ? 1 : 0)).ThenByDescending(e => e.Version.IsFolder))
        {
            var dir = ctx.DestinationDir(e);
            try
            {
                if (e.Version.IsFolder)
                {
                    await EnsureFolderAsync(ctx.Graph, drive, DataRestoreContext.Combine(dir, e.Version.Name), folders, ct);
                    ctx.Result.Restored++;
                    continue;
                }
                var parentId = await EnsureFolderAsync(ctx.Graph, drive, dir, folders, ct);
                if (await UploadAsync(ctx, drive, parentId, e.Version, ct)) ctx.Result.Restored++;
                else ctx.Result.Skipped++;
            }
            catch (Exception ex) when (ex is ApiException or IOException or InvalidDataException)
            {
                await ctx.FailAsync(e, ex.Message);
            }
        }
        await ctx.LogAsync($"Files restored to {(ctx.IntoFolder ? $"'{ctx.FolderName}' in " : "")}{ctx.Target.DisplayName}");
    }

    private async Task<string> TargetDriveAsync(DataRestoreContext ctx, CancellationToken ct)
    {
        if (ctx.SameSource) return ctx.Container.ExternalId;
        if (ctx.Target.Kind == DataSourceKind.OneDrive)
        {
            var d = await ctx.Graph.GetAsync($"/v1.0/users/{ctx.Target.ExternalId}/drive?$select=id", ct);
            return d!["id"]!.ToString();
        }
        // Another site: the library with the same name, else the first one.
        var drives = new List<JsonObject>();
        await foreach (var d in ctx.Graph.ListAsync($"/v1.0/sites/{ctx.Target.ExternalId}/drives?$select=id,name", ct)) drives.Add(d);
        var match = drives.FirstOrDefault(d => d["name"]?.ToString() == ctx.Container.Name) ?? drives.FirstOrDefault()
                    ?? throw new InvalidOperationException($"{ctx.Target.DisplayName} has no document library.");
        return match["id"]!.ToString();
    }

    /// <summary>Finds or creates each folder along a path; returns the last folder's ID.</summary>
    private static async Task<string> EnsureFolderAsync(IApiClient graph, string drive, string path, Dictionary<string, string> cache, CancellationToken ct)
    {
        path = DataRestoreContext.Combine(path, "");
        if (cache.TryGetValue(path, out var known)) return known;
        var slash = path.LastIndexOf('/');
        var parentPath = slash <= 0 ? "/" : path[..slash];
        var name = path[(slash + 1)..];
        var parentId = await EnsureFolderAsync(graph, drive, parentPath, cache, ct);
        var existing = await graph.TryGetAsync($"{drive}/items/{parentId}:/{Uri.EscapeDataString(name)}?$select=id,folder", ct);
        if (existing?["folder"] is null || existing["id"] is null)
        {
            try
            {
                existing = await graph.SendAsync(HttpMethod.Post, $"{drive}/items/{parentId}/children",
                    new JsonObject { ["name"] = name, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = existing is null ? "fail" : "rename" }, ct: ct);
            }
            catch (ApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
            {
                existing = await graph.GetAsync($"{drive}/items/{parentId}:/{Uri.EscapeDataString(name)}?$select=id", ct);
            }
        }
        return cache[path] = existing!["id"]!.ToString();
    }

    /// <summary>Uploads one file; false if it was skipped because a file with that name exists.</summary>
    private async Task<bool> UploadAsync(DataRestoreContext ctx, string drive, string parentId, DataItemVersion v, CancellationToken ct)
    {
        var behavior = ctx.Request.Conflict == "skip" ? "fail" : ctx.Request.Conflict;
        var target = $"{drive}/items/{parentId}:/{Uri.EscapeDataString(v.Name)}:";
        try
        {
            if (v.Size <= SimpleUploadLimit)
            {
                var bytes = await ctx.Reader.ReadContentAsync(ctx.Tenant, v, ct);
                using var resp = await ctx.Graph.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Put,
                    Abs(ctx.Graph, $"{target}/content?@microsoft.graph.conflictBehavior={behavior}")) { Content = new ByteArrayContent(bytes) }, ct);
                await EnsureOkAsync(resp, ct);
                ctx.Result.Bytes += bytes.Length;
                return true;
            }
            var session = await ctx.Graph.SendAsync(HttpMethod.Post, $"{target}/createUploadSession",
                new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = behavior } }, ct: ct);
            var uploadUrl = session!["uploadUrl"]!.ToString();
            await UploadSessionAsync(ctx.Graph, uploadUrl, ChunksOf(ctx.Tenant, v, ct), v.Size, ct);
            ctx.Result.Bytes += v.Size;
            return true;
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.Conflict && ctx.Request.Conflict == "skip")
        {
            return false;
        }
    }

    private async IAsyncEnumerable<byte[]> ChunksOf(Tenant tenant, DataItemVersion v, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var hash in v.Chunks.Split(',', StringSplitOptions.RemoveEmptyEntries))
            yield return await content.GetObjectAsync(tenant, v.StorageTargetId, hash, ct);
    }

    /// <summary>Sends stored chunks to an upload session in fragments (multiples of 320 KiB, final one shorter).</summary>
    public static async Task UploadSessionAsync(IApiClient graph, string uploadUrl, IAsyncEnumerable<byte[]> chunks, long total, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        long sent = 0;
        async Task SendAsync(byte[] fragment)
        {
            var start = sent;
            var end = sent + fragment.Length - 1;
            using var resp = await graph.SendRawAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(fragment) };
                req.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
                return req;
            }, ct);
            await EnsureOkAsync(resp, ct);
            sent += fragment.Length;
        }
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            buffer.Write(chunk);
            while (buffer.Length >= FragmentSize)
            {
                var all = buffer.ToArray();
                await SendAsync(all[..FragmentSize]);
                buffer = new MemoryStream();
                buffer.Write(all, FragmentSize, all.Length - FragmentSize);
            }
        }
        if (buffer.Length > 0 || sent == 0) await SendAsync(buffer.ToArray());
    }

    private static string Abs(IApiClient graph, string path) => graph.AbsoluteUrl(path);

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        string? code = null, message = text;
        try
        {
            var err = JsonNode.Parse(text)?["error"];
            code = err?["code"]?.ToString();
            message = err?["message"]?.ToString() ?? text;
        }
        catch (System.Text.Json.JsonException) { }
        throw new ApiException(resp.StatusCode, code, message ?? resp.ReasonPhrase ?? "", text);
    }
}
