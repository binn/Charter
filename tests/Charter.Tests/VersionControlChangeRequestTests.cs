using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The Phase 1 loop step: <em>session → change request</em>.
/// </summary>
/// <remarks>
/// Against a real Postgres, because the claims being tested are claims about rows — that a change
/// request is recorded once however many times the sweep runs, that a session with nothing to show
/// for it ends honestly rather than with an empty change request, and that the labels of sections
/// 7.5 and 15 are derived from what the database says rather than from what a caller passes in.
/// Skips when <c>CHARTER_TEST_DATABASE_URL</c> is unset, following <c>OnboardingServiceTests</c>.
/// </remarks>
public class VersionControlChangeRequestTests
{
    [Fact]
    public async Task ACompletedSessionOpensAChangeRequestAgainstTheBaseBranch()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] = new RevisionComparison(0, 3, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, result.Outcome);

        var row = Assert.Single(await world.ChangeRequestsAsync());
        Assert.Equal(142, row.Number);
        Assert.Equal("headsha", row.HeadSha);
        Assert.Equal(world.Branch, row.HeadBranch);
        Assert.Equal(ChangeRequestState.Open, row.State);
        Assert.Contains("/changes/142", row.Url, StringComparison.Ordinal);

        var command = Assert.Single(world.Provider.Opened);
        Assert.Equal("main", command.Target);
        Assert.Equal(world.Branch, command.Source);

        // Section 6: PROpen comes after Running, and this is the step that gets it there.
        Assert.Equal(SessionStatus.PrOpen, await world.StatusAsync());
    }

    [Fact]
    public async Task PublishingTwiceRecordsOneChangeRequest()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        await world.PublishAsync();
        var second = await world.PublishAsync();

        // Section 2.3: the sweep runs on an interval and after every restart, so the second pass has
        // to be a no-op rather than a second pull request.
        Assert.Equal(ChangeRequestPublication.AlreadyOpen, second.Outcome);
        Assert.Single(await world.ChangeRequestsAsync());
        Assert.Single(world.Provider.Opened);
    }

    [Fact]
    public async Task AnAgentThatChangedNothingIsARealOutcomeAndNotAnError()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // No session branch at all: the agent ran and wrote nothing.
        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.NoChanges, result.Outcome);
        Assert.Empty(await world.ChangeRequestsAsync());
        Assert.Empty(world.Provider.Opened);

        // The explanation is a fact on the transcript, not a stack trace: the requester is told the
        // agent found nothing to do, which is a different sentence from "this failed".
        var events = await world.EventsAsync(ChangeRequestEventTypes.NoChanges);
        var payload = Assert.Single(events);
        Assert.Contains("without changing anything", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABranchThatNeverLeftTheBaseCommitIsAlsoNoChanges()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        world.Provider.BranchHeads[world.Branch] = "basesha";

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.NoChanges, result.Outcome);
        Assert.Empty(world.Provider.Opened);
    }

    [Fact]
    public async Task AnAutoDispatchedSessionIsLabelledUnreviewedSpec()
    {
        await using var world = await ChangeRequestWorld.CreateAsync(autoDispatched: true);
        if (world is null)
        {
            return;
        }

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        // Section 7.5: nobody vetted the spec, and the engineer must know that before reading a line.
        Assert.Contains(ChangeRequestLabels.UnreviewedSpec, result.Labels);

        var command = Assert.Single(world.Provider.Opened);
        Assert.Contains(ChangeRequestLabels.UnreviewedSpec, command.Labels!);

        // Also in the body, because a provider that lost the label would otherwise lose the
        // disclosure with it.
        Assert.Contains("No human approved this specification", command.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionThatTouchedAMigrationIsLabelledSchemaChange()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Section 15: the shim classifies structurally and reports the classification as a check
        // result. The label is derived from that rather than from anything the agent asserted.
        await world.RecordMigrationClassificationAsync();

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] =
            new RevisionComparison(0, 1, ["src/Migrations/20260101_Add.cs"]);

        var result = await world.PublishAsync();

        Assert.Contains(ChangeRequestLabels.SchemaChange, result.Labels);
        Assert.DoesNotContain(ChangeRequestLabels.UnreviewedSpec, result.Labels);
    }

    [Fact]
    public async Task AProviderWithoutLabelsStillMakesTheDisclosure()
    {
        await using var world = await ChangeRequestWorld.CreateAsync(autoDispatched: true);
        if (world is null)
        {
            return;
        }

        world.Provider.DeclaredCapabilities = world.Provider.DeclaredCapabilities with
        {
            ChangeRequestLabels = false,
        };

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, result.Outcome);
        Assert.Empty(result.Labels);

        // Degraded explicitly rather than silently: the fact moves into the body.
        var command = Assert.Single(world.Provider.Opened);
        Assert.Contains("No human approved this specification", command.BodyMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderWithNoChangeRequestSurfaceDegradesRatherThanFailing()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Part A.6: a bare git remote. Charter produces the review artifact itself, and says so.
        world.Provider.DeclaredCapabilities = world.Provider.DeclaredCapabilities with
        {
            ChangeRequests = false,
        };

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.NotSupported, result.Outcome);
        Assert.Empty(await world.ChangeRequestsAsync());
    }

    [Fact]
    public async Task AProviderThatRefusesLeavesTheSessionForTheNextSweep()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        world.Provider.BranchHeads[world.Branch] = "headsha";
        world.Provider.Comparisons[("basesha", "headsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);
        world.Provider.DeclaredCapabilities = world.Provider.DeclaredCapabilities with { ChangeRequests = true };
        world.Provider.NextNumber = -1;

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Failed, result.Outcome);

        // Not failed, not settled: the provider was unreachable, which is not the session's fault.
        Assert.Equal(SessionStatus.Running, await world.StatusAsync());
    }

    [Fact]
    public async Task TheBranchTheRunnerReportedWinsOverTheConvention()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.RecordBranchPushAsync("feature/hand-rolled", "reportedsha");
        world.Provider.BranchHeads["feature/hand-rolled"] = "reportedsha";
        world.Provider.Comparisons[("basesha", "reportedsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, result.Outcome);
        Assert.Equal("feature/hand-rolled", Assert.Single(world.Provider.Opened).Source);
    }

    [Fact]
    public async Task ARevisionTheRunnerReportedIsPublishedWhenTheBranchIsNotThereYet()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.RecordBranchPushAsync(world.Branch, "reportedsha");
        world.Provider.Comparisons[("basesha", "reportedsha")] = new RevisionComparison(0, 1, ["src/Widget.cs"]);

        var result = await world.PublishAsync();

        Assert.Equal(ChangeRequestPublication.Opened, result.Outcome);
        Assert.Contains(world.Branch, world.Provider.BranchesCreated);
    }

    [Fact]
    public void TheSessionBranchIsAConventionAnyRunnerCanCompute()
    {
        var id = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        Assert.Equal($"charter/session-{id:N}", ChangeRequestPublisher.BranchFor(id));
    }
}

/// <summary>A session that has completed, its repository, and a provider that answers in memory.</summary>
internal sealed class ChangeRequestWorld : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private ChangeRequestWorld(CharterDbContext db, Session session, Repo repo)
    {
        Db = db;
        Session = session;
        Repo = repo;
        Provider = new FakeVersionControlProvider();
        Registry = new VersionControlProviderRegistry([Provider]);

        Publisher = new ChangeRequestPublisher(
            db,
            Registry,
            TimeProvider.System,
            NullLogger<ChangeRequestPublisher>.Instance);

        Tracker = new ChangeRequestStateTracker(
            db,
            Registry,
            TimeProvider.System,
            NullLogger<ChangeRequestStateTracker>.Instance);
    }

    public CharterDbContext Db { get; }

    public Session Session { get; }

    public Repo Repo { get; }

    public FakeVersionControlProvider Provider { get; }

    public VersionControlProviderRegistry Registry { get; }

    public ChangeRequestPublisher Publisher { get; }

    public ChangeRequestStateTracker Tracker { get; }

    public string Branch => ChangeRequestPublisher.BranchFor(Session.Id);

    public static async Task<ChangeRequestWorld?> CreateAsync(bool autoDispatched = false)
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);

        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the version control tests.");
            return null;
        }

        var options = new DbContextOptionsBuilder<CharterDbContext>();

        // A small pool on purpose. Every world here opens its own context against the one throwaway
        // Postgres the whole suite shares, and xUnit runs test classes in parallel — the default pool
        // size turns a green suite into "sorry, too many clients already" on a laptop.
        DataServiceCollectionExtensions.ConfigureNpgsql(
            options,
            DatabaseUrl.ToNpgsql(url) + ";Maximum Pool Size=3");

        var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create($"version-control-{tag}");
        var user = User.Create($"{tag}@example.test", "Test Engineer");
        var member = Member.Create(organization.Id, user.Id, Member.AllRoles);
        // The whole tag, not a prefix: a version 7 GUID is time-ordered, so two worlds created in
        // the same few seconds share their leading hex digits — and two repositories with the same
        // full name are one repository as far as a webhook is concerned.
        var repo = Repo.Connect(organization.Id, 4242, $"acme/version-control-{tag}");
        var request = Request.File(organization.Id, repo.Id, user.Id, "Make the total exclude tax");
        var spec = Spec.Draft(
            request.Id,
            1,
            "Exclude tax from the total",
            "The total on the invoice excludes tax.",
            "The total should exclude tax.",
            """["The total excludes tax."]""");

        var session = Session.Queue(
            spec.Id,
            RunnerKind.Agent,
            "anthropic/claude-opus-5",
            autoDispatched: autoDispatched);

        session.Start("basesha");

        db.Organizations.Add(organization);
        db.Users.Add(user);
        db.Members.Add(member);
        db.Repos.Add(repo);
        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);
        db.Events.Add(Event.Append(session.Id, 1, EventTypes.SessionEnded, """{"state":"completed"}"""));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new ChangeRequestWorld(db, session, repo);
    }

    public Task<ChangeRequestPublicationResult> PublishAsync()
        => Publisher.PublishAsync(Session.Id, TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<ChangeRequest>> ChangeRequestsAsync()
        => await Db.ChangeRequests
            .AsNoTracking()
            .Where(row => row.SessionId == Session.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task<SessionStatus> StatusAsync()
        => await Db.Sessions
            .AsNoTracking()
            .Where(row => row.Id == Session.Id)
            .Select(row => row.Status)
            .FirstAsync(TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<string>> EventsAsync(string type)
        => await Db.Events
            .AsNoTracking()
            .Where(row => row.SessionId == Session.Id && row.Type == type)
            .Select(row => row.Payload)
            .ToListAsync(TestContext.Current.CancellationToken);

    public async Task RecordBranchPushAsync(string branch, string revision)
    {
        await AppendAsync(
            ChangeRequestEventTypes.BranchPushed,
            $$"""{"branch":"{{branch}}","revision":"{{revision}}"}""");
    }

    public async Task RecordMigrationClassificationAsync()
    {
        await AppendAsync(
            EventTypes.CheckResult,
            """
            {"check":"migration_classification","class":"additive","label":"schema-change"}
            """);
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();

    private async Task AppendAsync(string type, string payload)
    {
        var seq = await Db.Events
            .Where(row => row.SessionId == Session.Id)
            .MaxAsync(row => (long?)row.Seq, TestContext.Current.CancellationToken) ?? 0L;

        Db.Events.Add(Event.Append(Session.Id, seq + 1, type, payload));
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
    }
}
