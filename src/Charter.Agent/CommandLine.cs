using System.Globalization;
using Charter.Agent.Pairing;

namespace Charter.Agent;

/// <summary>Result of parsing the agent's command line.</summary>
public sealed record CommandLineResult(AgentOptions? Options, IReadOnlyList<string> Problems)
{
    public bool Ok => Options is not null && Problems.Count == 0;
}

/// <summary>
/// Hand-written argument parsing for <c>charter-agent</c>.
/// </summary>
/// <remarks>
/// Deliberately dependency-free: section 33.7 wants a single static binary installable with one
/// command, and the argument surface here is small enough that a parser package would be the larger
/// half of it.
/// <para>
/// Every problem is collected and reported together. An operator writing a systemd unit or a launchd
/// plist edits it, restarts, and finds the next mistake - so fixing one flag per attempt is a poor
/// trade for the twenty lines it saves here.
/// </para>
/// </remarks>
public static class CommandLine
{
    public const string Usage = """
        charter-agent - Charter execution-plane daemon (spec section 33)

        The agent dials out to the control plane over a WebSocket and claims work. It never listens
        for inbound connections, so it needs no port forwarding and works behind NAT and CGNAT.

        Usage:
          charter-agent --server <url> --token <pairing-token> [options]
          charter-agent --server <url> [options]        # once paired, the credential is reused

        Options:
          --server        Control-plane base URL, e.g. https://charter.example.com. Required.
          --token         Single-use pairing token from Settings -> Runners. Required on first run;
                          exchanged once for a long-lived agent credential, then spent.
          --mode          docker  spawn ephemeral containers via the local Docker socket (default)
                          native  run jobs directly on this host, under a dedicated unprivileged user
          --name          Label shown in the runners list. Defaults to the machine name.
          --concurrency   Maximum concurrently claimed jobs. Defaults to 1.
          --state-dir     Where the agent credential is stored. Defaults to ~/.charter-agent.
          --work-dir      Root of the per-job working directories. Defaults to <state-dir>/work.
          --native-user   Dedicated unprivileged account for native jobs. Defaults to
                          charter-runner. Pass 'self' to run jobs as the agent's own user, which is
                          weaker isolation.
          --docker-socket Local Docker socket path. Defaults to /var/run/docker.sock. It is never
                          exposed off this host.
          --reprobe-hours How often to re-probe host capabilities. Defaults to 24.
          --auto-update   Install a newer agent build when the control plane offers one. Off by
                          default: the default is to warn and let you upgrade deliberately.
          --verbose       Debug-level logging.
          --help, -h      Show this message.

        Capabilities are probed, never declared: dotnet --list-sdks, node --version,
        xcodebuild -version, USB enumeration, and so on, re-run on restart and daily.
        """;

    /// <summary>
    /// Parses the command line, collecting every problem.
    /// </summary>
    /// <param name="args">The raw arguments.</param>
    /// <param name="credentialAlreadyStored">
    /// True when this host already holds an agent credential for the target server, which makes
    /// <c>--token</c> optional. A pairing token is single-use, so requiring one on every restart
    /// would mean generating a fresh token every time the machine reboots.
    /// </param>
    public static CommandLineResult Parse(IReadOnlyList<string> args, bool credentialAlreadyStored = false)
    {
        ArgumentNullException.ThrowIfNull(args);

        var problems = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--server":
                case "--token":
                case "--mode":
                case "--name":
                case "--concurrency":
                case "--state-dir":
                case "--work-dir":
                case "--native-user":
                case "--docker-socket":
                case "--reprobe-hours":
                    var value = Next(args, ref i, argument, problems);
                    if (value is null)
                    {
                        break;
                    }

                    if (!values.TryAdd(argument, value))
                    {
                        problems.Add($"{argument} was given more than once");
                    }

                    break;

                case "--auto-update":
                case "--verbose":
                    flags.Add(argument);
                    break;

                default:
                    problems.Add($"unrecognised argument '{argument}'");
                    break;
            }
        }

        Uri? serverUri = null;
        if (!values.TryGetValue("--server", out var server) || string.IsNullOrWhiteSpace(server))
        {
            problems.Add("--server is required, e.g. --server https://charter.example.com");
        }
        else if (!Uri.TryCreate(server, UriKind.Absolute, out serverUri) ||
                 serverUri.Scheme is not ("http" or "https"))
        {
            problems.Add($"--server must be an absolute http or https URL, got '{server}'");
            serverUri = null;
        }

        values.TryGetValue("--token", out var token);
        if (string.IsNullOrWhiteSpace(token) && !credentialAlreadyStored)
        {
            problems.Add(
                "--token is required until this agent has paired. Generate a pairing token in " +
                "Settings -> Runners.");
        }

        var executionMode = AgentExecutionMode.Docker;
        if (values.TryGetValue("--mode", out var mode))
        {
            switch (mode.ToLowerInvariant())
            {
                case "docker":
                    executionMode = AgentExecutionMode.Docker;
                    break;
                case "native":
                    executionMode = AgentExecutionMode.Native;
                    break;
                default:
                    problems.Add($"--mode must be docker or native, got '{mode}'");
                    break;
            }
        }

        var concurrency = 1;
        if (values.TryGetValue("--concurrency", out var rawConcurrency) &&
            (!int.TryParse(rawConcurrency, NumberStyles.Integer, CultureInfo.InvariantCulture, out concurrency) ||
             concurrency < 1 || concurrency > 64))
        {
            problems.Add($"--concurrency must be an integer between 1 and 64, got '{rawConcurrency}'");
            concurrency = 1;
        }

        var reprobeHours = 24.0;
        if (values.TryGetValue("--reprobe-hours", out var rawReprobe) &&
            (!double.TryParse(rawReprobe, NumberStyles.Float, CultureInfo.InvariantCulture, out reprobeHours) ||
             reprobeHours <= 0 || reprobeHours > 168))
        {
            problems.Add($"--reprobe-hours must be a positive number of hours up to 168, got '{rawReprobe}'");
            reprobeHours = 24;
        }

        var stateDirectory = values.TryGetValue("--state-dir", out var rawStateDir) && rawStateDir.Length > 0
            ? Path.GetFullPath(rawStateDir)
            : AgentCredentialStore.DefaultStateDirectory();

        var workDirectory = values.TryGetValue("--work-dir", out var rawWorkDir) && rawWorkDir.Length > 0
            ? Path.GetFullPath(rawWorkDir)
            : Path.Combine(stateDirectory, "work");

        var nativeUser = values.TryGetValue("--native-user", out var rawUser) && rawUser.Length > 0
            ? rawUser
            : "charter-runner";

        if (values.ContainsKey("--native-user") && executionMode != AgentExecutionMode.Native)
        {
            problems.Add("--native-user only applies to --mode native");
        }

        var name = values.TryGetValue("--name", out var rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName.Trim()
            : Environment.MachineName;

        if (name.Length > 100)
        {
            problems.Add("--name must be 100 characters or fewer");
        }

        if (problems.Count > 0 || serverUri is null)
        {
            return new CommandLineResult(null, problems);
        }

        return new CommandLineResult(
            new AgentOptions
            {
                Server = serverUri,
                Token = string.IsNullOrWhiteSpace(token) ? null : token,
                Mode = executionMode,
                Concurrency = concurrency,
                Name = name,
                StateDirectory = stateDirectory,
                WorkDirectory = workDirectory,
                NativeUser = nativeUser,
                DockerSocket = values.TryGetValue("--docker-socket", out var socket) && socket.Length > 0
                    ? socket
                    : "/var/run/docker.sock",
                ReprobeInterval = TimeSpan.FromHours(reprobeHours),
                AutoUpdate = flags.Contains("--auto-update"),
                Verbose = flags.Contains("--verbose"),
            },
            problems);
    }

    private static string? Next(IReadOnlyList<string> args, ref int index, string flag, List<string> problems)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            problems.Add($"{flag} expects a value");
            return null;
        }

        index++;
        return args[index];
    }
}
