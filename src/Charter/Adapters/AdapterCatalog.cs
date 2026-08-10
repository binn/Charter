using System.Diagnostics.CodeAnalysis;

namespace Charter.Adapters;

/// <summary>Records that a local adapter file replaced a shipped one of the same id.</summary>
public sealed record AdapterOverride(string Id, string ReplacedSourcePath, string SourcePath);

/// <summary>Every adapter this instance can dispatch to (section 12b).</summary>
public interface IAdapterCatalog
{
    /// <summary>The directories the catalog was loaded from, in precedence order.</summary>
    AdapterSources Sources { get; }

    /// <summary>Every adapter, ordered by id.</summary>
    IReadOnlyList<AdapterDocument> Adapters { get; }

    /// <summary>Unknown keys and other things Charter kept going past (section 8).</summary>
    IReadOnlyList<AdapterWarning> Warnings { get; }

    /// <summary>Shipped adapters a local file replaced.</summary>
    IReadOnlyList<AdapterOverride> Overrides { get; }

    bool TryGet(string id, [NotNullWhen(true)] out AdapterDocument? adapter);

    AdapterDocument Get(string id);
}

/// <summary>
/// Loads every adapter file from <see cref="AdapterSources"/> and applies the override rules.
/// </summary>
public sealed class AdapterCatalog : IAdapterCatalog
{
    private readonly Dictionary<string, AdapterDocument> _byId;

    private AdapterCatalog(
        AdapterSources sources,
        Dictionary<string, AdapterDocument> byId,
        IReadOnlyList<AdapterWarning> warnings,
        IReadOnlyList<AdapterOverride> overrides)
    {
        Sources = sources;
        _byId = byId;
        Adapters = [.. byId.Values.OrderBy(adapter => adapter.Id, StringComparer.Ordinal)];
        Warnings = warnings;
        Overrides = overrides;
    }

    public AdapterSources Sources { get; }

    public IReadOnlyList<AdapterDocument> Adapters { get; }

    public IReadOnlyList<AdapterWarning> Warnings { get; }

    public IReadOnlyList<AdapterOverride> Overrides { get; }

    /// <summary>Loads every <c>*.yml</c> and <c>*.yaml</c> file from each source directory in turn.</summary>
    /// <exception cref="AdapterLoadException">
    /// A directory is missing, a file is invalid, or two files in one directory claim the same id.
    /// Thrown once, carrying every problem found across every file.
    /// </exception>
    public static AdapterCatalog Load(AdapterSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var problems = new List<string>();
        var warnings = new List<AdapterWarning>();
        var overrides = new List<AdapterOverride>();
        var byId = new Dictionary<string, AdapterDocument>(StringComparer.Ordinal);

        foreach (var directory in sources.Directories)
        {
            if (!Directory.Exists(directory))
            {
                problems.Add(
                    $"{directory}: is listed as an adapter directory but does not exist. "
                    + $"Check {AdapterSources.PathVariable}.");
                continue;
            }

            // Within one directory, ordinal filename order, so a load is reproducible.
            var files = Directory
                .EnumerateFiles(directory, "*.*")
                .Where(file => file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                               || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal);

            var fromThisDirectory = new Dictionary<string, AdapterDocument>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                AdapterDocument adapter;
                try
                {
                    adapter = AdapterYamlLoader.Load(file, File.ReadAllText(file), warnings);
                }
                catch (AdapterLoadException ex)
                {
                    problems.AddRange(ex.Problems);
                    continue;
                }
                catch (IOException ex)
                {
                    problems.Add($"{file}: could not be read: {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    problems.Add($"{file}: could not be read: {ex.Message}");
                    continue;
                }

                if (fromThisDirectory.TryGetValue(adapter.Id, out var clash))
                {
                    problems.Add(
                        $"{file}: 'id' is '{adapter.Id}', which {clash.SourcePath} in the same directory "
                        + "already claims. Ids are unique within a directory; put an override in a "
                        + $"{AdapterSources.PathVariable} directory instead.");
                    continue;
                }

                fromThisDirectory[adapter.Id] = adapter;

                if (byId.TryGetValue(adapter.Id, out var replaced))
                {
                    overrides.Add(new AdapterOverride(adapter.Id, replaced.SourcePath, adapter.SourcePath));
                }

                byId[adapter.Id] = adapter;
            }
        }

        if (problems.Count > 0)
        {
            throw new AdapterLoadException(problems);
        }

        if (byId.Count == 0)
        {
            throw new AdapterLoadException(
                $"No adapter files were found in {string.Join(", ", sources.Directories)}. "
                + "Charter cannot dispatch a session without at least one adapter.");
        }

        return new AdapterCatalog(sources, byId, warnings, overrides);
    }

    public bool TryGet(string id, [NotNullWhen(true)] out AdapterDocument? adapter)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id, out adapter);
    }

    public AdapterDocument Get(string id)
        => TryGet(id, out var adapter)
            ? adapter
            : throw new AdapterLoadException(
                $"No adapter with id '{id}' is loaded. Available adapters: "
                + $"{string.Join(", ", Adapters.Select(a => a.Id))}.");
}
