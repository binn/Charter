using Charter.Adapters;

namespace Charter.Tests;

/// <summary>
/// Covers model x adapter compatibility (section 12b): the intersection of available credentials, the
/// adapter's supported providers, and repo policy.
/// </summary>
/// <remarks>
/// The rule under test is the one section 12b states outright — silently accepting an impossible
/// pairing and failing at dispatch is the worst outcome — so the resolver has to exclude, and say why.
/// </remarks>
public class AdapterCompatibilityTests
{
    private static readonly AdapterCompatibilityResolver Resolver = new();

    private static IReadOnlyList<AdapterDocument> Shipped()
        => AdapterCatalog.Load(new AdapterSources([AdapterTestFiles.ShippedDirectory])).Adapters;

    private static AdapterDocument Adapter(string id)
        => Shipped().Single(adapter => adapter.Id == id);

    [Fact]
    public void OffersOnlyModelsTheAdapterAndTheCredentialsBothSupport()
    {
        var result = Resolver.Resolve(
            [Adapter("claude-code")],
            ["anthropic/claude-opus-5", "openai/gpt-5", "google/gemini-3-pro"],
            CredentialInventory.OfKinds("anthropic_api_key", "openai_api_key", "google_api_key"),
            RepoModelPolicy.Unrestricted);

        var option = Assert.Single(result.Options);
        Assert.Equal("claude-code", option.AdapterId);
        Assert.Equal("anthropic/claude-opus-5", option.Model);
        Assert.Equal("anthropic_api_key", option.CredentialKind);

        Assert.Contains(
            result.Exclusions,
            exclusion => exclusion.Model == "openai/gpt-5"
                         && exclusion.Reason == AdapterModelExclusionReason.AdapterCannotUseProvider);
    }

    [Fact]
    public void PrefersTheAdaptersFirstDeclaredCredentialKindForAProvider()
    {
        // claude-code declares anthropic_oauth before anthropic_api_key, which is the section 20b.3
        // order: a linked subscription before a metered key.
        var result = Resolver.Resolve(
            [Adapter("claude-code")],
            ["anthropic/claude-opus-5"],
            CredentialInventory.OfKinds("anthropic_api_key", "anthropic_oauth"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal("anthropic_oauth", Assert.Single(result.Options).CredentialKind);
    }

    [Fact]
    public void ExcludesAProviderTheAdapterSupportsButNobodyHasACredentialFor()
    {
        var result = Resolver.Resolve(
            [Adapter("pi")],
            ["xai/grok-4"],
            CredentialInventory.OfKinds("anthropic_api_key"),
            RepoModelPolicy.Unrestricted);

        Assert.True(result.IsEmpty);
        var exclusion = Assert.Single(result.Exclusions);
        Assert.Equal(AdapterModelExclusionReason.NoCredential, exclusion.Reason);
        Assert.Contains("xai_api_key", exclusion.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void PiCoversTheWidestRangeOfProvidersFromOneAdapter()
    {
        // Section 12b calls this out as pi's reason for existing, so it is worth a regression test.
        var result = Resolver.Resolve(
            [Adapter("pi")],
            ["anthropic/claude-opus-5", "openai/gpt-5", "google/gemini-3-pro", "xai/grok-4", "openrouter/deepseek/deepseek-r1"],
            CredentialInventory.OfKinds(
                "anthropic_api_key", "openai_api_key", "google_api_key", "xai_api_key", "openrouter_key"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal(5, result.Options.Count);
        Assert.Empty(result.Exclusions);
    }

    [Fact]
    public void ClaudeCodeIsNotOfferedAnOpenRouterModelAndPiIs()
    {
        // The Phase 1 trap, stated in section 20b and change spec 001: OpenRouter is a control-plane
        // default, not an agent-run one. Claude Code authenticates against the Anthropic API or a
        // gateway presenting it; reaching an aggregator needs a proxy. Pi is provider-agnostic and
        // reaches it natively. The resolver has to say so before dispatch, not after.
        var result = Resolver.Resolve(
            [Adapter("claude-code"), Adapter("pi")],
            ["openrouter/anthropic/claude-sonnet-5", "openrouter/deepseek/deepseek-r1"],
            CredentialInventory.OfKinds("anthropic_api_key", "openrouter_key"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal(["pi"], result.AdapterIds);
        Assert.Equal(
            ["openrouter/anthropic/claude-sonnet-5", "openrouter/deepseek/deepseek-r1"],
            result.ModelsFor("pi"));
        Assert.Empty(result.ModelsFor("claude-code"));

        Assert.All(
            result.Exclusions,
            exclusion =>
            {
                Assert.Equal("claude-code", exclusion.AdapterId);
                Assert.Equal(AdapterModelExclusionReason.AdapterCannotUseProvider, exclusion.Reason);
            });
    }

    [Fact]
    public void ARefusalNamesAnAdapterThatCanRunTheModelInstead()
    {
        // "Claude Code cannot use OpenRouter" is a dead end for a requester. "Claude Code cannot use
        // OpenRouter; pi can" is the Phase 1 answer, and it is the resolver that knows it.
        var result = Resolver.Resolve(
            [Adapter("claude-code"), Adapter("pi")],
            ["openrouter/deepseek/deepseek-r1"],
            CredentialInventory.OfKinds("openrouter_key"),
            RepoModelPolicy.Unrestricted);

        var exclusion = Assert.Single(result.Exclusions);
        Assert.Contains("'pi' can", exclusion.Explanation, StringComparison.Ordinal);
        Assert.Equal(["pi"], result.AdaptersFor("openrouter/deepseek/deepseek-r1"));
    }

    [Fact]
    public void AnOpenRouterCredentialAloneDoesNotMakeClaudeCodeUsable()
    {
        // Both halves of the intersection have to hold. An OpenRouter key is a credential Claude Code
        // has no way to present, so the pairing stays refused however many keys the org has.
        var result = Resolver.Resolve(
            [Adapter("claude-code")],
            ["openrouter/anthropic/claude-opus-5"],
            CredentialInventory.OfKinds("openrouter_key"),
            RepoModelPolicy.Unrestricted);

        Assert.True(result.IsEmpty);
        Assert.Equal(
            AdapterModelExclusionReason.AdapterCannotUseProvider,
            Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void TheDefaultBuildModelIsOfferedByBothPhaseOneAdapters()
    {
        // CHARTER_MODEL_BUILD stays unqualified - Anthropic's - precisely so the default pairing is
        // one that exists. If this ever fails, the default has been changed without the adapter that
        // would have to run it.
        var result = Resolver.Resolve(
            [Adapter("claude-code"), Adapter("pi")],
            [Charter.Configuration.ModelConfig.DefaultBuild],
            CredentialInventory.OfKinds("anthropic_api_key"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal(["claude-code", "pi"], result.AdaptersFor(Charter.Configuration.ModelConfig.DefaultBuild));
        Assert.Empty(result.Exclusions);
    }

    [Fact]
    public void ACustomOpenAiCompatibleEndpointResolvesUnderEitherSpelling()
    {
        // The model layer prints `openai-compatible/` for a self-hosted endpoint; CredentialGrant calls
        // the kind `custom_openai_compatible`. They are the same thing, and codex declares that kind.
        foreach (var spelling in new[] { "openai-compatible/my-deployment", "custom/my-deployment" })
        {
            var result = Resolver.Resolve(
                [Adapter("codex")],
                [spelling],
                CredentialInventory.OfKinds("custom_openai_compatible"),
                RepoModelPolicy.Unrestricted);

            Assert.Equal(spelling, Assert.Single(result.Options).Model);
        }
    }

    [Fact]
    public void RepoPolicyCanDenyAModelEveryoneOtherwiseHasAccessTo()
    {
        var result = Resolver.Resolve(
            [Adapter("pi")],
            ["anthropic/claude-opus-5", "anthropic/claude-haiku-5"],
            CredentialInventory.OfKinds("anthropic_api_key"),
            new RepoModelPolicy([], [], ["anthropic/claude-opus-5"]));

        Assert.Equal("anthropic/claude-haiku-5", Assert.Single(result.Options).Model);
        Assert.Equal(AdapterModelExclusionReason.ModelDenied, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void RepoPolicyAllowListsAcceptATrailingWildcard()
    {
        var result = Resolver.Resolve(
            [Adapter("pi")],
            ["anthropic/claude-opus-5", "openai/gpt-5"],
            CredentialInventory.OfKinds("anthropic_api_key", "openai_api_key"),
            new RepoModelPolicy([], ["anthropic/*"], []));

        Assert.Equal("anthropic/claude-opus-5", Assert.Single(result.Options).Model);
        Assert.Equal(AdapterModelExclusionReason.ModelNotAllowed, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void RepoPolicyCanRestrictWhichAdaptersAreOffered()
    {
        var result = Resolver.Resolve(
            [Adapter("pi"), Adapter("claude-code")],
            ["anthropic/claude-opus-5"],
            CredentialInventory.OfKinds("anthropic_api_key"),
            new RepoModelPolicy(["claude-code"], [], []));

        Assert.Equal("claude-code", Assert.Single(result.Options).AdapterId);
        Assert.Equal(AdapterModelExclusionReason.AdapterNotPermitted, Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void AnEmptyIntersectionIsAResultWithReasons()
    {
        // The case the UI has to render: no credentials at all. It must come back empty *and*
        // explanatory, not throw and not quietly offer something that would fail at dispatch.
        var result = Resolver.Resolve(
            Shipped(),
            ["anthropic/claude-opus-5", "openai/gpt-5"],
            CredentialInventory.Empty,
            RepoModelPolicy.Unrestricted);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.AdapterIds);
        Assert.NotEmpty(result.Exclusions);
        Assert.All(result.Exclusions, exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Explanation)));
    }

    [Fact]
    public void CursorAgentOffersNothingUntilCredentialGrantLearnsItsKind()
    {
        // adapters/cursor-agent.yml documents this: Cursor authenticates against a Cursor account, and
        // `cursor_api_key` is not a CredentialGrant kind in section 20b.2 yet. Offering nothing is the
        // correct, visible outcome; offering a model that cannot authenticate is not.
        var result = Resolver.Resolve(
            [Adapter("cursor-agent")],
            ["anthropic/claude-opus-5", "openai/gpt-5"],
            CredentialInventory.OfKinds("anthropic_api_key", "openai_api_key"),
            RepoModelPolicy.Unrestricted);

        Assert.True(result.IsEmpty);
        Assert.All(
            result.Exclusions,
            exclusion => Assert.Equal(AdapterModelExclusionReason.AdapterCannotUseProvider, exclusion.Reason));
    }

    [Fact]
    public void AnUnqualifiedModelIdentifierIsAnthropics()
    {
        var result = Resolver.Resolve(
            [Adapter("claude-code")],
            ["claude-opus-5"],
            CredentialInventory.OfKinds("anthropic_api_key"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal("anthropic", Assert.Single(result.Options).Provider);
    }

    [Theory]
    [InlineData("anthropic/claude-opus-5", "anthropic", "claude-opus-5")]
    [InlineData("openrouter/deepseek/deepseek-r1", "openrouter", "deepseek/deepseek-r1")]
    [InlineData("claude-opus-5", "anthropic", "claude-opus-5")]
    [InlineData("gemini/gemini-3-pro", "google", "gemini-3-pro")]
    [InlineData("Anthropic/claude-opus-5", "anthropic", "claude-opus-5")]
    public void ParsesProviderQualifiedModelIdentifiers(string value, string provider, string name)
    {
        var identifier = ModelIdentifier.Parse(value);

        Assert.Equal(provider, identifier.Provider);
        Assert.Equal(name, identifier.Name);
    }

    [Fact]
    public void CarriesTheCredentialEndpointThroughSoTheUiCanNameIt()
    {
        var result = Resolver.Resolve(
            [Adapter("claude-code")],
            ["anthropic/claude-opus-5"],
            new CredentialInventory([new AvailableCredential("anthropic_api_key", "https://gateway.internal/v1")]),
            RepoModelPolicy.Unrestricted);

        Assert.Equal("https://gateway.internal/v1", Assert.Single(result.Options).Endpoint);
    }

    [Fact]
    public void ModelsForNarrowsTheOfferToOneAdapter()
    {
        var result = Resolver.Resolve(
            [Adapter("pi"), Adapter("claude-code")],
            ["anthropic/claude-opus-5", "openai/gpt-5"],
            CredentialInventory.OfKinds("anthropic_api_key", "openai_api_key"),
            RepoModelPolicy.Unrestricted);

        Assert.Equal(["anthropic/claude-opus-5", "openai/gpt-5"], result.ModelsFor("pi"));
        Assert.Equal(["anthropic/claude-opus-5"], result.ModelsFor("claude-code"));
    }
}
