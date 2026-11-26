using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mbr.Core.Catalog;
using Mbr.Core.Json;
using Mbr.Core.Restore;

namespace Mbr.Catalog.PowerShell;

/// <summary>
/// Captures and restores types that only PowerShell modules expose (Exchange Online, Security &amp; Compliance, Teams).
/// Every type of one module is read in a single connected session per backup (connecting takes seconds); writes run one
/// cmdlet per action and only send parameters the cmdlet accepts.
/// </summary>
public sealed class PowerShellHandler(ResourceDefinition definition) : IResourceHandler, IResourceWriter
{
    public ResourceDefinition Definition { get; } = definition;
    private PowerShellDefinition Ps => Definition.PowerShell ?? new PowerShellDefinition();
    private string Noun => Ps.Noun ?? Definition.ListPath;
    private string IdentityProperty => Ps.IdentityProperty ?? Definition.IdProperty;

    /// <summary>Properties PowerShell adds to every object, or that change on their own.</summary>
    public static readonly string[] Noise =
    [
        "RunspaceId", "PSComputerName", "PSShowComputerName", "PSSourceJobInstanceId", "ObjectState", "IsValid", "WhenChanged", "WhenCreated",
        "WhenChangedUTC", "WhenCreatedUTC", "ExchangeVersion", "DistinguishedName", "OrganizationId", "OriginatingServer", "Id", "ExchangeObjectId",
        "ObjectCategory", "ObjectClass", "OrganizationalUnitRoot", "LastModifiedTime", "LastStatusUpdateTime", "ModificationTimeUtc",
        "CreationTimeUtc", "LastModifiedBy", "CreatedBy", "ModifiedBy", "DistributionStatus", "DistributionResults", "ObjectVersion",
        "XsAnyElements", "Element", "Key", "DataSource", "ImmutableId", "ValidationErrors",
    ];

    public async IAsyncEnumerable<CapturedItem> CaptureAsync(ITenantSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in await ReadAsync(session, ct)) yield return item;
    }

    public async Task<List<CapturedItem>> ListAsync(ITenantSession session, CancellationToken ct) => await ReadAsync(session, ct);

    public async Task<CapturedItem?> CaptureByIdAsync(ITenantSession session, string id, CancellationToken ct) =>
        (await ReadAsync(session, ct)).FirstOrDefault(i => i.ObjectId.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task<List<CapturedItem>> ReadAsync(ITenantSession session, CancellationToken ct)
    {
        var all = await PowerShellCapture.GetAsync(session, Definition.Api, ct);
        var node = all[Noun];
        if (node is JsonObject err && err["__error"] is { } message)
        {
            await session.ReportIssueAsync(new CaptureIssue(Definition.Type, null, IssueSeverity.Warning, $"Get-{Noun} failed: {message}"));
            return [];
        }
        var items = node switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => [o], _ => [] };
        var result = new List<CapturedItem>();
        foreach (var raw in items)
        {
            if (Definition.Skip.Any(r => raw[r.Property] is JsonValue v && string.Equals(v.ToString(), r.Value, StringComparison.OrdinalIgnoreCase))) continue;
            var content = Normalize(raw);
            var id = Definition.Singleton ? "singleton" : raw[Definition.IdProperty]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var name = Definition.Singleton ? Definition.DisplayName : raw[Definition.NameProperty]?.ToString() ?? id;
            result.Add(new CapturedItem(Definition.Type, id, name, content));
        }
        return result;
    }

    private JsonObject Normalize(JsonObject raw)
    {
        var o = (JsonObject)raw.DeepClone();
        foreach (var p in Noise) o.Remove(p);
        foreach (var p in Definition.Strip) JsonPath.Remove(o, p);
        // Server-assigned identifiers change when an object is recreated: they're the object ID, not content.
        // (Teams policies are identified by their name-like Identity, which is kept.)
        o.Remove("Guid");
        if (!string.Equals(IdentityProperty, "Identity", StringComparison.OrdinalIgnoreCase)) o.Remove("Identity");
        // Empty values are noise in diffs and can't be "set back" anyway.
        foreach (var (k, v) in o.ToList())
            if (v is null || v is JsonArray { Count: 0 } || (v is JsonValue s && s.TryGetValue<string>(out var str) && str.Length == 0)) o.Remove(k);
        return o;
    }

    // ------------------------------------------------------------------------------------------------ writes

    public string? CannotCreate(JsonObject content) =>
        Definition.Singleton ? "This is a tenant-wide setting; it can only be updated." :
        string.Equals(content[IdentityProperty]?.ToString(), "Global", StringComparison.OrdinalIgnoreCase) ? "The Global policy always exists; it can only be updated." :
        Ps.BuiltInProperty is { } p && content[p] is JsonValue v && v.TryGetValue<bool>(out var b) && b
            ? "Built-in object: it can't be recreated, only updated." : null;

    public async Task<string> CreateAsync(IRestoreContext ctx, JsonObject body, CancellationToken ct)
    {
        var identity = body[IdentityProperty]?.ToString();
        if (identity != null && Ps.CreateIdentityPrefix is { } prefix && identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            body[IdentityProperty] = identity[prefix.Length..];
        var result = await WriteAsync(ctx, "create", null, body, ct);
        return result?[Definition.IdProperty]?.ToString() ?? identity ?? body[Definition.NameProperty]?.ToString()
               ?? throw new InvalidOperationException($"New-{Noun} returned no {Definition.IdProperty}.");
    }

    public async Task UpdateAsync(IRestoreContext ctx, string targetId, JsonObject body, CancellationToken ct)
    {
        // Only send what differs from the live object: Set cmdlets reject some values they return (deprecated or
        // read-only-in-context parameters), and sending fewer parameters keeps the change minimal.
        var live = await CaptureByIdAsync(ctx.Session, targetId, ct);
        var changed = new JsonObject();
        foreach (var (k, v) in body)
            if (live is null || !JsonNode.DeepEquals(live.Content[k], v)) changed[k] = v?.DeepClone();
        if (changed.Count == 0) return;
        var identity = Definition.Singleton ? null : live?.Content[IdentityProperty]?.ToString() ?? targetId;
        await WriteAsync(ctx, "update", identity, changed, ct);
    }

    public async Task DeleteAsync(IRestoreContext ctx, string targetId, CancellationToken ct)
    {
        var live = await CaptureByIdAsync(ctx.Session, targetId, ct);
        if (live != null && CannotCreate(live.Content) is { } reason) throw new InvalidOperationException(reason.Replace("recreated", "removed"));
        await WriteAsync(ctx, "delete", live?.Content[IdentityProperty]?.ToString() ?? targetId, new JsonObject(), ct);
    }

    private async Task<JsonNode?> WriteAsync(IRestoreContext ctx, string mode, string? identity, JsonObject body, CancellationToken ct)
    {
        var send = new JsonObject();
        foreach (var (k, v) in body)
        {
            if (Noise.Contains(k) || Ps.Skip.Contains(k)) continue;
            send[Ps.Renames.GetValueOrDefault(k, k)] = v?.DeepClone();
        }
        var parameters = new Dictionary<string, object?>
        {
            ["Operation"] = "write", ["Api"] = Definition.Api, ["Noun"] = Noun, ["Mode"] = mode, ["Identity"] = identity,
            ["Singleton"] = Definition.Singleton, ["Body"] = send,
        };
        try
        {
            return await ctx.Session.PowerShell.RunAsync(PowerShellScripts.Connect(Definition.Api) + PowerShellScripts.Write, parameters, ct);
        }
        finally
        {
            PowerShellCapture.Invalidate(ctx.Session, Definition.Api);
        }
    }
}

/// <summary>Reads every type of one PowerShell module in one session and keeps the result for the rest of the job.</summary>
public static class PowerShellCapture
{
    private static readonly ConditionalWeakTable<ITenantSession, ConcurrentDictionary<string, Lazy<Task<JsonObject>>>> Cache = new();

    public static Task<JsonObject> GetAsync(ITenantSession session, string api, CancellationToken ct) =>
        Cache.GetOrCreateValue(session).GetOrAdd(api, a => new Lazy<Task<JsonObject>>(() => LoadAsync(session, a, ct))).Value;

    public static void Invalidate(ITenantSession session, string api)
    {
        if (Cache.TryGetValue(session, out var c)) c.TryRemove(api, out _);
    }

    private static async Task<JsonObject> LoadAsync(ITenantSession session, string api, CancellationToken ct)
    {
        var nouns = CatalogLoader.All.Where(d => d.Api == api)
            .Select(d => new JsonObject
            {
                ["Noun"] = d.PowerShell?.Noun ?? d.ListPath,
                ["Args"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(d.PowerShell?.GetArgs ?? new Dictionary<string, object>())),
            })
            .DistinctBy(n => n["Noun"]!.ToString()).ToList();
        var parameters = new Dictionary<string, object?>
        {
            ["Operation"] = "capture", ["Api"] = api, ["Nouns"] = new JsonArray(nouns.Select(n => (JsonNode)n).ToArray()),
            ["Exclude"] = new JsonArray(PowerShellHandler.Noise.Select(n => (JsonNode)n).ToArray()),
        };
        try
        {
            return await session.PowerShell.RunAsync(PowerShellScripts.Connect(api) + PowerShellScripts.Capture, parameters, ct) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not connectable (module missing, no certificate, role not assigned): every type of this module reports it.
            var failed = new JsonObject();
            foreach (var n in nouns) failed[n["Noun"]!.ToString()] = new JsonObject { ["__error"] = e.Message };
            return failed;
        }
    }
}

/// <summary>The PowerShell side. Runs inside <c>PowerShellRunner</c>'s wrapper, which provides $Params and Connect-MbrCertificate.</summary>
public static class PowerShellScripts
{
    public static string Connect(string api) => api switch
    {
        "exo" => """
            Import-Module ExchangeOnlineManagement -ErrorAction Stop
            $c = Connect-MbrCertificate
            Connect-ExchangeOnline -AppId $Params.AppId -Organization $Params.Organization @c -ShowBanner:$false | Out-Null

            """,
        "ipps" => """
            Import-Module ExchangeOnlineManagement -ErrorAction Stop
            $c = Connect-MbrCertificate
            Connect-IPPSSession -AppId $Params.AppId -Organization $Params.Organization @c -ShowBanner:$false | Out-Null

            """,
        "teams" => """
            Import-Module MicrosoftTeams -ErrorAction Stop
            if ($Params.CertificateThumbprint) {
                Connect-MicrosoftTeams -ApplicationId $Params.AppId -TenantId $Params.TenantId -CertificateThumbprint $Params.CertificateThumbprint | Out-Null
            } else {
                $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Params.CertificatePath, $Params.CertificatePassword)
                Connect-MicrosoftTeams -ApplicationId $Params.AppId -TenantId $Params.TenantId -Certificate $cert | Out-Null
            }

            """,
        _ => throw new NotSupportedException($"Unknown PowerShell API '{api}'."),
    };

    /// <summary>Get-{Noun} for every requested noun; a failing cmdlet records its error instead of stopping the rest.</summary>
    public const string Capture = """
        $out = [ordered]@{}
        foreach ($n in $Params.Nouns) {
            try {
                $a = if ($n.Args) { $n.Args } else { @{} }
                $items = @(& "Get-$($n.Noun)" @a -ErrorAction Stop | Select-Object -Property * -ExcludeProperty $Params.Exclude)
                # Round-trip through JSON so enums become names and nested objects become plain data.
                $out[$n.Noun] = if ($items.Count) { ConvertFrom-Json (ConvertTo-Json -InputObject $items -Depth 8 -EnumsAsStrings -Compress) } else { @() }
            } catch {
                $out[$n.Noun] = @{ __error = $_.Exception.Message }
            }
        }
        $out
        """;

    /// <summary>New-/Set-/Remove-{Noun} with only the parameters the cmdlet has.</summary>
    public const string Write = """
        $noun = $Params.Noun
        $cmd = switch ($Params.Mode) { 'create' { "New-$noun" } 'update' { "Set-$noun" } 'delete' { "Remove-$noun" } }
        $allowed = (Get-Command $cmd -ErrorAction Stop).Parameters.Keys
        $splat = @{}
        if ($Params.Mode -ne 'delete') {
            foreach ($k in $Params.Body.Keys) {
                if ($allowed -notcontains $k -or $k -eq 'Identity' -and $Params.Mode -ne 'create') { continue }
                $v = $Params.Body[$k]
                # Location-style values come back as objects; cmdlets take their names.
                if ($v -is [System.Collections.IDictionary] -and $v.Contains('Name')) { $v = $v.Name }
                elseif ($v -is [System.Collections.IList]) { $v = @($v | ForEach-Object { if ($_ -is [System.Collections.IDictionary] -and $_.Contains('Name')) { $_.Name } else { $_ } }) }
                $splat[$k] = $v
            }
            # Rules report State; their cmdlets take -Enabled.
            if ($Params.Body.State -in 'Enabled', 'Disabled' -and $allowed -contains 'Enabled') { $splat.Enabled = $Params.Body.State -eq 'Enabled' }
        }
        if ($Params.Identity -and $Params.Mode -ne 'create') { $splat.Identity = $Params.Identity }
        if ($allowed -contains 'Confirm') { $splat.Confirm = $false }
        $r = & $cmd @splat -ErrorAction Stop
        if ($Params.Mode -eq 'create' -and $r) { $r | Select-Object -First 1 -Property Guid, Identity, Name } else { @{ ok = $true } }
        """;
}
