using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 20b.3, tiers 4 and 5, when the credential is an environment variable rather than a row.
/// </summary>
/// <remarks>
/// <para>
/// The defect these exist for: <c>ANTHROPIC_API_KEY</c> and <c>OPENROUTER_API_KEY</c> satisfied
/// startup validation <em>and</em> the section 30.1 preflight check, so the instance reported healthy,
/// and then resolution read <c>credential_grants</c> and nothing else. Every test below would have
/// failed on the code that shipped, and none of them needs a database — which is the other half of the
/// lesson, because the suite that did have a database stubbed the resolver out.
/// </para>
/// <para>
/// No test here asserts on a key's value, and none can: <see cref="ModelSecret"/> is the only way to
/// carry one and it renders redacted.
/// </para>
/// </remarks>
public class ModelInstanceCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly ModelIdentifier Sonnet = ModelIdentifier.Parse("anthropic/claude-sonnet-5");

    /// <summary>The section 4.2 default for <c>CHARTER_MODEL_REFINE</c>, which is OpenRouter's.</summary>
    private static readonly ModelIdentifier DefaultRefine =
        ModelIdentifier.Parse("openrouter/anthropic/claude-sonnet-5");

    private static ModelCredentialQuery Query(ModelIdentifier? model = null)
        => new(model ?? Sonnet, "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222");

    private static ModelCredentialResolution Resolve(
        InstanceModelCredentials instance,
        ModelCredentialQuery query,
        IEnumerable<ModelCredential>? stored = null,
        CredentialPolicy? policy = null)
    {
        var candidates = new List<ModelCredential>(stored ?? []);
        candidates.AddRange(instance.Candidates());

        return CredentialResolver.Resolve(query, candidates, Now, policy);
    }

    [Fact]
    public void TheDocumentedDefaultInstallResolvesACredential()
    {
        // `.env.example` and docs/getting-started.md: one OpenRouter key, no grants, and
        // CHARTER_MODEL_REFINE left at its OpenRouter-qualified default. This is the exact
        // configuration that could not make a single model call.
        var instance = InstanceModelCredentials.From(anthropicApiKey: null, openRouterApiKey: "sk-or-v1-test");

        var resolution = Resolve(instance, Query(DefaultRefine));

        Assert.True(resolution.Resolved);
        Assert.Equal(ModelCredentialTier.OpenRouter, resolution.Credential!.Tier);
        Assert.Equal(InstanceModelCredentials.IdPrefix + "OPENROUTER_API_KEY", resolution.Credential.Credential.Id);
        Assert.Equal(ModelCredentialUnavailability.None, resolution.Unavailability);
    }

    [Fact]
    public void AnAnthropicInstanceKeyLandsInTierFourAndNotTierFive()
    {
        // Section 20b.3 gives the instance keys no tier of their own: an API key billed to the
        // deployment is the organisation's metered key, and OpenRouter is the last resort. Collapsing
        // the two would put an Anthropic key behind an OpenRouter one that costs money to use.
        var instance = InstanceModelCredentials.From("sk-ant-test", "sk-or-v1-test");

        var resolution = Resolve(instance, Query(Sonnet));

        Assert.Equal(ModelCredentialTier.OrganizationMeteredKey, resolution.Credential!.Tier);
        Assert.Equal(ModelCredentialKind.AnthropicApiKey, resolution.Credential.Credential.Kind);
    }

    [Fact]
    public void AnAnthropicInstanceKeyIsNeverOfferedToAnOpenRouterModel()
    {
        // An openrouter/-qualified identifier can only be served by an OpenRouter key (section 20b.1),
        // and offering the Anthropic key anyway is how a working key ends up marked invalid by a 401
        // that was Charter's fault.
        var instance = InstanceModelCredentials.From("sk-ant-test", openRouterApiKey: null);

        var resolution = Resolve(instance, Query(DefaultRefine));

        Assert.False(resolution.Resolved);
        Assert.Equal(ModelCredentialUnavailability.NotConfigured, resolution.Unavailability);
        Assert.Contains("OPENROUTER_API_KEY", resolution.Explanation, StringComparison.Ordinal);
        Assert.Contains("openrouter/anthropic/claude-sonnet-5", resolution.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void APerMemberGrantStillBeatsTheInstanceKey()
    {
        // Tier 1 is above tiers 4 and 5 and adding an environment tier must not move it. A person who
        // linked their own subscription expects their work to run on it, not on the instance's card.
        var instance = InstanceModelCredentials.From("sk-ant-test", "sk-or-v1-test");

        var mine = new ModelCredential
        {
            Id = "grant-mine",
            Kind = ModelCredentialKind.AnthropicOAuth,
            Secret = new ModelSecret("subscription"),
            OwnerUserId = "11111111-1111-1111-1111-111111111111",
            OrganizationId = "22222222-2222-2222-2222-222222222222",
        };

        var resolution = Resolve(instance, Query(Sonnet), [mine]);

        Assert.Equal("grant-mine", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.RequesterSubscription, resolution.Credential.Tier);
    }

    [Fact]
    public void AnOrganisationsOwnMeteredGrantBeatsTheInstanceFallbackInTheSameTier()
    {
        // Both are tier 4, so the order inside the tier is what decides. The variables are documented
        // as the instance's fallback, and a key an operator linked for this organisation is the
        // deliberate one.
        var instance = InstanceModelCredentials.From("sk-ant-test", openRouterApiKey: null);

        var linked = new ModelCredential
        {
            Id = "grant-org",
            Kind = ModelCredentialKind.AnthropicApiKey,
            Secret = new ModelSecret("org-key"),
            OrganizationId = "22222222-2222-2222-2222-222222222222",
        };

        var resolution = Resolve(instance, Query(Sonnet), [linked]);

        Assert.Equal("grant-org", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OrganizationMeteredKey, resolution.Credential.Tier);
    }

    [Fact]
    public void TheInstanceKeyDoesNotReopenTheSharedPool()
    {
        // Section 20b.7, checked against the tier that was just added: an instance that has not opted
        // in must still skip a pooled grant, and the presence of an environment key must not change
        // which tier answers.
        var instance = InstanceModelCredentials.From("sk-ant-test", openRouterApiKey: null);

        var pooled = new ModelCredential
        {
            Id = "grant-pooled",
            Kind = ModelCredentialKind.AnthropicOAuth,
            Secret = new ModelSecret("someone-elses-subscription"),
            OwnerUserId = "99999999-9999-9999-9999-999999999999",
            OrganizationId = "22222222-2222-2222-2222-222222222222",
            Scope = ModelCredentialScope.SharedPool,
            Priority = -100,
        };

        var refused = Resolve(instance, Query(Sonnet), [pooled]);

        Assert.Equal(ModelCredentialTier.OrganizationMeteredKey, refused.Credential!.Tier);
        Assert.StartsWith(InstanceModelCredentials.IdPrefix, refused.Credential.Credential.Id, StringComparison.Ordinal);

        // The same grants on an instance whose operator did opt in: the pool outranks tier 4 again.
        var pooling = Resolve(instance, Query(Sonnet), [pooled], CredentialPolicy.Pooled);

        Assert.Equal("grant-pooled", pooling.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OrganizationSharedPool, pooling.Credential.Tier);
    }

    [Fact]
    public void NoCredentialAnywhereIsNotConfiguredAndNamesWhatToSet()
    {
        var resolution = Resolve(InstanceModelCredentials.None, Query(Sonnet));

        Assert.False(resolution.Resolved);
        Assert.False(resolution.RecoversOnItsOwn);
        Assert.Equal(ModelCredentialUnavailability.NotConfigured, resolution.Unavailability);
        Assert.Contains("ANTHROPIC_API_KEY", resolution.Explanation, StringComparison.Ordinal);
        Assert.Contains("OPENROUTER_API_KEY", resolution.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidCredentialNeedsAttentionRatherThanAWait()
    {
        // Section 20b.4: a hard authentication failure is not a rate limit, and a caller that defers
        // on it defers forever. This is the case an instance reaches with a revoked key.
        var dead = new ModelCredential
        {
            Id = "grant-dead",
            Kind = ModelCredentialKind.AnthropicApiKey,
            Secret = new ModelSecret("rejected"),
            OrganizationId = "22222222-2222-2222-2222-222222222222",
            Status = ModelCredentialStatus.Invalid,
        };

        var resolution = Resolve(InstanceModelCredentials.None, Query(Sonnet), [dead]);

        Assert.False(resolution.RecoversOnItsOwn);
        Assert.Equal(ModelCredentialUnavailability.NeedsAttention, resolution.Unavailability);
        Assert.Contains("will not clear on its own", resolution.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ARateLimitWithAResetInstantIsTheOneCaseThatWaits()
    {
        // Section 20b.3's own words: if everything is exhausted the session waits and does not fail.
        // The classification has to keep that true while making everything else loud.
        var limited = new ModelCredential
        {
            Id = "grant-limited",
            Kind = ModelCredentialKind.AnthropicApiKey,
            Secret = new ModelSecret("busy"),
            OrganizationId = "22222222-2222-2222-2222-222222222222",
            Status = ModelCredentialStatus.Exhausted,
            ExhaustedUntil = Now.AddHours(2),
        };

        var resolution = Resolve(InstanceModelCredentials.None, Query(Sonnet), [limited]);

        Assert.True(resolution.RecoversOnItsOwn);
        Assert.Equal(ModelCredentialUnavailability.WaitingForCapacity, resolution.Unavailability);
        Assert.Equal(Now.AddHours(2), resolution.WaitingForCapacityUntil);
    }

    [Fact]
    public async Task ExhaustingAnInstanceKeyTakesItOutOfTheChainWithoutTouchingTheDatabase()
    {
        var instance = InstanceModelCredentials.From(anthropicApiKey: null, openRouterApiKey: "sk-or-v1-test");
        var inner = new RecordingCredentialStore();

        var store = new InstanceKeyModelCredentialStore(
            inner,
            instance,
            TimeProvider.System,
            NullLogger<InstanceKeyModelCredentialStore>.Instance);

        var id = InstanceModelCredentials.IdPrefix + "OPENROUTER_API_KEY";

        await store.MarkExhaustedAsync(
            id,
            Now.AddHours(1),
            useOverflow: false,
            TestContext.Current.CancellationToken);

        // The write-back never reaches the row store: an environment id names no grant, and forwarding
        // it produces a "that grant was deleted" warning that sends an operator looking in the wrong
        // place entirely.
        Assert.Empty(inner.Exhausted);

        var described = Assert.Single(instance.Describe());
        Assert.Equal(ModelCredentialStatus.Exhausted, described.Status);
        Assert.Equal(Now.AddHours(1), described.ExhaustedUntil);

        // And the chain now waits for it rather than presenting a key the provider just refused.
        var resolution = Resolve(instance, Query(DefaultRefine));

        Assert.True(resolution.RecoversOnItsOwn);
        Assert.Equal(Now.AddHours(1), resolution.WaitingForCapacityUntil);
    }

    [Fact]
    public async Task AGrantsWriteBackStillReachesTheRowStore()
    {
        var inner = new RecordingCredentialStore();

        var store = new InstanceKeyModelCredentialStore(
            inner,
            InstanceModelCredentials.From("sk-ant-test", null),
            TimeProvider.System,
            NullLogger<InstanceKeyModelCredentialStore>.Instance);

        await store.MarkInvalidAsync(
            "6f1c0a26-0000-0000-0000-000000000001",
            "provider said 401",
            TestContext.Current.CancellationToken);

        Assert.Equal("6f1c0a26-0000-0000-0000-000000000001", Assert.Single(inner.Invalidated).CredentialId);
    }

    [Fact]
    public void NothingAboutAnInstanceKeyRendersItsValue()
    {
        // Section 20b.2 and section 19: never log a token. Both of the ways one of these could reach a
        // log - the credential's own ToString, and the status record an API returns - are checked here.
        var instance = InstanceModelCredentials.From("sk-ant-super-secret", "sk-or-v1-super-secret");

        foreach (var candidate in instance.Candidates())
        {
            Assert.DoesNotContain("super-secret", candidate.ToString(), StringComparison.Ordinal);
            Assert.Contains(ModelSecret.RedactedPlaceholder, candidate.ToString(), StringComparison.Ordinal);
        }

        foreach (var described in instance.Describe())
        {
            Assert.DoesNotContain("super-secret", described.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnsetVariableContributesNothing()
    {
        var instance = InstanceModelCredentials.From(anthropicApiKey: null, openRouterApiKey: "   ");

        Assert.False(instance.Any);
        Assert.Empty(instance.Candidates());
        Assert.Empty(instance.Describe());
    }
}
