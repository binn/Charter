using System.Text.Json;
using Charter.Api;
using Charter.Api.Contracts;
using Charter.Api.Setup;
using Charter.Auth.Authorization;
using Charter.Auth.Setup;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// The admin setup checklist (section 30.2).
/// </summary>
/// <remarks>
/// Every row is answered from state that already exists rather than from a counter, so the tests
/// below change the world and re-read the checklist rather than calling a setter. That is also the
/// property section 30.2 is really asking for: somebody who goes off to install a GitHub App and
/// comes back tomorrow finds the row ticked.
/// </remarks>
public class ApiSetupTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task ARequesterGetsNullRatherThanAForbiddenTheDashboardWouldHaveToRender()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // client.ts: "Resolves to `null` for a viewer who is not an admin - absence, not a 403 the
        // dashboard would have to treat as an error."
        var checklist = await fixture.Service().DescribeAsync(
            fixture.Requester,
            TestContext.Current.CancellationToken);

        Assert.Null(checklist);

        // And it goes on the wire as a JSON null body, which is what the client's
        // `SetupChecklist | null` resolves against — not an empty object, not an empty task list, and
        // specifically not an empty body, which `response.json()` throws on.
        Assert.Equal("null", await ApiPayloads.RenderAsync(CharterApiJson.NullOr<SetupChecklistResponse>(null)));
    }

    [Fact]
    public async Task AnAdminGetsSectionThirtyTwosSevenRowsInSectionThirtyTwosOrder()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var checklist = await fixture.Service().DescribeAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        Assert.NotNull(checklist);

        // Not sorted by done-ness: reordering a checklist under somebody as they tick things off is
        // disorienting, and the sequence is itself the advice.
        Assert.Equal(
            [
                ApiSetupTaskId.NameOrganisation,
                ApiSetupTaskId.ConnectGithub,
                ApiSetupTaskId.AddModelCredential,
                ApiSetupTaskId.ConnectRepository,
                ApiSetupTaskId.SetBudgets,
                ApiSetupTaskId.InvitePeople,
                ApiSetupTaskId.NotificationChannels,
            ],
            checklist.Tasks.Select(task => task.Id));
    }

    [Fact]
    public async Task ARowTicksItselfWhenTheThingItAsksForExists()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var before = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);
        Assert.False(Row(before!, ApiSetupTaskId.ConnectRepository).Done);

        await fixture.ConnectRepositoryAsync();

        var after = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);
        var repository = Row(after!, ApiSetupTaskId.ConnectRepository);

        Assert.True(repository.Done);

        // Proof, not a tick (section 30.2).
        Assert.Contains("quote-tool", repository.DoneSummary!, StringComparison.Ordinal);

        // Connecting a repository also proves the App is installed, so that row follows.
        Assert.True(Row(after!, ApiSetupTaskId.ConnectGithub).Done);
    }

    [Fact]
    public async Task TheSeededPlaceholderNameDoesNotCountAsHavingNamedIt()
    {
        await using var fixture = await SetupFixture.CreateAsync(
            PersonalOrganizationSeeder.DefaultOrganizationName);

        if (fixture is null)
        {
            return;
        }

        var checklist = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);

        Assert.False(Row(checklist!, ApiSetupTaskId.NameOrganisation).Done);
    }

    [Fact]
    public async Task BlockedByExplainsRatherThanLocks()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var checklist = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);
        var repository = Row(checklist!, ApiSetupTaskId.ConnectRepository);

        Assert.Equal(ApiSetupTaskId.ConnectGithub, repository.BlockedBy);

        // The row is still a link to somewhere. Section 30.2's whole point is that somebody can
        // leave and come back in any order they like.
        Assert.NotEmpty(repository.Href);
    }

    [Fact]
    public async Task DismissingAPartlyDoneChecklistIsRefusedWithATrueSentence()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (outcome, dismissed) = await fixture.Service().DismissAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        // client.ts: "allowed only once every task is done". A checklist dismissed at three of seven
        // is a checklist nobody sees again, and four things the instance still needs.
        Assert.False(outcome.Succeeded);
        Assert.Null(dismissed);
        Assert.Contains("still", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADismissalIsAServerSideFactThatSurvivesTheNextRead()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.CompleteEverythingAsync();

        var (outcome, dismissed) = await fixture.Service().DismissAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(dismissed?.DismissedAt);

        // There is no browser storage in this app, so the next read has to carry it too.
        var reread = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);
        Assert.Equal(dismissed.DismissedAt, reread!.DismissedAt);

        // Dismissing twice keeps the first timestamp rather than moving it.
        var (again, second) = await fixture.Service().DismissAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        Assert.True(again.Succeeded);
        Assert.Equal(dismissed.DismissedAt, second!.DismissedAt);
    }

    [Fact]
    public async Task ARequesterCannotDismissTheChecklistTheyCannotSee()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (outcome, _) = await fixture.Service().DismissAsync(
            fixture.Requester,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task AnUntickedRowCarriesNoDoneSummaryKeyAtAll()
    {
        await using var fixture = await SetupFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var checklist = await fixture.Service().DescribeAsync(fixture.Admin, TestContext.Current.CancellationToken);
        var body = await ApiPayloads.RenderAsync(checklist);

        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.GetProperty("tasks").EnumerateArray().ToList();

        var undone = rows.First(row => !row.GetProperty("done").GetBoolean());
        Assert.False(undone.TryGetProperty("doneSummary", out _));

        // And a checklist nobody has dismissed carries no timestamp rather than a null one.
        Assert.False(document.RootElement.TryGetProperty("dismissedAt", out _));
    }

    private static SetupTaskResponse Row(SetupChecklistResponse checklist, ApiSetupTaskId id)
        => checklist.Tasks.Single(task => task.Id == id);

    private sealed class SetupFixture : IAsyncDisposable
    {
        private readonly CharterDbContext db;
        private readonly CharterConfig config;
        private readonly Organization organization;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;

        private SetupFixture(
            CharterDbContext db,
            CharterConfig config,
            Organization organization,
            MemberSnapshot admin,
            MemberSnapshot requester,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            this.db = db;
            this.config = config;
            this.organization = organization;
            this.transaction = transaction;

            Admin = admin;
            Requester = requester;
        }

        public MemberSnapshot Admin { get; }

        public MemberSnapshot Requester { get; }

        public SetupChecklistService Service() => new(db, config, CharterTime.System);

        public async Task ConnectRepositoryAsync()
        {
            var repo = Repo.Connect(organization.Id, 42, "northbeam/quote-tool", "main");
            repo.TransitionTo(RepoStatus.Ready);

            db.Repos.Add(repo);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();
        }

        /// <summary>Ticks every row the database can tick, so dismissal becomes reachable.</summary>
        public async Task CompleteEverythingAsync()
        {
            await ConnectRepositoryAsync();

            db.CredentialGrants.Add(CredentialGrant.Create(
                organization.Id,
                Admin.UserId,
                CredentialKind.AnthropicApiKey,
                secretEncrypted: [1, 2, 3, 4]));

            db.Budgets.Add(Budget.Create(
                organization.Id,
                "Monthly",
                BudgetScopeType.Org,
                LedgerUnit.Usd,
                BudgetPeriod.Monthly,
                50m));

            var second = User.Create(
                $"second+{Guid.CreateVersion7():N}@example.com",
                "Second Person",
                TeachingLevel.SkipTheBasics);

            db.Users.Add(second);
            db.Members.Add(Member.Create(organization.Id, second.Id, [MemberRole.Requester]));

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();
        }

        public static async Task<SetupFixture?> CreateAsync(string organizationName = "Northbeam Solar")
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the setup checklist tests.");
                return null;
            }

            // Section 22 has one notification channel in v1, and its row is ticked by the instance
            // being able to send mail at all. The fixture therefore configures SMTP, so that
            // "everything is done" is a state the dismissal test can actually reach.
            var config = ConfigTestEnvironment.Valid(
                ("CHARTER_EMAIL_PROVIDER", "smtp"),
                ("CHARTER_SMTP_URL", "smtp://mailer:p%40ss@smtp.example.com:2525"),
                ("CHARTER_EMAIL_FROM", "charter@example.com"));

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create(organizationName, OrganizationMode.Organization);
            var adminUser = User.Create($"admin+{tag}@example.com", "Ada Admin", TeachingLevel.JustTheDecisions);
            var requesterUser = User.Create($"ayesha+{tag}@example.com", "Ayesha Rahman", TeachingLevel.SkipTheBasics);

            var adminMember = Member.Create(organization.Id, adminUser.Id, [MemberRole.Admin]);
            var requesterMember = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester]);

            db.Organizations.Add(organization);
            db.Users.AddRange(adminUser, requesterUser);
            db.Members.AddRange(adminMember, requesterMember);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new SetupFixture(
                db,
                config,
                organization,
                MemberSnapshot.From(adminMember),
                MemberSnapshot.From(requesterMember),
                transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await db.DisposeAsync();
        }
    }
}
