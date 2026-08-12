using System.Text.Json;
using Charter.Data;
using Charter.Deployments;
using Charter.Domain;
using Charter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Section 6's second notifying state, from the artifact turning ready to the requester being told.
/// </summary>
/// <remarks>
/// <para>
/// <c>PreviewReady</c> is the one moment in the loop the requester is actually waiting for —
/// <em>Ready to try</em> — and it is one of exactly two states that notify anybody. Before this
/// wiring it existed in the enum, the label table and the projection, and nothing ever set it, so the
/// notification could not fire and the thread never moved past <em>building this now</em>.
/// </para>
/// <para>
/// The section 6 gate itself is not re-tested here; it is
/// <see cref="NotifyWorthyStates"/>'s and <see cref="NotificationService"/>'s, checked once above the
/// channels. What is tested is that this path reaches it with the right state and the right payload.
/// </para>
/// </remarks>
public class DeploymentPreviewReadyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DeploymentOptions Settings => new()
    {
        Provider = DeploymentProviderKind.None,
        PreviewTtl = TimeSpan.FromHours(8),
        ProbeReachability = false,
        BaseUrl = new Uri("https://charter.example.test/"),
    };

    [Fact]
    public async Task AReadyPreviewMovesTheRequestToPreviewReadyAndTellsTheRequesterOnce()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var publisher = fixture.Publisher(Settings);
        var deployment = Ready(fixture);

        var first = await publisher.ApplyAsync(fixture.SessionId, deployment, Token);

        Assert.True(first.Changed);
        Assert.Equal(VerificationArtifactState.Ready, first.State);

        Assert.Equal(RequestStatus.PreviewReady, await RequestStatusAsync(fixture));
        Assert.Equal(SessionStatus.PreviewReady, await SessionStatusAsync(fixture));

        var told = Assert.Single(fixture.Notifications.Sent);

        Assert.Equal(RequestStatus.PreviewReady, told.Status);
        Assert.Equal(fixture.Scenario.User.Id, told.Recipient.UserId);
        Assert.Equal(fixture.Scenario.User.Email, told.Recipient.Email);

        // Section 11: "what to check" is the approved acceptance criteria, verbatim. Without it a
        // preview URL is a dead end.
        Assert.Equal(DeploymentScenario.Criteria, told.WhatToCheck);
        Assert.Equal(
            new Uri($"https://charter.example.test/requests/{fixture.Scenario.Request.Id:D}"),
            told.ThreadUrl);

        // Applying the same deployment again is the same news, and the reconcile loop does exactly
        // that every fifteen seconds.
        var second = await publisher.ApplyAsync(fixture.SessionId, deployment, Token);

        Assert.False(second.Changed);
        Assert.Single(fixture.Notifications.Sent);
    }

    [Fact]
    public async Task TheRequesterIsNeverSentACommitARepositoryOrASessionId()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.Publisher(Settings).ApplyAsync(fixture.SessionId, Ready(fixture), Token);

        var told = Assert.Single(fixture.Notifications.Sent);
        var payload = JsonSerializer.Serialize(told);

        // Section 7.4, at the boundary that leaves the process. Absent, not redacted: the payload has
        // nowhere to put any of them.
        Assert.DoesNotContain(DeploymentScenario.HeadSha, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeploymentScenario.RepoFullName, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.SessionId.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DeploymentScenario.HeadBranch, payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APreviewThatIsStillBuildingNotifiesNobody()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var building = Deployment.Report(
            fixture.Scenario.ChangeRequest.Id,
            "stub",
            DeploymentState.Building,
            url: null,
            now: fixture.Clock.GetUtcNow());

        await fixture.Publisher(Settings).ApplyAsync(fixture.SessionId, building, Token);

        Assert.Empty(fixture.Notifications.Sent);
        Assert.NotEqual(RequestStatus.PreviewReady, await RequestStatusAsync(fixture));
    }

    [Fact]
    public async Task ARequestAnEngineerIsAlreadyReviewingIsNotDraggedBackToPreviewReady()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        // Section 6 puts InReview after PreviewReady. A preview redeploying is not a reason to tell
        // the requester their thread went backwards.
        var request = await fixture.Db.Requests.SingleAsync(
            row => row.Id == fixture.Scenario.Request.Id,
            Token);

        request.TransitionTo(RequestStatus.InReview, fixture.Clock.GetUtcNow());
        await fixture.Db.SaveChangesAsync(Token);
        fixture.Db.ChangeTracker.Clear();

        await fixture.Publisher(Settings).ApplyAsync(fixture.SessionId, Ready(fixture), Token);

        Assert.Equal(RequestStatus.InReview, await RequestStatusAsync(fixture));
    }

    [Fact]
    public async Task ANotificationServiceThatThrowsDoesNotRollBackTheTransition()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var announcer = new PreviewReadyAnnouncer(
            fixture.Db,
            Settings,
            fixture.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PreviewReadyAnnouncer>.Instance,
            new ThrowingNotificationService());

        // Notifying is a side effect of a state transition, and a transition must not roll back
        // because a mail server was down.
        var announcement = await announcer.AnnounceAsync(fixture.SessionId, Token);

        Assert.True(announcement.Moved);
        Assert.Null(announcement.Notification);
        Assert.Equal(RequestStatus.PreviewReady, await RequestStatusAsync(fixture));
    }

    [Fact]
    public async Task AnInstanceWithNoNotificationChannelStillMovesTheState()
    {
        await using var fixture = await DeploymentFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var announcement = await fixture.Announcer(Settings, notify: false)
            .AnnounceAsync(fixture.SessionId, Token);

        Assert.True(announcement.Moved);
        Assert.Null(announcement.Notification);
        Assert.Equal(RequestStatus.PreviewReady, await RequestStatusAsync(fixture));
        Assert.Empty(fixture.Notifications.Sent);
    }

    [Fact]
    public void TheAnnouncerResolvesOnAnInstanceWithNoNotificationServiceRegistered()
    {
        // Email is registered by AddCharterNotifications, which an instance running with
        // CHARTER_EMAIL_PROVIDER=none still calls — but a host that has not called it at all must
        // still boot. If this resolves, section 6's transition happens either way and only the
        // message is missing; if it throws, the deployment module takes the whole instance down.
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(CharterTime.System);
        services.AddSingleton(Settings);
        services.AddDbContext<CharterDbContext>(builder =>
            DataServiceCollectionExtensions.ConfigureNpgsql(builder, "Host=localhost;Database=unused"));
        services.AddScoped<PreviewReadyAnnouncer>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PreviewReadyAnnouncer>());
    }

    private static Deployment Ready(DeploymentFixture fixture)
        => Deployment.Report(
            fixture.Scenario.ChangeRequest.Id,
            "stub",
            DeploymentState.Ready,
            "https://quote-tool-pr-142.up.railway.app",
            now: fixture.Clock.GetUtcNow());

    private static async Task<RequestStatus> RequestStatusAsync(DeploymentFixture fixture)
    {
        fixture.Db.ChangeTracker.Clear();

        return await fixture.Db.Requests
            .AsNoTracking()
            .Where(row => row.Id == fixture.Scenario.Request.Id)
            .Select(row => row.Status)
            .SingleAsync(Token);
    }

    private static async Task<SessionStatus> SessionStatusAsync(DeploymentFixture fixture)
    {
        fixture.Db.ChangeTracker.Clear();

        return await fixture.Db.Sessions
            .AsNoTracking()
            .Where(row => row.Id == fixture.SessionId)
            .Select(row => row.Status)
            .SingleAsync(Token);
    }

    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationOutcome> NotifyAsync(
            RequestNotification notification,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The mail server refused the connection.");
    }
}
