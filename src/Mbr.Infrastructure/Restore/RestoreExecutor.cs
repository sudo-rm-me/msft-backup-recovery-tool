using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Catalog.Restore;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Jobs;
using Mbr.Core.Json;
using Mbr.Core.Model;
using Mbr.Core.Restore;
using Microsoft.EntityFrameworkCore;

namespace Mbr.Infrastructure.Restore;

public sealed partial class RestoreEngine
{
    private sealed class Ctx(RunState state, ResourceDefinition def, RestoreAction action, IJobContext? job, Func<string, string, string?> map) : IRestoreContext
    {
        public ITenantSession Session => state.Session;
        public RestoreOptions Options => state.Options;
        public ResourceDefinition Definition => def;
        public string? MapId(string type, string sourceId) => map(type, sourceId);
        public async Task WarnAsync(string message)
        {
            lock (action.Warnings) action.Warnings.Add(message);
            if (job != null) await job.WarnAsync($"{action.DisplayName}: {message}");
        }
    }

    public async Task ApplyAsync(RestorePlan plan, IJobContext? job, CancellationToken ct)
    {
        var req = JsonSerializer.Deserialize<RestoreRequest>(plan.RequestJson, Json)!;
        var doc = JsonSerializer.Deserialize<RestorePlanDocument>(plan.PlanJson, Json)!;
        var s = await CreateStateAsync(req.SnapshotId, plan.TenantId, req.Options, ct);
        // Everything the plan already knew (matched objects, recycle-bin targets).
        foreach (var a in doc.Actions.Where(a => a.TargetObjectId != null && a.SourceObjectId != null && a.Kind != RestoreActionKind.Create))
            s.Map[(a.Type, a.SourceObjectId!)] = a.TargetObjectId!;

        var todo = doc.Actions.Where(a => !a.Excluded && a.Kind != RestoreActionKind.Skip).ToList();
        foreach (var a in todo.Where(a => a.Kind is RestoreActionKind.Create or RestoreActionKind.RecycleBin && a.SourceObjectId != null))
            s.Returning.Add((a.Type, a.SourceObjectId!));
        foreach (var a in doc.Actions.Where(a => a.Excluded)) a.Status = RestoreActionStatus.Excluded;
        foreach (var a in doc.Actions.Where(a => !a.Excluded && a.Kind == RestoreActionKind.Skip)) a.Status = RestoreActionStatus.Skipped;
        var stop = false;
        for (var i = 0; i < todo.Count; i++)
        {
            var action = todo[i];
            if (stop) { action.Status = RestoreActionStatus.Skipped; action.Error = "Not attempted: an earlier action failed and stop-on-error is on."; continue; }
            if (job != null) await job.ReportProgressAsync((double)i / todo.Count, $"{action.Kind} {action.DisplayName}");
            try
            {
                await ApplyActionAsync(s, action, job, ct);
                action.Status = RestoreActionStatus.Succeeded;
                if (job != null) await job.InfoAsync($"{action.Kind}: {action.DisplayName} ({registry.Get(action.Type).Definition.DisplayName}) ✓");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                action.Status = RestoreActionStatus.Failed;
                action.Error = e.Message;
                if (job != null) await job.ErrorAsync($"{action.Kind}: {action.DisplayName} failed: {e.Message}");
                if (req.Options.StopOnError) stop = true;
            }
            if (action.TargetObjectId != null && action.SourceObjectId != null)
                await SaveMappingAsync(s, action.Type, action.SourceObjectId, action.TargetObjectId, ct);
            if (i % 10 == 9) await SavePlanAsync(plan, doc, ct);
        }

        // Verify: re-read what was written and compare with the snapshot (references mapped to the target).
        if (job != null) await job.ReportProgressAsync(0.95, "Verifying");
        foreach (var action in todo.Where(a => a.Status == RestoreActionStatus.Succeeded && a.Kind is RestoreActionKind.Create or RestoreActionKind.Update))
        {
            try { await VerifyAsync(s, action, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { action.VerificationIssues.Add($"Couldn't verify: {e.Message}"); }
        }

        plan.SucceededCount = todo.Count(a => a.Status == RestoreActionStatus.Succeeded);
        plan.FailedCount = todo.Count(a => a.Status == RestoreActionStatus.Failed);
        plan.Status = plan.FailedCount == 0 ? RestorePlanStatus.Applied : plan.SucceededCount > 0 ? RestorePlanStatus.PartiallyApplied : RestorePlanStatus.Failed;
        plan.AppliedAt = DateTimeOffset.UtcNow;
        await SavePlanAsync(plan, doc, ct);
    }

    private async Task SavePlanAsync(RestorePlan plan, RestorePlanDocument doc, CancellationToken ct)
    {
        plan.PlanJson = JsonSerializer.Serialize(doc, Json);
        plan.WarningCount = doc.Actions.Where(a => a.Kind != RestoreActionKind.Skip).Sum(a => a.Warnings.Count + a.VerificationIssues.Count);
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveMappingAsync(RunState s, string type, string sourceId, string targetId, CancellationToken ct)
    {
        s.Map[(type, sourceId)] = targetId;
        var row = await db.IdMappings.FirstOrDefaultAsync(m => m.SourceTenantId == s.SourceTenant.Id && m.TargetTenantId == s.TargetTenant.Id &&
                                                               m.ResourceType == type && m.SourceId == sourceId, ct);
        if (row is null)
            db.IdMappings.Add(new IdMapping { SourceTenantId = s.SourceTenant.Id, TargetTenantId = s.TargetTenant.Id, ResourceType = type, SourceId = sourceId, TargetId = targetId });
        else if (row.TargetId != targetId)
        {
            row.TargetId = targetId;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resolves references at apply time: created/matched objects first, then the same ID in a same-tenant restore.</summary>
    private Func<string, string, string?> Resolver(RunState s) => (refType, sourceId) =>
    {
        foreach (var t in RefTypes(refType))
            if (s.Map.TryGetValue((t, sourceId), out var m)) return m;
        if (s.SameTenant)
        {
            // Same tenant: an object that wasn't part of the plan keeps its ID if it still exists.
            if (DirectoryObjectTypes.Contains(refType) || refType == "directoryObject")
                return s.DirectoryObjectExists.GetValueOrDefault(sourceId, true) ? sourceId : null;
            var live = s.Live.GetValueOrDefault(refType);
            return live is null || live.Any(l => l.ObjectId == sourceId) ? sourceId : null;
        }
        return null;
    };

    private async Task ApplyActionAsync(RunState s, RestoreAction action, IJobContext? job, CancellationToken ct)
    {
        var handler = registry.Get(action.Type);
        var def = handler.Definition;
        var graph = s.Session.Graph;
        var resolver = Resolver(s);
        var ctx = new Ctx(s, def, action, job, resolver);
        var customizer = Customizer(def.Type);

        if (action.Kind == RestoreActionKind.Delete)
        {
            if (customizer is null || !await customizer.DeleteAsync(ctx, action.TargetObjectId!, ct))
            {
                if (handler is IResourceWriter w) await w.DeleteAsync(ctx, action.TargetObjectId!, ct);
                else await graph.SendAsync(HttpMethod.Delete, def.Versioned(def.ItemPathFor(action.TargetObjectId!)), ct: ct);
            }
            return;
        }
        if (action.Kind == RestoreActionKind.RecycleBin)
        {
            await graph.SendAsync(HttpMethod.Post, $"/v1.0/directory/deletedItems/{Uri.EscapeDataString(action.TargetObjectId!)}/restore", new JsonObject(), ct: ct);
            s.DirectoryObjectExists[action.TargetObjectId!] = true;
            // Entra normally restores memberships with the object; re-add any that are missing (additive only).
            if (s.Rows.TryGetValue((action.Type, action.SourceObjectId!), out var deletedRow))
            {
                var restored = Remap(def, await ContentAsync(s, deletedRow, ct), s, resolver, null);
                await ApplyChildrenAsync(s, def, action, restored, action.TargetObjectId!, false, customizer, ct);
            }
            return;
        }

        // Pre-check references the plan didn't know about (same tenant: confirm referenced directory objects still exist).
        var row = s.Rows[(action.Type, action.SourceObjectId!)];
        var content = await ContentAsync(s, row, ct);
        if (action.IsRelink && action.Kind == RestoreActionKind.Update)
        {
            var current = await handler.CaptureByIdAsync(s.Session, action.TargetObjectId!, ct)
                          ?? throw new InvalidOperationException("The object no longer exists in the tenant.");
            content = s.RelinkContent[action.Index] = MergeRelink(def, current.Content, content, s.IsReturning);
        }
        foreach (var r in def.References)
        foreach (var id in JsonPath.GetStrings(content, r.Path).Where(v => IsReference(s, r, v)).Distinct())
            if (s.SameTenant && (DirectoryObjectTypes.Contains(r.Type) || r.Type == "directoryObject") &&
                !RefTypes(r.Type).Any(t => s.Map.ContainsKey((t, id))))
                await ExistsInTargetAsync(s, r.Type, id, ct);

        var remapped = Remap(def, content, s, resolver, null);
        var isCreate = action.Kind == RestoreActionKind.Create;
        var body = BuildBody(s, def, remapped, isCreate);
        customizer?.PrepareBody(ctx, body, isCreate);

        string targetId;
        if (isCreate)
        {
            var id = customizer is null ? null : await customizer.CreateAsync(ctx, body, ct);
            if (id is null && handler is IResourceWriter writer) id = await writer.CreateAsync(ctx, body, ct);
            if (id is null)
            {
                var createPath = (def.Restore.CreatePath ?? def.ListPath).Split('?')[0];
                var created = await graph.SendAsync(HttpMethod.Post, def.Versioned(createPath), body, ct: ct);
                id = created?[def.IdProperty]?.GetValue<string>() ?? created?["id"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("The create response contained no ID.");
            }
            targetId = id;
            action.TargetObjectId = id;
            s.Map[(def.Type, action.SourceObjectId!)] = id;
        }
        else
        {
            targetId = action.TargetObjectId!;
            if (customizer is null || !await customizer.UpdateAsync(ctx, targetId, body, ct))
            {
                if (handler is IResourceWriter w)
                {
                    await w.UpdateAsync(ctx, targetId, body, ct);
                    goto written;
                }
                var path = def.Singleton ? def.ListPath.Split('?')[0] : def.ItemPathFor(targetId);
                var method = def.Restore.UpdateMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Put : HttpMethod.Patch;
                await graph.SendAsync(method, def.Versioned(path), body, ct: ct);
            }
        }

        written:
        await ApplyChildrenAsync(s, def, action, remapped, targetId, isCreate, customizer, ct);
        if (customizer != null) await customizer.AfterWriteAsync(ctx, targetId, remapped, isCreate, ct);
    }

    private static JsonObject BuildBody(RunState s, ResourceDefinition def, JsonObject remapped, bool isCreate)
    {
        var body = (JsonObject)remapped.DeepClone();
        foreach (var child in def.Children)
            if (child.Restore.Mode != "inline") body.Remove(child.Name);
            else if (body[child.Name] is JsonArray arr) body[child.Name] = CleanChildren(arr);
        foreach (var p in def.Restore.ReadOnly) JsonPath.Remove(body, p);
        if (!isCreate) foreach (var p in def.Restore.CreateOnly) JsonPath.Remove(body, p);
        if (isCreate && !string.IsNullOrEmpty(s.Options.NameSuffix) && def.NameProperty is "displayName" or "name" &&
            body[def.NameProperty] is JsonValue v && v.TryGetValue<string>(out var name))
            body[def.NameProperty] = name + s.Options.NameSuffix;
        if (def.Type == "entra.conditionalAccessPolicy" && s.Options.ConditionalAccessReportOnly)
            body["state"] = "enabledForReportingButNotEnforced";
        return body;
    }

    /// <summary>Drops child entries whose key reference couldn't be resolved (e.g. an assignment to a group that doesn't exist).</summary>
    private static JsonArray CleanChildren(JsonArray children)
    {
        var result = new JsonArray();
        foreach (var c in children.OfType<JsonObject>())
        {
            if (c.ContainsKey("id") && c["id"] is null) continue;
            if (c["target"] is JsonObject target)
            {
                if (target.ContainsKey("groupId") && target["groupId"] is null) continue;
                if (target.ContainsKey("deviceAndAppManagementAssignmentFilterId") && target["deviceAndAppManagementAssignmentFilterId"] is null)
                {
                    target.Remove("deviceAndAppManagementAssignmentFilterId");
                    target["deviceAndAppManagementAssignmentFilterType"] = "none";
                }
            }
            result.Add(c.DeepClone());
        }
        return result;
    }

    private async Task ApplyChildrenAsync(RunState s, ResourceDefinition def, RestoreAction action, JsonObject remapped, string targetId, bool created,
        IRestoreCustomizer? customizer, CancellationToken ct)
    {
        var graph = s.Session.Graph;
        foreach (var child in def.Children)
        {
            if (child.Restore.Mode is "inline" or "none") continue;
            if (!s.Options.IncludeAssignments && AssignmentLikeChildren.Contains(child.Name)) continue;
            if (customizer is IChildFilter f && f.SkipChild(remapped, child.Name)) continue;
            var items = CleanChildren(remapped[child.Name] as JsonArray ?? new JsonArray());
            var version = child.Version ?? def.Version;
            string P(string path) => $"/{version}/{path.Replace("{id}", Uri.EscapeDataString(targetId)).Split('?')[0]}";

            switch (child.Restore.Mode)
            {
                case "assign":
                {
                    var path = child.Restore.Path ?? def.ItemPathFor(targetId) + "/assign";
                    var prop = child.Restore.BodyProperty ?? child.Name;
                    await graph.SendAsync(HttpMethod.Post, P(path), new JsonObject { [prop] = items }, ct: ct);
                    break;
                }
                case "each":
                {
                    var path = P(child.Restore.Path ?? child.Path);
                    if (!created)
                        foreach (var existing in await ListOrEmptyAsync(graph, path, ct))
                            if (existing["id"]?.GetValue<string>() is { } eid)
                                await graph.SendAsync(HttpMethod.Delete, $"{path}/{Uri.EscapeDataString(eid)}", ct: ct);
                    foreach (var item in items) await graph.SendAsync(HttpMethod.Post, path, item, ct: ct);
                    break;
                }
                case "add":
                {
                    // Additive: post entries that aren't there yet (compared on the configured keys).
                    var path = P(child.Restore.Path ?? child.Path);
                    var current = created ? [] : await ListOrEmptyAsync(graph, path, ct);
                    string Key(JsonNode? n) => string.Join("|", child.Restore.Keys.Select(k => JsonPath.GetStrings(n, k).FirstOrDefault()?.ToLowerInvariant() ?? ""));
                    var have = current.Select(c => Key(c)).ToHashSet();
                    foreach (var item in items.OfType<JsonObject>())
                    {
                        if (have.Contains(Key(item)) || child.Restore.Keys.Any(k => !JsonPath.GetStrings(item, k).Any())) continue;
                        var body = (JsonObject)item.DeepClone();
                        if (child.Restore.ParentIdProperty is { } pp) body[pp] = targetId;
                        await graph.SendAsync(HttpMethod.Post, path, body, ct: ct);
                    }
                    break;
                }
                case "ref":
                {
                    var listPath = P(child.Path);
                    var current = new HashSet<string>();
                    if (!created)
                        foreach (var m in await ListOrEmptyAsync(graph, listPath, ct))
                            if (m["id"]?.GetValue<string>() is { } mid) current.Add(mid);
                    var refPath = P(child.Restore.Path ?? child.Path.Split('?')[0] + "/$ref");
                    var added = 0;
                    foreach (var id in items.OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).Where(id => id != null && !current.Contains(id!)))
                    {
                        try
                        {
                            await graph.SendAsync(HttpMethod.Post, refPath,
                                new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{id}" }, ct: ct);
                            added++;
                        }
                        catch (ApiException e) when (e.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase)) { }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Lists a child collection; a 404 means there is nothing there yet.</summary>
    private static async Task<List<JsonObject>> ListOrEmptyAsync(IApiClient graph, string path, CancellationToken ct)
    {
        var list = new List<JsonObject>();
        try { await foreach (var o in graph.ListAsync(path, ct)) list.Add(o); }
        catch (ApiException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        return list;
    }

    private async Task VerifyAsync(RunState s, RestoreAction action, CancellationToken ct)
    {
        var handler = registry.Get(action.Type);
        var def = handler.Definition;
        var row = s.Rows[(action.Type, action.SourceObjectId!)];
        var source = s.RelinkContent.TryGetValue(action.Index, out var merged) ? merged : await ContentAsync(s, row, ct);
        var expected = Remap(def, source, s, Resolver(s), null);
        foreach (var child in def.Children.Where(c => c.Restore.Mode == "none" || !s.Options.IncludeAssignments && AssignmentLikeChildren.Contains(c.Name)))
            expected.Remove(child.Name);
        foreach (var child in def.Children) if (expected[child.Name] is JsonArray arr) expected[child.Name] = CleanChildren(arr);
        if (def.Type == "entra.conditionalAccessPolicy" && s.Options.ConditionalAccessReportOnly) expected["state"] = "enabledForReportingButNotEnforced";
        if (action.Kind == RestoreActionKind.Create && !string.IsNullOrEmpty(s.Options.NameSuffix) && expected[def.NameProperty] is JsonValue v && v.TryGetValue<string>(out var n))
            expected[def.NameProperty] = n + s.Options.NameSuffix;
        var live = await handler.CaptureByIdAsync(s.Session, action.TargetObjectId!, ct);
        if (live is null)
        {
            action.VerificationIssues.Add("The object can't be read back.");
            return;
        }
        var changes = MeaningfulChanges(live.Content, expected, ExcludedFromCompare(def, s.Options));
        action.VerificationIssues.AddRange(changes.Take(20).Select(c => $"{(c.Path.Length == 0 ? "(object)" : c.Path)}: expected {Short(c.After)}, found {Short(c.Before)}"));
    }

    private static string Short(JsonNode? n)
    {
        var t = n?.ToJsonString() ?? "nothing";
        return t.Length > 100 ? t[..97] + "..." : t;
    }
}
