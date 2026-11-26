using Mbr.Core.Catalog;

namespace Mbr.Catalog;

/// <summary>All resource handlers, keyed by type, with dependency ordering for restores.</summary>
public sealed class ResourceRegistry
{
    private readonly Dictionary<string, IResourceHandler> _handlers;

    public ResourceRegistry(IEnumerable<IResourceHandler> customHandlers)
    {
        _handlers = CatalogLoader.All.ToDictionary(d => d.Type, d => d.Api == "graph" ? new GenericGraphHandler(d) : (IResourceHandler)new PowerShell.PowerShellHandler(d));
        foreach (var h in customHandlers) _handlers[h.Definition.Type] = h; // custom handlers override catalog entries
    }

    public IReadOnlyCollection<IResourceHandler> Handlers => _handlers.Values;
    public IResourceHandler? Find(string type) => _handlers.GetValueOrDefault(type);
    public IResourceHandler Get(string type) => Find(type) ?? throw new KeyNotFoundException($"Unknown resource type '{type}'.");

    public IEnumerable<string> Workloads => _handlers.Values.Select(h => h.Definition.Workload).Distinct().Order();

    public IEnumerable<IResourceHandler> ForWorkloads(IEnumerable<string>? workloads)
    {
        var set = workloads?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ordered().Where(h => set is null || set.Count == 0 || set.Contains(h.Definition.Workload) || set.Contains(h.Definition.Type));
    }

    /// <summary>Handlers in dependency order (dependencies first), stable by type name otherwise.</summary>
    public IReadOnlyList<IResourceHandler> Ordered()
    {
        var result = new List<IResourceHandler>();
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        void Visit(IResourceHandler h, Stack<string> path)
        {
            var t = h.Definition.Type;
            if (state.GetValueOrDefault(t) == 2) return;
            if (state.GetValueOrDefault(t) == 1) throw new InvalidOperationException($"Dependency cycle: {string.Join(" -> ", path.Reverse())} -> {t}");
            state[t] = 1;
            path.Push(t);
            foreach (var dep in h.Definition.DependsOn.Order())
                if (_handlers.TryGetValue(dep, out var dh)) Visit(dh, path);
            path.Pop();
            state[t] = 2;
            result.Add(h);
        }
        foreach (var h in _handlers.Values.OrderBy(h => h.Definition.Type, StringComparer.Ordinal)) Visit(h, new Stack<string>());
        return result;
    }
}
