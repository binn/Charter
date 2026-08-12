using System.Net;
using System.Text.Json;
using Charter.Configuration;
using Charter.Data;
using Charter.Domain;
using Charter.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Charter.Tests;

/// <summary>
/// Section 28's release check, offline.
/// </summary>
/// <remarks>
/// Every test here runs with no network. That is not a convenience: the check's whole contract is what
/// it does when GitHub is unreachable, what it sends when it is, and what it decides afterwards, and
/// all three are decided by code that never has to open a socket to be exercised.
/// </remarks>
public class UpdateCheckTests
{
    private const string ReleasesJson = """
        [
          {
            "tag_name": "v0.6.0",
            "name": "v0.6.0",
            "html_url": "https://github.com/binn/Charter/releases/tag/v0.6.0",
            "body": "Ordinary release.",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-08-01T10:00:00Z"
          },
          {
            "tag_name": "v0.7.0-rc.1",
            "name": "v0.7.0-rc.1",
            "html_url": "https://github.com/binn/Charter/releases/tag/v0.7.0-rc.1",
            "body": "Release candidate.",
            "draft": false,
            "prerelease": true,
            "published_at": "2026-08-05T10:00:00Z"
          },
          {
            "tag_name": "v0.8.0",
            "name": "v0.8.0",
            "html_url": "https://github.com/binn/Charter/releases/tag/v0.8.0",
            "body": "Unreleased.",
            "draft": true,
            "prerelease": false,
            "published_at": null
          },
          {
            "tag_name": "nightly",
            "name": "nightly",
            "html_url": "https://github.com/binn/Charter/releases/tag/nightly",
            "body": "Not a version.",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-08-06T10:00:00Z"
          }
        ]
        """;

    // ── The tag grammar ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v0.5.0-rc.1", "0.5.0-rc.1")]
    [InlineData("v0.5.0+build.7", "0.5.0")]
    public void ATagParsesIntoTheVersionItNames(string tag, string expected)
        => Assert.Equal(expected, ReleaseVersion.TryParse(tag)?.ToString());

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("release-2026-08")]
    [InlineData("v1.2.x")]
    [InlineData("")]
    [InlineData(null)]
    public void ATagThatIsNotAVersionIsIgnoredRatherThanGuessedAt(string? tag)
        => Assert.Null(ReleaseVersion.TryParse(tag));

    [Fact]
    public void APrereleaseSortsBelowTheReleaseItLeadsTo()
    {
        var candidate = ReleaseVersion.TryParse("v0.5.0-rc.1")!;
        var release = ReleaseVersion.TryParse("v0.5.0")!;

        Assert.True(candidate.CompareTo(release) < 0);
        Assert.True(candidate.IsOlderThan(release));
        Assert.False(release.IsOlderThan(candidate));
    }

    [Fact]
    public void PrereleaseIdentifiersCompareNumericallyWhereTheyAreNumbers()
    {
        var second = ReleaseVersion.TryParse("v0.5.0-rc.2")!;
        var tenth = ReleaseVersion.TryParse("v0.5.0-rc.10")!;

        // Ordinally, "rc.10" sorts before "rc.2", which would offer an instance a build it already has.
        Assert.True(second.CompareTo(tenth) < 0);
    }

    // ── What leaves the instance ─────────────────────────────────────────────

    [Fact]
    public async Task TheRequestCarriesNothingAboutTheInstance()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(ReleasesJson);
        var source = Source(handler);

        await source.ListAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);

        // Section 28's promise in docs/privacy.md, asserted rather than described. The URL names the
        // repository and a page size and nothing else; the user agent is the software, not the build.
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.Equal("/repos/binn/Charter/releases", request.RequestUri.AbsolutePath);
        Assert.Equal("?per_page=30", request.RequestUri.Query);

        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(request.Headers, header =>
            header.Key.Contains("Cookie", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(GitHubReleaseSource.UserAgent, request.Headers.UserAgent.ToString());
        Assert.DoesNotContain(
            BuildVersion,
            request.Headers.UserAgent.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserAgentNamesTheSoftwareAndNeverTheBuild()
    {
        // A version in the user agent would tell GitHub which release each instance runs, which is
        // exactly the fact docs/privacy.md promises is never sent.
        Assert.Equal("Charter", GitHubReleaseSource.UserAgent);
    }

    // ── Reading the answer ───────────────────────────────────────────────────

    [Fact]
    public void DraftsAndUnparseableTagsAreDroppedFromTheList()
    {
        var releases = GitHubReleaseSource.Parse(ReleasesJson);

        Assert.Equal(
            new[] { "v0.6.0", "v0.7.0-rc.1" },
            releases.Select(release => release.Tag).ToArray());
        Assert.Equal("Ordinary release.", releases[0].Notes);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), releases[0].PublishedAt);
        Assert.True(releases[1].IsPrerelease);
    }

    [Fact]
    public void AResponseThatIsNotAReleaseListParsesToNothing()
    {
        Assert.Empty(GitHubReleaseSource.Parse("""{"message":"Not Found"}"""));
        Assert.Empty(GitHubReleaseSource.Parse("[]"));
    }

    [Theory]
    [InlineData("[SECURITY] v0.6.1", true)]
    [InlineData("[security] v0.6.1", true)]
    [InlineData("v0.6.1", false)]
    public void ASecurityReleaseIsRecognisedByItsMarker(string title, bool expected)
        => Assert.Equal(expected, Release(title: title).IsSecurity);

    [Fact]
    public void AReleaseWithSchemaMigrationsIsFlaggedFromTitleOrBody()
    {
        Assert.True(Release(title: "v0.6.1 [MIGRATIONS]").IncludesMigrations);
        Assert.True(Release(notes: "This upgrade applies [MIGRATIONS] - take a backup.").IncludesMigrations);
        Assert.False(Release().IncludesMigrations);
    }

    // ── The decision ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewerStableReleaseIsOffered()
    {
        var status = await CheckAsync("0.5.0", UpdateChannel.Stable, ReleasesJson);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("v0.6.0", status.LatestTag);
        Assert.Equal("0.6.0", status.LatestVersion);
        Assert.False(status.Security);
        Assert.Equal("stable", status.Channel);
        Assert.Equal("https://github.com/binn/Charter/releases/tag/v0.6.0", status.ReleaseUrl);
    }

    [Fact]
    public async Task TheStableChannelIsNeverOfferedAPrerelease()
    {
        // v0.7.0-rc.1 is the newest thing in the list and must not be what a stable instance is told
        // about, even though it is ahead of the running build.
        var status = await CheckAsync("0.6.0", UpdateChannel.Stable, ReleasesJson);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestTag);
    }

    [Fact]
    public async Task ThePrereleaseChannelIsOfferedOne()
    {
        var status = await CheckAsync("0.6.0", UpdateChannel.Prerelease, ReleasesJson);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("v0.7.0-rc.1", status.LatestTag);
        Assert.Equal("prerelease", status.Channel);
    }

    [Fact]
    public async Task AnInstanceAheadOfEveryReleaseIsToldNothing()
    {
        var status = await CheckAsync("9.0.0", UpdateChannel.Stable, ReleasesJson);

        Assert.False(status.UpdateAvailable);
        Assert.NotNull(status.CheckedAt);
    }

    [Fact]
    public async Task ABuildWhoseVersionIsNotAVersionComparesAgainstNothing()
    {
        // A source checkout, or a fork that overrode the build property. Announcing an update to every
        // such instance forever is the failure mode being avoided.
        var status = await CheckAsync("dev", UpdateChannel.Stable, ReleasesJson);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.CheckedAt);
    }

    // ── Offline ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnreachableGitHubIsNotAnErrorAndNotALogLine()
    {
        var logger = new RecordingLogger<GitHubReleaseSource>();
        var handler = new StubHttpMessageHandler().Enqueue(
            _ => throw new HttpRequestException("No such host is known."));

        var releases = await Source(handler, logger).ListAsync(TestContext.Current.CancellationToken);

        // Null, not empty: "could not look" is not "nothing newer".
        Assert.Null(releases);

        // Section 28: never log an error every day on an instance with no internet.
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task ARateLimitedInstanceIsAlsoSilent()
    {
        var logger = new RecordingLogger<GitHubReleaseSource>();
        var handler = new StubHttpMessageHandler().EnqueueError(
            HttpStatusCode.Forbidden,
            """{"message":"API rate limit exceeded"}""");

        Assert.Null(await Source(handler, logger).ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task AnInstanceWithNoNetworkAtAllIsSilentThroughTheRealHttpStack()
    {
        // The stubs above replace the message handler; this one replaces the socket. The client, the
        // handler pipeline, the timeout and the exception are all the real ones - the connection is
        // simply refused, which is what an air-gapped host does - so this is the closest a test gets
        // to unplugging the network.
        var entries = new List<(LogLevel Level, string Message)>();
        var config = CharterConfig.FromEnvironment(ConfigTestEnvironment.With(("CHARTER_UPDATE_CHECK", "true")));

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new CapturingLoggerProvider(entries)));
        services.AddCharterConfig(config);
        services.AddCharterData("Host=localhost;Port=5432;Database=charter;Username=charter;Password=unused");
        services.AddCharterUpdates(config);

        services.AddHttpClient(GitHubReleaseSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = (_, _) => throw new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.HostNotFound),
            });

        await using var provider = services.BuildServiceProvider();

        var releases = await provider.GetRequiredService<IReleaseSource>()
            .ListAsync(TestContext.Current.CancellationToken);

        Assert.Null(releases);

        // Section 28: an instance with no internet must not log an error every day.
        Assert.DoesNotContain(entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task DemoModesRefusalIsTreatedAsAnUnreachableNetwork()
    {
        // Nothing registers the source in demo mode, so this is defence in depth: were somebody to
        // wire it anyway, section 30.6's handler throws its own exception type and the check has to
        // degrade like any other network it cannot reach rather than failing the job.
        var logger = new RecordingLogger<GitHubReleaseSource>();
        var handler = new StubHttpMessageHandler().Enqueue(
            _ => throw Charter.Hosting.DemoModeException.For("GET to https://api.github.com"));

        Assert.Null(await Source(handler, logger).ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task AnOfflineCheckKeepsWhatWasAlreadyKnown()
    {
        var previous = UpdateStatus.Available(
            UpdateChannel.Stable,
            "0.5.0",
            new Release(
                "v0.6.0",
                ReleaseVersion.TryParse("v0.6.0")!,
                "[SECURITY] v0.6.0",
                "https://example.invalid/releases/v0.6.0",
                string.Empty,
                IsPrerelease: false,
                PublishedAt: null),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var status = Handler("0.5.0", UpdateChannel.Stable, new OfflineReleaseSource()).Evaluate(
            previous,
            releases: null,
            now: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));

        Assert.True(status.UpdateAvailable);
        Assert.True(status.Security);
        Assert.Equal("v0.6.0", status.LatestTag);

        // The timestamp records when Charter last learned something, not when it last tried. Moving it
        // would tell an air-gapped operator their instance was checked an hour ago.
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), status.CheckedAt);
    }

    [Fact]
    public void AnUnreachableCheckWithNothingKnownReportsNothingKnown()
    {
        var status = Handler("0.5.0", UpdateChannel.Stable, new OfflineReleaseSource()).Evaluate(
            previous: null,
            releases: null,
            now: DateTimeOffset.UnixEpoch);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.CheckedAt);
        Assert.Equal("0.5.0", status.CurrentVersion);
    }

    // ── The cached payload ───────────────────────────────────────────────────

    [Fact]
    public async Task TheStatusSurvivesARoundTripThroughTheJobPayload()
    {
        var status = await CheckAsync("0.5.0", UpdateChannel.Stable, ReleasesJson);
        var round = UpdateStatus.TryParse(status.ToJson());

        Assert.Equal(status, round);

        // jsonb, so it has to be an object rather than a bare value.
        using var document = JsonDocument.Parse(status.ToJson());
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void APayloadFromSomeOtherJobIsNotMistakenForAStatus()
    {
        Assert.Null(UpdateStatus.TryParse("""{"sessionId":"a2f0"}"""));
        Assert.Null(UpdateStatus.TryParse("not json"));
        Assert.Null(UpdateStatus.TryParse("{}"));
        Assert.Null(UpdateStatus.TryParse(null));
    }

    [Fact]
    public void ReleaseNotesAreStoredBounded()
    {
        var status = UpdateStatus.Available(
            UpdateChannel.Stable,
            "0.5.0",
            Release(notes: new string('n', UpdateStatus.MaxNotesLength * 2)),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(UpdateStatus.MaxNotesLength, status.Notes!.Length);
    }

    // ── Where it points ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/binn/Charter", "binn/Charter")]
    [InlineData("https://github.com/binn/Charter/", "binn/Charter")]
    [InlineData("https://github.com/binn/Charter.git", "binn/Charter")]
    [InlineData("https://gitlab.com/binn/Charter", null)]
    [InlineData("https://github.com/binn", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void TheRepositoryIsDerivedFromTheCompiledInSourceUrl(string? url, string? expected)
        => Assert.Equal(expected, UpdateCheckOptions.RepositoryFrom(url));

    [Fact]
    public async Task ABuildThatIsNotHostedOnGitHubChecksNothing()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(ReleasesJson);
        var source = new GitHubReleaseSource(
            new StubHttpClientFactory(handler),
            new UpdateCheckOptions { Repository = null },
            new RecordingLogger<GitHubReleaseSource>());

        Assert.Null(await source.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    // ── The schedule ─────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultScheduleIsDailyWithJitter()
    {
        var options = new UpdateCheckOptions();

        // Section 28: daily, with jitter, and additive so the interval is a floor - an instance never
        // asks more often than once a day however the jitter falls.
        Assert.Equal(TimeSpan.FromHours(24), options.Interval);
        Assert.True(options.Jitter > TimeSpan.Zero);
        Assert.True(options.StartupDelay > TimeSpan.Zero);
    }

    // ── Composition ──────────────────────────────────────────────────────────

    [Fact]
    public void TheCheckIsRegisteredWhenItIsOn()
    {
        using var provider = Provider(("CHARTER_UPDATE_CHECK", "true"));

        var handlers = provider.GetServices<Charter.Orchestration.IQueuedJobHandler>().ToArray();

        Assert.Contains(handlers, handler => handler is UpdateCheckJobHandler);
        Assert.IsType<GitHubReleaseSource>(provider.GetRequiredService<IReleaseSource>());
        Assert.IsType<UpdateStatusReader>(provider.GetRequiredService<IUpdateStatusReader>());
    }

    [Fact]
    public void TurningTheCheckOffLeavesNothingThatCanPhoneOut()
    {
        using var provider = Provider(("CHARTER_UPDATE_CHECK", "false"));

        // Not merely inert: the component that would make the call is never built.
        Assert.Null(provider.GetService<IReleaseSource>());
        Assert.Empty(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<UpdateCheckScheduler>());
        Assert.IsType<DisabledUpdateStatusReader>(provider.GetRequiredService<IUpdateStatusReader>());
    }

    [Fact]
    public void TurningTheCheckOffStillRegistersAHandlerToDrainTheQueue()
    {
        using var provider = Provider(("CHARTER_UPDATE_CHECK", "false"));

        // The dispatcher defers a job whose type has no handler and re-enqueues it every cycle, so an
        // instance that turned the check off would otherwise churn the row forever.
        var handler = Assert.Single(
            provider.GetServices<Charter.Orchestration.IQueuedJobHandler>(),
            candidate => candidate.Type == JobType.UpdateCheck);

        Assert.IsType<DisabledUpdateCheckJobHandler>(handler);
    }

    [Fact]
    public void DemoModeSuppressesTheOnlyOutboundCallCharterInitiates()
    {
        // Section 30.6: a demo instance contacts nobody. CHARTER_UPDATE_CHECK is left at its default
        // of true here, so this is the demo switch doing it and not the update switch.
        using var provider = Provider(("CHARTER_DEMO", "true"));

        Assert.Null(provider.GetService<IReleaseSource>());
        Assert.IsType<DisabledUpdateStatusReader>(provider.GetRequiredService<IUpdateStatusReader>());
    }

    [Fact]
    public async Task ADisabledInstanceReportsNothingEvenWithAStaleRowInTheQueue()
    {
        var status = await new DisabledUpdateStatusReader().ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildVersion => Charter.Diagnostics.BuildInfo.Version;

    private static Release Release(string title = "v0.6.1", string notes = "Ordinary release.")
        => new(
            "v0.6.1",
            ReleaseVersion.TryParse("v0.6.1")!,
            title,
            "https://github.com/binn/Charter/releases/tag/v0.6.1",
            notes,
            IsPrerelease: false,
            PublishedAt: null);

    private static GitHubReleaseSource Source(
        StubHttpMessageHandler handler,
        RecordingLogger<GitHubReleaseSource>? logger = null)
        => new(
            new StubHttpClientFactory(handler),
            new UpdateCheckOptions { Repository = "binn/Charter" },
            logger ?? new RecordingLogger<GitHubReleaseSource>());

    /// <summary>Runs one check end to end against a canned response, with no database in reach.</summary>
    private static async Task<UpdateStatus> CheckAsync(string current, UpdateChannel channel, string json)
    {
        var source = Source(new StubHttpMessageHandler().EnqueueJson(json));
        var releases = await source.ListAsync(TestContext.Current.CancellationToken);

        return Handler(current, channel, source).Evaluate(
            previous: null,
            releases,
            now: new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A handler whose queue and context point at a database that is never opened.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateCheckJobHandler.Evaluate"/> is pure - it decides what the instance now knows -
    /// so the whole decision matrix is a unit test. The persistence half is exercised against a real
    /// Postgres in <c>JobUpdateCheckTests</c>.
    /// </remarks>
    private static UpdateCheckJobHandler Handler(string current, UpdateChannel channel, IReleaseSource source)
    {
        var options = new DbContextOptionsBuilder<CharterDbContext>();
        DataServiceCollectionExtensions.ConfigureNpgsql(
            options,
            "Host=localhost;Port=5432;Database=charter;Username=charter;Password=unused");

        var db = new CharterDbContext(options.Options);

        return new UpdateCheckJobHandler(
            source,
            new UpdateCheckConfig { Enabled = true, Channel = channel },
            new UpdateCheckOptions { CurrentVersion = current, Repository = "binn/Charter" },
            new JobQueue(db),
            db,
            CharterTime.System,
            new RecordingLogger<UpdateCheckJobHandler>());
    }

    private static ServiceProvider Provider(params (string Key, string? Value)[] overrides)
    {
        var config = CharterConfig.FromEnvironment(ConfigTestEnvironment.With(overrides));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharterConfig(config);

        // A connection string that points nowhere. Nothing here opens it: the assertions are about
        // which registrations exist, and the ones that talk to Postgres are exercised in
        // JobUpdateCheckTests against a throwaway database.
        services.AddCharterData("Host=localhost;Port=5432;Database=charter;Username=charter;Password=unused");
        services.AddCharterUpdates(config);

        return services.BuildServiceProvider();
    }

    /// <summary>A source that cannot reach GitHub, which is the interesting half of section 28.</summary>
    private sealed class OfflineReleaseSource : IReleaseSource
    {
        public Task<IReadOnlyList<Release>?> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Release>?>(null);
    }

    /// <summary>Captures what the real logging pipeline was asked to write, with levels.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries;

        public CapturingLoggerProvider(List<(LogLevel Level, string Message)> entries) => _entries = entries;

        public ILogger CreateLogger(string categoryName) => new Capturing(_entries);

        public void Dispose()
        {
        }

        private sealed class Capturing : ILogger
        {
            private readonly List<(LogLevel Level, string Message)> _entries;

            public Capturing(List<(LogLevel Level, string Message)> entries) => _entries = entries;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
