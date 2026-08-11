using Charter.Auth;
using Charter.Auth.Providers;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// The one-time setup token of section 30.1.
/// </summary>
/// <remarks>
/// Security-critical: a self-hosted app that boots with open registration gets hijacked by whoever
/// finds it first. The token is what closes that window, so single-use and expiry are not conveniences.
/// </remarks>
public class AuthSetupTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATokenIsLongAndUrlSafe()
    {
        var token = new SetupTokenStore(new ModelFakeTimeProvider(Now)).Issue();

        // 32 random bytes, base64url. Long enough that guessing is not a strategy, and pasteable out
        // of a container log without an escaping accident.
        Assert.InRange(token.Value.Length, 40, 64);
        Assert.DoesNotContain('+', token.Value);
        Assert.DoesNotContain('/', token.Value);
        Assert.DoesNotContain('=', token.Value);
    }

    [Fact]
    public void IssuingTwiceReturnsTheSameOutstandingToken()
    {
        // A restart reprints; a second call within one process must not invalidate the line the
        // operator is already looking at.
        var store = new SetupTokenStore(new ModelFakeTimeProvider(Now));

        Assert.Equal(store.Issue().Value, store.Issue().Value);
    }

    [Fact]
    public void ATokenWorksExactlyOnce()
    {
        var store = new SetupTokenStore(new ModelFakeTimeProvider(Now));
        var token = store.Issue();

        Assert.Equal(SetupTokenRejection.None, store.TryConsume(token.Value));
        Assert.Equal(SetupTokenRejection.AlreadyUsed, store.TryConsume(token.Value));
        Assert.Null(store.Current);
    }

    [Fact]
    public void AWrongValueDoesNotBurnTheToken()
    {
        var store = new SetupTokenStore(new ModelFakeTimeProvider(Now));
        var token = store.Issue();

        Assert.Equal(SetupTokenRejection.Mismatch, store.TryConsume("nonsense"));
        Assert.Equal(SetupTokenRejection.Mismatch, store.TryConsume(null));
        Assert.Equal(SetupTokenRejection.None, store.TryConsume(token.Value));
    }

    [Fact]
    public void ATokenExpires()
    {
        var clock = new ModelFakeTimeProvider(Now);
        var store = new SetupTokenStore(clock);
        var token = store.Issue();

        clock.Now = Now + SetupToken.Lifetime + TimeSpan.FromMinutes(1);

        Assert.True(token.IsExpired(clock.Now));
        Assert.Equal(SetupTokenRejection.Expired, store.TryConsume(token.Value));
    }

    [Fact]
    public void AnExpiredTokenIsReplacedRatherThanLeavingTheInstanceUnusable()
    {
        var clock = new ModelFakeTimeProvider(Now);
        var store = new SetupTokenStore(clock);
        var first = store.Issue();

        clock.Now = Now + SetupToken.Lifetime + TimeSpan.FromMinutes(1);
        var second = store.Issue();

        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal(SetupTokenRejection.Mismatch, store.TryConsume(first.Value));
        Assert.Equal(SetupTokenRejection.None, store.TryConsume(second.Value));
    }

    [Fact]
    public void NothingIsConsumableBeforeATokenIsIssued()
        => Assert.Equal(
            SetupTokenRejection.NotIssued,
            new SetupTokenStore(new ModelFakeTimeProvider(Now)).TryConsume("anything"));

    [Fact]
    public void TheStateLatchOnlyEverMovesOneWay()
    {
        var state = new SetupState();

        Assert.False(state.IsCompleted);

        state.MarkCompleted();
        state.MarkCompleted();

        Assert.True(state.IsCompleted);
    }
}

/// <summary>What an unclaimed instance is willing to serve (section 30.1).</summary>
public class AuthSetupModeMiddlewareTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/api/instance")]
    [InlineData("/api/setup")]
    [InlineData("/api/setup/complete")]
    [InlineData("/")]
    [InlineData("/setup")]
    [InlineData("/assets/index-abc123.js")]
    public void OnlyTheSetupRouteTheProbesAndTheStaticShellAreServed(string path)
        => Assert.True(SetupModeMiddleware.IsPermittedDuringSetup(new PathString(path)));

    [Theory]
    [InlineData("/api/requests")]
    [InlineData("/api/repos")]
    [InlineData("/api/sessions/abc/transcript")]
    [InlineData("/api/auth/password")]
    [InlineData("/api/members")]
    public void EveryOtherEndpointIsClosed(string path)
        => Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString(path)));

    [Fact]
    public void TheSetupPrefixIsNotAPrefixMatchOnAnythingElse()
        => Assert.False(SetupModeMiddleware.IsPermittedDuringSetup(new PathString("/api/setups-elsewhere")));
}

/// <summary>
/// Setup end to end against a real Postgres.
/// </summary>
/// <remarks>
/// Skips unless <c>CHARTER_TEST_DATABASE_URL</c> points at a throwaway database, following the same
/// pattern as the job queue's integration tests. The claims being made here — that a user existing
/// closes setup permanently, and that the token creates exactly one admin — are about rows, and
/// asserting them without rows would be asserting nothing.
/// </remarks>
public class AuthSetupIntegrationTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task TheTokenCreatesExactlyOneAdminAndThenSetupIsOverForever()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        Assert.True(await fixture.Mode.IsSetupRequiredAsync(TestContext.Current.CancellationToken));

        var token = fixture.Tokens.Issue();

        var result = await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("a-long-enough-password"),
                OrganizationName = "Acme",
            },
            TestContext.Current.CancellationToken);

        var completed = Assert.IsType<SetupResult.Completed>(result);

        var member = await fixture.Db.Members
            .SingleAsync(row => row.Id == completed.MemberId, TestContext.Current.CancellationToken);

        // Section 7.2: one member holding every role.
        Assert.Equal(Member.AllRoles.Order(), member.Roles.Order());
        Assert.Contains(MemberCapability.CanCreateRepo, member.Capabilities);

        // Section 7.5: approval gates auto-satisfied by policy, expressed as an ordinary row.
        var policy = await fixture.Db.AutoDispatchPolicies
            .SingleAsync(row => row.OrgId == completed.OrganizationId, TestContext.Current.CancellationToken);

        Assert.True(policy.Enabled);
        Assert.Null(policy.RepoId);
        Assert.Null(policy.Role);
        Assert.Null(policy.UserId);

        // Section 7.3: attributable to a named human, from the very first action.
        var audit = await fixture.Db.AuditLogs
            .SingleAsync(row => row.OrgId == completed.OrganizationId, TestContext.Current.CancellationToken);

        Assert.Equal(AuditActions.SetupCompleted, audit.Action);
        Assert.Equal(completed.UserId, audit.ActorUserId);

        // The password is stored as a hash and verifies; nothing anywhere holds the plaintext.
        var identity = await fixture.Db.Identities
            .SingleAsync(
                row => row.UserId == completed.UserId && row.Provider == IdentityProviderKind.Password,
                TestContext.Current.CancellationToken);

        Assert.NotNull(identity.SecretHash);
        Assert.DoesNotContain("a-long-enough-password", identity.SecretHash, StringComparison.Ordinal);
        Assert.Equal(
            PasswordVerification.Success,
            fixture.Hasher.Verify(identity.SecretHash!, new Secret("a-long-enough-password")));

        // Setup mode has ended permanently and cannot be re-entered while a user exists.
        Assert.False(await fixture.Mode.IsSetupRequiredAsync(TestContext.Current.CancellationToken));
        Assert.True(fixture.State.IsCompleted);

        // Section 34.9: personal mode gets no budgets at all. One person, their own credentials.
        Assert.Empty(await fixture.Db.Budgets.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The two budget variables of section 4.2 reach a row an admin can see.
    /// </summary>
    /// <remarks>
    /// They used to reach nothing: <c>AddCharterBudgets</c> registered its own <c>BudgetOptions</c>
    /// through <c>TryAdd</c> with a hardcoded 5 and 500, and no host projected <c>BudgetConfig</c>
    /// into it, so an operator who set a cap silently got a different one. Asserting on the seeded
    /// row rather than on the options record is deliberate - the options record being right is not
    /// the same claim as the budget being right.
    /// </remarks>
    [Fact]
    public async Task AnOrganisationIsSeededWithTheConfiguredBudget()
    {
        await using var fixture = await SetupFixture.CreateAsync(
            ("CHARTER_MODE", "organization"),
            ("CHARTER_DEFAULT_MONTHLY_BUDGET_USD", "2500"),
            ("CHARTER_DEFAULT_SESSION_BUDGET_USD", "12.50"));

        if (fixture is null)
        {
            return;
        }

        var completed = Assert.IsType<SetupResult.Completed>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = fixture.Tokens.Issue().Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("a-long-enough-password"),
                OrganizationName = "Acme",
            },
            TestContext.Current.CancellationToken));

        var budget = await fixture.Db.Budgets
            .SingleAsync(row => row.OrgId == completed.OrganizationId, TestContext.Current.CancellationToken);

        Assert.Equal(2500m, budget.Amount);
        Assert.Equal(12.50m, budget.ApprovalThreshold);

        // Section 34.9: available, not imposed. The shipped budget routes to an approver rather than
        // refusing, and it covers every category including chat (section 34.6).
        Assert.Equal(BudgetBehaviour.RequireApproval, budget.Behaviour);
        Assert.Equal(BudgetScopeType.Org, budget.ScopeType);
        Assert.Equal(BudgetPeriod.Monthly, budget.Period);
        Assert.Empty(budget.Categories);
    }

    [Fact]
    public async Task SetupIsUnreachableOnceAnyUserExists()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Somebody else claimed the instance first. Even holding a valid token, setup refuses.
        var token = fixture.Tokens.Issue();

        fixture.Db.Users.Add(User.Create(fixture.Email, "Someone Else"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(await fixture.Mode.IsSetupRequiredAsync(TestContext.Current.CancellationToken));

        var result = await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = "attacker@example.com",
                DisplayName = "Attacker",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<SetupResult.Rejected>(result);
        Assert.Equal(SetupRejection.AlreadyCompleted, rejected.Reason);

        // And nothing was created.
        Assert.False(await fixture.Db.Organizations.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrongTokenCreatesNothing()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        fixture.Tokens.Issue();

        var result = await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = "not-the-token",
                Email = fixture.Email,
                DisplayName = "Attacker",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<SetupResult.Rejected>(result);
        Assert.Equal(SetupRejection.Token, rejected.Reason);
        Assert.False(await fixture.Db.Users.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheTokenCreatesExactlyOneAdminAndNotTwo()
    {
        // The route redeeming this is the one unauthenticated endpoint that creates an
        // administrator, so "exactly one" is checked against the rows rather than against the store.
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var token = fixture.Tokens.Issue();

        Assert.IsType<SetupResult.Completed>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken));

        // A second redemption of the same value, racing or replayed, creates nothing. Three separate
        // things refuse it; this asserts the outcome all three exist for.
        var second = Assert.IsType<SetupResult.Rejected>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = "second@example.com",
                DisplayName = "Second Admin",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(SetupRejection.AlreadyCompleted, second.Reason);

        Assert.Equal(1, await fixture.Db.Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Db.Members.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Db.Organizations.CountAsync(TestContext.Current.CancellationToken));

        // And the token itself is gone, so a restart is what it takes to get another.
        Assert.Null(fixture.Tokens.Current);
        Assert.Equal(SetupTokenRejection.AlreadyUsed, fixture.Tokens.TryConsume(token.Value));
    }

    [Fact]
    public async Task SetupModeCannotBeReEnteredOnARunningProcess()
    {
        // Section 30.1: setup mode ends permanently. Deleting every user would still not reopen it
        // here, which is the conservative direction to be wrong in — and it is the difference
        // between a bad afternoon and a stranger claiming the instance.
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var token = fixture.Tokens.Issue();

        Assert.IsType<SetupResult.Completed>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken));

        Assert.True(fixture.State.IsCompleted);

        await fixture.Db.Identities.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await fixture.Db.AuditLogs.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await fixture.Db.Members.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await fixture.Db.Users.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        Assert.False(await fixture.Db.Users.AnyAsync(TestContext.Current.CancellationToken));

        // Zero users, and setup is still over: the latch, not the row count, is what decides.
        Assert.False(await fixture.Mode.IsSetupRequiredAsync(TestContext.Current.CancellationToken));

        // And even a freshly issued token cannot claim the instance a second time on this process.
        var refused = Assert.IsType<SetupResult.Rejected>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = fixture.Tokens.Issue().Value,
                Email = "usurper@example.com",
                DisplayName = "Usurper",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(SetupRejection.AlreadyCompleted, refused.Reason);
        Assert.False(await fixture.Db.Users.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AShortPasswordIsRefusedBeforeTheTokenIsSpent()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var token = fixture.Tokens.Issue();

        var rejected = Assert.IsType<SetupResult.Rejected>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("short"),
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(SetupRejection.Password, rejected.Reason);

        // The token survives a rejected request, so a typo does not cost the operator a restart.
        Assert.IsType<SetupResult.Completed>(await fixture.Service.CompleteAsync(
            new SetupRequest
            {
                Token = token.Value,
                Email = fixture.Email,
                DisplayName = "First Admin",
                Password = new Secret("a-long-enough-password"),
            },
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A setup service pointed at a throwaway database, in a schema of its own.
    /// </summary>
    /// <remarks>
    /// Setup asks "are there zero users in this database", which no amount of row namespacing can
    /// make safe to share. Each test therefore gets its own Postgres schema via the search path, so
    /// the tests are isolated from each other and from whatever else is in the container.
    /// </remarks>
    private sealed class SetupFixture : IAsyncDisposable
    {
        private readonly string schema;

        private SetupFixture(CharterDbContext db, string schema, CharterConfig config)
        {
            Db = db;
            this.schema = schema;
            State = new SetupState();
            Tokens = new SetupTokenStore(TimeProvider.System);
            Mode = new SetupModeService(db, State);
            Hasher = new CharterPasswordHasher(iterationCount: 1_000);
            Service = new SetupService(
                db,
                Tokens,
                State,
                Mode,
                Hasher,
                config,
                BudgetLimitsServiceCollectionExtensions.From(config),
                TimeProvider.System);
            Email = $"admin-{schema}@example.com";
        }

        public CharterDbContext Db { get; }

        public SetupState State { get; }

        public SetupTokenStore Tokens { get; }

        public SetupModeService Mode { get; }

        public ICharterPasswordHasher Hasher { get; }

        public SetupService Service { get; }

        public string Email { get; }

        public static async Task<SetupFixture?> CreateAsync(params (string Key, string? Value)[] environment)
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the setup integration tests.");
                return null;
            }

            var schema = $"auth_setup_{Guid.CreateVersion7():N}";

            var admin = new CharterDbContext(Configure(url, schema: null));
            await admin.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var create = CreateSchemaSql(schema);
            await admin.Database.ExecuteSqlRawAsync(create, TestContext.Current.CancellationToken);
            await admin.DisposeAsync();

            var db = new CharterDbContext(Configure(url, schema));
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var config = ConfigTestEnvironment.Valid(
                environment.Length > 0 ? environment : [("CHARTER_MODE", "personal")]);

            return new SetupFixture(db, schema, config);
        }

        /// <summary>
        /// A schema name is an identifier, so it cannot be a bound parameter. The value is generated
        /// from a GUID a line above and never comes from input, and it is still quoted and escaped.
        /// </summary>
        private static string CreateSchemaSql(string schema) => string.Concat("CREATE SCHEMA ", Quote(schema));

        private static string DropSchemaSql(string schema)
            => string.Concat("DROP SCHEMA IF EXISTS ", Quote(schema), " CASCADE");

        private static string Quote(string identifier)
            => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

        private static DbContextOptions<CharterDbContext> Configure(string url, string? schema)
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(DatabaseUrl.ToNpgsql(url));

            if (schema is not null)
            {
                builder.SearchPath = schema;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, builder.ConnectionString);

            return options.Options;
        }

        public async ValueTask DisposeAsync()
        {
            var drop = DropSchemaSql(schema);
            await Db.Database.ExecuteSqlRawAsync(drop);
            await Db.DisposeAsync();
        }
    }
}
