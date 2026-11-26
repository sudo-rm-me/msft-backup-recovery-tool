using System.Text.Json.Nodes;
using Mbr.Core.Catalog;

namespace Mbr.Core.Restore;

/// <summary>Context passed to restore customizers.</summary>
public interface IRestoreContext
{
    ITenantSession Session { get; }
    RestoreOptions Options { get; }
    ResourceDefinition Definition { get; }
    /// <summary>Maps a source ID of the given type to the target ID (null if unknown).</summary>
    string? MapId(string type, string sourceId);
    Task WarnAsync(string message);
}

/// <summary>
/// Per-type adjustments for resources whose Graph write semantics differ from a plain POST/PATCH.
/// All members have sensible defaults; implement only what a type needs.
/// </summary>
public interface IRestoreCustomizer
{
    string Type { get; }

    /// <summary>Whether the object can be recreated at all (e.g. Intune apps with binaries can't).</summary>
    bool CanCreate(JsonObject content, out string? reason)
    {
        reason = null;
        return true;
    }

    /// <summary>Adjusts the request body (already remapped and stripped of read-only properties).</summary>
    void PrepareBody(IRestoreContext ctx, JsonObject body, bool isCreate) { }

    /// <summary>Creates the object and returns its new ID. Return null to use the default POST.</summary>
    Task<string?> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Updates the object. Return false to use the default PATCH/PUT.</summary>
    Task<bool> UpdateAsync(IRestoreContext ctx, string targetId, JsonObject body, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Deletes the object (mirror mode). Return false to use the default DELETE.</summary>
    Task<bool> DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Extra steps after create/update (e.g. writing definition values). <paramref name="content"/> is the remapped snapshot content.</summary>
    Task AfterWriteAsync(IRestoreContext ctx, string targetId, JsonObject content, bool created, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Implemented by handlers whose objects aren't written with Graph POST/PATCH (e.g. PowerShell-backed types).
/// Customizers still run first; the writer replaces the default Graph calls.
/// </summary>
public interface IResourceWriter
{
    Task<string> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct);
    Task UpdateAsync(IRestoreContext ctx, string targetId, JsonObject body, CancellationToken ct);
    Task DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct);
    /// <summary>Why an object can't be recreated (e.g. a built-in policy), or null.</summary>
    string? CannotCreate(JsonObject content) => null;
}
