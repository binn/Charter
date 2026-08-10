using System.Text;
using System.Text.RegularExpressions;

namespace Charter.Refinement;

/// <summary>Why a path was not acceptable.</summary>
public enum ScopeViolationReason
{
    /// <summary>The path matched an entry in <c>scopes.deny</c>.</summary>
    Denied,

    /// <summary>The path matched nothing in <c>scopes.allow</c>. Deny by default (section 7.3).</summary>
    OutsideAllowList,
}

/// <summary>One path a spec named that it may not touch.</summary>
/// <param name="Path">The path from the spec's scope.</param>
/// <param name="Pattern">The <c>.charter/config.yml</c> pattern that decided it, if any.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="Area">A requester-safe name for the area, never a path.</param>
public sealed record ScopeViolation(
    string Path,
    string? Pattern,
    ScopeViolationReason Reason,
    string Area);

/// <summary>
/// The outcome of checking a spec's scope against a repo's allow and deny lists.
/// </summary>
/// <remarks>
/// The two messages are separate on purpose. Section 7.4: a requester never sees a repo name, a
/// branch or a file path, so <see cref="RequesterMessage"/> names <em>areas</em> in plain English
/// and carries no paths at all. <see cref="EngineerDetail"/> carries the paths and the patterns,
/// because the engineer is the one who has to act on it.
/// </remarks>
public sealed record ScopeDecision
{
    /// <summary>Everything the spec named is inside scope.</summary>
    public static ScopeDecision Allowed { get; } = new() { IsAllowed = true, Violations = [] };

    /// <summary>Whether the spec may proceed.</summary>
    public bool IsAllowed { get; init; }

    /// <summary>Everything that was out of bounds.</summary>
    public IReadOnlyList<ScopeViolation> Violations { get; init; } = [];

    /// <summary>Plain English for the requester. Contains no paths (section 7.4).</summary>
    public string RequesterMessage
    {
        get
        {
            if (IsAllowed)
            {
                return string.Empty;
            }

            var areas = Violations
                .Select(static violation => violation.Area)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var subject = areas.Count switch
            {
                1 => areas[0],
                2 => $"{areas[0]} and {areas[1]}",
                _ => string.Join(", ", areas.Take(areas.Count - 1)) + $", and {areas[^1]}",
            };

            return
                $"I can't take this one on. Doing what you've asked would mean changing {subject}, "
                + "which this project keeps off limits to Charter. I've passed it to an engineer, "
                + "who can make the change by hand — you don't need to do anything else.";
        }
    }

    /// <summary>The paths and patterns, for the engineer picking the request up.</summary>
    public string EngineerDetail
    {
        get
        {
            if (IsAllowed)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("The refined spec named paths outside the repo's scope:");
            foreach (var violation in Violations)
            {
                builder.AppendLine().Append("- `").Append(violation.Path).Append('`');
                builder.Append(violation.Reason == ScopeViolationReason.Denied
                    ? $" matches deny pattern `{violation.Pattern}`"
                    : " matches no allow pattern (deny by default, section 7.3)");
            }

            return builder.ToString();
        }
    }

    /// <summary>A refusal.</summary>
    public static ScopeDecision Refused(IReadOnlyList<ScopeViolation> violations) =>
        new() { IsAllowed = false, Violations = violations };
}

/// <summary>
/// The repo's <c>scopes.allow</c> / <c>scopes.deny</c> lists from <c>.charter/config.yml</c>
/// (section 8), applied to a refined spec's scope.
/// </summary>
/// <remarks>
/// <para>
/// This is the refinement-time half of guardrail 2 in section 7.3, and it is <em>not</em> the
/// enforcement point: path scope is enforced in the runner, so a compromised session cannot widen
/// it. What this does is refuse to <em>produce</em> a spec that would need to be refused later, and
/// explain the refusal in language a requester can act on (section 10).
/// </para>
/// <para>
/// Deny wins over allow, and an empty allow list denies everything — a newly connected repo is
/// writable by nobody until someone says otherwise.
/// </para>
/// </remarks>
public sealed class RefinementScopePolicy
{
    private static readonly (string Fragment, string Area)[] AreaNames =
    [
        ("auth", "sign-in and accounts"),
        ("login", "sign-in and accounts"),
        ("identity", "sign-in and accounts"),
        ("migration", "the database structure"),
        ("appsettings", "deployment settings"),
        (".github", "the build and release pipeline"),
        ("infra", "the servers and infrastructure"),
        ("terraform", "the servers and infrastructure"),
        ("deploy", "the servers and infrastructure"),
        ("secret", "credentials and secrets"),
        ("credential", "credentials and secrets"),
        ("billing", "billing and payments"),
        ("payment", "billing and payments"),
        ("security", "security controls"),
        ("charter/", "Charter's own guardrail settings"),
    ];

    /// <summary>Creates a policy.</summary>
    public RefinementScopePolicy(IEnumerable<string>? allow, IEnumerable<string>? deny)
    {
        Allow = SpecText.CleanList(allow);
        Deny = SpecText.CleanList(deny);
    }

    /// <summary>A policy that permits nothing, which is what an unconfigured repo gets.</summary>
    public static RefinementScopePolicy DenyEverything { get; } = new(null, null);

    /// <summary>Patterns the agent may write inside.</summary>
    public IReadOnlyList<string> Allow { get; }

    /// <summary>Patterns the agent may never write inside. Deny wins.</summary>
    public IReadOnlyList<string> Deny { get; }

    /// <summary>Checks a refined spec's scope.</summary>
    public ScopeDecision Evaluate(SpecScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Evaluate(scope.All);
    }

    /// <summary>Checks a set of paths.</summary>
    public ScopeDecision Evaluate(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var violations = new List<ScopeViolation>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var path = Normalise(raw);

            var denied = Deny.FirstOrDefault(pattern => GlobPattern.IsMatch(pattern, path));
            if (denied is not null)
            {
                violations.Add(new ScopeViolation(
                    raw.Trim(),
                    denied,
                    ScopeViolationReason.Denied,
                    DescribeArea(path, denied)));
                continue;
            }

            if (!Allow.Any(pattern => GlobPattern.IsMatch(pattern, path)))
            {
                violations.Add(new ScopeViolation(
                    raw.Trim(),
                    null,
                    ScopeViolationReason.OutsideAllowList,
                    DescribeArea(path, pattern: null)));
            }
        }

        return violations.Count == 0 ? ScopeDecision.Allowed : ScopeDecision.Refused(violations);
    }

    /// <summary>The deny list as prompt text, so the refiner does not propose these in the first place.</summary>
    public string ToPromptText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Areas of this codebase Charter may change:");
        if (Allow.Count == 0)
        {
            builder.AppendLine("- (none configured — refuse every change and route to an engineer)");
        }
        else
        {
            foreach (var pattern in Allow)
            {
                builder.Append("- ").AppendLine(pattern);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Areas Charter may never change. Never propose a spec that touches these;");
        builder.AppendLine("refuse instead and say an engineer will pick it up:");
        if (Deny.Count == 0)
        {
            builder.AppendLine("- (none configured)");
        }
        else
        {
            foreach (var pattern in Deny)
            {
                builder.Append("- ").AppendLine(pattern);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string Normalise(string path) => GlobPattern.Normalise(path);

    private static string DescribeArea(string path, string? pattern)
    {
        var haystack = (path + " " + (pattern ?? string.Empty)).ToLowerInvariant();
        foreach (var (fragment, area) in AreaNames)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal))
            {
                return area;
            }
        }

        return "a part of the codebase that only engineers may change";
    }
}

/// <summary>
/// Glob matching for the <c>scopes</c> patterns of section 8: <c>**</c> crosses directories,
/// <c>*</c> does not, <c>?</c> is one character.
/// </summary>
public static class GlobPattern
{
    private static readonly Dictionary<string, Regex> Cache = [];
    private static readonly Lock Gate = new();

    /// <summary>Whether a path matches a pattern.</summary>
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);

        var normalisedPath = Normalise(path);
        var regex = Compile(pattern.Replace('\\', '/').Trim());

        if (regex.IsMatch(normalisedPath))
        {
            return true;
        }

        // A directory pattern covers everything under it, and a spec that names the directory
        // itself is naming everything under it too.
        return !normalisedPath.EndsWith('/') && regex.IsMatch(normalisedPath + "/x");
    }

    /// <summary>Normalises a path: forward slashes, no leading <c>./</c> and no leading slash.</summary>
    internal static string Normalise(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.TrimStart('/');
    }

    private static Regex Compile(string pattern)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            var regex = new Regex(
                Translate(pattern),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(500));

            Cache[pattern] = regex;
            return regex;
        }
    }

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");
        var index = 0;

        while (index < pattern.Length)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*' when index + 1 < pattern.Length && pattern[index + 1] == '*':
                    // `**/` matches zero or more directories; a trailing `**` matches everything.
                    if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        index += 3;
                    }
                    else
                    {
                        builder.Append(".*");
                        index += 2;
                    }

                    break;

                case '*':
                    builder.Append("[^/]*");
                    index++;
                    break;

                case '?':
                    builder.Append("[^/]");
                    index++;
                    break;

                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    index++;
                    break;
            }
        }

        return builder.Append('$').ToString();
    }
}
