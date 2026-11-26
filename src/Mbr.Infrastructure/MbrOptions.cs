namespace Mbr.Infrastructure;

public class MbrOptions
{
    public const string Section = "Mbr";

    public DatabaseOptions Database { get; set; } = new();
    public EncryptionOptions Encryption { get; set; } = new();
    public AppsOptions Apps { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public PowerShellOptions PowerShell { get; set; } = new();
    public NotificationOptions Notifications { get; set; } = new();
    public PartnerOptions Partner { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();

    /// <summary>Public URL of this deployment (used for consent redirect URIs and links in alerts).</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5001";
    /// <summary>
    /// Folder for local state in development (SQLite DB, local master key, default local storage); scratch space in Azure.
    /// Defaults to %LOCALAPPDATA%\Mbr (Windows) or ~/.local/share/Mbr.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    public static string ResolveDataDirectory(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Mbr")
            : Path.GetFullPath(configured);
}

public class DatabaseOptions
{
    /// <summary>"Sqlite" or "Postgres".</summary>
    public string Provider { get; set; } = "Sqlite";
    public string? ConnectionString { get; set; }
    /// <summary>
    /// Postgres only: sign in with a Microsoft Entra token for the managed identity (DefaultAzureCredential) instead of a
    /// password. The connection string's Username is the identity's name.
    /// </summary>
    public bool EntraAuth { get; set; }
}

public class StorageOptions
{
    /// <summary>
    /// When DefaultS3.Bucket is set, the storage target created on first run is this S3 bucket instead of local disk
    /// (in Azure: credentials AzureFederation, so the app's managed identity assumes an IAM role with no AWS keys).
    /// </summary>
    public Storage.S3StorageSettings DefaultS3 { get; set; } = new();
}

public class EncryptionOptions
{
    /// <summary>"Local" (master key file / config value) or "KeyVault".</summary>
    public string KeyWrapper { get; set; } = "Local";
    /// <summary>Base64 256-bit master key. If empty, a key file in the data directory is created on first run.</summary>
    public string? MasterKey { get; set; }
    /// <summary>Full Key Vault key identifier, e.g. https://myvault.vault.azure.net/keys/mbr-master.</summary>
    public string? KeyVaultKeyId { get; set; }
}

public class AppsOptions
{
    public AppCredentialOptions Backup { get; set; } = new();
    public AppCredentialOptions Restore { get; set; } = new();
}

public class AppCredentialOptions
{
    public string? ClientId { get; set; }
    /// <summary>Path to a PFX file.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    /// <summary>Thumbprint of a certificate in CurrentUser\My or LocalMachine\My.</summary>
    public string? CertificateThumbprint { get; set; }
    /// <summary>
    /// Key Vault certificate, as its secret URI (https://vault.vault.azure.net/secrets/name, optionally with a version).
    /// Downloaded at startup with DefaultAzureCredential into a private temporary PFX, which then serves as CertificatePath.
    /// </summary>
    public string? KeyVaultCertificate { get; set; }
    /// <summary>Client secret. Supported for development only; use certificates in production.</summary>
    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) &&
        (!string.IsNullOrWhiteSpace(CertificatePath) || !string.IsNullOrWhiteSpace(CertificateThumbprint) ||
         !string.IsNullOrWhiteSpace(KeyVaultCertificate) ||
         !string.IsNullOrWhiteSpace(ClientSecret));
}

public class WorkerOptions
{
    public int MaxConcurrency { get; set; } = 4;
    public int PollSeconds { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    /// <summary>
    /// Maximum jobs running at once for one customer tenant across all workers, so adding workers speeds up many tenants
    /// without piling onto one tenant's throttling budget. Restore jobs are exempt (they're interactive). 0 = no limit.
    /// </summary>
    public int MaxJobsPerTenant { get; set; } = 2;
    /// <summary>Maximum concurrent API requests per customer tenant (throttling protection).</summary>
    public int MaxRequestsPerTenant { get; set; } = 8;
}

public class PowerShellOptions
{
    /// <summary>Path to pwsh (PowerShell 7+).</summary>
    public string Executable { get; set; } = "pwsh";
    public int TimeoutMinutes { get; set; } = 30;
}

public class NotificationOptions
{
    /// <summary>Webhook URLs (Teams/Slack/generic) that receive drift and job-failure alerts.</summary>
    public List<string> Webhooks { get; set; } = new();
    public SmtpOptions? Smtp { get; set; }
}

public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public List<string> To { get; set; } = new();
}

public class PartnerOptions
{
    /// <summary>Your own (partner) tenant ID, where GDAP relationships with customers are managed. Enables "Import from GDAP".</summary>
    public string? TenantId { get; set; }
}
