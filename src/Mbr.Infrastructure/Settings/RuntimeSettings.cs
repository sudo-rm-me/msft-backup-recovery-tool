using System.Text.Json.Nodes;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Settings;

public enum SettingType { Text, Number, Boolean, List }

/// <summary>A deployment-wide setting that global admins can change in the app.</summary>
public sealed record SettingDefinition(
    string Key, string Group, string Label, string Hint, SettingType Type,
    Func<MbrOptions, JsonNode?> Get, Action<MbrOptions, JsonNode?> Set,
    bool Secret = false, int Min = int.MinValue, int Max = int.MaxValue, Func<string, string?>? Validate = null);

/// <summary>How a setting looks in the app. Secret values are never returned, only whether one is set.</summary>
public sealed record SettingView(
    string Key, string Group, string Label, string Hint, SettingType Type, bool Secret, int? Min, int? Max,
    JsonNode? Value, JsonNode? DeploymentValue, bool Overridden, bool IsSet, bool DeploymentIsSet);

/// <summary>
/// Settings changed in the app (Settings → General), stored in the database. They override the deployment's values
/// (environment variables from Terraform, appsettings.json) in the shared <see cref="MbrOptions"/>, so everything that
/// reads options sees them. Removing an override brings back the deployment's value. Every replica re-reads them
/// within <see cref="RuntimeSettingsRefresher.Interval"/>.
/// Infrastructure settings (database, Key Vault, app registrations, sign-in) are not here: they come from the deployment.
/// </summary>
public sealed class RuntimeSettings(IOptions<MbrOptions> options, IServiceScopeFactory scopes, SecretProtector secrets,
    ILogger<RuntimeSettings> log)
{
    public static readonly IReadOnlyList<SettingDefinition> Definitions =
    [
        new("Mbr:Notifications:Webhooks", "Alerts", "Webhook URLs",
            "Job failures and drift are posted to each URL as JSON with a text field (Teams or Slack workflows, or any receiver). Stored encrypted.",
            SettingType.List, o => Arr(o.Notifications.Webhooks), (o, n) => o.Notifications.Webhooks = List(n),
            Secret: true, Validate: HttpsUrl),
        new("Mbr:Notifications:Smtp:Host", "Alerts", "SMTP server", "Leave empty to send no email.",
            SettingType.Text, o => Val(o.Notifications.Smtp?.Host ?? ""), (o, n) => Smtp(o).Host = Str(n) ?? ""),
        new("Mbr:Notifications:Smtp:Port", "Alerts", "SMTP port", "",
            SettingType.Number, o => Val(o.Notifications.Smtp?.Port ?? 587), (o, n) => Smtp(o).Port = Int(n) ?? 587, Min: 1, Max: 65535),
        new("Mbr:Notifications:Smtp:UseSsl", "Alerts", "SMTP uses TLS", "",
            SettingType.Boolean, o => Val(o.Notifications.Smtp?.UseSsl ?? true), (o, n) => Smtp(o).UseSsl = Bool(n) ?? true),
        new("Mbr:Notifications:Smtp:Username", "Alerts", "SMTP user name", "Leave empty for a relay without sign-in.",
            SettingType.Text, o => Val(o.Notifications.Smtp?.Username ?? ""), (o, n) => Smtp(o).Username = Empty(Str(n))),
        new("Mbr:Notifications:Smtp:Password", "Alerts", "SMTP password", "Stored encrypted.",
            SettingType.Text, o => Val(o.Notifications.Smtp?.Password ?? ""), (o, n) => Smtp(o).Password = Empty(Str(n)), Secret: true),
        new("Mbr:Notifications:Smtp:From", "Alerts", "Email from", "",
            SettingType.Text, o => Val(o.Notifications.Smtp?.From ?? ""), (o, n) => Smtp(o).From = Str(n) ?? "", Validate: Email),
        new("Mbr:Notifications:Smtp:To", "Alerts", "Email recipients", "",
            SettingType.List, o => Arr(o.Notifications.Smtp?.To ?? []), (o, n) => Smtp(o).To = List(n), Validate: Email),

        new("Mbr:Partner:TenantId", "GDAP", "Partner tenant ID",
            "Your own tenant's ID, where your GDAP relationships with customers are managed. Enables Tenants → Import from GDAP.",
            SettingType.Text, o => Val(o.Partner.TenantId ?? ""), (o, n) => o.Partner.TenantId = Empty(Str(n)), Validate: TenantId),

        new("Mbr:Worker:MaxConcurrency", "Throughput", "Jobs per replica", "Jobs each replica runs at once.",
            SettingType.Number, o => Val(o.Worker.MaxConcurrency), (o, n) => o.Worker.MaxConcurrency = Int(n) ?? 4, Min: 1, Max: 64),
        new("Mbr:Worker:MaxJobsPerTenant", "Throughput", "Jobs per tenant",
            "Jobs running at once for one customer tenant, across all replicas (restores are exempt). 0 = no limit.",
            SettingType.Number, o => Val(o.Worker.MaxJobsPerTenant), (o, n) => o.Worker.MaxJobsPerTenant = Int(n) ?? 2, Min: 0, Max: 64),
        new("Mbr:Worker:MaxRequestsPerTenant", "Throughput", "API requests per tenant",
            "Concurrent Microsoft API requests per customer tenant, per replica. Lower it if a tenant is throttled.",
            SettingType.Number, o => Val(o.Worker.MaxRequestsPerTenant), (o, n) => o.Worker.MaxRequestsPerTenant = Int(n) ?? 8, Min: 1, Max: 64),
        new("Mbr:PowerShell:TimeoutMinutes", "Throughput", "PowerShell timeout (minutes)",
            "Longest a PowerShell workload (Exchange, Purview, Teams) may run in one job step.",
            SettingType.Number, o => Val(o.PowerShell.TimeoutMinutes), (o, n) => o.PowerShell.TimeoutMinutes = Int(n) ?? 30, Min: 5, Max: 480),
    ];

    public static SettingDefinition? Find(string key) =>
        Definitions.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The deployment's values, captured before any override is applied.</summary>
    private readonly Dictionary<string, JsonNode?> _deployment = Definitions.ToDictionary(d => d.Key, d => d.Get(options.Value));
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, JsonNode?> _overrides = new();
    private string? _stamp;

    public IReadOnlyList<SettingView> Describe()
    {
        var overrides = _overrides;
        return Definitions.Select(d =>
        {
            var overridden = overrides.TryGetValue(d.Key, out var o);
            var deployment = _deployment[d.Key];
            var effective = overridden ? o : deployment;
            return new SettingView(d.Key, d.Group, d.Label, d.Hint, d.Type, d.Secret,
                d.Min == int.MinValue ? null : d.Min, d.Max == int.MaxValue ? null : d.Max,
                d.Secret ? null : effective?.DeepClone(), d.Secret ? null : deployment?.DeepClone(),
                overridden, !IsEmpty(effective), !IsEmpty(deployment));
        }).ToList();
    }

    /// <summary>Reads the overrides from the database and applies them (or the deployment's values) to the options.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
            var rows = await db.AppSettings.AsNoTracking().Where(s => !s.Key.StartsWith("_")).ToListAsync(ct);
            var overrides = new Dictionary<string, JsonNode?>();
            foreach (var r in rows)
            {
                if (Find(r.Key) is not { } d) continue; // a setting this version no longer has
                try { overrides[d.Key] = JsonNode.Parse(r.Encrypted ? await secrets.UnprotectAsync(r.Value, ct) : r.Value); }
                catch (Exception e) { log.LogWarning(e, "Ignoring the app setting {Key}: it couldn't be read", r.Key); }
            }
            foreach (var d in Definitions)
                d.Set(options.Value, overrides.TryGetValue(d.Key, out var v) ? v : _deployment[d.Key]);
            _overrides = overrides;
            _stamp = Stamp(rows.Select(r => (r.Key, r.UpdatedAt)));
        }
        finally { _lock.Release(); }
    }

    /// <summary>Reloads when another replica (or the CLI) changed a setting.</summary>
    public async Task RefreshIfChangedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var rows = await db.AppSettings.AsNoTracking().Where(s => !s.Key.StartsWith("_")).Select(s => new { s.Key, s.UpdatedAt }).ToListAsync(ct);
        if (Stamp(rows.Select(r => (r.Key, r.UpdatedAt))) != _stamp) await ReloadAsync(ct);
    }

    /// <summary>
    /// Sets (non-null value) or resets to the deployment's value (null) each given setting. All values are checked first,
    /// so nothing is saved if one is invalid. Returns the keys changed.
    /// </summary>
    public async Task<List<string>> SaveAsync(IReadOnlyDictionary<string, JsonNode?> changes, string actor, CancellationToken ct = default)
    {
        var checkedValues = new List<(SettingDefinition Def, JsonNode? Value)>();
        foreach (var (key, value) in changes)
        {
            var d = Find(key) ?? throw new ArgumentException($"'{key}' isn't a setting that can be changed in the app.");
            checkedValues.Add((d, value is null ? null : Normalize(d, value)));
        }

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
            foreach (var (d, value) in checkedValues)
            {
                var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == d.Key, ct);
                if (value is null)
                {
                    if (row != null) db.AppSettings.Remove(row);
                    continue;
                }
                var json = value.ToJsonString();
                row ??= db.AppSettings.Add(new AppSetting { Key = d.Key }).Entity;
                row.Value = d.Secret ? await secrets.ProtectAsync(json, ct) : json;
                row.Encrypted = d.Secret;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedBy = actor;
            }
            await db.SaveChangesAsync(ct);
        }
        await ReloadAsync(ct);
        return checkedValues.Select(c => c.Def.Key).ToList();
    }

    /// <summary>Checks a value against its setting and returns it in canonical form; throws ArgumentException if it doesn't fit.</summary>
    public static JsonNode Normalize(SettingDefinition d, JsonNode value)
    {
        ArgumentException Bad(string why) => new($"{d.Label}: {why}");
        void Check(string s)
        {
            if (d.Validate?.Invoke(s) is { } error) throw Bad(error);
        }

        switch (d.Type)
        {
            case SettingType.Text:
            {
                var text = (Str(value) ?? throw Bad("expected text.")).Trim();
                if (text.Length > 0) Check(text);
                return JsonValue.Create(text);
            }
            case SettingType.Number:
            {
                var number = Int(value) ?? (int.TryParse(Str(value), out var parsed) ? parsed : throw Bad("expected a whole number."));
                if (number < d.Min || number > d.Max) throw Bad($"must be between {d.Min} and {d.Max}.");
                return JsonValue.Create(number);
            }
            case SettingType.Boolean:
                return JsonValue.Create(Bool(value) ?? throw Bad("expected true or false."));
            default:
            {
                if (value is not JsonArray items) throw Bad("expected a list.");
                var list = new List<string>();
                foreach (var item in items)
                {
                    var s = (Str(item) ?? throw Bad("expected a list of text values.")).Trim();
                    if (s.Length == 0) continue;
                    Check(s);
                    if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
                }
                return Arr(list);
            }
        }
    }

    // ---- helpers

    private static string Stamp(IEnumerable<(string Key, DateTimeOffset UpdatedAt)> rows) =>
        string.Join("|", rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}@{r.UpdatedAt.UtcTicks}"));

    private static bool IsEmpty(JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrEmpty(s),
        _ => false,
    };

    private static SmtpOptions Smtp(MbrOptions o) => o.Notifications.Smtp ??= new SmtpOptions();
    private static JsonNode Val<T>(T value) => JsonValue.Create(value)!;
    private static JsonArray Arr(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static List<string> List(JsonNode? n) =>
        n is JsonArray a ? a.Select(Str).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() : [];

    private static string? HttpsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? null : $"'{s}' isn't an https:// URL.";
    private static string? Email(string s) =>
        System.Net.Mail.MailAddress.TryCreate(s, out _) ? null : $"'{s}' isn't an email address.";
    private static string? TenantId(string s) =>
        Guid.TryParse(s, out _) ? null : $"'{s}' isn't a tenant ID (a GUID).";
}

/// <summary>Picks up settings that another replica changed.</summary>
public sealed class RuntimeSettingsRefresher(RuntimeSettings settings, ILogger<RuntimeSettingsRefresher> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await settings.RefreshIfChangedAsync(stoppingToken); }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Couldn't refresh app settings"); }
        }
    }
}
