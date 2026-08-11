using System.Threading.Channels;
using Charter.Auth.Providers;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Runners;
using Charter.Runners.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// Every test class that stands up an <see cref="AgentPlaneFixture"/>.
/// </summary>
/// <remarks>
/// One collection, so they never run alongside each other. Two of them would otherwise share both
/// the <c>runner_agents</c> table and a wall clock, and <see cref="Charter.Runners.Agent.AgentRunner"/>
/// advertises every online agent on the instance rather than every online agent in an organisation —
/// so one class's connected agent would legitimately show up in another's capability union.
/// </remarks>
[CollectionDefinition(AgentPlaneCollection.Name, DisableParallelization = true)]
public sealed class AgentPlaneCollection
{
    public const string Name = "Agent plane";
}

/// <summary>A clock the test moves by hand, so leases and TTLs need no waiting.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset now) => _now = now.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now.ToUniversalTime();
}

/// <summary>
/// The agent's side of a connection, in memory.
/// </summary>
/// <remarks>
/// The control plane's half of section 33 is a frame exchange, so the interesting behaviour — lease
/// renewal, capability-filtered granting, protocol refusal, close codes — is reachable without a
/// listening port. That is the whole reason <see cref="IAgentChannel"/> exists as a seam.
/// </remarks>
public sealed class LoopbackAgentChannel : IAgentChannel
{
    private readonly Channel<Envelope> _toPlane = Channel.CreateUnbounded<Envelope>();
    private readonly Channel<Envelope> _fromPlane = Channel.CreateUnbounded<Envelope>();

    /// <summary>The close code the agent would have seen. Null while the socket is open.</summary>
    public int? CloseCode { get; private set; }

    public string? CloseReason { get; private set; }

    /// <summary>Pushes a frame as if the agent had sent it.</summary>
    public void Send(Envelope envelope) => _toPlane.Writer.TryWrite(envelope);

    /// <summary>The agent's process went away without a goodbye.</summary>
    public void Disconnect() => _toPlane.Writer.TryComplete();

    /// <summary>Waits for the next frame the control plane wrote.</summary>
    public async Task<Envelope> NextAsync(TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));

        try
        {
            return await _fromPlane.Reader.ReadAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
            throw new Xunit.Sdk.XunitException(
                "The control plane sent no frame. Close code: " + (CloseCode?.ToString() ?? "none"));
        }
    }

    /// <summary>Waits for the next frame and asserts its type.</summary>
    public async Task<Envelope> ExpectAsync(string type)
    {
        var envelope = await NextAsync();
        Assert.Equal(type, envelope.Type);
        return envelope;
    }

    /// <summary>Everything the plane has written so far and nobody has read.</summary>
    public bool TryRead(out Envelope? envelope) => _fromPlane.Reader.TryRead(out envelope);

    Task IAgentChannel.SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        _fromPlane.Writer.TryWrite(envelope);
        return Task.CompletedTask;
    }

    async Task<Envelope?> IAgentChannel.ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _toPlane.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    Task IAgentChannel.CloseAsync(int closeCode, string reason, CancellationToken cancellationToken)
    {
        CloseCode = closeCode;
        CloseReason = reason;
        _toPlane.Writer.TryComplete();
        _fromPlane.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The per-job credentials broker, stubbed.
/// </summary>
/// <remarks>
/// The real one needs a GitHub App. What matters for section 33.5 is not where the token came from
/// but that exactly one repository's token and one model key cross the wire, and that neither is
/// ever written to a row — both of which are testable against a stub.
/// </remarks>
public sealed class StubRunnerCredentialBroker : IRunnerCredentialBroker
{
    public const string GitHubToken = "ghs_test_scoped_to_one_repo";

    public const string ModelApiKey = "sk-test-scoped-model-key";

    /// <summary>Every repository a token was minted for, so a test can prove there was only one.</summary>
    public List<string> Issued { get; } = [];

    public bool Throw { get; set; }

    public Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("No GitHub App is configured.");
        }

        Issued.Add(repoFullName);
        return Task.FromResult(new RunnerCredentials(GitHubToken, ModelApiKey));
    }
}

/// <summary>
/// A control plane with the agent plane wired up, against a throwaway Postgres.
/// </summary>
/// <remarks>
/// Skips when <c>CHARTER_TEST_DATABASE_URL</c> is unset, the same pattern as
/// <c>DataJobQueueTests</c>, so a developer without Docker still gets a green build. Every fixture
/// gets its own organisation and its own capability tag, so a shared database stays usable and one
/// test's claims can never satisfy another's.
/// </remarks>
public sealed class AgentPlaneFixture : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    /// <summary>
    /// The schema is brought up once per process, not once per fixture.
    /// </summary>
    /// <remarks>
    /// Three dozen fixtures each running <c>MigrateAsync</c> against one Postgres is three dozen
    /// transactions contending for the migrations history table while every other integration test is
    /// also creating and dropping schemas. Migrating once is both faster and the difference between a
    /// suite that is deterministic and one that fails somewhere different every third run.
    /// </remarks>
    private static readonly Lazy<Task> Migrated = new(
        () =>
        {
            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(
                options,
                DatabaseUrl.ToNpgsql(Environment.GetEnvironmentVariable(DatabaseUrlVariable)!));

            return MigrateAsync(options.Options);
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ServiceProvider _services;
    private readonly string _connectionString;
    private readonly string? _schema;

    private AgentPlaneFixture(
        ServiceProvider services,
        Guid orgId,
        string tag,
        TestClock clock,
        string connectionString,
        string? schema)
    {
        _services = services;
        OrgId = orgId;
        Tag = tag;
        Clock = clock;
        _connectionString = connectionString;
        _schema = schema;
    }

    public Guid OrgId { get; }

    /// <summary>
    /// A capability no other test advertises, so this fixture's jobs are invisible to them.
    /// </summary>
    public string Tag { get; }

    public TestClock Clock { get; }

    public IServiceProvider Services => _services;

    public AgentPlaneOptions Options => _services.GetRequiredService<AgentPlaneOptions>();

    public AgentConnectionRegistry Connections => _services.GetRequiredService<AgentConnectionRegistry>();

    public StubRunnerCredentialBroker Broker =>
        (StubRunnerCredentialBroker)_services.GetRequiredService<IRunnerCredentialBroker>();

    public AgentRunner Runner => (AgentRunner)_services.GetServices<IAgentRunner>().First(r => r.Kind == RunnerKind.Agent);

    public AsyncServiceScope Scope() => _services.CreateAsyncScope();

    /// <summary>Runs one unit of work in its own scope, the way a frame handler does.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Scope();
        return await work(scope.ServiceProvider);
    }

    public async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = Scope();
        await work(scope.ServiceProvider);
    }

    /// <summary>Section 33.3 step 1, then step 2: an invitation spent for a credential.</summary>
    public async Task<(Guid AgentId, string Token)> PairAsync(
        string name = "mac-mini",
        IReadOnlyList<string>? capabilities = null,
        int concurrency = 2,
        string mode = "docker")
    {
        var invitation = await InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(OrgId, name, cancellationToken: TestContext.Current.CancellationToken));

        var result = await InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>().PairAsync(
                PairRequestFor(invitation.PairingToken, name, capabilities, concurrency, mode),
                TestContext.Current.CancellationToken));

        Assert.True(result.Ok, result.Message);
        return (invitation.AgentId, result.Response!.AgentToken);
    }

    public PairRequest PairRequestFor(
        string pairingToken,
        string name = "mac-mini",
        IReadOnlyList<string>? capabilities = null,
        int concurrency = 2,
        string mode = "docker",
        int protocolVersion = AgentProtocol.Version) =>
        new()
        {
            PairingToken = pairingToken,
            Name = name,
            Mode = mode,
            AgentVersion = "0.1.0-test",
            ProtocolVersion = protocolVersion,
            Concurrency = concurrency,
            Platform = Platform,
            Capabilities = capabilities ?? ["linux", "docker", Tag],
        };

    public HostPlatform Platform { get; } = new()
    {
        Os = "linux",
        Arch = "x64",
        Rid = "linux-x64",
        Hostname = "runner.test",
        CpuCount = 8,
    };

    /// <summary>Starts a connection over an in-memory channel and hands back both ends.</summary>
    public (LoopbackAgentChannel Channel, Task Run) Connect(Guid agentId)
    {
        var channel = new LoopbackAgentChannel();

        var run = AgentPlaneEndpoints.RunAsync(
            agentId,
            channel,
            Connections,
            Options,
            _services.GetRequiredService<IServiceScopeFactory>(),
            Clock,
            _services.GetRequiredService<ILoggerFactory>(),
            CancellationToken.None);

        return (channel, run);
    }

    /// <summary>The <c>hello</c> a freshly started daemon would send.</summary>
    public Envelope Hello(
        IReadOnlyList<string>? capabilities = null,
        int concurrency = 2,
        int protocolVersion = AgentProtocol.Version,
        IReadOnlyList<int>? supported = null,
        IReadOnlyList<string>? heldJobIds = null) =>
        Envelope.Create(
            MessageTypes.Hello,
            new HelloPayload
            {
                ProtocolVersion = protocolVersion,
                SupportedProtocolVersions = supported ?? [protocolVersion],
                AgentVersion = "0.1.0-test",
                Name = "mac-mini",
                Mode = "docker",
                Concurrency = concurrency,
                Platform = Platform,
                Capabilities = capabilities ?? ["linux", "docker", Tag],
                CapabilitiesProbedAt = Clock.GetUtcNow(),
                HeldJobIds = heldJobIds ?? [],
            },
            Clock.GetUtcNow());

    /// <summary>Completes the handshake and returns the welcome the plane answered with.</summary>
    public async Task<WelcomePayload> HandshakeAsync(
        LoopbackAgentChannel channel,
        IReadOnlyList<string>? capabilities = null,
        int concurrency = 2,
        IReadOnlyList<string>? heldJobIds = null)
    {
        channel.Send(Hello(capabilities, concurrency, heldJobIds: heldJobIds));
        var welcome = await channel.ExpectAsync(MessageTypes.Welcome);
        return welcome.ReadPayload<WelcomePayload>()!;
    }

    /// <summary>
    /// Enqueues one agent-claimable job the way <see cref="AgentRunner"/> would.
    /// </summary>
    /// <remarks>
    /// Including the state <see cref="AgentRunner.DispatchAsync"/> is only ever called in: a live
    /// session with its dispatch claim already in the journal, because <c>SessionCoordinator</c>
    /// writes that claim before any backend is called. Credentials are minted only for such a session
    /// (<see cref="SessionCredentialGuard"/>), so a job without one would be refused — correctly, and
    /// for a reason that has nothing to do with whatever the test is about.
    /// </remarks>
    public async Task<Guid> EnqueueClaimableAsync(
        Guid sessionId,
        IEnumerable<string>? requires = null,
        string repo = "acme/widgets",
        bool seedSession = true)
    {
        if (seedSession)
        {
            await SeedSessionAsync(repo, sessionId, dispatched: true);
        }

        var payload = new AgentJobPayload
        {
            SessionId = sessionId,
            RepoFullName = repo,
            CloneUrl = $"https://github.com/{repo}.git",
            BaseBranch = "main",
            BaseCommitSha = "a3f9c21",
            AdapterId = "claude-code",
            Model = "openrouter/deepseek/deepseek-r1",
            CallbackUrl = "https://charter.test/api/runners/sessions/" + sessionId.ToString("D"),
            SpecUrl = "https://charter.test/api/runners/sessions/" + sessionId.ToString("D") + "/spec",
            RequiredCapabilities = [.. requires ?? []],
            TimeoutMinutes = 60,
            DispatchKey = "dispatch:" + sessionId.ToString("D"),
        };

        var required = new List<string> { AgentRunner.ClaimCapability, Tag };
        required.AddRange(RunnerCapability.ExpandAll(requires ?? []));

        return await InScopeAsync(async provider =>
        {
            var job = await provider.GetRequiredService<JobQueue>().EnqueueAsync(
                JobType.Build,
                payload.ToJson(),
                requiredCapabilities: required,
                now: Clock.GetUtcNow(),
                cancellationToken: TestContext.Current.CancellationToken);

            return job.Id;
        });
    }

    /// <summary>
    /// Seeds the aggregate a session hangs off, and returns the session id.
    /// </summary>
    /// <remarks>
    /// Needed wherever a test asserts that something was <em>not</em> written to a session: without a
    /// real row the write would fail on a foreign key anyway, and the test would pass for a reason
    /// that has nothing to do with the behaviour under test.
    /// </remarks>
    /// <param name="repoFullName">The repository the session's request was filed against.</param>
    /// <param name="sessionId">An explicit id, for when a queue row already names one.</param>
    /// <param name="dispatched">
    /// Whether to write the journal's dispatch claim. True is the state every backend is called in —
    /// <c>SessionCoordinator</c> writes the claim before calling one — and false is a session nothing
    /// has dispatched, which may never be given credentials.
    /// </param>
    public Task<Guid> SeedSessionAsync(
        string repoFullName = "acme/widgets",
        Guid? sessionId = null,
        bool dispatched = false) =>
        InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();
            var token = TestContext.Current.CancellationToken;
            var now = Clock.GetUtcNow();

            if (sessionId is { } existing && await db.Sessions.AsNoTracking().AnyAsync(row => row.Id == existing, token))
            {
                return existing;
            }

            var user = User.Create($"{Guid.NewGuid():N}@example.test", "Ayesha", now: now);

            // One repository row per name: ux_repos_org_id_full_name is unique, and a fixture seeds
            // several sessions against the same repository.
            var repo = await db.Repos.FirstOrDefaultAsync(
                row => row.OrgId == OrgId && row.FullName == repoFullName,
                token);

            if (repo is null)
            {
                repo = Repo.Connect(OrgId, Random.Shared.Next(1, int.MaxValue), repoFullName, now: now);
                db.Repos.Add(repo);
            }

            var request = Request.File(OrgId, repo.Id, user.Id, "Remember the last selected vertical", now: now);

            var spec = Spec.Draft(
                request.Id,
                1,
                "Remember the last selected vertical",
                "The wizard opens on the vertical you used last time.",
                "## Approach\nPersist the selection.",
                """["Vertical is pre-selected on return"]""",
                now: now);

            var session = Session.Queue(
                spec.Id,
                RunnerKind.Agent,
                "openrouter/deepseek/deepseek-r1",
                now: now,
                id: sessionId);

            db.Users.Add(user);
            db.Requests.Add(request);
            db.Specs.Add(spec);
            db.Sessions.Add(session);

            await db.SaveChangesAsync(token);

            if (dispatched)
            {
                await MarkDispatchedAsync(provider, session.Id);
            }

            return session.Id;
        });

    /// <summary>Writes the dispatch claim <c>SessionCoordinator</c> writes before calling a backend.</summary>
    public Task MarkDispatchedAsync(Guid sessionId)
        => InScopeAsync(provider => MarkDispatchedAsync(provider, sessionId));

    /// <summary>Moves a session, the way settlement or the cancel button would.</summary>
    public Task MoveSessionAsync(Guid sessionId, SessionStatus? status = null, bool cancelRequested = false)
        => InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();
            var session = await db.Sessions.FirstAsync(
                row => row.Id == sessionId,
                TestContext.Current.CancellationToken);

            if (status is { } moved)
            {
                session.TransitionTo(moved, Clock.GetUtcNow());
            }

            if (cancelRequested)
            {
                session.RequestCancellation(Clock.GetUtcNow());
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    private static async Task MarkDispatchedAsync(IServiceProvider provider, Guid sessionId)
        => await provider.GetRequiredService<Charter.Orchestration.SessionJournal>().AppendAsync(
            sessionId,
            Charter.Orchestration.OrchestrationEventTypes.SessionDispatched,
            """{"runner":"agent","generation":0}""",
            $"dispatch:{sessionId:D}:0",
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Reads a job row back, untracked.</summary>
    public Task<Job?> JobAsync(Guid jobId) =>
        InScopeAsync(async provider => await provider.GetRequiredService<CharterDbContext>()
            .Jobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == jobId, TestContext.Current.CancellationToken));

    public Task<RunnerAgent?> AgentAsync(Guid agentId) =>
        InScopeAsync(async provider => await provider.GetRequiredService<CharterDbContext>()
            .RunnerAgents.AsNoTracking()
            .FirstOrDefaultAsync(agent => agent.Id == agentId, TestContext.Current.CancellationToken));

    /// <summary>Returns null — and the caller returns green — when no test database is configured.</summary>
    public static Task<AgentPlaneFixture?> CreateAsync(Action<AgentPlaneOptions>? configure = null)
        => CreateAsync(isolated: false, configure);

    /// <summary>
    /// As above, optionally in a schema of its own.
    /// </summary>
    /// <param name="isolated">
    /// <see langword="true"/> to migrate a private schema and drop it on dispose. Costs one migration,
    /// and buys a <c>jobs</c> table nobody else is enqueueing into — which a test whose subject is
    /// <em>what a claim sweeps up</em> needs, because a job requiring no capabilities is claimable by
    /// every worker in the suite and the shared queue makes such a test both flaky and infectious.
    /// </param>
    /// <param name="configure">Options overrides, as for the shared-database form.</param>
    public static async Task<AgentPlaneFixture?> CreateAsync(
        bool isolated,
        Action<AgentPlaneOptions>? configure = null)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the agent plane tests.");
            return null;
        }

        // Started from the wall clock rather than a fixed instant, because the `jobs` table is shared
        // with every other integration test and lease expiry is evaluated against absolute time. A
        // fixture living in 2026-08-10 09:00 while everyone else lives in the present would have its
        // claims swept away by the first sweep another test ran.
        var clock = new TestClock(DateTimeOffset.UtcNow);

        var schema = isolated ? $"charter_agent_{Guid.NewGuid():N}"[..40] : null;
        var connectionString = schema is null
            ? DatabaseUrl.ToNpgsql(url)
            : await CreateSchemaAsync(DatabaseUrl.ToNpgsql(url), schema);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterData(connectionString);

        // PBKDF2 at production parameters is the right cost on a connect and the wrong cost in a
        // test that pairs a dozen agents. The construction under test is the same either way.
        services.AddSingleton<ICharterPasswordHasher>(_ => new CharterPasswordHasher(iterationCount: 1));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IRunnerCredentialBroker, StubRunnerCredentialBroker>();
        services.AddSingleton(new RunnerSessionTokens("charter-test-secret-key-0123456789"));

        // Registered by AddCharterOrchestration in the real host, and needed here for the same reason:
        // without it, a frame handler that streams an event into the journal fails on a missing service
        // rather than on the behaviour under test — which would let a guard pass for being unreachable.
        services.AddScoped<Charter.Orchestration.SessionJournal>();
        services.AddCharterAgentPlane(options =>
        {
            // Shorter than Job.DefaultLease on purpose. A lease-expiry test advances the clock past
            // this and runs the queue's sweep, which is global — a minute keeps that inside the five
            // minutes every other test's claims are good for, so the sweep proves the mechanism
            // without reaching into anybody else's rows.
            options.Lease = TimeSpan.FromSeconds(60);
            options.HeartbeatInterval = TimeSpan.FromSeconds(15);
            configure?.Invoke(options);
        });

        var provider = services.BuildServiceProvider();

        var tag = $"t{Guid.CreateVersion7():N}";
        var orgId = Guid.CreateVersion7();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharterDbContext>();

            if (schema is null)
            {
                await Migrated.Value;
            }
            else
            {
                await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            db.Organizations.Add(Organization.Create($"agent-plane-{tag}", id: orgId, now: clock.GetUtcNow()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new AgentPlaneFixture(provider, orgId, tag, clock, connectionString, schema);
    }

    /// <summary>Creates a private schema and returns a connection string whose search path is it.</summary>
    private static async Task<string> CreateSchemaAsync(string connectionString, string schema)
    {
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\";", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
    }

    private static async Task MigrateAsync(DbContextOptions<CharterDbContext> options)
    {
        await using var db = new CharterDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();

        if (_schema is null)
        {
            return;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_connectionString) { SearchPath = null };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);

            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE;", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (NpgsqlException)
        {
            // A schema that will not drop is a test-server problem, not a test failure.
        }
    }
}
