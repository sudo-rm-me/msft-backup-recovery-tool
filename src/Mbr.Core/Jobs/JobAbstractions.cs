using System.Text.Json.Nodes;
using Mbr.Core.Model;

namespace Mbr.Core.Jobs;

/// <summary>Executes one job type. Handlers are resolved from DI by <see cref="Type"/>.</summary>
public interface IJobHandler
{
    string Type { get; }
    /// <summary>Minimum tenant role needed to start this job from the API.</summary>
    TenantRole RequiredRole { get; }
    Task<JobOutcome> ExecuteAsync(IJobContext context, CancellationToken ct);
}

public record JobOutcome(JobStatus Status, JsonNode? Result = null)
{
    public static JobOutcome Success(JsonNode? result = null) => new(JobStatus.Succeeded, result);
    public static JobOutcome Partial(JsonNode? result = null) => new(JobStatus.PartiallySucceeded, result);
}

public interface IJobContext
{
    Job Job { get; }
    Tenant? Tenant { get; }
    JsonObject Parameters { get; }
    IServiceProvider Services { get; }
    Task ReportProgressAsync(double fraction, string? message = null);
    Task LogAsync(JobLogLevel level, string message);
    Task SaveCheckpointAsync(JsonNode checkpoint);
    JsonNode? Checkpoint { get; }
}

public static class JobContextExtensions
{
    public static Task InfoAsync(this IJobContext c, string m) => c.LogAsync(JobLogLevel.Info, m);
    public static Task WarnAsync(this IJobContext c, string m) => c.LogAsync(JobLogLevel.Warning, m);
    public static Task ErrorAsync(this IJobContext c, string m) => c.LogAsync(JobLogLevel.Error, m);

    public static Tenant RequireTenant(this IJobContext c) =>
        c.Tenant ?? throw new InvalidOperationException($"Job type '{c.Job.Type}' requires a tenant.");

    public static string? GetString(this JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    public static bool GetBool(this JsonObject o, string name, bool fallback = false) =>
        o.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : fallback;

    public static Guid? GetGuid(this JsonObject o, string name) =>
        Guid.TryParse(o.GetString(name), out var g) ? g : null;

    public static IReadOnlyList<string> GetStringList(this JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var v) && v is JsonArray a
            ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList()
            : Array.Empty<string>();
}
