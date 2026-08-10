using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Covers the adapter schema of section 12b and the extensibility rules of section 8: a required
/// version, unknown keys that warn rather than fail, and validation failures that name the file and
/// the field so an adapter author can fix them without guessing.
/// </summary>
public class AdapterYamlLoaderTests
{
    [Fact]
    public void ParsesTheWorkedExampleFromTheSpec()
    {
        var adapter = AdapterTestFiles.Load(AdapterTestFiles.ValidYaml, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal("example", adapter.Id);
        Assert.Equal("Example", adapter.DisplayName);
        Assert.Equal(1, adapter.Version);
        Assert.Equal("example --version", adapter.Install.Check);
        Assert.Equal(["example", "--print", "--output-format", "jsonl"], adapter.Invoke.Command);
        Assert.Equal(AdapterPromptDelivery.Stdin, adapter.Invoke.PromptDelivery);
        Assert.Equal(AdapterEventFormat.Jsonl, adapter.Events.Format);
        Assert.Equal(3, adapter.Events.Map.Count);
        Assert.Equal(
            [AdapterCapability.Steering, AdapterCapability.Resume, AdapterCapability.CostReporting],
            adapter.Capabilities);
        Assert.True(adapter.TryGetEnvironmentVariable("anthropic_api_key", out var env));
        Assert.Equal("ANTHROPIC_API_KEY", env);
    }

    [Fact]
    public void BuildsTheInvocationWithTheModelAndAPromptOnStandardInput()
    {
        var adapter = AdapterTestFiles.Load(AdapterTestFiles.ValidYaml);

        var invocation = adapter.BuildInvocation("add a field", "claude-opus-5");

        Assert.Equal(
            ["example", "--print", "--output-format", "jsonl", "--model", "claude-opus-5"],
            invocation.Arguments);
        Assert.Equal("add a field", invocation.StandardInput);
    }

    [Fact]
    public void BuildsTheInvocationWithAnArgumentDeliveredPrompt()
    {
        var yaml = AdapterTestFiles.ValidYaml
            .Replace("  prompt: stdin", "  prompt: \"--message={prompt}\"", StringComparison.Ordinal)
            .Replace("capabilities: [steering, resume, cost_reporting]", "capabilities: [resume]", StringComparison.Ordinal);

        var invocation = AdapterTestFiles.Load(yaml).BuildInvocation("add a field", "claude-opus-5");

        Assert.Equal("--message=add a field", invocation.Arguments[^1]);
        Assert.Null(invocation.StandardInput);
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8: version
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RejectsAFileWithNoVersion()
    {
        var yaml = AdapterTestFiles.WithLineRemoved("version: 1");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("adapters/example.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("'version'", error.Message, StringComparison.Ordinal);
        Assert.Contains("version 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnknownVersionAndNamesTheSupportedOne()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("version: 1", "version: 2");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("is 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Supported versions: 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsANonNumericVersion()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("version: 1", "version: \"one\"");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'version'", error.Message, StringComparison.Ordinal);
        Assert.Contains("must be an integer", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Section 8: unknown keys warn, never fail
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownTopLevelKeyWarnsAndTheFileStillLoads()
    {
        var yaml = AdapterTestFiles.ValidYaml + Environment.NewLine + "sandbox_profile: \"strict\"";

        var adapter = AdapterTestFiles.Load(yaml, out var warnings);

        Assert.Equal("example", adapter.Id);
        var warning = Assert.Single(warnings);
        Assert.Equal("adapters/example.yml", warning.SourcePath);
        Assert.Equal("sandbox_profile", warning.Field);
        Assert.Contains("ignored", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNestedKeyWarnsWithItsFullPath()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "  prompt: stdin",
            "  prompt: stdin" + Environment.NewLine + "  working_directory: \"/src\"");

        var adapter = AdapterTestFiles.Load(yaml, out var warnings);

        Assert.Equal("example", adapter.Id);
        Assert.Equal("invoke.working_directory", Assert.Single(warnings).Field);
    }

    [Fact]
    public void AnUnknownCredentialKindWarnsAndIsIgnored()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "  anthropic_api_key: { env: \"ANTHROPIC_API_KEY\" }",
            "  anthropic_api_key: { env: \"ANTHROPIC_API_KEY\" }"
            + Environment.NewLine
            + "  future_provider_key: { env: \"FUTURE_API_KEY\" }");

        var adapter = AdapterTestFiles.Load(yaml, out var warnings);

        Assert.Single(adapter.Auth);
        Assert.Equal("auth.future_provider_key", Assert.Single(warnings).Field);
    }

    [Fact]
    public void AnUnknownCapabilityWarnsAndIsIgnored()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "capabilities: [steering, resume, cost_reporting]",
            "capabilities: [steering, resume, cost_reporting, time_travel]");

        var adapter = AdapterTestFiles.Load(yaml, out var warnings);

        Assert.Equal(3, adapter.Capabilities.Count);
        Assert.Contains("time_travel", Assert.Single(warnings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEventTypeWarnsAndIsIgnored()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "    message:    \"$.type == 'assistant'\"",
            "    message:    \"$.type == 'assistant'\""
            + Environment.NewLine
            + "    thinking:   \"$.type == 'thinking'\"");

        var adapter = AdapterTestFiles.Load(yaml, out var warnings);

        Assert.Equal(3, adapter.Events.Map.Count);
        Assert.Equal("events.map.thinking", Assert.Single(warnings).Field);
    }

    // ---------------------------------------------------------------------------------------------
    // Validation: name the file and the field
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("id: example", "id")]
    [InlineData("display_name: \"Example\"", "display_name")]
    [InlineData("  check: \"example --version\"", "install.check")]
    [InlineData("  hint: \"npm install -g example\"", "install.hint")]
    [InlineData("  prompt: stdin", "invoke.prompt")]
    [InlineData("capabilities: [steering, resume, cost_reporting]", "capabilities")]
    public void AMissingRequiredFieldFailsNamingTheFileAndTheField(string line, string field)
    {
        var yaml = AdapterTestFiles.WithLineRemoved(line);

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("adapters/example.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains($"'{field}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingBlockFailsNamingTheBlock()
    {
        var yaml = """
            id: example
            display_name: "Example"
            version: 1
            capabilities: []
            """;

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'install' is required", error.Message, StringComparison.Ordinal);
        Assert.Contains("'invoke' is required", error.Message, StringComparison.Ordinal);
        Assert.Contains("'auth' is required", error.Message, StringComparison.Ordinal);
        Assert.Contains("'events' is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsEveryProblemInOnePass()
    {
        var yaml = """
            id: Example
            display_name: "Example"
            version: 1
            install:
              check: "example --version"
              hint: "npm install -g example"
            invoke:
              command: []
              prompt: "no placeholder here"
            auth:
              anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
            events:
              format: yaml
            capabilities: []
            """;

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Equal(4, error.Problems.Count);
        Assert.All(error.Problems, problem =>
            Assert.StartsWith("adapters/example.yml:", problem, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAPromptThatWouldNeverReachTheAgent()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("  prompt: stdin", "  prompt: \"--message\"");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'invoke.prompt'", error.Message, StringComparison.Ordinal);
        Assert.Contains("{prompt}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAModelArgWithNoPlaceholder()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "model_arg: [\"--model\", \"{model}\"]",
            "model_arg: [\"--model\", \"opus\"]");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'model_arg'", error.Message, StringComparison.Ordinal);
        Assert.Contains("{model}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnsupportedEventFormat()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("  format: jsonl", "  format: ndjson");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'events.format'", error.Message, StringComparison.Ordinal);
        Assert.Contains("jsonl, text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMalformedEventMappingExpressionNamingTheEventType()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "    tool_use:   \"$.type == 'tool_call'\"",
            "    tool_use:   \"$.type ~= 'tool_call'\"");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("adapters/example.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("'events.map.tool_use'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJsonlAdapterMustMapItsEvents()
    {
        var yaml = """
            id: example
            display_name: "Example"
            version: 1
            install:
              check: "example --version"
              hint: "npm install -g example"
            invoke:
              command: ["example"]
              prompt: stdin
            auth:
              anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
            events:
              format: jsonl
            capabilities: []
            """;

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'events.map' is required when 'events.format' is jsonl", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextAdapterCannotMapEvents()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("  format: jsonl", "  format: text");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'events.map'", error.Message, StringComparison.Ordinal);
        Assert.Contains("no structured lines to match", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // A capability the invocation cannot support
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RejectsCostReportingOnATextAdapter()
    {
        // Section 12b: pretending parity is worse than documenting the degraded experience.
        var yaml = """
            id: example
            display_name: "Example"
            version: 1
            install:
              check: "example --version"
              hint: "npm install -g example"
            invoke:
              command: ["example"]
              prompt: stdin
            auth:
              anthropic_api_key: { env: "ANTHROPIC_API_KEY" }
            events:
              format: text
            capabilities: [cost_reporting]
            """;

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'capabilities'", error.Message, StringComparison.Ordinal);
        Assert.Contains("cost_reporting", error.Message, StringComparison.Ordinal);
        Assert.Contains("events.format", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSteeringWhenThePromptIsAnArgument()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("  prompt: stdin", "  prompt: \"{prompt}\"");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'capabilities'", error.Message, StringComparison.Ordinal);
        Assert.Contains("steering", error.Message, StringComparison.Ordinal);
        Assert.Contains("invoke.prompt: stdin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnIdThatIsNotLowercaseAndHyphenated()
    {
        var yaml = AdapterTestFiles.WithLineReplaced("id: example", "id: Example_Agent");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("'id'", error.Message, StringComparison.Ordinal);
        Assert.Contains("lowercase and hyphenated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAFileThatIsNotYaml()
    {
        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterYamlLoader.Load("adapters/broken.yml", "id: [unclosed", []));

        Assert.Contains("adapters/broken.yml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAFileThatIsNotAMapping()
    {
        var error = Assert.Throws<AdapterLoadException>(
            () => AdapterYamlLoader.Load("adapters/list.yml", "- one\n- two\n", []));

        Assert.Contains("adapters/list.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("a single YAML mapping", error.Message, StringComparison.Ordinal);
    }
}
