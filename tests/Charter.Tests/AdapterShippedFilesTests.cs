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
    public void PiMatchesItsPublishedCliReference()
    {
        // Checked against pi's own docs (earendil-works/pi, packages/coding-agent/docs/usage.md and
        // json.md): `--mode json` is the event stream, `--print` is a different mode, `--output-format`
        // does not exist, and the message is a positional argument. The file this replaced was the
        // illustrative snippet from spec 12b, and none of those four things were true of it.
        var pi = Catalog().Get("pi");

        Assert.Equal(["pi", "--mode", "json"], pi.Invoke.Command);
        Assert.Equal(AdapterPromptDelivery.Argument, pi.Invoke.PromptDelivery);
        Assert.Equal(["--provider", "{provider}", "--model", "{model}"], pi.ModelArg);
        Assert.Equal(AdapterModelFormat.Bare, pi.ModelFormat);
        Assert.Equal(
            ["anthropic_api_key", "openai_api_key", "openrouter_key", "google_api_key", "xai_api_key"],
            pi.CredentialKinds);

        // Steering is pi's `--mode rpc`, a JSON command protocol Charter's shim does not speak, so it
        // is not claimed. Resume is --continue/--resume/--session; cost is message.usage.cost.total.
        Assert.Equal(
            [AdapterCapability.Resume, AdapterCapability.CostReporting],
            pi.Capabilities);
    }

    [Fact]
    public void PiReachesAnOpenRouterModelWithoutGuessingHowItSplitsTheId()
    {
        // The whole point of shipping pi in Phase 1 (change spec 001): it is the adapter that makes an
        // aggregator model usable for the expensive surface. --provider and --model are separate
        // options, so the nested vendor segment never has to be re-parsed by the CLI.
        var invocation = Catalog().Get("pi").BuildInvocation("build it", "openrouter/deepseek/deepseek-r1");

        Assert.Equal(
            ["pi", "--mode", "json", "--provider", "openrouter", "--model", "deepseek/deepseek-r1", "build it"],
            invocation.Arguments);
    }

    [Fact]
    public void ClaudeCodeDispatchesTheBareModelNameItsHelpDocuments()
    {
        // `claude --help`: an alias or a model's full name. anthropic/claude-opus-5 is neither.
        var invocation = Catalog().Get("claude-code").BuildInvocation("build it", "anthropic/claude-opus-5");

        Assert.Equal(
            ["claude", "--print", "--verbose", "--output-format", "stream-json", "--model", "claude-opus-5"],
            invocation.Arguments);
    }

    [Fact]
    public void ClaudeCodeClaimsOnlyTheCapabilitiesItsHeadlessInvocationCanDeliver()
    {
        // Mid-run steering needs --input-format stream-json, which also turns the stdin prompt into a
        // JSON envelope; the shim writes the spec as text. Section 12b: document the gap, do not
        // pretend parity.
        var claudeCode = Catalog().Get("claude-code");

        Assert.DoesNotContain(AdapterCapability.Steering, claudeCode.Capabilities);
        Assert.Equal(
            [AdapterCapability.Resume, AdapterCapability.CostReporting],
            claudeCode.Capabilities);
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
    public void ClaudeCodeEmitsOneContentBlockPerLineWhichIsWhyIndexZeroIsEnough()
    {
        // A previous pass flagged `$.message.content[0]` as a guess that would miss later blocks in a
        // multi-block turn. Verified against a real `claude --print --verbose --output-format
        // stream-json` run: a turn containing text, a tool call, thinking, and a second tool call is
        // emitted as four assistant lines, each with a single-element content array. These are those
        // lines, and each has to classify on its own.
        var classifier = new AdapterEventClassifier(Catalog().Get("claude-code"));

        string[] turn =
        [
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"I'll list /tmp."}]}}""",
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"Bash"}]}}""",
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":""}]}}""",
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t2","name":"Write"}]}}""",
        ];

        Assert.Equal([AdapterEventType.Message], classifier.Classify(turn[0]).Matches);
        Assert.Equal([AdapterEventType.ToolUse], classifier.Classify(turn[1]).Matches);
        Assert.Equal(AdapterLineKind.Unmatched, classifier.Classify(turn[2]).Kind);
        Assert.Equal([AdapterEventType.FileWrite, AdapterEventType.ToolUse], classifier.Classify(turn[3]).Matches);

        // A tool_result comes back on a `user` line, and must not be read as a file write just because
        // a `name` could appear at that path in some future shape.
        Assert.Equal(
            AdapterLineKind.Unmatched,
            classifier.Classify(
                """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2"}]}}""")
                .Kind);
    }

    [Fact]
    public void PiClassifiesItsOwnJsonlOutput()
    {
        // Shapes taken from pi's json.md and its AgentEvent union: tool_execution_start carries
        // toolName, and message lifecycle events are message_start / message_update / message_end.
        var classifier = new AdapterEventClassifier(Catalog().Get("pi"));

        Assert.Equal(
            [AdapterEventType.FileWrite, AdapterEventType.ToolUse],
            classifier.Classify(
                """{"type":"tool_execution_start","toolCallId":"t1","toolName":"write","args":{"path":"src/App.tsx"}}""")
                .Matches);
        Assert.Equal(
            [AdapterEventType.ToolUse],
            classifier.Classify("""{"type":"tool_execution_start","toolCallId":"t2","toolName":"bash","args":{}}""")
                .Matches);
        Assert.Equal(
            [AdapterEventType.Message],
            classifier.Classify(
                """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""")
                .Matches);

        // The session header and the lifecycle chatter around a turn are not events in their own right.
        Assert.Equal(
            AdapterLineKind.Unmatched,
            classifier.Classify("""{"type":"session","version":3,"id":"u","cwd":"/repo"}""").Kind);
        Assert.Equal(
            AdapterLineKind.Unmatched,
            classifier.Classify("""{"type":"tool_execution_end","toolCallId":"t2","result":"ok","isError":false}""")
                .Kind);
        Assert.Equal(
            AdapterLineKind.Unmatched,
            classifier.Classify(
                """{"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}""")
                .Kind);
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
