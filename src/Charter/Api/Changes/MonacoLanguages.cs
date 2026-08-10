namespace Charter.Api.Changes;

/// <summary>
/// Path to Monaco language id, resolved on the server (section 12).
/// </summary>
/// <remarks>
/// Resolved here rather than in the client for the same reason the change request's noun is
/// (change spec 001 part A.2): one table, one answer, and a file type added to it does not need a
/// front-end release. Anything unrecognised is <c>plaintext</c>, which renders correctly and simply
/// does not colour — never a guess that highlights C# as JavaScript.
/// </remarks>
public static class MonacoLanguages
{
    /// <summary>What Monaco calls "no language in particular".</summary>
    public const string PlainText = "plaintext";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".fs"] = "fsharp",
        [".vb"] = "vb",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".mts"] = "typescript",
        [".cts"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".vue"] = "html",
        [".svelte"] = "html",
        [".json"] = "json",
        [".jsonc"] = "json",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".xml"] = "xml",
        [".csproj"] = "xml",
        [".fsproj"] = "xml",
        [".props"] = "xml",
        [".targets"] = "xml",
        [".config"] = "xml",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".md"] = "markdown",
        [".mdx"] = "markdown",
        [".sql"] = "sql",
        [".sh"] = "shell",
        [".bash"] = "shell",
        [".zsh"] = "shell",
        [".ps1"] = "powershell",
        [".py"] = "python",
        [".rb"] = "ruby",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".swift"] = "swift",
        [".m"] = "objective-c",
        [".mm"] = "objective-c",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".hpp"] = "cpp",
        [".php"] = "php",
        [".toml"] = "ini",
        [".ini"] = "ini",
        [".graphql"] = "graphql",
        [".gql"] = "graphql",
        [".proto"] = "proto",
        [".tf"] = "hcl",
        [".hcl"] = "hcl",
        [".lua"] = "lua",
        [".r"] = "r",
        [".pl"] = "perl",
        [".dart"] = "dart",
        [".scala"] = "scala",
        [".ex"] = "elixir",
        [".exs"] = "elixir",
    };

    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = "dockerfile",
        ["containerfile"] = "dockerfile",
        ["makefile"] = "makefile",
        ["procfile"] = "yaml",
        [".gitignore"] = "ignore",
        [".dockerignore"] = "ignore",
        [".editorconfig"] = "ini",
        [".env"] = "ini",
    };

    /// <summary>The language id for a repository-relative path.</summary>
    public static string For(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalised = path.Replace('\\', '/').Trim();
        var slash = normalised.LastIndexOf('/');
        var fileName = slash < 0 ? normalised : normalised[(slash + 1)..];

        if (fileName.Length == 0)
        {
            return PlainText;
        }

        if (ByFileName.TryGetValue(fileName, out var byName))
        {
            return byName;
        }

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            return PlainText;
        }

        return ByExtension.TryGetValue(fileName[dot..], out var byExtension) ? byExtension : PlainText;
    }
}
