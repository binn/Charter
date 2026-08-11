namespace Charter.Api.Changes;

/// <summary>
/// The gate a file path passes before Charter asks a provider for it (sections 7.3, 16.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Pane 3's path arrives twice from outside: once in the request URL, and
/// once in the <c>file_write</c> events the allowlist is built from. Both come from the execution plane —
/// the transcript's paths are whatever the agent wrote, and the client offers the reviewer the paths the
/// transcript listed — so neither is a fact about a file inside the repository (section 16.3).
/// </para>
/// <para>
/// A path that climbs is not a formatting problem. The old normalisation swapped <c>\</c> for <c>/</c> and
/// trimmed a leading <c>/</c>, and nothing removed a <c>..</c> segment; <c>GitHubRepositoryClient</c> escapes
/// each segment with <see cref="Uri.EscapeDataString"/>, under which <c>.</c> is unreserved and <c>..</c>
/// survives intact; and the request URL is then built with <c>new Uri(apiBase, path)</c>, which applies
/// RFC 3986's <em>remove_dot_segments</em>. Three <c>..</c> from <c>repos/{owner}/{name}/contents/</c> is
/// therefore a request to a different path on the API host, carrying this session's installation token.
/// </para>
/// <para>
/// Kestrel collapses <c>..</c> and <c>%2e%2e</c> segments in a request target before routing, so the URL
/// half of that is not reachable in the obvious spelling — but <c>%5C</c> is decoded to a backslash and is
/// <em>not</em> a path separator, so <c>a%5C..%5C..%5C..</c> arrives at the endpoint intact and the old
/// normalisation turned it straight back into a climb. The parsing is fixed here regardless of what any one
/// server does with a request target: whether a path leaves the repository is a question this code must be
/// able to answer on its own.
/// </para>
/// <para>
/// <strong>Reject rather than sanitise.</strong> Stripping the <c>..</c> and reading whatever was left would
/// answer an attack with a file, and leave nobody any reason to look. Every path below is refused whole, and
/// the endpoint reports the same <em>no such file</em> it reports for a path outside the change (section 7.3)
/// — a reader who is allowed to see the file gets the file, and everyone else learns nothing.
/// </para>
/// </remarks>
public static class RepositoryPath
{
    /// <summary>
    /// Longer than any path a repository can hold, and short enough not to be a memory tool.
    /// </summary>
    /// <remarks>
    /// git's own limit is 4096 bytes for a full path, and every forge is stricter. A path past this is not
    /// a file somebody is trying to read.
    /// </remarks>
    public const int MaxLength = 1024;

    /// <summary>
    /// The path as a repository-relative path, or empty when it is not one.
    /// </summary>
    /// <remarks>
    /// Empty is the only failure answer on purpose: callers treat "not a path in this repository" and "not a
    /// file in this change" as the same, unremarkable, <em>not found</em>.
    /// </remarks>
    public static string Normalise(string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        var candidate = path.Trim();

        if (candidate.Length is 0 or > MaxLength)
        {
            return string.Empty;
        }

        // A backslash is refused rather than translated. On the only forge Charter reads from, `/` is the
        // separator and `\` is a legal character in a filename — so translating one to the other invents a
        // path, and it was the translation itself that turned an unreachable request target into a climb.
        // A repository that genuinely contains a backslash in a filename loses pane 3 for that file and
        // nothing else.
        if (candidate.Contains('\\', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // Absolute, drive-qualified, UNC, or scheme-like: all of them name something that is not a file in
        // this repository, whatever the rest of the string says.
        if (candidate[0] == '/' || candidate.Contains(':', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        foreach (var segment in candidate.Split('/'))
        {
            // An empty segment is a `//`, a trailing slash, or a leading one — none of which name a file,
            // and all of which change how a URL built from the path is read. `.` and `..` are the climb.
            if (segment.Length == 0 || segment is "." or "..")
            {
                return string.Empty;
            }

            // A percent sequence would be escaped by the client, so it cannot smuggle a separator today.
            // It is refused anyway: a path that only becomes a climb after somebody else decodes it is a
            // path this function cannot answer for.
            if (segment.Contains('%', StringComparison.Ordinal))
            {
                return string.Empty;
            }

            foreach (var character in segment)
            {
                if (char.IsControl(character))
                {
                    return string.Empty;
                }
            }
        }

        return candidate;
    }
}
