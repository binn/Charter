using Charter.Configuration;
using Charter.Configuration.Preflight;
using Charter.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The backend is named, not inferred, and an unusable one stops the boot (sections 2.3, 4.1, 4.2, 30.1).
/// </summary>
/// <remarks>
/// <para>
/// Three backends: <c>none</c> keeps everything in Postgres and is the default a PaaS deployment
/// wants (section 2.3); <c>filesystem</c> is for the self-hoster with a durable volume;
/// <c>s3</c> is any S3-compatible bucket. The selection is the value of
/// <c>CHARTER_STORAGE_BACKEND</c> and nothing else, which is the property these tests hold in place —
/// deducing it from which variables are set means a typo silently changes what an instance does.
/// </para>
/// <para>
/// The other half is that a named backend which cannot work refuses to boot rather than failing at
/// the first artifact. A missing directory, a read-only one, and a bucket block that is not there are
/// all conclusive from inside the container, so all three are blocking.
/// </para>
/// </remarks>
public class StorageBackendTests
{
    private static readonly (string Key, string? Value)[] Bucket =
    [
        ("CHARTER_STORAGE_ENDPOINT", "https://minio.internal:9000"),
        ("CHARTER_STORAGE_BUCKET", "charter-artifacts"),
        ("CHARTER_STORAGE_ACCESS_KEY", "storage-access"),
        ("CHARTER_STORAGE_SECRET_KEY", "storage-secret"),
    ];

    [Fact]
    public void TheDefaultIsNoStorageAtAll()
    {
        var options = Options();

        Assert.Equal(StorageBackend.None, options.Backend);
        Assert.False(StorageServiceCollectionExtensions.Build(options).Enabled);

        var result = new ObjectStoragePreflightCheck(options).Run();

        Assert.Equal(PreflightStatus.Skipped, result.Status);
        Assert.Contains("Postgres", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABucketBlockAloneSelectsNothing()
    {
        // The whole point of a named switch. Before this, setting four variables was the selection;
        // now it is inert until CHARTER_STORAGE_BACKEND says s3, and the check says so rather than
        // letting the operator believe their bucket is in use.
        var options = Options(Bucket);

        Assert.Equal(StorageBackend.None, options.Backend);
        Assert.NotNull(options.S3);

        var result = new ObjectStoragePreflightCheck(options).Run();

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.True(result.IsBlockingFailure);
        Assert.Contains("CHARTER_STORAGE_ENDPOINT", result.Detail, StringComparison.Ordinal);
        Assert.Contains(StorageOptions.BackendVariable, result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void APathSetWithoutABackendIsRefusedRatherThanIgnored()
    {
        var options = Options([(StorageOptions.PathVariable, "/var/lib/charter")]);
        var result = new ObjectStoragePreflightCheck(options).Run();

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.Contains(StorageOptions.PathVariable, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledBackendIsQuotedBackWithTheOnesThatExist()
    {
        var result = new ObjectStoragePreflightCheck(
            Options([(StorageOptions.BackendVariable, "filesytem")])).Run();

        Assert.Equal(PreflightStatus.Failed, result.Status);
        Assert.Contains("filesytem", result.Detail, StringComparison.Ordinal);

        foreach (var name in StorageOptions.BackendNames)
        {
            Assert.Contains(name, result.Remediation!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilesystemWithoutAPathStopsTheBoot()
    {
        var result = new ObjectStoragePreflightCheck(
            Options([(StorageOptions.BackendVariable, "filesystem")])).Run();

        Assert.True(result.IsBlockingFailure);
        Assert.Contains(StorageOptions.PathVariable, result.Remediation!, StringComparison.Ordinal);

        // And it names the platform constraint that makes this the wrong backend there (section 2.3).
        Assert.Contains("durable", result.Remediation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilesystemWithAMissingDirectoryStopsTheBoot()
    {
        var missing = Path.Combine(Path.GetTempPath(), "charter-storage-" + Guid.NewGuid().ToString("N"));

        var result = new ObjectStoragePreflightCheck(Options(
        [
            (StorageOptions.BackendVariable, "filesystem"),
            (StorageOptions.PathVariable, missing),
        ])).Run();

        Assert.True(result.IsBlockingFailure);
        Assert.Contains(missing, result.Detail, StringComparison.Ordinal);

        // Charter does not create it: an absent path at boot is usually a volume that failed to mount,
        // and quietly creating a directory on the container's own disk would hide exactly that.
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void FilesystemWithAWritableDirectoryPassesAndLeavesNoProbeBehind()
    {
        using var directory = new TemporaryDirectory();

        var result = new ObjectStoragePreflightCheck(Options(
        [
            (StorageOptions.BackendVariable, "filesystem"),
            (StorageOptions.PathVariable, directory.Path),
        ])).Run();

        Assert.Equal(PreflightStatus.Passed, result.Status);
        Assert.Contains("writable", result.Detail, StringComparison.Ordinal);

        // The check proves writability by writing, so it has to clean up after itself.
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void FilesystemThatCannotBeWrittenToStopsTheBoot()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            // Permission bits mean something different on Windows, and root ignores them everywhere.
            return;
        }

        using var directory = new TemporaryDirectory();
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var result = new ObjectStoragePreflightCheck(Options(
            [
                (StorageOptions.BackendVariable, "filesystem"),
                (StorageOptions.PathVariable, directory.Path),
            ])).Run();

            Assert.True(result.IsBlockingFailure);
            Assert.Contains("write", result.Remediation!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(
                directory.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void S3WithoutABucketStopsTheBoot()
    {
        var result = new ObjectStoragePreflightCheck(
            Options([(StorageOptions.BackendVariable, "s3")])).Run();

        Assert.True(result.IsBlockingFailure);
        Assert.Contains("CHARTER_STORAGE_BUCKET", result.Remediation!, StringComparison.Ordinal);
        Assert.Contains("CHARTER_STORAGE_ENDPOINT", result.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void S3WithACompleteBlockPassesAndPrintsNeitherKey()
    {
        var options = Options([.. Bucket, (StorageOptions.BackendVariable, "s3"), ("CHARTER_STORAGE_REGION", "us-east-1")]);
        var result = new ObjectStoragePreflightCheck(options).Run();
        var described = result.Describe();

        Assert.Equal(PreflightStatus.Passed, result.Status);

        // Actionable: the endpoint, the bucket, the region and the addressing mode are all in the line.
        Assert.Contains("minio.internal", described, StringComparison.Ordinal);
        Assert.Contains("charter-artifacts", described, StringComparison.Ordinal);
        Assert.Contains("us-east-1", described, StringComparison.Ordinal);
        Assert.Contains("path-style", described, StringComparison.Ordinal);

        // And this line goes to a log platform, so neither credential appears in it.
        Assert.DoesNotContain("storage-access", described, StringComparison.Ordinal);
        Assert.DoesNotContain("storage-secret", described, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVariableInTheBlockReachesTheStore()
    {
        // Section 4.1's rule against configuration that parses and does nothing, asked of all eight.
        var options = Options(
        [
            .. Bucket,
            (StorageOptions.BackendVariable, "s3"),
            ("CHARTER_STORAGE_REGION", "eu-central-1"),
            ("CHARTER_STORAGE_FORCE_PATH_STYLE", "false"),
        ]);

        Assert.Equal(StorageBackend.S3, options.Backend);
        Assert.NotNull(options.S3);
        Assert.Equal("eu-central-1", options.S3.Region);
        Assert.False(options.S3.ForcePathStyle);

        using var store = Assert.IsType<S3ObjectStore>(StorageServiceCollectionExtensions.Build(options));

        Assert.Equal("charter-artifacts", store.Bucket);
        Assert.True(store.Enabled);
    }

    [Fact]
    public async Task ThePreflightRunReportsAFailingBackendAsBlocking()
    {
        // Through the runner an operator actually meets on first run, rather than the check alone.
        var check = new ObjectStoragePreflightCheck(Options([(StorageOptions.BackendVariable, "s3")]));
        var report = await new PreflightRunner([check]).RunAsync(
            PreflightScope.All,
            TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(ObjectStoragePreflightCheck.CheckName, report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AddCharterStorageRegistersTheStoreTheOffloadAndTheCheck()
    {
        using var directory = new TemporaryDirectory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterStorage(Options(
        [
            (StorageOptions.BackendVariable, "filesystem"),
            (StorageOptions.PathVariable, directory.Path),
        ]));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileSystemObjectStore>(provider.GetRequiredService<IObjectStore>());
        Assert.True(provider.GetRequiredService<TranscriptOffload>().Enabled);
        Assert.Contains(provider.GetServices<IPreflightCheck>(), check => check is ObjectStoragePreflightCheck);
    }

    private static StorageOptions Options(params (string Key, string? Value)[] overrides)
    {
        var read = ConfigTestEnvironment.With(overrides);
        return StorageOptions.FromEnvironment(read, CharterConfig.FromEnvironment(read).Storage);
    }
}

/// <summary>A directory that exists for the length of a test and then does not.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "charter-storage-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary directory that outlives a test run is not worth failing one over.
        }
    }
}
