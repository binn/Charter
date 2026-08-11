using Charter.Data;
using Charter.Domain;
using Charter.Runners.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Registration and revocation (section 33.3).
/// </summary>
/// <remarks>
/// The pairing token is the one moment an operator holds a secret that can turn into a runner. Every
/// property that makes that safe — single use, short TTL, no credential ever written to a row, and a
/// status-code contract the daemon acts on rather than guesses at — is asserted here.
/// </remarks>
[Collection(AgentPlaneCollection.Name)]
public class AgentPlanePairingTests
{
    [Fact]
    public async Task APairingTokenWorksExactlyOnce()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        var first = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(invitation.PairingToken), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.Paired, first.Outcome);
        Assert.Equal(invitation.AgentId.ToString("D"), first.Response!.AgentId);
        Assert.StartsWith(AgentCredentialMint.AgentTokenPrefix, first.Response.AgentToken, StringComparison.Ordinal);

        // Section 33.3: single use. The daemon reads 410 as "generate a fresh one" and stops.
        var second = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(invitation.PairingToken), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.TokenExpired, second.Outcome);
        Assert.Null(second.Response);
        Assert.Equal(AgentErrorCodes.PairingTokenExpired, second.ErrorCode);
    }

    [Fact]
    public async Task APairingTokenExpires()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>().InviteAsync(
                fixture.OrgId,
                "mac-mini",
                TimeSpan.FromMinutes(15),
                TestContext.Current.CancellationToken));

        fixture.Clock.Advance(TimeSpan.FromMinutes(16));

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(invitation.PairingToken), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.TokenExpired, result.Outcome);

        // And the row is still there with its verifier intact rather than half-paired.
        var agent = await fixture.AgentAsync(invitation.AgentId);
        Assert.NotNull(agent);
        Assert.False(agent.IsPaired);
        Assert.Null(agent.CredentialHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("chpair_notaguid.secret")]
    public async Task AForgedPairingTokenIsRejectedAndNotRetried(string forged)
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(forged), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.TokenRejected, result.Outcome);
        Assert.Equal(AgentErrorCodes.PairingTokenRejected, result.ErrorCode);
    }

    [Fact]
    public async Task ATokenWithTheRightShapeButTheWrongSecretIsRejected()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        var tampered = $"{AgentCredentialMint.PairingTokenPrefix}{invitation.AgentId:n}.wrong-secret";

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(tampered), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.TokenRejected, result.Outcome);
    }

    [Fact]
    public async Task AnUnsupportedProtocolIsRefusedRatherThanPaired()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>().PairAsync(
                fixture.PairRequestFor(invitation.PairingToken, protocolVersion: AgentProtocol.Version + 1),
                TestContext.Current.CancellationToken));

        // 426 in the endpoint. Section 33.6: the message names both versions and says which to
        // upgrade, rather than failing subtly three sessions later.
        Assert.Equal(AgentPairingOutcome.ProtocolRefused, result.Outcome);
        Assert.Contains("Protocol mismatch", result.Message, StringComparison.Ordinal);

        // The token was not spent on a refusal, so an upgraded agent can still use it.
        var agent = await fixture.AgentAsync(invitation.AgentId);
        Assert.NotNull(agent);
        Assert.True(agent.PairingTokenIsLiveAt(fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task NoCredentialIsEverWrittenToARow()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(invitation.PairingToken), TestContext.Current.CancellationToken));

        var agent = await fixture.AgentAsync(invitation.AgentId);
        Assert.NotNull(agent);

        // Both tokens are bearer secrets. What survives is a verifier and nothing else - a database
        // dump must not be replayable against the connect endpoint.
        var row = string.Join(
            "|",
            agent.Name,
            agent.CredentialHash,
            agent.PairingTokenHash,
            agent.CapabilitiesHash,
            agent.Hostname,
            string.Join(",", agent.Capabilities));

        Assert.DoesNotContain(invitation.PairingToken, row, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Response!.AgentToken, row, StringComparison.Ordinal);

        // The secret half specifically, not just the whole token.
        Assert.True(AgentCredentialMint.TryParse(
            result.Response.AgentToken,
            AgentCredentialMint.AgentTokenPrefix,
            out _,
            out var secret));

        Assert.DoesNotContain(secret, row, StringComparison.Ordinal);

        // And the pairing verifier is gone the moment it is spent.
        Assert.Null(agent.PairingTokenHash);
        Assert.NotNull(agent.CredentialHash);
        Assert.True(agent.IsPaired);
    }

    [Fact]
    public async Task PairingRecordsWhatTheAgentProbedRatherThanWhatItDeclared()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync(
            "mac-mini",
            capabilities: ["macos", "xcode:16.2"],
            concurrency: 3,
            mode: "native");

        var agent = await fixture.AgentAsync(agentId);
        Assert.NotNull(agent);

        Assert.Equal(RunnerAgentMode.Native, agent.Mode);
        Assert.Equal(3, agent.Concurrency);
        Assert.Equal("linux-x64", agent.Rid);

        // Section 27.3: expanded at registration, so matching is set containment and the SQL filter
        // and the C# matcher can never disagree.
        Assert.Contains("xcode", agent.Capabilities);
        Assert.Contains("xcode:16", agent.Capabilities);
        Assert.Contains("xcode:16.2", agent.Capabilities);
        Assert.Contains("macos", agent.Capabilities);

        // Not online until it connects. A paired agent is not a running one.
        Assert.Equal(RunnerAgentStatus.Offline, agent.Status);
        Assert.False(agent.IsOnlineAt(fixture.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task TheCredentialAuthenticatesAndAForgedOneDoesNot()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, token) = await fixture.PairAsync();

        var good = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .AuthenticateAsync(token, TestContext.Current.CancellationToken));

        Assert.Equal(AgentAuthOutcome.Authenticated, good.Outcome);
        Assert.Equal(agentId, good.AgentId);

        var forged = $"{AgentCredentialMint.AgentTokenPrefix}{agentId:n}.forged-secret";

        var bad = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .AuthenticateAsync(forged, TestContext.Current.CancellationToken));

        Assert.Equal(AgentAuthOutcome.Unknown, bad.Outcome);

        // A credential for an agent that does not exist is answered the same way, so the endpoint is
        // not an enumeration oracle for which agent ids are real.
        var unknown = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>().AuthenticateAsync(
                $"{AgentCredentialMint.AgentTokenPrefix}{Guid.CreateVersion7():n}.whatever",
                TestContext.Current.CancellationToken));

        Assert.Equal(AgentAuthOutcome.Unknown, unknown.Outcome);
    }

    [Fact]
    public async Task ARevokedCredentialIsRefusedAndTheVerifierIsDestroyed()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, token) = await fixture.PairAsync();

        await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .RevokeAsync(agentId, "Laptop was stolen.", TestContext.Current.CancellationToken));

        var authentication = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .AuthenticateAsync(token, TestContext.Current.CancellationToken));

        // 403, not 401: the operator needs to know the difference between "wrong token" and "this
        // agent is revoked" when they read the daemon's log.
        Assert.Equal(AgentAuthOutcome.Revoked, authentication.Outcome);

        var agent = await fixture.AgentAsync(agentId);
        Assert.NotNull(agent);
        Assert.Equal(RunnerAgentStatus.Revoked, agent.Status);
        Assert.Equal("Laptop was stolen.", agent.RevokedReason);

        // Destroyed, not flagged. A build that forgot the status check still could not authenticate.
        Assert.Null(agent.CredentialHash);
        Assert.Null(agent.PairingTokenHash);
    }

    [Fact]
    public async Task ARevokedAgentCannotBePairedAgainWithAnOldInvitation()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .RevokeAsync(invitation.AgentId, "Never mind.", TestContext.Current.CancellationToken));

        var result = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .PairAsync(fixture.PairRequestFor(invitation.PairingToken), TestContext.Current.CancellationToken));

        Assert.Equal(AgentPairingOutcome.Revoked, result.Outcome);
    }

    [Fact]
    public async Task RevokingReturnsEveryJobTheAgentWasHoldingToTheQueue()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync();
        var jobId = await fixture.EnqueueClaimableAsync(Guid.CreateVersion7());

        var claimed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<JobQueue>().ClaimAsync(
                AgentRunner.WorkerIdFor(agentId),
                fixture.Options.Lease,
                1,
                [AgentRunner.ClaimCapability, fixture.Tag],
                AgentRunner.ClaimCapability,
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken));

        Assert.Equal(jobId, Assert.Single(claimed).Id);

        await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .RevokeAsync(agentId, "Revoked mid-build.", TestContext.Current.CancellationToken));

        // Section 33.3: revocation kills in-flight jobs. The work is runnable by somebody else the
        // moment the credential dies, rather than after a five-minute lease.
        var job = await fixture.JobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.ClaimedBy);
    }

    [Fact]
    public async Task TheRunnersListShowsEveryAgentAndWhetherItIsOnline()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var (agentId, _) = await fixture.PairAsync("mac-mini", ["macos", "xcode:16.2"]);

        var listed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .ListAsync(fixture.OrgId, TestContext.Current.CancellationToken));

        var view = Assert.Single(listed);

        Assert.Equal(agentId, view.Id);
        Assert.Equal("mac-mini", view.Name);
        Assert.Equal("offline", view.Status);
        Assert.False(view.Online);
        Assert.Contains("xcode:16", view.Capabilities);
        Assert.NotNull(view.PairedAt);
    }

    [Fact]
    public async Task AnAgentBelongingToAnotherOrganisationIsNotListed()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        await fixture.PairAsync();

        var listed = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .ListAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Empty(listed);
    }

    [Fact]
    public async Task TwoAgentsRacingForOneTokenProduceExactlyOneCredential()
    {
        await using var fixture = await AgentPlaneFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var invitation = await fixture.InScopeAsync(async provider =>
            await provider.GetRequiredService<AgentPlaneService>()
                .InviteAsync(fixture.OrgId, "mac-mini", cancellationToken: TestContext.Current.CancellationToken));

        // Two scopes, two DbContexts, both reading a live row. The concurrency token on the entity is
        // what makes exactly one of them win - a copied command line run twice must not produce two
        // runners sharing one identity.
        await using var first = fixture.Scope();
        await using var second = fixture.Scope();

        var request = fixture.PairRequestFor(invitation.PairingToken);

        var one = await first.ServiceProvider.GetRequiredService<AgentPlaneService>()
            .PairAsync(request, TestContext.Current.CancellationToken);
        var two = await second.ServiceProvider.GetRequiredService<AgentPlaneService>()
            .PairAsync(request, TestContext.Current.CancellationToken);

        Assert.True(one.Ok ^ two.Ok, "Exactly one of the two attempts must have paired.");

        var loser = one.Ok ? two : one;
        Assert.Equal(AgentPairingOutcome.TokenExpired, loser.Outcome);

        var agent = await fixture.AgentAsync(invitation.AgentId);
        Assert.NotNull(agent);
        Assert.True(agent.IsPaired);
    }

    [Fact]
    public async Task TheEntityRefusesToPairARevokedAgent()
    {
        // A domain invariant rather than an endpoint check: the state machine itself refuses, so a
        // future caller that skipped the status check still cannot resurrect a revoked runner.
        var agent = RunnerAgent.Invite(Guid.CreateVersion7(), "mac-mini", "hash");
        agent.Revoke("Stolen.");

        await Task.CompletedTask;

        Assert.Throws<InvalidOperationException>(() => agent.CompletePairing(
            "another-hash",
            "mac-mini",
            RunnerAgentMode.Docker,
            "0.1.0",
            1,
            2,
            new RunnerAgentPlatform("linux", "x64", "linux-x64", "host", 4),
            []));
    }
}
