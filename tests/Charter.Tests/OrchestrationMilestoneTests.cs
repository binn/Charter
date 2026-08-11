using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Orchestration;
using Charter.VersionControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Section 11's pane 1: a build streams milestones rather than nothing.
/// </summary>
/// <remarks>
/// Against a real Postgres, because the claim is a claim about rows — that a session accumulates four
/// milestones in one order, that a redelivered event adds none, and that the thread never walks
/// backwards. Skips when <c>CHARTER_TEST_DATABASE_URL</c> is unset.
/// </remarks>
public class OrchestrationMilestoneTests
{
    [Fact]
    public void TheFourLabelsAreTheFourSectionElevenPromotes()
    {
        // The vocabulary is fixed and small on purpose: everything else stays in the engineer view.
        Assert.Equal(
            [
                MilestoneLabel.UnderstandingSetup,
                MilestoneLabel.MakingChanges,
                MilestoneLabel.CheckingItWorks,
                MilestoneLabel.PuttingItTogether,
            ],
            Enum.GetValues<MilestoneLabel>());
    }

    [Theory]
    [InlineData(EventTypes.SessionStarted, MilestoneLabel.UnderstandingSetup)]
    [InlineData(EventTypes.ToolUse, MilestoneLabel.UnderstandingSetup)]
    [InlineData(EventTypes.FileWrite, MilestoneLabel.MakingChanges)]
    [InlineData(EventTypes.Command, MilestoneLabel.CheckingItWorks)]
    [InlineData(EventTypes.CheckResult, MilestoneLabel.CheckingItWorks)]
    [InlineData(ChangeRequestEventTypes.BranchPushed, MilestoneLabel.PuttingItTogether)]
    public void AnEventTypeMapsToTheMilestoneARequesterWouldRecognise(string type, MilestoneLabel expected)
        => Assert.Equal(expected, SessionMilestones.LabelFor(type));

    [Theory]
    [InlineData(EventTypes.Message)]
    [InlineData(EventTypes.Cost)]
    [InlineData(EventTypes.NetworkCall)]
    [InlineData(EventTypes.Error)]
    public void TheRestOfTheTranscriptStaysInTheEngineerView(string type)
        => Assert.Null(SessionMilestones.LabelFor(type));

    [Fact]
    public void TheOrchestrationRegistrationIncludesThePromoter()
    {
        var services = new ServiceCollection();
        services.AddCharterOrchestration();

        // A promoter nothing resolves is a silent pane 1, which is the failure this exists to fix.
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(SessionMilestones));
    }

    [Fact]
    public async Task ASessionPromotesTheFourMilestonesInOrderAsItRuns()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // The stream a real session produces, in the order it produces it.
        await world.IngestAsync(EventTypes.SessionStarted, """{"run_url":"https://ci.example/1"}""");
        await world.IngestAsync(EventTypes.ToolUse, """{"paths":[]}""");
        await world.IngestAsync(EventTypes.Message, """{"text":"Looking at the quote wizard"}""");
        await world.IngestAsync(EventTypes.FileWrite, """{"paths":["src/Features/Wizard.cs"]}""");
        await world.IngestAsync(EventTypes.Command, """{"command":"dotnet test"}""");
        await world.IngestAsync(EventTypes.CheckResult, """{"check":"tests","outcome":"passed"}""");
        await world.IngestAsync(
            ChangeRequestEventTypes.BranchPushed,
            """{"branch":"charter/session-1","revision":"deadbee"}""");

        Assert.Equal(
            [
                MilestoneLabel.UnderstandingSetup,
                MilestoneLabel.MakingChanges,
                MilestoneLabel.CheckingItWorks,
                MilestoneLabel.PuttingItTogether,
            ],
            await world.LabelsAsync());
    }

    [Fact]
    public async Task ARequesterSeesTheFirstMilestoneBeforeTheAgentHasWrittenAnything()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // Section 11: a five to twenty minute silent gap reads as broken, and the first minutes of a
        // build are exactly when there is nothing else to say.
        Assert.Equal(MilestoneLabel.UnderstandingSetup, await world.IngestAsync(EventTypes.SessionStarted, "{}"));
        Assert.Single(await world.LabelsAsync());
    }

    [Fact]
    public async Task TheThreadNeverWalksBackwardsAndNeverRepeatsItself()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.IngestAsync(EventTypes.FileWrite, """{"paths":["src/A.cs"]}""");
        await world.IngestAsync(EventTypes.Command, """{"command":"dotnet build"}""");

        // An agent that goes back to reading after it has started checking is normal, and pane 1
        // saying "understanding the current setup" again would read as a build starting over.
        Assert.Null(await world.IngestAsync(EventTypes.ToolUse, "{}"));
        Assert.Null(await world.IngestAsync(EventTypes.FileWrite, """{"paths":["src/B.cs"]}"""));

        Assert.Equal(
            [MilestoneLabel.MakingChanges, MilestoneLabel.CheckingItWorks],
            await world.LabelsAsync());
    }

    [Fact]
    public async Task AMilestoneCarriesWhenItHappenedAndNeverHowLongIsLeft()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.IngestAsync(EventTypes.FileWrite, """{"paths":["src/A.cs"]}""");

        var milestone = Assert.Single(await world.MilestonesAsync());

        // Section 11: never an ETA, elapsed only. A milestone records a moment; nothing about it
        // predicts a finish, and there is nowhere for a projection to find one if it wanted to.
        Assert.NotEqual(default, milestone.CreatedAt);
        Assert.Null(milestone.AnnotationMd);
        Assert.DoesNotContain(
            typeof(Milestone).GetProperties(),
            property => property.Name.Contains("Eta", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Estimate", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("Remaining", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AMilestonePointsAtTheEventThatProducedIt()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.IngestAsync(EventTypes.FileWrite, """{"paths":["src/A.cs"]}""");

        // Section 12: clicking a milestone in pane 1 scrolls pane 2 to the event behind it, which
        // only works if the link is real.
        var milestone = Assert.Single(await world.MilestonesAsync());
        Assert.Equal(await world.EventIdAsync(EventTypes.FileWrite), milestone.EventId);
    }

    [Fact]
    public async Task ASessionWhoseEventsPredateThePromoterIsBackfilled()
    {
        await using var world = await MilestoneWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.AppendOnlyAsync(EventTypes.SessionStarted, "{}");
        await world.AppendOnlyAsync(EventTypes.FileWrite, """{"paths":["src/A.cs"]}""");
        await world.AppendOnlyAsync(
            ChangeRequestEventTypes.BranchPushed,
            """{"branch":"charter/session-1","revision":"deadbee"}""");

        Assert.Empty(await world.LabelsAsync());

        var promoted = await world.Milestones.BackfillAsync(
            world.SessionId,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [MilestoneLabel.UnderstandingSetup, MilestoneLabel.MakingChanges, MilestoneLabel.PuttingItTogether],
            promoted);

        // And running it again is a no-op rather than a second copy of the thread.
        Assert.Empty(await world.Milestones.BackfillAsync(world.SessionId, TestContext.Current.CancellationToken));
    }
}

/// <summary>One session, its transcript, and the promoter that reads it.</summary>
internal sealed class MilestoneWorld : IAsyncDisposable
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    private MilestoneWorld(CharterDbContext db, Guid sessionId)
    {
        Db = db;
        SessionId = sessionId;
        Journal = new SessionJournal(db);
        Milestones = new SessionMilestones(db, TimeProvider.System);
    }

    public CharterDbContext Db { get; }

    public Guid SessionId { get; }

    public SessionJournal Journal { get; }

    public SessionMilestones Milestones { get; }

    public static async Task<MilestoneWorld?> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);

        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the milestone tests.");
            return null;
        }

        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(
            options,
            DatabaseUrl.ToNpgsql(url) + ";Maximum Pool Size=3");

        var db = new CharterDbContext(options.Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var tag = Guid.CreateVersion7().ToString("N");

        var organization = Organization.Create($"milestones-{tag}");
        var user = User.Create($"{tag}@example.test", "Dana Okoro");
        var member = Member.Create(organization.Id, user.Id, Member.AllRoles);
        var repo = Repo.Connect(organization.Id, 7373, $"acme/milestones-{tag}");
        var request = Request.File(organization.Id, repo.Id, user.Id, "Remember the last vertical");
        var spec = Spec.Draft(
            request.Id,
            1,
            "Remember the last selected vertical",
            "The wizard remembers the vertical.",
            "The wizard remembers the vertical.",
            """["It remembers."]""");

        var session = Session.Queue(spec.Id, RunnerKind.GitHubActions, "anthropic/claude-opus-5");
        session.Start("basesha");

        db.Organizations.Add(organization);
        db.Users.Add(user);
        db.Members.Add(member);
        db.Repos.Add(repo);
        db.Requests.Add(request);
        db.Specs.Add(spec);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new MilestoneWorld(db, session.Id);
    }

    /// <summary>Appends an event and promotes it, exactly as the runner callback does.</summary>
    public async Task<MilestoneLabel?> IngestAsync(string type, string payload)
    {
        var appended = await Journal.AppendAsync(
            SessionId,
            type,
            payload,
            $"runner-content:{Guid.NewGuid():N}",
            cancellationToken: TestContext.Current.CancellationToken);

        return await Milestones.PromoteAsync(
            SessionId,
            appended.EventId,
            type,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Appends without promoting — a transcript written before promotion existed.</summary>
    public async Task AppendOnlyAsync(string type, string payload)
        => await Journal.AppendAsync(
            SessionId,
            type,
            payload,
            $"runner-content:{Guid.NewGuid():N}",
            cancellationToken: TestContext.Current.CancellationToken);

    public async Task<IReadOnlyList<MilestoneLabel>> LabelsAsync()
        => [.. (await MilestonesAsync()).Select(row => row.Label)];

    public async Task<IReadOnlyList<Milestone>> MilestonesAsync()
    {
        Db.ChangeTracker.Clear();

        return await Db.Milestones
            .AsNoTracking()
            .Where(row => row.SessionId == SessionId)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    public async Task<Guid> EventIdAsync(string type)
        => await Db.Events
            .AsNoTracking()
            .Where(row => row.SessionId == SessionId && row.Type == type)
            .Select(row => row.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
