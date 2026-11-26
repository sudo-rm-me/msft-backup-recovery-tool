using System.Reflection;
using Mbr.Core.Catalog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mbr.Catalog;

/// <summary>Loads the declarative resource catalog (catalog/*.yaml, embedded in this assembly).</summary>
public static class CatalogLoader
{
    private static readonly Lazy<IReadOnlyList<ResourceDefinition>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<ResourceDefinition> All => Embedded.Value;

    private static IReadOnlyList<ResourceDefinition> LoadEmbedded()
    {
        var asm = typeof(CatalogLoader).Assembly;
        var list = new List<ResourceDefinition>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".yaml")).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.AddRange(Parse(reader.ReadToEnd(), name));
        }
        Validate(list);
        return list;
    }

    public static List<ResourceDefinition> Parse(string yaml, string source = "yaml")
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        try
        {
            return deserializer.Deserialize<List<ResourceDefinition>>(yaml) ?? new();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Invalid catalog file {source}: {e.Message}", e);
        }
    }

    public static void Validate(IReadOnlyCollection<ResourceDefinition> defs)
    {
        var dupes = defs.GroupBy(d => d.Type).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) throw new InvalidOperationException($"Duplicate catalog types: {string.Join(", ", dupes)}");
        foreach (var d in defs)
        {
            if (string.IsNullOrWhiteSpace(d.Type) || string.IsNullOrWhiteSpace(d.Workload) || string.IsNullOrWhiteSpace(d.ListPath) && d.Api == "graph" && !d.Singleton)
                throw new InvalidOperationException($"Catalog entry '{d.Type}' is missing type, workload or listPath.");
        }
    }
}
