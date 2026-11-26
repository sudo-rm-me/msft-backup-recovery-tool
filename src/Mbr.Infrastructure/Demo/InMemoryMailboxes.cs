using System.Net;
using System.Text.Json.Nodes;
using MimeKit;

namespace Mbr.Infrastructure.Demo;

/// <summary>An in-memory Exchange mailbox with folders, per-folder message delta (immutable IDs) and MIME content.</summary>
public sealed class FakeMailbox
{
    public sealed class Folder
    {
        public required string Id;
        public string? ParentId;
        public string Name = "";
        public bool Deleted;
    }

    public sealed class Message
    {
        public required string Id;
        public string FolderId = "";
        public byte[] Mime = [];
        public string Subject = "";
        public string From = "";
        public DateTimeOffset Received = DateTimeOffset.UtcNow;
        public int ChangeVersion = 1;
        public bool IsDraft;
        public bool Deleted;
        public long Seq;
        public string InternetMessageId = "";
        /// <summary>Folders the message left (moved or deleted), with the sequence number at that time.</summary>
        public List<(string Folder, long Seq)> Left = new();
        /// <summary>The JSON a restore created the message with (null for seeded messages).</summary>
        public JsonObject? Created;
        public List<JsonObject> Attachments = new();
    }

    private readonly Func<string> _newId;
    private long _seq;
    public string UserId { get; }
    public string RootId { get; }
    public Dictionary<string, Folder> Folders { get; } = new();
    public Dictionary<string, Message> Messages { get; } = new();
    public int MimeDownloads { get; internal set; }

    internal FakeMailbox(string userId, Func<string> newId)
    {
        UserId = userId;
        _newId = newId;
        RootId = "root-" + newId();
        Folders[RootId] = new Folder { Id = RootId, Name = "Top of Information Store" };
        foreach (var f in new[] { "Inbox", "Sent Items", "Deleted Items", "Drafts" }) AddFolder("/" + f);
    }

    internal long NextSeq() => ++_seq;
    internal long Seq => _seq;

    public Folder? FindFolder(string path)
    {
        var current = Folders[RootId];
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Folders.Values.FirstOrDefault(f => !f.Deleted && f.ParentId == current.Id && f.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (current is null) return null;
        }
        return current;
    }

    public string AddFolder(string path)
    {
        var current = Folders[RootId];
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Folders.Values.FirstOrDefault(f => !f.Deleted && f.ParentId == current.Id && f.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new Folder { Id = "f-" + _newId(), ParentId = current.Id, Name = part };
                Folders[next.Id] = next;
            }
            current = next;
        }
        return current.Id;
    }

    public static byte[] BuildMime(string subject, string from, string to, string body, DateTimeOffset date, params (string Name, byte[] Content)[] attachments)
    {
        var m = new MimeMessage { Subject = subject, Date = date, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("contoso.com") };
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        var builder = new BodyBuilder { HtmlBody = $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>", TextBody = body };
        foreach (var (name, content) in attachments) builder.Attachments.Add(name, content);
        m.Body = builder.ToMessageBody();
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    public string AddMessage(string folderPath, string subject, string body = "Hello", string from = "megan@contoso.com",
        params (string Name, byte[] Content)[] attachments)
    {
        var folder = FindFolder(folderPath) ?? throw new KeyNotFoundException(folderPath);
        var date = DateTimeOffset.UtcNow.AddMinutes(-Messages.Count - 1);
        var mime = BuildMime(subject, from, $"{UserId}@contoso.com", body, date, attachments);
        var parsed = MimeMessage.Load(new MemoryStream(mime));
        var msg = new Message
        {
            Id = "m-" + _newId(), FolderId = folder.Id, Mime = mime, Subject = subject, From = from, Received = date, Seq = NextSeq(),
            InternetMessageId = "<" + parsed.MessageId + ">",
        };
        Messages[msg.Id] = msg;
        return msg.Id;
    }

    public void Move(string messageId, string folderPath)
    {
        var m = Messages[messageId];
        var to = FindFolder(folderPath) ?? throw new KeyNotFoundException(folderPath);
        m.Left.Add((m.FolderId, NextSeq()));
        m.FolderId = to.Id;
        m.ChangeVersion++;
        m.Seq = _seq;
    }

    public void Delete(string messageId)
    {
        var m = Messages[messageId];
        m.Left.Add((m.FolderId, NextSeq()));
        m.Deleted = true;
    }

    /// <summary>Hard-deletes a folder with its subfolders and messages.</summary>
    public void DeleteFolder(string path)
    {
        var f = FindFolder(path) ?? throw new KeyNotFoundException(path);
        void Mark(Folder x)
        {
            foreach (var child in Folders.Values.Where(c => c.ParentId == x.Id && !c.Deleted).ToList()) Mark(child);
            foreach (var m in Messages.Values.Where(m => m.FolderId == x.Id && !m.Deleted)) Delete(m.Id);
            x.Deleted = true;
        }
        Mark(f);
    }

    public IEnumerable<Message> In(string folderPath)
    {
        var f = FindFolder(folderPath);
        return f is null ? [] : Messages.Values.Where(m => !m.Deleted && m.FolderId == f.Id);
    }

    internal JsonObject ToJson(Message m) => new()
    {
        ["id"] = m.Id, ["subject"] = m.Subject, ["changeKey"] = $"CQAAA{m.ChangeVersion}", ["isDraft"] = m.IsDraft, ["parentFolderId"] = m.FolderId,
        ["receivedDateTime"] = m.Received.ToString("o"), ["internetMessageId"] = m.InternetMessageId, ["hasAttachments"] = m.Attachments.Count > 0,
        ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = m.From, ["name"] = m.From } },
        ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = $"{UserId}@contoso.com" } }),
    };

    internal static JsonObject ToJson(Folder f) => new() { ["id"] = f.Id, ["displayName"] = f.Name, ["parentFolderId"] = f.ParentId };
}

public partial class InMemoryGraph
{
    public Dictionary<string, FakeMailbox> Mailboxes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeMailbox AddMailbox(string userId)
    {
        var mb = new FakeMailbox(userId, NewId);
        Mailboxes[userId] = mb;
        Collection($"users/{userId}/events");
        Collection($"users/{userId}/contacts");
        Collection($"users/{userId}/calendars");
        Collection($"users/{userId}/contactFolders");
        return mb;
    }

    private HttpResponseMessage? HandleMailboxes(HttpRequestMessage request, string path, string? body)
    {
        var parts = path.Split('/');
        if (parts.Length < 3 || parts[0] != "users" || !Mailboxes.TryGetValue(parts[1], out var mb)) return null;
        var method = request.Method;
        var rest = parts[2..];
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);

        // Calendars / contact folders created by a restore get an item collection.
        if (method == HttpMethod.Post && rest is ["calendars" or "contactFolders"])
        {
            var item = JsonNode.Parse(body ?? "{}")!.AsObject();
            item["id"] = NewId();
            Collection($"users/{mb.UserId}/{rest[0]}").Add(item);
            Collection($"users/{mb.UserId}/{rest[0]}/{item["id"]}/{(rest[0] == "calendars" ? "events" : "contacts")}");
            return Json(item, HttpStatusCode.Created);
        }
        if (rest is not (["mailFolders", ..] or ["messages", ..])) return null;

        lock (mb)
        {
            switch (rest)
            {
                case ["mailFolders", "msgfolderroot"] when method == HttpMethod.Get:
                    return Json(new JsonObject { ["id"] = mb.RootId });
                case ["mailFolders", "inbox"] when method == HttpMethod.Get:
                    return mb.FindFolder("/Inbox") is { } inbox ? Json(FakeMailbox.ToJson(inbox)) : Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "Not found");
                case ["mailFolders", "delta"] when method == HttpMethod.Get:
                    return Json(new JsonObject
                    {
                        ["value"] = new JsonArray(mb.Folders.Values.Where(f => !f.Deleted && f.Id != mb.RootId).Select(f => (JsonNode)FakeMailbox.ToJson(f)).ToArray()),
                        ["@odata.deltaLink"] = $"https://graph.microsoft.com/v1.0/users/{mb.UserId}/mailFolders/delta?token={mb.Seq}",
                    });
                case ["mailFolders", var fid] when method == HttpMethod.Get:
                    return mb.Folders.TryGetValue(fid, out var folder) && !folder.Deleted
                        ? Json(FakeMailbox.ToJson(folder)) : Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "The specified object was not found in the store.");
                case ["mailFolders", var fid, "childFolders"] when method == HttpMethod.Get:
                    return Json(new JsonObject
                    {
                        ["value"] = new JsonArray(mb.Folders.Values.Where(f => !f.Deleted && f.ParentId == fid).Select(f => (JsonNode)FakeMailbox.ToJson(f)).ToArray()),
                    });
                case ["mailFolders", var fid, "childFolders"] when method == HttpMethod.Post:
                {
                    var name = JsonNode.Parse(body ?? "{}")!["displayName"]!.ToString();
                    if (mb.Folders.Values.Any(f => !f.Deleted && f.ParentId == fid && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        return Error(HttpStatusCode.Conflict, "ErrorFolderExists", "A folder with the specified name already exists.");
                    var f = new FakeMailbox.Folder { Id = "f-" + NewId(), ParentId = fid, Name = name };
                    mb.Folders[f.Id] = f;
                    return Json(FakeMailbox.ToJson(f), HttpStatusCode.Created);
                }
                case ["mailFolders", var fid, "messages", "delta"] when method == HttpMethod.Get:
                {
                    if (!mb.Folders.TryGetValue(fid, out var deltaFolder) || deltaFolder.Deleted)
                        return Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "The specified folder was not found.");
                    var token = long.TryParse(query["token"], out var t) ? t : 0;
                    var skip = int.TryParse(query["skip"], out var sk) ? sk : 0;
                    var changes = mb.Messages.Values.Where(m => !m.Deleted && m.FolderId == fid && m.Seq > token)
                        .OrderBy(m => m.Seq).Select(m => (JsonNode)mb.ToJson(m)).ToList();
                    if (token > 0)
                        changes.AddRange(mb.Messages.Values.Where(m => m.Left.Any(l => l.Folder == fid && l.Seq > token) && (m.Deleted || m.FolderId != fid))
                            .Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["@removed"] = new JsonObject { ["reason"] = "deleted" } }));
                    var page = changes.Skip(skip).Take(PageSize).ToArray();
                    var result = new JsonObject { ["value"] = new JsonArray(page.Select(n => n.DeepClone()).ToArray()) };
                    var baseUrl = $"https://graph.microsoft.com/v1.0/users/{mb.UserId}/mailFolders/{fid}/messages/delta";
                    if (skip + PageSize < changes.Count) result["@odata.nextLink"] = $"{baseUrl}?token={token}&skip={skip + PageSize}";
                    else result["@odata.deltaLink"] = $"{baseUrl}?token={mb.Seq}";
                    return Json(result);
                }
                case ["mailFolders", var fid, "messages"] when method == HttpMethod.Get:
                {
                    var filter = query["$filter"] ?? "";
                    var wanted = System.Text.RegularExpressions.Regex.Match(filter, "internetMessageId eq '(.*)'").Groups[1].Value.Replace("''", "'");
                    return Json(new JsonObject
                    {
                        ["value"] = new JsonArray(mb.Messages.Values.Where(m => !m.Deleted && m.FolderId == fid && (wanted == "" || m.InternetMessageId == wanted))
                            .Select(m => (JsonNode)mb.ToJson(m)).ToArray()),
                    });
                }
                case ["mailFolders", var fid, "messages"] when method == HttpMethod.Post:
                {
                    var json = JsonNode.Parse(body ?? "{}")!.AsObject();
                    var flags = (json["singleValueExtendedProperties"] as JsonArray)?.FirstOrDefault(p => p?["id"]?.ToString() == "Integer 0x0E07")?["value"]?.ToString();
                    var date = DateTimeOffset.TryParse((json["singleValueExtendedProperties"] as JsonArray)?
                        .FirstOrDefault(p => p?["id"]?.ToString() == "SystemTime 0x0E06")?["value"]?.ToString(), out var d) ? d : DateTimeOffset.UtcNow;
                    var from = json["from"]?["emailAddress"]?["address"]?.ToString() ?? "unknown@contoso.com";
                    var subject = json["subject"]?.ToString() ?? "";
                    var mime = FakeMailbox.BuildMime(subject, from, $"{mb.UserId}@contoso.com", json["body"]?["content"]?.ToString() ?? "", d);
                    var m = new FakeMailbox.Message
                    {
                        Id = "m-" + NewId(), FolderId = fid, Subject = subject, From = from, Received = date, Mime = mime, IsDraft = flags != "1",
                        Seq = mb.NextSeq(), Created = json,
                        InternetMessageId = json["internetMessageId"]?.ToString() ?? "<" + MimeMessage.Load(new MemoryStream(mime)).MessageId + ">",
                    };
                    mb.Messages[m.Id] = m;
                    return Json(mb.ToJson(m), HttpStatusCode.Created);
                }
                case ["messages", var mid, "$value"] when method == HttpMethod.Get:
                    if (!mb.Messages.TryGetValue(mid, out var msg) || msg.Deleted) return Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "Not found");
                    mb.MimeDownloads++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msg.Mime) };
                case ["messages", var mid] when method == HttpMethod.Delete:
                    if (mb.Messages.TryGetValue(mid, out var del) && !del.Deleted) mb.Delete(mid);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ["messages", var mid, "attachments"] when method == HttpMethod.Post:
                    mb.Messages[mid].Attachments.Add(JsonNode.Parse(body ?? "{}")!.AsObject());
                    return Json(new JsonObject { ["id"] = NewId() }, HttpStatusCode.Created);
                case ["messages", var mid, "attachments", "createUploadSession"] when method == HttpMethod.Post:
                {
                    var item = JsonNode.Parse(body ?? "{}")!["AttachmentItem"]!.AsObject();
                    var target = mb.Messages[mid];
                    var url = NewUploadSession(data =>
                    {
                        lock (mb)
                        {
                            target.Attachments.Add(new JsonObject
                            {
                                ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = item["name"]?.DeepClone(), ["contentType"] = item["contentType"]?.DeepClone(),
                                ["contentBytes"] = Convert.ToBase64String(data), ["uploaded"] = true,
                            });
                        }
                        return new JsonObject { ["id"] = NewId() };
                    });
                    return Json(new JsonObject { ["uploadUrl"] = url });
                }
            }
        }
        return Error(HttpStatusCode.NotFound, "ErrorItemNotFound", $"No fake for {method} {path}");
    }
}
