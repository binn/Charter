using System.Text.Json;
using Charter.Api.Contracts;
using Charter.Api.Settings;
using Charter.Auth.Authorization;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Charter.Tests;

/// <summary>
/// Admin settings → Email (change spec 001 part C.3).
/// </summary>
/// <remarks>
/// The behaviour worth testing here is a refusal to fail loudly. A mail server that is down, wrong,
/// or not configured at all must produce a sentence an operator can act on — not a 500, and not a
/// stack trace, which section 11 rules out for every surface. The test doubles below stand in for
/// the three ways that goes wrong.
/// </remarks>
public class ApiEmailSettingsTests
{
    private const string DatabaseUrlVariable = "CHARTER_TEST_DATABASE_URL";

    [Fact]
    public async Task ARequesterIsRefusedTheSettingsEntirely()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (outcome, settings) = await fixture.Service().DescribeAsync(
            fixture.Requester,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(settings);
    }

    [Fact]
    public async Task DisabledEmailArrivesWithTheVariablesToChangeRatherThanABareFalse()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (outcome, settings) = await fixture.Service().DescribeAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(settings);
        Assert.False(settings.Enabled);

        // "Email is off" is not actionable on its own. Naming the variables is.
        Assert.NotNull(settings.DisabledReason);
        Assert.Contains("CHARTER_EMAIL_PROVIDER", settings.HowToEnable!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecentLogIsCarriedAndTheLatestFailureLeads()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        fixture.Log.Record(new EmailDeliveryRecord
        {
            At = DateTimeOffset.UtcNow.AddMinutes(-5),
            Recipient = "ayesha@example.com",
            Kind = "invitation",
            Status = EmailDeliveryStatus.Failed,
            Summary = "The mail server refused the message.",
            Detail = "550 5.7.1 Relay access denied",
        });

        var (_, settings) = await fixture.Service().DescribeAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        var recorded = Assert.Single(settings!.Recent);

        Assert.Equal(ApiEmailDeliveryStatus.Failed, recorded.Status);
        Assert.Equal("550 5.7.1 Relay access denied", recorded.Detail);

        // Change spec 001 C.3: the failure is what the page leads with.
        Assert.NotNull(settings.LastFailure);
        Assert.Equal(recorded.At, settings.LastFailure.At);

        // And the page is told the log will be empty after a restart, so an empty list is not read
        // as "nothing has ever been sent".
        Assert.True(settings.RecentIsInMemory);
    }

    [Fact]
    public async Task AMisconfiguredServerAnswersSentFalseInTheServersOwnWords()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        fixture.Tester.Result = new EmailTestResult
        {
            Sent = false,
            Recipient = "ada@example.com",
            Message = "The mail server refused the message.",
            Detail = "550 5.7.1 Relay access denied",
        };

        var (outcome, result) = await fixture.Service().SendTestAsync(
            fixture.Admin,
            new SendTestEmailBody { Recipient = "ada@example.com" },
            TestContext.Current.CancellationToken);

        // The endpoint succeeded; the send did not. Conflating the two turns "your SMTP host said
        // no" into "Charter is broken".
        Assert.True(outcome.Succeeded);
        Assert.NotNull(result);
        Assert.False(result.Sent);
        Assert.Equal("550 5.7.1 Relay access denied", result.Detail);
    }

    [Fact]
    public async Task ATransportThatThrowsStillProducesASentenceRatherThanAFiveHundred()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // The sender contract says delivery problems are returned rather than thrown. A transport
        // that breaks that contract must not turn this button into an error page.
        fixture.Tester.Throw = new InvalidOperationException("Connection refused (smtp.example.com:2525)");

        var (outcome, result) = await fixture.Service().SendTestAsync(
            fixture.Admin,
            new SendTestEmailBody { Recipient = "ada@example.com" },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.False(result!.Sent);
        Assert.Contains("CHARTER_SMTP_URL", result.Message, StringComparison.Ordinal);
        Assert.Contains("Connection refused", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestGoesToTheAdminsOwnAddressWhenNoneIsGiven()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Service().SendTestAsync(
            fixture.Admin,
            new SendTestEmailBody(),
            TestContext.Current.CancellationToken);

        // An admin checking whether mail works wants it in their own inbox; asking them to retype an
        // address Charter already holds is friction for nothing.
        Assert.Equal(fixture.AdminEmail, fixture.Tester.LastRecipient);
    }

    [Fact]
    public async Task ARequesterCannotSendATestEmailToAnybody()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (outcome, result) = await fixture.Service().SendTestAsync(
            fixture.Requester,
            new SendTestEmailBody { Recipient = "somebody@example.com" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Null(result);
        Assert.Null(fixture.Tester.LastRecipient);
    }

    [Fact]
    public async Task TheSettingsBodyOmitsWhatDoesNotApply()
    {
        await using var fixture = await EmailFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (_, settings) = await fixture.Service().DescribeAsync(
            fixture.Admin,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(await ApiPayloads.RenderAsync(settings));

        // Nothing has failed yet, so there is no key rather than a null one.
        Assert.False(document.RootElement.TryGetProperty("lastFailure", out _));

        // And with email off there is no address to send as.
        Assert.False(document.RootElement.TryGetProperty("fromAddress", out _));
        Assert.Equal("none", document.RootElement.GetProperty("provider").GetString());
    }

    /// <summary>An availability answer without a mail server behind it.</summary>
    private sealed class StubSender : IEmailSender
    {
        public EmailAvailability Availability { get; init; } =
            EmailAvailability.From(ConfigTestEnvironment.Valid().Email);

        public Task<EmailDeliveryResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The settings tests never send through the real path.");
    }

    /// <summary>A tester that answers however the case needs, including badly.</summary>
    private sealed class StubTester : IEmailTester
    {
        public string? LastRecipient { get; private set; }

        public EmailTestResult? Result { get; set; }

        public Exception? Throw { get; set; }

        public Task<EmailTestResult> SendTestAsync(string recipient, CancellationToken cancellationToken = default)
        {
            LastRecipient = recipient;

            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Result ?? new EmailTestResult
            {
                Sent = true,
                Recipient = recipient,
                Message = $"Test email sent to {recipient}.",
            });
        }
    }

    private sealed class EmailFixture : IAsyncDisposable
    {
        private readonly CharterDbContext db;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction;

        private EmailFixture(
            CharterDbContext db,
            MemberSnapshot admin,
            MemberSnapshot requester,
            string adminEmail,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            this.db = db;
            this.transaction = transaction;

            Admin = admin;
            Requester = requester;
            AdminEmail = adminEmail;
        }

        public MemberSnapshot Admin { get; }

        public MemberSnapshot Requester { get; }

        public string AdminEmail { get; }

        public StubTester Tester { get; } = new();

        public IEmailDeliveryLog Log { get; } = new RecentEmailDeliveryLog();

        public EmailSettingsService Service() => new(db, new StubSender(), Tester, Log);

        public static async Task<EmailFixture?> CreateAsync()
        {
            var url = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
            if (string.IsNullOrWhiteSpace(url))
            {
                Assert.Skip($"Set {DatabaseUrlVariable} to a throwaway Postgres to run the email settings tests.");
                return null;
            }

            var options = new DbContextOptionsBuilder<CharterDbContext>();
            DataServiceCollectionExtensions.ConfigureNpgsql(options, DatabaseUrl.ToNpgsql(url));

            var db = new CharterDbContext(options.Options);
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            var tag = Guid.CreateVersion7().ToString("N");
            var organization = Organization.Create("Northbeam Solar", OrganizationMode.Organization);
            var adminUser = User.Create($"ada+{tag}@example.com", "Ada Admin", TeachingLevel.JustTheDecisions);
            var requesterUser = User.Create($"ayesha+{tag}@example.com", "Ayesha Rahman", TeachingLevel.SkipTheBasics);

            var adminMember = Member.Create(organization.Id, adminUser.Id, [MemberRole.Admin]);
            var requesterMember = Member.Create(organization.Id, requesterUser.Id, [MemberRole.Requester]);

            db.Organizations.Add(organization);
            db.Users.AddRange(adminUser, requesterUser);
            db.Members.AddRange(adminMember, requesterMember);

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            return new EmailFixture(
                db,
                MemberSnapshot.From(adminMember),
                MemberSnapshot.From(requesterMember),
                adminUser.Email,
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
