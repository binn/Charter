using Charter.Agent;

// Charter Agent daemon (spec section 33).
//
// STATUS: stub. Argument parsing and the startup banner are real; pairing, capability probing,
// job claiming, leasing and heartbeats are Phase 2 work (section 23) and are not implemented here.
// Everything below prints what the finished daemon would do rather than pretending to do it.

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(CommandLine.Usage);
    return args.Length == 0 ? 1 : 0;
}

var parsed = CommandLine.Parse(args);
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

Console.WriteLine($"charter-agent (not yet implemented) would start as '{options.Name}':");
Console.WriteLine();
Console.WriteLine($"  control plane   {options.Server}");
Console.WriteLine($"  pairing token   {Redact(options.Token)}");
Console.WriteLine($"  execution mode  {options.Mode.ToString().ToLowerInvariant()}");
Console.WriteLine($"  concurrency     {options.Concurrency}");
Console.WriteLine();
Console.WriteLine("  Planned startup sequence (section 33.3):");
Console.WriteLine("   1. Dial out over wss to the control plane. No inbound port is opened, so this");
Console.WriteLine("      works behind NAT, CGNAT and corporate firewalls.");
Console.WriteLine("   2. Exchange the single-use pairing token for a long-lived agent credential.");
Console.WriteLine("   3. Probe and report capabilities (section 32.2) rather than being told what");
Console.WriteLine("      this host has: dotnet --list-sdks, node --version, xcodebuild -version,");
Console.WriteLine("      attached USB devices, plus mode, version and resource limits.");
Console.WriteLine("   4. Negotiate a protocol version; refuse to claim work on mismatch (section 33.6).");
Console.WriteLine("   5. Claim jobs filtered by capability, each under a lease renewed by heartbeat.");
Console.WriteLine("      The control plane never pushes; a crashed agent's jobs return to the queue.");
Console.WriteLine();

switch (options.Mode)
{
    case AgentExecutionMode.Docker:
        Console.WriteLine("  In docker mode each job runs in an ephemeral container spawned through the");
        Console.WriteLine("  local Docker socket. The socket never leaves this host.");
        break;

    case AgentExecutionMode.Native:
        Console.WriteLine("  In native mode each job runs directly on this host under a dedicated");
        Console.WriteLine("  unprivileged user with a scoped working directory. Isolation is");
        Console.WriteLine("  process-level, not container-level: use a dedicated machine or VM, not a");
        Console.WriteLine("  daily driver. Native mode exists because macOS with Xcode cannot be");
        Console.WriteLine("  containerised and USB-attached targets are awkward to pass through.");
        break;

    default:
        break;
}

return 0;

static string Redact(string token) =>
    token.Length <= 4 ? new string('*', token.Length) : string.Concat(token.AsSpan(0, 4), "...");
