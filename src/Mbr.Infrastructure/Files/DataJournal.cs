using System.Text.Json;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Files;

/// <summary>
/// After each data backup of a source, an encrypted journal of what changed (new versions, superseded and deleted ones) is
/// written next to the content. Replaying the journals rebuilds the file and mail index if the database is lost
/// (<see cref="Backup.StorageImportService"/>).
/// </summary>
public static class DataJournal
{
    public const int SchemaVersion = 1;
    public const string Prefix = "data/";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Journal names sort in the order they were written.</summary>
    public static string Name(Guid sourceId, DateTimeOffset at) => $"{Prefix}{sourceId}/journal/{at.UtcTicks:D19}-{Guid.NewGuid():N}.json";

    public sealed record SourceEntry(Guid Id, DataSourceKind Kind, string ExternalId, string DisplayName, string? Address, bool Missing);

    public sealed record ContainerEntry(Guid Id, string ExternalId, string Kind, string Name, string? WebUrl, string? RootItemId, bool Missing);

    public sealed record VersionEntry(
        Guid ContainerId, string ItemId, string? ParentItemId, string Name, string Path, bool IsFolder, long Size, string Chunks,
        string? ETag, string? CTag, string? QuickXorHash, string? ContentType, DateTimeOffset? ModifiedAt, string? ModifiedBy,
        DateTimeOffset CapturedAt, DateTimeOffset? SupersededAt, DateTimeOffset? DeletedAt, string? MetadataJson);

    public sealed record Document(int SchemaVersion, Guid TenantId, DateTimeOffset WrittenAt, SourceEntry Source,
        List<ContainerEntry> Containers, List<VersionEntry> Versions);

    /// <summary>Writes the journal for everything in <paramref name="source"/> that changed since <paramref name="since"/>.</summary>
    public static async Task WriteAsync(MbrDbContext db, ITenantContentStore content, Tenant tenant, DataSource source, Guid storageTargetId,
        DateTimeOffset since, CancellationToken ct)
    {
        var versions = await db.DataItemVersions.AsNoTracking()
            .Where(v => v.SourceId == source.Id && (v.CapturedAt >= since || v.SupersededAt >= since || v.DeletedAt >= since))
            .ToListAsync(ct);
        if (versions.Count == 0) return;
        var containers = await db.DataContainers.AsNoTracking().Where(c => c.SourceId == source.Id).ToListAsync(ct);
        var doc = new Document(SchemaVersion, tenant.Id, DateTimeOffset.UtcNow,
            new SourceEntry(source.Id, source.Kind, source.ExternalId, source.DisplayName, source.Address, source.Missing),
            containers.Select(c => new ContainerEntry(c.Id, c.ExternalId, c.Kind, c.Name, c.WebUrl, c.RootItemId, c.Missing)).ToList(),
            versions.Select(v => new VersionEntry(v.ContainerId, v.ItemId, v.ParentItemId, v.Name, v.Path, v.IsFolder, v.Size, v.Chunks,
                v.ETag, v.CTag, v.QuickXorHash, v.ContentType, v.ModifiedAt, v.ModifiedBy, v.CapturedAt, v.SupersededAt, v.DeletedAt,
                v.MetadataJson)).ToList());
        await content.PutDocumentAsync(tenant, storageTargetId, Name(source.Id, DateTimeOffset.UtcNow), JsonSerializer.SerializeToUtf8Bytes(doc, Json), ct);
    }
}
