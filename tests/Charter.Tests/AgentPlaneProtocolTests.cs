using System.Text.Json;
using Daemon = Charter.Agent.Protocol;
using Plane = Charter.Runners.Agent;

namespace Charter.Tests;

/// <summary>
/// The two halves of section 33.6 agree about the wire.
/// </summary>
/// <remarks>
/// <para>
/// <c>Charter</c> and <c>Charter.Agent</c> do not reference each other — the daemon ships as a
/// self-contained single file (section 33.7) and must not carry EF Core and the whole control plane
/// with it — so the frame contract exists twice. That is a real risk and this file is the control for
/// it: every message type is built with one side's records, serialised with that side's options, and
/// read back with the other side's, in both directions.
/// </para>
/// <para>
/// A field renamed on one side, a casing policy changed, an enum spelled differently, a
/// <c>DefaultIgnoreCondition</c> quietly dropped — each of those is invisible to a compiler and each
/// would show up in production as an agent that connects, says hello, and then does nothing anyone
/// can explain. Section 33.6 exists precisely so that a mismatch is loud now rather than subtle three
/// sessions later, and a protocol constant is only worth as much as the test that pins it.
/// </para>
/// </remarks>
public class AgentPlaneProtocolTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 9, 12, 44, 113, TimeSpan.Zero);

    [Fact]
    public void TheProtocolConstantsAreIdenticalOnBothSides()
    {
        Assert.Equal(Daemon.AgentProtocol.Version, Plane.AgentProtocol.Version);
        Assert.Equal(Daemon.AgentProtocol.MinimumSupportedVersion, Plane.AgentProtocol.MinimumSupportedVersion);
        Assert.Equal(Daemon.AgentProtocol.VersionHeader, Plane.AgentProtocol.VersionHeader);
        Assert.Equal(Daemon.AgentProtocol.PairPath, Plane.AgentProtocol.PairPath);
        Assert.Equal(Daemon.AgentProtocol.ConnectPath, Plane.AgentProtocol.ConnectPath);
        Assert.Equal(Daemon.AgentProtocol.VersionQueryParameter, Plane.AgentProtocol.VersionQueryParameter);
        Assert.Equal(Daemon.AgentProtocol.SupportedVersions, Plane.AgentProtocol.SupportedVersions);
    }

    [Fact]
    public void TheCloseCodesAreIdenticalOnBothSides()
    {
        // 4001, 4003 and 4008 are the whole vocabulary of "why this socket ended". An agent that
        // reads 4003 stops retrying; one that reads 4008 does not. Getting these apart matters.
        Assert.Equal(Daemon.AgentProtocol.CloseProtocolMismatch, Plane.AgentProtocol.CloseProtocolMismatch);
        Assert.Equal(Daemon.AgentProtocol.CloseCredentialRevoked, Plane.AgentProtocol.CloseCredentialRevoked);
        Assert.Equal(Daemon.AgentProtocol.CloseReplaced, Plane.AgentProtocol.CloseReplaced);

        Assert.Equal(4001, Plane.AgentProtocol.CloseProtocolMismatch);
        Assert.Equal(4003, Plane.AgentProtocol.CloseCredentialRevoked);
        Assert.Equal(4008, Plane.AgentProtocol.CloseReplaced);
    }

    [Fact]
    public void EveryMessageTypeNameIsIdenticalOnBothSides()
    {
        Assert.Equal(Daemon.MessageTypes.Hello, Plane.MessageTypes.Hello);
        Assert.Equal(Daemon.MessageTypes.Welcome, Plane.MessageTypes.Welcome);
        Assert.Equal(Daemon.MessageTypes.ProtocolMismatch, Plane.MessageTypes.ProtocolMismatch);
        Assert.Equal(Daemon.MessageTypes.Heartbeat, Plane.MessageTypes.Heartbeat);
        Assert.Equal(Daemon.MessageTypes.HeartbeatAck, Plane.MessageTypes.HeartbeatAck);
        Assert.Equal(Daemon.MessageTypes.CapabilitiesReport, Plane.MessageTypes.CapabilitiesReport);
        Assert.Equal(Daemon.MessageTypes.JobClaim, Plane.MessageTypes.JobClaim);
        Assert.Equal(Daemon.MessageTypes.JobGrant, Plane.MessageTypes.JobGrant);
        Assert.Equal(Daemon.MessageTypes.JobEvent, Plane.MessageTypes.JobEvent);
        Assert.Equal(Daemon.MessageTypes.JobResult, Plane.MessageTypes.JobResult);
        Assert.Equal(Daemon.MessageTypes.JobCancel, Plane.MessageTypes.JobCancel);
        Assert.Equal(Daemon.MessageTypes.Goodbye, Plane.MessageTypes.Goodbye);
        Assert.Equal(Daemon.MessageTypes.Revoked, Plane.MessageTypes.Revoked);
        Assert.Equal(Daemon.MessageTypes.Error, Plane.MessageTypes.Error);
    }

    [Fact]
    public void TheEnvelopeTheDaemonWritesIsTheEnvelopeThePlaneReads()
    {
        var sent = Daemon.Envelope.Create(
            Daemon.MessageTypes.JobClaim,
            new Daemon.JobClaimPayload
            {
                MaxJobs = 2,
                Capabilities = ["linux", "docker", "dotnet:10.0.100"],
                Mode = "docker",
            },
            Now);

        var received = Plane.Envelope.FromJson(sent.ToJson());

        Assert.NotNull(received);
        Assert.Equal(sent.ProtocolVersion, received.ProtocolVersion);
        Assert.Equal(sent.Type, received.Type);
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal(sent.SentAt, received.SentAt);

        var payload = received.ReadPayload<Plane.JobClaimPayload>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.MaxJobs);
        Assert.Equal("docker", payload.Mode);
        Assert.Equal(["linux", "docker", "dotnet:10.0.100"], payload.Capabilities);
    }

    [Fact]
    public void TheEnvelopeThePlaneWritesIsTheEnvelopeTheDaemonReads()
    {
        var claimId = Guid.CreateVersion7().ToString("n");

        var sent = Plane.Envelope.Create(
            Plane.MessageTypes.JobGrant,
            new Plane.JobGrantPayload { Jobs = [], RetryAfterSeconds = 15 },
            Now,
            claimId);

        var received = Daemon.Envelope.FromJson(sent.ToJson());

        Assert.NotNull(received);
        Assert.Equal(Daemon.MessageTypes.JobGrant, received.Type);
        Assert.Equal(claimId, received.CorrelationId);

        var payload = received.ReadPayload<Daemon.JobGrantPayload>();
        Assert.NotNull(payload);
        Assert.Empty(payload.Jobs);
        Assert.Equal(15, payload.RetryAfterSeconds);
    }

    [Fact]
    public void TheRawFrameShapeIsTheOneTheEnvelopeDocumentationPromises()
    {
        // v, type, id, correlationId, sentAt, payload - short keys, not the CLR names. A naming
        // policy change on either side would rename all of these at once and break nothing at
        // compile time, so the literal keys are asserted rather than inferred.
        using var document = JsonDocument.Parse(
            Plane.Envelope.Create(
                Plane.MessageTypes.Revoked,
                new Plane.RevokedPayload { Reason = "Revoked by an administrator." },
                Now).ToJson());

        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("revoked", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("sentAt", out _));
        Assert.Equal(
            "Revoked by an administrator.",
            root.GetProperty("payload").GetProperty("reason").GetString());

        // WhenWritingNull on both sides: an absent correlation is an absent key, not a null one.
        Assert.False(root.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public void TheHandshakeRoundTripsBothWays()
    {
        var hello = Daemon.Envelope.Create(
            Daemon.MessageTypes.Hello,
            new Daemon.HelloPayload
            {
                ProtocolVersion = Daemon.AgentProtocol.Version,
                SupportedProtocolVersions = Daemon.AgentProtocol.SupportedVersions,
                AgentVersion = "0.1.0-dev",
                Name = "mac-mini",
                Mode = "native",
                Concurrency = 2,
                Platform = new Daemon.HostPlatform
                {
                    Os = "macos",
                    Arch = "arm64",
                    Rid = "osx-arm64",
                    Hostname = "studio.local",
                    CpuCount = 12,
                    TotalMemoryMb = 32_768,
                },
                Capabilities = ["macos", "xcode:16.2"],
                CapabilitiesProbedAt = Now,
                HeldJobIds = ["018f0000-0000-7000-8000-000000000001"],
            },
            Now);

        var read = Plane.Envelope.FromJson(hello.ToJson())!.ReadPayload<Plane.HelloPayload>();

        Assert.NotNull(read);
        Assert.Equal("mac-mini", read.Name);
        Assert.Equal("native", read.Mode);
        Assert.Equal(2, read.Concurrency);
        Assert.Equal("osx-arm64", read.Platform.Rid);
        Assert.Equal(32_768, read.Platform.TotalMemoryMb);
        Assert.Equal(["macos", "xcode:16.2"], read.Capabilities);
        Assert.Equal(Now, read.CapabilitiesProbedAt);
        Assert.Equal(["018f0000-0000-7000-8000-000000000001"], read.HeldJobIds);

        var welcome = Plane.Envelope.Create(
            Plane.MessageTypes.Welcome,
            new Plane.WelcomePayload
            {
                AgentId = "018f0000-0000-7000-8000-0000000000aa",
                ProtocolVersion = Plane.AgentProtocol.Version,
                SupportedProtocolVersions = Plane.AgentProtocol.SupportedVersions,
                ServerVersion = "0.1.0-dev",
                HeartbeatSeconds = 30,
                LeaseSeconds = 300,
                ReprobeSeconds = 86_400,
                ClaimIntervalSeconds = 5,
            },
            Now,
            hello.Id);

        var answered = Daemon.Envelope.FromJson(welcome.ToJson())!.ReadPayload<Daemon.WelcomePayload>();

        Assert.NotNull(answered);
        Assert.Equal(30, answered.HeartbeatSeconds);
        Assert.Equal(300, answered.LeaseSeconds);
        Assert.Equal(86_400, answered.ReprobeSeconds);
        Assert.Equal(5, answered.ClaimIntervalSeconds);

        // The daemon's own negotiator has to accept what the plane sent, or the agent refuses work
        // on a version both sides can speak.
        var negotiated = Daemon.ProtocolNegotiation.Evaluate(answered);
        Assert.True(negotiated.Ok);
        Assert.Equal(Daemon.AgentProtocol.Version, negotiated.AgreedVersion);
    }

    [Fact]
    public void ThePlanesMismatchFrameIsOneTheDaemonExplainsRatherThanIgnores()
    {
        var mismatch = Plane.Envelope.Create(
            Plane.MessageTypes.ProtocolMismatch,
            new Plane.ProtocolMismatchPayload
            {
                ServerProtocolVersion = Plane.AgentProtocol.Version,
                SupportedProtocolVersions = Plane.AgentProtocol.SupportedVersions,
                Message = Plane.AgentProtocol.DescribeMismatch(99),
            },
            Now);

        var read = Daemon.Envelope.FromJson(mismatch.ToJson())!.ReadPayload<Daemon.ProtocolMismatchPayload>();
        Assert.NotNull(read);

        var negotiation = Daemon.ProtocolNegotiation.Evaluate(read);

        // Section 33.6: a refusal, and a sentence naming both versions rather than a status code.
        Assert.False(negotiation.Ok);
        Assert.NotNull(negotiation.Message);
        Assert.Contains("99", negotiation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGrantWithSecretsRoundTripsIntoTheEnvironmentTheDaemonWouldBuild()
    {
        var grant = Plane.Envelope.Create(
            Plane.MessageTypes.JobGrant,
            new Plane.JobGrantPayload
            {
                Jobs =
                [
                    new Plane.JobAssignment
                    {
                        JobId = "018f0000-0000-7000-8000-000000000042",
                        Type = "build",
                        LeaseExpiresAt = Now.AddMinutes(5),
                        Attempt = 1,
                        MaxAttempts = 3,
                        RequiredCapabilities = ["linux", "dotnet:10"],
                        Repo = new Plane.JobRepo
                        {
                            FullName = "acme/widgets",
                            CloneUrl = "https://github.com/acme/widgets.git",
                            DefaultBranch = "main",
                            Branch = "main",
                            CacheScope = "acme/widgets",
                        },
                        RunnerImage = "ghcr.io/binn/charter-runner-fullstack:1",
                        Command = new Plane.JobCommand
                        {
                            Executable = "charter-runner-shim",
                            Arguments = ["run", "--session-id", "018f0000-0000-7000-8000-000000000007"],
                            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["CHARTER_PATH_SCOPE"] = """{"allow":[],"deny":[]}""",
                            },
                            WorkingSubdirectory = "workspace",
                        },
                        Secrets = new Plane.JobSecrets
                        {
                            GitHub = new Plane.GitHubInstallationToken
                            {
                                Token = "ghs_scoped_to_one_repo",
                                Repository = "acme/widgets",
                                ExpiresAt = Now.AddHours(1),
                            },
                            Model = new Plane.ModelCredential
                            {
                                Provider = "openrouter",
                                ApiKey = "sk-or-scoped",
                            },
                        },
                        TimeoutSeconds = 3600,
                    },
                ],
            },
            Now);

        var read = Daemon.Envelope.FromJson(grant.ToJson())!.ReadPayload<Daemon.JobGrantPayload>();

        Assert.NotNull(read);
        var job = Assert.Single(read.Jobs);

        Assert.Equal("018f0000-0000-7000-8000-000000000042", job.JobId);
        Assert.Equal("build", job.Type);
        Assert.Equal(Now.AddMinutes(5), job.LeaseExpiresAt);
        Assert.Equal("acme/widgets", job.Repo?.FullName);
        Assert.Equal("acme/widgets", job.Repo?.CacheScope);
        Assert.Equal("charter-runner-shim", job.Command.Executable);
        Assert.Equal("workspace", job.Command.WorkingSubdirectory);
        Assert.Equal(3600, job.TimeoutSeconds);

        // The end of the journey: the daemon turns exactly this into the child process environment.
        var environment = Charter.Agent.Execution.JobEnvironment.Build(job);

        Assert.Equal("ghs_scoped_to_one_repo", environment["GITHUB_TOKEN"]);
        Assert.Equal("acme/widgets", environment["CHARTER_GITHUB_REPOSITORY"]);
        Assert.Equal("sk-or-scoped", environment["OPENROUTER_API_KEY"]);
        Assert.Equal("""{"allow":[],"deny":[]}""", environment["CHARTER_PATH_SCOPE"]);

        // And the scrubber has to be able to find both values, or a job that echoes its own token
        // streams it back over job.event (section 33.5).
        Assert.Equal(
            ["ghs_scoped_to_one_repo", "sk-or-scoped"],
            job.Secrets!.Values().ToArray());
    }

    [Fact]
    public void TheDaemonsResultsAndEventsRoundTripIntoThePlane()
    {
        var result = Daemon.Envelope.Create(
            Daemon.MessageTypes.JobResult,
            new Daemon.JobResultPayload
            {
                JobId = "018f0000-0000-7000-8000-000000000042",
                Outcome = Charter.Agent.Jobs.JobOutcomes.Abandoned,
                ExitCode = null,
                Error = "the lease expired before it could be renewed",
                FinishedAt = Now,
                DurationMs = 1234,
            },
            Now);

        var read = Plane.Envelope.FromJson(result.ToJson())!.ReadPayload<Plane.JobResultPayload>();

        Assert.NotNull(read);
        Assert.Equal(Plane.AgentJobOutcomes.Abandoned, read.Outcome);
        Assert.Null(read.ExitCode);
        Assert.Equal(1234, read.DurationMs);

        var streamed = Daemon.Envelope.Create(
            Daemon.MessageTypes.JobEvent,
            new Daemon.JobEventPayload
            {
                JobId = "018f0000-0000-7000-8000-000000000042",
                Sequence = 7,
                Kind = "stdout",
                Message = "restored 214 packages",
                At = Now,
            },
            Now);

        var events = Plane.Envelope.FromJson(streamed.ToJson())!.ReadPayload<Plane.JobEventPayload>();

        Assert.NotNull(events);
        Assert.Equal(7, events.Sequence);
        Assert.Equal("stdout", events.Kind);
        Assert.Equal("restored 214 packages", events.Message);
    }

    [Fact]
    public void TheOutcomeVocabularyIsIdenticalOnBothSides()
    {
        Assert.Equal(Charter.Agent.Jobs.JobOutcomes.Succeeded, Plane.AgentJobOutcomes.Succeeded);
        Assert.Equal(Charter.Agent.Jobs.JobOutcomes.Failed, Plane.AgentJobOutcomes.Failed);
        Assert.Equal(Charter.Agent.Jobs.JobOutcomes.Cancelled, Plane.AgentJobOutcomes.Cancelled);
        Assert.Equal(Charter.Agent.Jobs.JobOutcomes.Abandoned, Plane.AgentJobOutcomes.Abandoned);
    }

    [Fact]
    public void HeartbeatAndItsAckRoundTripWithTheLeasesTheAgentRenews()
    {
        var heartbeat = Daemon.Envelope.Create(
            Daemon.MessageTypes.Heartbeat,
            new Daemon.HeartbeatPayload
            {
                Status = "busy",
                HeldJobIds = ["018f0000-0000-7000-8000-000000000042"],
                AvailableSlots = 1,
                CapabilitiesHash = "abc123",
            },
            Now);

        var read = Plane.Envelope.FromJson(heartbeat.ToJson())!.ReadPayload<Plane.HeartbeatPayload>();
        Assert.NotNull(read);
        Assert.Equal("busy", read.Status);
        Assert.Equal("abc123", read.CapabilitiesHash);

        var ack = Plane.Envelope.Create(
            Plane.MessageTypes.HeartbeatAck,
            new Plane.HeartbeatAckPayload
            {
                Leases =
                [
                    new Plane.LeaseGrant
                    {
                        JobId = "018f0000-0000-7000-8000-000000000042",
                        LeaseExpiresAt = Now.AddMinutes(5),
                    },
                ],
                ReprobeRequested = true,
            },
            Now,
            heartbeat.Id);

        var answered = Daemon.Envelope.FromJson(ack.ToJson())!.ReadPayload<Daemon.HeartbeatAckPayload>();

        Assert.NotNull(answered);
        Assert.True(answered.ReprobeRequested);
        Assert.Equal(Now.AddMinutes(5), Assert.Single(answered.Leases).LeaseExpiresAt);
    }

    [Fact]
    public void CapabilityReportsCancelsGoodbyesAndErrorsAllRoundTrip()
    {
        var report = Daemon.Envelope.Create(
            Daemon.MessageTypes.CapabilitiesReport,
            new Daemon.CapabilitiesReportPayload
            {
                Capabilities = ["macos", "xcode:16.2"],
                ProbedAt = Now,
                CapabilitiesHash = "deadbeef",
                Probes =
                [
                    new Daemon.ProbeReport { Name = "xcodebuild", ToolPresent = true, Capabilities = ["xcode:16.2"] },
                    new Daemon.ProbeReport { Name = "probe-rs", ToolPresent = false, Note = "not installed" },
                ],
            },
            Now);

        var readReport = Plane.Envelope.FromJson(report.ToJson())!.ReadPayload<Plane.CapabilitiesReportPayload>();
        Assert.NotNull(readReport);
        Assert.Equal("deadbeef", readReport.CapabilitiesHash);
        Assert.Equal(2, readReport.Probes.Count);
        Assert.Equal("not installed", readReport.Probes[1].Note);

        var cancel = Plane.Envelope.Create(
            Plane.MessageTypes.JobCancel,
            new Plane.JobCancelPayload { JobId = "018f0000-0000-7000-8000-000000000042", Reason = "Cancelled by request." },
            Now);

        var readCancel = Daemon.Envelope.FromJson(cancel.ToJson())!.ReadPayload<Daemon.JobCancelPayload>();
        Assert.NotNull(readCancel);
        Assert.Equal("Cancelled by request.", readCancel.Reason);

        var goodbye = Daemon.Envelope.Create(
            Daemon.MessageTypes.Goodbye,
            new Daemon.GoodbyePayload
            {
                Reason = "charter-agent shutting down",
                ReleasedJobIds = ["018f0000-0000-7000-8000-000000000042"],
            },
            Now);

        var readGoodbye = Plane.Envelope.FromJson(goodbye.ToJson())!.ReadPayload<Plane.GoodbyePayload>();
        Assert.NotNull(readGoodbye);
        Assert.Single(readGoodbye.ReleasedJobIds);

        var error = Plane.Envelope.Create(
            Plane.MessageTypes.Error,
            new Plane.ErrorPayload { Code = Plane.AgentErrorCodes.HandshakeRequired, Message = "Send hello first." },
            Now);

        var readError = Daemon.Envelope.FromJson(error.ToJson())!.ReadPayload<Daemon.ErrorPayload>();
        Assert.NotNull(readError);
        Assert.Equal("handshake_required", readError.Code);
    }

    [Fact]
    public void ThePairingBodiesRoundTripBetweenTheClientAndTheEndpoint()
    {
        var request = new Daemon.PairRequest
        {
            PairingToken = "chpair_0192.secret",
            Name = "mac-mini",
            Mode = "native",
            AgentVersion = "0.1.0-dev",
            ProtocolVersion = Daemon.AgentProtocol.Version,
            Concurrency = 2,
            Platform = new Daemon.HostPlatform
            {
                Os = "macos",
                Arch = "arm64",
                Rid = "osx-arm64",
                Hostname = "studio.local",
                CpuCount = 12,
            },
            Capabilities = ["macos", "xcode:16.2"],
        };

        var json = JsonSerializer.Serialize(request, Daemon.AgentJson.Options);
        var read = JsonSerializer.Deserialize<Plane.PairRequest>(json, Plane.AgentJson.Options);

        Assert.NotNull(read);
        Assert.Equal("chpair_0192.secret", read.PairingToken);
        Assert.Equal("native", read.Mode);
        Assert.Equal(12, read.Platform.CpuCount);
        Assert.Null(read.Platform.TotalMemoryMb);

        var response = JsonSerializer.Serialize(
            new Plane.PairResponse
            {
                AgentId = "018f0000-0000-7000-8000-0000000000aa",
                AgentToken = "chagt_0192.secret",
                ProtocolVersion = Plane.AgentProtocol.Version,
            },
            Plane.AgentJson.Options);

        var readResponse = JsonSerializer.Deserialize<Daemon.PairResponse>(response, Daemon.AgentJson.Options);

        Assert.NotNull(readResponse);
        Assert.Equal("chagt_0192.secret", readResponse.AgentToken);
        Assert.Equal(Daemon.AgentProtocol.Version, readResponse.ProtocolVersion);

        var failure = JsonSerializer.Serialize(
            new Plane.ErrorResponse
            {
                Error = Plane.AgentErrorCodes.PairingTokenExpired,
                Message = "This pairing token has expired or was already used.",
            },
            Plane.AgentJson.Options);

        var readFailure = JsonSerializer.Deserialize<Daemon.ErrorResponse>(failure, Daemon.AgentJson.Options);

        Assert.NotNull(readFailure);
        Assert.Equal("pairing_token_expired", readFailure.Error);
    }

    [Fact]
    public void NoSecretSurvivesAToStringOnEitherSide()
    {
        // A record printer is how a credential ends up in a log line written by somebody who had no
        // idea they were holding one (section 33.5).
        var secrets = new Plane.JobSecrets
        {
            GitHub = new Plane.GitHubInstallationToken
            {
                Token = "ghs_never_log_me",
                Repository = "acme/widgets",
                ExpiresAt = Now,
            },
            Model = new Plane.ModelCredential { Provider = "anthropic", ApiKey = "sk-never-log-me" },
        };

        Assert.DoesNotContain("ghs_never_log_me", secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-never-log-me", secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ghs_never_log_me", $"{secrets.GitHub}", StringComparison.Ordinal);
        Assert.DoesNotContain("sk-never-log-me", $"{secrets.Model}", StringComparison.Ordinal);

        var response = new Plane.PairResponse
        {
            AgentId = "018f",
            AgentToken = "chagt_never_log_me",
            ProtocolVersion = 1,
        };

        Assert.DoesNotContain("chagt_never_log_me", response.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, new[] { 1 }, 1)]
    [InlineData(2, new[] { 2, 1 }, 1)]
    [InlineData(2, new int[0], 0)]
    [InlineData(99, new[] { 99 }, 0)]
    public void NegotiationPicksTheNewestVersionBothSidesSpeak(int asked, int[] supported, int expected)
        => Assert.Equal(expected, Plane.AgentProtocol.Negotiate(asked, supported));
}
