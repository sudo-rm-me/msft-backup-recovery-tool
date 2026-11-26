namespace Mbr.Core.Model;

/// <summary>What a protected data source is: a user's OneDrive, a SharePoint site, a mailbox.</summary>
public enum DataSourceKind { OneDrive, SharePointSite, Mailbox }

/// <summary>
/// A protected unit of data in a tenant (a OneDrive, a SharePoint site, a mailbox). Found by discovery; backups run per source.
/// </summary>
public class DataSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DataSourceKind Kind { get; set; }
    /// <summary>Graph ID of the owner: user ID (OneDrive, mailbox) or site ID.</summary>
    public string ExternalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>UPN or site URL.</summary>
    public string? Address { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>No longer found by the latest discovery (deleted user or site). Its backups are kept.</summary>
    public bool Missing { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastBackupAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    /// <summary>Current (not deleted) items and their size, as of the last backup.</summary>
    public long ItemCount { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>A drive (document library / OneDrive) or mail folder tree inside a source, with its delta sync state.</summary>
public class DataContainer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SourceId { get; set; }
    /// <summary>Graph ID (drive ID), or "mail" / "calendar" / "contacts" for mailbox containers.</summary>
    public string ExternalId { get; set; } = "";
    /// <summary>"drive", "mail", "calendar" or "contacts".</summary>
    public string Kind { get; set; } = "drive";
    /// <summary>Extra sync state (e.g. per-folder delta links for mail), as JSON.</summary>
    public string? StateJson { get; set; }
    public string Name { get; set; } = "";
    public string? WebUrl { get; set; }
    /// <summary>The root item's ID (root of the browsable tree).</summary>
    public string? RootItemId { get; set; }
    /// <summary>Delta link from the last completed sync.</summary>
    public string? DeltaLink { get; set; }
    /// <summary>Next page of an interrupted sync: the next run resumes here.</summary>
    public string? ResumeLink { get; set; }
    /// <summary>The interrupted sync is a full enumeration (items not seen by the end are treated as deleted).</summary>
    public bool ResumeIsFull { get; set; }
    /// <summary>Items whose content couldn't be read last time (comma-separated IDs); retried on the next run.</summary>
    public string RetryItemIds { get; set; } = "";
    public DateTimeOffset? LastSyncAt { get; set; }
    public bool Missing { get; set; }
}

/// <summary>
/// One version of a file or folder (or message). A version is valid from <see cref="CapturedAt"/> until it is superseded by
/// a newer version or the item is deleted, so "the tree as of T" is every version valid at T.
/// </summary>
public class DataItemVersion
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SourceId { get; set; }
    public Guid ContainerId { get; set; }
    public string ItemId { get; set; } = "";
    public string? ParentItemId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Folder path at capture time, for search and display ("/Reports/2026"). Browsing uses ParentItemId.</summary>
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    /// <summary>Comma-separated content-store hashes of the content chunks (empty for folders).</summary>
    public string Chunks { get; set; } = "";
    public Guid StorageTargetId { get; set; }
    public string? ETag { get; set; }
    /// <summary>Content tag: changes only when the content changes (renames/moves keep it).</summary>
    public string? CTag { get; set; }
    public string? QuickXorHash { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SupersededAt { get; set; }
    /// <summary>When the item was seen deleted in the source.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? JobId { get; set; }
    /// <summary>Extra metadata JSON (e.g. message headers).</summary>
    public string? MetadataJson { get; set; }
}
