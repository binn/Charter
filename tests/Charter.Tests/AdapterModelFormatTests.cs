using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// The form of model identifier each CLI is handed at dispatch (section 12b, <c>model_format</c>).
/// </summary>
/// <remarks>
/// Charter resolves one canonical identifier (section 20b.1) and the adapter declares how to render
/// it. The alternative - every call site stripping or adding a provider prefix by agent name - is the
/// branch-on-<c>id</c> that section 12b exists to prevent, and it fails silently: a CLI handed a name
/// it does not know reports an unknown model four minutes into a session.
/// </remarks>
public class AdapterModelFormatTests
{
    private static AdapterDocument WithFormat(string? format)
    {
        var yaml = format is null
            ? AdapterTestFiles.ValidYaml
            : AdapterTestFiles.WithLineReplaced(
                "model_arg: [\"--model\", \"{model}\"]",
                $"model_arg: [\"--model\", \"{{model}}\"]\nmodel_format: {format}");

        return AdapterTestFiles.Load(yaml);
    }

    [Fact]
    public void AnAbsentModelFormatIsBareWhichIsWhatCharterDispatchedBefore()
    {
        var adapter = WithFormat(null);

        Assert.Equal(AdapterModelFormat.Bare, adapter.ModelFormat);
        Assert.Equal("claude-opus-5", adapter.FormatModel("anthropic/claude-opus-5"));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-5", "claude-opus-5")]
    [InlineData("claude-opus-5", "claude-opus-5")]
    [InlineData("openrouter/deepseek/deepseek-r1", "deepseek/deepseek-r1")]
    [InlineData("openrouter/meta-llama/llama-3.3-70b-instruct:free", "meta-llama/llama-3.3-70b-instruct:free")]
    public void BareStripsOnlyCharterSProviderPrefix(string canonical, string expected)
    {
        // The nested-slash rule from section 20b.1: an OpenRouter model id contains its own vendor
        // segment, and dropping it leaves a name no provider can route.
        Assert.Equal(expected, WithFormat("bare").FormatModel(canonical));
    }

    [Theory]
    [InlineData("anthropic/claude-opus-5", "anthropic/claude-opus-5")]
    [InlineData("claude-opus-5", "anthropic/claude-opus-5")]
    [InlineData("openrouter/deepseek/deepseek-r1", "openrouter/deepseek/deepseek-r1")]
    public void QualifiedRendersCharterSCanonicalForm(string input, string expected)
    {
        Assert.Equal(expected, WithFormat("qualified").FormatModel(input));
    }

    [Fact]
    public void VerbatimReinterpretsNothing()
    {
        var adapter = WithFormat("verbatim");

        Assert.Equal("openrouter/anthropic/claude-sonnet-4.5", adapter.FormatModel("openrouter/anthropic/claude-sonnet-4.5"));
        Assert.Equal("some-litellm-name", adapter.FormatModel("some-litellm-name"));
    }

    [Fact]
    public void FormattingIsIdempotentSoACallerMayPassEitherForm()
    {
        var bare = WithFormat("bare");
        var qualified = WithFormat("qualified");

        Assert.Equal(bare.FormatModel("claude-opus-5"), bare.FormatModel(bare.FormatModel("claude-opus-5")));
        Assert.Equal(
            qualified.FormatModel("anthropic/claude-opus-5"),
            qualified.FormatModel(qualified.FormatModel("anthropic/claude-opus-5")));
    }

    [Fact]
    public void TheProviderPlaceholderNamesTheProviderSegment()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "model_arg: [\"--model\", \"{model}\"]",
            "model_arg: [\"--provider\", \"{provider}\", \"--model\", \"{model}\"]\nmodel_format: bare");

        var invocation = AdapterTestFiles.Load(yaml).BuildInvocation("do it", "openrouter/deepseek/deepseek-r1");

        Assert.Equal(
            ["example", "--print", "--output-format", "jsonl", "--provider", "openrouter", "--model", "deepseek/deepseek-r1"],
            invocation.Arguments);
    }

    [Fact]
    public void AProviderPlaceholderCannotBeCombinedWithVerbatim()
    {
        // Charter would have to interpret the identifier to name its provider, which is exactly what
        // verbatim says not to do. Section 8: a contradiction inside the file fails at load.
        var yaml = AdapterTestFiles.WithLineReplaced(
            "model_arg: [\"--model\", \"{model}\"]",
            "model_arg: [\"--provider\", \"{provider}\", \"--model\", \"{model}\"]\nmodel_format: verbatim");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("model_format", error.Message, StringComparison.Ordinal);
        Assert.Contains("{provider}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownModelFormatIsAnErrorNamingTheFormsThatExist()
    {
        var yaml = AdapterTestFiles.WithLineReplaced(
            "model_arg: [\"--model\", \"{model}\"]",
            "model_arg: [\"--model\", \"{model}\"]\nmodel_format: slug");

        var error = Assert.Throws<AdapterLoadException>(() => AdapterTestFiles.Load(yaml));

        Assert.Contains("model_format", error.Message, StringComparison.Ordinal);
        Assert.Contains("qualified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedAdaptersDeclareTheFormTheirCliDocuments()
    {
        var catalog = AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

        Assert.Equal(AdapterModelFormat.Bare, catalog.Get("claude-code").ModelFormat);
        Assert.Equal(AdapterModelFormat.Bare, catalog.Get("pi").ModelFormat);
        Assert.Equal(AdapterModelFormat.Qualified, catalog.Get("opencode").ModelFormat);
        Assert.Equal(AdapterModelFormat.Verbatim, catalog.Get("aider").ModelFormat);
    }

    [Fact]
    public void OneIdentifierProducesTheRightArgumentForEveryShippedAdapter()
    {
        // The case that motivated the key: the same resolved model, dispatched to two CLIs that
        // disagree about how to spell it.
        var catalog = AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory]));

        Assert.Contains("claude-opus-5", catalog.Get("claude-code").BuildInvocation("x", "anthropic/claude-opus-5").Arguments);
        Assert.Contains("anthropic/claude-opus-5", catalog.Get("opencode").BuildInvocation("x", "anthropic/claude-opus-5").Arguments);
    }
}
