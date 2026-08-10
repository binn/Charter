using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Charter.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// A throwaway Postgres schema for the orchestration tests.
/// </summary>
/// <remarks>
/// <para>
/// Its own schema rather than the shared database, for one reason: the session orchestrator's job is
/// to sweep <em>every</em> live session, so pointing it at a database other tests are also writing
/// sessions into would have it reconciling their rows. Isolating the schema keeps the sweep honest —
/// it really does look at everything — without letting it reach outside the test.
/// </para>
/// <para>
/// Skips, and the suite stays green, when <c>CHARTER_TEST_DATABASE_URL</c> is unset. Advisory locks,
/// <c>SKIP LOCKED</c> and lease expiry have no in-memory equivalent, so there is nothing useful to
/// fall back to.
/// </para>
/// </remarks>
internal sealed class OrchestrationDatabase : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private OrchestrationDatabase(string connectionString, string schema)
    {
        ConnectionString = connectionString;
        Schema = schema;
    }

    public string ConnectionString { get; }

    public string Schema { get; }

    public static async Task<OrchestrationDatabase?> CreateAsync(CancellationToken cancellationToken)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the orchestration tests.");
            return null;
        }

        var schema = $"charter_orch_{Guid.NewGuid():N}"[..40];
        var baseConnectionString = DatabaseUrl.ToNpgsql(url);

        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\";", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var scoped = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema }
            .ConnectionString;

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, scoped);

        await using (var db = new CharterDbContext(options.Options))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        return new OrchestrationDatabase(scoped, schema);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = null };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{Schema}\" CASCADE;", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (NpgsqlException)
        {
            // A dropped schema that will not drop is a test-server problem, not a test failure.
        }
    }
}

/// <summary>
/// One control-plane process: its own container, its own dispatcher, its own orchestrator.
/// </summary>
/// <remarks>
/// Disposing it is how the tests simulate the container going away mid-session. Nothing survives the
/// dispose except Postgres, which is precisely the claim section 2.3 makes about a real deployment,
/// so a second instance built over the same database is a genuine restart rather than a re-run of the
/// same objects.
/// </remarks>
internal sealed class ControlPlaneInstance : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private ControlPlaneInstance(ServiceProvider provider, RecordingRunner runner, OrchestrationOptions options)
    {
        _provider = provider;
        Runner = runner;
        Options = options;
    }

    public RecordingRunner Runner { get; }

    public OrchestrationOptions Options { get; }

    public QueueDispatcher Dispatcher => _provider.GetRequiredService<QueueDispatcher>();

    public SessionOrchestrator Orchestrator => _provider.GetRequiredService<SessionOrchestrator>();

    /// <summary>
    /// Builds an instance. Pass the same <paramref name="runner"/> to two instances to count
    /// dispatches across a restart, and the same <paramref name="lockKey"/> to make them compete.
    /// </summary>
    public static ControlPlaneInstance Create(
        OrchestrationDatabase database,
        RecordingRunner? runner = null,
        long? lockKey = null,
        string? workerId = null,
        TimeSpan? lease = null)
    {
        var backend = runner ?? new RecordingRunner();

        var options = new OrchestrationOptions
        {
            DispatcherLockKey = lockKey ?? Random.Shared.NextInt64(),
            WorkerId = workerId ?? $"worker-{Guid.NewGuid():N}",
            Lease = lease ?? TimeSpan.FromMinutes(5),
            BaseUrl = new Uri("https://charter.example.test/"),
        };

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<CharterDbContext>(
            builder => DataServiceCollectionExtensions.ConfigureNpgsql(builder, database.ConnectionString),
            ServiceLifetime.Scoped);

        services.AddScoped<JobQueue>();
        services.AddScoped<SessionJournal>();
        services.AddScoped<ISessionDispatchPlanner, SessionDispatchPlanner>();
        services.AddScoped<SessionCoordinator>();
        services.AddScoped<IQueuedJobHandler, BuildJobHandler>();

        // Section 7.5's spend gate, resolved from the same rows an admin edits. Registered rather
        // than stubbed so a test that writes an auto-dispatch policy row gets the real answer.
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ICharterAuthorizationService, CharterAuthorizationService>();
        services.AddScoped<IAutoDispatchGate, AutoDispatchGate>();

        services.AddSingleton(options);
        services.AddSingleton<IAgentRunner>(backend);
        services.AddSingleton<IRunnerRegistry>(provider => new RunnerRegistry(provider.GetServices<IAgentRunner>()));
        services.AddSingleton<QueueDispatcher>();
        services.AddSingleton<SessionOrchestrator>();

        return new ControlPlaneInstance(services.BuildServiceProvider(), backend, options);
    }

    /// <summary>Runs <paramref name="work"/> in a fresh scope, as a request or a timer tick would.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>Seeds the aggregate a session hangs off, and returns the session id.</summary>
    public async Task<Guid> SeedSessionAsync(
        CancellationToken cancellationToken,
        string repoFullName = "acme/spectra",
        string? configSnapshot = null)
    {
        return await InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();

            var organization = Organization.Create("Acme");
            var user = User.Create($"{Guid.NewGuid():N}@example.test", "Ayesha");
            var repo = Repo.Connect(organization.Id, 42, repoFullName);

            if (configSnapshot is not null)
            {
                repo.RecordConfigSnapshot(configSnapshot);
            }

            var request = Request.File(organization.Id, repo.Id, user.Id, "Remember the last selected vertical");
            var spec = Spec.Draft(
                request.Id,
                1,
                "Remember the last selected vertical",
                "The wizard opens on the vertical you used last time.",
                "## Approach\nPersist the selection.",
                """["Vertical is pre-selected on return"]""");

            var session = Session.Queue(spec.Id, RunnerKind.GitHubActions, "anthropic/claude-opus-5");

            db.Organizations.Add(organization);
            db.Users.Add(user);
            db.Repos.Add(repo);
            db.Requests.Add(request);
            db.Specs.Add(spec);
            db.Sessions.Add(session);

            await db.SaveChangesAsync(cancellationToken);
            return session.Id;
        });
    }

    /// <summary>
    /// Seeds everything the spend gate leaves behind: a request, a specification, and no session.
    /// </summary>
    /// <remarks>
    /// The state the control plane is in the instant after somebody presses Approve. Whether the
    /// specification carries an approver is the whole difference between section 7.5's two paths, so
    /// it is the parameter.
    /// </remarks>
    public async Task<SpendGateSeed> SeedApprovedSpecAsync(
        CancellationToken cancellationToken,
        bool approved = true,
        bool autoDispatchPolicy = false,
        string repoFullName = "acme/spectra")
    {
        return await InScopeAsync(async provider =>
        {
            var db = provider.GetRequiredService<CharterDbContext>();

            var organization = Organization.Create("Acme");
            var user = User.Create($"{Guid.NewGuid():N}@example.test", "Ayesha");
            var repo = Repo.Connect(organization.Id, 42, repoFullName);
            repo.TransitionTo(RepoStatus.Ready);

            var member = Member.Create(organization.Id, user.Id, [MemberRole.Requester]);

            var request = Request.File(organization.Id, repo.Id, user.Id, "Remember the last selected vertical");
            request.TransitionTo(approved ? RequestStatus.Queued : RequestStatus.SpecReady);

            var spec = Spec.Draft(
                request.Id,
                1,
                "Remember the last selected vertical",
                "The wizard opens on the vertical you used last time.",
                "## Approach\nPersist the selection.",
                """["Vertical is pre-selected on return"]""");

            if (approved)
            {
                spec.Approve(user.Id);
            }

            db.Organizations.Add(organization);
            db.Users.Add(user);
            db.Members.Add(member);
            db.Repos.Add(repo);
            db.Requests.Add(request);
            db.Specs.Add(spec);

            if (autoDispatchPolicy)
            {
                db.AutoDispatchPolicies.Add(AutoDispatchPolicy.Create(organization.Id, enabled: true));
            }

            await db.SaveChangesAsync(cancellationToken);

            return new SpendGateSeed(organization.Id, user.Id, repo.Id, request.Id, spec.Id);
        });
    }

    /// <summary>Writes the build job the spend gate writes: a request and a specification, no session.</summary>
    public Task<Job> EnqueueSpecBuildAsync(SpendGateSeed approved, CancellationToken cancellationToken)
        => InScopeAsync(provider => provider.GetRequiredService<SessionCoordinator>().EnqueueSpecAsync(
            new SpecBuildPayload { RequestId = approved.RequestId, SpecId = approved.SpecId },
            cancellationToken));

    /// <summary>Every session in the database. The count is what the no-double-dispatch claim rests on.</summary>
    public Task<List<Session>> SessionsAsync(CancellationToken cancellationToken)
        => InScopeAsync(async provider => await provider.GetRequiredService<CharterDbContext>()
            .Sessions
            .AsNoTracking()
            .ToListAsync(cancellationToken));

    public Task<Job> EnqueueBuildAsync(Guid sessionId, CancellationToken cancellationToken)
        => InScopeAsync(provider => provider.GetRequiredService<SessionCoordinator>().EnqueueAsync(
            new BuildJobPayload { SessionId = sessionId },
            cancellationToken: cancellationToken));

    public Task<SessionJournalSummary> SummarizeAsync(Guid sessionId, CancellationToken cancellationToken)
        => InScopeAsync(provider => provider.GetRequiredService<SessionJournal>()
            .SummarizeAsync(sessionId, cancellationToken));

    public Task<IReadOnlyList<SessionJournalEntry>> EventsAsync(Guid sessionId, CancellationToken cancellationToken)
        => InScopeAsync(provider => provider.GetRequiredService<SessionJournal>()
            .ReadAsync(sessionId, limit: 1000, cancellationToken: cancellationToken));

    /// <summary>Ingests an event the way the <c>/events</c> callback does: keyed, and idempotent.</summary>
    public Task<JournalAppend> IngestRunnerEventAsync(
        Guid sessionId,
        long index,
        string type,
        string payload,
        CancellationToken cancellationToken)
        => InScopeAsync(provider => provider.GetRequiredService<SessionJournal>().AppendAsync(
            sessionId,
            type,
            payload,
            $"runner:{index}",
            cancellationToken: cancellationToken));

    public Task<Session?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        => InScopeAsync(async provider => await provider.GetRequiredService<CharterDbContext>()
            .Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken));

    public Task<List<Job>> JobsAsync(CancellationToken cancellationToken)
        => InScopeAsync(async provider => await provider.GetRequiredService<CharterDbContext>()
            .Jobs
            .AsNoTracking()
            .ToListAsync(cancellationToken));

    public async ValueTask DisposeAsync()
    {
        // Stop the dispatcher the way the host does on shutdown: drain, hand back claims, release
        // the advisory lock (section 31).
        await Dispatcher.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }

    /// <summary>Disposes without the graceful path: this is what a killed container looks like.</summary>
    public async ValueTask KillAsync() => await _provider.DisposeAsync();
}

/// <summary>What the spend gate leaves behind, before anything has been built.</summary>
internal sealed record SpendGateSeed(
    Guid OrganizationId,
    Guid RequesterId,
    Guid RepoId,
    Guid RequestId,
    Guid SpecId);
