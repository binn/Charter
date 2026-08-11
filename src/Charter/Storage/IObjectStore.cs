namespace Charter.Storage;

/// <summary>What a successful write recorded.</summary>
/// <param name="Key">The key the object is under - what a <c>file_ref</c> carries.</param>
/// <param name="SizeBytes">How many bytes were stored.</param>
/// <param name="Sha256">Lowercase hex digest of the stored bytes.</param>
public sealed record StoredObject(string Key, long SizeBytes, string Sha256);

/// <summary>One object, read back.</summary>
/// <param name="Key">The key it was read from.</param>
/// <param name="Bytes">The content.</param>
/// <param name="ContentType">What it was stored as.</param>
public sealed record ObjectContent(string Key, byte[] Bytes, string ContentType);

/// <summary>
/// Bytes Charter keeps outside Postgres (sections 2.3, 4.2, 27.1).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. Charter needs to put an object somewhere, read it back through its own
/// authorised endpoint, and drop everything belonging to a session - and nothing else. There is no
/// signed-URL method on this interface on purpose: a URL that authorises by possession is a URL that
/// bypasses section 7.4 the moment it is pasted into a chat, and an engineer-only artifact reachable
/// by link is not engineer-only. Reads go through <c>MapCharterStorageBlobs</c>, which asks the same
/// question the transcript endpoint asks.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> Every write is bounded by <see cref="MaxObjectBytes"/> and every
/// object is keyed under the session that produced it, so <see cref="DeleteSessionAsync"/> is the
/// unit of deletion (sections 20, 27.1). Charter does not run a sweeper of its own: an operator's
/// retention policy belongs to the operator, and the honest version of that is a documented bucket
/// lifecycle rule or a cron over the directory rather than a hidden background job that deletes
/// somebody's evidence. <c>docs/privacy.md</c> says so in those words.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>Which backend this is.</summary>
    StorageBackend Backend { get; }

    /// <summary>False when nothing is configured, in which case every call is a no-op.</summary>
    /// <remarks>
    /// A property rather than a null store reference, so callers branch on a fact instead of on a
    /// missing registration. The disabled store is always registered and always resolvable.
    /// </remarks>
    bool Enabled { get; }

    /// <summary>The most one object may carry, <see cref="StorageOptions.MaxObjectBytes"/>.</summary>
    long MaxObjectBytes { get; }

    /// <summary>
    /// Stores one object, overwriting any object already at that key.
    /// </summary>
    /// <returns><c>null</c> when storage is disabled, so the caller keeps the bytes where they are.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bytes"/> is longer than <see cref="MaxObjectBytes"/>. Producers truncate before
    /// they call; exceeding the cap is a bug in the producer, not an operator's problem.
    /// </exception>
    Task<StoredObject?> PutAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one object back. <c>null</c> when it is not there, or storage is disabled.</summary>
    Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes everything stored under one session. Returns how many objects went.</summary>
    Task<int> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>A backend refused, and the caller cannot fix it.</summary>
public sealed class ObjectStoreException : Exception
{
    /// <inheritdoc />
    public ObjectStoreException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public ObjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public ObjectStoreException()
    {
    }
}
