using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Covers loading precedence (section 12b): built-in adapters ship in-tree, and an operator's local
/// files add to or override them by <c>id</c> without anyone forking Charter.
/// </summary>
public class AdapterCatalogTests
{
    private static string AdapterYaml(string id, string displayName) => $$"""
        id: {{id}}
        display_name: "{{displayName}}"
        version: 1
        install:
          check: "{{id}} --version"
          hint: "npm install -g {{id}}"
        invoke:
          command: ["{{id}}"]
          prompt: stdin
        auth:
          anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
        events:
          format: text
        capabilities: []
        """;

    [Fact]
    public void LoadsEveryAdapterInADirectory()
    {
        using var directory = new AdapterScratchDirectory();
        directory.Write("one.yml", AdapterYaml("one", "One"));
        directory.Write("two.yaml", AdapterYaml("two", "Two"));
        directory.Write("notes.md", "not an adapter");

        var catalog = AdapterCatalog.Load(new AdapterSources([directory.Path]));

        Assert.Equal(["one", "two"], catalog.Adapters.Select(adapter => adapter.Id));
        Assert.Empty(catalog.Overrides);
    }

    [Fact]
    public void ALocalDirectoryOverridesAShippedAdapterById()
    {
        using var shipped = new AdapterScratchDirectory();
        using var local = new AdapterScratchDirectory();
        shipped.Write("pi.yml", AdapterYaml("pi", "Pi"));
        var localFile = local.Write("pi.yml", AdapterYaml("pi", "Pi, patched locally"));

        var catalog = AdapterCatalog.Load(new AdapterSources([shipped.Path, local.Path]));

        var adapter = Assert.Single(catalog.Adapters);
        Assert.Equal("Pi, patched locally", adapter.DisplayName);
        Assert.Equal(localFile, adapter.SourcePath);

        var replaced = Assert.Single(catalog.Overrides);
        Assert.Equal("pi", replaced.Id);
        Assert.Equal(localFile, replaced.SourcePath);
    }

    [Fact]
    public void ALocalDirectoryCanAddAnAdapterWithoutForking()
    {
        using var shipped = new AdapterScratchDirectory();
        using var local = new AdapterScratchDirectory();
        shipped.Write("pi.yml", AdapterYaml("pi", "Pi"));
        local.Write("in-house.yml", AdapterYaml("in-house", "In-house agent"));

        var catalog = AdapterCatalog.Load(new AdapterSources([shipped.Path, local.Path]));

        Assert.Equal(["in-house", "pi"], catalog.Adapters.Select(adapter => adapter.Id));
        Assert.Empty(catalog.Overrides);
    }

    [Fact]
    public void TwoFilesInOneDirectoryClaimingOneIdIsAnErrorNamingBoth()
    {
        using var directory = new AdapterScratchDirectory();
        var first = directory.Write("a-pi.yml", AdapterYaml("pi", "Pi"));
        var second = directory.Write("b-pi.yml", AdapterYaml("pi", "Also Pi"));

        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterCatalog.Load(new AdapterSources([directory.Path])));

        Assert.Contains(first, error.Message, StringComparison.Ordinal);
        Assert.Contains(second, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidFileFailsTheWholeLoadNamingTheFileAndTheField()
    {
        using var directory = new AdapterScratchDirectory();
        directory.Write("good.yml", AdapterYaml("good", "Good"));
        var broken = directory.Write("broken.yml", AdapterYaml("broken", "Broken").Replace("version: 1", string.Empty, StringComparison.Ordinal));

        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterCatalog.Load(new AdapterSources([directory.Path])));

        Assert.Contains(broken, error.Message, StringComparison.Ordinal);
        Assert.Contains("'version'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingLocalDirectoryIsAnErrorRatherThanASilentNoOp()
    {
        using var shipped = new AdapterScratchDirectory();
        shipped.Write("pi.yml", AdapterYaml("pi", "Pi"));
        var missing = Path.Combine(shipped.Path, "does-not-exist");

        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterCatalog.Load(new AdapterSources([shipped.Path, missing])));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Contains(AdapterSources.PathVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyDirectorySetIsAnErrorRatherThanAnInstanceThatCannotDispatch()
    {
        using var directory = new AdapterScratchDirectory();

        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterCatalog.Load(new AdapterSources([directory.Path])));

        Assert.Contains("No adapter files were found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningsFromEveryFileAreCollectedRatherThanSwallowed()
    {
        using var directory = new AdapterScratchDirectory();
        directory.Write("one.yml", AdapterYaml("one", "One") + "\nfuture_key: true\n");
        directory.Write("two.yml", AdapterYaml("two", "Two") + "\nanother_future_key: true\n");

        var catalog = AdapterCatalog.Load(new AdapterSources([directory.Path]));

        Assert.Equal(2, catalog.Adapters.Count);
        Assert.Equal(2, catalog.Warnings.Count);
        Assert.Contains(catalog.Warnings, warning => warning.Field == "future_key");
        Assert.Contains(catalog.Warnings, warning => warning.Field == "another_future_key");
    }

    [Fact]
    public void GetNamesTheAvailableAdaptersWhenAnIdIsUnknown()
    {
        using var directory = new AdapterScratchDirectory();
        directory.Write("one.yml", AdapterYaml("one", "One"));

        var catalog = AdapterCatalog.Load(new AdapterSources([directory.Path]));

        Assert.False(catalog.TryGet("nope", out _));
        var error = Assert.Throws<AdapterLoadException>(() => catalog.Get("nope"));
        Assert.Contains("Available adapters: one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesTheShippedDirectoryPlusEveryDirectoryInTheEnvironmentVariable()
    {
        using var local = new AdapterScratchDirectory();
        using var other = new AdapterScratchDirectory();

        var sources = AdapterSources.FromEnvironment(name => name == AdapterSources.PathVariable
            ? string.Join(Path.PathSeparator, local.Path, other.Path)
            : null);

        Assert.Equal(3, sources.Directories.Count);
        Assert.Equal(AdapterTestFiles.ShippedDirectory, sources.Directories[0]);
        Assert.Equal(local.Path, sources.Directories[1]);
        Assert.Equal(other.Path, sources.Directories[2]);
    }

    [Fact]
    public void ResolvesOnlyTheShippedDirectoryWhenNothingIsConfigured()
    {
        var sources = AdapterSources.FromEnvironment(_ => null);

        Assert.Equal([AdapterTestFiles.ShippedDirectory], sources.Directories);
    }
}
