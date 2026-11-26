using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mbr.Cli;

/// <summary>Shared helpers for commands: client, output formatting, common options.</summary>
public static class Cli
{
    public static readonly Option<bool> JsonOption = new("--json") { Description = "Output raw JSON", Recursive = true };

    public static Option<string?> TenantOption() => new("--tenant", "-t") { Description = "Tenant: ID, directory ID, domain or display name (default: MBR_TENANT)" };

    public static MbrClient Client() => new(CliConfig.Load());

    public static bool WantsJson(ParseResult pr) => pr.GetValue(JsonOption);

    public static Command Cmd(string name, string description, Func<ParseResult, CancellationToken, Task> action, params Symbol[] symbols)
    {
        var c = new Command(name, description);
        foreach (var s in symbols)
        {
            switch (s)
            {
                case Option o: c.Options.Add(o); break;
                case Argument a: c.Arguments.Add(a); break;
            }
        }
        c.SetAction(async (pr, ct) =>
        {
            try
            {
                await action(pr, ct);
                return 0;
            }
            catch (CliException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
            catch (HttpRequestException e)
            {
                Console.Error.WriteLine($"Could not reach the server: {e.Message}");
                return 2;
            }
        });
        return c;
    }

    public static Command Group(string name, string description, params Command[] children)
    {
        var g = new Command(name, description);
        foreach (var c in children) g.Subcommands.Add(c);
        return g;
    }

    public static void PrintJson(JsonNode? node) =>
        Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");

    /// <summary>Prints an array as a table using the given (header, path) columns, or JSON with --json.</summary>
    public static void Print(ParseResult pr, JsonNode? data, params (string Header, string Path)[] columns)
    {
        if (WantsJson(pr) || data is not JsonArray arr)
        {
            PrintJson(data);
            return;
        }
        var rows = arr.Select(item => columns.Select(c => Format(Get(item, c.Path))).ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Min(60, Math.Max(c.Header.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length)))).ToArray();
        Console.WriteLine(string.Join("  ", columns.Select((c, i) => c.Header.PadRight(widths[i]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in rows)
            Console.WriteLine(string.Join("  ", r.Select((v, i) => (v.Length > widths[i] ? v[..(widths[i] - 1)] + "…" : v).PadRight(widths[i]))));
        if (rows.Count == 0) Console.WriteLine("(none)");
    }

    public static void PrintObject(ParseResult pr, JsonNode? data, params (string Label, string Path)[] fields)
    {
        if (WantsJson(pr) || data is null)
        {
            PrintJson(data);
            return;
        }
        var w = fields.Max(f => f.Label.Length);
        foreach (var f in fields) Console.WriteLine($"{f.Label.PadRight(w)}  {Format(Get(data, f.Path))}");
    }

    public static JsonNode? Get(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (node is JsonObject o) node = o[part];
            else return null;
        }
        return node;
    }

    private static string Format(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => DateTimeOffset.TryParse(s, out var d) && s.Contains('T')
            ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : s,
        JsonValue v when v.TryGetValue<double>(out var d) => d % 1 == 0 ? d.ToString("0") : d.ToString("0.##"),
        JsonArray a => string.Join(",", a.Select(x => Format(x))),
        _ => n.ToJsonString(),
    };

    /// <summary>Parses repeated "key=value" options into a JSON object (values parsed as JSON when possible).</summary>
    public static JsonObject ParseParams(IEnumerable<string>? pairs)
    {
        var o = new JsonObject();
        foreach (var p in pairs ?? [])
        {
            var i = p.IndexOf('=');
            if (i <= 0) throw new CliException($"Invalid parameter '{p}', expected key=value.");
            var key = p[..i];
            var value = p[(i + 1)..];
            JsonNode? node;
            try { node = JsonNode.Parse(value); } catch { node = JsonValue.Create(value); }
            if (value.Contains(',') && node is not JsonArray && !value.TrimStart().StartsWith('{'))
                node = new JsonArray(value.Split(',').Select(v => (JsonNode?)JsonValue.Create(v.Trim())).ToArray());
            o[key] = node;
        }
        return o;
    }

    /// <summary>Polls a job until it finishes, printing new log lines.</summary>
    public static async Task<JsonNode> WatchJobAsync(MbrClient client, string jobId, CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            var logs = (await client.GetAsync($"api/jobs/{jobId}/logs?after={after}", ct))!.AsArray();
            foreach (var l in logs)
            {
                after = l!["id"]!.GetValue<long>();
                Console.WriteLine($"{DateTimeOffset.Parse(l["at"]!.GetValue<string>()).ToLocalTime():HH:mm:ss} {l["level"],-7} {l["message"]}");
            }
            var job = (await client.GetAsync($"api/jobs/{jobId}", ct))!;
            var status = job["status"]!.GetValue<string>();
            if (status is "Succeeded" or "PartiallySucceeded" or "Failed" or "Cancelled")
            {
                Console.WriteLine($"Job {status}{(job["error"] is { } e ? ": " + e : "")}");
                return job;
            }
            await Task.Delay(2000, ct);
        }
    }
}
