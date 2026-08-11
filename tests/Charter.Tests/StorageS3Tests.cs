using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Charter.Configuration;
using Charter.Storage;

namespace Charter.Tests;

/// <summary>
/// The S3 backend against a real S3-compatible server.
/// </summary>
/// <remarks>
/// <para>
/// A mocked <see cref="IAmazonS3"/> proves nothing that matters here. Everything worth catching in
/// this backend is a disagreement between implementations of the same protocol — path-style versus
/// virtual-hosted addressing, the signing region, and the v4 SDK's habit of attaching a CRC32 trailer
/// to every upload, which the non-AWS servers reject outright. None of that has a mock. So these run
/// against a real server and are skipped by returning early when there is not one, matching how the
/// Postgres suites gate on <c>CHARTER_TEST_DATABASE_URL</c>.
/// </para>
/// <para>
/// To run them:
/// </para>
/// <code>
/// docker run -d --name charter-minio -p 9010:9000 \
///   -e MINIO_ROOT_USER=charterverify -e MINIO_ROOT_PASSWORD=charterverify123 \
///   minio/minio:latest server /data
///
/// export CHARTER_TEST_S3_ENDPOINT=http://localhost:9010
/// export CHARTER_TEST_S3_ACCESS_KEY=charterverify
/// export CHARTER_TEST_S3_SECRET_KEY=charterverify123
/// </code>
/// <para>
/// The bucket is created by the test, so nothing has to exist first.
/// </para>
/// </remarks>
public class StorageS3Tests
{
    private const string EndpointVariable = "CHARTER_TEST_S3_ENDPOINT";
    private const string AccessKeyVariable = "CHARTER_TEST_S3_ACCESS_KEY";
    private const string SecretKeyVariable = "CHARTER_TEST_S3_SECRET_KEY";

    private static readonly Guid Session = Guid.Parse("77777777-8888-9999-aaaa-bbbbbbbbbbbb");

    [Fact]
    public async Task BytesRoundTripThroughARealBucket()
    {
        await using var fixture = await S3Fixture.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture is null)
        {
            return;
        }

        var key = ObjectKey.WithExtension(
            ObjectKey.ForSession(Session, "transcript", "dotnet test", "output"),
            "txt");

        var text = new string('x', 50_000) + "\nBuild FAILED. 3 errors.";
        var bytes = Encoding.UTF8.GetBytes(text);

        var stored = await fixture.Store.PutAsync(key, bytes, ObjectContentTypes.Text, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(bytes.Length, stored.SizeBytes);

        var read = await fixture.Store.GetAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(text, Encoding.UTF8.GetString(read.Bytes));

        // The content type served is decided from the key Charter chose, not from what the bucket
        // reports, so a bucket somebody else can write to cannot make a browser render an object.
        Assert.Equal(ObjectContentTypes.Text, read.ContentType);
    }

    [Fact]
    public async Task AMissingObjectReadsAsNothing()
    {
        await using var fixture = await S3Fixture.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture is null)
        {
            return;
        }

        var read = await fixture.Store.GetAsync(
            ObjectKey.ForSession(Session, "transcript", "never", "written"),
            TestContext.Current.CancellationToken);

        Assert.Null(read);
    }

    [Fact]
    public async Task DeletingASessionTakesEverythingUnderItAndNothingElse()
    {
        await using var fixture = await S3Fixture.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture is null)
        {
            return;
        }

        var other = Guid.NewGuid();
        var kept = ObjectKey.ForSession(other, "transcript", "kept", "one");

        await fixture.Store.PutAsync(ObjectKey.ForSession(Session, "transcript", "a", "one"), "a"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);
        await fixture.Store.PutAsync(ObjectKey.ForSession(Session, "transcript", "b", "two"), "b"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);
        await fixture.Store.PutAsync(kept, "c"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);

        var deleted = await fixture.Store.DeleteSessionAsync(Session, TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Null(await fixture.Store.GetAsync(ObjectKey.ForSession(Session, "transcript", "a", "one"), TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.Store.GetAsync(kept, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WritingToABucketThatIsNotThereIsReportedRatherThanSwallowed()
    {
        await using var fixture = await S3Fixture.CreateAsync(TestContext.Current.CancellationToken);
        if (fixture is null)
        {
            return;
        }

        using var missing = new S3ObjectStore(fixture.Client, "charter-no-such-bucket-" + Guid.NewGuid().ToString("N"));

        var failure = await Assert.ThrowsAsync<ObjectStoreException>(async () => await missing.PutAsync(
            ObjectKey.ForSession(Session, "x"),
            "bytes"u8.ToArray(),
            ObjectContentTypes.Text,
            TestContext.Current.CancellationToken));

        Assert.Contains("bucket", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A real bucket, created for one test class and removed after it.</summary>
    private sealed class S3Fixture : IAsyncDisposable
    {
        private S3Fixture(IAmazonS3 client, string bucket)
        {
            Client = client;
            Bucket = bucket;
            Store = new S3ObjectStore(client, bucket);
        }

        public IAmazonS3 Client { get; }

        public string Bucket { get; }

        public S3ObjectStore Store { get; }

        public static async Task<S3Fixture?> CreateAsync(CancellationToken cancellationToken)
        {
            var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
            var accessKey = Environment.GetEnvironmentVariable(AccessKeyVariable);
            var secretKey = Environment.GetEnvironmentVariable(SecretKeyVariable);

            if (string.IsNullOrWhiteSpace(endpoint)
                || string.IsNullOrWhiteSpace(accessKey)
                || string.IsNullOrWhiteSpace(secretKey))
            {
                return null;
            }

            var bucket = "charter-verify-" + Guid.NewGuid().ToString("N")[..12];

            // Built the way an operator's configuration builds it, so the addressing mode and signing
            // region under test are the ones section 4.2 describes rather than test-only settings.
            var client = S3ObjectStore.Build(new StorageConfig
            {
                Endpoint = new Uri(endpoint),
                Bucket = bucket,
                AccessKey = Secret.From(accessKey)!,
                SecretKey = Secret.From(secretKey)!,
                Region = "us-east-1",
                ForcePathStyle = true,
            });

            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken);

            return new S3Fixture(client, bucket);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var listed = await Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });

                foreach (var entry in listed.S3Objects ?? [])
                {
                    await Client.DeleteObjectAsync(Bucket, entry.Key);
                }

                await Client.DeleteBucketAsync(Bucket);
            }
            catch (AmazonS3Exception)
            {
                // A leftover throwaway bucket is not worth failing a run over.
            }

            Store.Dispose();
            Client.Dispose();
        }
    }
}
