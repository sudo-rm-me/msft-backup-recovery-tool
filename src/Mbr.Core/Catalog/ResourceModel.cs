using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;

namespace Mbr.Core.Catalog;

/// <summary>
/// Declarative description of a backup-able resource type. Most types are fully described by this
/// (see Mbr.Catalog/catalog/*.yaml) and handled by the generic handler; unusual ones get custom handlers.
/// </summary>
public class ResourceDefinition
{
    /// <summary>Unique type key, e.g. "intune.deviceConfiguration".</summary>
    public string Type { get; set; } = "";
    /// <summary>entra, intune, azure, exchange, teams, sharepoint, defender, purview.</summary>
    public string Workload { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Api { get; set; } = "graph";
    /// <summary>Graph version: v1.0 or beta.</summary>
    public string Version { get; set; } = "v1.0";
    /// <summary>Collection path (or object path for singletons), without the version prefix.</summary>
    public string ListPath { get; set; } = "";
    /// <summary>Item path; "{id}" is replaced. Defaults to ListPath/{id}.</summary>
    public string? ItemPath { get; set; }
    /// <summary>The object is a singleton (one per tenant) rather than a collection.</summary>
    public bool Singleton { get; set; }
    /// <summary>Re-read each item individually (list responses omit properties for some types).</summary>
    public bool GetItem { get; set; }
    /// <summary>Query string appended to item GETs (e.g. "$expand=settings").</summary>
    public string? ItemQuery { get; set; }
    public string NameProperty { get; set; } = "displayName";
    /// <summary>Property holding the object's key (almost always "id").</summary>
    public string IdProperty { get; set; } = "id";
    public List<ChildDefinition> Children { get; set; } = new();
    /// <summary>Skip items where property equals value (e.g. built-in objects).</summary>
    public List<SkipRule> Skip { get; set; } = new();
    /// <summary>Extra properties to strip (read-only/volatile) on top of the defaults.</summary>
    public List<string> Strip { get; set; } = new();
    /// <summary>Paths holding secrets that Graph does not return; flagged for manual re-entry on restore.</summary>
    public List<string> SecretPaths { get; set; } = new();
    /// <summary>Paths holding IDs of other objects, used for ID remapping on restore.</summary>
    public List<ReferenceDefinition> References { get; set; } = new();
    /// <summary>Types that must be restored before this one.</summary>
    public List<string> DependsOn { get; set; } = new();
    /// <summary>Properties that identify an object when IDs differ (cross-tenant or recreated objects).</summary>
    public List<string> MatchKeys { get; set; } = new() { "displayName" };
    public RestoreDefinition Restore { get; set; } = new();
    /// <summary>Optional free-text caveats shown in the UI.</summary>
    public string? Notes { get; set; }
    /// <summary>For PowerShell-backed types (Api "exo", "ipps" or "teams"): how the cmdlets are called.</summary>
    public PowerShellDefinition? PowerShell { get; set; }

    public string ItemPathFor(string id) => (ItemPath ?? ListPath.Split('?')[0] + "/{id}").Replace("{id}", Uri.EscapeDataString(id));
    public string Versioned(string path) => Api == "graph" ? $"/{Version}/{path.TrimStart('/')}" : "/" + path.TrimStart('/');
}

/// <summary>
/// A type read and written with Get-/New-/Set-/Remove-{Noun} cmdlets (Exchange Online, Security &amp; Compliance, Teams).
/// Only properties the New/Set cmdlet accepts as parameters are sent back.
/// </summary>
public class PowerShellDefinition
{
    /// <summary>Cmdlet noun, e.g. "TransportRule". Defaults to <see cref="ResourceDefinition.ListPath"/>.</summary>
    public string? Noun { get; set; }
    /// <summary>Extra switches/parameters for Get-{Noun} (e.g. IncludePreviewFeatures: true).</summary>
    public Dictionary<string, object> GetArgs { get; set; } = new();
    /// <summary>Snapshot property → cmdlet parameter when they're named differently (e.g. ParentPolicyName → Policy).</summary>
    public Dictionary<string, string> Renames { get; set; } = new();
    /// <summary>Properties never sent back even if a cmdlet has a parameter with that name.</summary>
    public List<string> Skip { get; set; } = new();
    /// <summary>The property identifying the object for Set/Remove (defaults to the ID property).</summary>
    public string? IdentityProperty { get; set; }
    /// <summary>Strip this prefix from the identity when creating (Teams: "Tag:").</summary>
    public string? CreateIdentityPrefix { get; set; }
    /// <summary>Objects with this property true can't be created or removed (built-in/default policies), only updated.</summary>
    public string? BuiltInProperty { get; set; }
}

public class ChildDefinition
{
    /// <summary>Property the child collection is embedded under, e.g. "assignments".</summary>
    public string Name { get; set; } = "";
    /// <summary>Path with "{id}" for the parent ID, without version prefix.</summary>
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public List<string> Strip { get; set; } = new();
    /// <summary>The parent GET already includes this child (via $expand); don't fetch separately.</summary>
    public bool Expanded { get; set; }
    /// <summary>How the child is written back on restore.</summary>
    public ChildRestore Restore { get; set; } = new();
}

public class ChildRestore
{
    /// <summary>
    /// "inline" (sent in the create body), "assign" (POST {parent}/assign with {name: [...]}),
    /// "each" (replace: delete existing, POST each item), "add" (POST items that are missing, matched on <see cref="Keys"/>),
    /// "ref" (POST {"@odata.id"} to path/$ref), "none".
    /// </summary>
    public string Mode { get; set; } = "none";
    /// <summary>Override path for assign/each/ref modes ("{id}" = new parent ID).</summary>
    public string? Path { get; set; }
    /// <summary>Wrapper property for assign mode (defaults to the child name).</summary>
    public string? BodyProperty { get; set; }
    /// <summary>"add" mode: properties that identify an entry.</summary>
    public List<string> Keys { get; set; } = new();
    /// <summary>"add" mode: property set to the parent's ID in the target tenant (e.g. resourceId).</summary>
    public string? ParentIdProperty { get; set; }
}

public class SkipRule
{
    public string Property { get; set; } = "";
    public string Value { get; set; } = "";
}

public class ReferenceDefinition
{
    public string Path { get; set; } = "";
    /// <summary>Referenced resource type (e.g. entra.group). "directoryObject" resolves against users, groups and service principals.</summary>
    public string Type { get; set; } = "";
    /// <summary>
    /// Values that aren't in the snapshot are kept unchanged instead of being treated as unresolved — for built-in objects
    /// (e.g. built-in directory roles) that aren't backed up and have the same ID in every tenant.
    /// </summary>
    public bool KeepUnknown { get; set; }
}

public class RestoreDefinition
{
    public bool Supported { get; set; } = true;
    /// <summary>POST path for create (defaults to ListPath).</summary>
    public string? CreatePath { get; set; }
    /// <summary>PATCH (default) or PUT for updates.</summary>
    public string UpdateMethod { get; set; } = "PATCH";
    /// <summary>Properties that can be set on create but not on update.</summary>
    public List<string> CreateOnly { get; set; } = new();
    /// <summary>Properties never sent to the API (read-only even on create).</summary>
    public List<string> ReadOnly { get; set; } = new();
    /// <summary>For singletons: updated in place, never created or deleted.</summary>
    public bool UpdateOnly { get; set; }
    /// <summary>Directory objects that can be restored from the recycle bin (directory/deletedItems).</summary>
    public bool RecycleBin { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One captured object, normalized and ready to store.</summary>
public record CapturedItem(string Type, string ObjectId, string DisplayName, JsonObject Content);

public enum IssueSeverity { Info, Warning, Error }

public record CaptureIssue(string Type, string? ObjectId, IssueSeverity Severity, string Message);

/// <summary>Everything a handler needs to talk to a tenant.</summary>
public interface ITenantSession
{
    string DirectoryId { get; }
    IApiClient Graph { get; }
    IApiClient Arm { get; }
    /// <summary>Runs a PowerShell script for workloads only reachable through PowerShell modules.</summary>
    IPowerShellRunner PowerShell { get; }
    Task ReportIssueAsync(CaptureIssue issue);
}

public interface IPowerShellRunner
{
    /// <summary>Runs a script with parameters and returns its JSON output.</summary>
    Task<JsonNode?> RunAsync(string script, IDictionary<string, object?> parameters, CancellationToken ct);
}

/// <summary>Captures (and, from phase 3, restores) one resource type.</summary>
public interface IResourceHandler
{
    ResourceDefinition Definition { get; }
    IAsyncEnumerable<CapturedItem> CaptureAsync(ITenantSession session, CancellationToken ct);

    /// <summary>Captures one object by ID (null if it doesn't exist). Used for restore planning and verification.</summary>
    async Task<CapturedItem?> CaptureByIdAsync(ITenantSession session, string id, CancellationToken ct)
    {
        await foreach (var item in CaptureAsync(session, ct))
            if (item.ObjectId == id) return item;
        return null;
    }

    /// <summary>Lists objects (ID, name, top-level properties without children) cheaply, for matching.</summary>
    async Task<List<CapturedItem>> ListAsync(ITenantSession session, CancellationToken ct)
    {
        var list = new List<CapturedItem>();
        await foreach (var item in CaptureAsync(session, ct)) list.Add(item);
        return list;
    }
}
