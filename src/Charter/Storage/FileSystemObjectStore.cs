using System.Globalization;
using System.Security.Cryptography;

namespace Charter.Storage;

/// <summary>
/// Objects on durable local disk - <c>CHARTER_STORAGE_BACKEND=filesystem</c>.
/// </summary>
/// <remarks>
/// <para>
/// Section 2.3 says transcripts, diffs and artifacts never go to the container filesystem. That is a
/// <em>PaaS</em> constraint and it is written down as one: it exists because a container on Railway,
/// Fly or Heroku loses its disk on every deploy, so anything written there is gone at the worst
/// possible moment and the operator finds out weeks later. A self-hoster running Charter on a VPS
/// with a mounted volume is not in that position, and telling them their disk does not exist would
/// make them stand up an S3 gateway to store a few megabytes of build output.
/// </para>
/// <para>
/// The opt-in is what makes this safe. The default is <see cref="StorageBackend.None"/>, so a PaaS
/// deployment that names no backend keeps section 2.3's behaviour exactly; choosing
/// <c>filesystem</c> is an operator stating that they have a durable volume, and
/// <see cref="ObjectStoragePreflightCheck"/> refuses to boot if the path they named is missing or
/// not writable.
/// </para>
/// <para>
/// <strong>Writes are atomic.</strong> Bytes go to a temporary file in the same directory and are
/// then moved over the target, so a crash or a full disk mid-write leaves either the previous object
/// or none - never a half-written one that reads as truncated evidence.
/// </para>
/// </remarks>
public sealed class FileSystemObjectStore : IObjectStore
{
    private readonly string _root;

    /// <summary>Opens a store rooted at <paramref name="root"/>, creating it if it is not there.</summary>
    public FileSystemObjectStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <summary>The absolute directory objects live under.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public StorageBackend Backend => StorageBackend.Filesystem;

    /// <inheritdoc />
    public bool Enabled => true;

    /// <inheritdoc />
    public long MaxObjectBytes => StorageOptions.MaxObjectBytes;

    /// <inheritdoc />
    public async Task<StoredObject?> PutAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, MaxObjectBytes, nameof(bytes));

        var directory = Path.GetDirectoryName(path)
                        ?? throw new ObjectStoreException($"'{key}' does not name a file.");

        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, Path.GetRandomFileName());

        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Discard(temporary);

            throw new ObjectStoreException(
                $"Charter could not write '{key}' under {_root}. {exception.Message}",
                exception);
        }
        catch
        {
            Discard(temporary);
            throw;
        }

        return new StoredObject(key, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
    }

    /// <inheritdoc />
    public async Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return new ObjectContent(key, bytes, ObjectContentTypes.ForKey(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ObjectStoreException(
                $"Charter could not read '{key}' under {_root}. {exception.Message}",
                exception);
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.Combine(
            _root,
            ObjectKey.SessionPrefix,
            sessionId.ToString("N", CultureInfo.InvariantCulture));

        if (!Directory.Exists(directory))
        {
            return Task.FromResult(0);
        }

        var count = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ObjectStoreException(
                $"Charter could not delete the objects of session {sessionId:D} under {_root}. {exception.Message}",
                exception);
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Turns a key into an absolute path, refusing anything that would leave the root.
    /// </summary>
    /// <remarks>
    /// Section 16, belt and braces. <see cref="ObjectKey"/> already guarantees a key cannot carry a
    /// separator it was not given, and this refuses one anyway - by rebuilding the path segment by
    /// segment and then checking that the result is still under the root. A traversal has to defeat
    /// both, and the second one does not care how the first was reached.
    /// </remarks>
    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Length > ObjectKey.MaxKeyLength || key.Contains('\\', StringComparison.Ordinal))
        {
            throw new ObjectStoreException($"'{key}' is not a usable object key.");
        }

        var path = _root;

        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ObjectStoreException($"'{key}' is not a usable object key.");
            }

            foreach (var character in segment)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
                {
                    throw new ObjectStoreException($"'{key}' is not a usable object key.");
                }
            }

            path = Path.Combine(path, segment);
        }

        var resolved = Path.GetFullPath(path);

        if (!resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ObjectStoreException($"'{key}' resolves outside {_root}.");
        }

        return resolved;
    }

    private static void Discard(string temporary)
    {
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The write already failed and is being reported. A leftover temporary file is not worth
            // replacing that message with this one.
        }
    }
}

/// <summary>The content types Charter stores, inferred from the key it chose.</summary>
/// <remarks>
/// Every key Charter builds ends in an extension Charter picked, so this is a lookup over its own
/// choices rather than a guess about somebody else's file. Anything unrecognised is served as opaque
/// bytes, which is the safe direction: a browser must never be talked into rendering a stored object
/// as HTML.
/// </remarks>
public static class ObjectContentTypes
{
    /// <summary>Plain UTF-8 text - transcript output, logs, reports.</summary>
    public const string Text = "text/plain; charset=utf-8";

    /// <summary>Anything else.</summary>
    public const string Binary = "application/octet-stream";

    /// <summary>The content type for a key, from its extension.</summary>
    public static string ForKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return Path.GetExtension(key).ToLowerInvariant() switch
        {
            ".txt" or ".log" => Text,
            ".json" => "application/json; charset=utf-8",
            _ => Binary,
        };
    }
}
