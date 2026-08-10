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

    private AgentPlaneFixture(ServiceProvider services, Guid orgId, string tag, TestClock clock)
    {
        _services = services;
        OrgId = orgId;
        Tag = tag;
        Clock = clock;
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

    /// <summary>Enqueues one agent-claimable job the way <see cref="AgentRunner"/> would.</summary>
    public async Task<Guid> EnqueueClaimableAsync(
        Guid sessionId,
        IEnumerable<string>? requires = null,
        string repo = "acme/widgets")
    {
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
    public static async Task<AgentPlaneFixture?> CreateAsync(Action<AgentPlaneOptions>? configure = null)
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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterData(DatabaseUrl.ToNpgsql(url));

        // PBKDF2 at production parameters is the right cost on a connect and the wrong cost in a
        // test that pairs a dozen agents. The construction under test is the same either way.
        services.AddSingleton<ICharterPasswordHasher>(_ => new CharterPasswordHasher(iterationCount: 1));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IRunnerCredentialBroker, StubRunnerCredentialBroker>();
        services.AddSingleton(new RunnerSessionTokens("charter-test-secret-key-0123456789"));
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

            await Migrated.Value;

            db.Organizations.Add(Organization.Create($"agent-plane-{tag}", id: orgId, now: clock.GetUtcNow()));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new AgentPlaneFixture(provider, orgId, tag, clock);
    }

    private static async Task MigrateAsync(DbContextOptions<CharterDbContext> options)
    {
        await using var db = new CharterDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
