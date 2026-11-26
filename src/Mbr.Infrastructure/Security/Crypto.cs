using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Security;

/// <summary>AES-256-GCM authenticated encryption. Format: "MBR1" | nonce(12) | tag(16) | ciphertext.</summary>
public static class Aead
{
    private static readonly byte[] Magic = "MBR1"u8.ToArray();

    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[4 + 12 + 16 + plaintext.Length];
        Magic.CopyTo(output, 0);
        var nonce = output.AsSpan(4, 12);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(32), output.AsSpan(16, 16));
        return output;
    }

    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length < 32 || !data[..4].SequenceEqual(Magic))
            throw new CryptographicException("Not an MBR1 encrypted payload.");
        var plaintext = new byte[data.Length - 32];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(data.Slice(4, 12), data[32..], data.Slice(16, 16), plaintext);
        return plaintext;
    }
}

/// <summary>Wraps keys with a local 256-bit master key (config value or auto-created key file).</summary>
public sealed class LocalKeyWrapper : IKeyWrapper
{
    private readonly byte[] _masterKey;
    public string Id => "local";

    public LocalKeyWrapper(IOptions<MbrOptions> options)
    {
        var o = options.Value;
        if (!string.IsNullOrWhiteSpace(o.Encryption.MasterKey))
        {
            _masterKey = Convert.FromBase64String(o.Encryption.MasterKey);
        }
        else
        {
            Directory.CreateDirectory(o.DataDirectory);
            var path = Path.Combine(o.DataDirectory, "master.key");
            if (!File.Exists(path))
                File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            _masterKey = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        if (_masterKey.Length != 32) throw new InvalidOperationException("Master key must be 32 bytes (base64).");
    }

    public Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default) => Task.FromResult(Aead.Encrypt(_masterKey, key));
    public Task<byte[]> UnwrapAsync(byte[] wrapped, CancellationToken ct = default) => Task.FromResult(Aead.Decrypt(_masterKey, wrapped));
}

/// <summary>Wraps keys with an RSA key in Azure Key Vault (the master key never leaves the vault).</summary>
public sealed class KeyVaultKeyWrapper : IKeyWrapper
{
    private readonly CryptographyClient _client;
    public string Id { get; }

    public KeyVaultKeyWrapper(IOptions<MbrOptions> options)
    {
        var keyId = options.Value.Encryption.KeyVaultKeyId
            ?? throw new InvalidOperationException("Mbr:Encryption:KeyVaultKeyId is required for the KeyVault key wrapper.");
        Id = "keyvault:" + keyId;
        _client = new CryptographyClient(new Uri(keyId), new DefaultAzureCredential());
    }

    public async Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default) =>
        (await _client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, key, ct)).EncryptedKey;

    public async Task<byte[]> UnwrapAsync(byte[] wrapped, CancellationToken ct = default) =>
        (await _client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrapped, ct)).Key;
}

/// <summary>Creates and caches per-tenant data-encryption keys (envelope encryption).</summary>
public sealed class TenantKeyService(IKeyWrapper wrapper, IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<Guid, byte[]> _cache = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public async Task<byte[]> GetKeyAsync(Tenant tenant, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenant.Id, out var cached)) return cached;
        await _createLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(tenant.Id, out cached)) return cached;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
            var row = await db.Tenants.FirstAsync(t => t.Id == tenant.Id, ct);
            byte[] key;
            if (row.WrappedDataKey is null)
            {
                key = RandomNumberGenerator.GetBytes(32);
                row.WrappedDataKey = Convert.ToBase64String(await wrapper.WrapAsync(key, ct));
                row.DataKeyWrapperId = wrapper.Id;
                await db.SaveChangesAsync(ct);
                tenant.WrappedDataKey = row.WrappedDataKey;
                tenant.DataKeyWrapperId = row.DataKeyWrapperId;
            }
            else
            {
                key = await wrapper.UnwrapAsync(Convert.FromBase64String(row.WrappedDataKey), ct);
            }
            _cache[tenant.Id] = key;
            return key;
        }
        finally { _createLock.Release(); }
    }
}

/// <summary>Encrypts small secrets (e.g. S3 secret keys) for storage in the database.</summary>
public sealed class SecretProtector(IKeyWrapper wrapper)
{
    public async Task<string> ProtectAsync(string secret, CancellationToken ct = default) =>
        Convert.ToBase64String(await wrapper.WrapAsync(System.Text.Encoding.UTF8.GetBytes(secret), ct));

    public async Task<string> UnprotectAsync(string protectedSecret, CancellationToken ct = default) =>
        System.Text.Encoding.UTF8.GetString(await wrapper.UnwrapAsync(Convert.FromBase64String(protectedSecret), ct));
}
