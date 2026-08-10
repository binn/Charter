using System.Collections.Concurrent;
using System.Globalization;
using Charter.GitHub;
using Microsoft.Extensions.Logging;

namespace Charter.Onboarding;

/// <summary>Reads a repository's committed <c>.charter/</c> folder at a commit (section 8).</summary>
public interface ICharterFolderLoader
{
    /// <summary>
    /// The folder as it stands at <paramref name="commitSha"/>, from cache when it has been read
    /// before.
    /// </summary>
    /// <param name="repository">The repository, with its installation.</param>
    /// <param name="commitSha">
    /// A commit SHA, or a branch name. A branch is resolved to its head commit first, because the
    /// cache key must be immutable — caching against "main" would serve yesterday's guardrails.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<CharterFolder> LoadAsync(
        GitHubRepository repository,
        string commitSha,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A bounded, per-commit cache of parsed <c>.charter/</c> folders.
/// </summary>
/// <remarks>
/// <para>
/// Section 8 gitignores <c>.charter/cache/</c> in the target repository, so the cache lives on
/// Charter's side. The key is (repository, commit) and never (repository, branch): a commit's
/// <c>.charter/</c> folder cannot change, so an entry is never stale, and the guardrails a session
/// runs under are exactly the ones committed at the commit it branched from.
/// </para>
/// <para>
/// In-memory rather than in Postgres. Section 2.3 forbids orchestration state in memory, and this is
/// not orchestration state — it is a pure function of an immutable commit, so losing it on restart
/// costs one extra API call and nothing else.
/// </para>
/// </remarks>
public sealed class CharterFolderCache
{
    private readonly ConcurrentDictionary<string, CharterFolder> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;

    /// <summary>Creates a cache holding at most <paramref name="capacity"/> folders.</summary>
    public CharterFolderCache(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>How many folders are held.</summary>
    public int Count => _entries.Count;

    /// <summary>The cached folder for this repository at this commit, if any.</summary>
    public CharterFolder? Get(string repositoryFullName, string commitSha)
        => _entries.TryGetValue(Key(repositoryFullName, commitSha), out var folder) ? folder : null;

    /// <summary>Caches a folder, evicting the oldest entry when the cache is full.</summary>
    public void Set(string repositoryFullName, string commitSha, CharterFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var key = Key(repositoryFullName, commitSha);

        if (_entries.TryAdd(key, folder))
        {
            _order.Enqueue(key);
        }
        else
        {
            _entries[key] = folder;
        }

        while (_entries.Count > _capacity && _order.TryDequeue(out var oldest))
        {
            _entries.TryRemove(oldest, out _);
        }
    }

    /// <summary>Forgets everything cached for one repository — the re-recon escape hatch.</summary>
    public void Evict(string repositoryFullName)
    {
        var prefix = repositoryFullName + "@";

        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string repositoryFullName, string commitSha)
        => string.Create(CultureInfo.InvariantCulture, $"{repositoryFullName}@{commitSha}");
}

/// <inheritdoc />
public sealed class CharterFolderLoader : ICharterFolderLoader
{
    private readonly IGitHubRepositoryClient _github;
    private readonly CharterFolderCache _cache;
    private readonly GitHubOptions _options;
    private readonly ILogger<CharterFolderLoader> _logger;

    public CharterFolderLoader(
        IGitHubRepositoryClient github,
        CharterFolderCache cache,
        GitHubOptions options,
        ILogger<CharterFolderLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _github = github;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CharterFolder> LoadAsync(
        GitHubRepository repository,
        string commitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        // A branch name is a moving target; resolve it before it becomes a cache key.
        var resolved = LooksLikeCommitSha(commitSha)
            ? commitSha
            : await _github.GetBranchHeadShaAsync(repository, commitSha, cancellationToken) ?? commitSha;

        if (_cache.Get(repository.FullName, resolved) is { } cached)
        {
            return cached;
        }

        var tree = await _github.ListTreeAsync(repository, resolved, cancellationToken);

        var wanted = tree
            .Where(entry => entry.IsBlob)
            .Where(entry => entry.Path.StartsWith(CharterFolder.Root, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !entry.Path.StartsWith(CharterFolder.CacheDirectory, StringComparison.OrdinalIgnoreCase))
            .Take(_options.MaxCharterFolderFiles)
            .ToList();

        CharterFolder folder;

        if (wanted.Count == 0)
        {
            folder = CharterFolder.Missing(resolved);
        }
        else
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in wanted)
            {
                files[entry.Path] = await _github.GetBlobTextAsync(repository, entry.Sha, cancellationToken);
            }

            folder = CharterFolder.FromFiles(files, resolved);
        }

        foreach (var warning in folder.Warnings)
        {
            // Section 8's whole point: these never stop a load, and they must not be silent either.
            _logger.LogWarning(
                "{Repository}@{Commit}: {Warning}",
                repository.FullName,
                resolved,
                warning);
        }

        _cache.Set(repository.FullName, resolved, folder);

        return folder;
    }

    /// <summary>A full or abbreviated hex object name, as opposed to a branch.</summary>
    private static bool LooksLikeCommitSha(string value)
        => value.Length is >= 7 and <= 40 && value.All(static character => Uri.IsHexDigit(character));
}
