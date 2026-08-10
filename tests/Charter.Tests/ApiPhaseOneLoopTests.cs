using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Deployments;
using Charter.GitHub;
using Charter.Domain;
using Charter.Hubs;
using Charter.Models;
using Charter.Orchestration;
using Charter.Refinement;
using Charter.Runners;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Charter.Tests;

/// <summary>
/// Section 23's definition of Phase 1, as one test.
/// </summary>
/// <remarks>
/// <para>
/// <em>"Phase 1 is a vertical slice, not a layer. It is done when this runs end to end, for one web
/// repository, for one person, once: request → refinement → spec → approval → session → PR → preview
/// → what to check → Works / Not quite."</em> Every other suite in this repository proves one link.
/// This one walks the chain, through the real services, in order, against a real Postgres.
/// </para>
/// <para>
/// Three things are stubbed and nothing else: the model client, so refinement spends no tokens; the
/// version control provider, so no pull request is opened anywhere; and the deployment provider, so
/// no environment is created. Everything between them — intake, the spend gate, dispatch, the change
/// request row, the artifact, the two buttons — is the code that ships.
/// </para>
/// <para>
/// The world runs in its own Postgres schema. The change request publisher and the preview lifecycle
/// both sweep <em>every</em> row they can see, so sharing a database with the rest of the suite would
/// have them reconciling other tests' sessions.
/// </para>
/// </remarks>
public class ApiPhaseOneLoopTests
{
    [Fact]
    public async Task TheWholeLoopRunsFromATypedRequestToWorks()
    {
        await using var world = await PhaseOneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var token = TestContext.Current.CancellationToken;

        // ── request ──────────────────────────────────────────────────────────────────────────────
        // Section 10: intake lands in Refining and queues a refine job. Nothing that could write to a
        // repository is queued, because nothing has been through refinement yet.
        var (filed, requestId) = await world.Commands().CreateAsync(
            world.Requester,
            new CreateRequestBody
            {
                ProjectId = world.RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            token);

        Assert.True(filed.Succeeded);
        Assert.Equal(RequestStatus.Refining, await world.RequestStatusAsync(requestId));
        Assert.Equal(JobType.Refine, await world.LatestJobTypeAsync());

        // ── refinement ───────────────────────────────────────────────────────────────────────────
        // Two turns, both against a stubbed model. The first is a refusal to guess: section 10 says
        // nothing ambiguous dispatches, and the requester answering is what unblocks it.
        var (spec, questions) = await world.RefineAsync(requestId, token);

        Assert.NotEmpty(questions);
        Assert.Equal(2, spec.AcceptanceCriteria.Count);

        // ── spec ─────────────────────────────────────────────────────────────────────────────────
        Assert.Equal(RequestStatus.SpecReady, await world.RequestStatusAsync(requestId));

        var awaiting = await world.Queries().ListPendingApprovalsAsync(world.Approver, token);
        Assert.Contains(awaiting, entry => entry.RequestId == requestId.ToString());

        // The requester's rendering of the spec carries no technical approach — section 10b's line
        // between the two views, checked at the moment they are asked to approve it.
        var beforeApproval = await world.RequesterBodyAsync(requestId);
        Assert.DoesNotContain("technicalApproach", ApiPayloads.Keys(beforeApproval));

        // ── approval ─────────────────────────────────────────────────────────────────────────────
        // Section 7.5: the spend gate, and nothing else. It says the work is worth tokens, never that
        // the code may ship — there is no merge button anywhere in this test for that reason.
        var approved = await world.Commands().ApproveSpecAsync(world.Approver, requestId, version: 1, token);

        Assert.True(approved.Succeeded);
        Assert.Equal(RequestStatus.Queued, await world.RequestStatusAsync(requestId));
        Assert.Equal(JobType.Build, await world.LatestJobTypeAsync());

        // ── session ──────────────────────────────────────────────────────────────────────────────
        var sessionId = await world.StartSessionAsync(spec, token);
        var dispatch = await world.Coordinator().DispatchAsync(new BuildJobPayload { SessionId = sessionId }, token);

        Assert.Equal(DispatchDecision.Dispatched, dispatch.Decision);
        Assert.Single(world.Runner.Dispatches);

        // What a runner streams back while it works. Section 2.3: these rows, not anything held in
        // the process, are what the rest of the loop reads.
        await world.RunnerReportsAsync(sessionId, token);

        // ── change request ───────────────────────────────────────────────────────────────────────
        var publication = await world.Publisher().PublishAsync(sessionId, token);

        Assert.Equal(ChangeRequestPublication.Opened, publication.Outcome);

        var changeRequest = await world.ChangeRequestAsync(sessionId);
        Assert.Equal(PhaseOneWorld.HeadSha, changeRequest.HeadSha);
        Assert.Equal(ChangeRequestState.Open, changeRequest.State);

        // Section 18: recorded at open, because a preview platform that refuses a branch from outside
        // its workspace can only report that as absence, and the warning has to name somebody.
        Assert.Equal(PhaseOneWorld.AuthorLogin, changeRequest.AuthorLogin);
        Assert.Equal(SessionStatus.PrOpen, await world.SessionStatusAsync(sessionId));

        // ── deployment → artifact ────────────────────────────────────────────────────────────────
        var context = await world.Ingestor().FindAsync(PhaseOneWorld.RepoFullName, changeRequest.Number, token);
        Assert.NotNull(context);

        var observation = await world.Ingestor().PollAsync(context, token);
        Assert.Equal(DeploymentAvailability.Reported, observation.Availability);

        var artifact = await world.ArtifactAsync(sessionId);
        Assert.Equal(VerificationArtifactState.Ready, artifact.State);
        Assert.Equal(PhaseOneWorld.PreviewUrl, artifact.Url);
        Assert.NotNull(artifact.ExpiresAt);

        // ── what to check ────────────────────────────────────────────────────────────────────────
        // Section 27.7: the list beside the button is the approved acceptance criteria, verbatim. Not
        // regenerated, because it is the contract the requester agreed to.
        var body = await world.RequesterBodyAsync(requestId);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var criteria = root.GetProperty("spec").GetProperty("acceptanceCriteria")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("text").GetString() ?? string.Empty)
            .ToList();

        Assert.Equal(spec.AcceptanceCriteria, criteria);

        var card = Assert.Single(root.GetProperty("artifacts").EnumerateArray().ToList());
        Assert.Equal("ready", card.GetProperty("state").GetString());
        Assert.Equal(PhaseOneWorld.PreviewUrl, card.GetProperty("payload").GetProperty("url").GetString());

        // Section 7.4: a requester gets a preview, never a repository. Absent keys, not null ones.
        var keys = ApiPayloads.Keys(body);
        Assert.DoesNotContain("details", keys);
        Assert.DoesNotContain("transcript", keys);
        Assert.DoesNotContain("changes", keys);
        Assert.DoesNotContain(PhaseOneWorld.HeadSha, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PhaseOneWorld.RepoFullName, body, StringComparison.Ordinal);

        // ── Works ────────────────────────────────────────────────────────────────────────────────
        // Section 11: two buttons, and the verdict is a row before it is a job.
        var verdict = await world.Commands().SubmitFeedbackAsync(
            world.Requester,
            requestId,
            new SubmitFeedbackBody { Verdict = ApiFeedbackVerdict.Works },
            token);

        Assert.True(verdict.Succeeded);

        var feedback = await world.Db.RequestFeedback
            .AsNoTracking()
            .SingleAsync(row => row.RequestId == requestId, token);

        Assert.Equal(FeedbackVerdict.Works, feedback.Verdict);
        Assert.Equal(sessionId, feedback.SessionId);

        // "Works" does not start another build. It asks for the engineer recap (section 14).
        Assert.Equal(JobType.Recap, await world.LatestJobTypeAsync());

        var afterFeedback = await world.RequesterBodyAsync(requestId);
        Assert.Contains("\"works\"", afterFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEngineerSeesTheHalfOfTheLoopTheRequesterNeverDoes()
    {
        await using var world = await PhaseOneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        var token = TestContext.Current.CancellationToken;

        var (_, requestId) = await world.Commands().CreateAsync(
            world.Requester,
            new CreateRequestBody
            {
                ProjectId = world.RepoId.ToString(),
                RawText = "every time i start a new quote it makes me pick solar again",
            },
            token);

        var (spec, _) = await world.RefineAsync(requestId, token);
        await world.Commands().ApproveSpecAsync(world.Approver, requestId, version: 1, token);

        var sessionId = await world.StartSessionAsync(spec, token);
        await world.Coordinator().DispatchAsync(new BuildJobPayload { SessionId = sessionId }, token);
        await world.RunnerReportsAsync(sessionId, token);
        await world.Publisher().PublishAsync(sessionId, token);

        var changeRequest = await world.ChangeRequestAsync(sessionId);
        var context = await world.Ingestor().FindAsync(PhaseOneWorld.RepoFullName, changeRequest.Number, token);
        await world.Ingestor().PollAsync(context!, token);

        var view = await world.Queries().LoadAsync(world.Approver, requestId, token);
        Assert.NotNull(view);

        var body = await ApiPayloads.RenderAsync(
            RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow));

        var keys = ApiPayloads.Keys(body);

        // Section 27.7's `Details` disclosure, and panes 2 and 3: a permission, not a preference.
        Assert.Contains("transcript", keys);
        Assert.Contains("changes", keys);
        Assert.Contains("details", keys);
        Assert.Contains(PhaseOneWorld.HeadSha, body, StringComparison.Ordinal);
    }
}

/// <summary>
/// Section 6's other terminal outcome: the agent ran, ran correctly, and changed nothing.
/// </summary>
/// <remarks>
/// This state exists because the alternative was worse. Before it, a session that found nothing to
/// do ended as <c>Failed</c>, whose requester copy is <em>this turned out to be bigger than
/// expected</em> — the exact opposite of what happened, told to the one person least equipped to
/// work out that it was wrong.
/// </remarks>
public class ApiNoChangesOutcomeTests
{
    private static RequestAggregate Aggregate(RequestStatus status)
    {
        var scenario = ApiScenario.Build();
        scenario.Request.TransitionTo(status);
        scenario.Session.TransitionTo(SessionStatus.NoChangesNeeded);

        return scenario.AggregateFor(scenario.Requester);
    }

    [Fact]
    public void TheLabelSaysNothingNeededChangingRatherThanAnythingAboutSize()
    {
        var label = RequestPresentation.StatusLabel(RequestStatus.NoChangesNeeded);

        Assert.Equal("Nothing needed changing", label);
        Assert.DoesNotContain("bigger", label, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(RequestPresentation.StatusLabel(RequestStatus.Failed), label);
    }

    [Fact]
    public void TheCopyNamesTheCommonCauseAndDoesNotReadAsAFailure()
    {
        var copy = RequestPresentation.NoChangesSummary;

        // Section 10b treats "it already does that" as the cheapest possible outcome. This is that
        // same answer, found one step later, so the sentence has to say so.
        Assert.Contains("already works", copy, StringComparison.Ordinal);
        Assert.Contains("Nothing went wrong", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("engineer has been told", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("bigger than expected", copy, StringComparison.Ordinal);
    }

    [Fact]
    public void ItNotifiesNobody()
    {
        // Section 6: only NeedsInput and PreviewReady notify. A closed thread with nothing to do is
        // not worth a push.
        Assert.False(RequestPresentation.NeedsAttention(RequestStatus.NoChangesNeeded));
    }

    [Fact]
    public void TheThreadEndsDoneWithTheExplanationAndNoFailureLine()
    {
        var aggregate = Aggregate(RequestStatus.NoChangesNeeded);

        var detail = RequestProjection.Detail(
            aggregate,
            SessionVisibility.None with { StatusThread = true },
            DateTimeOffset.UtcNow);

        // Section 11's failure line is for failures. This is not one.
        Assert.Null(detail.Thread.FailureSummary);

        var outcome = detail.Thread.Milestones[^1];

        Assert.Equal(ApiMilestoneState.Done, outcome.State);
        Assert.Equal("Nothing needed changing", outcome.Label);
        Assert.Equal(RequestPresentation.NoChangesSummary, outcome.Detail);
    }

    [Fact]
    public void AFailedRequestStillGetsTheFailureLine()
    {
        // The control: the two outcomes must not have converged into one.
        var detail = RequestProjection.Detail(
            Aggregate(RequestStatus.Failed),
            SessionVisibility.None with { StatusThread = true },
            DateTimeOffset.UtcNow);

        Assert.Equal(RequestPresentation.FailureSummary, detail.Thread.FailureSummary);
        Assert.Equal(ApiMilestoneState.Failed, detail.Thread.Milestones[^1].State);
    }

    [Fact]
    public void TheWireVocabularyCarriesIt()
        => Assert.Equal(ApiRequestStatus.NoChangesNeeded, RequestStatus.NoChangesNeeded.ToApi());
}

/// <summary>
/// One organisation, one repository, one requester, and every service the loop touches.
/// </summary>
/// <remarks>
/// Built by hand rather than from the container, because the loop crosses six subsystems and the
/// point of the test is the order they run in. Each service is the real one; only the three seams
/// that leave the process are stubbed.
/// </remarks>
internal sealed class PhaseOneWorld : IAsyncDisposable
{
    public const string RepoFullName = "northbeam/quote-tool";
    public const string BaseSha = "b0a5e0000000000000000000000000000000ba5e";
    public const string HeadSha = "a3f9c21deadbeef0000000000000000000000cafe";
    public const string AuthorLogin = "charter-app[bot]";
    public const string PreviewUrl = "https://quote-tool-pr-142.up.railway.app";

    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private readonly string _schema;
    private readonly string _connectionString;

    private PhaseOneWorld(
        CharterDbContext db,
        string schema,
        string connectionString,
        Organization organization,
        Member requester,
        Member approver,
        Repo repo)
    {
        Db = db;
        _schema = schema;
        _connectionString = connectionString;
        Organization = organization;
        RequesterMember = requester;
        ApproverMember = approver;
        Repo = repo;

        VersionControl = new FakeVersionControlProvider { AuthorLogin = AuthorLogin };
        VersionControlRegistry = new VersionControlProviderRegistry([VersionControl]);
        Deployments = new StubDeploymentProvider();
        Runner = new RecordingRunner();
    }

    public CharterDbContext Db { get; }

    public Organization Organization { get; }

    public Member RequesterMember { get; }

    public Member ApproverMember { get; }

    public Repo Repo { get; }

    public FakeVersionControlProvider VersionControl { get; }

    public VersionControlProviderRegistry VersionControlRegistry { get; }

    public StubDeploymentProvider Deployments { get; }

    public RecordingRunner Runner { get; }

    public Guid RepoId => Repo.Id;

    public MemberSnapshot Requester => MemberSnapshot.From(RequesterMember);

    public MemberSnapshot Approver => MemberSnapshot.From(ApproverMember);

    private static DeploymentOptions DeploymentSettings => new()
    {
        Provider = DeploymentProviderKind.None,
        PreviewTtl = TimeSpan.FromHours(8),
        ProbeReachability = false,
    };

    public static async Task<PhaseOneWorld?> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the Phase 1 loop test.");
            return null;
        }

        var token = TestContext.Current.CancellationToken;
        var schema = $"charter_loop_{Guid.NewGuid():N}"[..40];
        var baseConnectionString = DatabaseUrl.ToNpgsql(url);

        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\";", connection);
            await command.ExecuteNonQueryAsync(token);
        }

        var scoped = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            MaxPoolSize = 4,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(options, scoped);

        var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(token);

        var tag = Guid.CreateVersion7().ToString("N");
        var now = DateTimeOffset.UtcNow.AddMinutes(-30);

        var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization, now);
        var requesterUser = User.Create($"ayesha+{tag}@example.test", "Ayesha Rahman", TeachingLevel.SkipTheBasics, now);
        var approverUser = User.Create($"tomas+{tag}@example.test", "Tomas Beck", TeachingLevel.JustTheDecisions, now);

        var requester = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester], now: now);
        var approver = Member.Create(
            organization.Id,
            approverUser.Id,
            [MemberRole.Approver, MemberRole.Engineer],
            now: now);

        var repo = Repo.Connect(organization.Id, 42, RepoFullName, "main", now);
        repo.TransitionTo(RepoStatus.Ready, now);
        repo.RecordConfigSnapshot(
            """
            {
              "project": { "name": "Quote tool", "description": "The internal quoting wizard." },
              "limits": { "max_session_usd": 3.4 },
              "glossary": { "vertical": "The kind of installation a quote is for." }
            }
            """,
            now);

        db.Organizations.Add(organization);
        db.Users.Add(requesterUser);
        db.Users.Add(approverUser);
        db.Members.Add(requester);
        db.Members.Add(approver);
        db.Repos.Add(repo);
        db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Requester, canRequest: true, now));
        db.RepoScopes.Add(RepoScope.ForRole(repo.Id, MemberRole.Engineer, canRequest: true, now));

        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        return new PhaseOneWorld(db, schema, scoped, organization, requester, approver, repo);
    }

    public RequestQueryService Queries()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)),
            VersionControlRegistry,
            TimeProvider.System);

    public RequestCommandService Commands()
        => new(
            Db,
            new CharterAuthorizationService(Db, new AuditWriter(Db, TimeProvider.System)),
            Queries(),
            new SilentStreamPublisher(),
            new JobQueue(Db),
            TimeProvider.System);

    public SessionCoordinator Coordinator()
    {
        var options = new OrchestrationOptions
        {
            DispatcherLockKey = Random.Shared.NextInt64(),
            WorkerId = $"loop-{Guid.NewGuid():N}",
            BaseUrl = new Uri("https://charter.example.test/"),
        };

        return new SessionCoordinator(
            Db,
            new SessionJournal(Db),
            new RunnerRegistry([Runner]),
            new SessionDispatchPlanner(Db, options),
            new JobQueue(Db),
            options,
            NullLogger<SessionCoordinator>.Instance);
    }

    public ChangeRequestPublisher Publisher()
        => new(Db, VersionControlRegistry, TimeProvider.System, NullLogger<ChangeRequestPublisher>.Instance);

    public DeploymentIngestor Ingestor()
    {
        var settings = DeploymentSettings;

        var publisher = new PreviewArtifactPublisher(
            Db,
            settings,
            new PreviewReachabilityProbe(
                new StubHttpClientFactory(new StubHttpMessageHandler()),
                settings,
                NullLogger<PreviewReachabilityProbe>.Instance),
            TimeProvider.System,
            NullLogger<PreviewArtifactPublisher>.Instance);

        return new DeploymentIngestor(
            Db,
            new DeploymentBinder(Db, TimeProvider.System, NullLogger<DeploymentBinder>.Instance),
            publisher,
            new DeploymentProviderRegistry([Deployments]),
            NullLogger<DeploymentIngestor>.Instance);
    }

    /// <summary>
    /// The refinement conversation, both turns, and the spec row it leaves behind.
    /// </summary>
    /// <remarks>
    /// The refiner and the conversation are the real ones over a stubbed model client. Persisting the
    /// result is done here because nothing in the control plane yet handles a <c>Refine</c> job — the
    /// queue row that intake writes has no consumer, so the step between it and a <c>Spec</c> row is
    /// the one link in section 23's chain that has no owner.
    /// </remarks>
    public async Task<(SpecDocument Spec, IReadOnlyList<string> Questions)> RefineAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var client = new RefinementStubClient()
            .Enqueue(JsonSerializer.Serialize(new
            {
                resolution = "clarify",
                message = "Before I write this down — one question.",
                clarifying_questions = new[] { "Should that apply to quotes you started before today?" },
            }))
            .Enqueue(JsonSerializer.Serialize(new
            {
                resolution = "spec",
                message = "Here's what I've understood.",
                spec = new
                {
                    title = "Remember the last selected vertical",
                    outcome = "When you start a new quote, the vertical you chose last time is already selected.",
                    acceptance_criteria = new[]
                    {
                        "Starting a new quote pre-selects the vertical you chose on your previous quote.",
                        "A person who has never created a quote still starts on Solar.",
                    },
                    technical_approach = "Add a per-user preference row and read it on quote creation.",
                    scope = new
                    {
                        files = new[] { "src/Features/Quotes/QuoteWizard.cs" },
                        paths = new[] { "src/Features/Quotes/**" },
                    },
                    risks = new[] { "Touching the wizard's session state could affect in-flight quotes." },
                    open_questions = Array.Empty<string>(),
                },
            }));

        var refiner = RefinementStubs.Refiner(client);
        var context = RefinementStubs.Context();
        var conversation = RefinementConversation.Start(InteractionMode.Plan);
        var credential = new ModelCredential
        {
            Id = "grant-loop",
            Kind = ModelCredentialKind.AnthropicApiKey,
            Secret = new ModelSecret("sk-test-not-a-real-key"),
        };

        var request = await Db.Requests.SingleAsync(row => row.Id == requestId, cancellationToken);

        var first = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From(request.RawText),
            context,
            credential,
            cancellationToken);

        var clarification = Assert.IsType<RefinementOutcome.NeedsClarification>(first.Outcome);

        var second = await refiner.AdvanceAsync(
            conversation,
            RequesterText.From("no, only new ones"),
            context,
            credential,
            cancellationToken);

        var proposed = Assert.IsType<RefinementOutcome.SpecProposed>(second.Outcome);
        var now = DateTimeOffset.UtcNow;

        var record = ConversationRecord.Start(Organization.Id, InteractionMode.Plan, requestId, now);
        record.AppendRequesterMessage(request.RawText, now);
        record.AppendCharterTurn(
            ConversationTurnKind.ClarifyingQuestion,
            clarification.Questions[0],
            now.AddSeconds(1));
        record.AppendRequesterMessage("no, only new ones", now.AddSeconds(2));
        record.AppendCharterTurn(ConversationTurnKind.SpecProposed, proposed.RequesterMessage, now.AddSeconds(3));

        var row = SpecDocumentMapper.ToDraft(proposed.Spec, requestId, version: 1, now.AddSeconds(3));

        Db.Conversations.Add(record);
        Db.Specs.Add(row);
        request.TransitionTo(RequestStatus.SpecReady, now.AddSeconds(3));

        await Db.SaveChangesAsync(cancellationToken);
        Db.ChangeTracker.Clear();

        return (proposed.Spec, clarification.Questions);
    }

    /// <summary>
    /// Creates the session the approved spec is built by, and starts it at the base commit.
    /// </summary>
    /// <remarks>
    /// Also unowned: <c>ApproveSpecAsync</c> writes a <c>Build</c> job keyed on the request and the
    /// spec, and <c>BuildJobHandler</c> reads one keyed on a session, so nothing turns an approved
    /// spec into a <see cref="Session"/>. The row is created here so the rest of the chain can be
    /// exercised as it will run once that step exists.
    /// </remarks>
    public async Task<Guid> StartSessionAsync(SpecDocument spec, CancellationToken cancellationToken)
    {
        var row = await Db.Specs.AsNoTracking().SingleAsync(
            candidate => candidate.Title == spec.Title,
            cancellationToken);

        var session = Session.Queue(row.Id, RunnerKind.Agent, "anthropic/claude-opus-5");
        session.Start(BaseSha);

        Db.Sessions.Add(session);
        await Db.SaveChangesAsync(cancellationToken);
        Db.ChangeTracker.Clear();

        return session.Id;
    }

    /// <summary>What the runner streams back, and the branch it says it pushed.</summary>
    public async Task RunnerReportsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var journal = new SessionJournal(Db);
        var branch = ChangeRequestPublisher.BranchFor(sessionId);

        await journal.AppendAsync(sessionId, EventTypes.SessionStarted, "{}", "runner:1", cancellationToken: cancellationToken);
        await journal.AppendAsync(
            sessionId,
            EventTypes.FileWrite,
            """{"path":"src/Features/Quotes/QuoteWizard.cs"}""",
            "runner:2",
            cancellationToken: cancellationToken);
        await journal.AppendAsync(
            sessionId,
            ChangeRequestEventTypes.BranchPushed,
            $$"""{"branch":"{{branch}}","revision":"{{HeadSha}}"}""",
            "runner:3",
            cancellationToken: cancellationToken);
        await journal.AppendAsync(
            sessionId,
            EventTypes.SessionEnded,
            """{"state":"completed"}""",
            "runner:4",
            cancellationToken: cancellationToken);

        // What the provider will report about that branch when the publisher asks.
        VersionControl.BranchHeads[branch] = HeadSha;
        VersionControl.Comparisons[(BaseSha, HeadSha)] =
            new RevisionComparison(3, 0, ["src/Features/Quotes/QuoteWizard.cs"]);

        Db.ChangeTracker.Clear();
    }

    public async Task<RequestStatus> RequestStatusAsync(Guid requestId)
        => await Db.Requests
            .AsNoTracking()
            .Where(row => row.Id == requestId)
            .Select(row => row.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

    public async Task<SessionStatus> SessionStatusAsync(Guid sessionId)
        => await Db.Sessions
            .AsNoTracking()
            .Where(row => row.Id == sessionId)
            .Select(row => row.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

    public async Task<JobType> LatestJobTypeAsync()
        => await Db.Jobs
            .AsNoTracking()
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => row.Type)
            .FirstAsync(TestContext.Current.CancellationToken);

    public async Task<ChangeRequest> ChangeRequestAsync(Guid sessionId)
        => await Db.ChangeRequests
            .AsNoTracking()
            .SingleAsync(row => row.SessionId == sessionId, TestContext.Current.CancellationToken);

    public async Task<VerificationArtifact> ArtifactAsync(Guid sessionId)
        => await Db.VerificationArtifacts
            .AsNoTracking()
            .SingleAsync(row => row.SessionId == sessionId, TestContext.Current.CancellationToken);

    /// <summary>The exact bytes the requester's <c>GET /api/requests/{id}</c> would write.</summary>
    public async Task<string> RequesterBodyAsync(Guid requestId)
    {
        Db.ChangeTracker.Clear();

        var view = await Queries().LoadAsync(Requester, requestId, TestContext.Current.CancellationToken);
        Assert.NotNull(view);

        return await ApiPayloads.RenderAsync(
            RequestProjection.Detail(view.Aggregate, view.Visibility, DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();

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

    /// <summary>SignalR is a courtesy on top of the rows, never the record (section 2.3).</summary>
    private sealed class SilentStreamPublisher : IRequestStreamPublisher
    {
        public Task PublishAsync(
            Guid requestId,
            RequestStreamEvent frame,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishAsync(
            Guid requestId,
            RequestStreamEvent requesterFrame,
            RequestStreamEvent engineerFrame,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>A preview platform that answers when polled, and does nothing else.</summary>
/// <remarks>
/// Deliberately not Railway-shaped. The loop must run against the seam rather than against one
/// platform's GraphQL, which is the whole reason section 18 has a seam.
/// </remarks>
internal sealed class StubDeploymentProvider : IDeploymentProvider
{
    public string Id => "stub";

    public DeploymentProviderCapabilities Capabilities { get; set; } = new(
        Poll: true,
        CommentParsing: false,
        Teardown: false,
        NativeExpiry: false);

    public TimeSpan? PreviewLifetime => null;

    /// <summary>What the next poll answers.</summary>
    public DeploymentObservation Next { get; set; } = DeploymentObservation.Reported(
        new DeploymentReport("stub", DeploymentState.Ready, PhaseOneWorld.PreviewUrl));

    public Task<DeploymentObservation> ObserveAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default) => Task.FromResult(Next);

    public DeploymentObservation ReadComment(DeploymentComment comment, DeploymentTarget target)
        => DeploymentObservation.NotYet();

    public Task<DeploymentTeardownResult> TeardownAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DeploymentTeardownResult.NothingToDo("this stub reclaims nothing"));
}
