namespace Mbr.Core.Model;

public enum TenantStatus { PendingConsent, Active, Disabled }

/// <summary>A customer tenant managed by this deployment.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Entra directory (tenant) ID, a GUID string.</summary>
    public string DirectoryId { get; set; } = "";
    public string DefaultDomain { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public TenantStatus Status { get; set; } = TenantStatus.PendingConsent;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BackupConsentedAt { get; set; }
    public DateTimeOffset? RestoreConsentedAt { get; set; }
    public DateTimeOffset? LastPermissionCheckAt { get; set; }
    public string? LastPermissionCheckJson { get; set; }
    public Guid? DefaultStorageTargetId { get; set; }
    /// <summary>Per-tenant data encryption key, wrapped by the configured key wrapper.</summary>
    public string? WrappedDataKey { get; set; }
    public string? DataKeyWrapperId { get; set; }
    public string? Notes { get; set; }
}

public enum StorageKind { Local, S3 }

/// <summary>Where backup content is written. Secrets are never stored here in plaintext.</summary>
public class StorageTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public StorageKind Kind { get; set; }
    /// <summary>Kind-specific, non-secret settings as JSON (see StorageTargetSettings).</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>Optional secret (e.g. S3 secret key) encrypted with the master key wrapper.</summary>
    public string? EncryptedSecret { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Cancelled }

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string Type { get; set; } = "";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string ParametersJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    /// <summary>Opaque state a handler saves so a restarted job can resume.</summary>
    public string? CheckpointJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NotBefore { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CreatedBy { get; set; }
    public Guid? ScheduleId { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public double Progress { get; set; }
    public string? ProgressMessage { get; set; }
    public bool CancelRequested { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

public enum JobLogLevel { Debug, Info, Warning, Error }

public class JobLog
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public JobLogLevel Level { get; set; }
    public string Message { get; set; } = "";
}

public class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string JobType { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    /// <summary>Five-field cron expression, e.g. "0 2 * * *".</summary>
    public string Cron { get; set; } = "0 2 * * *";
    public string TimeZone { get; set; } = "UTC";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "";
    public Guid? TenantId { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? DetailsJson { get; set; }
    public string? IpAddress { get; set; }
}

public enum TenantRole { Viewer = 0, BackupOperator = 1, RestoreOperator = 2, Admin = 3 }

public class Operator
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Entra object ID (oid claim) in the home tenant.</summary>
    public string ObjectId { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsGlobalAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public List<OperatorTenantRole> Roles { get; set; } = new();
}

public class OperatorTenantRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperatorId { get; set; }
    /// <summary>Null means the role applies to every tenant.</summary>
    public Guid? TenantId { get; set; }
    public TenantRole Role { get; set; }
}

/// <summary>Distributed lease used to make singleton services (e.g. the scheduler) run on one host.</summary>
public class Lease
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

/// <summary>Content-addressed object written to a storage target (for dedup, GC and usage stats).</summary>
public class StoredObject
{
    public Guid TenantId { get; set; }
    public Guid StorageTargetId { get; set; }
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public long StoredSize { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SnapshotStatus { Running, Completed, Partial, Failed }

/// <summary>A point-in-time configuration backup of one tenant.</summary>
public class Snapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StorageTargetId { get; set; }
    public Guid? JobId { get; set; }
    public SnapshotStatus Status { get; set; } = SnapshotStatus.Running;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Comma-separated workloads included, e.g. "entra,intune".</summary>
    public string Workloads { get; set; } = "";
    public int ItemCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public long TotalBytes { get; set; }
    /// <summary>Changes compared with the previous completed snapshot.</summary>
    public int AddedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int RemovedCount { get; set; }
    public string? Label { get; set; }
    /// <summary>Baselines are never pruned and are what drift is measured against.</summary>
    public bool IsBaseline { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class SnapshotItem
{
    public long Id { get; set; }
    public Guid SnapshotId { get; set; }
    public string ResourceType { get; set; } = "";
    public string Workload { get; set; } = "";
    /// <summary>The object's ID in the tenant when it was captured.</summary>
    public string ObjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>SHA-256 of the normalized content (content-addressed in storage).</summary>
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}

public class SnapshotIssue
{
    public long Id { get; set; }
    public Guid SnapshotId { get; set; }
    public string ResourceType { get; set; } = "";
    public string? ObjectId { get; set; }
    public string Severity { get; set; } = "Warning";
    public string Message { get; set; } = "";
}

/// <summary>Per-tenant retention and backup settings.</summary>
public class TenantSettings
{
    public Guid TenantId { get; set; }
    /// <summary>Configuration snapshots older than this are pruned (baselines excepted).</summary>
    public int ConfigRetentionDays { get; set; } = 90;
    /// <summary>Always keep at least this many recent snapshots, whatever their age.</summary>
    public int ConfigKeepMinimum { get; set; } = 7;
    /// <summary>File/mail versions that changed or were deleted are kept this long.</summary>
    public int DataRetentionDays { get; set; } = 30;
    /// <summary>Alert when a backup detects configuration changes compared with the baseline.</summary>
    public bool DriftAlerts { get; set; } = true;
    /// <summary>Restores must be approved by a second operator before they are applied.</summary>
    public bool RequireRestoreApproval { get; set; }
}

/// <summary>Maps an object in a backed-up tenant to the corresponding object in a restore target (recreated or cloned objects get new IDs).</summary>
public class IdMapping
{
    public long Id { get; set; }
    public Guid SourceTenantId { get; set; }
    public Guid TargetTenantId { get; set; }
    public string ResourceType { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum RestorePlanStatus { Planning, Draft, PendingApproval, Approved, Applying, Applied, PartiallyApplied, Failed, Rejected }

public class RestorePlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Target tenant (where changes are written).</summary>
    public Guid TenantId { get; set; }
    public Guid SourceTenantId { get; set; }
    public Guid SourceSnapshotId { get; set; }
    public RestorePlanStatus Status { get; set; } = RestorePlanStatus.Planning;
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? PlanJobId { get; set; }
    public Guid? ApplyJobId { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    /// <summary>Selection and options the plan was built with (RestoreRequest JSON).</summary>
    public string RequestJson { get; set; } = "{}";
    /// <summary>The ordered actions (RestorePlanDocument JSON), updated with results when applied.</summary>
    public string PlanJson { get; set; } = "{}";
    public int CreateCount { get; set; }
    public int UpdateCount { get; set; }
    public int RecycleBinCount { get; set; }
    public int DeleteCount { get; set; }
    public int SkipCount { get; set; }
    public int WarningCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>A job worker process; it reports in while running so operators can see the fleet.</summary>
public class WorkerInstance
{
    /// <summary>Lease owner name (machine:process:random).</summary>
    public string Id { get; set; } = "";
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public int MaxConcurrency { get; set; }
    public int RunningJobs { get; set; }
    public string? Version { get; set; }
}

/// <summary>
/// A deployment-wide setting changed in the app (Settings → General). It overrides the value from configuration
/// (environment variables set by Terraform, appsettings.json). Keys are configuration paths, e.g. Mbr:Partner:TenantId.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    /// <summary>The value as JSON; for secrets, the JSON encrypted with the master key wrapper.</summary>
    public string Value { get; set; } = "";
    public bool Encrypted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}
