using System.Security.Cryptography;
using Charter.Configuration;
using Charter.Data;
using Charter.Data.Credentials;
using Charter.Domain;
using Charter.Models;
using Charter.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// Charter.Configuration and Charter.Adapters each carry a ModelIdentifier of their own; this file
// means the model layer's.
using ModelIdentifier = Charter.Models.ModelIdentifier;

namespace Charter.Tests;

/// <summary>
/// The projection from <see cref="CredentialGrant"/> onto the model layer's view, checked without a
/// database.
/// </summary>
/// <remarks>
/// The mappings are where a silent mistake is most expensive: a kind that maps to the wrong provider
/// sends a key to the wrong endpoint, and a priority mapped the wrong way round quietly inverts the
/// shared pool's ordering.
/// </remarks>
public class CredentialProjectionTests
{
    private static CredentialGrant Grant(
        CredentialKind kind = CredentialKind.AnthropicApiKey,
        CredentialScope scope = CredentialScope.Personal,
        int priority = 0,
        string? baseUrl = null) =>
        CredentialGrant.Create(
            orgId: Guid.CreateVersion7(),
            ownerUserId: Guid.CreateVersion7(),
            kind: kind,
            secretEncrypted: [1, 2, 3],
            scope: scope,
            baseUrl: baseUrl,
            priority: priority);

    [Fact]
    public void EveryPersistedKindHasAModelLayerEquivalent()
    {
        // The switch has no default arm, so this loop failing to throw is the runtime half of the
        // same guarantee: a new CredentialKind breaks the build, and none of the existing ones map
        // to something arbitrary.
        foreach (var kind in Enum.GetValues<CredentialKind>())
        {
            var mapped = EfModelCredentialStore.MapKind(kind);

            Assert.True(Enum.IsDefined(mapped));
            Assert.Equal(
                kind is CredentialKind.AnthropicOauth or CredentialKind.OpenAiOauth,
                EfModelCredentialStore.IsSubscriptionKind(kind));
        }
    }

    [Theory]
    [InlineData(CredentialKind.AnthropicOauth, ModelCredentialKind.AnthropicOAuth, ModelProvider.Anthropic)]
    [InlineData(CredentialKind.AnthropicApiKey, ModelCredentialKind.AnthropicApiKey, ModelProvider.Anthropic)]
    [InlineData(CredentialKind.OpenAiOauth, ModelCredentialKind.OpenAiOAuth, ModelProvider.OpenAi)]
    [InlineData(CredentialKind.OpenAiApiKey, ModelCredentialKind.OpenAiApiKey, ModelProvider.OpenAi)]
    [InlineData(CredentialKind.GoogleApiKey, ModelCredentialKind.GoogleApiKey, ModelProvider.Google)]
    [InlineData(CredentialKind.XaiApiKey, ModelCredentialKind.XaiApiKey, ModelProvider.XAi)]
    [InlineData(CredentialKind.OpenRouterKey, ModelCredentialKind.OpenRouterKey, ModelProvider.OpenRouter)]
    [InlineData(
        CredentialKind.CustomOpenAiCompatible,
        ModelCredentialKind.CustomOpenAiCompatible,
        ModelProvider.OpenAiCompatible)]
    public void AKindMapsToItsOwnProvider(
        CredentialKind kind,
        ModelCredentialKind expected,
        ModelProvider provider)
    {
        var mapped = EfModelCredentialStore.MapKind(kind);

        Assert.Equal(expected, mapped);
        Assert.Equal(provider, EfModelCredentialStore.Project(Grant(kind), default).Provider);
    }

    [Fact]
    public void StatusAndScopeMapAcrossWithoutLosingRevocation()
    {
        foreach (var status in Enum.GetValues<CredentialStatus>())
        {
            Assert.True(Enum.IsDefined(EfModelCredentialStore.MapStatus(status)));
        }

        Assert.Equal(ModelCredentialStatus.Revoked, EfModelCredentialStore.MapStatus(CredentialStatus.Revoked));
        Assert.Equal(ModelCredentialScope.SharedPool, EfModelCredentialStore.MapScope(CredentialScope.SharedPool));
        Assert.Equal(ModelCredentialScope.Personal, EfModelCredentialStore.MapScope(CredentialScope.Personal));
    }

    [Fact]
    public void PriorityIsInvertedBecauseTheTwoTypesRankItOppositeWays()
    {
        // CredentialGrant: "higher wins". ModelCredential: "lower values are tried first", and the
        // resolver sorts ascending. Mapping straight through would put the operator's first choice
        // last.
        var ranked = EfModelCredentialStore.Project(Grant(priority: 10), default);
        var unranked = EfModelCredentialStore.Project(Grant(priority: 0), default);

        Assert.True(ranked.Priority < unranked.Priority);
    }

    [Fact]
    public void ABaseUrlSurvivesAndRubbishDoesNot()
    {
        Assert.Equal(
            new Uri("https://gateway.internal/v1/"),
            EfModelCredentialStore.Project(Grant(baseUrl: "https://gateway.internal/v1/"), default).BaseUrl);

        Assert.Null(EfModelCredentialStore.Project(Grant(baseUrl: "not a url"), default).BaseUrl);
        Assert.Null(EfModelCredentialStore.Project(Grant(), default).BaseUrl);
    }

    [Fact]
    public void TheProjectionNeverRendersItsSecret()
    {
        const string secret = "sk-ant-projection-should-never-print-this";

        var projected = EfModelCredentialStore.Project(Grant(), new ModelSecret(secret));

        Assert.DoesNotContain(secret, projected.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, $"a grant: {projected}", StringComparison.Ordinal);
        Assert.Equal(secret, projected.Secret.Reveal());
    }

    [Fact]
    public void OnlyKindsThatCanActuallyServeTheProviderAreQueriedFor()
    {
        // An openrouter/-qualified model can only be served by an OpenRouter key; everything else
        // can also fall back to one (section 20b.3, tier 5).
        Assert.Equal(
            CredentialKind.OpenRouterKey,
            Assert.Single(EfModelCredentialStore.ServingKinds(ModelProvider.OpenRouter)));

        var anthropic = EfModelCredentialStore.ServingKinds(ModelProvider.Anthropic);
        Assert.Contains(CredentialKind.AnthropicOauth, anthropic);
        Assert.Contains(CredentialKind.AnthropicApiKey, anthropic);
        Assert.Contains(CredentialKind.OpenRouterKey, anthropic);
        Assert.DoesNotContain(CredentialKind.GoogleApiKey, anthropic);
        Assert.DoesNotContain(CredentialKind.OpenAiApiKey, anthropic);

        foreach (var provider in new[]
                 {
                     ModelProvider.DeepSeek,
                     ModelProvider.Groq,
                     ModelProvider.AzureOpenAi,
                     ModelProvider.Ollama,
                     ModelProvider.OpenAiCompatible,
                 })
        {
            Assert.Contains(CredentialKind.CustomOpenAiCompatible, EfModelCredentialStore.ServingKinds(provider));
        }
    }
}

/// <summary>
/// The store against a real Postgres.
/// </summary>
/// <remarks>
/// Like the job queue's integration tests, these run only when <c>CHARTER_TEST_DATABASE_URL</c>
/// points at a throwaway database and skip otherwise. Every test creates its own organisation and
/// users, so a shared database stays usable and no test can see another's grants.
/// </remarks>
public class CredentialStoreIntegrationTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private static readonly ModelIdentifier Claude = ModelIdentifier.Parse("anthropic/claude-opus-5");

    [Fact]
    public async Task TheRequestersOwnGrantComesBackDecrypted()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        const string secret = "sk-ant-owner-key-value";
        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicOauth, secret);

        var candidates = await fixture.Store.GetCandidatesAsync(fixture.Query(), TestContext.Current.CancellationToken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(grant.Id.ToString(), candidate.Id);
        Assert.Equal(fixture.OwnerId.ToString(), candidate.OwnerUserId);
        Assert.Equal(fixture.OrgId.ToString(), candidate.OrganizationId);
        Assert.Equal(ModelCredentialKind.AnthropicOAuth, candidate.Kind);
        Assert.Equal(secret, candidate.Secret.Reveal());
    }

    [Fact]
    public async Task WhatIsStoredIsCiphertextAndNotTheSecret()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        const string secret = "sk-ant-at-rest-value";
        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicApiKey, secret);

        // Read the column back out of Postgres rather than off the tracked entity.
        var stored = await fixture.Db.CredentialGrants
            .AsNoTracking()
            .Where(candidate => candidate.Id == grant.Id)
            .Select(candidate => candidate.SecretEncrypted)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AesGcmCredentialProtector.CurrentVersion, stored[0]);
        Assert.Equal(-1, stored.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret)));
        Assert.Equal(secret, fixture.Protector.Unprotect(stored));
    }

    [Fact]
    public async Task AnotherUsersSubscriptionIsInvisibleUntilTheyPoolIt()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var theirs = await fixture.AddGrantAsync(
            CredentialKind.AnthropicOauth,
            "sk-ant-someone-elses-subscription",
            ownerId: fixture.OtherUserId);

        // Section 20b.5 and 20b.7: pooling a subscription is an explicit act, so before it happens
        // the grant must not even be a candidate for somebody else's session.
        Assert.Empty(await fixture.Store.GetCandidatesAsync(fixture.Query(), TestContext.Current.CancellationToken));

        theirs.JoinSharedPool();
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var pooled = Assert.Single(
            await fixture.Store.GetCandidatesAsync(fixture.Query(), TestContext.Current.CancellationToken));

        Assert.Equal(ModelCredentialScope.SharedPool, pooled.Scope);
        Assert.Equal(theirs.Id.ToString(), pooled.Id);
    }

    [Fact]
    public async Task TheOrganisationsMeteredKeysAndOpenRouterAreCandidates()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddGrantAsync(
            CredentialKind.AnthropicApiKey,
            "sk-ant-org-metered",
            ownerId: fixture.OtherUserId);
        await fixture.AddGrantAsync(
            CredentialKind.OpenRouterKey,
            "sk-or-org-fallback",
            ownerId: fixture.OtherUserId);

        var candidates = await fixture.Store.GetCandidatesAsync(fixture.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Kind == ModelCredentialKind.AnthropicApiKey);
        Assert.Contains(candidates, candidate => candidate.Kind == ModelCredentialKind.OpenRouterKey);
    }

    [Fact]
    public async Task AGrantThatCannotServeTheProviderIsNotACandidate()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddGrantAsync(CredentialKind.GoogleApiKey, "google-key");
        await fixture.AddGrantAsync(CredentialKind.OpenRouterKey, "sk-or-key");

        var forClaude = await fixture.Store.GetCandidatesAsync(
            fixture.Query(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCredentialKind.OpenRouterKey, Assert.Single(forClaude).Kind);

        var forGemini = await fixture.Store.GetCandidatesAsync(
            fixture.Query(ModelIdentifier.Parse("google/gemini-3-pro")),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, forGemini.Count);

        // An openrouter/-qualified model is OpenRouter's alone.
        var forOpenRouter = await fixture.Store.GetCandidatesAsync(
            fixture.Query(ModelIdentifier.Parse("openrouter/deepseek/deepseek-r1")),
            TestContext.Current.CancellationToken);

        Assert.Equal(ModelCredentialKind.OpenRouterKey, Assert.Single(forOpenRouter).Kind);
    }

    [Fact]
    public async Task ARevokedGrantIsNeverOffered()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicApiKey, "sk-ant-revoked");

        grant.Revoke();
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Section 20b.2: revocation is immediate.
        Assert.Empty(await fixture.Store.GetCandidatesAsync(fixture.Query(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGrantEncryptedUnderAnotherKeyIsSkippedRatherThanFailingEverySession()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var stranger = new AesGcmCredentialProtector(
            new Secret(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        fixture.Db.CredentialGrants.Add(CredentialGrant.Create(
            fixture.OrgId,
            fixture.OwnerId,
            CredentialKind.AnthropicApiKey,
            stranger.Protect("sk-ant-written-under-a-rotated-key")));
        await fixture.AddGrantAsync(CredentialKind.AnthropicApiKey, "sk-ant-readable");

        var candidates = await fixture.Store.GetCandidatesAsync(
            fixture.Query(),
            TestContext.Current.CancellationToken);

        // One unreadable row must not take the readable ones down with it.
        var candidate = Assert.Single(candidates);
        Assert.Equal("sk-ant-readable", candidate.Secret.Reveal());
    }

    [Fact]
    public async Task ExhaustionIsRecordedWithItsResetInstant()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicOauth, "sk-ant-exhausted");
        var until = DateTimeOffset.UtcNow.AddHours(3);

        await fixture.Store.MarkExhaustedAsync(
            grant.Id.ToString(),
            until,
            useOverflow: false,
            TestContext.Current.CancellationToken);

        var reloaded = await fixture.ReloadAsync(grant.Id);

        Assert.Equal(CredentialStatus.Exhausted, reloaded.Status);
        Assert.NotNull(reloaded.ExhaustedUntil);
        Assert.Equal(until.ToUnixTimeSeconds(), reloaded.ExhaustedUntil.Value.ToUnixTimeSeconds());
        Assert.False(reloaded.IsUsableAt(DateTimeOffset.UtcNow));
        Assert.True(reloaded.IsUsableAt(until.AddMinutes(1)));
    }

    [Fact]
    public async Task ExhaustionWithoutAResetHeaderStaysExhausted()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicOauth, "sk-ant-no-reset-header");

        await fixture.Store.MarkExhaustedAsync(
            grant.Id.ToString(),
            exhaustedUntil: null,
            useOverflow: true,
            TestContext.Current.CancellationToken);

        var reloaded = await fixture.ReloadAsync(grant.Id);

        Assert.Equal(CredentialStatus.Exhausted, reloaded.Status);
        Assert.False(reloaded.IsUsableAt(DateTimeOffset.UtcNow.AddYears(50)));
    }

    [Fact]
    public async Task AnAuthenticationFailureMarksTheGrantInvalid()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicApiKey, "sk-ant-rejected");

        await fixture.Store.MarkInvalidAsync(
            grant.Id.ToString(),
            "Provider rejected the credential with 401.",
            TestContext.Current.CancellationToken);

        var reloaded = await fixture.ReloadAsync(grant.Id);

        Assert.Equal(CredentialStatus.Invalid, reloaded.Status);
        Assert.Null(reloaded.ExhaustedUntil);
        Assert.False(reloaded.IsUsableAt(DateTimeOffset.UtcNow.AddYears(1)));
    }

    [Fact]
    public async Task AUsedGrantRecordsThatItWasUsed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var grant = await fixture.AddGrantAsync(CredentialKind.AnthropicOauth, "sk-ant-used");
        Assert.Null(grant.LastUsedAt);

        await fixture.Store.RecordUsageAsync(
            grant.Id.ToString(),
            new ModelUsage { InputTokens = 1_200, OutputTokens = 300 },
            new ModelCharge
            {
                Unit = ModelChargeUnit.SubscriptionQuota,
                Basis = ModelCostBasis.Estimated,
                NotionalCostUsd = 0.42m,
                CredentialId = grant.Id.ToString(),
                OwnerUserId = fixture.OwnerId.ToString(),
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull((await fixture.ReloadAsync(grant.Id)).LastUsedAt);
    }

    [Fact]
    public async Task AWriteBackAgainstAMissingGrantIsNotAnError()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // The failure path runs after a session has already gone wrong; a grant deleted underneath
        // it must not turn that into a second failure.
        await fixture.Store.MarkInvalidAsync(
            Guid.CreateVersion7().ToString(),
            "gone",
            TestContext.Current.CancellationToken);
        await fixture.Store.MarkExhaustedAsync(
            "not-even-a-guid",
            null,
            useOverflow: false,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheResolverWalksTheChainOverRealRows()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var own = await fixture.AddGrantAsync(CredentialKind.AnthropicOauth, "sk-ant-mine");
        await fixture.AddGrantAsync(
            CredentialKind.AnthropicApiKey,
            "sk-ant-org-metered",
            ownerId: fixture.OtherUserId);

        var resolver = new CredentialResolver(
            fixture.Store,
            TimeProvider.System,
            NullLogger<CredentialResolver>.Instance);

        var first = await resolver.ResolveAsync(fixture.Query(), TestContext.Current.CancellationToken);

        Assert.True(first.Resolved);
        Assert.Equal(ModelCredentialTier.RequesterSubscription, first.Credential!.Tier);
        Assert.Equal(own.Id.ToString(), first.Credential.Credential.Id);

        // Exhaust it and the chain falls through to the organisation's metered key.
        await fixture.Store.MarkExhaustedAsync(
            own.Id.ToString(),
            DateTimeOffset.UtcNow.AddHours(2),
            useOverflow: false,
            TestContext.Current.CancellationToken);

        var second = await resolver.ResolveAsync(fixture.Query(), TestContext.Current.CancellationToken);

        Assert.True(second.Resolved);
        Assert.Equal(ModelCredentialTier.OrganizationMeteredKey, second.Credential!.Tier);
        Assert.Equal("sk-ant-org-metered", second.Credential.Credential.Secret.Reveal());
    }

    [Fact]
    public async Task TheHighestPriorityPooledGrantIsTriedFirst()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.AddGrantAsync(
            CredentialKind.AnthropicOauth,
            "sk-ant-second-choice",
            ownerId: fixture.OtherUserId,
            scope: CredentialScope.SharedPool,
            priority: 1);
        await fixture.AddGrantAsync(
            CredentialKind.AnthropicOauth,
            "sk-ant-first-choice",
            ownerId: fixture.OtherUserId,
            scope: CredentialScope.SharedPool,
            priority: 9);

        var resolver = new CredentialResolver(
            fixture.Store,
            TimeProvider.System,
            NullLogger<CredentialResolver>.Instance);

        var resolution = await resolver.ResolveAsync(fixture.Query(), TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(ModelCredentialTier.OrganizationSharedPool, resolution.Credential!.Tier);
        Assert.Equal("sk-ant-first-choice", resolution.Credential.Credential.Secret.Reveal());
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(CharterDbContext db, ICredentialProtector protector, Guid orgId, Guid ownerId, Guid otherUserId)
        {
            Db = db;
            Protector = protector;
            OrgId = orgId;
            OwnerId = ownerId;
            OtherUserId = otherUserId;
            Store = new EfModelCredentialStore(
                db,
                protector,
                TimeProvider.System,
                NullLogger<EfModelCredentialStore>.Instance);
        }

        public CharterDbContext Db { get; }

        public ICredentialProtector Protector { get; }

        public EfModelCredentialStore Store { get; }

        public Guid OrgId { get; }

        public Guid OwnerId { get; }

        public Guid OtherUserId { get; }

        /// <summary>Returns null - and the caller returns green - when no test database is configured.</summary>
        public static async Task<StoreFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the credential store tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            // Its own organisation and its own users, so a shared database stays usable and no test
            // can see another's grants.
            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create($"credential-tests-{tag}");
            var owner = User.Create($"owner-{tag}@charter.invalid", "Owner");
            var other = User.Create($"other-{tag}@charter.invalid", "Other");

            db.Organizations.Add(organization);
            db.Users.Add(owner);
            db.Users.Add(other);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var protector = new AesGcmCredentialProtector(
                new Secret(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

            return new StoreFixture(db, protector, organization.Id, owner.Id, other.Id);
        }

        public ModelCredentialQuery Query(ModelIdentifier? model = null) =>
            new(model ?? Claude, OwnerId.ToString(), OrgId.ToString());

        public async Task<CredentialGrant> AddGrantAsync(
            CredentialKind kind,
            string secret,
            Guid? ownerId = null,
            CredentialScope scope = CredentialScope.Personal,
            int priority = 0)
        {
            var grant = CredentialGrant.Create(
                OrgId,
                ownerId ?? OwnerId,
                kind,
                Protector.Protect(secret),
                scope,
                priority: priority);

            Db.CredentialGrants.Add(grant);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return grant;
        }

        public async Task<CredentialGrant> ReloadAsync(Guid id) =>
            await Db.CredentialGrants
                .AsNoTracking()
                .SingleAsync(grant => grant.Id == id, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
