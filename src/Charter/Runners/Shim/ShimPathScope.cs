namespace Charter.Runners.Shim;

/// <summary>Why a write was refused, in the words the transcript will carry.</summary>
public enum PathScopeRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>The path resolves outside the session workspace — <c>../</c>, or an absolute path.</summary>
    OutsideWorkspace,

    /// <summary>Matched a pattern in <c>scopes.deny</c>.</summary>
    Denied,

    /// <summary>Matched nothing in a non-empty <c>scopes.allow</c>.</summary>
    NotAllowed,

    /// <summary>Matched a rule no repository can switch off — the agent's own guardrails, or <c>.git</c>.</summary>
    AlwaysDenied,
}

/// <summary>One scope decision about one path.</summary>
public sealed record PathScopeDecision(
    bool Allowed,
    string Path,
    PathScopeRefusal Refusal,
    string? Pattern,
    string? Explanation)
{
    public static PathScopeDecision Allow(string path) => new(true, path, PathScopeRefusal.None, null, null);
}

/// <summary>
/// Path scope, enforced where section 7.3 says it must be: in the runner, not in the UI, so a
/// compromised session cannot widen it.
/// </summary>
/// <remarks>
/// <para>
/// The control plane sends the scope on the dispatch and the shim enforces it on every write the
/// agent reports. Both halves matter. Sending it without enforcing it makes the scope advisory;
/// enforcing it in the UI makes it a rendering decision, which is not a guardrail at all.
/// </para>
/// <para>
/// Order of evaluation, strictest first: workspace containment, the non-negotiable deny list, the
/// repository's own deny list, then the allow list. Deny always beats allow — a repository can only
/// ever tighten (section 7.5's composition rule), and an allow entry that overlapped a deny entry
/// would be a way to loosen one.
/// </para>
/// </remarks>
public sealed class ShimPathScope
{
    /// <summary>
    /// Patterns no <c>.charter/config.yml</c> can switch off.
    /// </summary>
    /// <remarks>
    /// A session that can rewrite <c>.charter/config.yml</c> or <c>.charter/policies/</c> can widen its
    /// own path scope and reclassify its own migrations, which is the whole guardrail model gone in
    /// one file write (sections 8, 15). A session that can write <c>.git/</c> can rewrite history
    /// under the branch protection that section 7.4 depends on. Both are refused unconditionally, and
    /// the refusal is surfaced as an event rather than silently dropped, because an agent trying it is
    /// worth an engineer's attention.
    /// </remarks>
    public static IReadOnlyList<string> AlwaysDenied { get; } =
    [
        ".git/**",
        ".charter/config.yml",
        ".charter/config.yaml",
        ".charter/policies/**",
    ];

    private readonly string _workspaceRoot;
    private readonly IReadOnlyList<string> _allow;
    private readonly IReadOnlyList<string> _deny;

    private ShimPathScope(string workspaceRoot, IReadOnlyList<string> allow, IReadOnlyList<string> deny)
    {
        _workspaceRoot = workspaceRoot;
        _allow = allow;
        _deny = deny;
    }

    /// <summary>The repository's allow list. Empty means the whole workspace.</summary>
    public IReadOnlyList<string> Allow => _allow;

    /// <summary>The repository's deny list, which always beats the allow list.</summary>
    public IReadOnlyList<string> Deny => _deny;

    /// <summary>The absolute directory every path must resolve inside.</summary>
    public string WorkspaceRoot => _workspaceRoot;

    public static ShimPathScope Create(
        string workspaceRoot,
        IReadOnlyList<string>? allow = null,
        IReadOnlyList<string>? deny = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        return new ShimPathScope(
            NormalizeRoot(workspaceRoot),
            [.. (allow ?? []).Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0)],
            [.. (deny ?? []).Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0)]);
    }

    /// <summary>Decides one path. Anything not clearly inside the scope is refused.</summary>
    public PathScopeDecision Evaluate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryResolveInsideWorkspace(path, out var relative))
        {
            return new PathScopeDecision(
                false,
                path,
                PathScopeRefusal.OutsideWorkspace,
                null,
                $"'{path}' resolves outside the session workspace. A session may only write inside the "
                + "repository checkout it was given.");
        }

        var alwaysDenied = ShimGlob.FirstMatch(AlwaysDenied, relative);
        if (alwaysDenied is not null)
        {
            return new PathScopeDecision(
                false,
                relative,
                PathScopeRefusal.AlwaysDenied,
                alwaysDenied,
                $"'{relative}' is never writable by a session: '{alwaysDenied}' covers the repository's own "
                + "guardrails and git metadata, and no repository configuration can permit it.");
        }

        var denied = ShimGlob.FirstMatch(_deny, relative);
        if (denied is not null)
        {
            return new PathScopeDecision(
                false,
                relative,
                PathScopeRefusal.Denied,
                denied,
                $"'{relative}' is denied by the path scope in .charter/config.yml (matched '{denied}').");
        }

        if (_allow.Count > 0 && !ShimGlob.MatchesAny(_allow, relative))
        {
            return new PathScopeDecision(
                false,
                relative,
                PathScopeRefusal.NotAllowed,
                null,
                $"'{relative}' is outside the path scope this session was dispatched with. Allowed: "
                + $"{string.Join(", ", _allow)}.");
        }

        return PathScopeDecision.Allow(relative);
    }

    /// <summary>Convenience for a caller that only cares whether the write may happen.</summary>
    public bool Permits(string path) => Evaluate(path).Allowed;

    /// <summary>
    /// Resolves a reported path against the workspace and refuses anything that escapes it.
    /// </summary>
    /// <remarks>
    /// The agent reports paths; the shim does not trust them. Absolute paths outside the workspace,
    /// <c>..</c> traversal, and a path that resolves through a symlink pointing out of the checkout
    /// all end up outside the root once resolved, and all are refused by the same containment test.
    /// </remarks>
    private bool TryResolveInsideWorkspace(string path, out string relative)
    {
        relative = string.Empty;

        var candidate = path.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(
                Path.IsPathRooted(candidate) ? candidate : Path.Combine(_workspaceRoot, candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = _workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _workspaceRoot
            : _workspaceRoot + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(root, PathComparison))
        {
            return false;
        }

        relative = ShimGlob.NormalizePath(resolved[root.Length..]);
        return relative.Length > 0;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string NormalizeRoot(string workspaceRoot)
    {
        var full = Path.GetFullPath(workspaceRoot);
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }
}
