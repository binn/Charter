using System.Text;
using System.Text.Json;
using Charter.Api;
using Charter.Api.Projects;
using Charter.Api.Requests;
using Charter.Auth.Authorization;
using Charter.Domain;
using Charter.Recaps;
using Charter.Refinement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// The shared scaffolding the other <c>Api*</c> and <c>Hub*</c> suites use.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.AspNetCore.Mvc.Testing</c> is not part of the ASP.NET Core shared framework and is
/// not referenced by this project, so there is no <c>WebApplicationFactory</c> here. What matters for
/// section 7.4 is not that a socket was involved, it is <em>what bytes the serialiser wrote</em> — so
/// <see cref="ApiPayloads.RenderAsync"/> executes the real <see cref="IResult"/> the endpoint returns
/// against a real <see cref="HttpContext"/> and hands back the body verbatim. That is the same code
/// path a request takes, minus Kestrel.
/// </para>
/// </remarks>
public static class ApiPayloads
{
    /// <summary>Runs an <see cref="IResult"/> and returns the exact bytes it wrote, as text.</summary>
    public static async Task<string> RenderAsync(IResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>The JSON an API response body carries, serialised exactly as an endpoint would.</summary>
    public static Task<string> RenderAsync<T>(T value)
        => RenderAsync(Results.Json(value, CharterApiJson.Options));

    /// <summary>
    /// Every property name anywhere in a JSON document, read from the raw text.
    /// </summary>
    /// <remarks>
    /// Deliberately not a deserialisation into the response type: a type-shaped read would happily
    /// report <c>Details == null</c> for a body that never contained the key, and the whole point of
    /// section 7.4 is the difference between those two.
    /// </remarks>
    public static IReadOnlySet<string> Keys(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, keys);

        return keys;
    }

    private static void Walk(JsonElement element, HashSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);
                    Walk(property.Value, keys);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, keys);
                }

                break;

            default:
                break;
        }
    }
}

/// <summary>
/// A whole request, built from the real domain aggregates, with one requester and one engineer
/// looking at it.
/// </summary>
/// <remarks>
/// The spec deliberately carries a technical approach, a scope and risks, and the session carries a
/// pull request with a commit SHA and a cost — the exact fields section 7.4 and section 27.7 say a
/// requester must never receive. A fixture without them would make the omission tests vacuous.
/// </remarks>
public sealed class ApiScenario
{
    public const string CommitSha = "a3f9c21deadbeef";
    public const string HeadBranch = "charter/remember-last-vertical";
    public const string TechnicalApproach = "Add a per-user preference row and read it on quote creation.";
    public const decimal CostUsd = 1.42m;

    private ApiScenario(
        Organization organization,
        User requesterUser,
        User engineerUser,
        Member requesterMember,
        Member engineerMember,
        Repo repo,
        IReadOnlyList<RepoScope> scopes,
        Request request,
        Spec spec,
        Session session,
        IReadOnlyList<Milestone> milestones,
        IReadOnlyList<Event> events,
        IReadOnlyList<VerificationArtifact> artifacts,
        ChangeRequest changeRequest,
        RequestFeedback? feedback,
        ConversationRecord conversation,
        Charter.Domain.Recap recap)
    {
        Recap = recap;
        Feedback = feedback;
        Conversation = conversation;
        Organization = organization;
        RequesterUser = requesterUser;
        EngineerUser = engineerUser;
        RequesterMember = requesterMember;
        EngineerMember = engineerMember;
        Repo = repo;
        Scopes = scopes;
        Request = request;
        Spec = spec;
        Session = session;
        Milestones = milestones;
        Events = events;
        Artifacts = artifacts;
        ChangeRequest = changeRequest;
    }

    public Organization Organization { get; }

    public User RequesterUser { get; }

    public User EngineerUser { get; }

    public Member RequesterMember { get; }

    public Member EngineerMember { get; }

    public Repo Repo { get; }

    public IReadOnlyList<RepoScope> Scopes { get; }

    public Request Request { get; }

    public Spec Spec { get; }

    public Session Session { get; }

    public IReadOnlyList<Milestone> Milestones { get; }

    public IReadOnlyList<Event> Events { get; }

    public IReadOnlyList<VerificationArtifact> Artifacts { get; }

    public ChangeRequest ChangeRequest { get; }

    /// <summary>
    /// Section 14's recap row, composed through the real <see cref="RecapComposer"/>.
    /// </summary>
    /// <remarks>
    /// Composed rather than hand-written, for the same reason the spec carries a technical approach:
    /// a fixture that wrote its own markdown would let <c>RecapProjection</c> and the composer drift
    /// apart while every test stayed green.
    /// </remarks>
    public Charter.Domain.Recap Recap { get; }

    /// <summary>Section 11: the latest verdict, when somebody has pressed one of the two buttons.</summary>
    public RequestFeedback? Feedback { get; }

    /// <summary>
    /// The persisted refinement conversation (sections 2.3, 10). The thread is rebuilt from these
    /// turns, so a fixture without one would make the refinement projection tests vacuous.
    /// </summary>
    public ConversationRecord Conversation { get; }

    public RepoSnapshot RepoSnapshot => RepoSnapshot.From(Repo, Scopes);

    public MemberSnapshot Requester => MemberSnapshot.From(RequesterMember);

    public MemberSnapshot Engineer => MemberSnapshot.From(EngineerMember);

    /// <summary>Someone from another organisation entirely.</summary>
    public MemberSnapshot Outsider => new()
    {
        MemberId = Guid.CreateVersion7(),
        OrgId = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        Roles = [MemberRole.Engineer, MemberRole.Admin],
    };

    /// <summary>Builds the scenario.</summary>
    /// <param name="now">When it all happened. Everything is offset from this.</param>
    /// <param name="openQuestions">
    /// Section 10: what refinement still needs an answer to. Null for the default scenario, which is
    /// a settled spec — the state every confirmable spec is in.
    /// </param>
    /// <param name="feedback">Section 11: what the requester said when they tried it, if they have.</param>
    /// <param name="artifactPayload">Section 27.7: the kind-specific body, as stored jsonb.</param>
    public static ApiScenario Build(
        DateTimeOffset? now = null,
        IEnumerable<string>? openQuestions = null,
        RequestFeedback? feedback = null,
        string? artifactPayload = null)
    {
        var at = now ?? DateTimeOffset.UtcNow.AddHours(-3);

        // Emails are globally unique in the schema, so a fixture that hard-coded them could only ever
        // be built once against a shared database.
        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization, at);
        var requesterUser = User.Create($"ayesha+{tag}@example.com", "Ayesha Rahman", TeachingLevel.SkipTheBasics, at);
        var engineerUser = User.Create($"tomas+{tag}@example.com", "Tomas Beck", TeachingLevel.JustTheDecisions, at);

        var requesterMember = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester], now: at);
        var engineerMember = Member.Create(
            organization.Id,
            engineerUser.Id,
            [MemberRole.Engineer, MemberRole.Approver],
            now: at);

        var repo = Repo.Connect(organization.Id, 42, "northbeam/quote-tool", "main", at);
        repo.TransitionTo(RepoStatus.Ready, at);
        repo.RecordConfigSnapshot(
            """
            {
              "project": { "name": "Quote tool", "description": "The internal quoting wizard." },
              "limits": { "max_session_usd": 3.4 },
              "glossary": { "vertical": "The kind of installation a quote is for." },
              "templates": [
                {
                  "id": "tpl-copy",
                  "name": "Change some wording",
                  "description": "A label says the wrong thing.",
                  "icon": "text",
                  "prompt": "",
                  "fields": [
                    { "key": "where", "label": "Where do you see it?", "required": true, "multiline": false }
                  ]
                }
              ]
            }
            """,
            at);

        var scopes = new List<RepoScope>
        {
            RepoScope.ForRole(repo.Id, MemberRole.Requester, canRequest: true, at),
            RepoScope.ForRole(repo.Id, MemberRole.Engineer, canRequest: true, at),
        };

        var request = Request.File(
            organization.Id,
            repo.Id,
            requesterUser.Id,
            "every time i start a new quote it makes me pick solar again",
            "tpl-copy",
            at);
        request.TransitionTo(RequestStatus.PreviewReady, at.AddHours(2));

        var document = SpecDocument.Create(
            "Remember the last selected vertical",
            "When you start a new quote, the vertical you chose last time is already selected.",
            [
                "Starting a new quote pre-selects the vertical you chose on your previous quote.",
                "A person who has never created a quote still starts on Solar.",
            ],
            TechnicalApproach,
            SpecScope.Of(["src/Quotes/QuoteWizard.cs"], ["src/Quotes/**"]),
            ["Touching the wizard's session state could affect in-flight quotes."],
            openQuestions);

        var spec = SpecDocumentMapper.ToDraft(document, request.Id, version: 2, at);
        spec.Approve(requesterUser.Id, at.AddMinutes(20));

        var session = Session.Queue(spec.Id, RunnerKind.GitHubActions, "anthropic/claude-opus-5", now: at.AddMinutes(21));
        session.Start(CommitSha, at.AddMinutes(22));
        session.AddCost(CostUsd);
        session.TransitionTo(SessionStatus.PreviewReady, at.AddHours(2));

        var events = new List<Event>
        {
            Event.Append(session.Id, 1, EventTypes.SessionStarted, "{}", at.AddMinutes(22)),
            Event.Append(
                session.Id,
                12,
                EventTypes.FileWrite,
                """{"path":"src/Auth/LoginController.cs"}""",
                at.AddMinutes(30)),
            Event.Append(
                session.Id,
                48,
                EventTypes.Command,
                """{"command":"dotnet test"}""",
                at.AddMinutes(40)),
        };

        var milestones = new List<Milestone>
        {
            Milestone.Promote(session.Id, events[0].Id, MilestoneLabel.UnderstandingSetup, at.AddMinutes(23)),
            Milestone.Promote(session.Id, events[1].Id, MilestoneLabel.MakingChanges, at.AddMinutes(31)),
            Milestone.Promote(session.Id, events[2].Id, MilestoneLabel.CheckingItWorks, at.AddMinutes(41)),
        };

        var artifact = VerificationArtifact.Pending(
            session.Id,
            VerificationArtifactKind.HostedPreview,
            VerificationArtifactAudience.Requester,
            at.AddHours(2));
        artifact.MarkReady(
            url: "https://pr-142.preview.northbeam.charter.app/quotes/new",
            instructionsMd: "This is a full copy of the quote tool with your change in it.",
            expiresAt: at.AddHours(8),
            payload: artifactPayload);

        var changeRequest = ChangeRequest.Open(
            session.Id,
            142,
            "https://github.com/northbeam/quote-tool/pull/142",
            CommitSha,
            ChangeRequestState.Open,
            at.AddHours(1),
            headBranch: HeadBranch);

        // Section 2.3: the thread survives a restart because it is rows. The opening turn repeats the
        // request's own text, exactly as a refine job that seeded the conversation would leave it, so
        // the projection's de-duplication is exercised rather than assumed.
        var conversation = ConversationRecord.Start(organization.Id, InteractionMode.Chat, request.Id, at);
        conversation.AppendRequesterMessage(request.RawText, at);
        conversation.PromoteTo(InteractionMode.Plan, at.AddMinutes(1));
        conversation.AppendCharterTurn(
            ConversationTurnKind.ClarifyingQuestion,
            "Should that apply to quotes you started before today?",
            at.AddMinutes(2));
        conversation.AppendRequesterMessage("no, only new ones", at.AddMinutes(3));
        conversation.AppendCharterTurn(
            ConversationTurnKind.SpecProposed,
            "That is enough to build from.",
            at.AddMinutes(4));

        return new ApiScenario(
            organization,
            requesterUser,
            engineerUser,
            requesterMember,
            engineerMember,
            repo,
            scopes,
            request,
            spec,
            session,
            milestones,
            events,
            artifacts: [artifact],
            changeRequest,
            feedback,
            conversation,
            ComposeRecap(document, session.Id, at.AddHours(2)));
    }

    /// <summary>
    /// The section 14 recap, built through the real ranker and composer.
    /// </summary>
    /// <remarks>
    /// The two files it touches are deliberately the ones section 14 says must float and sink: a
    /// login controller and a test. A recap whose ranking happened to be alphabetical would then pass
    /// nothing, which is the property <c>ApiRecapTests</c> checks.
    /// </remarks>
    public static Charter.Domain.Recap ComposeRecap(SpecDocument document, Guid sessionId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ranked = RecapFileRiskRanker.Rank(
        [
            new RecapFileChange("tests/Quotes/QuoteWizardTests.cs") { LinesAdded = 40 },
            new RecapFileChange("src/Auth/LoginController.cs") { LinesAdded = 9, LinesRemoved = 2 },
        ]);

        var payload = new RecapPayload
        {
            WhatAndWhy = "The wizard read the vertical off the quote row, so this adds a per-user preference.",
            Deviations =
            [
                new RecapDeviationPayload
                {
                    What = "Keyed the preference on the auth user id",
                    SpecSaid = "The remembered choice is yours alone",
                    Why = "the wizard only has the auth id in scope",
                    Where = "src/Auth/LoginController.cs",
                },
                new RecapDeviationPayload
                {
                    What = "Cached the resolved user id on the accessor",
                },
            ],
            FileNotes = [new RecapFileNotePayload { Path = "src/Auth/LoginController.cs", Note = "Resolves the signed-in user." }],
            CouldNotVerify = ["No test covers two tabs picking different verticals."],
        };

        var (body, riskItems, recapDocument, _) = RecapComposer.Compose(
            new RecapEvidence { SessionId = sessionId, Spec = document, AutoDispatched = false },
            ranked,
            payload,
            new RecapOptions());

        return Charter.Domain.Recap.Generate(
            sessionId,
            body,
            riskItems,
            CostUsd,
            at,
            payloadJson: recapDocument.ToJson());
    }

    /// <summary>What section 7.4 says this member may be shown, via the real policy.</summary>
    public SessionVisibility VisibilityFor(MemberSnapshot member)
        => RequestVisibility.Resolve(member, RepoSnapshot, Request, Session);

    /// <summary>The loaded rows, filtered the way the query service filters them.</summary>
    public RequestAggregate AggregateFor(MemberSnapshot member)
    {
        var visibility = VisibilityFor(member);

        return new RequestAggregate
        {
            Request = Request,
            Repo = Repo,
            Profile = RepoProjectProfile.For(Repo),
            Spec = Spec,
            Session = Session,
            Milestones = Milestones,
            Events = visibility.Transcript ? Events : [],
            Artifacts = Artifacts,
            ChangeRequest = RequestVisibility.CanSeeEngineerDetails(visibility) ? ChangeRequest : null,

            // Change spec 001 part A.2: the provider supplies the word. This scenario is a GitHub
            // repository, so the engineer detail reads "pull request".
            ChangeRequestTerm = "pull request",
            ChangeRequestTermShort = "PR",
            RefinementMessages = RefinementThread.Derive(Request, Spec, Conversation),
            Feedback = RequestPresentation.Feedback(Feedback),
            ApprovedByName = RequesterUser.DisplayName,
            AwaitingApprovalFrom = "Tomas Beck",

            // The query service loads these on exactly these terms (section 7.4), so the fixture
            // filters them the same way rather than handing the projection rows it would never see.
            TotalEvents = visibility.Transcript ? Events.Count : 0,
            ChangedPaths = visibility.Code
                ?
                [
                    .. Events
                        .Select(row => RequestPresentation.TranscriptPath(row.Type, row.Payload))
                        .Where(path => path is not null)
                        .Select(path => path!),
                ]
                : [],
            Recap = visibility.Transcript ? Recap : null,
            HandedOffByName = Session.Status == SessionStatus.HandedOff ? EngineerUser.DisplayName : null,
        };
    }

    /// <summary>The bytes this member's <c>GET /api/requests/{id}</c> would return.</summary>
    public Task<string> RenderDetailAsync(MemberSnapshot member)
        => ApiPayloads.RenderAsync(
            RequestProjection.Detail(AggregateFor(member), VisibilityFor(member), DateTimeOffset.UtcNow));
}

/// <summary>The scaffolding itself, checked, so a failure elsewhere is never the fixture lying.</summary>
public class ApiFixtureTests
{
    [Fact]
    public async Task TheHarnessReturnsTheBytesTheResultWrote()
    {
        var body = await ApiPayloads.RenderAsync(Results.Json(new { hello = "world" }));

        Assert.Equal("""{"hello":"world"}""", body);
    }

    [Fact]
    public void KeyWalkingFindsNestedNames()
    {
        var keys = ApiPayloads.Keys("""{"a":{"b":[{"c":1}]},"d":null}""");

        Assert.Contains("a", keys);
        Assert.Contains("b", keys);
        Assert.Contains("c", keys);
        Assert.Contains("d", keys);
        Assert.DoesNotContain("e", keys);
    }

    [Fact]
    public void TheFixtureCarriesTheFieldsARequesterMustNotSee()
    {
        var scenario = ApiScenario.Build();

        Assert.Equal(ApiScenario.TechnicalApproach, scenario.Spec.TechnicalApproach);
        Assert.Equal(ApiScenario.CommitSha, scenario.ChangeRequest.HeadSha);
        Assert.Equal(ApiScenario.CostUsd, scenario.Session.CostUsd);
        Assert.NotEmpty(scenario.Events);
    }

    [Fact]
    public void SectionSevenPointFourGivesTheRequesterNoPanesAndTheEngineerBoth()
    {
        var scenario = ApiScenario.Build();

        var requester = scenario.VisibilityFor(scenario.Requester);
        var engineer = scenario.VisibilityFor(scenario.Engineer);
        var outsider = scenario.VisibilityFor(scenario.Outsider);

        Assert.True(requester.StatusThread);
        Assert.False(requester.Transcript);
        Assert.False(requester.Code);
        Assert.False(requester.Cost);

        Assert.True(engineer.Transcript);
        Assert.True(engineer.Code);
        Assert.True(engineer.Cost);

        // A member of another organisation gets nothing at all, and the endpoint turns that into a 404.
        Assert.True(outsider.IsEmpty);
    }
}
