using System.Text;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Charter.Runners;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// <c>POST /api/runners/sessions/{id}/credentials</c>: who may mint, for which session, and when
/// (sections 7.4, 16, 20b.2).
/// </summary>
/// <remarks>
/// <para>
/// This is the most security-sensitive route in the product: it mints a contribute-scoped GitHub
/// token and a twelve-hour event token. The bearer secret it authenticates is <em>per repository</em>
/// — Charter writes one value into <c>secrets.CHARTER_SESSION_SECRET</c> and every workflow run in
/// that repository reads the same one. Authenticating a repository is not authorising a session, and
/// the difference is the whole of what these tests are about: a run started for one session must not
/// be able to name another live session of the same repository and be handed its credentials, its
/// transcript-writing token and the ability to declare it failed.
/// </para>
/// <para>
/// The event token already reached that standard — it carries the session id and a signature over it
/// — so the exchange is held to the same one by a per-session dispatch token, minted at dispatch and
/// delivered only to the run for that session.
/// </para>
/// </remarks>
public class RunnerCredentialExchangeTests
{
    private const string SigningKey = "a-test-signing-key-of-sufficient-length-000000";

    [Fact]
    public async Task ARunExchangesItsOwnSessionsCredentials()
    {
        // The control. Everything below asserts a refusal, and a refusal proves nothing if the happy
        // path does not work.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(world.SessionId);

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Contains(ExchangeWorld.GitHubToken, response.Body, StringComparison.Ordinal);
        Assert.Equal([world.RepoFullName], world.Broker.Issued);

        // And what comes back is bound to this session, not reusable against another.
        var eventToken = ReadJson(response.Body, "event_token");
        Assert.True(world.Tokens.ValidateEventToken(world.SessionId, eventToken));
        Assert.False(world.Tokens.ValidateEventToken(world.OtherSessionId, eventToken));
    }

    [Fact]
    public async Task TheRepositorySecretDoesNotMintForAnotherSessionOfTheSameRepository()
    {
        // Both sessions are live, dispatched, and in the same repository, so the only thing standing
        // between one run and the other's credentials is the session binding.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(
            world.OtherSessionId,
            sessionToken: world.Tokens.DispatchTokenFor(world.SessionId));

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Empty(world.Broker.Issued);

        // No event token either: the ability to write another session's transcript and to report it
        // failed is exactly as damaging as the repository token, and arrives in the same response.
        Assert.DoesNotContain(ExchangeWorld.GitHubToken, response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithNoSessionTokenAtAllIsRefusedWithSomethingActionable()
    {
        // The shape a target repository on the old workflow file produces. It must fail loudly at the
        // first step and say what to update, rather than 500 or — far worse — succeed.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(world.SessionId, sessionToken: null);

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Contains("agent-session.yml", response.Body, StringComparison.Ordinal);
        Assert.Empty(world.Broker.Issued);
    }

    [Fact]
    public async Task AForgedSessionTokenIsRefused()
    {
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Another instance's key. The signature is the only thing that makes a dispatch token one.
        var elsewhere = new RunnerSessionTokens("a-different-instances-signing-key-00000000000");

        var response = await world.ExchangeAsync(
            world.SessionId,
            sessionToken: elsewhere.DispatchTokenFor(world.SessionId));

        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Empty(world.Broker.Issued);
    }

    [Fact]
    public async Task AnotherRepositorysSecretIsRefused()
    {
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(
            world.SessionId,
            secret: world.Tokens.SessionSecretFor("someone-else/repo"));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Status);
        Assert.Empty(world.Broker.Issued);
    }

    [Theory]
    [InlineData(SessionStatus.Failed)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Merged)]
    [InlineData(SessionStatus.NoChangesNeeded)]
    public async Task ATerminalSessionMintsNothing(SessionStatus status)
    {
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.MoveAsync(world.SessionId, status);

        var response = await world.ExchangeAsync(world.SessionId);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Empty(world.Broker.Issued);
    }

    [Fact]
    public async Task ASessionWithACancelInFlightMintsNothing()
    {
        // Section 11: cancel is a request first, and the terminal state follows once the runner has
        // been stopped and cost settled. A twelve-hour token minted in that window outlives the thing
        // that justified it by most of a day.
        await using var world = await ExchangeWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.MoveAsync(world.SessionId, cancelRequested: true);

        var response = await world.ExchangeAsync(world.SessionId);

        Assert.Equal(StatusCodes.Status404NotFound, response.Status);
        Assert.Empty(world.Broker.Issued);
    }

    [Fact]
    public async Task ASessionNoBackendHoldsMintsNothing()
    {
        await using var world = await ExchangeWorld.CreateAsync(dispatched: false);
        if (world is null)
        {
            return;
        }

        var response = await world.ExchangeAsync(world.SessionId);

        Assert.Equal(StatusCodes.Status409Conflict, response.Status);
        Assert.Empty(world.Broker.Issued);
    }

    [Fact]
    public void ADispatchTokenIsBoundToOneSessionAndToThisInstance()
    {
        var mine = new RunnerSessionTokens(SigningKey);
        var theirs = new RunnerSessionTokens("a-different-instances-signing-key-00000000000");
        var session = Guid.NewGuid();

        Assert.True(mine.ValidateDispatchToken(session, mine.DispatchTokenFor(session)));
        Assert.False(mine.ValidateDispatchToken(Guid.NewGuid(), mine.DispatchTokenFor(session)));
        Assert.False(mine.ValidateDispatchToken(session, theirs.DispatchTokenFor(session)));
        Assert.False(mine.ValidateDispatchToken(session, null));
        Assert.False(mine.ValidateDispatchToken(session, "nonsense"));

        // And it is not the repository secret under another name: one holder of the secret must not
        // be able to derive the other factor.
        Assert.NotEqual(mine.SessionSecretFor("acme/widgets"), mine.DispatchTokenFor(session));
    }

    private static string? ReadJson(string body, string property)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }
}

/// <summary>
/// Two live, dispatched sessions in one repository, and the endpoint under test.
/// </summary>
/// <remarks>
/// Against real Postgres in a schema of its own. The subject is a decision taken from rows — session
/// status, cancel request, the journal's dispatch claim — so substituting the store would substitute
/// the thing being tested.
/// </remarks>
internal sealed class ExchangeWorld : IAsyncDisposable
{
    public const string GitHubToken = "ghs_scoped_to_one_repo";

    private readonly TestSchema _schema;

    private ExchangeWorld(TestSchema schema, CharterDbContext db, Guid sessionId, Guid otherSessionId, string repo)
    {
        _schema = schema;
        Db = db;
        SessionId = sessionId;
        OtherSessionId = otherSessionId;
        RepoFullName = repo;
    }

    public CharterDbContext Db { get; }

    public Guid SessionId { get; }

    /// <summary>A second session of the same repository, equally live and equally dispatched.</summary>
    public Guid OtherSessionId { get; }

    public string RepoFullName { get; }

    /// <summary>
    /// A second repository connected to the same instance, with no session of its own.
    /// </summary>
    /// <remarks>
    /// The target of the cross-repository write: the instance holds a credential for it, so a call
    /// Charter can be talked into making against it is one that will succeed.
    /// </remarks>
    public string OtherRepoFullName { get; } = "someone-else/private-repo";

    /// <summary>A run URL for this session's own repository. The shape the shipped workflow reports.</summary>
    public string OwnRunUrl => $"https://github.com/{RepoFullName}/actions/runs/900100";

    /// <summary>The same shape, naming the other connected repository. A lie, and the whole defect.</summary>
    public string CrossRepoRunUrl => $"https://github.com/{OtherRepoFullName}/actions/runs/900100";

    public RunnerSessionTokens Tokens { get; } = new("a-test-signing-key-of-sufficient-length-000000");

    public RecordingCredentialBroker Broker { get; } = new();

    public static async Task<ExchangeWorld?> CreateAsync(bool dispatched = true)
    {
        var schema = await TestSchema.CreateAsync(TestContext.Current.CancellationToken);
        if (schema is null)
        {
            return null;
        }

        var token = TestContext.Current.CancellationToken;
        var db = schema.NewContext();
        var now = DateTimeOffset.UtcNow;

        const string repoFullName = "acme/widgets";

        var org = Organization.Create("acme");
        var user = User.Create("ayesha@example.test", "Ayesha", now: now);
        var repo = Repo.Connect(org.Id, 4211, repoFullName, now: now);

        // A second repository the instance holds a credential for. Nothing in these tests runs a
        // session in it; it exists to be the thing a lying run URL points at.
        var elsewhere = Repo.Connect(org.Id, 4212, "someone-else/private-repo", now: now);

        db.Organizations.Add(org);
        db.Users.Add(user);
        db.Repos.Add(repo);
        db.Repos.Add(elsewhere);

        var first = Seed(db, org.Id, repo.Id, user.Id, now);
        var second = Seed(db, org.Id, repo.Id, user.Id, now);

        await db.SaveChangesAsync(token);

        var journal = new SessionJournal(db);

        if (dispatched)
        {
            foreach (var session in new[] { first, second })
            {
                await journal.AppendAsync(
                    session,
                    OrchestrationEventTypes.SessionDispatched,
                    """{"runner":"github-actions","generation":0}""",
                    $"dispatch:{session:D}:0",
                    cancellationToken: token);
            }
        }

        db.ChangeTracker.Clear();

        return new ExchangeWorld(schema, db, first, second, repoFullName);
    }

    /// <summary>Calls the endpoint exactly as the shipped workflow's first step does.</summary>
    public async Task<(int Status, string Body)> ExchangeAsync(
        Guid sessionId,
        string? secret = null,
        string? sessionToken = "",
        string? runUrl = null)
    {
        Db.ChangeTracker.Clear();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Headers.Authorization = "Bearer " + (secret ?? Tokens.SessionSecretFor(RepoFullName));

        var result = await RunnerCallbackEndpoints.ExchangeCredentialsAsync(
            sessionId,
            new CredentialExchangeRequest
            {
                SessionId = sessionId.ToString("D"),
                RunUrl = runUrl,

                // The empty default means "the token this session's own dispatch carried"; null means
                // a workflow that sends none at all.
                SessionToken = sessionToken is "" ? Tokens.DispatchTokenFor(sessionId) : sessionToken,
            },
            context,
            Db,
            new SessionJournal(Db),
            Tokens,
            Broker,
            provider.GetRequiredService<ILoggerFactory>(),
            TestContext.Current.CancellationToken);

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    /// Posts one event exactly as the shipped workflow's <em>Report session started</em> step does.
    /// </summary>
    /// <remarks>
    /// The event token is the one the exchange hands out, and it is what a shim holds in
    /// <c>CHARTER_EVENT_TOKEN</c> — reachable by prompt injection, since the agent runs untrusted
    /// repository content in the same sandbox (section 16). So everything in the body below is under
    /// an attacker's control, and the token being valid says only which session is speaking.
    /// </remarks>
    public async Task<(int Status, string Body)> IngestAsync(
        Guid sessionId,
        string type,
        string payloadJson,
        string? eventToken = null)
    {
        Db.ChangeTracker.Clear();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Headers.Authorization = "Bearer " + (eventToken ?? Tokens.IssueEventToken(sessionId));

        var journal = new SessionJournal(Db);
        var options = new OrchestrationOptions();

        var result = await RunnerCallbackEndpoints.IngestEventAsync(
            sessionId,
            new RunnerEventRequest
            {
                SessionId = sessionId.ToString("D"),
                Type = type,
                Payload = System.Text.Json.JsonDocument.Parse(payloadJson).RootElement.Clone(),
            },
            context,
            Db,
            journal,
            new SessionMilestones(Db, CharterTime.System),
            Tokens,
            new NeedsInputAnnouncer(
                Db,
                journal,
                new JobQueue(Db),
                options,
                CharterTime.System,
                provider.GetRequiredService<ILogger<NeedsInputAnnouncer>>()),
            provider.GetRequiredService<ILoggerFactory>(),
            TestContext.Current.CancellationToken);

        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>What the orchestrator would hand a backend to cancel with (section 11).</summary>
    public async Task<string?> ExternalReferenceAsync(Guid sessionId)
    {
        Db.ChangeTracker.Clear();

        var summary = await new SessionJournal(Db).SummarizeAsync(sessionId, TestContext.Current.CancellationToken);

        return summary.ExternalReference;
    }

    /// <summary>Writes an event straight to the journal, bypassing every callback check.</summary>
    /// <remarks>
    /// How a row poisoned by a Charter that predates the callback validation gets into a test. The
    /// fix at the recording points cannot reach rows already in the database, so something downstream
    /// has to hold as well.
    /// </remarks>
    public async Task PoisonAsync(Guid sessionId, string runUrl)
    {
        await new SessionJournal(Db).AppendAsync(
            sessionId,
            EventTypes.SessionStarted,
            $$"""{"run_url":{{System.Text.Json.JsonSerializer.Serialize(runUrl)}}}""",
            $"poison:{sessionId:D}",
            cancellationToken: TestContext.Current.CancellationToken);

        Db.ChangeTracker.Clear();
    }

    public async Task MoveAsync(Guid sessionId, SessionStatus? status = null, bool cancelRequested = false)
    {
        var session = await Db.Sessions.FirstAsync(row => row.Id == sessionId, TestContext.Current.CancellationToken);

        if (status is { } moved)
        {
            session.TransitionTo(moved);
        }

        if (cancelRequested)
        {
            session.RequestCancellation();
        }

        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _schema.DisposeAsync();
    }

    private static Guid Seed(CharterDbContext db, Guid orgId, Guid repoId, Guid userId, DateTimeOffset now)
    {
        var request = Request.File(orgId, repoId, userId, "Remember the last selected vertical", now: now);

        var spec = Spec.Draft(
            request.Id,
            1,
            "Remember the last selected vertical",
            "The wizard opens on the vertical you used last time.",
            "## Approach\nPersist the selection.",
            """["Vertical is pre-selected on return"]""",
            now: now);

        var session = Session.Queue(spec.Id, RunnerKind.GitHubActions, "anthropic/claude-opus-5", now: now);

        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);

        return session.Id;
    }
}

/// <summary>Records what a mint was asked for. Nothing here reaches GitHub.</summary>
internal sealed class RecordingCredentialBroker : IRunnerCredentialBroker
{
    public List<string> Issued { get; } = [];

    public Task<RunnerCredentials> IssueAsync(
        Guid sessionId,
        string repoFullName,
        CancellationToken cancellationToken = default)
    {
        Issued.Add(repoFullName);
        return Task.FromResult(new RunnerCredentials(ExchangeWorld.GitHubToken, "sk-test-model-key"));
    }
}
