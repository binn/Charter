using Charter.Configuration;
using Charter.Configuration.Preflight;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Storage;

/// <summary>
/// Registers the object store section 4.2's <c>CHARTER_STORAGE_*</c> block selects (sections 4.1, 27.1).
/// </summary>
/// <remarks>
/// <para>
/// One store, always registered, and which one is decided once here from the named backend. Consumers
/// take <see cref="IObjectStore"/> and branch on <see cref="IObjectStore.Enabled"/> rather than on a
/// missing registration, so the default configuration - no storage at all - is a code path that runs
/// on every instance rather than one nobody exercises.
/// </para>
/// <para>
/// The preflight check is registered whatever the backend, because the interesting cases are the ones
/// where the backend is <em>not</em> what the operator meant: a misspelled value, or a path set beside
/// a backend that is still <c>none</c>. A check that only ran when storage was on would be silent for
/// exactly those.
/// </para>
/// </remarks>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Adds the object store, the transcript offload, and the section 30.1 preflight check.
    /// </summary>
    /// <param name="services">The collection.</param>
    /// <param name="options">
    /// The resolved block. Build it with
    /// <see cref="StorageOptions.FromEnvironment"/>, passing <see cref="CharterConfig.Storage"/>, so
    /// the six bucket variables are read by the section 4.1 parser and not a second time here.
    /// </param>
    public static IServiceCollection AddCharterStorage(this IServiceCollection services, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IObjectStore>(_ => Build(options));
        services.TryAddSingleton<TranscriptOffload>();

        services.AddSingleton<IPreflightCheck>(provider =>
            new ObjectStoragePreflightCheck(provider.GetRequiredService<StorageOptions>()));

        return services;
    }

    /// <summary>
    /// The store for a resolved block.
    /// </summary>
    /// <remarks>
    /// Falls back to <see cref="NullObjectStore"/> rather than throwing when a backend is named but
    /// its detail is missing. The blocking preflight check has already refused that instance by the
    /// time anything resolves a store, and a constructor that threw during composition would replace
    /// its named first-run line with a stack trace (section 30.1).
    /// </remarks>
    public static IObjectStore Build(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options switch
        {
            { Backend: StorageBackend.Filesystem, Root: { Length: > 0 } root } => new FileSystemObjectStore(root),
            { Backend: StorageBackend.S3, S3: { } bucket } => new S3ObjectStore(bucket),
            _ => NullObjectStore.Instance,
        };
    }
}
