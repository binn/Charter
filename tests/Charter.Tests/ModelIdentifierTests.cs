using Charter.Models;

namespace Charter.Tests;

/// <summary>Section 20b.1: provider-qualified model identifiers.</summary>
public class ModelIdentifierTests
{
    [Fact]
    public void BareIdentifierResolvesToAnthropic()
    {
        var identifier = ModelIdentifier.Parse("claude-opus-5");

        Assert.Equal(ModelProvider.Anthropic, identifier.Provider);
        Assert.Equal("claude-opus-5", identifier.Model);
        Assert.False(identifier.WasQualified);
        Assert.Equal("anthropic/claude-opus-5", identifier.Canonical);
    }

    [Fact]
    public void Section42DefaultsAreWrittenBareAndParse()
    {
        foreach (var bare in new[] { "claude-sonnet-5", "claude-opus-5" })
        {
            var identifier = ModelIdentifier.Parse(bare);
            Assert.Equal(ModelProvider.Anthropic, identifier.Provider);
            Assert.Equal(bare, identifier.Model);
        }
    }

    [Fact]
    public void QualifiedAnthropicIdentifierKeepsTheModelName()
    {
        var identifier = ModelIdentifier.Parse("anthropic/claude-opus-5");

        Assert.Equal(ModelProvider.Anthropic, identifier.Provider);
        Assert.Equal("claude-opus-5", identifier.Model);
        Assert.True(identifier.WasQualified);
    }

    [Fact]
    public void OpenRouterIdentifierKeepsItsNestedVendorSegment()
    {
        // The hazard: splitting on every slash loses "deepseek/" and leaves an unroutable model name.
        var identifier = ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1");

        Assert.Equal(ModelProvider.OpenRouter, identifier.Provider);
        Assert.Equal("deepseek/deepseek-r1", identifier.Model);
        Assert.Equal("openrouter/deepseek/deepseek-r1", identifier.Canonical);
    }

    [Fact]
    public void OpenRouterIdentifierWithAVariantSuffixSurvives()
    {
        var identifier = ModelIdentifier.Parse("openrouter/meta-llama/llama-3.3-70b-instruct:free");

        Assert.Equal(ModelProvider.OpenRouter, identifier.Provider);
        Assert.Equal("meta-llama/llama-3.3-70b-instruct:free", identifier.Model);
    }

    [Theory]
    [InlineData("openai/gpt-5", ModelProvider.OpenAi, "gpt-5")]
    [InlineData("xai/grok-4", ModelProvider.XAi, "grok-4")]
    [InlineData("google/gemini-2.5-pro", ModelProvider.Google, "gemini-2.5-pro")]
    [InlineData("gemini/gemini-2.5-flash", ModelProvider.Google, "gemini-2.5-flash")]
    [InlineData("groq/llama-3.3-70b", ModelProvider.Groq, "llama-3.3-70b")]
    [InlineData("deepseek/deepseek-chat", ModelProvider.DeepSeek, "deepseek-chat")]
    [InlineData("ollama/qwen2.5-coder", ModelProvider.Ollama, "qwen2.5-coder")]
    [InlineData("azure/my-deployment", ModelProvider.AzureOpenAi, "my-deployment")]
    public void KnownPrefixesResolve(string value, ModelProvider provider, string model)
    {
        var identifier = ModelIdentifier.Parse(value);

        Assert.Equal(provider, identifier.Provider);
        Assert.Equal(model, identifier.Model);
    }

    [Fact]
    public void RoundTrippingTheCanonicalFormIsStable()
    {
        var once = ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1");
        var twice = ModelIdentifier.Parse(once.Canonical);

        Assert.Equal(once.Provider, twice.Provider);
        Assert.Equal(once.Model, twice.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/claude-opus-5")]
    [InlineData("anthropic/")]
    public void MalformedIdentifiersDoNotParse(string value)
    {
        Assert.False(ModelIdentifier.TryParse(value, out _));
    }

    [Fact]
    public void AnUnknownProviderPrefixIsRejectedRatherThanGuessedAt()
    {
        Assert.False(ModelIdentifier.TryParse("acme/some-model", out _, out var error));
        Assert.Contains("acme", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WhitespaceIsTrimmed()
    {
        var identifier = ModelIdentifier.Parse("  anthropic/claude-opus-5  ");

        Assert.Equal("claude-opus-5", identifier.Model);
    }
}
