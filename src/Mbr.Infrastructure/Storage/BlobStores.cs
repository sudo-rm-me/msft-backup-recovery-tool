using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Azure.Core;
using Azure.Identity;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Security;

namespace Mbr.Infrastructure.Storage;

public class LocalStorageSettings
{
    public string Path { get; set; } = "";
}

public class S3StorageSettings
{
    public string Bucket { get; set; } = "";
    public string? Region { get; set; }
    /// <summary>Custom endpoint for S3-compatible stores (MinIO, Wasabi, B2, R2).</summary>
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? Prefix { get; set; }
    /// <summary>
    /// "AzureFederation" (no keys: this app's Azure managed identity assumes RoleArn through an IAM OIDC provider),
    /// "Default" (AWS credential chain: env vars, profile, instance/container role), "Static" or "Profile".
    /// </summary>
    public string Credentials { get; set; } = "Default";
    public string? AccessKeyId { get; set; }
    public string? ProfileName { get; set; }
    /// <summary>AzureFederation: the IAM role to assume.</summary>
    public string? RoleArn { get; set; }
    /// <summary>
    /// AzureFederation: Application ID URI of the Entra app the managed identity requests its token for. The IAM OIDC
    /// provider's audience is that app's client ID.
    /// </summary>
    public string? WebIdentityAudience { get; set; }
    /// <summary>When set, every object is written with S3 Object Lock retention (bucket must have Object Lock enabled).</summary>
    public int? ObjectLockDays { get; set; }
    /// <summary>"GOVERNANCE" or "COMPLIANCE".</summary>
    public string ObjectLockMode { get; set; } = "GOVERNANCE";
    /// <summary>e.g. STANDARD, STANDARD_IA, INTELLIGENT_TIERING, GLACIER_IR.</summary>
    public string? StorageClass { get; set; }
}

public static class StorageSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static T Parse<T>(StorageTarget t) where T : new() => JsonSerializer.Deserialize<T>(t.SettingsJson, Json) ?? new T();
    public static string Serialize<T>(T settings) => JsonSerializer.Serialize(settings, Json);
}

public sealed class LocalBlobStore : IBlobStore
{
    private readonly string _root;
    public LocalBlobStore(string root)
    {
        _root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, key.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Key escapes the storage root.", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var f = File.Create(tmp)) await content.CopyToAsync(f, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        if (!Directory.Exists(_root)) yield break;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var key = System.IO.Path.GetRelativePath(_root, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (key.Contains(".tmp-")) continue;
            if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return key;
        }
    }
}

public sealed class S3BlobStore : IBlobStore
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageSettings _s;
    private readonly string _prefix;

    public S3BlobStore(S3StorageSettings settings, string? secretKey)
    {
        _s = settings;
        _prefix = string.IsNullOrWhiteSpace(settings.Prefix) ? "" : settings.Prefix.TrimEnd('/') + "/";
        var config = new AmazonS3Config { ForcePathStyle = settings.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(settings.ServiceUrl)) config.ServiceURL = settings.ServiceUrl;
        if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            if (string.IsNullOrWhiteSpace(settings.ServiceUrl)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
            else config.AuthenticationRegion = settings.Region;
        }

        AWSCredentials? creds = settings.Credentials switch
        {
            "Static" => new BasicAWSCredentials(
                settings.AccessKeyId ?? throw new InvalidOperationException("AccessKeyId is required for Static S3 credentials."),
                secretKey ?? throw new InvalidOperationException("A secret key is required for Static S3 credentials.")),
            "Profile" => new CredentialProfileStoreChain().TryGetAWSCredentials(settings.ProfileName ?? "default", out var c)
                ? c : throw new InvalidOperationException($"AWS profile '{settings.ProfileName}' not found."),
            "AzureFederation" => new AzureFederatedAwsCredentials(
                string.IsNullOrWhiteSpace(settings.RoleArn)
                    ? throw new InvalidOperationException("The IAM role ARN isn't set yet (Terraform variable aws_role_arn; see docs/setup.md, \"S3 access\").")
                    : settings.RoleArn,
                string.IsNullOrWhiteSpace(settings.WebIdentityAudience)
                    ? throw new InvalidOperationException("WebIdentityAudience is required for AzureFederation S3 credentials.")
                    : settings.WebIdentityAudience,
                settings.Region),
            _ => null,
        };
        _s3 = creds is null ? new AmazonS3Client(config) : new AmazonS3Client(creds, config);
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        var req = new PutObjectRequest { BucketName = _s.Bucket, Key = _prefix + key, InputStream = content, AutoCloseStream = false };
        if (!string.IsNullOrWhiteSpace(_s.StorageClass)) req.StorageClass = new S3StorageClass(_s.StorageClass);
        if (_s.ObjectLockDays is > 0)
        {
            req.ObjectLockMode = new ObjectLockMode(_s.ObjectLockMode.ToUpperInvariant());
            req.ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(_s.ObjectLockDays.Value);
        }
        await _s3.PutObjectAsync(req, ct);
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _s3.GetObjectAsync(_s.Bucket, _prefix + key, ct);
            var ms = new MemoryStream();
            await resp.ResponseStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_s.Bucket, _prefix + key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) => _s3.DeleteObjectAsync(_s.Bucket, _prefix + key, ct);

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? token = null;
        do
        {
            var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _s.Bucket, Prefix = _prefix + prefix, ContinuationToken = token,
            }, ct);
            foreach (var o in resp.S3Objects ?? []) yield return o.Key[_prefix.Length..];
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token != null);
    }
}

/// <summary>
/// Temporary AWS credentials for an IAM role, obtained with AssumeRoleWithWebIdentity using a Microsoft Entra token for this
/// app's managed identity (DefaultAzureCredential). No AWS keys exist anywhere; credentials refresh before they expire.
/// </summary>
public sealed class AzureFederatedAwsCredentials(string roleArn, string audience, string? region, TokenCredential? azure = null) : RefreshingAWSCredentials
{
    private readonly TokenCredential _azure = azure ?? new DefaultAzureCredential();

    /// <summary>Token scope for an Application ID URI such as api://mbr-aws-federation.</summary>
    public static string Scope(string audience) => audience.TrimEnd('/') + "/.default";

    protected override CredentialsRefreshState GenerateNewCredentials() => GenerateNewCredentialsAsync().GetAwaiter().GetResult();

    protected override async Task<CredentialsRefreshState> GenerateNewCredentialsAsync()
    {
        var token = await _azure.GetTokenAsync(new TokenRequestContext([Scope(audience)]), default);
        using var sts = new AmazonSecurityTokenServiceClient(new AnonymousAWSCredentials(),
            new AmazonSecurityTokenServiceConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(region) ? "us-east-1" : region) });
        var resp = await sts.AssumeRoleWithWebIdentityAsync(new AssumeRoleWithWebIdentityRequest
        {
            RoleArn = roleArn, WebIdentityToken = token.Token, RoleSessionName = SessionName(), DurationSeconds = 3600,
        });
        var c = resp.Credentials;
        return new CredentialsRefreshState(new ImmutableCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken),
            (c.Expiration ?? DateTime.UtcNow.AddMinutes(55)).ToUniversalTime());
    }

    /// <summary>Shows up in CloudTrail: "mbr-" plus the host (container replica) name, within AWS's limits.</summary>
    public static string SessionName()
    {
        var host = new string(Environment.MachineName.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '_').ToArray());
        var name = "mbr-" + host;
        return name.Length > 64 ? name[..64] : name;
    }
}

public sealed class BlobStoreFactory(SecretProtector secrets, Microsoft.Extensions.Options.IOptions<MbrOptions> options) : IBlobStoreFactory
{
    public IBlobStore Create(StorageTarget target)
    {
        string? secret = target.EncryptedSecret is null ? null : secrets.UnprotectAsync(target.EncryptedSecret).GetAwaiter().GetResult();
        return target.Kind switch
        {
            StorageKind.Local => new LocalBlobStore(ResolveLocal(StorageSettings.Parse<LocalStorageSettings>(target).Path)),
            StorageKind.S3 => new S3BlobStore(StorageSettings.Parse<S3StorageSettings>(target), secret),
            _ => throw new NotSupportedException(target.Kind.ToString()),
        };
    }

    private string ResolveLocal(string path) =>
        string.IsNullOrWhiteSpace(path) ? System.IO.Path.Combine(options.Value.DataDirectory, "storage")
        : System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(options.Value.DataDirectory, path);
}
