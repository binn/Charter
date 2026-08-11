using System.Reflection;
using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Charter.Orchestration;
using Charter.Refinement;
using Charter.Runners;
using Charter.VersionControl;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Section 6's first notifying state, from an agent stopping to ask to the requester answering.
/// </summary>
/// <remarks>
/// <para>
/// <c>NeedsInput</c> is one of exactly two states that reach a person, and before this wiring it
/// existed only in the enum, the label table and the projection — nothing ever set it. An agent that
/// stopped to ask therefore reached nobody, and the session sat in <c>Running</c> until somebody
/// happened to open the app.
/// </para>
/// <para>
/// The section 6 gate itself is not re-tested here; it belongs to <see cref="NotifyWorthyStates"/> and
/// <see cref="NotificationService"/>, checked once above the channels. What is tested is that this
/// path reaches it with the right state, the right payload, exactly once — and that answering
/// actually unblocks the run rather than filing a comment beside a session that stays stopped.
/// </para>
/// </remarks>
public class RunnerNeedsInputTests
{
    private const string Question = "Should the reminder go to the person who filed the quote, or to their manager?";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQuestionFromTheRunnerMovesBothRowsToNeedsInputAndTellsTheRequesterOnce()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var announced = await world.Announcer().AskAsync(world.SessionId, Question, Token);

        Assert.True(announced.Moved);
        Assert.Equal(NotificationOutcomeKind.Delivered, announced.Notification?.Kind);

        Assert.Equal(RequestStatus.NeedsInput, await world.RequestStatusAsync());
        Assert.Equal(SessionStatus.NeedsInput, await world.SessionStatusAsync());

        var told = Assert.Single(world.Notifications.Sent);

        Assert.Equal(RequestStatus.NeedsInput, told.Status);
        Assert.Equal(world.Scenario.User.Id, told.Recipient.UserId);
        Assert.Equal(world.Scenario.User.Email, told.Recipient.Email);
        Assert.Equal(Question, told.Question);
        Assert.Equal(
            new Uri($"https://charter.example.test/requests/{world.Scenario.Request.Id:D}"),
            told.ThreadUrl);
    }

    [Fact]
    public async Task TheSameQuestionAskedTwiceTellsNobodyASecondTime()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // A runner that loses its connection retries, and a control plane that restarted has no way to
        // know whether it saw the delivery before. Section 6's argument for keeping the notifying set
        // to two is that Charter gets muted the moment it is noisy, so a redelivery must be silent.
        await world.Announcer().AskAsync(world.SessionId, Question, Token);
        var second = await world.Announcer().AskAsync(world.SessionId, Question, Token);

        Assert.False(second.Moved);
        Assert.Null(second.Notification);
        Assert.Single(world.Notifications.Sent);
    }

    [Fact]
    public async Task TheRequesterIsNeverSentACommitARepositoryABranchOrASessionId()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.Announcer().AskAsync(world.SessionId, Question, Token);

        var payload = JsonSerializer.Serialize(Assert.Single(world.Notifications.Sent));

        // Section 7.4, at the boundary that leaves the process. Absent, not redacted: the payload has
        // nowhere to put any of them.
        Assert.DoesNotContain(DeploymentScenario.HeadSha, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeploymentScenario.RepoFullName, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeploymentScenario.HeadBranch, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(world.SessionId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);

        // The engineer-only keys of section 7.1, by name, so a future field cannot smuggle one in.
        foreach (var forbidden in new[] { "costUsd", "headSha", "branch", "runner", "agentModel", "transcript" })
        {
            Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ARequestThatHasMovedPastTheBuildIsNotDraggedBackwards()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // A preview is up and the requester is trying it. A late question from a runner nobody is
        // waiting on must not reopen the thread — and must not notify, either.
        await world.SetRequestStatusAsync(RequestStatus.PreviewReady);

        var announced = await world.Announcer().AskAsync(world.SessionId, Question, Token);

        Assert.False(announced.Moved);
        Assert.Equal(RequestStatus.PreviewReady, await world.RequestStatusAsync());
        Assert.Equal(SessionStatus.Running, await world.SessionStatusAsync());
        Assert.Empty(world.Notifications.Sent);
    }

    [Fact]
    public async Task AnsweringPutsTheSessionBackToRunningAndQueuesItsDispatchAgain()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.Announcer().AskAsync(world.SessionId, Question, Token);

        var resumed = await world.Announcer().AnswerAsync(world.SessionId, "the person who filed it", Token);

        Assert.True(resumed);
        Assert.Equal(SessionStatus.Running, await world.SessionStatusAsync());
        Assert.Equal(RequestStatus.Running, await world.RequestStatusAsync());

        // Section 2.3: the queue row is the record, and it names the same session — section 11's one
        // thread, same branch, rather than a second session on a second branch.
        var job = Assert.Single(await world.BuildJobsAsync());
        Assert.Equal(world.SessionId, BuildJobPayload.TryParse(job.Payload)?.SessionId);

        // The answer itself is an event, because the control plane may be a different container by the
        // time a runner asks what it was.
        var answered = Assert.Single(
            await world.EventsOfAsync(RunnerEventTypes.QuestionAnswered));

        Assert.Contains("the person who filed it", answered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnsweringTwiceDispatchesOnce()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.Announcer().AskAsync(world.SessionId, Question, Token);

        Assert.True(await world.Announcer().AnswerAsync(world.SessionId, "the person who filed it", Token));
        Assert.False(await world.Announcer().AnswerAsync(world.SessionId, "the person who filed it", Token));

        Assert.Single(await world.BuildJobsAsync());
    }

    [Fact]
    public async Task AnsweringASessionNobodyIsWaitingOnDoesNothing()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        Assert.False(await world.Announcer().AnswerAsync(world.SessionId, "anything", Token));
        Assert.Empty(await world.BuildJobsAsync());
    }

    [Fact]
    public async Task TheThreadShowsTheQuestionTheEmailQuoted()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Section 11: somebody who follows the link in the notification must not arrive at a card that
        // says "Question for you" and does not say what it is.
        await world.AppendQuestionEventAsync(Question);
        await world.Announcer().AskAsync(world.SessionId, Question, Token);

        var view = await world.Queries().LoadAsync(world.Requester, world.Scenario.Request.Id, Token);
        Assert.NotNull(view);

        var detail = RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow);
        var outcome = detail.Thread.Milestones[^1];

        Assert.Equal(ApiMilestoneKind.Question, outcome.Kind);
        Assert.Equal("Question for you", outcome.Label);
        Assert.Equal(Question, outcome.Detail);
    }

    [Fact]
    public async Task TheRequesterAnswersInTheSameBoxTheyRefinedIn()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.AppendQuestionEventAsync(Question);
        await world.Announcer().AskAsync(world.SessionId, Question, Token);

        // Section 11: one thread per request, forever. The reply box has to be open in the one
        // mid-build state that exists because Charter is blocked on this person.
        var waiting = await world.DetailAsync();
        Assert.True(waiting.Refinement.CanReply);

        world.Stream.Clear();

        var answered = await world.Commands().SendRefinementMessageAsync(
            world.Requester,
            world.Scenario.Request.Id,
            new SendRefinementMessageBody { Body = "the person who filed it" },
            Token);

        Assert.True(answered.Succeeded);
        Assert.Equal(SessionStatus.Running, await world.SessionStatusAsync());
        Assert.Equal(RequestStatus.Running, await world.RequestStatusAsync());
        Assert.Single(await world.BuildJobsAsync());

        // What they typed is in the thread, live and on the next load, and it is the same message
        // both times — the frame is looked up in the projection's own output, not built beside it.
        var echoed = Assert.Single(world.Stream.RequesterFramesOf<RefinementMessageStreamEvent>());
        Assert.Equal("the person who filed it", echoed.Message.Body);

        var reloaded = await world.DetailAsync();

        Assert.Equal(
            await ApiPayloads.RenderAsync(
                Assert.Single(reloaded.Refinement.Messages, message => message.Id == echoed.Message.Id)),
            await ApiPayloads.RenderAsync(echoed.Message));

        // Section 7.4: the answer path streams the requester nothing engineer-shaped either.
        foreach (var frame in world.Stream.RequesterFrames)
        {
            var json = await ApiPayloads.RenderAsync(frame);

            Assert.DoesNotContain(DeploymentScenario.HeadSha, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(DeploymentScenario.RepoFullName, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.SessionId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AnAnswerToANothingIsRefusedRatherThanFiledAsAComment()
    {
        await using var world = await NeedsInputWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Nothing has asked anything. The request is mid-build, so section 10b's rule applies and the
        // reply is refused — the delegation must not have turned every dispatched request back into a
        // refinable one.
        var outcome = await world.Commands().SendRefinementMessageAsync(
            world.Requester,
            world.Scenario.Request.Id,
            new SendRefinementMessageBody { Body = "hello?" },
            Token);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Empty(await world.BuildJobsAsync());
    }

    [Fact]
    public void TheCallbackThatIngestsEventsIsTheThingThatAnnounces()
    {
        // Not a style point. The whole claim of task 1 is that the *runner callback path* enters
        // NeedsInput; an announcer nothing calls is exactly the state the enum was already in.
        var ingest = typeof(RunnerCallbackEndpoints)
            .GetMethod("IngestEventAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(ingest);
        Assert.Contains(
            typeof(NeedsInputAnnouncer),
            ingest.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Theory]
    [InlineData("""{"question":"why?"}""", "why?")]
    [InlineData("""{"message":"why?"}""", "why?")]
    [InlineData("""{"text":" why? "}""", "why?")]
    [InlineData("""{"question":"  "}""", null)]
    [InlineData("""{"other":"why?"}""", null)]
    [InlineData("not json at all", null)]
    [InlineData("""["why?"]""", null)]
    public void APayloadWithNoReadableQuestionIsNotAQuestion(string payload, string? expected)
    {
        // A payload Charter cannot read a question out of would otherwise put the request into a state
        // that notifies somebody and then show them nothing to answer.
        Assert.Equal(expected, RunnerEventTypes.ReadQuestion(payload));
    }
}

/// <summary>
/// A dispatched session in a throwaway schema, with the notification sink section 6 is counted on.
/// </summary>
/// <remarks>
/// Its own schema rather than the shared database, for the same reason the orchestration suites use
/// one: nothing here sweeps, but the job table is asserted on by count, and a shared queue would make
/// that count depend on whatever else the suite happened to be doing.
/// </remarks>
internal sealed class NeedsInputWorld : IAsyncDisposable
{
    private readonly TestSchema _schema;

    private NeedsInputWorld(TestSchema schema, CharterDbContext db, DeploymentScenario scenario)
    {
        _schema = schema;
        Db = db;
        Scenario = scenario;
    }

    public CharterDbContext Db { get; }

    public DeploymentScenario Scenario { get; }

    public Guid SessionId => Scenario.Session.Id;

    public RecordingNotificationService Notifications { get; } = new();

    public MemberSnapshot Requester => MemberSnapshot.From(Scenario.Member);

    private OrchestrationOptions Options { get; } = new()
    {
        BaseUrl = new Uri("https://charter.example.test/"),
        WorkerId = $"needs-input-{Guid.NewGuid():N}",
    };

    public NeedsInputAnnouncer Announcer()
        => new(
            Db,
            new SessionJournal(Db),
            new JobQueue(Db),
            Options,
            TimeProvider.System,
            NullLogger<NeedsInputAnnouncer>.Instance,
            Notifications);

    public RecordingStreamPublisher Stream { get; } = new();

    public RequestQueryService Queries()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)),
            new VersionControlProviderRegistry([]),
            TimeProvider.System);

    public RequestCommandService Commands()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)),
            Queries(),
            Stream,
            new JobQueue(Db),
            TimeProvider.System,
            Announcer());

    /// <summary>The bytes this request's <c>GET /api/requests/{id}</c> would return to its requester.</summary>
    public async Task<RequestDetailResponse> DetailAsync()
    {
        Db.ChangeTracker.Clear();

        var view = await Queries().LoadAsync(
            Requester,
            Scenario.Request.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(view);

        return RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow);
    }

    public static async Task<NeedsInputWorld?> CreateAsync()
    {
        var schema = await TestSchema.CreateAsync(TestContext.Current.CancellationToken);
        if (schema is null)
        {
            return null;
        }

        var token = TestContext.Current.CancellationToken;
        var db = schema.NewContext();
        var scenario = DeploymentScenario.Build(DateTimeOffset.UtcNow.AddMinutes(-20));

        db.Organizations.Add(scenario.Organization);
        db.Users.Add(scenario.User);
        db.Members.Add(scenario.Member);
        db.Repos.Add(scenario.Repo);
        db.RepoScopes.Add(RepoScope.ForRole(scenario.Repo.Id, MemberRole.Requester, canRequest: true));
        db.Requests.Add(scenario.Request);
        db.Specs.Add(scenario.Spec);
        db.Sessions.Add(scenario.Session);

        // Section 11's one thread, as refinement left it. Without a conversation there is nowhere for
        // the answer to be echoed back to, and the test would pass by having nothing to check.
        var conversation = ConversationRecord.Start(
            scenario.Organization.Id,
            InteractionMode.Plan,
            scenario.Request.Id);

        conversation.AppendRequesterMessage(scenario.Request.RawText);
        conversation.AppendCharterTurn(ConversationTurnKind.SpecProposed, "That is enough to build from.");

        db.Conversations.Add(conversation);

        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        return new NeedsInputWorld(schema, db, scenario);
    }

    /// <summary>What the runner's <c>/events</c> callback would have written before announcing.</summary>
    public async Task AppendQuestionEventAsync(string question)
        => await new SessionJournal(Db).AppendAsync(
            SessionId,
            RunnerEventTypes.Question,
            JsonSerializer.Serialize(new { question }),
            "runner:1",
            cancellationToken: TestContext.Current.CancellationToken);

    public async Task SetRequestStatusAsync(RequestStatus status)
    {
        var request = await Db.Requests.SingleAsync(
            row => row.Id == Scenario.Request.Id,
            TestContext.Current.CancellationToken);

        request.TransitionTo(status);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
    }

    public async Task<RequestStatus> RequestStatusAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.Requests
            .AsNoTracking()
            .Where(row => row.Id == Scenario.Request.Id)
            .Select(row => row.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    public async Task<SessionStatus> SessionStatusAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.Sessions
            .AsNoTracking()
            .Where(row => row.Id == SessionId)
            .Select(row => row.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    public async Task<IReadOnlyList<Job>> BuildJobsAsync()
        => await Db.Jobs
            .AsNoTracking()
            .Where(row => row.Type == JobType.Build)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<string>> EventsOfAsync(string type)
        => await Db.Events
            .AsNoTracking()
            .Where(row => row.SessionId == SessionId && row.Type == type)
            .OrderBy(row => row.Seq)
            .Select(row => row.Payload)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _schema.DisposeAsync();
    }
}

/// <summary>
/// A throwaway Postgres schema, migrated, dropped on dispose.
/// </summary>
/// <remarks>
/// Skips — and the suite stays green — when <c>CHARTER_TEST_DATABASE_URL</c> is unset. There is
/// deliberately no in-memory fallback: the things these suites assert about are transactions, unique
/// constraints and <c>jsonb</c>, none of which a provider substitute reproduces faithfully enough to
/// be worth trusting.
/// </remarks>
internal sealed class TestSchema : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private TestSchema(string connectionString, string schema)
    {
        ConnectionString = connectionString;
        Schema = schema;
    }

    public string ConnectionString { get; }

    public string Schema { get; }

    public static async Task<TestSchema?> CreateAsync(CancellationToken cancellationToken)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run this suite.");
            return null;
        }

        var name = $"charter_test_{Guid.NewGuid():N}"[..40];
        var baseConnectionString = Charter.Configuration.DatabaseUrl.ToNpgsql(url);

        await using (var connection = new Npgsql.NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new Npgsql.NpgsqlCommand($"CREATE SCHEMA \"{name}\";", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var scoped = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = name,
            MaxPoolSize = 4,
        }.ConnectionString;

        var schema = new TestSchema(scoped, name);

        await using var db = schema.NewContext();
        await db.Database.MigrateAsync(cancellationToken);

        return schema;
    }

    /// <summary>A fresh context over this schema. Each is its own unit of work, as in production.</summary>
    public CharterDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, ConnectionString);

        return new CharterDbContext(options.Options);
    }

    /// <summary>A fresh context with <paramref name="interceptors"/> attached.</summary>
    public CharterDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, ConnectionString);
        options.AddInterceptors(interceptors);

        return new CharterDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { SearchPath = null };

            await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new Npgsql.NpgsqlCommand($"DROP SCHEMA \"{Schema}\" CASCADE;", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Npgsql.NpgsqlException)
        {
            // A schema that will not drop is a test-server problem, not a test failure.
        }
    }
}
