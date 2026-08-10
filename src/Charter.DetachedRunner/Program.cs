// Charter detached runner host (spec sections 2.2, 27.3, 32.5).
//
// STATUS: stub. Nothing here executes a session yet.
//
// The detached runner is the execution-plane host that runs on a machine the operator controls.
// Section 27.3 moves it from "nice to have" to mandatory: GitHub Actions offers macOS and Windows
// runners, but it cannot offer a physical STM32 on a USB port, a GNSS receiver under a live sky
// view, or a Unity licence someone has paid for. Section 32.5 adds the second argument - it is also
// the fastest backend, because toolchains, package caches and bare git mirrors persist on local
// disk between sessions.
//
// It shares the outbound job-claim protocol with Charter.Agent (section 33.4): work is claimed, not
// pushed, under a lease renewed by heartbeat, filtered by advertised capability.

Console.WriteLine("charter-detached-runner (not yet implemented)");
Console.WriteLine();
Console.WriteLine("The detached runner host is scheduled for Phase 2 (spec section 23). When built it will:");
Console.WriteLine();
Console.WriteLine("  - probe this host's capabilities and advertise them to the control plane");
Console.WriteLine("    (section 32.2), re-probing on restart and daily so a machine that picked up an");
Console.WriteLine("    Xcode update stops advertising the old version;");
Console.WriteLine("  - claim only jobs whose required capabilities it can satisfy (section 27.3);");
Console.WriteLine("  - maintain a bare git mirror per repository and create worktrees rather than");
Console.WriteLine("    cloning from scratch (section 32.4);");
Console.WriteLine("  - keep package caches scoped per repository - a shared cache is a cross-repo");
Console.WriteLine("    contamination path, so this is a security requirement, not an optimisation");
Console.WriteLine("    (section 32.3);");
Console.WriteLine("  - receive a short-TTL, single-repo GitHub installation token and a scoped model");
Console.WriteLine("    credential per job, and never the control plane's environment (section 33.5).");
Console.WriteLine();
Console.WriteLine("Register a runner from Settings -> Runners in the Charter UI.");

return 0;
