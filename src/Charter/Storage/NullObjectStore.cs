namespace Charter.Storage;

/// <summary>
/// The store on an instance that configured none - <c>CHARTER_STORAGE_BACKEND=none</c>, the default.
/// </summary>
/// <remarks>
/// <para>
/// Registered rather than absent, so every consumer has one dependency and one branch:
/// <see cref="Enabled"/>. The alternative - a nullable <c>IObjectStore?</c> that half the call sites
/// remember to check - is how a subsystem ends up silently not running on the configuration most
/// instances use.
/// </para>
/// <para>
/// Writing returns <c>null</c> rather than throwing. The producers are all offloads: bytes that have
/// somewhere else to live, in a truncated Postgres row. A store that threw here would turn "this
/// operator has no bucket" into a failed session (section 2.3).
/// </para>
/// </remarks>
public sealed class NullObjectStore : IObjectStore
{
    /// <summary>The single instance. It holds nothing, so there is no reason to have two.</summary>
    public static NullObjectStore Instance { get; } = new();

    /// <inheritdoc />
    public StorageBackend Backend => StorageBackend.None;

    /// <inheritdoc />
    public bool Enabled => false;

    /// <inheritdoc />
    public long MaxObjectBytes => StorageOptions.MaxObjectBytes;

    /// <inheritdoc />
    public Task<StoredObject?> PutAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken = default)
        => Task.FromResult<StoredObject?>(null);

    /// <inheritdoc />
    public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<ObjectContent?>(null);

    /// <inheritdoc />
    public Task<int> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
