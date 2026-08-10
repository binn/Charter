using System.Globalization;

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
/// command, and the argument surface here is four flags.
/// </remarks>
public static class CommandLine
{
    public const string Usage = """
        charter-agent - Charter execution-plane daemon (spec section 33)

        Usage:
          charter-agent --server <url> --token <pairing-token> [--mode docker|native]
                        [--name <label>] [--concurrency <n>]

        Options:
          --server       Control-plane base URL, e.g. https://charter.example.com. Required.
                         The agent dials out to this; it never listens for inbound connections.
          --token        Single-use pairing token generated in Settings -> Runners. Required.
                         Exchanged once for a long-lived agent credential, then discarded.
          --mode         docker  spawn ephemeral containers via the local Docker socket (default)
                         native  run jobs directly on the host, under a dedicated unprivileged user
          --name         Label shown in the runners list. Defaults to the machine name.
          --concurrency  Maximum concurrently claimed jobs. Defaults to 1.
          --help, -h     Show this message.
        """;

    public static CommandLineResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var problems = new List<string>();
        string? server = null;
        string? token = null;
        string? mode = null;
        string? name = null;
        string? concurrency = null;

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--server":
                    server = Next(args, ref i, argument, problems);
                    break;
                case "--token":
                    token = Next(args, ref i, argument, problems);
                    break;
                case "--mode":
                    mode = Next(args, ref i, argument, problems);
                    break;
                case "--name":
                    name = Next(args, ref i, argument, problems);
                    break;
                case "--concurrency":
                    concurrency = Next(args, ref i, argument, problems);
                    break;
                default:
                    problems.Add($"unrecognised argument '{argument}'");
                    break;
            }
        }

        Uri? serverUri = null;
        if (string.IsNullOrWhiteSpace(server))
        {
            problems.Add("--server is required, e.g. --server https://charter.example.com");
        }
        else if (!Uri.TryCreate(server, UriKind.Absolute, out serverUri) ||
                 serverUri.Scheme is not ("http" or "https"))
        {
            problems.Add($"--server must be an absolute http or https URL, got '{server}'");
            serverUri = null;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add("--token is required. Generate a pairing token in Settings -> Runners.");
        }

        var executionMode = AgentExecutionMode.Docker;
        if (mode is not null)
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

        var parallelism = 1;
        if (concurrency is not null &&
            (!int.TryParse(concurrency, NumberStyles.Integer, CultureInfo.InvariantCulture, out parallelism) ||
             parallelism < 1))
        {
            problems.Add($"--concurrency must be a positive integer, got '{concurrency}'");
            parallelism = 1;
        }

        if (problems.Count > 0 || serverUri is null || token is null)
        {
            return new CommandLineResult(null, problems);
        }

        return new CommandLineResult(
            new AgentOptions
            {
                Server = serverUri,
                Token = token,
                Mode = executionMode,
                Concurrency = parallelism,
                Name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name,
            },
            problems);
    }

    private static string? Next(IReadOnlyList<string> args, ref int index, string flag, List<string> problems)
    {
        if (index + 1 >= args.Count)
        {
            problems.Add($"{flag} expects a value");
            return null;
        }

        index++;
        return args[index];
    }
}
