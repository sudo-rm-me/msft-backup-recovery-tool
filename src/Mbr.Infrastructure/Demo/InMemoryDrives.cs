using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>An in-memory OneDrive / document library with a Graph-like delta feed.</summary>
public sealed class FakeDrive
{
    internal sealed class Entry
    {
        public required string Id;
        public string? ParentId;
        public string Name = "";
        public bool IsFolder;
        public byte[] Content = [];
        public long Seq;
        public bool Deleted;
        public int ETagVersion;
        public int CTagVersion;
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
    }

    private readonly Func<string> _newId;
    internal readonly Dictionary<string, Entry> Entries = new();
    private long _seq;
    public string Id { get; }
    public string Name { get; set; }
    public string RootId { get; }
    /// <summary>Delta tokens below this value are rejected with 410 (expired).</summary>
    public long MinimumToken { get; set; }
    /// <summary>Downloads of items for which this returns true fail with 423 Locked.</summary>
    public Func<string, bool> FailDownload { get; set; } = _ => false;
    public int Downloads { get; internal set; }

    internal FakeDrive(string id, string name, Func<string> newId)
    {
        Id = id;
        Name = name;
        _newId = newId;
        RootId = newId();
        Entries[RootId] = new Entry { Id = RootId, Name = "root", IsFolder = true, Seq = ++_seq };
    }

    internal long Seq => _seq;

    private Entry? Find(string path)
    {
        var current = Entries[RootId];
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Entries.Values.FirstOrDefault(e => !e.Deleted && e.ParentId == current.Id && e.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    public string IdOf(string path) => (Find(path) ?? throw new KeyNotFoundException(path)).Id;
    public bool Exists(string path) => Find(path) != null;
    public byte[] Read(string path) => (Find(path) ?? throw new KeyNotFoundException(path)).Content;
    public List<string> List(string path) =>
        Entries.Values.Where(e => !e.Deleted && e.ParentId == (Find(path) ?? throw new KeyNotFoundException(path)).Id).Select(e => e.Name).OrderBy(n => n).ToList();

    internal Entry? Child(string parentId, string name) =>
        Entries.Values.FirstOrDefault(e => !e.Deleted && e.ParentId == parentId && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private string FreeName(string parentId, string name)
    {
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 1; ; i++)
            if (Child(parentId, $"{stem} {i}{ext}") is null) return $"{stem} {i}{ext}";
    }

    /// <summary>Writes a file under a parent with Graph's conflict behaviours; null when it exists and behavior is "fail".</summary>
    internal Entry? Write(string parentId, string name, byte[] content, string behavior)
    {
        var existing = Child(parentId, name);
        if (existing != null && behavior == "fail") return null;
        if (existing != null && behavior == "rename") { name = FreeName(parentId, name); existing = null; }
        var e = existing ?? new Entry { Id = _newId(), ParentId = parentId, Name = name };
        Entries[e.Id] = e;
        e.Content = content;
        e.ETagVersion++;
        e.CTagVersion++;
        e.Modified = DateTimeOffset.UtcNow;
        e.Seq = ++_seq;
        return e;
    }

    internal Entry? CreateFolder(string parentId, string name, string behavior)
    {
        var existing = Child(parentId, name);
        if (existing != null && behavior == "fail") return null;
        if (existing != null && behavior != "rename") return existing;
        if (existing != null) name = FreeName(parentId, name);
        var e = new Entry { Id = _newId(), ParentId = parentId, Name = name, IsFolder = true, Seq = ++_seq };
        Entries[e.Id] = e;
        return e;
    }

    public string AddFolder(string path)
    {
        var current = Entries[RootId];
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Entries.Values.FirstOrDefault(e => !e.Deleted && e.ParentId == current.Id && e.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new Entry { Id = _newId(), ParentId = current.Id, Name = part, IsFolder = true, Seq = ++_seq };
                Entries[next.Id] = next;
            }
            current = next;
        }
        return current.Id;
    }

    /// <summary>Creates or overwrites a file (new content: both eTag and cTag change).</summary>
    public string PutFile(string path, byte[] content)
    {
        var slash = path.LastIndexOf('/');
        var folder = slash <= 0 ? RootId : AddFolder(path[..slash]);
        var name = path[(slash + 1)..];
        var e = Entries.Values.FirstOrDefault(x => !x.Deleted && x.ParentId == folder && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (e is null)
        {
            e = new Entry { Id = _newId(), ParentId = folder, Name = name };
            Entries[e.Id] = e;
        }
        e.Content = content;
        e.ETagVersion++;
        e.CTagVersion++;
        e.Modified = DateTimeOffset.UtcNow;
        e.Seq = ++_seq;
        return e.Id;
    }

    public string PutFile(string path, string text) => PutFile(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Renames a file or folder (metadata change: eTag changes, cTag doesn't).</summary>
    public void Rename(string path, string newName)
    {
        var e = Find(path) ?? throw new KeyNotFoundException(path);
        e.Name = newName;
        e.ETagVersion++;
        e.Seq = ++_seq;
    }

    /// <summary>Deletes an item and everything under it.</summary>
    public void Delete(string path)
    {
        var e = Find(path) ?? throw new KeyNotFoundException(path);
        void Mark(Entry x)
        {
            foreach (var child in Entries.Values.Where(c => c.ParentId == x.Id && !c.Deleted).ToList()) Mark(child);
            x.Deleted = true;
            x.Seq = ++_seq;
        }
        Mark(e);
    }

    /// <summary>Puts a deleted item back (as the recycle bin would), with the same ID.</summary>
    public void Undelete(string id)
    {
        var e = Entries[id];
        e.Deleted = false;
        e.Seq = ++_seq;
    }

    internal JsonObject ToJson(Entry e)
    {
        var o = new JsonObject { ["id"] = e.Id };
        if (e.Deleted)
        {
            o["deleted"] = new JsonObject { ["state"] = "deleted" };
            o["parentReference"] = new JsonObject { ["id"] = e.ParentId, ["driveId"] = Id };
            return o;
        }
        o["name"] = e.Name;
        o["eTag"] = $"\"{{{e.Id}}},{e.ETagVersion}\"";
        o["cTag"] = $"\"c:{{{e.Id}}},{e.CTagVersion}\"";
        o["lastModifiedDateTime"] = e.Modified.ToString("o");
        o["lastModifiedBy"] = new JsonObject { ["user"] = new JsonObject { ["email"] = "adele@contoso.onmicrosoft.com", ["displayName"] = "Adele Vance" } };
        if (e.Id == RootId) o["root"] = new JsonObject();
        else o["parentReference"] = new JsonObject { ["id"] = e.ParentId, ["driveId"] = Id };
        if (e.IsFolder) o["folder"] = new JsonObject { ["childCount"] = Entries.Values.Count(c => c.ParentId == e.Id && !c.Deleted) };
        else
        {
            o["size"] = e.Content.LongLength;
            o["file"] = new JsonObject
            {
                ["mimeType"] = e.Name.EndsWith(".docx") ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document" : "application/octet-stream",
                ["hashes"] = new JsonObject { ["sha256Hash"] = Convert.ToHexString(SHA256.HashData(e.Content)) },
            };
        }
        return o;
    }
}

public partial class InMemoryGraph
{
    private sealed record Upload(Func<byte[], JsonObject?> Complete)
    {
        public MemoryStream Received { get; } = new();
    }

    private readonly Dictionary<string, Upload> _uploads = new();

    /// <summary>Registers an upload session (used by other fakes, e.g. large mail attachments).</summary>
    internal string NewUploadSession(Func<byte[], JsonObject?> complete)
    {
        var session = Guid.NewGuid().ToString("N");
        lock (_uploads) _uploads[session] = new Upload(complete);
        return $"https://upload.fake/{session}";
    }

    /// <summary>Upload-session URLs (https://upload.fake/{session}): accepts Content-Range fragments, completes on the last one.</summary>
    private HttpResponseMessage? HandleUploads(HttpRequestMessage request, byte[]? bytes)
    {
        if (request.RequestUri!.Host != "upload.fake") return null;
        if (request.Headers.Authorization != null)
            return Error(HttpStatusCode.Unauthorized, "unauthenticated", "Upload URLs are pre-authorized; don't send a token.");
        var session = request.RequestUri.AbsolutePath.Trim('/');
        Upload? upload;
        lock (_uploads)
        {
            if (!_uploads.TryGetValue(session, out upload)) return Error(HttpStatusCode.NotFound, "itemNotFound", "Upload session not found.");
            var range = request.Content?.Headers.ContentRange ?? throw new InvalidOperationException("Content-Range is required.");
            if (range.From != upload.Received.Length) return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange", "Unexpected range.");
            upload.Received.Write(bytes ?? []);
            if (upload.Received.Length < range.Length)
                return Json(new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{upload.Received.Length}-") }, HttpStatusCode.Accepted);
            _uploads.Remove(session);
            UploadSessionsCompleted++;
        }
        return upload.Complete(upload.Received.ToArray()) is { } item
            ? Json(item, HttpStatusCode.Created)
            : Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
    }

    public int UploadSessionsCompleted { get; private set; }

    public Dictionary<string, FakeDrive> Drives { get; } = new();
    /// <summary>Drive owners: "users/{id}" or "sites/{id}" → drive IDs.</summary>
    public Dictionary<string, List<string>> DriveOwners { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeDrive AddDrive(string owner, string name = "Documents")
    {
        var d = new FakeDrive("b!" + NewId().Replace("-", ""), name, NewId);
        Drives[d.Id] = d;
        if (!DriveOwners.TryGetValue(owner, out var list)) DriveOwners[owner] = list = new List<string>();
        list.Add(d.Id);
        return d;
    }

    private static JsonObject DriveJson(FakeDrive d) =>
        new() { ["id"] = d.Id, ["name"] = d.Name, ["webUrl"] = $"https://contoso.sharepoint.com/{d.Name}", ["driveType"] = "documentLibrary" };

    private HttpResponseMessage? HandleDrives(HttpRequestMessage request, string path, string? body, byte[]? bytes)
    {
        var method = request.Method;
        var query = request.RequestUri!.Query;
        var parts = path.Split('/');
        if (method == HttpMethod.Get && parts is ["users", var uid, "drive"])
            return DriveOwners.TryGetValue($"users/{uid}", out var ids) && ids.Count > 0
                ? Json(DriveJson(Drives[ids[0]]))
                : Error(HttpStatusCode.NotFound, "itemNotFound", "The user's OneDrive is not provisioned.");
        if (method == HttpMethod.Get && parts is ["sites", var sid, "drives"])
            return Json(new JsonObject
            {
                ["value"] = new JsonArray((DriveOwners.GetValueOrDefault($"sites/{sid}") ?? []).Select(id => (JsonNode)DriveJson(Drives[id])).ToArray()),
            });
        if (parts is not ["drives", var driveId, ..] || !Drives.TryGetValue(driveId, out var drive)) return null;

        lock (drive)
        {
            if (method == HttpMethod.Get && parts is [_, _, "root"]) return Json(drive.ToJson(drive.Entries[drive.RootId]));

            // Path addressing: drives/{d}/items/{parentId}:/{name}[:/content | :/createUploadSession]
            var colon = System.Text.RegularExpressions.Regex.Match(path, @"^drives/[^/]+/items/([^/:]+):/([^:]+?)(?::/(content|createUploadSession))?:?$");
            if (colon.Success)
            {
                var parentId = colon.Groups[1].Value;
                var name = colon.Groups[2].Value;
                var action = colon.Groups[3].Value;
                var behavior = System.Web.HttpUtility.ParseQueryString(query)["@microsoft.graph.conflictBehavior"] ?? "fail";
                if (!drive.Entries.TryGetValue(parentId, out var parentEntry) || parentEntry.Deleted)
                    return Error(HttpStatusCode.NotFound, "itemNotFound", "Parent not found.");
                if (method == HttpMethod.Get && action == "")
                {
                    var hit = drive.Child(parentId, name);
                    return hit is null ? Error(HttpStatusCode.NotFound, "itemNotFound", "The resource could not be found.") : Json(drive.ToJson(hit));
                }
                if (method == HttpMethod.Put && action == "content")
                    return drive.Write(parentId, name, bytes ?? [], behavior) is { } written
                        ? Json(drive.ToJson(written), HttpStatusCode.Created)
                        : Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
                if (method == HttpMethod.Post && action == "createUploadSession")
                {
                    behavior = JsonNode.Parse(body ?? "{}")?["item"]?["@microsoft.graph.conflictBehavior"]?.ToString() ?? "fail";
                    if (behavior == "fail" && drive.Child(parentId, name) != null)
                        return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
                    var b = behavior;
                    var session = Guid.NewGuid().ToString("N");
                    lock (_uploads) _uploads[session] = new Upload(data => { lock (drive) return drive.Write(parentId, name, data, b) is { } w ? drive.ToJson(w) : null; });
                    return Json(new JsonObject { ["uploadUrl"] = $"https://upload.fake/{session}", ["expirationDateTime"] = DateTimeOffset.UtcNow.AddHours(1).ToString("o") });
                }
            }
            // drives/{d}/items/{id}/children (create folder)
            if (method == HttpMethod.Post && parts is [_, _, "items", var pid, "children"])
            {
                var req = JsonNode.Parse(body ?? "{}")!;
                var behavior = req["@microsoft.graph.conflictBehavior"]?.ToString() ?? "fail";
                var created = drive.CreateFolder(pid, req["name"]!.ToString(), behavior);
                return created is null
                    ? Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.")
                    : Json(drive.ToJson(created), HttpStatusCode.Created);
            }

            // drives/{d}/root/delta
            if (method == HttpMethod.Get && parts is [_, _, "root", "delta"])
            {
                var q = System.Web.HttpUtility.ParseQueryString(query);
                var token = long.TryParse(q["token"], out var t) ? t : 0;
                var skip = int.TryParse(q["skip"], out var sk) ? sk : 0;
                if (q["token"] != null && token < drive.MinimumToken)
                    return Error(HttpStatusCode.Gone, "resyncRequired", "The delta token has expired.");
                // A full sync (no token) doesn't report deletions; an incremental one does.
                var changed = drive.Entries.Values.Where(e => e.Seq > token && (token > 0 || !e.Deleted)).OrderBy(e => e.Seq).ToList();
                var max = drive.Seq;
                var page = changed.Skip(skip).Take(PageSize).Select(e => (JsonNode)drive.ToJson(e)).ToArray();
                var result = new JsonObject { ["value"] = new JsonArray(page) };
                var baseUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/delta";
                if (skip + PageSize < changed.Count) result["@odata.nextLink"] = $"{baseUrl}?token={token}&skip={skip + PageSize}";
                else result["@odata.deltaLink"] = $"{baseUrl}?token={max}";
                return Json(result);
            }
            // drives/{d}/items/{id}[/content]
            if (method == HttpMethod.Get && parts is [_, _, "items", var itemId, ..])
            {
                if (!drive.Entries.TryGetValue(itemId, out var e) || e.Deleted)
                    return Error(HttpStatusCode.NotFound, "itemNotFound", "The resource could not be found.");
                if (parts.Length == 4) return Json(drive.ToJson(e));
                if (parts is [.., "content"])
                {
                    if (drive.FailDownload(itemId)) return Error(HttpStatusCode.Locked, "resourceLocked", "The file is locked.");
                    drive.Downloads++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(e.Content) };
                }
            }
        }
        return Error(HttpStatusCode.NotFound, "itemNotFound", $"No fake for {method} {path}");
    }
}
