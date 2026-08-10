using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Guards the fixture every other adapter test mutates. If the baseline stops being valid, the
/// "now break one field" tests start passing for the wrong reason.
/// </summary>
public class AdapterFixtureTests
{
    [Fact]
    public void TheBaselineFixtureIsAValidAdapter()
    {
        var adapter = AdapterTestFiles.Load(AdapterTestFiles.ValidYaml, out var warnings);

        Assert.Equal("example", adapter.Id);
        Assert.Empty(warnings);
    }
}

/// <summary>Shared fixtures for the adapter tests: a valid file to mutate, and a scratch directory.</summary>
internal static class AdapterTestFiles
{
    /// <summary>A minimal but complete adapter, used as the base for "now break one field" tests.</summary>
    public const string ValidYaml = """
        id: example
        display_name: "Example"
        version: 1
        install:
          check: "example --version"
          hint: "npm install -g example"
        invoke:
          command: ["example", "--print", "--output-format", "jsonl"]
          prompt: stdin
        auth:
          anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
        model_arg: ["--model", "{model}"]
        events:
          format: jsonl
          map:
            tool_use:   "$.type == 'tool_call'"
            file_write: "$.tool == 'edit' || $.tool == 'write'"
            message:    "$.type == 'assistant'"
        capabilities: [steering, resume, cost_reporting]
        """;

    /// <summary>Replaces one line of <see cref="ValidYaml"/>, so a test changes exactly one thing.</summary>
    public static string WithLineReplaced(string original, string replacement)
    {
        var yaml = ValidYaml;
        Assert.Contains(original, yaml, StringComparison.Ordinal);
        return yaml.Replace(original, replacement, StringComparison.Ordinal);
    }

    public static string WithLineRemoved(string line)
    {
        var lines = ValidYaml.Split('\n');
        var kept = lines.Where(candidate => !string.Equals(candidate.TrimEnd('\r'), line, StringComparison.Ordinal));
        Assert.NotEqual(lines.Length, kept.Count());
        return string.Join('\n', kept);
    }

    public static AdapterDocument Load(string yaml, out List<AdapterWarning> warnings)
    {
        warnings = [];
        return AdapterYamlLoader.Load("adapters/example.yml", yaml, warnings);
    }

    public static AdapterDocument Load(string yaml)
        => AdapterYamlLoader.Load("adapters/example.yml", yaml, []);

    /// <summary>The in-tree <c>adapters/</c> directory that ships with Charter.</summary>
    public static string ShippedDirectory
        => AdapterSources.FindBuiltInDirectory(AppContext.BaseDirectory)
           ?? throw new DirectoryNotFoundException(
               $"Could not find the shipped adapters directory from {AppContext.BaseDirectory}.");
}

/// <summary>A temporary directory that deletes itself, for the loading-precedence tests.</summary>
internal sealed class AdapterScratchDirectory : IDisposable
{
    public AdapterScratchDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "charter-adapter-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string content)
    {
        var file = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
