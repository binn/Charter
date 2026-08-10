using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Every adapter Charter ships in <c>adapters/</c> parses, validates, and describes itself honestly
/// (section 12b).
/// </summary>
/// <remarks>
/// Adapters are data, and data with no test is data that is wrong the first time someone edits it.
/// These are the tests a contributor adding an eighth adapter runs before opening the pull request.
/// </remarks>
public class AdapterShippedFilesTests
{
    private static AdapterCatalog Catalog()
        => AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

    public static TheoryData<string> ShippedIds { get; } =
        new("claude-code", "codex", "gemini-cli", "opencode", "pi", "cursor-agent", "aider");

    [Fact]
    public void EverySpecifiedAdapterIsShippedAndNothingElseIs()
    {
        var catalog = Catalog();

        Assert.Equal(
            ["aider", "claude-code", "codex", "cursor-agent", "gemini-cli", "opencode", "pi"],
            catalog.Adapters.Select(adapter => adapter.Id));
    }

    [Fact]
    public void EveryShippedAdapterLoadsWithoutAWarning()
    {
        // A shipped adapter that warns is a shipped adapter with a typo in a key name.
        var catalog = Catalog();

        Assert.Empty(catalog.Warnings);
        Assert.Empty(catalog.Overrides);
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EachShippedAdapterIsComplete(string id)
    {
        var adapter = Catalog().Get(id);

        Assert.Equal(AdapterYamlLoader.SupportedVersion, adapter.Version);
        Assert.False(string.IsNullOrWhiteSpace(adapter.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(adapter.Install.Check));
        Assert.False(string.IsNullOrWhiteSpace(adapter.Install.Hint));
        Assert.NotEmpty(adapter.Invoke.Command);
        Assert.NotEmpty(adapter.Auth);
        Assert.Contains(AdapterDocument.ModelPlaceholder, string.Join(" ", adapter.ModelArg), StringComparison.Ordinal);
        Assert.EndsWith(
            $"{id}.yml",
            adapter.SourcePath,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EachShippedAdapterProducesARunnableInvocation(string id)
    {
        var adapter = Catalog().Get(id);

        var invocation = adapter.BuildInvocation("implement the spec", "claude-opus-5");

        Assert.NotEmpty(invocation.Arguments);
        Assert.All(invocation.Arguments, argument => Assert.False(string.IsNullOrEmpty(argument)));
        Assert.DoesNotContain(
            invocation.Arguments,
            argument => argument.Contains(AdapterDocument.ModelPlaceholder, StringComparison.Ordinal)
                        || argument.Contains(AdapterDocument.PromptPlaceholder, StringComparison.Ordinal));

        if (adapter.Invoke.PromptDelivery == AdapterPromptDelivery.Stdin)
        {
            Assert.Equal("implement the spec", invocation.StandardInput);
        }
        else
        {
            Assert.Null(invocation.StandardInput);
            Assert.Contains(
                invocation.Arguments,
                argument => argument.Contains("implement the spec", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EachShippedAdapterOnlyClaimsCapabilitiesItsInvocationCanSupport(string id)
    {
        var adapter = Catalog().Get(id);

        if (adapter.Supports(AdapterCapability.CostReporting))
        {
            Assert.Equal(AdapterEventFormat.Jsonl, adapter.Events.Format);
        }

        if (adapter.Supports(AdapterCapability.Steering))
        {
            Assert.Equal(AdapterPromptDelivery.Stdin, adapter.Invoke.PromptDelivery);
        }
    }

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EachShippedAdapterNamesOnlyCredentialKindsCharterKnows(string id)
    {
        var adapter = Catalog().Get(id);

        Assert.All(adapter.CredentialKinds, kind => Assert.True(
            AdapterCredentialKinds.IsKnown(kind),
            $"{id} declares the unknown credential kind '{kind}'."));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    [InlineData("cursor-agent")]
    [InlineData("pi")]
    public void StructuredAdaptersMapAllThreeEventTypes(string id)
    {
        var adapter = Catalog().Get(id);

        Assert.Equal(AdapterEventFormat.Jsonl, adapter.Events.Format);
        Assert.Equal(
            [AdapterEventType.Message, AdapterEventType.ToolUse, AdapterEventType.FileWrite],
            adapter.Events.Map.Select(mapping => mapping.EventType).Order());
    }

    [Theory]
    [InlineData("aider")]
    [InlineData("gemini-cli")]
    [InlineData("opencode")]
    public void TextAdaptersDeclareTheDegradedExperienceRatherThanPretendingParity(string id)
    {
        // Section 12b: mark such adapters events.format: text and document the degraded experience
        // rather than pretending parity. Cost reporting in particular must not be claimed.
        var adapter = Catalog().Get(id);

        Assert.Equal(AdapterEventFormat.Text, adapter.Events.Format);
        Assert.False(adapter.IsStructured);
        Assert.Empty(adapter.Events.Map);
        Assert.DoesNotContain(AdapterCapability.CostReporting, adapter.Capabilities);
    }

    [Fact]
    public void PiMatchesTheWorkedExampleInTheSpec()
    {
        var pi = Catalog().Get("pi");

        Assert.Equal(["pi", "--print", "--output-format", "jsonl"], pi.Invoke.Command);
        Assert.Equal(AdapterPromptDelivery.Stdin, pi.Invoke.PromptDelivery);
        Assert.Equal(["--model", "{model}"], pi.ModelArg);
        Assert.Equal(
            ["anthropic_api_key", "openai_api_key", "openrouter_key", "google_api_key", "xai_api_key"],
            pi.CredentialKinds);
        Assert.Equal(
            [AdapterCapability.Steering, AdapterCapability.Resume, AdapterCapability.CostReporting],
            pi.Capabilities);
    }

    [Fact]
    public void ClaudeCodeClassifiesItsOwnStreamJsonOutput()
    {
        var classifier = new AdapterEventClassifier(Catalog().Get("claude-code"));

        var write = classifier.Classify(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{}}]}}""");
        Assert.Equal([AdapterEventType.FileWrite, AdapterEventType.ToolUse], write.Matches);

        var read = classifier.Classify(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{}}]}}""");
        Assert.Equal([AdapterEventType.ToolUse], read.Matches);

        var text = classifier.Classify(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Adding the field now."}]}}""");
        Assert.Equal([AdapterEventType.Message], text.Matches);

        var result = classifier.Classify("""{"type":"result","subtype":"success","total_cost_usd":0.42}""");
        Assert.Equal(AdapterLineKind.Unmatched, result.Kind);
    }

    [Fact]
    public void PiClassifiesItsOwnJsonlOutput()
    {
        var classifier = new AdapterEventClassifier(Catalog().Get("pi"));

        Assert.Equal(
            [AdapterEventType.FileWrite, AdapterEventType.ToolUse],
            classifier.Classify("""{"type":"tool_call","tool":"write","path":"src/App.tsx"}""").Matches);
        Assert.Equal(
            [AdapterEventType.ToolUse],
            classifier.Classify("""{"type":"tool_call","tool":"bash"}""").Matches);
        Assert.Equal(
            [AdapterEventType.Message],
            classifier.Classify("""{"type":"assistant","text":"Done."}""").Matches);
    }

    [Fact]
    public void CodexClassifiesItsOwnJsonlOutput()
    {
        var classifier = new AdapterEventClassifier(Catalog().Get("codex"));

        Assert.Equal(
            [AdapterEventType.FileWrite, AdapterEventType.ToolUse],
            classifier.Classify("""{"id":"3","msg":{"type":"patch_apply_begin","auto_approved":true}}""").Matches);
        Assert.Equal(
            [AdapterEventType.ToolUse],
            classifier.Classify("""{"id":"2","msg":{"type":"exec_command_begin","command":["ls"]}}""").Matches);
        Assert.Equal(
            [AdapterEventType.Message],
            classifier.Classify("""{"id":"1","msg":{"type":"agent_message","message":"Working on it."}}""").Matches);
    }
}
