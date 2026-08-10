using Charter.Agent.Execution;
using Charter.Agent.Jobs;
using Charter.Agent.Logging;
using Charter.Agent.Protocol;

namespace Charter.Tests;

/// <summary>
/// Section 33.5: the agent receives a short-TTL single-repo installation token and a scoped model
/// credential, per job, and neither ever reaches a log, a log line's exception text, or any frame
/// the agent sends back.
/// </summary>
public class AgentSecretTests
{
    private const string GitHubToken = "ghs_16C7e42F292c6912E7710c838347Ae178B4a";
    private const string ModelKey = "sk-ant-api03-VERY-SECRET-VALUE-0123456789";

    [Fact]
    public void ARegisteredSecretIsRedactedWhereverItAppears()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register(GitHubToken);

        var scrubbed = scrubber.Scrub($"git clone https://x-access-token:{GitHubToken}@github.com/acme/widgets.git");

        Assert.DoesNotContain(GitHubToken, scrubbed, StringComparison.Ordinal);
        Assert.Contains(SecretScrubber.Placeholder, scrubbed, StringComparison.Ordinal);
        Assert.Contains("github.com/acme/widgets.git", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOccurrenceOfEverySecretGoes()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register([GitHubToken, ModelKey]);

        var scrubbed = scrubber.Scrub($"{GitHubToken} then {ModelKey} then {GitHubToken} again");

        Assert.DoesNotContain(GitHubToken, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortValuesAreNotRegistered()
    {
        // Redacting a three-character string would blank out half of every line for no benefit,
        // and a secret that short is not one.
        var scrubber = new SecretScrubber();
        scrubber.Register("abc");

        Assert.Equal(0, scrubber.Count);
        Assert.Equal("abc def", scrubber.Scrub("abc def"));
    }

    [Fact]
    public void ASecretIsForgottenWhenItsJobEnds()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register(GitHubToken);
        Assert.Equal(1, scrubber.Count);

        scrubber.Forget(GitHubToken);

        Assert.Equal(0, scrubber.Count);
    }

    [Fact]
    public void AFingerprintIdentifiesACredentialWithoutRevealingIt()
    {
        var fingerprint = SecretScrubber.Fingerprint(GitHubToken);

        Assert.StartsWith("sha256:", fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, fingerprint, StringComparison.Ordinal);
        Assert.Equal(fingerprint, SecretScrubber.Fingerprint(GitHubToken));
        Assert.NotEqual(fingerprint, SecretScrubber.Fingerprint(ModelKey));
    }

    [Fact]
    public void TheLogItselfScrubsEveryLineItWrites()
    {
        var scrubber = new SecretScrubber();
        scrubber.Register(GitHubToken);
        var writer = new StringWriter();
        var log = new ConsoleAgentLog(scrubber, LogLevel.Debug, writer);

        log.Info($"cloning with {GitHubToken}");
        log.Error($"failed: remote rejected token {GitHubToken}");

        var written = writer.ToString();
        Assert.DoesNotContain(GitHubToken, written, StringComparison.Ordinal);
        Assert.Equal(2, written.Split(SecretScrubber.Placeholder).Length - 1);
    }

    [Fact]
    public void SecretBearingRecordsDoNotPrintThemselves()
    {
        // The record printer is the trap: any interpolated string touching one of these would
        // otherwise carry the token straight into a log line.
        var secrets = Secrets();

        Assert.DoesNotContain(GitHubToken, secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, $"{secrets.GitHub}", StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, $"{secrets.Model}", StringComparison.Ordinal);
        Assert.DoesNotContain(
            GitHubToken,
            new Charter.Agent.Pairing.AgentCredential
            {
                Server = "https://charter.example.com",
                AgentId = "agt_1",
                AgentToken = GitHubToken,
                PairedAt = DateTimeOffset.UnixEpoch,
            }.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsReachTheJobAsEnvironmentAndNothingElse()
    {
        var job = AgentSessionTests.Job("job-1", ["linux"], secrets: Secrets());

        var environment = JobEnvironment.Build(job);

        Assert.Equal(GitHubToken, environment["GITHUB_TOKEN"]);
        Assert.Equal(ModelKey, environment["ANTHROPIC_API_KEY"]);
        Assert.Equal("acme/widgets", environment["CHARTER_GITHUB_REPOSITORY"]);

        // Nothing that would put a secret on a command line, where ps would show it to every user.
        Assert.DoesNotContain(GitHubToken, string.Join(' ', job.Command.Arguments), StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, string.Join(' ', job.Command.Arguments), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic", "ANTHROPIC_API_KEY")]
    [InlineData("openai", "OPENAI_API_KEY")]
    [InlineData("openrouter", "OPENROUTER_API_KEY")]
    [InlineData("something-else", "CHARTER_MODEL_API_KEY")]
    public void EachProviderGetsTheVariableItsCliAlreadyLooksFor(string provider, string variable)
    {
        Assert.Equal(variable, JobEnvironment.ModelKeyVariable(provider));
    }

    [Fact]
    public async Task NoSecretReachesTheLogOrTheWireEvenWhenAJobEchoesOne()
    {
        // The realistic leak: a job's own tooling prints the token it was given, or throws with it
        // in the message. Both go through the scrubber before they can be logged or streamed.
        var transport = new FakeTransport();
        transport.OnSend(MessageTypes.Hello, _ => [AgentSessionTests.Welcome()]);
        var granted = 0;
        transport.OnSend(
            MessageTypes.JobClaim,
            _ => Interlocked.Increment(ref granted) == 1
                ? [AgentSessionTests.Grant(AgentSessionTests.Job("job-1", ["linux"], secrets: Secrets()))]
                : []);

        var executor = new StubExecutor
        {
            Run = (job, events) =>
            {
                events.Publish(job.JobId, "stdout", $"authenticating with {GitHubToken}");
                events.Publish(job.JobId, "stderr", $"remote: rejected key {ModelKey} for acme/widgets");
                throw new InvalidOperationException($"clone failed using {GitHubToken}");
            },
        };

        var harness = new DaemonHarness(transport, executor);
        await harness.RunUntilAsync(() => transport.Sent.Any(e => e.Type == MessageTypes.JobResult));

        var wire = string.Join('\n', transport.Sent.Select(e => e.ToJson()));
        var logged = string.Join('\n', harness.Log.Lines);

        Assert.DoesNotContain(GitHubToken, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(ModelKey, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-token-value-that-is-long", logged, StringComparison.Ordinal);

        // The output still arrived - it is redacted, not dropped.
        var events = transport.Sent
            .Where(e => e.Type == MessageTypes.JobEvent)
            .Select(e => e.ReadPayload<JobEventPayload>()!)
            .ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Contains(SecretScrubber.Placeholder, e.Message, StringComparison.Ordinal));
        Assert.Contains(events, e => e.Message.Contains("for acme/widgets", StringComparison.Ordinal));

        var result = Assert.Single(transport.Sent, e => e.Type == MessageTypes.JobResult);
        var payload = result.ReadPayload<JobResultPayload>()!;
        Assert.Equal(JobOutcomes.Failed, payload.Outcome);
        Assert.Contains(SecretScrubber.Placeholder, payload.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentNeverAsksForAnythingItIsNotEntitledTo()
    {
        // Section 33.5 and section 32.8: what crosses is one repository's installation token and a
        // scoped model credential. There is nowhere in the schema for a refresh token, a signing
        // identity, a licence, or a registry credential - they are the operator's, held locally.
        var fields = typeof(JobSecrets).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(["GitHub", "Model"], fields);
        Assert.Single(typeof(GitHubInstallationToken).GetProperties(), p => p.Name == "Repository");
        Assert.DoesNotContain(
            typeof(JobSecrets).GetProperties().Concat(typeof(GitHubInstallationToken).GetProperties()),
            p => p.Name.Contains("Refresh", StringComparison.OrdinalIgnoreCase));
    }

    private static JobSecrets Secrets() => new()
    {
        GitHub = new GitHubInstallationToken
        {
            Token = GitHubToken,
            Repository = "acme/widgets",
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1),
        },
        Model = new ModelCredential { Provider = "anthropic", ApiKey = ModelKey },
    };
}
