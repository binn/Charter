using Charter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>Section 20b.3: the resolution chain, and section 20b.4's write-back rules.</summary>
public class ModelCredentialResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModelIdentifier Claude = ModelIdentifier.Parse("anthropic/claude-sonnet-5");

    private static ModelCredentialQuery Query() => new(Claude, "user-1", "org-1");

    private static ModelCredential Grant(
        string id,
        ModelCredentialKind kind,
        string? owner = null,
        ModelCredentialScope scope = ModelCredentialScope.Personal,
        ModelCredentialStatus status = ModelCredentialStatus.Active,
        DateTimeOffset? exhaustedUntil = null,
        int priority = 0,
        ModelCredentialOverflow? overflow = null) => new()
        {
            Id = id,
            Kind = kind,
            Secret = new ModelSecret("secret-" + id),
            OwnerUserId = owner,
            OrganizationId = "org-1",
            Scope = scope,
            Status = status,
            ExhaustedUntil = exhaustedUntil,
            Priority = priority,
            Overflow = overflow,
        };

    [Fact]
    public void ChainPrefersTheRequestersOwnSubscription()
    {
        var candidates = new[]
        {
            Grant("openrouter", ModelCredentialKind.OpenRouterKey),
            Grant("org-key", ModelCredentialKind.AnthropicApiKey),
            Grant("pool", ModelCredentialKind.AnthropicOAuth, "user-2", ModelCredentialScope.SharedPool),
            Grant("mine", ModelCredentialKind.AnthropicOAuth, "user-1"),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.True(resolution.Resolved);
        Assert.Equal("mine", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.RequesterSubscription, resolution.Credential.Tier);
    }

    [Fact]
    public void ExhaustedOwnSubscriptionFallsToItsOverflowBeforeThePool()
    {
        var candidates = new[]
        {
            Grant("pool", ModelCredentialKind.AnthropicOAuth, "user-2", ModelCredentialScope.SharedPool),
            Grant(
                "mine",
                ModelCredentialKind.AnthropicOAuth,
                "user-1",
                status: ModelCredentialStatus.Exhausted,
                exhaustedUntil: Now.AddHours(3),
                overflow: new ModelCredentialOverflow { Enabled = true }),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.Equal("mine", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.RequesterOverflow, resolution.Credential.Tier);
        Assert.True(resolution.Credential.UseOverflow);
    }

    [Fact]
    public void ExhaustedOverflowFallsToTheSharedPoolByPriority()
    {
        var candidates = new[]
        {
            Grant("pool-low", ModelCredentialKind.AnthropicOAuth, "user-3", ModelCredentialScope.SharedPool, priority: 10),
            Grant("pool-high", ModelCredentialKind.AnthropicOAuth, "user-2", ModelCredentialScope.SharedPool, priority: 1),
            Grant(
                "mine",
                ModelCredentialKind.AnthropicOAuth,
                "user-1",
                status: ModelCredentialStatus.Exhausted,
                exhaustedUntil: Now.AddHours(3),
                overflow: new ModelCredentialOverflow
                {
                    Enabled = true,
                    Status = ModelCredentialStatus.Exhausted,
                    ExhaustedUntil = Now.AddHours(2),
                }),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.Equal("pool-high", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OrganizationSharedPool, resolution.Credential.Tier);
    }

    [Fact]
    public void ExhaustedPoolFallsToTheOrganisationMeteredKeyThenOpenRouter()
    {
        var pooled = Grant(
            "pool",
            ModelCredentialKind.AnthropicOAuth,
            "user-2",
            ModelCredentialScope.SharedPool,
            ModelCredentialStatus.Exhausted,
            Now.AddHours(1));

        var withMeteredKey = CredentialResolver.Resolve(
            Query(),
            [pooled, Grant("org-key", ModelCredentialKind.AnthropicApiKey), Grant("or", ModelCredentialKind.OpenRouterKey)],
            Now);

        Assert.Equal("org-key", withMeteredKey.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OrganizationMeteredKey, withMeteredKey.Credential.Tier);

        var withoutMeteredKey = CredentialResolver.Resolve(
            Query(),
            [pooled, Grant("or", ModelCredentialKind.OpenRouterKey)],
            Now);

        Assert.Equal("or", withoutMeteredKey.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OpenRouter, withoutMeteredKey.Credential.Tier);
    }

    [Fact]
    public void InvalidAndRevokedGrantsAreSkippedEvenAtTheHeadOfTheChain()
    {
        var candidates = new[]
        {
            Grant("mine-invalid", ModelCredentialKind.AnthropicOAuth, "user-1", status: ModelCredentialStatus.Invalid),
            Grant("pool-revoked", ModelCredentialKind.AnthropicOAuth, "user-2", ModelCredentialScope.SharedPool, ModelCredentialStatus.Revoked),
            Grant("org-key", ModelCredentialKind.AnthropicApiKey),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.Equal("org-key", resolution.Credential!.Credential.Id);
    }

    [Fact]
    public void AnExhaustedGrantWhoseResetHasPassedIsUsableAgain()
    {
        var candidates = new[]
        {
            Grant(
                "mine",
                ModelCredentialKind.AnthropicOAuth,
                "user-1",
                status: ModelCredentialStatus.Exhausted,
                exhaustedUntil: Now.AddMinutes(-1)),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.Equal("mine", resolution.Credential!.Credential.Id);
    }

    [Fact]
    public void WhenEverythingIsExhaustedTheEarliestResetIsReportedRatherThanFailing()
    {
        var candidates = new[]
        {
            Grant("mine", ModelCredentialKind.AnthropicOAuth, "user-1", status: ModelCredentialStatus.Exhausted, exhaustedUntil: Now.AddHours(5)),
            Grant("pool", ModelCredentialKind.AnthropicOAuth, "user-2", ModelCredentialScope.SharedPool, ModelCredentialStatus.Exhausted, Now.AddHours(2)),
            Grant("or", ModelCredentialKind.OpenRouterKey, status: ModelCredentialStatus.Exhausted, exhaustedUntil: Now.AddHours(9)),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.False(resolution.Resolved);
        Assert.True(resolution.AllExhausted);
        Assert.Equal(Now.AddHours(2), resolution.WaitingForCapacityUntil);
    }

    [Fact]
    public void AGrantForADifferentProviderCannotServeTheModel()
    {
        var candidates = new[]
        {
            Grant("google", ModelCredentialKind.GoogleApiKey, "user-1"),
            Grant("anthropic", ModelCredentialKind.AnthropicApiKey),
        };

        var resolution = CredentialResolver.Resolve(Query(), candidates, Now);

        Assert.Equal("anthropic", resolution.Credential!.Credential.Id);
    }

    [Fact]
    public void AnOpenRouterQualifiedModelOnlyResolvesToAnOpenRouterKey()
    {
        var query = new ModelCredentialQuery(
            ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1"),
            "user-1",
            "org-1");

        var candidates = new[]
        {
            Grant("mine", ModelCredentialKind.AnthropicOAuth, "user-1"),
            Grant("org-key", ModelCredentialKind.AnthropicApiKey),
            Grant("or", ModelCredentialKind.OpenRouterKey),
        };

        var resolution = CredentialResolver.Resolve(query, candidates, Now);

        Assert.Equal("or", resolution.Credential!.Credential.Id);
        Assert.Equal(ModelCredentialTier.OpenRouter, resolution.Credential.Tier);
    }

    [Fact]
    public async Task ResolveReadsCandidatesFromTheStore()
    {
        var store = new RecordingCredentialStore(Grant("org-key", ModelCredentialKind.AnthropicApiKey));
        var resolver = new CredentialResolver(
            store,
            new ModelFakeTimeProvider(Now),
            NullLogger<CredentialResolver>.Instance);

        var resolution = await resolver.ResolveAsync(Query(), TestContext.Current.CancellationToken);

        Assert.Equal("org-key", resolution.Credential!.Credential.Id);
    }

    [Fact]
    public async Task A429MarksTheGrantExhaustedAndRecordsTheReset()
    {
        var store = new RecordingCredentialStore();
        var resolver = new CredentialResolver(
            store,
            new ModelFakeTimeProvider(Now),
            NullLogger<CredentialResolver>.Instance);

        var resolved = new ResolvedModelCredential(
            Grant("mine", ModelCredentialKind.AnthropicOAuth, "user-1"),
            ModelCredentialTier.RequesterSubscription);

        var resetAt = Now.AddMinutes(37);
        await resolver.ReportFailureAsync(
            resolved,
            new ModelRateLimitException(
                "rate limited",
                ModelProvider.Anthropic,
                new RateLimitReset(resetAt, TimeSpan.FromMinutes(37))),
            TestContext.Current.CancellationToken);

        var (credentialId, exhaustedUntil, useOverflow) = Assert.Single(store.Exhausted);
        Assert.Equal("mine", credentialId);
        Assert.Equal(resetAt, exhaustedUntil);
        Assert.False(useOverflow);
        Assert.Empty(store.Invalidated);
    }

    [Fact]
    public async Task A429AgainstOverflowExhaustsTheOverflowRatherThanTheSubscription()
    {
        var store = new RecordingCredentialStore();
        var resolver = new CredentialResolver(
            store,
            new ModelFakeTimeProvider(Now),
            NullLogger<CredentialResolver>.Instance);

        var resolved = new ResolvedModelCredential(
            Grant("mine", ModelCredentialKind.AnthropicOAuth, "user-1"),
            ModelCredentialTier.RequesterOverflow,
            UseOverflow: true);

        await resolver.ReportFailureAsync(
            resolved,
            new ModelRateLimitException("rate limited", ModelProvider.Anthropic, RateLimitReset.Unknown),
            TestContext.Current.CancellationToken);

        var recorded = Assert.Single(store.Exhausted);
        Assert.True(recorded.UseOverflow);
        Assert.Null(recorded.ExhaustedUntil);
    }

    [Fact]
    public async Task AHardAuthFailureMarksTheGrantInvalidRatherThanExhausted()
    {
        var store = new RecordingCredentialStore();
        var resolver = new CredentialResolver(
            store,
            new ModelFakeTimeProvider(Now),
            NullLogger<CredentialResolver>.Instance);

        var resolved = new ResolvedModelCredential(
            Grant("org-key", ModelCredentialKind.AnthropicApiKey),
            ModelCredentialTier.OrganizationMeteredKey);

        await resolver.ReportFailureAsync(
            resolved,
            new ModelAuthenticationException(
                "rejected",
                ModelProvider.Anthropic,
                System.Net.HttpStatusCode.Unauthorized),
            TestContext.Current.CancellationToken);

        Assert.Empty(store.Exhausted);
        Assert.Equal("org-key", Assert.Single(store.Invalidated).CredentialId);
    }
}
