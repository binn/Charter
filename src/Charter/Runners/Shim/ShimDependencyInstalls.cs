namespace Charter.Runners.Shim;

/// <summary>One dependency install the shim will run, and what makes it safe.</summary>
/// <param name="Ecosystem">Which package manager this is, for the transcript.</param>
/// <param name="Command">The executable.</param>
/// <param name="Arguments">Argument vector. No shell is involved, so nothing here is quoted.</param>
/// <param name="Lockfile">The lockfile that pinned it, relative to the workspace.</param>
/// <param name="LockfileEnforced">
/// True when the command cannot resolve a fresh version. False is reported as a warning rather than
/// silently accepted (section 16.2).
/// </param>
/// <param name="InstallScriptsDisabled">True when postinstall/prepare hooks cannot run.</param>
public sealed record ShimInstallStep(
    string Ecosystem,
    string Command,
    IReadOnlyList<string> Arguments,
    string Lockfile,
    bool LockfileEnforced,
    bool InstallScriptsDisabled)
{
    /// <summary>How the step reads in a transcript event.</summary>
    public string Display => $"{Command} {string.Join(' ', Arguments)}";
}

/// <summary>What the shim decided to install, and anything an engineer should know about it.</summary>
public sealed record ShimInstallPlan(IReadOnlyList<ShimInstallStep> Steps, IReadOnlyList<string> Warnings);

/// <summary>
/// Dependency installation, under the rules section 16.2 states plainly.
/// </summary>
/// <remarks>
/// <para>
/// Locked toolchains (section 32.1) close the <em>tooling</em> vector; they do nothing about the
/// repository's own dependencies, and the docs must not imply otherwise. What this class does about
/// that is exactly what section 16.2 lists: lockfile-only installs, never a fresh resolve, and
/// install scripts disabled by default.
/// </para>
/// <para>
/// Install scripts are opt-in per repository and per ecosystem, never globally, because "this repo
/// genuinely needs a postinstall" is a decision an engineer makes about one codebase after looking at
/// it. When a repository opts in, the transcript says so, and the engineer recap ranks dependency
/// changes alongside auth and migrations (section 14).
/// </para>
/// </remarks>
public static class ShimDependencyInstalls
{
    /// <summary>Detects lockfiles under <paramref name="workspaceRoot"/> and plans the installs.</summary>
    /// <param name="workspaceRoot">The repository checkout.</param>
    /// <param name="allowInstallScripts">
    /// The repository's explicit opt-in. Defaults to false; section 16.2 is unambiguous that this is
    /// the default and that opting in is per repository.
    /// </param>
    /// <param name="fileExists">
    /// How the plan checks for a lockfile. Injected so a test does not need a directory on disk.
    /// </param>
    public static ShimInstallPlan Plan(
        string workspaceRoot,
        bool allowInstallScripts = false,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var exists = fileExists ?? File.Exists;
        bool Present(string relative) => exists(Path.Combine(workspaceRoot, relative));

        var steps = new List<ShimInstallStep>();
        var warnings = new List<string>();

        if (Present("package-lock.json"))
        {
            steps.Add(Npm("npm", "package-lock.json", ["ci"], allowInstallScripts));
        }
        else if (Present("pnpm-lock.yaml"))
        {
            steps.Add(Npm("pnpm", "pnpm-lock.yaml", ["install", "--frozen-lockfile"], allowInstallScripts));
        }
        else if (Present("yarn.lock"))
        {
            steps.Add(Npm("yarn", "yarn.lock", ["install", "--frozen-lockfile"], allowInstallScripts));
        }
        else if (Present("package.json"))
        {
            warnings.Add(
                "package.json is present but no lockfile is committed, so dependencies cannot be "
                + "installed without resolving fresh versions. Commit package-lock.json, pnpm-lock.yaml "
                + "or yarn.lock; section 16.2 requires lockfile-only installs.");
        }

        if (Present("packages.lock.json"))
        {
            steps.Add(new ShimInstallStep(
                "nuget",
                "dotnet",
                ["restore", "--locked-mode"],
                "packages.lock.json",
                LockfileEnforced: true,
                InstallScriptsDisabled: true));
        }

        if (Present("Cargo.lock"))
        {
            steps.Add(new ShimInstallStep(
                "cargo",
                "cargo",
                ["fetch", "--locked"],
                "Cargo.lock",
                LockfileEnforced: true,
                InstallScriptsDisabled: false));
        }

        if (Present("go.sum"))
        {
            steps.Add(new ShimInstallStep(
                "go",
                "go",
                ["mod", "download"],
                "go.sum",
                LockfileEnforced: true,
                InstallScriptsDisabled: true));
        }

        if (Present("uv.lock"))
        {
            steps.Add(new ShimInstallStep(
                "uv",
                "uv",
                ["sync", "--frozen"],
                "uv.lock",
                LockfileEnforced: true,
                InstallScriptsDisabled: true));
        }

        if (allowInstallScripts && steps.Any(step => !step.InstallScriptsDisabled))
        {
            warnings.Add(
                "This repository has opted in to running dependency install scripts. A compromised "
                + "transitive dependency can execute code in the session sandbox (section 16.2).");
        }

        return new ShimInstallPlan(steps, warnings);
    }

    private static ShimInstallStep Npm(
        string command,
        string lockfile,
        IReadOnlyList<string> arguments,
        bool allowInstallScripts)
    {
        var vector = allowInstallScripts ? arguments : [.. arguments, "--ignore-scripts"];

        return new ShimInstallStep(
            command,
            command,
            vector,
            lockfile,
            LockfileEnforced: true,
            InstallScriptsDisabled: !allowInstallScripts);
    }
}
