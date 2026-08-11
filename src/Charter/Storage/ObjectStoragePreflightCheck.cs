using System.Globalization;
using Charter.Configuration;
using Charter.Configuration.Preflight;

namespace Charter.Storage;

/// <summary>
/// The selected storage backend is one Charter has, and is actually usable (sections 4.1, 4.2, 30.1).
/// </summary>
/// <remarks>
/// <para>
/// Three backends, and which one is in use is stated rather than deduced. Inferring it from which
/// variables happen to be set reads well right up until somebody writes
/// <c>CHARTER_STORAGE_BUCKETT</c>: the instance then selects a different backend, boots cleanly, and
/// tells nobody. Section 4.1 wants configuration that fails loudly, so the switch is
/// <c>CHARTER_STORAGE_BACKEND</c> and everything else is the detail of the backend it names.
/// </para>
/// <para>
/// Blocking, and it touches the disk. The two failures worth catching are an incomplete bucket block
/// and a directory that is missing or read-only, and the second can only be settled by writing to it -
/// so this check does, with a probe file it removes. Both failures are conclusive and both mean that
/// the first artifact of the first session would be lost, which is the worst possible moment to find
/// out.
/// </para>
/// <para>
/// <strong>Neither key is ever printed.</strong> The endpoint, bucket, region and addressing mode make
/// the line actionable; the access key and secret key would put credentials into the operator's log
/// platform, so the message says only whether they arrived.
/// </para>
/// </remarks>
public sealed class ObjectStoragePreflightCheck(StorageOptions options) : IPreflightCheck
{
    /// <summary>The check name, in the section 30.1 first-run results.</summary>
    public const string CheckName = "object storage";

    /// <summary>The name of the probe file the filesystem backend writes and removes.</summary>
    public const string ProbeFileName = ".charter-write-probe";

    /// <summary>Every variable in the block, in the order the failure messages name them.</summary>
    public static IReadOnlyList<string> Variables { get; } =
    [
        StorageOptions.BackendVariable,
        StorageOptions.PathVariable,
        "CHARTER_STORAGE_ENDPOINT",
        "CHARTER_STORAGE_BUCKET",
        "CHARTER_STORAGE_ACCESS_KEY",
        "CHARTER_STORAGE_SECRET_KEY",
        "CHARTER_STORAGE_REGION",
        "CHARTER_STORAGE_FORCE_PATH_STYLE",
    ];

    /// <summary>The per-object cap, in whole mebibytes, as the passing lines print it.</summary>
    private static string Cap { get; } =
        (StorageOptions.MaxObjectBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    /// <remarks>The filesystem backend is verified by writing to the directory, which is I/O.</remarks>
    public bool RequiresIo => true;

    /// <inheritdoc />
    public PreflightSeverity Severity => PreflightSeverity.Blocking;

    /// <inheritdoc />
    public ValueTask<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Run());
    }

    /// <summary>Runs the check.</summary>
    public PreflightResult Run()
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.UnrecognisedBackend is { Length: > 0 } unrecognised)
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.BackendVariable} is '{unrecognised}', which is not a backend Charter has",
                $"set {StorageOptions.BackendVariable} to one of {string.Join(", ", StorageOptions.BackendNames)}. "
                + "'none' keeps everything in Postgres and is the right answer on a platform with an "
                + "ephemeral filesystem (section 2.3)");
        }

        return options.Backend switch
        {
            StorageBackend.Filesystem => Filesystem(),
            StorageBackend.S3 => S3(),
            _ => Off(),
        };
    }

    private PreflightResult Off()
    {
        // Say so when a backend-specific variable is set and the backend is not, because that is the
        // shape a typo in CHARTER_STORAGE_BACKEND takes: the detail is all there and nothing reads it.
        var stranded = new List<string>();

        if (options.Root is not null)
        {
            stranded.Add(StorageOptions.PathVariable);
        }

        if (options.S3 is not null)
        {
            stranded.Add("CHARTER_STORAGE_ENDPOINT");
        }

        if (stranded.Count > 0)
        {
            return PreflightResult.Fail(
                Name,
                $"{string.Join(" and ", stranded)} {(stranded.Count == 1 ? "is" : "are")} set and "
                + $"{StorageOptions.BackendVariable} is not, so nothing would read {(stranded.Count == 1 ? "it" : "them")}",
                $"set {StorageOptions.BackendVariable} to the backend you meant "
                + $"({string.Join(" or ", StorageOptions.BackendNames.Skip(1))}), or unset "
                + $"{string.Join(" and ", stranded)}. Section 4.1 refuses configuration it would ignore "
                + "rather than accepting it and doing nothing");
        }

        return PreflightResult.Skip(
            Name,
            $"{StorageOptions.BackendVariable} is none, so transcripts and verification artifacts stay "
            + "in Postgres. That is the supported configuration on a platform whose filesystem does not "
            + "survive a deploy (section 2.3), and the artifact of a web project is a preview URL anyway");
    }

    private PreflightResult Filesystem()
    {
        if (options.Root is not { Length: > 0 } root)
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.BackendVariable} is filesystem and {StorageOptions.PathVariable} is not set",
                $"set {StorageOptions.PathVariable} to a directory on a volume that survives a restart. "
                + $"If this instance has no durable disk, set {StorageOptions.BackendVariable}=none or "
                + "s3 instead - a container filesystem loses everything written to it on the next "
                + "deploy (section 2.3)");
        }

        string resolved;

        try
        {
            resolved = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.PathVariable} is '{root}', which is not a usable path: {exception.Message}",
                $"set {StorageOptions.PathVariable} to an absolute directory path");
        }

        if (!Directory.Exists(resolved))
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.PathVariable} is '{resolved}', and no such directory exists",
                "create the directory and mount the volume it lives on, or point "
                + $"{StorageOptions.PathVariable} at one that is already mounted. Charter does not "
                + "create it: a path that is absent at boot is usually a volume that failed to mount, "
                + "and creating a directory on the container's own disk would hide that");
        }

        var probe = Path.Combine(resolved, ProbeFileName);

        try
        {
            File.WriteAllBytes(probe, [0x63, 0x68, 0x61, 0x72, 0x74, 0x65, 0x72]);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.PathVariable} is '{resolved}', which this process cannot write to: {exception.Message}",
                "give the user Charter runs as write access to that directory, or point "
                + $"{StorageOptions.PathVariable} somewhere it has it");
        }

        return PreflightResult.Pass(
            Name,
            $"filesystem, at {resolved}, writable; objects are capped at {Cap} MiB each and Charter "
            + "never deletes on a schedule - see docs/privacy.md for the retention you are expected to run");
    }

    private PreflightResult S3()
    {
        if (options.S3 is not { } storage)
        {
            return PreflightResult.Fail(
                Name,
                $"{StorageOptions.BackendVariable} is s3 and the bucket is not configured",
                "set CHARTER_STORAGE_ENDPOINT, CHARTER_STORAGE_BUCKET, CHARTER_STORAGE_ACCESS_KEY and "
                + "CHARTER_STORAGE_SECRET_KEY. They are one block: an endpoint without a bucket cannot "
                + "store anything, so all four are required together (section 4.2). AWS S3 needs an "
                + "endpoint here too - the regional one, https://s3.<region>.amazonaws.com");
        }

        var addressing = storage.ForcePathStyle ? "path-style" : "virtual-hosted";
        var credentials = storage.AccessKey.Length > 0 && storage.SecretKey.Length > 0
            ? "an access key and a secret key"
            : "incomplete credentials";

        return PreflightResult.Pass(
            Name,
            $"s3, bucket {storage.Bucket} at {storage.Endpoint} (region {storage.Region}, {addressing} "
            + $"addressing, {credentials}); objects are capped at {Cap} MiB each and Charter never "
            + "expires them - configure a bucket lifecycle rule for retention (docs/privacy.md)");
    }
}
