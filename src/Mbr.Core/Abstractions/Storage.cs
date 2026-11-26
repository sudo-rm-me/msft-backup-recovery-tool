using Mbr.Core.Model;

namespace Mbr.Core.Abstractions;

/// <summary>A flat key/value object store (local folder or S3 bucket).</summary>
public interface IBlobStore
{
    Task PutAsync(string key, Stream content, CancellationToken ct = default);
    /// <summary>Returns null when the key does not exist.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);
}

public interface IBlobStoreFactory
{
    IBlobStore Create(StorageTarget target);
}

/// <summary>Wraps (encrypts) data-encryption keys with a master key.</summary>
public interface IKeyWrapper
{
    string Id { get; }
    Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default);
    Task<byte[]> UnwrapAsync(byte[] wrapped, CancellationToken ct = default);
}

/// <summary>
/// Tenant-scoped, encrypted, content-addressed storage. Every byte written for a tenant goes through here.
/// </summary>
public interface ITenantContentStore
{
    /// <summary>Stores content (deduplicated by SHA-256 of the plaintext) and returns its hash.</summary>
    Task<string> PutObjectAsync(Tenant tenant, Guid storageTargetId, ReadOnlyMemory<byte> content, CancellationToken ct = default);
    Task<byte[]> GetObjectAsync(Tenant tenant, Guid storageTargetId, string hash, CancellationToken ct = default);
    /// <summary>Writes a named (non-deduplicated) document such as a snapshot manifest.</summary>
    Task PutDocumentAsync(Tenant tenant, Guid storageTargetId, string name, ReadOnlyMemory<byte> content, CancellationToken ct = default);
    Task<byte[]?> GetDocumentAsync(Tenant tenant, Guid storageTargetId, string name, CancellationToken ct = default);
    Task DeleteObjectAsync(Tenant tenant, Guid storageTargetId, string hash, CancellationToken ct = default);
    Task DeleteDocumentAsync(Tenant tenant, Guid storageTargetId, string name, CancellationToken ct = default);
}
