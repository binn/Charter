// charter-runner-shim - the session execution shim of spec section 32.1.
//
// This runs INSIDE the session sandbox and is what all three backends invoke: the GitHub Actions
// workflow runs it in a workflow container, DockerRunner runs it in a sibling container, and
// Charter.Agent runs it on the operator's host (spec section 3.1). Path scope is enforced here, in
// one place, because a compromised session must not be able to widen it by talking to the UI
// (spec section 7.3).
//
// The order of what follows is the security model, not a convenience:
//
//   1. Verify the toolchain against what the image actually has. A session NEVER installs a language
//      runtime - if the image lacks a declared requirement it stops here with a message naming an
//      image that has it (spec sections 16.1, 32.1).
//   2. Install dependencies lockfile-only, with install scripts disabled unless the repository has
//      opted in explicitly: npm ci --ignore-scripts, dotnet restore --locked-mode (spec section 16.2).
//   3. Run the agent CLI per the adapter YAML, classify its output with the adapter's own event map,
//      and stream each event to the control plane as it happens - a five to twenty minute silent gap
//      reads as broken (spec sections 11, 12b).
//   4. Refuse, and stop the run, on any write outside the path scope.

using Charter.Adapters;
using Charter.Runners.Shim;

var problems = new List<string>();
var command = ShimCommandLine.Parse(args, problems);

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("charter-runner-shim run --session-id <guid> --adapter <id> --model <model> \\");
    Console.WriteLine("    --repo <owner/name> --base-branch <branch> --base-commit <sha> \\");
    Console.WriteLine("    --spec-url <url> --callback-url <url> [--workspace <dir>] [--stream-events]");
    Console.WriteLine("    [--require <capability>]... [--runner-image <image>] [--allow-install-scripts]");
    Console.WriteLine();
    Console.WriteLine("Reads CHARTER_PATH_SCOPE (JSON) for the path scope and CHARTER_EVENT_TOKEN for the");
    Console.WriteLine("bearer token minted by the control plane's credential exchange.");
    return args.Length == 0 ? 2 : 0;
}

if (problems.Count > 0)
{
    await Console.Error.WriteLineAsync("charter-runner-shim cannot start:");
    foreach (var problem in problems)
    {
        await Console.Error.WriteLineAsync($"  - {problem}");
    }

    return 2;
}

using var cancellation = new CancellationTokenSource();

// Section 11: the cancel button must actually kill the runner. A backend cancelling the job signals
// the process, and that has to unwind into killing the agent CLI rather than orphaning it.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var workspace = command.Workspace ?? Directory.GetCurrentDirectory();
var (allow, deny) = ShimPathScopeEnvironment.Read(
    Environment.GetEnvironmentVariable(ShimPathScopeEnvironment.Variable));

using var http = ShimHttp.Create(Environment.GetEnvironmentVariable(ShimHttp.EventTokenVariable));

var processes = new ProcessShimRunner();
var sink = new HttpShimEventSink(http, new Uri(command.CallbackUrl!), Guid.Parse(command.SessionId!));

IAdapterCatalog catalog;
try
{
    catalog = AdapterCatalog.Load(AdapterSources.FromEnvironment());
}
catch (AdapterLoadException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    await sink.ReportResultAsync(
        new ShimResult(ShimSessionState.Failed, exception.Message),
        CancellationToken.None);
    return 3;
}

// Section 32.2: probe, do not assume. Re-probed every run, so a host that picked up a toolchain
// update stops claiming the version it had yesterday.
var probed = await new ShimCapabilityProbe(processes).ProbeAsync(workspace, cancellation.Token);

var request = command.ToRunRequest(
    allow,
    deny,
    probed,
    AgentEnvironment(catalog, command.Adapter!));

var session = new ShimSession(catalog, processes, sink, new HttpShimSpecSource(http));
var result = await session.RunAsync(request, cancellation.Token);

Console.WriteLine($"charter-runner-shim: {result.State}{(result.Message is null ? string.Empty : $" - {result.Message}")}");

return result.State switch
{
    ShimSessionState.Completed => 0,
    ShimSessionState.Cancelled => 130,
    ShimSessionState.ToolchainMissing => 4,
    ShimSessionState.ScopeViolation => 5,
    ShimSessionState.InstallFailed => 6,
    _ => 1,
};

// Section 7.4: the agent process receives the credential variables its adapter declares, populated
// from what the credential exchange returned, and nothing else from this process's environment.
static Dictionary<string, string> AgentEnvironment(IAdapterCatalog catalog, string adapterId)
{
    var environment = new Dictionary<string, string>(StringComparer.Ordinal);

    if (!catalog.TryGet(adapterId, out var adapter))
    {
        return environment;
    }

    var modelKey = Environment.GetEnvironmentVariable("CHARTER_MODEL_API_KEY");

    foreach (var binding in adapter.Auth)
    {
        // The exchange returns one scoped model credential; the adapter says which variable the CLI
        // reads it from. Anything already set in this process wins, so an operator-provisioned
        // credential on a detached runner is not clobbered (section 32.8).
        var existing = Environment.GetEnvironmentVariable(binding.EnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(existing))
        {
            environment[binding.EnvironmentVariable] = existing;
        }
        else if (!string.IsNullOrWhiteSpace(modelKey))
        {
            environment[binding.EnvironmentVariable] = modelKey;
        }
    }

    if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } githubToken)
    {
        environment["GITHUB_TOKEN"] = githubToken;
    }

    return environment;
}
