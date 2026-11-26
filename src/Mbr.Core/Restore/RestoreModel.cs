using System.Text.Json.Serialization;

namespace Mbr.Core.Restore;

/// <summary>What to restore and how. Stored with the plan.</summary>
public class RestoreRequest
{
    public Guid SnapshotId { get; set; }
    /// <summary>Restore into this tenant (defaults to the snapshot's tenant). Another tenant = clone/migrate.</summary>
    public Guid? TargetTenantId { get; set; }
    /// <summary>Limit to these workloads (e.g. "intune").</summary>
    public List<string> Workloads { get; set; } = new();
    /// <summary>Limit to these resource types.</summary>
    public List<string> Types { get; set; } = new();
    /// <summary>Limit to these snapshot item IDs.</summary>
    public List<long> ItemIds { get; set; } = new();
    public RestoreOptions Options { get; set; } = new();
}

public class RestoreOptions
{
    /// <summary>Update objects that exist but differ from the snapshot (otherwise only missing objects are restored).</summary>
    public bool OverwriteChanged { get; set; } = true;
    /// <summary>Also restore missing objects the selection depends on (e.g. groups used in assignments).</summary>
    public bool IncludeDependencies { get; set; } = true;
    /// <summary>Restore assignments and memberships.</summary>
    public bool IncludeAssignments { get; set; } = true;
    /// <summary>Delete objects of the selected types that are not in the snapshot (mirror). Dangerous; off by default.</summary>
    public bool DeleteExtra { get; set; }
    /// <summary>Create/update Conditional Access policies in report-only mode regardless of their saved state.</summary>
    public bool ConditionalAccessReportOnly { get; set; }
    /// <summary>Restore deleted directory objects from the Entra recycle bin when possible (keeps their original IDs).</summary>
    public bool UseRecycleBin { get; set; } = true;
    /// <summary>
    /// When a deleted object comes back (recreated with a new ID, or undeleted), also update existing objects in the snapshot that
    /// reference it — policies, assignments, group memberships, role assignments — so the references point at it again.
    /// </summary>
    public bool RelinkReferences { get; set; } = true;
    /// <summary>Stop at the first failed action instead of continuing.</summary>
    public bool StopOnError { get; set; }
    /// <summary>Optional suffix for names of newly created objects (useful when cloning into another tenant for testing).</summary>
    public string? NameSuffix { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestoreActionKind { Create, Update, RecycleBin, Delete, Skip }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestoreActionStatus { Pending, Succeeded, Failed, Skipped, Excluded }

public class RestoreAction
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string Workload { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public RestoreActionKind Kind { get; set; }
    /// <summary>Why this action was chosen (shown in the plan).</summary>
    public string Reason { get; set; } = "";
    /// <summary>Snapshot item this action restores (null for deletes).</summary>
    public long? SnapshotItemId { get; set; }
    public string? SourceObjectId { get; set; }
    /// <summary>The object in the target tenant (existing, restored from the recycle bin, or created when applied).</summary>
    public string? TargetObjectId { get; set; }
    /// <summary>Property paths that differ (updates).</summary>
    public List<string> Changes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Added automatically because a selected object depends on it.</summary>
    public bool IsDependency { get; set; }
    /// <summary>Added automatically because it references an object this plan brings back.</summary>
    public bool IsRelink { get; set; }
    public bool Excluded { get; set; }
    public RestoreActionStatus Status { get; set; } = RestoreActionStatus.Pending;
    public string? Error { get; set; }
    public List<string> VerificationIssues { get; set; } = new();
}

public class RestorePlanDocument
{
    public List<RestoreAction> Actions { get; set; } = new();
    /// <summary>Plan-wide notes (e.g. unresolved references, secrets that need re-entry).</summary>
    public List<string> Notes { get; set; } = new();
}
