using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Mbr.Infrastructure.Hosting;

/// <summary>Running in Azure Container Apps: where state must live outside the (non-persistent) container.</summary>
public static class AzureHosting
{
    /// <summary>True inside Azure Container Apps (the platform sets CONTAINER_APP_NAME).</summary>
    public static bool InContainerApps => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTAINER_APP_NAME"));

    /// <summary>
    /// Container storage is lost on every restart and new revision, so the database, master key and backups must be elsewhere.
    /// Refuses to start rather than quietly writing them to the container.
    /// </summary>
    public static void Validate(MbrOptions o)
    {
        if (!InContainerApps) return;
        var problems = new List<string>();
        if (!o.Database.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            problems.Add("Mbr:Database:Provider must be Postgres (SQLite would live in the container).");
        if (!o.Encryption.KeyWrapper.Equals("KeyVault", StringComparison.OrdinalIgnoreCase))
            problems.Add("Mbr:Encryption:KeyWrapper must be KeyVault (a local master key would be lost, and every backup with it).");
        if (string.IsNullOrWhiteSpace(o.Storage.DefaultS3.Bucket))
            problems.Add("Mbr:Storage:DefaultS3:Bucket must be set (the default storage target would be the container's disk).");
        if (problems.Count > 0)
            throw new InvalidOperationException("This configuration isn't safe in Azure Container Apps:\n- " + string.Join("\n- ", problems) +
                                                "\nDeploy with the Terraform in deploy/terraform, which sets these.");
    }

    /// <summary>Downloads Key Vault certificates (Mbr:Apps:*:KeyVaultCertificate) into private PFX files and points CertificatePath at them.</summary>
    public static void MaterializeCertificates(MbrOptions o, TokenCredential? credential = null)
    {
        foreach (var (role, app) in new[] { ("backup", o.Apps.Backup), ("restore", o.Apps.Restore) })
        {
            if (string.IsNullOrWhiteSpace(app.KeyVaultCertificate)) continue;
            var (vault, name, version) = ParseSecretUri(app.KeyVaultCertificate);
            KeyVaultSecret secret;
            try
            {
                secret = new SecretClient(vault, credential ?? new DefaultAzureCredential()).GetSecret(name, version).Value;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Couldn't read the {role} app's certificate from Key Vault ({app.KeyVaultCertificate}): {e.Message}", e);
            }
            var dir = Path.Combine(Path.GetTempPath(), "mbr-certs");
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(dir, $"{role}.pfx");
            File.WriteAllBytes(path, Convert.FromBase64String(secret.Value)); // a Key Vault certificate's secret is the base64 PFX
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            app.CertificatePath = path;
            app.CertificatePassword = null;
            app.CertificateThumbprint = null;
        }
    }

    /// <summary>https://vault.vault.azure.net/secrets/name[/version] → (vault URI, name, version or null).</summary>
    public static (Uri Vault, string Name, string? Version) ParseSecretUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"'{value}' isn't a Key Vault secret URI (https://<vault>.vault.azure.net/secrets/<name>).");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is < 2 or > 3 || !parts[0].Equals("secrets", StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0)
            throw new ArgumentException($"'{value}' isn't a Key Vault secret URI (https://<vault>.vault.azure.net/secrets/<name>). " +
                                        "Use the certificate's secret URI, not its /certificates/ URI.");
        return (new Uri($"{uri.Scheme}://{uri.Authority}"), parts[1], parts.Length == 3 ? parts[2] : null);
    }
}
