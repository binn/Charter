using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Charter.Configuration;

namespace Charter.Storage;

/// <summary>
/// Objects in an S3-compatible bucket - <c>CHARTER_STORAGE_BACKEND=s3</c>.
/// </summary>
/// <remarks>
/// <para>
/// The four vendors section 4.2 names - AWS S3, Cloudflare R2, MinIO and Backblaze B2 - agree on the
/// protocol and disagree on three details, all of which are configuration here rather than code:
/// the endpoint (<c>CHARTER_STORAGE_ENDPOINT</c>), whether the bucket is a subdomain or the first
/// path segment (<c>CHARTER_STORAGE_FORCE_PATH_STYLE</c>, default on because MinIO and most
/// self-hosted gateways need it), and what to sign with (<c>CHARTER_STORAGE_REGION</c>, default
/// <c>auto</c>, which is what R2 wants and what B2 tolerates).
/// </para>
/// <para>
/// <strong>Checksums are requested only where the protocol requires them.</strong> The v4 AWS SDK
/// defaults to attaching a CRC32 trailer to every upload, which the non-AWS implementations have
/// historically rejected outright; <c>WHEN_REQUIRED</c> restores the behaviour every S3-compatible
/// server understands. Integrity is not lost by this - <see cref="StoredObject.Sha256"/> is computed
/// over the same bytes Charter sent.
/// </para>
/// <para>
/// <strong>No presigned URLs.</strong> Section 7.4 puts authorisation on Charter's own endpoints, and
/// a presigned URL authorises by possession: paste it into a chat and the engineer-only artifact it
/// points at is no longer engineer-only, with no request reaching Charter to refuse. Reads therefore
/// come back through <c>MapCharterStorageBlobs</c>, which pays one proxied round trip and asks the
/// same permission question the transcript endpoint asks.
/// </para>
/// </remarks>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly bool _ownsClient;

    /// <summary>Opens a store against the configured bucket.</summary>
    public S3ObjectStore(StorageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _bucket = config.Bucket;
        _ownsClient = true;
        _client = Build(config);
    }

    /// <summary>Opens a store against a client somebody else owns. For tests and for reuse.</summary>
    public S3ObjectStore(IAmazonS3 client, string bucket)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        _client = client;
        _bucket = bucket;
        _ownsClient = false;
    }

    /// <inheritdoc />
    public StorageBackend Backend => StorageBackend.S3;

    /// <inheritdoc />
    public bool Enabled => true;

    /// <inheritdoc />
    public long MaxObjectBytes => StorageOptions.MaxObjectBytes;

    /// <summary>The bucket every key in this store lives in.</summary>
    public string Bucket => _bucket;

    /// <summary>Builds the client section 4.2's six variables describe.</summary>
    public static IAmazonS3 Build(StorageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new AmazonS3Config
        {
            ServiceURL = config.Endpoint.ToString(),
            ForcePathStyle = config.ForcePathStyle,
            AuthenticationRegion = config.Region,

            // See the class remarks: the v4 default breaks every non-AWS implementation of the same
            // protocol, and Charter's own digest covers what the trailer would have.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKey.Reveal(), config.SecretKey.Reveal()),
            options);
    }

    /// <inheritdoc />
    public async Task<StoredObject?> PutAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, MaxObjectBytes, nameof(bytes));

        using var content = new MemoryStream(bytes.ToArray(), writable: false);

        try
        {
            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = content,
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? ObjectContentTypes.Binary : contentType,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStoreException(Describe("write", key, exception), exception);
        }

        return new StoredObject(key, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)));
    }

    /// <inheritdoc />
    public async Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            using var response = await _client
                .GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            // The stored content type is not trusted for serving: the read endpoint decides that from
            // the key Charter itself chose, so a bucket somebody else can write to cannot make a
            // browser render an object as HTML (section 16).
            return new ObjectContent(key, buffer.ToArray(), ObjectContentTypes.ForKey(key));
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStoreException(Describe("read", key, exception), exception);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var prefix = ObjectKey.SessionScope(sessionId);
        var deleted = 0;
        string? continuation = null;

        try
        {
            do
            {
                var listed = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = prefix,
                        ContinuationToken = continuation,
                    },
                    cancellationToken).ConfigureAwait(false);

                var keys = listed.S3Objects?.Select(entry => new KeyVersion { Key = entry.Key }).ToList() ?? [];

                if (keys.Count > 0)
                {
                    await _client.DeleteObjectsAsync(
                        new DeleteObjectsRequest { BucketName = _bucket, Objects = keys },
                        cancellationToken).ConfigureAwait(false);

                    deleted += keys.Count;
                }

                continuation = listed.IsTruncated == true ? listed.NextContinuationToken : null;
            }
            while (continuation is not null);
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStoreException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Charter could not delete the objects of session {sessionId:D} in bucket '{_bucket}': {exception.Message}"),
                exception);
        }

        return deleted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private string Describe(string verb, string key, AmazonS3Exception exception)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Charter could not {verb} '{key}' in bucket '{_bucket}' ({(int)exception.StatusCode} {exception.ErrorCode}): {exception.Message}");
}
