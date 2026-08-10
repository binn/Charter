using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Runners.Shim;

/// <summary>
/// The glob dialect <c>.charter/config.yml</c> writes scopes in (section 8).
/// </summary>
/// <remarks>
/// <para>
/// Supports <c>**</c> (any number of path segments), <c>*</c> (anything within one segment) and
/// <c>?</c> (one character within one segment) — which is exactly what section 8's example needs:
/// <c>src/Features/**</c>, <c>**/Migrations/**</c>, <c>**/appsettings*.json</c>.
/// </para>
/// <para>
/// Separate from <see cref="Charter.Auth.Authorization.PathScope"/> on purpose. That one answers a
/// question about <em>policy</em> — does one pattern cover another — and is deliberately prefix-only.
/// This one answers a question about a <em>file</em>, inside the sandbox, on the write path, and has
/// to be exact. Section 3.1 puts enforcement in the shim; this is the matcher it enforces with.
/// </para>
/// </remarks>
public static class ShimGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    /// <summary>Normalises a path to forward slashes with no leading <c>./</c> or <c>/</c>.</summary>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/').Trim();

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    /// <summary>True when <paramref name="path"/> matches <paramref name="pattern"/>.</summary>
    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = NormalizePath(pattern);
        if (trimmed.Length == 0)
        {
            return false;
        }

        return Compiled.GetOrAdd(trimmed, Compile).IsMatch(NormalizePath(path));
    }

    /// <summary>True when any pattern matches. An empty list matches nothing.</summary>
    public static bool MatchesAny(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.Any(pattern => Matches(pattern, path));
    }

    /// <summary>The first pattern that matches, or <see langword="null"/>.</summary>
    public static string? FirstMatch(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return patterns.FirstOrDefault(pattern => Matches(pattern, path));
    }

    private static Regex Compile(string pattern)
    {
        var builder = new StringBuilder("^");

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            switch (character)
            {
                case '*' when index + 1 < pattern.Length && pattern[index + 1] == '*':
                    // `**/` crosses zero or more segments; a trailing `**` swallows the rest.
                    if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                    {
                        builder.Append("(?:[^/]+/)*");
                        index += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        index++;
                    }

                    break;

                case '*':
                    builder.Append("[^/]*");
                    break;

                case '?':
                    builder.Append("[^/]");
                    break;

                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        builder.Append('$');

        // Ordinal, culture-invariant, and time-boxed: patterns come from a repository file, and a
        // pathological one must not be able to wedge the write path of a live session.
        return new Regex(
            builder.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(250));
    }
}
