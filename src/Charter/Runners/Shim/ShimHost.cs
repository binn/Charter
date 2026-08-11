using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Charter.Runners.Shim;

/// <summary>
/// The command line of <c>charter-runner-shim run</c>, exactly as the shipped workflow invokes it.
/// </summary>
/// <remarks>
/// Parsed into a record rather than read from the environment so that
/// <c>.github/workflows/agent-session.yml</c>, <c>DockerRunner</c> and <c>Charter.Agent</c> all drive
/// the shim through one documented surface. <c>RunnerShimTests</c> pins the flag names against the
/// workflow file.
/// </remarks>
public sealed record ShimCommandLine
{
    public string? SessionId { get; init; }

    public string? Adapter { get; init; }

    public string? Model { get; init; }

    public string? Repo { get; init; }

    public string? BaseBranch { get; init; }

    public string? BaseCommit { get; init; }

    public string? SpecUrl { get; init; }

    public string? CallbackUrl { get; init; }

    /// <summary>The repository checkout. Defaults to the working directory, as in a workflow job.</summary>
    public string? Workspace { get; init; }

    /// <summary>Present as <c>--stream-events</c>; absent means buffer and post at the end.</summary>
    public bool StreamEvents { get; init; }

    /// <summary>Capabilities the session declared it needs (section 27.3). Repeatable.</summary>
    public IReadOnlyList<string> Require { get; init; } = [];

    /// <summary>Section 16.2: off unless this repository opted in, explicitly.</summary>
    public bool AllowInstallScripts { get; init; }

    /// <summary>The image in use, for the section 32.1 message when a toolchain is missing.</summary>
    public string? RunnerImage { get; init; }

    /// <summary>Where the session's work is published. Defaults to the branch convention.</summary>
    public string? Branch { get; init; }

    /// <summary>The remote the session branch is pushed to.</summary>
    public string? Remote { get; init; }

    /// <summary>Where to clone from when the workspace is not already a checkout.</summary>
    public string? CloneUrl { get; init; }

    /// <summary>The requester's display name, for the commit's authorship (sections 7.3, 24).</summary>
    public string? RequesterName { get; init; }

    /// <summary>The requester's address, for the commit's authorship.</summary>
    public string? RequesterEmail { get; init; }

    /// <summary>Present as <c>--no-publish</c>; leaves the work uncommitted in the workspace.</summary>
    public bool NoPublish { get; init; }

    /// <summary>Parses the argument vector. Unknown flags are reported, never ignored.</summary>
    public static ShimCommandLine Parse(IReadOnlyList<string> arguments, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(problems);

        var line = new ShimCommandLine();
        var require = new List<string>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            string? Value()
            {
                if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return arguments[++index];
                }

                problems.Add($"{argument} needs a value.");
                return null;
            }

            switch (argument)
            {
                case "run":
                    break;
                case "--session-id": line = line with { SessionId = Value() }; break;
                case "--adapter": line = line with { Adapter = Value() }; break;
                case "--model": line = line with { Model = Value() }; break;
                case "--repo": line = line with { Repo = Value() }; break;
                case "--base-branch": line = line with { BaseBranch = Value() }; break;
                case "--base-commit": line = line with { BaseCommit = Value() }; break;
                case "--spec-url": line = line with { SpecUrl = Value() }; break;
                case "--callback-url": line = line with { CallbackUrl = Value() }; break;
                case "--workspace": line = line with { Workspace = Value() }; break;
                case "--runner-image": line = line with { RunnerImage = Value() }; break;
                case "--branch": line = line with { Branch = Value() }; break;
                case "--remote": line = line with { Remote = Value() }; break;
                case "--clone-url": line = line with { CloneUrl = Value() }; break;
                case "--requester-name": line = line with { RequesterName = Value() }; break;
                case "--requester-email": line = line with { RequesterEmail = Value() }; break;
                case "--require": if (Value() is { } capability) { require.Add(capability); } break;
                case "--stream-events": line = line with { StreamEvents = true }; break;
                case "--allow-install-scripts": line = line with { AllowInstallScripts = true }; break;
                case "--no-publish": line = line with { NoPublish = true }; break;
                default:
                    problems.Add($"'{argument}' is not a flag charter-runner-shim understands.");
                    break;
            }
        }

        line = line with { Require = require };

        if (!Guid.TryParse(line.SessionId, out _))
        {
            problems.Add("--session-id must be the session's GUID.");
        }

        foreach (var (name, value) in new[]
                 {
                     ("--adapter", line.Adapter),
                     ("--model", line.Model),
                     ("--spec-url", line.SpecUrl),
                     ("--callback-url", line.CallbackUrl),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{name} is required.");
            }
        }

        return line;
    }

    /// <summary>Projects the parsed command line onto a run request.</summary>
    public ShimRunRequest ToRunRequest(
        IReadOnlyList<string> allowPaths,
        IReadOnlyList<string> denyPaths,
        IReadOnlyList<string> probedCapabilities,
        IReadOnlyDictionary<string, string> agentEnvironment)
        => new()
        {
            SessionId = Guid.Parse(SessionId!),
            WorkspaceRoot = Workspace ?? Directory.GetCurrentDirectory(),
            AdapterId = Adapter!,
            Model = Model!,
            SpecUrl = new Uri(SpecUrl!),
            AllowPaths = allowPaths,
            DenyPaths = denyPaths,
            RequiredCapabilities = Require,
            ProbedCapabilities = probedCapabilities,
            RunnerImage = RunnerImage,
            AllowInstallScripts = AllowInstallScripts,
            Branch = Branch,
            Remote = string.IsNullOrWhiteSpace(Remote) ? "origin" : Remote,
            CloneUrl = CloneUrl,
            BaseCommit = BaseCommit,
            Requester = ShimCommitIdentity.TryCreate(RequesterName, RequesterEmail),
            Publish = !NoPublish,
            AgentEnvironment = agentEnvironment,
        };
}

/// <summary>
/// Reads <c>CHARTER_PATH_SCOPE</c>, the JSON object the workflow passes as <c>toJSON(path_scope)</c>.
/// </summary>
/// <remarks>
/// The scope arrives on the dispatch and is enforced here, in the sandbox (section 7.3). A payload
/// that cannot be read is not treated as "no restrictions": an unreadable scope denies everything, so
/// the failure mode of a malformed dispatch is a session that writes nothing rather than a session
/// that writes anywhere.
/// </remarks>
public static class ShimPathScopeEnvironment
{
    public const string Variable = "CHARTER_PATH_SCOPE";

    public static (IReadOnlyList<string> Allow, IReadOnlyList<string> Deny) Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "null" or "{}")
        {
            return ([], []);
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return ([], ["**"]);
            }

            return (ReadArray(root, "allow"), ReadArray(root, "deny"));
        }
        catch (JsonException)
        {
            return ([], ["**"]);
        }
    }

    private static IReadOnlyList<string> ReadArray(JsonObject root, string name)
    {
        if (root[name] is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>();

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                values.Add(text);
            }
        }

        return values;
    }
}

/// <summary>
/// Probes what this host actually has (section 32.2).
/// </summary>
/// <remarks>
/// Probed, never assumed: a Mac mini that picked up an Xcode update must stop advertising the old
/// version, and the only way to know is to ask. A probe that fails is a capability this host does not
/// have, which is the safe reading — it makes the session stop with the section 32.1 message rather
/// than proceed and fail later in a way that looks like an agent error.
/// </remarks>
public sealed class ShimCapabilityProbe
{
    private static readonly (string Capability, string Command, string[] Arguments)[] Probes =
    [
        ("dotnet", "dotnet", ["--version"]),
        ("node", "node", ["--version"]),
        ("python", "python3", ["--version"]),
        ("uv", "uv", ["--version"]),
        ("git", "git", ["--version"]),
        ("xcode", "xcodebuild", ["-version"]),
    ];

    private readonly IShimProcessRunner _processes;

    public ShimCapabilityProbe(IShimProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _processes = processes;
    }

    public async Task<IReadOnlyList<string>> ProbeAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var found = new List<string> { PlatformCapability() };

        foreach (var (capability, command, arguments) in Probes)
        {
            var output = new StringBuilder();

            try
            {
                var result = await _processes.RunAsync(
                    new ShimProcessRequest(command, arguments, workingDirectory),
                    (line, _) => { output.AppendLine(line); return ValueTask.CompletedTask; },
                    cancellationToken);

                if (result.ExitCode == 0)
                {
                    found.Add(Format(capability, output.ToString()));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not installed. Not an error: it is an answer.
            }
        }

        return found;
    }

    /// <summary>Turns <c>10.0.100</c> or <c>v22.11.0</c> into <c>dotnet:10.0.100</c>.</summary>
    internal static string Format(string capability, string output)
    {
        var version = new string([.. output.Where(character => char.IsDigit(character) || character == '.')])
            .Trim('.');

        var firstLine = version.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? capability : $"{capability}:{firstLine}";
    }

    private static string PlatformCapability()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return OperatingSystem.IsWindows() ? "windows" : "linux";
    }
}

/// <summary>Starts real processes, streaming standard output line by line as it arrives.</summary>
public sealed class ProcessShimRunner : IShimProcessRunner
{
    /// <inheritdoc />
    public async Task<ShimProcessResult> RunAsync(
        ShimProcessRequest request,
        Func<string, CancellationToken, ValueTask>? onStandardOutputLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            UseShellExecute = false,
        };

        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Section 7.4: the runner sees no control-plane environment. The agent process gets the
        // credential variables its adapter declared, and nothing that was not put there on purpose.
        if (request.Environment is { Count: > 0 })
        {
            foreach (var (name, value) in request.Environment)
            {
                info.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        var errors = new StringBuilder();

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                errors.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            return new ShimProcessResult(-1, $"Could not start '{request.FileName}'.");
        }

        process.BeginErrorReadLine();

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (onStandardOutputLine is not null)
                {
                    await onStandardOutputLine(line, cancellationToken);
                }
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Section 11: cancelling has to actually kill the runner, not merely stop reading it.
            TryKill(process);
            throw;
        }

        return new ShimProcessResult(
            process.ExitCode,
            errors.Length == 0 ? null : errors.ToString().Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
    }
}

/// <summary>Posts events and the terminal result back to the control plane over HTTP.</summary>
public sealed class HttpShimEventSink : IShimEventSink
{
    private readonly HttpClient _http;
    private readonly Uri _callbackUrl;
    private readonly Guid _sessionId;

    public HttpShimEventSink(HttpClient http, Uri callbackUrl, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(callbackUrl);

        _http = http;
        _callbackUrl = callbackUrl;
        _sessionId = sessionId;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(ShimOutboundEvent outboundEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboundEvent);

        var body = new JsonObject
        {
            ["session_id"] = _sessionId.ToString(),
            ["type"] = outboundEvent.Type,
            ["index"] = outboundEvent.Index,
            ["payload"] = JsonNode.Parse(outboundEvent.Payload),
        };

        using var response = await _http.PostAsync(
            new Uri($"{_callbackUrl.ToString().TrimEnd('/')}/events"),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

        // A dropped event must not end a session. The control plane can still reconstruct the run
        // from the terminal report, and a transcript with a gap beats a build that died posting it.
        _ = response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async ValueTask ReportResultAsync(ShimResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var body = new JsonObject
        {
            ["session_id"] = _sessionId.ToString(),
            ["state"] = result.Wire,
            ["message"] = result.Message,
        };

        using var response = await _http.PostAsync(
            new Uri($"{_callbackUrl.ToString().TrimEnd('/')}/result"),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

        _ = response.IsSuccessStatusCode;
    }
}

/// <summary>Fetches the approved spec from the control plane (section 16).</summary>
public sealed class HttpShimSpecSource : IShimSpecSource
{
    private readonly HttpClient _http;

    public HttpShimSpecSource(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public async ValueTask<string> LoadAsync(Uri specUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specUrl);
        return await _http.GetStringAsync(specUrl, cancellationToken);
    }
}

/// <summary>Builds the HTTP client the callbacks use, authenticated with the session's event token.</summary>
public static class ShimHttp
{
    /// <summary>The variable the workflow puts the exchanged event token in.</summary>
    public const string EventTokenVariable = "CHARTER_EVENT_TOKEN";

    public static HttpClient Create(string? eventToken)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!string.IsNullOrWhiteSpace(eventToken))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", eventToken);
        }

        return http;
    }
}
