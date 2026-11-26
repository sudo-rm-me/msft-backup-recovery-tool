using System.Text.Json;
using Mbr.Core.Catalog;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Core.Restore;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Restore;

public sealed partial class RestoreEngine
{
    public async Task<RestorePlanDocument> BuildPlanAsync(RestorePlan plan, RestoreRequest req, IJobContext? job, CancellationToken ct)
    {
        var s = await CreateStateAsync(req.SnapshotId, req.TargetTenantId, req.Options, ct);
        var doc = new RestorePlanDocument();
        var o = req.Options;
        if (!s.SameTenant)
            doc.Notes.Add($"Cross-tenant restore from {s.SourceTenant.DisplayName} into {s.TargetTenant.DisplayName}: objects are matched by name and references are remapped.");

        // 1. Selection
        var selected = s.Rows.Values.Where(r =>
            (req.Workloads.Count == 0 || req.Workloads.Contains(r.Workload)) &&
            (req.Types.Count == 0 || req.Types.Contains(r.ResourceType)) &&
            (req.ItemIds.Count == 0 || req.ItemIds.Contains(r.Id))).ToList();
        var chosen = selected.ToDictionary(r => (r.ResourceType, r.ObjectId));
        var dependencies = new HashSet<(string, string)>();

        // 2. Dependencies: referenced objects that are in the snapshot are pulled in (they'll be skipped if they exist).
        if (o.IncludeDependencies)
        {
            var queue = new Queue<SnapshotItem>(selected);
            while (queue.Count > 0)
            {
                var row = queue.Dequeue();
                var def = registry.Find(row.ResourceType)?.Definition;
                if (def is null) continue;
                var content = await ContentAsync(s, row, ct);
                foreach (var r in def.References)
                {
                    if (!o.IncludeAssignments && def.Children.Any(c => AssignmentLikeChildren.Contains(c.Name) && r.Path.StartsWith(c.Name + "[")))
                        continue;
                    foreach (var id in JsonPath.GetStrings(content, r.Path))
                    foreach (var t in RefTypes(r.Type))
                    {
                        if (!s.Rows.TryGetValue((t, id), out var dep) || chosen.ContainsKey((t, id))) continue;
                        chosen[(t, id)] = dep;
                        dependencies.Add((t, id));
                        queue.Enqueue(dep);
                    }
                }
            }
        }
        if (job != null) await job.InfoAsync($"Planning {chosen.Count} object(s) ({dependencies.Count} added as dependencies)");

        // 3. Match each object to the target tenant and decide the action kind.
        var order = registry.Ordered().Select((h, i) => (h.Definition.Type, i)).ToDictionary(x => x.Type, x => x.i);
        var rows = chosen.Values.OrderBy(r => order.GetValueOrDefault(r.ResourceType, int.MaxValue)).ThenBy(r => r.DisplayName).ToList();
        var actions = new List<RestoreAction>();
        var matched = new HashSet<RestoreAction>();
        var matchedLive = new Dictionary<string, HashSet<string>>();
        var n = 0;
        foreach (var row in rows)
        {
            n++;
            if (job != null && n % 10 == 0) await job.ReportProgressAsync(0.7 * n / rows.Count, $"Matching {row.DisplayName}");
            var action = await MatchAsync(s, row, dependencies.Contains((row.ResourceType, row.ObjectId)), matched, matchedLive, ct);
            if (action != null) actions.Add(action);
        }

        // 3b. Re-link: objects that reference something this plan brings back get those references added back.
        var returning = actions.Where(a => !matched.Contains(a) && a.Kind is RestoreActionKind.Create or RestoreActionKind.RecycleBin)
            .Select(a => (a.Type, a.SourceObjectId!)).ToHashSet();
        bool IsReturning(string refType, string id) => RefTypes(refType).Any(t => returning.Contains((t, id)));
        if (o.RelinkReferences && o.IncludeAssignments && returning.Count > 0 && !s.SameTenant)
            doc.Notes.Add("References from other existing objects aren't re-linked in cross-tenant restores; select those objects explicitly.");
        else if (o.RelinkReferences && o.IncludeAssignments && returning.Count > 0)
        {
            var added = 0;
            foreach (var row in s.Rows.Values.Where(r => !chosen.ContainsKey((r.ResourceType, r.ObjectId))).ToList())
            {
                var def = registry.Find(row.ResourceType)?.Definition;
                if (def is null || def.References.Count == 0 || !def.Restore.Supported) continue;
                var content = await ContentAsync(s, row, ct);
                if (!ReferencesReturning(def, content, IsReturning)) continue;
                // Existing objects get the references added back. Missing ones are only brought back when they exist *for*
                // the returning object (e.g. its role assignments, which Entra removes with it).
                if (await FindTargetAsync(s, def, row, content, ct) is null && !IsAboutReturning(def, content, IsReturning)) continue;
                chosen[(row.ResourceType, row.ObjectId)] = row;
                var action = await MatchAsync(s, row, false, matched, matchedLive, ct);
                if (action is null) continue;
                action.IsRelink = true;
                actions.Add(action);
                added++;
            }
            if (added > 0)
            {
                doc.Notes.Add($"{added} other object(s) reference restored objects and were added so those references can be re-linked.");
                actions = actions.OrderBy(a => order.GetValueOrDefault(a.Type, int.MaxValue)).ThenBy(a => a.DisplayName).ToList();
            }
        }

        // 4. Compare matched objects and check references now that every target ID (or pending creation) is known.
        var pendingCreates = actions.Where(a => a.Kind is RestoreActionKind.Create && !matched.Contains(a)).Select(a => (a.Type, a.SourceObjectId!)).ToHashSet();
        n = 0;
        foreach (var action in actions)
        {
            n++;
            if (job != null && n % 10 == 0) await job.ReportProgressAsync(0.8 + 0.2 * n / actions.Count, $"Comparing {action.DisplayName}");
            var def = registry.Get(action.Type).Definition;
            var row = s.Rows[(action.Type, action.SourceObjectId!)];
            var content = await ContentAsync(s, row, ct);
            var unresolved = new List<string>();
            CapturedItem? live = null;
            if (action.IsRelink && matched.Contains(action))
            {
                // Only the references to returning objects are added; everything else stays as it is now.
                live = await registry.Get(def.Type).CaptureByIdAsync(s.Session, action.TargetObjectId!, ct);
                if (live != null) content = MergeRelink(def, live.Content, content, IsReturning);
            }
            var expected = await RemapForPlanAsync(s, def, content, pendingCreates, unresolved, ct);
            var willWrite = action.Kind != RestoreActionKind.Skip || matched.Contains(action);
            if (unresolved.Count > 0 && willWrite)
                action.Warnings.Add($"References that can't be resolved in the target tenant will be removed: {string.Join(", ", unresolved.Distinct().Take(10))}{(unresolved.Distinct().Count() > 10 ? "…" : "")}");
            var secrets = def.SecretPaths.Where(p => JsonPath.Select(content, p).Any()).ToList();
            if (secrets.Count > 0 && willWrite)
                action.Warnings.Add($"Secret values ({string.Join(", ", secrets)}) are never returned by Microsoft Graph and must be re-entered after the restore.");
            if (!matched.Contains(action)) continue;

            live ??= await registry.Get(def.Type).CaptureByIdAsync(s.Session, action.TargetObjectId!, ct);
            var changes = MeaningfulChanges(live?.Content, expected, ExcludedFromCompare(def, o));
            action.Changes = changes.Select(c => c.Path.Length == 0 ? "(object)" : c.Path).Distinct().ToList();
            // A reference to an object that's being recreated looks unchanged here (its new ID isn't known yet) but will change.
            var refsRecreated = action.IsRelink && ReferencesReturning(def, content, (t, id) => RefTypes(t).Any(x => pendingCreates.Contains((x, id))));
            if (refsRecreated && changes.Count == 0) action.Changes = def.References.Select(r => r.Path).Where(p => JsonPath.GetStrings(content, p).Any()).Distinct().ToList();
            if (changes.Count == 0 && !refsRecreated)
            {
                action.Kind = RestoreActionKind.Skip;
                action.Reason = "Already matches the snapshot.";
            }
            else if (IsSynced(content) || IsSynced(live?.Content))
            {
                action.Kind = RestoreActionKind.Skip;
                action.Reason = SyncedReason;
            }
            else if (action.IsRelink && !def.Restore.UpdateMethod.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                action.Kind = RestoreActionKind.Update;
                action.Reason = "References restored objects; those references will be added back (nothing else is changed).";
            }
            else if (!o.OverwriteChanged)
            {
                action.Kind = RestoreActionKind.Skip;
                action.Reason = $"Exists but differs in {action.Changes.Count} place(s); overwriting changed objects is turned off.";
            }
            else if (def.Restore.UpdateMethod.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                action.Kind = RestoreActionKind.Skip;
                action.Reason = "Exists but differs, and this type can't be updated in place. Delete it in the tenant and restore again to recreate it.";
            }
            else
            {
                action.Kind = RestoreActionKind.Update;
                action.Reason = $"Differs from the snapshot in {action.Changes.Count} place(s); it will be updated.";
            }
        }

        // 5. Mirror mode: delete objects of the selected types that aren't in the snapshot.
        if (o.DeleteExtra)
        {
            var types = (req.Types.Count > 0 ? req.Types : registry.ForWorkloads(req.Workloads.Count > 0 ? req.Workloads : null).Select(h => h.Definition.Type))
                .Where(t => !registry.Get(t).Definition.Singleton && !registry.Get(t).Definition.Restore.UpdateOnly).ToList();
            foreach (var type in types.OrderByDescending(t => order.GetValueOrDefault(t)))
            {
                var live = await LiveAsync(s, type, ct);
                if (live is null) continue;
                var matchedIds = matchedLive.GetValueOrDefault(type) ?? [];
                foreach (var extra in live.Where(l => !matchedIds.Contains(l.ObjectId)))
                    actions.Add(new RestoreAction
                    {
                        Type = type, Workload = registry.Get(type).Definition.Workload, DisplayName = extra.DisplayName, TargetObjectId = extra.ObjectId,
                        Kind = RestoreActionKind.Delete, Reason = "Not in the snapshot; it will be deleted (mirror mode).",
                    });
            }
        }

        for (var i = 0; i < actions.Count; i++) actions[i].Index = i;
        doc.Actions = actions;
        plan.CreateCount = actions.Count(a => a.Kind == RestoreActionKind.Create);
        plan.UpdateCount = actions.Count(a => a.Kind == RestoreActionKind.Update);
        plan.RecycleBinCount = actions.Count(a => a.Kind == RestoreActionKind.RecycleBin);
        plan.DeleteCount = actions.Count(a => a.Kind == RestoreActionKind.Delete);
        plan.SkipCount = actions.Count(a => a.Kind == RestoreActionKind.Skip);
        plan.WarningCount = actions.Where(a => a.Kind != RestoreActionKind.Skip).Sum(a => a.Warnings.Count);
        plan.PlanJson = JsonSerializer.Serialize(doc, Json);
        return doc;
    }

    private const string SyncedReason = "Synced from on-premises Active Directory: make this change in AD and Entra Connect will sync it.";

    private static bool IsSynced(System.Text.Json.Nodes.JsonObject? content) =>
        content?["onPremisesSyncEnabled"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Creates the action for one snapshot item: matched (compared later), undelete, create, or skip.</summary>
    private async Task<RestoreAction?> MatchAsync(RunState s, SnapshotItem row, bool isDependency, HashSet<RestoreAction> matched,
        Dictionary<string, HashSet<string>> matchedLive, CancellationToken ct)
    {
        var handler = registry.Find(row.ResourceType);
        if (handler is null) return null;
        var def = handler.Definition;
        var o = s.Options;
        var content = await ContentAsync(s, row, ct);
        var action = new RestoreAction
        {
            Type = def.Type, Workload = def.Workload, DisplayName = row.DisplayName, SnapshotItemId = row.Id, SourceObjectId = row.ObjectId,
            IsDependency = isDependency,
        };
        // Objects named after a reference (role assignments) read better with the referenced object's name.
        if (def.References.FirstOrDefault(r => r.Path == def.NameProperty) is { } nameRef)
            foreach (var t in RefTypes(nameRef.Type))
                if (s.Rows.TryGetValue((t, row.DisplayName), out var named)) { action.DisplayName = named.DisplayName; break; }
        if (!def.Restore.Supported)
        {
            action.Kind = RestoreActionKind.Skip;
            action.Reason = "Restore isn't supported for this type.";
            return action;
        }

        var targetId = await FindTargetAsync(s, def, row, content, ct);
        if (targetId != null)
        {
            action.TargetObjectId = targetId;
            matched.Add(action);
            s.Map[(def.Type, row.ObjectId)] = targetId;
            (matchedLive.TryGetValue(def.Type, out var set) ? set : matchedLive[def.Type] = new()).Add(targetId);
            return action; // compared once all targets are known
        }
        if (def.Restore.UpdateOnly || def.Singleton)
        {
            action.Kind = RestoreActionKind.Skip;
            action.Reason = "Not found in the target tenant (it can only be updated, not created).";
            return action;
        }
        if (s.SameTenant && def.Restore.RecycleBin && o.UseRecycleBin &&
            await s.Session.Graph.TryGetAsync($"/v1.0/directory/deletedItems/{Uri.EscapeDataString(row.ObjectId)}", ct) != null)
        {
            action.Kind = RestoreActionKind.RecycleBin;
            action.Reason = "Deleted; it will be restored from the Entra recycle bin with its original ID and memberships.";
            action.TargetObjectId = row.ObjectId;
            s.Map[(def.Type, row.ObjectId)] = row.ObjectId;
            s.DirectoryObjectExists[row.ObjectId] = true;
            return action;
        }
        if (IsSynced(content))
        {
            action.Kind = RestoreActionKind.Skip;
            action.Reason = SyncedReason;
            return action;
        }
        if (handler is IResourceWriter writer && writer.CannotCreate(content) is { } cannot)
        {
            action.Kind = RestoreActionKind.Skip;
            action.Reason = cannot;
            return action;
        }
        var customizer = Customizer(def.Type);
        if (customizer != null && !customizer.CanCreate(content, out var why))
        {
            action.Kind = RestoreActionKind.Skip;
            action.Reason = why ?? "This object can't be recreated.";
            return action;
        }
        action.Kind = RestoreActionKind.Create;
        action.Reason = s.SameTenant ? "Missing from the tenant; it will be recreated (with a new ID)." : "Doesn't exist in the target tenant; it will be created.";
        if (def.Restore.Notes != null) action.Warnings.Add(def.Restore.Notes);
        return action;
    }

    /// <summary>Finds the object in the target that corresponds to a snapshot item.</summary>
    private async Task<string?> FindTargetAsync(RunState s, ResourceDefinition def, SnapshotItem row, System.Text.Json.Nodes.JsonObject content, CancellationToken ct)
    {
        if (def.Singleton) return (await LiveAsync(s, def.Type, ct))?.FirstOrDefault()?.ObjectId;
        var live = await LiveAsync(s, def.Type, ct) ?? [];
        if (s.Map.TryGetValue((def.Type, row.ObjectId), out var mapped) && live.Any(l => l.ObjectId == mapped)) return mapped;
        if (s.SameTenant && live.Any(l => l.ObjectId == row.ObjectId)) return row.ObjectId;
        // Same key (e.g. same name) — only when it's unambiguous.
        var key = MatchKey(def, content, row.DisplayName);
        var candidates = live.Where(l => MatchKey(def, l.Content, l.DisplayName) == key).ToList();
        return candidates.Count == 1 ? candidates[0].ObjectId : null;
    }

    private async Task<System.Text.Json.Nodes.JsonObject> RemapForPlanAsync(RunState s, ResourceDefinition def, System.Text.Json.Nodes.JsonObject content,
        HashSet<(string, string)> pendingCreates, List<string> unresolved, CancellationToken ct)
    {
        // Resolve asynchronously first (existence checks), then rewrite synchronously.
        var resolved = new Dictionary<(string, string), string?>();
        foreach (var r in def.References)
        foreach (var id in JsonPath.GetStrings(content, r.Path).Where(v => IsReference(s, r, v)).Distinct())
            resolved[(r.Type, id)] = await ResolveForPlanAsync(s, r.Type, id, pendingCreates, ct);
        return Remap(def, content, s, (t, id) => resolved.GetValueOrDefault((t, id)), unresolved);
    }

    /// <summary>Target ID for a referenced object; the source ID itself as a placeholder if it will be created by this plan.</summary>
    private async Task<string?> ResolveForPlanAsync(RunState s, string refType, string sourceId, HashSet<(string, string)> pendingCreates, CancellationToken ct)
    {
        foreach (var t in RefTypes(refType))
        {
            if (s.Map.TryGetValue((t, sourceId), out var m)) return m;
            if (pendingCreates.Contains((t, sourceId))) return sourceId;
        }
        if (s.SameTenant && await ExistsInTargetAsync(s, refType, sourceId, ct)) return sourceId;
        if (!s.SameTenant)
        {
            foreach (var t in RefTypes(refType))
            {
                if (!s.Rows.TryGetValue((t, sourceId), out var row) || registry.Find(t) is not { } h) continue;
                var match = await FindTargetAsync(s, h.Definition, row, await ContentAsync(s, row, ct), ct);
                if (match != null)
                {
                    s.Map[(t, sourceId)] = match;
                    return match;
                }
            }
        }
        return null;
    }
}
