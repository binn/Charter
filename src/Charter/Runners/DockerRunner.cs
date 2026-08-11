using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Charter.Domain;
using Charter.Runners.Shim;
using Charter.VersionControl;
using Microsoft.Extensions.Logging;

namespace Charter.Runners;

/// <summary>One container the Docker backend asks the daemon to create.</summary>
/// <remarks>
/// The Docker Engine API's own PascalCase, so this record is the request body verbatim rather than
/// something a mapper has to keep in step with it.
/// </remarks>
public sealed record DockerContainerSpec
{
    [JsonPropertyName("Image")]
    public required string Image { get; init; }

    [JsonPropertyName("Cmd")]
    public IReadOnlyList<string> Cmd { get; init; } = [];

    /// <summary><c>KEY=value</c>. The per-session secrets live here and nowhere else.</summary>
    [JsonPropertyName("Env")]
    public IReadOnlyList<string> Env { get; init; } = [];

    [JsonPropertyName("WorkingDir")]
    public string? WorkingDir { get; init; }

    /// <summary>How a restarted control plane finds a container it never started (section 2.3).</summary>
    [JsonPropertyName("Labels")]
    public IReadOnlyDictionary<string, string> Labels { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("HostConfig")]
    public DockerContainerHostConfig HostConfig { get; init; } = new();
}

/// <summary>The host-side half of a container Charter creates.</summary>
public sealed record DockerContainerHostConfig
{
    /// <summary>
    /// False on purpose: the container is removed once its exit code has been read, not before.
    /// </summary>
    [JsonPropertyName("AutoRemove")]
    public bool AutoRemove { get; init; }

    /// <summary>
    /// Named volumes for the per-repository caches and git mirror.
    /// </summary>
    /// <remarks>
    /// Section 32.3: scoping a cache to one repository is a security requirement, not an
    /// optimisation. A cache shared across repositories is a cross-repo contamination path — a
    /// poisoned transitive dependency pulled in one repository persists into the next.
    /// </remarks>
    [JsonPropertyName("Binds")]
    public IReadOnlyList<string> Binds { get; init; } = [];

    /// <summary>An init process, so a runaway toolchain leaves no zombies behind.</summary>
    [JsonPropertyName("Init")]
    public bool Init { get; init; } = true;
}

/// <summary>A container the daemon knows about.</summary>
public sealed record DockerContainerSummary(string Id, string State);

/// <summary>
/// The three secrets a session container holds, and the only three (sections 7.4, 33.5).
/// </summary>
/// <remarks>
/// A callback token scoped to one session, a version-control token scoped to one repository, and a
/// scoped model credential — never a refresh token, never the control plane's environment, never a
/// credential for another repository. <see cref="ToString"/> is overridden because the default record
/// printer would put a live secret into any interpolated string that touched one, including a log
/// line written by somebody who had no idea they were holding a credential.
/// </remarks>
public sealed record DockerSessionSecrets(string EventToken, string GitHubToken, string? ModelApiKey)
{
    /// <summary>The container environment these become, in the names the shim reads.</summary>
    public IReadOnlyList<string> Environment()
    {
        var entries = new List<string>(3)
        {
            $"{ShimHttp.EventTokenVariable}={EventToken}",
            $"GITHUB_TOKEN={GitHubToken}",
        };

        if (!string.IsNullOrWhiteSpace(ModelApiKey))
        {
            entries.Add($"CHARTER_MODEL_API_KEY={ModelApiKey}");
        }

        return entries;
    }

    public override string ToString() => "DockerSessionSecrets { redacted }";
}

/// <summary>
/// The Docker Engine calls this backend makes, behind a seam.
/// </summary>
/// <remarks>
/// Declared here rather than taken as a concrete client for the same reason
/// <see cref="IGitHubRepositoryDispatcher"/> is: the routing, idempotency and cancellation rules are
/// what can be wrong, and they must be assertable without a Docker daemon on the machine running the
/// tests.
/// </remarks>
public interface IDockerEngine
{
    /// <summary><c>GET /_ping</c>. False when the daemon is unreachable or refuses this process.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary><c>GET /containers/json?filters=...</c>, by label.</summary>
    Task<IReadOnlyList<DockerContainerSummary>> ListByLabelAsync(
        string label,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary><c>POST /containers/create</c> then <c>POST /containers/{id}/start</c>.</summary>
    Task<string> RunAsync(DockerContainerSpec spec, CancellationToken cancellationToken = default);

    /// <summary><c>POST /containers/{id}/kill</c>. False when it had already stopped.</summary>
    Task<bool> KillAsync(string containerId, CancellationToken cancellationToken = default);
}

/// <summary>What the Docker backend advertises and how it names things.</summary>
public sealed record DockerRunnerOptions
{
    /// <summary>The label carrying the session id, so a container is findable without any memory.</summary>
    public const string SessionLabel = "com.charter.session";

    /// <summary>The label carrying the dispatch key, which is what makes dispatch idempotent.</summary>
    public const string DispatchLabel = "com.charter.dispatch";

    /// <summary>The image used when the repository's <c>.charter/config.yml</c> names none.</summary>
    public const string DefaultRunnerImage = "ghcr.io/binn/charter-runner-fullstack:1";

    /// <summary>Where <c>DOCKER_HOST</c> points by default on Linux and macOS.</summary>
    public const string DefaultSocketPath = "/var/run/docker.sock";

    /// <summary>
    /// What a container on this host can offer.
    /// </summary>
    /// <remarks>
    /// Reported rather than probed, and that is a real limitation of this backend rather than an
    /// oversight: section 32.2's probe asks the machine that will run the work, and here that machine
    /// is a container that does not exist until a session is dispatched to it. The Charter Agent
    /// probes properly because it is the host. An operator whose image carries more than this says so
    /// in configuration.
    /// </remarks>
    public IReadOnlyList<string> Capabilities { get; init; } = ["linux", "docker", "dotnet:10", "node:22"];

    public string SocketPath { get; init; } = DefaultSocketPath;

    public string DefaultImage { get; init; } = DefaultRunnerImage;

    /// <summary>The shim's entrypoint inside the runner image (section 32.1).</summary>
    public string ShimExecutable { get; init; } = "charter-runner-shim";

    /// <summary>The workspace the shim runs in, inside the container.</summary>
    public string WorkingDirectory { get; init; } = "/workspace";

    /// <summary>Wall-clock cap (section 27.5).</summary>
    public int DefaultTimeoutMinutes { get; init; } = 60;
}

/// <summary>
/// The Compose self-host backend of section 2.2: sibling containers on the host's own Docker socket.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Holding the Docker socket is root-equivalent access to the host</strong>, and section 2.2
/// says so in as many words. That is why this is not the default and never runs on a PaaS: Railway
/// and comparable platforms prohibit privileged containers and block daemon access outright
/// (section 2.1). It exists for the operator who runs Charter and Docker on one machine they own,
/// and <c>docs/runners.md</c> documents the trade rather than glossing it.
/// </para>
/// <para>
/// Like every backend, this one only <em>dispatches</em>. It holds no session state — it cannot, since
/// the container can restart mid-session (section 2.3) — so both idempotency and cancellation are
/// resolved by asking the daemon what is labelled with this dispatch rather than by remembering what
/// was started. A control plane that restarts between creating a container and recording the fact
/// finds the container again by its label and refuses to start a second one.
/// </para>
/// </remarks>
public sealed class DockerRunner : IAgentRunner
{
    private readonly IDockerEngine _docker;
    private readonly DockerRunnerOptions _options;
    private readonly ILogger<DockerRunner> _logger;
    private readonly RunnerSessionTokens? _tokens;
    private readonly IRunnerCredentialBroker? _credentials;

    /// <param name="docker">The Engine API, over the host's unix socket.</param>
    /// <param name="options">What this backend advertises and how it names things.</param>
    /// <param name="logger">Structured, correlated on the session id (section 19).</param>
    /// <param name="tokens">
    /// Mints the per-session callback token. Optional so the dispatch contract can be asserted without
    /// a signing key; a runner without it starts containers whose events would be rejected, which is
    /// why the composition root always supplies it.
    /// </param>
    /// <param name="credentials">
    /// Mints the short-TTL, single-repository token and the scoped model credential (sections 7.4,
    /// 33.5). There is no equivalent of the workflow's credential-exchange step here — the container
    /// has no repository secret to exchange — so the control plane mints at dispatch instead. Nothing
    /// long-lived is ever written into a container definition either way.
    /// </param>
    public DockerRunner(
        IDockerEngine docker,
        DockerRunnerOptions options,
        ILogger<DockerRunner> logger,
        RunnerSessionTokens? tokens = null,
        IRunnerCredentialBroker? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _docker = docker;
        _options = options;
        _logger = logger;
        _tokens = tokens;
        _credentials = credentials;
    }

    /// <inheritdoc />
    public RunnerKind Kind => RunnerKind.Docker;

    /// <inheritdoc />
    /// <remarks>
    /// The daemon is pinged on every describe rather than at startup. A socket that has gone away —
    /// Docker Desktop quit, the daemon restarted, the user was dropped from the docker group — must
    /// stop routing immediately, and section 27.3 would rather tell an operator "Docker is not
    /// answering" than leave sessions queueing against a backend that cannot run them.
    /// </remarks>
    public async ValueTask<RunnerDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
        => new(
            RunnerKind.Docker,
            $"Docker ({_options.SocketPath})",
            RunnerCapability.ExpandAll(_options.Capabilities),
            await _docker.PingAsync(cancellationToken));

    /// <inheritdoc />
    public async Task<RunnerDispatchResult> DispatchAsync(
        RunnerDispatch dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        var existing = await _docker.ListByLabelAsync(
            DockerRunnerOptions.DispatchLabel,
            dispatch.DispatchKey,
            cancellationToken);

        if (existing.Count > 0)
        {
            // Started already, by this process or by the one that restarted. Not an error and not a
            // reason to start a second container.
            return RunnerDispatchResult.Ok(existing[0].Id);
        }

        try
        {
            var secrets = await MintAsync(dispatch, cancellationToken);
            var containerId = await _docker.RunAsync(BuildSpec(dispatch, _options, secrets), cancellationToken);

            _logger.LogInformation(
                "Started container {ContainerId} for session {SessionId} on {Repo} at {BaseCommitSha}",
                containerId,
                dispatch.SessionId,
                dispatch.RepoFullName,
                dispatch.BaseCommitSha);

            return RunnerDispatchResult.Ok(containerId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Section 27.3: a refusal queues the session with an explanation. It never fails it, and
            // it never surfaces the exception text to a requester.
            _logger.LogWarning(
                exception,
                "Docker refused to start a container for session {SessionId}",
                dispatch.SessionId);

            return RunnerDispatchResult.Refused(
                $"Docker could not start a session container from '{Image(dispatch, _options)}': "
                + $"{exception.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<RunnerCancelResult> CancelAsync(
        RunnerCancellation cancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        // The session label, always, and never the external reference on its own. The reference is a
        // container id, but it is folded from the session's events and session_started arrives from
        // the execution plane — so acting on it unverified is a `docker kill` on any container id a
        // sandbox cared to name, on the operator's own host. The label is written by this runner at
        // dispatch and is the only statement about which container belongs to which session that the
        // sandbox never touched (sections 16, 33.2).
        var labelled = await _docker.ListByLabelAsync(
            DockerRunnerOptions.SessionLabel,
            cancellation.SessionId.ToString("D"),
            cancellationToken);

        // A control plane that restarted between starting the container and recording it has no
        // reference to offer, which is why the label is the primary lookup rather than the fallback.
        IReadOnlyList<DockerContainerSummary> containers =
            cancellation.ExternalReference is { Length: > 0 } reference
                ? [.. labelled.Where(container => string.Equals(container.Id, reference, StringComparison.Ordinal))]
                : labelled;

        // A reference that matches no container of this session is not a reason to fall back to the
        // whole label set — it is a reason to stop, because the two disagree about what is running.
        if (containers.Count == 0 && cancellation.ExternalReference is { Length: > 0 })
        {
            return RunnerCancelResult.NothingToStop(
                "The container recorded for this session is not one of this session's containers, so "
                + "Charter will not kill it. The session is settled here; check `docker ps` for a "
                + "container still running against this session.");
        }

        if (containers.Count == 0)
        {
            return RunnerCancelResult.NothingToStop(
                "No session container is running for this session, so there is nothing to stop here. "
                + "The session is settled in Charter.");
        }

        var stopped = false;

        foreach (var container in containers)
        {
            stopped |= await _docker.KillAsync(container.Id, cancellationToken);
        }

        return stopped
            ? RunnerCancelResult.Confirmed
            : RunnerCancelResult.NothingToStop("The session container had already stopped.");
    }

    /// <summary>
    /// The per-session secrets this container will hold, minted moments before it starts.
    /// </summary>
    /// <remarks>
    /// Never persisted, and never written into the queue row: a credential in <c>jobs.payload</c> would
    /// sit in Postgres for as long as the backlog does, readable by every backup and every replica
    /// (section 33.5). Failing to mint is not fatal here — the refusal message from the broker reaches
    /// the operator through the dispatch result, which is more useful than a container that starts and
    /// cannot say why it stopped.
    /// </remarks>
    private async Task<DockerSessionSecrets?> MintAsync(
        RunnerDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (_tokens is null || _credentials is null)
        {
            return null;
        }

        var issued = await _credentials.IssueAsync(
            dispatch.SessionId,
            dispatch.RepoFullName,
            cancellationToken);

        return new DockerSessionSecrets(
            _tokens.IssueEventToken(dispatch.SessionId),
            issued.GitHubToken,
            issued.ModelApiKey);
    }

    /// <summary>Builds the container. Separated so the contract can be asserted without a daemon.</summary>
    public static DockerContainerSpec BuildSpec(
        RunnerDispatch dispatch,
        DockerRunnerOptions options,
        DockerSessionSecrets? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(options);

        var branch = string.IsNullOrWhiteSpace(dispatch.Branch)
            ? ChangeRequestPublisher.BranchFor(dispatch.SessionId)
            : dispatch.Branch;

        var arguments = new List<string>
        {
            options.ShimExecutable,
            "run",
            "--session-id", dispatch.SessionId.ToString("D"),
            "--adapter", dispatch.AdapterId,
            "--model", dispatch.Model,
            "--repo", dispatch.RepoFullName,
            "--base-branch", dispatch.BaseBranch,
            "--base-commit", dispatch.BaseCommitSha,
            "--spec-url", dispatch.SpecUrl.ToString(),
            "--callback-url", dispatch.CallbackUrl.ToString().TrimEnd('/'),
            "--workspace", options.WorkingDirectory,
            "--branch", branch,

            // The container starts empty, so the shim clones for itself. The other two backends hand
            // it a checkout and ignore this.
            "--clone-url", $"https://github.com/{dispatch.RepoFullName}.git",
            "--stream-events",
        };

        if (dispatch.Requester is { } requester)
        {
            arguments.AddRange(
                ["--requester-name", requester.DisplayName, "--requester-email", requester.Email]);
        }

        foreach (var capability in dispatch.RequiredCapabilities)
        {
            arguments.AddRange(["--require", capability]);
        }

        var image = Image(dispatch, options);
        arguments.AddRange(["--runner-image", image]);

        // Section 32.3: one cache set per repository, and never one shared between them.
        var scope = CacheScope(dispatch.RepoFullName);

        return new DockerContainerSpec
        {
            Image = image,
            Cmd = arguments,
            WorkingDir = options.WorkingDirectory,
            Env =
            [
                $"CHARTER_SESSION_ID={dispatch.SessionId:D}",
                $"{ShimPathScopeEnvironment.Variable}={PathScopeJson(dispatch.PathScope)}",
                .. secrets?.Environment() ?? [],
            ],
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DockerRunnerOptions.SessionLabel] = dispatch.SessionId.ToString("D"),
                [DockerRunnerOptions.DispatchLabel] = dispatch.DispatchKey,
            },
            HostConfig = new DockerContainerHostConfig
            {
                Binds =
                [
                    $"charter-nuget-{scope}:/root/.nuget/packages",
                    $"charter-npm-{scope}:/root/.npm",
                    $"charter-git-{scope}:/var/cache/charter/git",
                ],
            },
        };
    }

    /// <summary>A volume-name-safe form of <c>owner/name</c>.</summary>
    internal static string CacheScope(string repoFullName)
    {
        ArgumentNullException.ThrowIfNull(repoFullName);

        var scope = new string([
            .. repoFullName.ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-'),
        ]).Trim('-');

        return scope.Length == 0 ? "unnamed" : scope;
    }

    private static string Image(RunnerDispatch dispatch, DockerRunnerOptions options)
        => string.IsNullOrWhiteSpace(dispatch.RunnerImage) ? options.DefaultImage : dispatch.RunnerImage;

    private static string PathScopeJson(RunnerPathScope scope)
    {
        var allow = new JsonArray();
        foreach (var pattern in scope.Allow)
        {
            allow.Add(pattern);
        }

        var deny = new JsonArray();
        foreach (var pattern in scope.Deny)
        {
            deny.Add(pattern);
        }

        return new JsonObject { ["allow"] = allow, ["deny"] = deny }.ToJsonString();
    }
}

/// <summary>
/// The Docker Engine API over the host's unix socket.
/// </summary>
/// <remarks>
/// A unix socket and not TCP, ever. A network-reachable Docker daemon is root-equivalent access to
/// the machine and a permanent target, even behind mTLS; the socket at least cannot be reached from
/// off the host.
/// </remarks>
public sealed class DockerSocketEngine : IDockerEngine, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public DockerSocketEngine(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);

        SocketPath = socketPath.StartsWith("unix://", StringComparison.Ordinal)
            ? socketPath["unix://".Length..]
            : socketPath;

        var path = SocketPath;

        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            // Meaningless over a unix socket, and ignored by the daemon.
            BaseAddress = new Uri("http://localhost/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public string SocketPath { get; }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri("/_ping", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DockerContainerSummary>> ListByLabelAsync(
        string label,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var filters = new JsonObject
        {
            ["label"] = new JsonArray($"{label}={value}"),
        }.ToJsonString();

        var url = $"/containers/json?all=true&filters={Uri.EscapeDataString(filters)}";

        try
        {
            using var response = await _http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var listed = await response.Content.ReadFromJsonAsync<JsonArray>(Json, cancellationToken);

            return listed is null
                ? []
                :
                [
                    .. listed
                        .OfType<JsonObject>()
                        .Select(row => new DockerContainerSummary(
                            row["Id"]?.GetValue<string>() ?? string.Empty,
                            row["State"]?.GetValue<string>() ?? "unknown"))
                        .Where(row => row.Id.Length > 0),
                ];
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException or JsonException)
        {
            // An unreachable daemon answers "nothing running", which makes dispatch try and fail
            // loudly rather than skip on the belief that a container it cannot see is already there.
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<string> RunAsync(DockerContainerSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        using var created = await _http.PostAsJsonAsync(
            new Uri("/containers/create", UriKind.Relative),
            spec,
            Json,
            cancellationToken);

        if (!created.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the daemon answered {(int)created.StatusCode} to a container create: "
                + await created.Content.ReadAsStringAsync(cancellationToken));
        }

        var body = await created.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken);
        var id = body?["Id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("the daemon created a container but named no id.");
        }

        using var started = await _http.PostAsync(
            new Uri($"/containers/{id}/start", UriKind.Relative),
            content: null,
            cancellationToken);

        if (!started.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"the daemon answered {(int)started.StatusCode} starting container {id}: "
                + await started.Content.ReadAsStringAsync(cancellationToken));
        }

        return id;
    }

    /// <inheritdoc />
    public async Task<bool> KillAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            using var response = await _http.PostAsync(
                new Uri($"/containers/{containerId}/kill", UriKind.Relative),
                content: null,
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The engine used when the socket named by <c>CHARTER_DOCKER_SOCKET</c> does not exist.
/// </summary>
/// <remarks>
/// Section 4.1's fail-loud rule, applied to a dependency rather than to a variable. The alternative —
/// registering the backend anyway — is exactly the failure this replaced: <c>CHARTER_RUNNER=docker</c>
/// that validates, registers nothing, and queues every session forever with no error anywhere.
/// Describing itself as offline means section 27.3's routing says so in words, on the session.
/// </remarks>
public sealed class UnreachableDockerEngine : IDockerEngine
{
    private readonly string _socketPath;

    public UnreachableDockerEngine(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = socketPath;
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<DockerContainerSummary>> ListByLabelAsync(
        string label,
        string value,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DockerContainerSummary>>([]);

    /// <inheritdoc />
    public Task<string> RunAsync(DockerContainerSpec spec, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            $"CHARTER_RUNNER includes 'docker' but there is no Docker socket at '{_socketPath}'. Start "
            + "Docker, point CHARTER_DOCKER_SOCKET at the right path, or set CHARTER_RUNNER to "
            + "'agent' and pair a Charter Agent instead.");

    /// <inheritdoc />
    public Task<bool> KillAsync(string containerId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>How the Docker backend is wired from the environment.</summary>
public static class DockerRunnerEnvironment
{
    /// <summary>Overrides the socket path. Documented in <c>docs/runners.md</c>.</summary>
    public const string SocketVariable = "CHARTER_DOCKER_SOCKET";

    /// <summary>The socket this instance would use.</summary>
    public static string SocketPath(string? configured = null)
    {
        var value = configured ?? Environment.GetEnvironmentVariable(SocketVariable);

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.StartsWith("unix://", StringComparison.Ordinal) ? value["unix://".Length..] : value;
        }

        // DOCKER_HOST is the ecosystem's own variable, and an operator who set it for the CLI expects
        // it to be honoured here too. Only its unix form: see DockerSocketEngine on why not TCP.
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        return dockerHost is { Length: > 0 } host && host.StartsWith("unix://", StringComparison.Ordinal)
            ? host["unix://".Length..]
            : DockerRunnerOptions.DefaultSocketPath;
    }

    /// <summary>True when there is a socket to talk to.</summary>
    public static bool SocketExists(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        return File.Exists(socketPath) || Directory.Exists(socketPath);
    }

    /// <summary>
    /// The engine for this host: the real one where a socket exists, and one that refuses with the
    /// path it looked at where none does.
    /// </summary>
    public static IDockerEngine Resolve(string socketPath)
        => SocketExists(socketPath) ? new DockerSocketEngine(socketPath) : new UnreachableDockerEngine(socketPath);
}
