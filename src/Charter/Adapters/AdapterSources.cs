namespace Charter.Adapters;

/// <summary>
/// The directories adapter files are loaded from, in precedence order.
/// </summary>
/// <remarks>
/// <para>
/// Section 12b requires that an operator can drop a local adapter into their instance without
/// forking, but does not name a directory. Charter's choice, which docs/adapters.md documents:
/// </para>
/// <list type="number">
/// <item><description>The in-tree <c>adapters/</c> directory, which ships with Charter. Found by
/// walking up from the running assembly and the working directory, so it works from
/// <c>dotnet run</c>, from a published output, and from a test.</description></item>
/// <item><description>Every directory listed in <c>CHARTER_ADAPTERS_PATH</c>, separated by the
/// platform path separator (<c>:</c> on Linux and macOS), in the order given.</description></item>
/// </list>
/// <para>
/// <strong>Later wins, by <c>id</c>.</strong> A file in a <c>CHARTER_ADAPTERS_PATH</c> directory that
/// declares an <c>id</c> a shipped adapter already uses replaces it wholesale; a file with a new
/// <c>id</c> adds an adapter. Two files in the <em>same</em> directory claiming one <c>id</c> is an
/// error naming both files — inside one directory there is no defensible winner, and picking one
/// silently is how an operator ends up debugging an adapter that is not the one they edited.
/// </para>
/// <para>
/// A local directory that does not exist is a configuration error, not something to skip quietly: an
/// operator who mistypes a mount path should be told, not left wondering why their override does
/// nothing.
/// </para>
/// </remarks>
public sealed record AdapterSources(IReadOnlyList<string> Directories)
{
    /// <summary>Environment variable holding extra adapter directories, highest precedence last.</summary>
    public const string PathVariable = "CHARTER_ADAPTERS_PATH";

    /// <summary>The name of the in-tree directory shipped adapters live in.</summary>
    public const string BuiltInDirectoryName = "adapters";

    /// <summary>Resolves the built-in directory plus anything in <see cref="PathVariable"/>.</summary>
    public static AdapterSources FromEnvironment(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        var directories = new List<string>();

        var builtIn = FindBuiltInDirectory();
        if (builtIn is not null)
        {
            directories.Add(builtIn);
        }

        var raw = environment(PathVariable);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = entry.Trim();
                if (trimmed.Length > 0)
                {
                    directories.Add(Path.GetFullPath(trimmed));
                }
            }
        }

        if (directories.Count == 0)
        {
            throw new AdapterLoadException(
                $"No adapter directory was found. Charter looks for an '{BuiltInDirectoryName}' directory "
                + $"beside the application and in its parent directories, and at every path in "
                + $"{PathVariable}. Set {PathVariable} to the directory holding your adapter YAML files.");
        }

        return new AdapterSources(directories);
    }

    /// <summary>
    /// Walks up from the running assembly and the working directory looking for the shipped
    /// <c>adapters/</c> directory. Returns <see langword="null"/> when there is none.
    /// </summary>
    public static string? FindBuiltInDirectory()
        => FindBuiltInDirectory(AppContext.BaseDirectory) ?? FindBuiltInDirectory(Directory.GetCurrentDirectory());

    /// <summary>Walks up from <paramref name="startDirectory"/> looking for <c>adapters/</c>.</summary>
    public static string? FindBuiltInDirectory(string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, BuiltInDirectoryName);
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.yml").Any())
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
