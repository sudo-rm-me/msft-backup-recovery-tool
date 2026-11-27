using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mbr.Core.Restore;

/// <summary>
/// Old → new domain names for restores into another tenant (or after a domain change). Rewrites addresses such as
/// alice@contoso.com, smtp:alice@contoso.com and values that are just the domain; other text is left alone.
/// </summary>
public sealed class DomainMap
{
    private readonly Dictionary<string, string> _map;
    private readonly Regex? _address;

    public DomainMap(IEnumerable<KeyValuePair<string, string>>? entries)
    {
        _map = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in entries ?? [])
        {
            var f = Normalize(from);
            var t = Normalize(to);
            if (f.Length == 0 || t.Length == 0 || f.Equals(t, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsDomain(f) || !IsDomain(t)) throw new ArgumentException($"'{from} → {to}' isn't a pair of domain names.");
            _map[f] = t;
        }
        if (_map.Count > 0)
            _address = new Regex($"(?<=@)({string.Join("|", _map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape))})(?![A-Za-z0-9.-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static readonly DomainMap Empty = new(null);

    public bool IsEmpty => _map.Count == 0;
    public IReadOnlyDictionary<string, string> Entries => _map;

    /// <summary>This map plus <paramref name="from"/> → <paramref name="to"/> unless <paramref name="from"/> is already mapped.</summary>
    public DomainMap With(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || _map.ContainsKey(Normalize(from))) return this;
        return new DomainMap(_map.Append(new(from, to)));
    }

    public string Apply(string value)
    {
        if (_address is null || string.IsNullOrEmpty(value)) return value;
        if (_map.TryGetValue(value.Trim(), out var whole)) return whole;
        return value.Contains('@') ? _address.Replace(value, m => _map[m.Value]) : value;
    }

    /// <summary>A copy of <paramref name="node"/> with every string value mapped.</summary>
    public JsonNode? Apply(JsonNode? node)
    {
        if (node is null || IsEmpty) return node?.DeepClone();
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o) obj[k] = Apply(v);
                return obj;
            case JsonArray a:
                return new JsonArray(a.Select(Apply).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Apply(s));
            default:
                return node.DeepClone();
        }
    }

    private static string Normalize(string? d) => (d ?? "").Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();

    private static bool IsDomain(string d) =>
        d.Contains('.') && d.Split('.').All(p => p.Length > 0 && p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
}
