using Charter.Configuration;

namespace Charter.Storage;

/// <summary>
/// Where Charter puts bytes that are too big, too binary, or too long-lived for a Postgres row
/// (sections 2.3, 4.2, 27.1).
/// </summary>
public enum StorageBackend
{
    /// <summary>
    /// No object store. Everything stays in Postgres, truncated where it has to be.
    /// </summary>
    /// <remarks>
    /// The default, and the only correct answer on a platform with an ephemeral filesystem
    /// (section 2.3). It is not a degraded mode: a web project's verification artifact is a preview
    /// URL, and a transcript event that fits in a row belongs in one.
    /// </remarks>
    None,

    /// <summary>A directory on durable local disk.</summary>
    /// <remarks>
    /// For the self-hoster on a VPS or a home server with a real volume. Section 2.3's "never the
    /// container filesystem" is a PaaS constraint, not a universal one - it exists because a
    /// container on Railway or Fly loses its disk on every deploy, which is a fact about those
    /// platforms rather than about storage. An operator who has a durable volume and has said so
    /// explicitly is not covered by that rule.
    /// </remarks>
    Filesystem,

    /// <summary>An S3-compatible bucket: AWS S3, Cloudflare R2, MinIO, Backblaze B2, Wasabi.</summary>
    S3,
}

/// <summary>
/// The object-storage block of section 4.2, resolved into one selected backend.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The backend is named, never inferred.</strong> Deducing it from which variables happen to
/// be set reads well until an operator types <c>CHARTER_STORAGE_BUCKETT</c>, at which point the
/// instance quietly selects a different backend and says nothing - the exact failure mode section 4.1
/// exists to prevent. <c>CHARTER_STORAGE_BACKEND</c> is therefore the switch, and everything else is
/// the detail of the backend it names.
/// </para>
/// <para>
/// Validation lives in <see cref="ObjectStoragePreflightCheck"/> rather than here, because the two
/// failures worth catching - a bucket block that is incomplete, and a directory that is missing or
/// read-only - are respectively a config-block problem and a question only the filesystem can answer.
/// Both surface as named section 30.1 first-run lines with the remediation beside them.
/// </para>
/// </remarks>
public sealed record StorageOptions
{
    /// <summary><c>CHARTER_STORAGE_BACKEND</c>.</summary>
    public const string BackendVariable = "CHARTER_STORAGE_BACKEND";

    /// <summary><c>CHARTER_STORAGE_PATH</c>.</summary>
    public const string PathVariable = "CHARTER_STORAGE_PATH";

    /// <summary>
    /// The most one stored object may carry, 8 MiB.
    /// </summary>
    /// <remarks>
    /// Storage that only grows is an operator's problem handed to them by somebody else, so every
    /// producer is bounded before it writes. 8 MiB is far above any transcript event and far below
    /// anything that would make a bucket expensive by accident; a producer with more than this
    /// truncates and records that it did, rather than writing an object nobody sized.
    /// </remarks>
    public const long MaxObjectBytes = 8L * 1024 * 1024;

    /// <summary>The named backend. <see cref="StorageBackend.None"/> unless an operator chose one.</summary>
    public required StorageBackend Backend { get; init; }

    /// <summary><c>CHARTER_STORAGE_PATH</c>, when the backend is <see cref="StorageBackend.Filesystem"/>.</summary>
    public string? Root { get; init; }

    /// <summary>The six <c>CHARTER_STORAGE_*</c> bucket variables, when they parsed as a block.</summary>
    public StorageConfig? S3 { get; init; }

    /// <summary>What the operator wrote for <see cref="BackendVariable"/>, when it was not a backend.</summary>
    /// <remarks>
    /// Kept rather than discarded so the preflight failure can quote it back. "unknown backend" is
    /// not actionable; "unknown backend 'filesytem'" is.
    /// </remarks>
    public string? UnrecognisedBackend { get; init; }

    /// <summary>The accepted spellings, in the order the failure message lists them.</summary>
    public static IReadOnlyList<string> BackendNames { get; } = ["none", "filesystem", "s3"];

    /// <summary>Storage is off. The default, and what a PaaS deployment keeps.</summary>
    public static StorageOptions Disabled { get; } = new() { Backend = StorageBackend.None };

    /// <summary>Reads the block from the environment.</summary>
    /// <param name="read">Environment access, injected so a test needs no process variables.</param>
    /// <param name="s3">
    /// The already-parsed bucket block from <see cref="CharterConfig.Storage"/>. Passed in rather
    /// than re-read: section 4.1 parses configuration once, and a second reader of the same six
    /// variables would be a second place for them to disagree.
    /// </param>
    public static StorageOptions FromEnvironment(Func<string, string?> read, StorageConfig? s3)
    {
        ArgumentNullException.ThrowIfNull(read);

        var raw = read(BackendVariable)?.Trim();
        var root = read(PathVariable)?.Trim();

        var backend = raw?.ToLowerInvariant() switch
        {
            null or "" or "none" => StorageBackend.None,
            "filesystem" => StorageBackend.Filesystem,
            "s3" => StorageBackend.S3,
            _ => (StorageBackend?)null,
        };

        return new StorageOptions
        {
            Backend = backend ?? StorageBackend.None,
            Root = string.IsNullOrWhiteSpace(root) ? null : root,
            S3 = s3,
            UnrecognisedBackend = backend is null ? raw : null,
        };
    }
}
