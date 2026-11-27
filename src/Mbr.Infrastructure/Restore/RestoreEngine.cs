using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Catalog.Restore;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Core.Restore;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Restore;

/// <summary>
/// Builds restore plans (what would change) and applies them. Planning only reads the target tenant;
/// applying writes with the restore app, in dependency order, remapping object IDs as it goes.
/// </summary>
public sealed partial class RestoreEngine(
    MbrDbContext db, ResourceRegistry registry, TenantSessionFactory sessions, SnapshotReader reader, IEnumerable<IRestoreCustomizer> customizers)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly string[] DirectoryObjectTypes = ["entra.user", "entra.group", "entra.servicePrincipal", "entra.administrativeUnit", "entra.application"];
    private static readonly HashSet<string> AssignmentLikeChildren = ["assignments", "members", "owners", "apps", "appRoleAssignedTo"];

    private IRestoreCustomizer? Customizer(string type) => customizers.FirstOrDefault(c => c.Type == type);

    // ------------------------------------------------------------------ shared state for one plan/apply run
    private sealed class RunState
    {
        public required Snapshot Snapshot { get; init; }
        public required Tenant SourceTenant { get; init; }
        public required Tenant TargetTenant { get; init; }
        public required TenantSession Session { get; init; }
        public required RestoreOptions Options { get; init; }
        public bool SameTenant => SourceTenant.Id == TargetTenant.Id;
        /// <summary>Applied to snapshot content as it's read, so matching, plans and writes all see the new domains.</summary>
        public DomainMap Domains { get; init; } = DomainMap.Empty;
        public Dictionary<(string Type, string Id), SnapshotItem> Rows { get; } = new();
        public Dictionary<long, JsonObject> Contents { get; } = new();
        public Dictionary<string, List<CapturedItem>?> Live { get; } = new();
        /// <summary>Known source → target IDs (mappings, matches, created objects).</summary>
        public Dictionary<(string Type, string Id), string> Map { get; } = new();
        public Dictionary<string, bool> DirectoryObjectExists { get; } = new();
        /// <summary>Objects the plan brings back (created or undeleted), by source type and ID.</summary>
        public HashSet<(string Type, string Id)> Returning { get; } = new();
        /// <summary>Content actually written for re-link actions (live + re-added references), by action index.</summary>
        public Dictionary<int, JsonObject> RelinkContent { get; } = new();
        public bool IsReturning(string refType, string id) => RefTypes(refType).Any(t => Returning.Contains((t, id)));
    }

    private async Task<RunState> CreateStateAsync(Guid snapshotId, Guid? targetTenantId, RestoreOptions options, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId, ct) ?? throw new KeyNotFoundException("Snapshot not found.");
        var source = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == snapshot.TenantId, ct);
        var target = targetTenantId is { } tid && tid != source.Id ? await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tid, ct) : source;
        var state = new RunState
        {
            Snapshot = snapshot, SourceTenant = source, TargetTenant = target, Options = options,
            Session = sessions.Create(target, AppRole.Restore),
            Domains = EffectiveDomainMap(options.DomainMap, source, target),
        };
        foreach (var row in await db.SnapshotItems.AsNoTracking().Where(i => i.SnapshotId == snapshotId).ToListAsync(ct))
            state.Rows[(row.ResourceType, row.ObjectId)] = row;
        foreach (var m in await db.IdMappings.AsNoTracking().Where(m => m.SourceTenantId == source.Id && m.TargetTenantId == target.Id).ToListAsync(ct))
            state.Map[(m.ResourceType, m.SourceId)] = m.TargetId;
        return state;
    }

    /// <summary>The requested map, plus source → target .onmicrosoft.com domain for cross-tenant restores.</summary>
    public static DomainMap EffectiveDomainMap(IEnumerable<KeyValuePair<string, string>>? requested, Tenant source, Tenant target)
    {
        var map = new DomainMap(requested);
        static bool Initial(string d) => d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase);
        return source.Id != target.Id && Initial(source.DefaultDomain) && Initial(target.DefaultDomain)
            ? map.With(source.DefaultDomain, target.DefaultDomain)
            : map;
    }

    private async Task<JsonObject> ContentAsync(RunState s, SnapshotItem row, CancellationToken ct)
    {
        if (!s.Contents.TryGetValue(row.Id, out var c))
        {
            c = await reader.GetContentAsync(s.SourceTenant, s.Snapshot, row, ct);
            if (!s.Domains.IsEmpty) c = (JsonObject)s.Domains.Apply(c)!;
            s.Contents[row.Id] = c;
        }
        return c;
    }

    private async Task<List<CapturedItem>?> LiveAsync(RunState s, string type, CancellationToken ct)
    {
        if (s.Live.TryGetValue(type, out var list)) return list;
        var handler = registry.Find(type);
        try { list = handler is null ? null : await handler.ListAsync(s.Session, ct); }
        catch (ApiException) { list = null; } // not readable (permissions/licensing): treated as unknown
        return s.Live[type] = list;
    }

    private static IEnumerable<string> RefTypes(string refType) => refType == "directoryObject" ? DirectoryObjectTypes : [refType];

    /// <summary>A value at a reference path is an object ID (not a keyword like "All" or a built-in scope tag "0").</summary>
    private static bool IsReference(RunState s, string refType, string value) =>
        Guid.TryParse(value, out _) || RefTypes(refType).Any(t => s.Rows.ContainsKey((t, value)));

    private static bool IsReference(RunState s, ReferenceDefinition r, string value) =>
        r.KeepUnknown ? RefTypes(r.Type).Any(t => s.Rows.ContainsKey((t, value))) : IsReference(s, r.Type, value);

    private async Task<bool> ExistsInTargetAsync(RunState s, string type, string id, CancellationToken ct)
    {
        if (DirectoryObjectTypes.Contains(type) || type == "directoryObject")
        {
            if (s.DirectoryObjectExists.TryGetValue(id, out var known)) return known;
            var obj = await s.Session.Graph.TryGetAsync($"/v1.0/directoryObjects/{Uri.EscapeDataString(id)}", ct);
            return s.DirectoryObjectExists[id] = obj != null;
        }
        var live = await LiveAsync(s, type, ct);
        return live?.Any(l => l.ObjectId == id) ?? false;
    }

    private static string MatchKey(ResourceDefinition def, JsonObject content, string displayName) =>
        string.Join("|", def.MatchKeys.Select(k => (JsonPath.GetStrings(content, k).FirstOrDefault() ?? (k == def.NameProperty ? displayName : "")).Trim().ToLowerInvariant()));

    /// <summary>Copies content with every reference rewritten through <paramref name="resolve"/> (null removes it).</summary>
    private static JsonObject Remap(ResourceDefinition def, JsonObject content, RunState s, Func<string, string, string?> resolve, List<string>? unresolved)
    {
        var copy = (JsonObject)content.DeepClone();
        foreach (var r in def.References)
        {
            JsonPath.Rewrite(copy, r.Path, value =>
            {
                if (!IsReference(s, r, value)) return value;
                var mapped = resolve(r.Type, value);
                if (mapped is null) unresolved?.Add($"{r.Type} {DescribeSource(s, r.Type, value)}");
                return mapped;
            });
        }
        return copy;
    }

    private static string DescribeSource(RunState s, string refType, string id)
    {
        foreach (var t in RefTypes(refType))
            if (s.Rows.TryGetValue((t, id), out var row)) return $"'{row.DisplayName}' ({id})";
        return id;
    }

    /// <summary>Properties that never take part in comparisons (secrets, read-only, create-only).</summary>
    private static HashSet<string> ExcludedFromCompare(ResourceDefinition def, RestoreOptions o)
    {
        var set = new HashSet<string>(def.SecretPaths.Concat(def.Restore.ReadOnly).Concat(def.Restore.CreateOnly).Select(p => p.Replace("[*]", "")));
        if (!o.IncludeAssignments) set.UnionWith(def.Children.Where(c => AssignmentLikeChildren.Contains(c.Name)).Select(c => c.Name));
        return set;
    }

    /// <summary>Differences between live and expected that matter: changed or missing values; extra array entries count, extra server-side properties don't.</summary>
    private static List<JsonChange> MeaningfulChanges(JsonNode? live, JsonNode? expected, HashSet<string> excluded) =>
        JsonDiff.Compare(live, expected)
            .Where(c => !(c.Kind == DiffKind.Removed && !c.Path.EndsWith(']')))
            .Where(c => !excluded.Any(e => c.Path == e || c.Path.StartsWith(e + ".") || c.Path.StartsWith(e + "[")))
            .ToList();
}
