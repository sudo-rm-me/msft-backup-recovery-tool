using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;

namespace Mbr.Catalog;

/// <summary>Captures any Graph resource described by a <see cref="ResourceDefinition"/>.</summary>
public class GenericGraphHandler(ResourceDefinition definition) : IResourceHandler
{
    public ResourceDefinition Definition { get; } = definition;

    public async IAsyncEnumerable<CapturedItem> CaptureAsync(ITenantSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        var d = Definition;
        if (d.Singleton)
        {
            var obj = await session.Graph.GetAsync(d.Versioned(d.ListPath), ct) as JsonObject
                      ?? throw new InvalidOperationException($"{d.Type}: empty response");
            yield return new CapturedItem(d.Type, "singleton", d.DisplayName, Normalizer.Normalize(obj, d));
            yield break;
        }

        var raws = new List<JsonObject>();
        await foreach (var raw in session.Graph.ListAsync(d.Versioned(d.ListPath), ct))
            if (!ShouldSkip(raw)) raws.Add(raw);

        // Fetch details and children with bounded parallelism (the API client also limits per-tenant concurrency).
        var results = new CapturedItem?[raws.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, raws.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) => results[i] = await CaptureOneAsync(session, raws[i], token));
        foreach (var r in results)
            if (r != null) yield return r;
    }

    public async Task<CapturedItem?> CaptureByIdAsync(ITenantSession session, string id, CancellationToken ct)
    {
        var d = Definition;
        if (d.Singleton)
        {
            await foreach (var s in CaptureAsync(session, ct)) return s;
            return null;
        }
        var raw = await session.Graph.TryGetAsync(ItemUrl(id), ct) as JsonObject;
        return raw is null ? null : await CaptureOneAsync(session, raw, ct, alreadyFull: true);
    }

    public async Task<List<CapturedItem>> ListAsync(ITenantSession session, CancellationToken ct)
    {
        var d = Definition;
        var list = new List<CapturedItem>();
        if (d.Singleton)
        {
            await foreach (var s in CaptureAsync(session, ct)) list.Add(s);
            return list;
        }
        await foreach (var raw in session.Graph.ListAsync(d.Versioned(d.ListPath), ct))
        {
            if (ShouldSkip(raw)) continue;
            var id = IdOf(raw);
            list.Add(new CapturedItem(d.Type, id, Normalizer.DisplayNameOf(raw, d, id), Normalizer.Normalize(raw, d)));
        }
        return list;
    }

    /// <summary>Item URL including the list's $expand, so a single GET returns the same shape as the list.</summary>
    private string ItemUrl(string id)
    {
        var d = Definition;
        var query = d.ItemQuery;
        if (query is null && d.ListPath.Contains("$expand="))
            query = string.Join("&", d.ListPath[(d.ListPath.IndexOf('?') + 1)..].Split('&').Where(p => p.StartsWith("$expand=")));
        return d.Versioned(d.ItemPathFor(id)) + (string.IsNullOrEmpty(query) ? "" : "?" + query);
    }

    private string IdOf(JsonObject raw) =>
        raw[Definition.IdProperty]?.GetValue<string>() ?? throw new InvalidOperationException($"{Definition.Type}: item without {Definition.IdProperty}");

    protected virtual async Task<CapturedItem?> CaptureOneAsync(ITenantSession session, JsonObject raw, CancellationToken ct, bool alreadyFull = false)
    {
        var d = Definition;
        var id = IdOf(raw);
        try
        {
            if (d.GetItem && !alreadyFull)
            {
                if (await session.Graph.TryGetAsync(ItemUrl(id), ct) is not JsonObject full) return null; // deleted meanwhile
                foreach (var (k, v) in raw) full.TryAdd(k, v?.DeepClone());
                raw = full;
            }

            var normalized = Normalizer.Normalize(raw, d);
            foreach (var child in d.Children)
            {
                JsonArray arr;
                if (child.Expanded)
                {
                    arr = raw[child.Name] as JsonArray ?? new JsonArray();
                }
                else
                {
                    arr = new JsonArray();
                    var path = "/" + (child.Version ?? d.Version) + "/" + child.Path.Replace("{id}", Uri.EscapeDataString(id));
                    try
                    {
                        await foreach (var c in session.Graph.ListAsync(path, ct)) arr.Add(c);
                    }
                    catch (ApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Some child collections 404 instead of returning an empty list.
                    }
                }
                normalized[child.Name] = new JsonArray(arr.OfType<JsonObject>().Select(c => (JsonNode)Normalizer.NormalizeChild(c, child)).ToArray());
            }
            return new CapturedItem(d.Type, id, Normalizer.DisplayNameOf(raw, d, id), normalized);
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // removed between list and get
        }
        catch (ApiException e)
        {
            await session.ReportIssueAsync(new CaptureIssue(d.Type, id, IssueSeverity.Warning,
                $"Could not read '{Normalizer.DisplayNameOf(raw, d, id)}' completely: {e.Message}"));
            return new CapturedItem(d.Type, id, Normalizer.DisplayNameOf(raw, d, id), Normalizer.Normalize(raw, d));
        }
    }

    private bool ShouldSkip(JsonObject raw) =>
        Definition.Skip.Any(rule => raw[rule.Property] is JsonValue v &&
                                    string.Equals(v.ToString(), rule.Value, StringComparison.OrdinalIgnoreCase));
}
