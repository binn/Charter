using System.Net;
using System.Text.Json;
using Charter.Configuration;
using Charter.Data;
using Charter.Deployments;
using Charter.Domain;
using Charter.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>An <see cref="ILogger{T}"/> that keeps every entry, with its level.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>DNS, decided by the test rather than by the internet.</summary>
/// <remarks>
/// Everything resolves to a documentation address in TEST-NET-3 unless a test says otherwise, which
/// is a public address and therefore the boring case. A test that cares names its own answer — the
/// interesting one being a perfectly ordinary hostname that resolves to <c>169.254.169.254</c>.
/// </remarks>
internal sealed class StubPreviewHostResolver : IPreviewHostResolver
{
    /// <summary>What an unmapped name resolves to: a routable, public address.</summary>
    public static readonly IPAddress Public = IPAddress.Parse("203.0.113.10");

    private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every host this resolver was asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Points <paramref name="host"/> at <paramref name="addresses"/>.</summary>
    public StubPreviewHostResolver Map(string host, params string[] addresses)
    {
        _answers[host] = [.. addresses.Select(IPAddress.Parse)];
        return this;
    }

    /// <summary>Makes <paramref name="host"/> resolve to nothing at all.</summary>
    public StubPreviewHostResolver Unresolvable(string host)
    {
        _answers[host] = [];
        return this;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Asked.Add(host);

        return ValueTask.FromResult<IReadOnlyList<IPAddress>>(
            _answers.TryGetValue(host, out var mapped) ? mapped : [Public]);
    }
}

/// <summary>
/// A Railway that answers by what it was asked rather than by what order it was asked in.
/// </summary>
/// <remarks>
/// The lifecycle loop reconciles every open change request in the instance, and the throwaway
/// database these tests run against is shared with the rest of the suite. A queue of canned responses
/// would therefore depend on how many other rows happen to be in the database, which is the kind of
/// test that passes for a week and then fails for a reason nobody can reproduce. This answers each
/// GraphQL document on its content, so the assertions are about one change request no matter how many
/// others exist.
/// </remarks>
internal sealed class RailwayStubHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _deploymentsByEnvironment = new(StringComparer.Ordinal);

    private string _environments = """{"data":{"environments":{"edges":[]}}}""";

    public List<string> RequestBodies { get; } = [];

    /// <summary>What the project's environment list looks like.</summary>
    public RailwayStubHandler WithEnvironments(params string[] nodes)
    {
        _environments = "{\"data\":{\"environments\":{\"edges\":[" + string.Join(",", nodes) + "]}}}";
        return this;
    }

    /// <summary>What one environment's deployments look like.</summary>
    public RailwayStubHandler WithDeployment(string environmentId, string status, string url, string commit)
    {
        _deploymentsByEnvironment[environmentId] =
            "{\"data\":{\"deployments\":{\"edges\":[{\"node\":{\"id\":\"dep_1\",\"status\":\"" + status +
            "\",\"staticUrl\":\"" + url + "\",\"meta\":{\"commitHash\":\"" + commit + "\"}}}]}}}";

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        RequestBodies.Add(body);

        var json = Answer(body);

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private string Answer(string body)
    {
        if (body.Contains("environmentDelete", StringComparison.Ordinal))
        {
            return """{"data":{"environmentDelete":true}}""";
        }

        if (body.Contains("query Environments", StringComparison.Ordinal))
        {
            return _environments;
        }

        foreach (var (environmentId, deployments) in _deploymentsByEnvironment)
        {
            if (body.Contains($"\"{environmentId}\"", StringComparison.Ordinal))
            {
                return deployments;
            }
        }

        return """{"data":{"deployments":{"edges":[]}}}""";
    }
}

/// <summary>
/// A whole request that has reached the point section 18 cares about: an approved spec, a session,
/// and a change request with a head commit that a hosting platform could report a preview for.
/// </summary>
/// <remarks>
/// Built from the real domain factories rather than from EF stubs, so the head commit these tests
/// bind against is a commit a session genuinely recorded. The acceptance criteria are deliberately
/// distinctive: the "what to check" list on section 27.7's card is rendered from them verbatim, and a
/// fixture with placeholder text would make that assertion vacuous.
/// </remarks>
internal sealed class DeploymentScenario
{
    public const string HeadSha = "f00dcafe1234567890abcdef1234567890abcdef";
    public const string HeadBranch = "charter/remember-last-vertical";
    public const string RepoFullName = "northbeam/quote-tool";
    public const int Number = 142;

    /// <summary>The contract the requester approved. Rendered verbatim, never regenerated.</summary>
    public static IReadOnlyList<string> Criteria { get; } =
    [
        "Starting a new quote pre-selects the vertical you chose on your previous quote.",
        "A person who has never created a quote still starts on Solar.",
    ];

    private DeploymentScenario(
        Organization organization,
        User user,
        Member member,
        Repo repo,
        Request request,
        Spec spec,
        Session session,
        ChangeRequest changeRequest)
    {
        Organization = organization;
        User = user;
        Member = member;
        Repo = repo;
        Request = request;
        Spec = spec;
        Session = session;
        ChangeRequest = changeRequest;
    }

    public Organization Organization { get; }

    public User User { get; }

    public Member Member { get; }

    public Repo Repo { get; }

    public Request Request { get; }

    public Spec Spec { get; }

    public Session Session { get; }

    public ChangeRequest ChangeRequest { get; }

    /// <summary>Builds the graph, unsaved.</summary>
    public static DeploymentScenario Build(DateTimeOffset now, ChangeRequestState state = ChangeRequestState.Open)
    {
        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization, now);
        var user = User.Create($"ayesha+{tag}@example.com", "Ayesha Rahman", TeachingLevel.SkipTheBasics, now);
        var member = Member.Create(organization.Id, user.Id, [MemberRole.Requester], now: now);

        var repo = Repo.Connect(organization.Id, 42, RepoFullName, "main", now);
        repo.TransitionTo(RepoStatus.Ready, now);

        var request = Request.File(
            organization.Id,
            repo.Id,
            user.Id,
            "every time i start a new quote it makes me pick solar again",
            null,
            now);

        // Where section 6 puts a request whose change request is open and whose preview is being
        // built. A preview binding to a request still in Draft would not be a scenario.
        request.TransitionTo(RequestStatus.PrOpen, now);

        var spec = Spec.Draft(
            request.Id,
            1,
            "Remember the last selected vertical",
            "When you start a new quote, the vertical you chose last time is already selected.",
            "The wizard reads the requester's last quote and pre-selects its vertical.",
            JsonSerializer.Serialize(Criteria),
            now: now);

        spec.Approve(user.Id, now);

        var session = Session.Queue(spec.Id, RunnerKind.GitHubActions, "anthropic/claude-opus-5", now: now);
        session.Start(HeadSha, now);

        var changeRequest = ChangeRequest.Open(
            session.Id,
            Number,
            $"https://github.com/{RepoFullName}/pull/{Number}",
            HeadSha,
            state,
            now,
            headBranch: HeadBranch);

        return new DeploymentScenario(organization, user, member, repo, request, spec, session, changeRequest);
    }
}

/// <summary>
/// A throwaway database with one scenario in it, rolled back on dispose.
/// </summary>
/// <remarks>
/// Skips when <c>CHARTER_TEST_DATABASE_URL</c> is unset, so a developer without Docker still gets a
/// green build. Everything happens inside one transaction, so a shared database stays usable.
/// </remarks>
internal sealed class DeploymentFixture : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private readonly IDbContextTransaction _transaction;

    private DeploymentFixture(CharterDbContext database, DeploymentScenario scenario, IDbContextTransaction transaction)
    {
        Db = database;
        Scenario = scenario;
        _transaction = transaction;
    }

    public CharterDbContext Db { get; }

    public DeploymentScenario Scenario { get; }

    public Guid SessionId => Scenario.Session.Id;

    /// <summary>The clock every service in the fixture reads.</summary>
    public ModelFakeTimeProvider Clock { get; private set; } = new(DateTimeOffset.UnixEpoch);

    /// <summary>
    /// How preview host names resolve for everything this fixture builds.
    /// </summary>
    /// <remarks>
    /// Stubbed rather than left to DNS for two reasons. It makes the suite deterministic and offline —
    /// <c>quote-tool-pr-142.up.railway.app</c> is not a name anybody owns — and it is the only way to
    /// pin the case section 16.3 is actually about: an ordinary-looking name that resolves to somewhere
    /// on the operator's own network.
    /// </remarks>
    public StubPreviewHostResolver Resolver { get; } = new();

    /// <summary>The URL gate, reading this fixture's resolver.</summary>
    public PreviewUrlPolicy Urls(DeploymentOptions? options = null)
        => new(options ?? DeploymentOptions.WebhookOnly, Resolver, NullLogger<PreviewUrlPolicy>.Instance);

    /// <summary>What section 18's generic webhook writes.</summary>
    public DeploymentBinder Binder(DeploymentOptions? options = null)
        => new(Db, Clock, NullLogger<DeploymentBinder>.Instance, Urls(options));

    /// <summary>Everything the requester was told, so section 6's two states can be counted.</summary>
    public RecordingNotificationService Notifications { get; } = new();

    /// <summary>Section 6's <c>PreviewReady</c> transition, over this fixture's clock.</summary>
    public PreviewReadyAnnouncer Announcer(DeploymentOptions options, bool notify = true)
        => new(
            Db,
            options,
            Clock,
            NullLogger<PreviewReadyAnnouncer>.Instance,
            notify ? Notifications : null);

    /// <summary>The publisher, with a probe that answers from <paramref name="handler"/>.</summary>
    public PreviewArtifactPublisher Publisher(
        DeploymentOptions options,
        StubHttpMessageHandler? handler = null)
        => new(
            Db,
            options,
            new PreviewReachabilityProbe(
                new StubHttpClientFactory(handler ?? new StubHttpMessageHandler()),
                options,
                NullLogger<PreviewReachabilityProbe>.Instance,
                Urls(options)),
            Clock,
            NullLogger<PreviewArtifactPublisher>.Instance,
            Announcer(options),
            Urls(options));

    /// <summary>Both ingestion paths, wired to <paramref name="providers"/>.</summary>
    public DeploymentIngestor Ingestor(
        DeploymentOptions options,
        DeploymentProviderRegistry? providers = null,
        StubHttpMessageHandler? handler = null)
        => new(
            Db,
            Binder(options),
            Publisher(options, handler),
            providers ?? new DeploymentProviderRegistry([]),
            NullLogger<DeploymentIngestor>.Instance);

    /// <summary>Expiry queries and the sweep.</summary>
    public PreviewExpiry Expiry(DeploymentProviderRegistry? providers = null)
        => new(Db, providers ?? new DeploymentProviderRegistry([]), Clock, NullLogger<PreviewExpiry>.Instance);

    public static async Task<DeploymentFixture?> CreateAsync(
        DateTimeOffset? now = null,
        ChangeRequestState state = ChangeRequestState.Open)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);

        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the deployment binding tests.");
            return null;
        }

        var at = now ?? new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

        var database = new CharterDbContext(options.Options);
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var transaction = await database.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var scenario = DeploymentScenario.Build(at, state);

        database.Organizations.Add(scenario.Organization);
        database.Users.Add(scenario.User);
        database.Members.Add(scenario.Member);
        database.Repos.Add(scenario.Repo);
        database.Requests.Add(scenario.Request);
        database.Specs.Add(scenario.Spec);
        database.Sessions.Add(scenario.Session);
        database.ChangeRequests.Add(scenario.ChangeRequest);

        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();

        return new DeploymentFixture(database, scenario, transaction) { Clock = new ModelFakeTimeProvider(at) };
    }

    /// <summary>The session's hosted preview artifact, freshly read.</summary>
    public async Task<VerificationArtifact?> PreviewAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.VerificationArtifacts
            .AsNoTracking()
            .Where(row => row.SessionId == SessionId && row.Kind == VerificationArtifactKind.HostedPreview)
            .OrderBy(row => row.CreatedAt)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await Db.DisposeAsync();
    }
}

/// <summary>The scaffolding itself, so a failure elsewhere is never the fixture lying.</summary>
public class DeploymentFixtureTests
{
    [Fact]
    public void TheScenarioCarriesAnApprovedSpecAndAHeadCommitToBindAgainst()
    {
        var scenario = DeploymentScenario.Build(DateTimeOffset.UnixEpoch);

        Assert.True(scenario.Spec.IsApproved);
        Assert.Equal(DeploymentScenario.HeadSha, scenario.ChangeRequest.HeadSha);
        Assert.Equal(DeploymentScenario.Number, scenario.ChangeRequest.Number);
        Assert.Equal(
            JsonSerializer.Serialize(DeploymentScenario.Criteria),
            scenario.Spec.AcceptanceCriteria);
    }

    [Fact]
    public void TheRecordingLoggerKeepsLevelsApart()
    {
        var logger = new RecordingLogger<DeploymentFixtureTests>();

        logger.LogInformation("quiet");
        logger.LogWarning("loud");

        Assert.Equal(2, logger.Entries.Count);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }
}
