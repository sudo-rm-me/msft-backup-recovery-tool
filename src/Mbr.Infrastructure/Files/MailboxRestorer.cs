using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using MimeKit;

namespace Mbr.Infrastructure.Files;

/// <summary>
/// Restores mail (from MIME, as received non-draft messages with their original dates and attachments), calendar events
/// (without attendees, so nobody gets invitations) and contacts.
/// </summary>
public sealed class MailboxRestorer : IDataRestorer
{
    /// <summary>Attachments up to this size are posted inline; larger ones use an upload session.</summary>
    public const int InlineAttachmentLimit = 3 * 1024 * 1024;

    public bool Handles(string containerKind) => containerKind is "mail" or "calendar" or "contacts";

    public Task RestoreAsync(DataRestoreContext ctx, CancellationToken ct) => ctx.Container.Kind switch
    {
        "mail" => RestoreMailAsync(ctx, ct),
        "calendar" => RestoreFlatAsync(ctx, "calendars", "events", "name", PrepareEvent, ct),
        _ => RestoreFlatAsync(ctx, "contactFolders", "contacts", "displayName", PrepareContact, ct),
    };

    private static string User(DataRestoreContext ctx) => $"/v1.0/users/{ctx.Target.ExternalId}";

    // ------------------------------------------------------------------ mail

    private async Task RestoreMailAsync(DataRestoreContext ctx, CancellationToken ct)
    {
        var user = User(ctx);
        var graph = ctx.Graph;
        var root = (await graph.SendAsync(HttpMethod.Get, $"{user}/mailFolders/msgfolderroot?$select=id", null, MailboxBackup.Headers, ct))!["id"]!.ToString();
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["/"] = root };

        foreach (var e in ctx.Entries.OrderBy(e => e.RelativeDir.Length).ThenByDescending(e => e.Version.IsFolder))
        {
            try
            {
                var folderId = await DestinationFolderAsync(ctx, e, root, folders, ct);
                if (e.Version.IsFolder) { ctx.Result.Restored++; continue; }
                var mime = await ctx.Reader.ReadContentAsync(ctx.Tenant, e.Version, ct);
                var message = MimeMessage.Load(new MemoryStream(mime));
                if (ctx.Request.Conflict != "rename" && message.MessageId is { Length: > 0 } mid)
                {
                    var existing = await FindByInternetIdAsync(graph, user, folderId, "<" + mid + ">", ct);
                    if (existing != null && ctx.Request.Conflict == "skip") { ctx.Result.Skipped++; continue; }
                    if (existing != null) await graph.SendAsync(HttpMethod.Delete, $"{user}/messages/{Uri.EscapeDataString(existing)}", ct: ct);
                }
                await CreateMessageAsync(graph, user, folderId, message, ct);
                ctx.Result.Restored++;
                ctx.Result.Bytes += mime.Length;
            }
            catch (Exception ex) when (ex is ApiException or IOException or FormatException or InvalidDataException)
            {
                await ctx.FailAsync(e, ex.Message);
            }
        }
        await ctx.LogAsync($"Mail restored to {(ctx.IntoFolder ? $"folder '{ctx.FolderName}' in " : "")}{ctx.Target.DisplayName}'s mailbox");
    }

    /// <summary>The folder an entry goes into: the original folder if it still exists, else recreated by name.</summary>
    private static async Task<string> DestinationFolderAsync(DataRestoreContext ctx, RestoreEntry e, string root, Dictionary<string, string> cache, CancellationToken ct)
    {
        var user = User(ctx);
        if (!ctx.IntoFolder && e.RelativeDir.Length == 0 && e.Version.ParentItemId is { } parent && parent != ctx.Container.RootItemId)
        {
            // In place: the original folder, if it's still there.
            if (await ctx.Graph.TryGetAsync($"{user}/mailFolders/{Uri.EscapeDataString(parent)}?$select=id", ct) is { } f)
            {
                var id = f["id"]!.ToString();
                return e.Version.IsFolder ? await EnsurePathAsync(ctx, id, [e.Version.Name], cache, ct) : id;
            }
        }
        var dir = ctx.DestinationDir(e);
        if (e.Version.IsFolder) dir = DataRestoreContext.Combine(dir, e.Version.Name);
        return await EnsurePathAsync(ctx, root, dir.Split('/', StringSplitOptions.RemoveEmptyEntries), cache, ct, dir);
    }

    private static async Task<string> EnsurePathAsync(DataRestoreContext ctx, string startId, string[] names, Dictionary<string, string> cache, CancellationToken ct, string? cacheKey = null)
    {
        if (cacheKey != null && cache.TryGetValue(cacheKey, out var known)) return known;
        var user = User(ctx);
        var current = startId;
        foreach (var name in names)
        {
            string? found = null;
            await foreach (var child in ctx.Graph.ListAsync($"{user}/mailFolders/{Uri.EscapeDataString(current)}/childFolders?$select=id,displayName&$top=100", ct))
                if (string.Equals(child["displayName"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)) { found = child["id"]!.ToString(); break; }
            found ??= (await ctx.Graph.SendAsync(HttpMethod.Post, $"{user}/mailFolders/{Uri.EscapeDataString(current)}/childFolders",
                new JsonObject { ["displayName"] = name }, ct: ct))!["id"]!.ToString();
            current = found;
        }
        if (cacheKey != null) cache[cacheKey] = current;
        return current;
    }

    private static async Task<string?> FindByInternetIdAsync(IApiClient graph, string user, string folderId, string internetMessageId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"internetMessageId eq '{internetMessageId.Replace("'", "''")}'");
        await foreach (var m in graph.ListAsync($"{user}/mailFolders/{Uri.EscapeDataString(folderId)}/messages?$filter={filter}&$select=id", ct))
            return m["id"]?.ToString();
        return null;
    }

    private static JsonArray Recipients(InternetAddressList list) =>
        new(list.Mailboxes.Select(m => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = m.Name ?? m.Address, ["address"] = m.Address } }).ToArray());

    /// <summary>Builds the Graph message: non-draft (PR_MESSAGE_FLAGS = read), with the original sent/received times.</summary>
    public static JsonObject ToGraphMessage(MimeMessage m)
    {
        var date = (m.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : m.Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var html = m.HtmlBody;
        var json = new JsonObject
        {
            ["subject"] = m.Subject ?? "",
            ["body"] = new JsonObject { ["contentType"] = html != null ? "html" : "text", ["content"] = html ?? m.TextBody ?? "" },
            ["toRecipients"] = Recipients(m.To),
            ["ccRecipients"] = Recipients(m.Cc),
            ["bccRecipients"] = Recipients(m.Bcc),
            ["replyTo"] = Recipients(m.ReplyTo),
            ["importance"] = m.Importance switch { MessageImportance.High => "high", MessageImportance.Low => "low", _ => "normal" },
            ["isRead"] = true,
            ["internetMessageId"] = m.MessageId is { Length: > 0 } id ? "<" + id + ">" : null,
            ["singleValueExtendedProperties"] = new JsonArray(
                new JsonObject { ["id"] = "Integer 0x0E07", ["value"] = "1" },      // PR_MESSAGE_FLAGS: read, not a draft
                new JsonObject { ["id"] = "SystemTime 0x0E06", ["value"] = date },  // PR_MESSAGE_DELIVERY_TIME
                new JsonObject { ["id"] = "SystemTime 0x0039", ["value"] = date }), // PR_CLIENT_SUBMIT_TIME
        };
        if (m.From.Mailboxes.FirstOrDefault() is { } from)
        {
            json["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = from.Name ?? from.Address, ["address"] = from.Address } };
            json["sender"] = json["from"]!.DeepClone();
        }
        return json;
    }

    private static IEnumerable<MimeEntity> AttachmentParts(MimeMessage m) =>
        m.BodyParts.Where(p => p.IsAttachment || (p is MimePart { ContentId: not null } && p is not TextPart));

    private static async Task CreateMessageAsync(IApiClient graph, string user, string folderId, MimeMessage message, CancellationToken ct)
    {
        var created = await graph.SendAsync(HttpMethod.Post, $"{user}/mailFolders/{Uri.EscapeDataString(folderId)}/messages", ToGraphMessage(message),
            MailboxBackup.Headers, ct);
        var id = created!["id"]!.ToString();
        foreach (var part in AttachmentParts(message))
        {
            using var ms = new MemoryStream();
            string name, contentType;
            if (part is MessagePart { Message: { } inner })
            {
                await inner.WriteToAsync(ms, ct);
                name = (inner.Subject ?? "attached message") + ".eml";
                contentType = "message/rfc822";
            }
            else
            {
                if (part is not MimePart p) continue;
                if (p.Content != null) await p.Content.DecodeToAsync(ms, ct);
                name = p.FileName ?? p.ContentId ?? "attachment";
                contentType = p.ContentType.MimeType;
            }
            var bytes = ms.ToArray();
            var inline = part.ContentDisposition?.Disposition == ContentDisposition.Inline && part.ContentId != null;
            if (bytes.Length <= InlineAttachmentLimit)
            {
                var att = new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = name, ["contentType"] = contentType,
                    ["contentBytes"] = Convert.ToBase64String(bytes), ["isInline"] = inline,
                };
                if (part.ContentId != null) att["contentId"] = part.ContentId;
                await graph.SendAsync(HttpMethod.Post, $"{user}/messages/{Uri.EscapeDataString(id)}/attachments", att, ct: ct);
            }
            else
            {
                var session = await graph.SendAsync(HttpMethod.Post, $"{user}/messages/{Uri.EscapeDataString(id)}/attachments/createUploadSession",
                    new JsonObject
                    {
                        ["AttachmentItem"] = new JsonObject
                        {
                            ["attachmentType"] = "file", ["name"] = name, ["size"] = bytes.Length, ["contentType"] = contentType, ["isInline"] = inline,
                        },
                    }, ct: ct);
                await DriveRestorer.UploadSessionAsync(graph, session!["uploadUrl"]!.ToString(), One(bytes), bytes.Length, ct);
            }
        }
    }

    private static async IAsyncEnumerable<byte[]> One(byte[] b) { await Task.CompletedTask; yield return b; }

    // ------------------------------------------------------------------ calendar / contacts

    private static readonly string[] EventProperties =
        ["subject", "body", "start", "end", "location", "locations", "isAllDay", "recurrence", "showAs", "sensitivity", "importance", "categories", "isReminderOn", "reminderMinutesBeforeStart"];

    private static JsonObject? PrepareEvent(JsonObject saved)
    {
        // Occurrences and exceptions come back with their series.
        if (saved["type"]?.ToString() is "occurrence" or "exception") return null;
        var body = new JsonObject();
        foreach (var p in EventProperties) if (saved[p] is { } v) body[p] = v.DeepClone();
        // Attendees aren't re-added: creating an event with attendees sends them invitations. Keep them as a note instead.
        if (saved["attendees"] is JsonArray { Count: > 0 } attendees)
        {
            var names = string.Join(", ", attendees.Select(a => a?["emailAddress"]?["name"]?.ToString() ?? a?["emailAddress"]?["address"]?.ToString()).Where(x => x != null));
            var b = body["body"] as JsonObject ?? new JsonObject { ["contentType"] = "text", ["content"] = "" };
            var isHtml = b["contentType"]?.ToString() == "html";
            var note = $"Restored from backup. Original attendees: {names}";
            b["content"] = (b["content"]?.ToString() ?? "") + (isHtml ? $"<p><i>{WebUtility.HtmlEncode(note)}</i></p>" : $"\n\n{note}");
            body["body"] = b;
        }
        return body;
    }

    private static readonly HashSet<string> ContactDrop = ["id", "changeKey", "createdDateTime", "lastModifiedDateTime", "parentFolderId", "categories"];

    private static JsonObject? PrepareContact(JsonObject saved)
    {
        var body = (JsonObject)saved.DeepClone();
        foreach (var k in body.Select(kv => kv.Key).Where(k => k.StartsWith('@') || ContactDrop.Contains(k)).ToList()) body.Remove(k);
        return body;
    }

    private static async Task RestoreFlatAsync(DataRestoreContext ctx, string folderCollection, string itemCollection, string folderNameProperty,
        Func<JsonObject, JsonObject?> prepare, CancellationToken ct)
    {
        var user = User(ctx);
        var target = $"{user}/{itemCollection}";
        if (ctx.IntoFolder)
        {
            var folder = await ctx.Graph.SendAsync(HttpMethod.Post, $"{user}/{folderCollection}", new JsonObject { [folderNameProperty] = ctx.FolderName }, ct: ct);
            target = $"{user}/{folderCollection}/{Uri.EscapeDataString(folder!["id"]!.ToString())}/{itemCollection}";
        }
        foreach (var e in ctx.Entries.Where(e => !e.Version.IsFolder))
        {
            try
            {
                var saved = JsonNode.Parse(await ctx.Reader.ReadContentAsync(ctx.Tenant, e.Version, ct))!.AsObject();
                if (prepare(saved) is not { } body) { ctx.Result.Skipped++; continue; }
                await ctx.Graph.SendAsync(HttpMethod.Post, target, body, ct: ct);
                ctx.Result.Restored++;
            }
            catch (Exception ex) when (ex is ApiException or IOException or System.Text.Json.JsonException)
            {
                await ctx.FailAsync(e, ex.Message);
            }
        }
        await ctx.LogAsync($"{(itemCollection == "events" ? "Calendar events" : "Contacts")} restored to {ctx.Target.DisplayName}{(ctx.IntoFolder ? $" ('{ctx.FolderName}')" : "")}");
    }
}
