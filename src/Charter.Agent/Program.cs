using System.Runtime.InteropServices;
using Charter.Agent;
using Charter.Agent.Capabilities;
using Charter.Agent.Execution;
using Charter.Agent.Logging;
using Charter.Agent.Pairing;
using Charter.Agent.Protocol;
using Charter.Agent.Transport;

// charter-agent - the companion daemon of spec section 33.
//
// It dials out to the control plane over a WebSocket, exchanges a single-use pairing token for a
// long-lived credential, probes what this host can actually do, and then claims work. Nothing
// listens on this machine and the local Docker socket never leaves it.

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(CommandLine.Usage);
    return args.Length == 0 ? 1 : 0;
}

// The pairing token is single-use, so a restarted agent presents the credential it already holds
// instead. Whether one exists decides whether --token is required, so the store is read first.
var stateDirectory = Prescan(args, "--state-dir") is { Length: > 0 } stateDirArgument
    ? Path.GetFullPath(stateDirArgument)
    : AgentCredentialStore.DefaultStateDirectory();

var store = new AgentCredentialStore(stateDirectory);
var storedCredential = Uri.TryCreate(Prescan(args, "--server"), UriKind.Absolute, out var prescannedServer)
    ? store.Load(prescannedServer)
    : null;

var parsed = CommandLine.Parse(args, credentialAlreadyStored: storedCredential is not null);
if (!parsed.Ok)
{
    await Console.Error.WriteLineAsync("charter-agent cannot start:");
    foreach (var problem in parsed.Problems)
    {
        await Console.Error.WriteLineAsync($"  - {problem}");
    }

    await Console.Error.WriteLineAsync();
    await Console.Error.WriteLineAsync(CommandLine.Usage);
    return 1;
}

var options = parsed.Options!;
var scrubber = new SecretScrubber();
scrubber.Register(options.Token);
var log = new ConsoleAgentLog(scrubber, options.Verbose ? LogLevel.Debug : LogLevel.Info);

log.Info($"charter-agent {AgentBuild.Version} ({AgentBuild.CommitSha[..Math.Min(7, AgentBuild.CommitSha.Length)]}), " +
    $"protocol {AgentProtocol.Version}");
log.Info($"control plane {options.Server} - outbound only; this host opens no inbound port");
log.Info($"mode {options.Mode.ToWire()}, concurrency {options.Concurrency}, name '{options.Name}'");

using var docker = options.Mode == AgentExecutionMode.Docker
    ? new DockerSocketClient(options.DockerSocket)
    : null;

var processRunner = new ProcessRunner();
IJobExecutor executor = options.Mode == AgentExecutionMode.Docker
    ? new DockerJobExecutor(options, docker!, log)
    : new NativeJobExecutor(options, log, processRunner);

log.Info("execution: " + executor.Describe());

// Section 33.2: isolation in native mode is weaker, and the daemon says so rather than leaving the
// operator to find out.
if (executor is NativeJobExecutor native)
{
    foreach (var warning in native.IsolationWarnings())
    {
        log.Warn(warning);
    }
}

using var lifetime = new CancellationTokenSource();
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

var preflight = await executor.PreflightAsync(lifetime.Token);
if (preflight.Count > 0)
{
    log.Error("this host is not ready to run jobs:");
    foreach (var problem in preflight)
    {
        log.Error("  - " + problem);
    }

    return 1;
}

// Section 32.2: probe, do not be told. Re-run on every restart and daily thereafter.
var prober = new CapabilityProber(processRunner);
var capabilities = await prober.ProbeAsync(options.Mode, DateTimeOffset.UtcNow, lifetime.Token);
log.Info($"probed {capabilities.Capabilities.Count} capabilities: {string.Join(", ", capabilities.Capabilities)}");
foreach (var report in capabilities.Reports.Where(r => r.Capabilities.Count == 0 && r.Note is not null))
{
    log.Debug($"probe {report.Name}: {report.Note}");
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(AgentBuild.UserAgent);

var credential = storedCredential;
if (credential is null)
{
    var pairing = new HttpPairingClient(httpClient);
    var outcome = await pairing.PairAsync(
        options.Server,
        new PairRequest
        {
            PairingToken = options.Token!,
            Name = options.Name,
            Mode = options.Mode.ToWire(),
            AgentVersion = AgentBuild.Version,
            ProtocolVersion = AgentProtocol.Version,
            Concurrency = options.Concurrency,
            Platform = AgentBuild.Platform(),
            Capabilities = capabilities.Capabilities,
        },
        DateTimeOffset.UtcNow,
        lifetime.Token);

    if (!outcome.Ok)
    {
        log.Error("pairing failed: " + outcome.Error);
        return 1;
    }

    credential = outcome.Credential!;
    store.Save(credential);
    log.Info($"paired as {credential.AgentId}; the pairing token is spent and the credential is at " +
        $"{store.CredentialPath} (owner-only)");
}
else
{
    log.Info($"using the stored credential for {credential.AgentId}");
}

var daemon = new AgentDaemon(
    options,
    credential.AgentToken,
    capabilities,
    new WebSocketTransportFactory(options.Server, AgentBuild.UserAgent),
    executor,
    prober,
    log,
    scrubber);

var reason = await daemon.RunAsync(lifetime.Token);
if (reason == AgentExitReason.Revoked)
{
    store.Clear();
    log.Error("this agent was revoked from the control plane. Pair again with a fresh token to return.");
    return 3;
}

log.Info("charter-agent stopped");
return 0;

void OnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    log.Info("shutting down; handing back any leases this agent holds");
    lifetime.Cancel();
}

// Reads a flag's value before the full parse, for the two settings that decide how to parse.
static string? Prescan(string[] arguments, string flag)
{
    var index = Array.IndexOf(arguments, flag);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
