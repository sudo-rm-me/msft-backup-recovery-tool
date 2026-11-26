using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mbr.Catalog;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Model;
using Mbr.Infrastructure.Backup;

namespace Mbr.Infrastructure.Demo;

/// <summary>
/// Stands in for Exchange Online / Security &amp; Compliance / Teams PowerShell: understands the handler's "capture" and "write"
/// operations (not arbitrary scripts) and keeps objects per module and cmdlet noun.
/// </summary>
public sealed class InMemoryPowerShell : IPowerShellRunner
{
    private readonly ConcurrentDictionary<string, List<JsonObject>> _store = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> Log { get; } = new();
    /// <summary>Nouns whose Get- cmdlet fails (e.g. not licensed), with the error.</summary>
    public Dictionary<string, string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<JsonObject> Objects(string api, string noun) => _store.GetOrAdd($"{api}:{noun}", _ => new List<JsonObject>());

    public JsonObject Add(string api, string noun, JsonObject item)
    {
        item["Guid"] ??= Guid.NewGuid().ToString();
        item["Identity"] ??= item["Name"]?.ToString();
        lock (Objects(api, noun)) Objects(api, noun).Add(item);
        return item;
    }

    public JsonObject? Find(string api, string noun, string identity)
    {
        var list = Objects(api, noun);
        lock (list)
            return list.FirstOrDefault(o => new[] { "Guid", "Identity", "Name" }.Any(k => string.Equals(o[k]?.ToString(), identity, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<JsonNode?> RunAsync(string script, IDictionary<string, object?> parameters, CancellationToken ct)
    {
        var api = parameters["Api"]!.ToString()!;
        return Task.FromResult<JsonNode?>(parameters["Operation"]?.ToString() switch
        {
            "capture" => Capture(api, (JsonArray)parameters["Nouns"]!),
            "write" => Write(api, parameters["Noun"]!.ToString()!, parameters["Mode"]!.ToString()!, parameters["Identity"]?.ToString(),
                parameters["Singleton"] is true, (JsonObject)parameters["Body"]!),
            _ => throw new NotSupportedException("The in-memory PowerShell only runs the handler's capture and write operations."),
        });
    }

    private JsonObject Capture(string api, JsonArray nouns)
    {
        var result = new JsonObject();
        foreach (var n in nouns)
        {
            var noun = n!["Noun"]!.ToString();
            Log.Enqueue($"{api} Get-{noun}");
            if (Failing.TryGetValue(noun, out var error)) { result[noun] = new JsonObject { ["__error"] = error }; continue; }
            var list = Objects(api, noun);
            lock (list) result[noun] = new JsonArray(list.Select(o => (JsonNode)o.DeepClone()).ToArray());
        }
        return result;
    }

    private JsonObject Write(string api, string noun, string mode, string? identity, bool singleton, JsonObject body)
    {
        Log.Enqueue($"{api} {mode} {noun} {identity}");
        var def = CatalogLoader.All.FirstOrDefault(d => d.Api == api && (d.PowerShell?.Noun ?? d.ListPath) == noun);
        // Parameters that are named differently from the property they come from (Policy ← ParentPolicyName).
        var props = new JsonObject();
        foreach (var (k, v) in body)
        {
            var prop = def?.PowerShell?.Renames.FirstOrDefault(r => r.Value == k).Key ?? k;
            props[prop] = v?.DeepClone();
        }
        var list = Objects(api, noun);
        lock (list)
        {
            switch (mode)
            {
                case "create":
                {
                    if (def?.PowerShell?.CreateIdentityPrefix is { } prefix && props["Identity"]?.ToString() is { } id && !id.StartsWith(prefix))
                        props["Identity"] = prefix + id;
                    if (list.Any(o => string.Equals(o["Name"]?.ToString() ?? o["Identity"]?.ToString(), props["Name"]?.ToString() ?? props["Identity"]?.ToString(), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"New-{noun}: an object with this name already exists.");
                    props["Guid"] = Guid.NewGuid().ToString();
                    props["Identity"] ??= props["Name"]?.ToString();
                    list.Add(props);
                    return new JsonObject { ["Guid"] = props["Guid"]!.ToString(), ["Identity"] = props["Identity"]?.ToString(), ["Name"] = props["Name"]?.ToString() };
                }
                case "update":
                {
                    var target = singleton ? list.FirstOrDefault() : Find(api, noun, identity ?? "")
                                 ?? throw new InvalidOperationException($"Set-{noun}: '{identity}' wasn't found.");
                    foreach (var (k, v) in props)
                        if (k != "Identity") target[k] = v?.DeepClone();
                    return new JsonObject { ["ok"] = true };
                }
                case "delete":
                {
                    var target = Find(api, noun, identity ?? "") ?? throw new InvalidOperationException($"Remove-{noun}: '{identity}' wasn't found.");
                    list.Remove(target);
                    return new JsonObject { ["ok"] = true };
                }
                default: throw new NotSupportedException(mode);
            }
        }
    }
}

/// <summary>Serves one in-memory PowerShell for the given tenants (the demo tenant), the real runner otherwise.</summary>
public sealed class InMemoryPowerShellFactory(IPowerShellRunnerFactory? inner, InMemoryPowerShell powerShell, Func<Tenant, bool>? applies = null) : IPowerShellRunnerFactory
{
    public IPowerShellRunner Create(Tenant tenant, AppRole role) =>
        applies is null || applies(tenant) ? powerShell : inner?.Create(tenant, role) ?? powerShell;
}
