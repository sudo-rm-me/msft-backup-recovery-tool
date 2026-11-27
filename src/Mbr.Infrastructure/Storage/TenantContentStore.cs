using System.Collections.Concurrent;
using Mbr.Core.Abstractions;
using Mbr.Core.Model;
using Mbr.Core.Security;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure.Storage;

/// <summary>Resolves (and caches) blob stores for storage targets.</summary>
public sealed class StorageTargetResolver(IBlobStoreFactory factory, IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<string, IBlobStore> _cache = new();

    public async Task<(StorageTarget Target, IBlobStore Store)> ResolveAsync(Guid storageTargetId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var target = await db.StorageTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == storageTargetId, ct)
            ?? throw new InvalidOperationException($"Storage target {storageTargetId} not found.");
        return (target, Get(target));
    }

    public IBlobStore Get(StorageTarget target)
    {
        var cacheKey = $"{target.Id}:{Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(target.SettingsJson + target.EncryptedSecret))}";
        return _cache.GetOrAdd(cacheKey, _ => factory.Create(target));
    }

    /// <summary>The tenant's default target, else the global default target.</summary>
    public async Task<Guid> GetTargetIdForTenantAsync(Tenant tenant, Guid? requested, CancellationToken ct = default)
    {
        if (requested is { } r) return r;
        if (tenant.DefaultStorageTargetId is { } t) return t;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        var def = await db.StorageTargets.AsNoTracking().OrderByDescending(x => x.IsDefault).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No storage target is configured. Add one under Settings → Storage.");
        return def.Id;
    }
}

/// <summary>
/// Layout inside a storage target:
///   tenants/{tenantId}/objects/{hh}/{sha256}   encrypted content-addressed objects
///   tenants/{tenantId}/docs/{name}             encrypted named documents (manifests, indexes)
///   tenants/{tenantId}/key.json                the tenant's data key, wrapped by the master key (see KeyEscrowKey)
/// </summary>
public sealed class TenantContentStore(StorageTargetResolver resolver, TenantKeyService keys, IServiceScopeFactory scopes) : ITenantContentStore
{
    private static string ObjectKey(Tenant t, string hash) => $"tenants/{t.Id}/objects/{hash[..2]}/{hash}";
    private static string DocKey(Tenant t, string name) => $"tenants/{t.Id}/docs/{name.TrimStart('/')}";

    /// <summary>
    /// A copy of the tenant's wrapped data key next to its backups, so they stay decryptable with the master key even if the
    /// metadata database is lost. Useless without the master key (it's wrapped by it), so it's stored as plain JSON.
    /// </summary>
    public static string KeyEscrowKey(Guid tenantId) => $"tenants/{tenantId}/key.json";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, Guid Target), bool> Escrowed = new();

    private async Task<byte[]> KeyForWriteAsync(Tenant tenant, Guid storageTargetId, IBlobStore store, CancellationToken ct)
    {
        var key = await keys.GetKeyAsync(tenant, ct);
        if (Escrowed.ContainsKey((tenant.Id, storageTargetId))) return key;
        var path = KeyEscrowKey(tenant.Id);
        if (!await store.ExistsAsync(path, ct))
        {
            using var scope = scopes.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<MbrDbContext>().Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant.Id, ct);
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                tenantId = row.Id, directoryId = row.DirectoryId, displayName = row.DisplayName,
                wrappedDataKey = row.WrappedDataKey, wrapperId = row.DataKeyWrapperId, writtenAt = DateTimeOffset.UtcNow,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await store.PutAsync(path, new MemoryStream(json), ct);
        }
        Escrowed[(tenant.Id, storageTargetId)] = true;
        return key;
    }

    public async Task<string> PutObjectAsync(Tenant tenant, Guid storageTargetId, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var hash = Hashing.Sha256Hex(content.Span);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        if (await db.StoredObjects.AnyAsync(o => o.TenantId == tenant.Id && o.StorageTargetId == storageTargetId && o.Hash == hash, ct))
            return hash;

        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        var encrypted = Aead.Encrypt(await KeyForWriteAsync(tenant, storageTargetId, store, ct), content.Span);
        await store.PutAsync(ObjectKey(tenant, hash), new MemoryStream(encrypted), ct);

        db.StoredObjects.Add(new StoredObject
        {
            TenantId = tenant.Id, StorageTargetId = storageTargetId, Hash = hash,
            Size = content.Length, StoredSize = encrypted.Length,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* stored concurrently by another worker: fine */ }
        return hash;
    }

    public async Task<byte[]> GetObjectAsync(Tenant tenant, Guid storageTargetId, string hash, CancellationToken ct = default)
    {
        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        await using var s = await store.GetAsync(ObjectKey(tenant, hash), ct)
            ?? throw new FileNotFoundException($"Object {hash} is missing from storage target {storageTargetId}.");
        var data = await ReadAllAsync(s, ct);
        var plain = Aead.Decrypt(await keys.GetKeyAsync(tenant, ct), data);
        if (Hashing.Sha256Hex(plain) != hash) throw new InvalidDataException($"Object {hash} failed integrity check.");
        return plain;
    }

    public async Task PutDocumentAsync(Tenant tenant, Guid storageTargetId, string name, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        var encrypted = Aead.Encrypt(await KeyForWriteAsync(tenant, storageTargetId, store, ct), content.Span);
        await store.PutAsync(DocKey(tenant, name), new MemoryStream(encrypted), ct);
    }

    public async Task<byte[]?> GetDocumentAsync(Tenant tenant, Guid storageTargetId, string name, CancellationToken ct = default)
    {
        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        await using var s = await store.GetAsync(DocKey(tenant, name), ct);
        if (s is null) return null;
        return Aead.Decrypt(await keys.GetKeyAsync(tenant, ct), await ReadAllAsync(s, ct));
    }

    public async Task DeleteObjectAsync(Tenant tenant, Guid storageTargetId, string hash, CancellationToken ct = default)
    {
        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        await store.DeleteAsync(ObjectKey(tenant, hash), ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        await db.StoredObjects.Where(o => o.TenantId == tenant.Id && o.StorageTargetId == storageTargetId && o.Hash == hash)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteDocumentAsync(Tenant tenant, Guid storageTargetId, string name, CancellationToken ct = default)
    {
        var (_, store) = await resolver.ResolveAsync(storageTargetId, ct);
        await store.DeleteAsync(DocKey(tenant, name), ct);
    }

    private static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
