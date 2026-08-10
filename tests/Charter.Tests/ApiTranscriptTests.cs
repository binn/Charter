using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Requests;
using Charter.Auth.Audit;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Pane 2 (section 12): the adapter-independent projection, and the windows the pane pages through.
/// </summary>
/// <remarks>
/// The projection half runs without a database. The paging half cannot: <c>aroundSeq</c> exists
/// because a milestone can point at event 12 of 12,480, and a fixture of three events would prove
/// nothing about the query that makes that jump one round trip. Those tests skip without
/// <c>CHARTER_TEST_DATABASE_URL</c>, the same as every other database-backed suite here.
/// </remarks>
public class ApiTranscriptTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    /// <summary>Section 12b: what the client switches on, per Charter event type.</summary>
    public static TheoryData<string, ApiTranscriptEventKind> Kinds => new()
    {
        { EventTypes.FileWrite, ApiTranscriptEventKind.FileWrite },
        { EventTypes.ToolUse, ApiTranscriptEventKind.ToolUse },
        { EventTypes.Command, ApiTranscriptEventKind.Command },
        { EventTypes.Message, ApiTranscriptEventKind.Message },
        { EventTypes.Error, ApiTranscriptEventKind.Diagnostic },
        { EventTypes.CheckResult, ApiTranscriptEventKind.Diagnostic },
        { EventTypes.NetworkCall, ApiTranscriptEventKind.Diagnostic },
        { EventTypes.SessionStarted, ApiTranscriptEventKind.Lifecycle },
        { EventTypes.SessionEnded, ApiTranscriptEventKind.Lifecycle },
        { EventTypes.Cost, ApiTranscriptEventKind.Lifecycle },
        { "session_queued", ApiTranscriptEventKind.Lifecycle },
        { "session_dispatched", ApiTranscriptEventKind.Lifecycle },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryCharterEventTypeProjectsToAKindTheClientKnows(string type, ApiTranscriptEventKind expected)
        => Assert.Equal(expected, TranscriptProjection.KindOf(type));

    [Fact]
    public void AnAdapterTypeThisBuildHasNeverHeardOfStillRenders()
    {
        // Section 12b: `Event.Type` is an open string by design, because an adapter is a
        // configuration PR. A throw here would take pane 2 down for a new adapter rather than
        // showing its rows with a generic icon.
        Assert.Equal(ApiTranscriptEventKind.Message, TranscriptProjection.KindOf("tool_execution_start"));
        Assert.Equal(ApiTranscriptEventKind.Message, TranscriptProjection.KindOf("whatever_pi_calls_this"));
    }

    [Fact]
    public void LevelIsAbsentUntilSomethingActuallyWentWrong()
    {
        // Section 27.7's rule generalised: never colour alone. Stamping `info` on twelve thousand
        // rows makes the column noise and the icon meaningless.
        Assert.Null(TranscriptProjection.LevelOf(EventTypes.Message, "{}"));
        Assert.Null(TranscriptProjection.LevelOf(EventTypes.FileWrite, """{"path":"a.cs"}"""));
        Assert.Equal(ApiTranscriptLevel.Error, TranscriptProjection.LevelOf(EventTypes.Error, "{}"));

        // A check that passed is not a warning; one that failed is an error.
        Assert.Null(TranscriptProjection.LevelOf(EventTypes.CheckResult, """{"passed":true}"""));
        Assert.Equal(
            ApiTranscriptLevel.Error,
            TranscriptProjection.LevelOf(EventTypes.CheckResult, """{"passed":false}"""));
    }

    [Fact]
    public void EveryEventUnderAMilestoneCarriesItsIdRatherThanOnlyTheFirst()
    {
        // Section 12: the linkage teaches by marking *the run* a milestone produced. Only tagging the
        // anchor would highlight one line out of two hundred and teach nothing.
        var at = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();

        var events = new List<Event>
        {
            Event.Append(sessionId, 1, EventTypes.SessionStarted, "{}", at),
            Event.Append(sessionId, 2, EventTypes.ToolUse, """{"name":"read"}""", at),
            Event.Append(sessionId, 3, EventTypes.FileWrite, """{"path":"src/A.cs"}""", at),
            Event.Append(sessionId, 4, EventTypes.Command, """{"command":"dotnet test"}""", at),
        };

        var milestones = new List<Milestone>
        {
            Milestone.Promote(sessionId, events[0].Id, MilestoneLabel.UnderstandingSetup, at),
            Milestone.Promote(sessionId, events[2].Id, MilestoneLabel.MakingChanges, at),
        };

        var page = TranscriptProjection.Page(
            events,
            milestones,
            events.ToDictionary(row => row.Id, row => row.Seq),
            totalCount: 4,
            hasEarlier: false);

        var understanding = milestones[0].Id.ToString();
        var changing = milestones[1].Id.ToString();

        Assert.Equal(
            [understanding, understanding, changing, changing],
            page.Events.Select(row => row.MilestoneId));

        // The tail of the stream is the newest page, so there is nothing older to fetch.
        Assert.Null(page.NextCursor);
        Assert.Equal(4, page.TotalCount);
    }

    [Fact]
    public void AnEventBeforeTheFirstMilestoneBelongsToNoneOfThem()
    {
        var at = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();

        var events = new List<Event>
        {
            Event.Append(sessionId, 1, EventTypes.Message, """{"text":"thinking"}""", at),
            Event.Append(sessionId, 2, EventTypes.FileWrite, """{"path":"src/A.cs"}""", at),
        };

        var milestones = new List<Milestone>
        {
            Milestone.Promote(sessionId, events[1].Id, MilestoneLabel.MakingChanges, at),
        };

        var page = TranscriptProjection.Page(
            events,
            milestones,
            events.ToDictionary(row => row.Id, row => row.Seq),
            totalCount: 2,
            hasEarlier: false);

        Assert.Null(page.Events[0].MilestoneId);
        Assert.Equal(milestones[0].Id.ToString(), page.Events[1].MilestoneId);
    }

    [Fact]
    public void AHunkIndexIsCarriedOnlyWhenTheWriteReportedOne()
    {
        var at = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();

        var events = new List<Event>
        {
            Event.Append(sessionId, 1, EventTypes.FileWrite, """{"path":"src/A.cs"}""", at),
            Event.Append(sessionId, 2, EventTypes.FileWrite, """{"path":"src/B.cs","hunk_index":1}""", at),
        };

        var page = TranscriptProjection.Page(events, [], new Dictionary<Guid, long>(), 2, hasEarlier: false);

        Assert.Null(page.Events[0].HunkIndex);
        Assert.Equal(1, page.Events[1].HunkIndex);
    }

    [Fact]
    public async Task ThePageIsSerialisedWithoutTheKeysItHasNoAnswerFor()
    {
        var at = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var events = new List<Event> { Event.Append(sessionId, 1, EventTypes.Message, """{"text":"hello"}""", at) };

        var body = await ApiPayloads.RenderAsync(
            TranscriptProjection.Page(events, [], new Dictionary<Guid, long>(), 1, hasEarlier: false));

        using var document = JsonDocument.Parse(body);
        var row = document.RootElement.GetProperty("events")[0];

        foreach (var absent in new[] { "path", "hunkIndex", "milestoneId", "level" })
        {
            Assert.False(row.TryGetProperty(absent, out _), $"`{absent}` should be absent, not null.");
        }

        // `nextCursor` is the exception: the client tests it for null to know it has reached the
        // beginning, so it is written even when it is null.
        Assert.True(document.RootElement.TryGetProperty("nextCursor", out var cursor));
        Assert.Equal(JsonValueKind.Null, cursor.ValueKind);
    }

    [Fact]
    public async Task ARequesterIsRefusedThePageRatherThanHandedAnEmptyOne()
    {
        await using var fixture = await TranscriptFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // client.ts: "403 for a viewer without repo read - the same rule that keeps `transcript` out
        // of RequestDetail". An empty page would read as "this session did nothing", which is a
        // different statement and a false one.
        var refused = await fixture.ReadAsync(fixture.Requester, default);

        Assert.Equal(TranscriptReadStatus.Forbidden, refused.Status);
        Assert.Null(refused.Page);
        Assert.DoesNotContain("src/", refused.Reason, StringComparison.Ordinal);

        var allowed = await fixture.ReadAsync(fixture.Engineer, default);
        Assert.Equal(TranscriptReadStatus.Ok, allowed.Status);
        Assert.NotNull(allowed.Page);
    }

    [Fact]
    public async Task TheTailIsWhatThePaneOpensOnAndTheCursorWalksBackwards()
    {
        await using var fixture = await TranscriptFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var tail = await fixture.ReadAsync(fixture.Engineer, new TranscriptWindow(Limit: 25));
        Assert.NotNull(tail.Page);

        // Oldest-first within the page, newest page first.
        Assert.Equal(25, tail.Page.Events.Count);
        Assert.Equal(TranscriptFixture.EventCount, tail.Page.TotalCount);
        Assert.Equal(TranscriptFixture.EventCount, tail.Page.Events[^1].Seq);
        Assert.NotNull(tail.Page.NextCursor);

        var previous = await fixture.ReadAsync(
            fixture.Engineer,
            new TranscriptWindow(Cursor: tail.Page.NextCursor, Limit: 25));

        Assert.NotNull(previous.Page);

        // Contiguous, with no overlap and no gap: the cursor is the lowest sequence already held.
        Assert.Equal(tail.Page.Events[0].Seq - 1, previous.Page.Events[^1].Seq);
    }

    [Fact]
    public async Task AroundSeqCentresTheWindowRatherThanPagingToIt()
    {
        await using var fixture = await TranscriptFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // The case section 12 argues from: a milestone points at event 12 of many, and paging
        // backwards to reach it is not a user experience.
        var window = await fixture.ReadAsync(fixture.Engineer, new TranscriptWindow(AroundSeq: 12, Limit: 20));

        Assert.NotNull(window.Page);
        Assert.Contains(window.Page.Events, row => row.Seq == 12);

        // Centred: roughly half the window sits either side of the anchor.
        Assert.Equal(20, window.Page.Events.Count);
        Assert.True(window.Page.Events[0].Seq < 12, "the window should reach back before the anchor");
        Assert.True(window.Page.Events[^1].Seq > 12, "the window should reach past the anchor");

        // And it is one page of a much longer stream, so the total is the stream's.
        Assert.Equal(TranscriptFixture.EventCount, window.Page.TotalCount);
    }

    [Fact]
    public async Task AWindowAtTheVeryStartHasNothingOlderToOffer()
    {
        await using var fixture = await TranscriptFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var start = await fixture.ReadAsync(fixture.Engineer, new TranscriptWindow(AroundSeq: 1, Limit: 10));

        Assert.NotNull(start.Page);
        Assert.Equal(1, start.Page.Events[0].Seq);
        Assert.Null(start.Page.NextCursor);
    }

    [Fact]
    public async Task ALimitBeyondTheCeilingIsClampedRatherThanHonoured()
    {
        await using var fixture = await TranscriptFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // A window, not a download. The event table is the largest in the database by orders of
        // magnitude (section 5), and one request must not be able to ask for all of it.
        var page = await fixture.ReadAsync(fixture.Engineer, new TranscriptWindow(Limit: 100_000));

        Assert.NotNull(page.Page);
        Assert.True(page.Page.Events.Count <= TranscriptProjection.MaxPageSize);
    }

    /// <summary>A session with enough events that paging is doing real work.</summary>
    private sealed class TranscriptFixture : IAsyncDisposable
    {
        /// <summary>Enough to page through several times without being slow to insert.</summary>
        public const int EventCount = 400;

        private readonly CharterDbContext db;
        private readonly ApiScenario scenario;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;

        private TranscriptFixture(
            CharterDbContext db,
            ApiScenario scenario,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            this.db = db;
            this.scenario = scenario;
            this.transaction = transaction;
        }

        public MemberSnapshot Requester => scenario.Requester;

        public MemberSnapshot Engineer => scenario.Engineer;

        public Task<TranscriptRead> ReadAsync(MemberSnapshot member, TranscriptWindow window)
            => new TranscriptQueryService(db, Queries()).ReadAsync(
                member,
                scenario.Request.Id,
                window,
                TestContext.Current.CancellationToken);

        public static async Task<TranscriptFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the transcript tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var scenario = ApiScenario.Build();

            db.Organizations.Add(scenario.Organization);
            db.Users.Add(scenario.RequesterUser);
            db.Users.Add(scenario.EngineerUser);
            db.Members.Add(scenario.RequesterMember);
            db.Members.Add(scenario.EngineerMember);
            db.Repos.Add(scenario.Repo);
            db.RepoScopes.AddRange(scenario.Scopes);
            db.Requests.Add(scenario.Request);
            db.Specs.Add(scenario.Spec);
            db.Sessions.Add(scenario.Session);

            var at = scenario.Session.CreatedAt;

            for (var seq = 1; seq <= EventCount; seq++)
            {
                db.Events.Add(Event.Append(
                    scenario.Session.Id,
                    seq,
                    seq % 50 == 0 ? EventTypes.FileWrite : EventTypes.ToolUse,
                    seq % 50 == 0 ? """{"path":"src/Quotes/Wizard.cs"}""" : """{"name":"read"}""",
                    at.AddSeconds(seq)));
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new TranscriptFixture(db, scenario, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }

        private RequestQueryService Queries()
            => new(
                db,
                new CharterAuthorizationService(db, new AuditWriter(db, TimeProvider.System)),
                new Charter.VersionControl.VersionControlProviderRegistry([]),
                TimeProvider.System);
    }
}
