using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mbr.Core.Abstractions;
using Mbr.Core.Catalog;
using Mbr.Core.Model;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Backup;

public sealed class TenantSession(
    Tenant tenant, IApiClientFactory apis, AppRole role, IPowerShellRunner powerShell, Func<CaptureIssue, Task> onIssue) : ITenantSession
{
    private IApiClient? _graph, _arm;
    public Tenant Tenant => tenant;
    public AppRole Role => role;
    public string DirectoryId => tenant.DirectoryId;
    public IApiClient Graph => _graph ??= apis.Create(tenant.DirectoryId, ApiKind.Graph, role);
    public IApiClient Arm => _arm ??= apis.Create(tenant.DirectoryId, ApiKind.Arm, role);
    public IPowerShellRunner PowerShell => powerShell;
    public Task ReportIssueAsync(CaptureIssue issue) => onIssue(issue);
}

/// <summary>Creates tenant sessions with the right API clients and PowerShell credentials.</summary>
public sealed class TenantSessionFactory(IApiClientFactory apis, IPowerShellRunnerFactory powerShell)
{
    public TenantSession Create(Tenant tenant, AppRole role, Func<CaptureIssue, Task>? onIssue = null) =>
        new(tenant, apis, role, powerShell.Create(tenant, role), onIssue ?? (_ => Task.CompletedTask));
}

/// <summary>Creates the PowerShell runner for a tenant (replaced by an in-memory one in tests and demo mode).</summary>
public interface IPowerShellRunnerFactory
{
    IPowerShellRunner Create(Tenant tenant, AppRole role);
}

public sealed class PowerShellRunnerFactory(IOptions<MbrOptions> options) : IPowerShellRunnerFactory
{
    public IPowerShellRunner Create(Tenant tenant, AppRole role) => new PowerShellRunner(options.Value, tenant, role);
}

/// <summary>
/// Runs PowerShell 7 scripts out of process (script on stdin, JSON on stdout). Exchange Online, Teams and
/// Security &amp; Compliance cmdlets run in a child pwsh so their assemblies can't clash with this process.
/// The script receives $Params (a hashtable) including AppId, CertificatePath, CertificatePassword,
/// CertificateThumbprint, Organization and TenantId, and must write a single JSON document as its last output.
/// </summary>
public sealed class PowerShellRunner(MbrOptions options, Tenant tenant, AppRole role) : IPowerShellRunner
{
    public async Task<JsonNode?> RunAsync(string script, IDictionary<string, object?> parameters, CancellationToken ct)
    {
        var app = role == AppRole.Backup ? options.Apps.Backup : options.Apps.Restore;
        if (string.IsNullOrEmpty(app.ClientId) || (string.IsNullOrEmpty(app.CertificatePath) && string.IsNullOrEmpty(app.CertificateThumbprint)))
            throw new InvalidOperationException("PowerShell workloads need the app configured with a certificate (CertificatePath or CertificateThumbprint).");

        var all = new Dictionary<string, object?>(parameters)
        {
            ["AppId"] = app.ClientId,
            ["CertificatePath"] = app.CertificatePath,
            ["CertificatePassword"] = app.CertificatePassword,
            ["CertificateThumbprint"] = app.CertificateThumbprint,
            ["Organization"] = tenant.DefaultDomain,
            ["TenantId"] = tenant.DirectoryId,
        };
        // Parameters travel as base64 JSON so nothing in them is ever interpreted as PowerShell code.
        var paramB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all)));
        var wrapper = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $Params = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{paramB64}}')) | ConvertFrom-Json -AsHashtable
            function Connect-MbrCertificate {
                if ($Params.CertificateThumbprint) { return @{ CertificateThumbprint = $Params.CertificateThumbprint } }
                $pwd = if ($Params.CertificatePassword) { ConvertTo-SecureString $Params.CertificatePassword -AsPlainText -Force } else { $null }
                return @{ CertificateFilePath = $Params.CertificatePath; CertificatePassword = $pwd }
            }
            $result = & {
            {{script}}
            }
            $result | ConvertTo-Json -Depth 20 -Compress
            """;

        var psi = new ProcessStartInfo(options.PowerShell.Executable, "-NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pwsh.");
        await proc.StandardInput.WriteAsync(wrapper);
        proc.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.PowerShell.TimeoutMinutes));
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell failed ({proc.ExitCode}): {Trim(stderr)}");
        var lastLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(lastLine) ? null : JsonNode.Parse(lastLine);
    }

    private static string Trim(string s) => s.Length > 2000 ? s[..2000] : s;
}
