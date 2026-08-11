using Charter.Diagnostics;

namespace Charter.Updates;

/// <summary>
/// How often the release check runs, and against what (section 28).
/// </summary>
/// <remarks>
/// None of this is an environment variable. Section 4.2 gives the operator two switches —
/// <c>CHARTER_UPDATE_CHECK</c> and <c>CHARTER_UPDATE_CHANNEL</c> — and the rest is fixed by the
/// design: daily with jitter, against the repository this build says it came from. A knob for the
/// interval would be a knob for how far behind an instance may silently fall.
/// </remarks>
public sealed record UpdateCheckOptions
{
    /// <summary>How long after a check the next one becomes claimable, before jitter.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The spread added to <see cref="Interval"/>.
    /// </summary>
    /// <remarks>
    /// Section 28 asks for jitter because every instance that boots from the same image would
    /// otherwise ask GitHub at the same second of the same minute. Additive rather than symmetric, so
    /// the interval is a floor: an instance never checks more often than daily.
    /// </remarks>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromHours(3);

    /// <summary>How long the first check waits after boot, so a restart loop cannot become traffic.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long the request may take before it counts as unreachable.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many releases to read. Enough to find the newest on either channel.</summary>
    public int PageSize { get; init; } = 30;

    /// <summary>
    /// The version this instance is running, compared locally against what the response carries.
    /// </summary>
    public string CurrentVersion { get; init; } = BuildInfo.Version;

    /// <summary>
    /// <c>owner/name</c> for the releases endpoint, derived from the compiled-in source URL.
    /// </summary>
    /// <remarks>
    /// Derived rather than configured: section 24 compiles the source URL in for AGPL section 13, and
    /// the release an instance should be offered is a release of the source it is running. A fork that
    /// rebranded and re-pointed <c>CharterSourceUrl</c> checks its own releases; one hosted somewhere
    /// that is not GitHub gets <see langword="null"/> here and no check at all.
    /// </remarks>
    public string? Repository { get; init; } = RepositoryFrom(BuildInfo.SourceUrl);

    /// <summary>Extracts <c>owner/name</c> from a GitHub repository URL.</summary>
    public static string? RepositoryFrom(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return null;
        }

        var owner = segments[0];
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        return IsSafeSegment(owner) && IsSafeSegment(name) ? $"{owner}/{name}" : null;
    }

    /// <summary>
    /// Whether a path segment may be interpolated into the request URL.
    /// </summary>
    /// <remarks>
    /// The value comes from a build property rather than from a request, but it is interpolated into a
    /// URL, and section 16's habit is to validate at the boundary rather than to reason about where a
    /// value came from. GitHub owner and repository names are this alphabet.
    /// </remarks>
    private static bool IsSafeSegment(string segment)
        => segment.Length is > 0 and <= 100
           && segment.All(character =>
               char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
