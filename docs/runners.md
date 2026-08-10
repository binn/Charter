---
title: "Runners"
description: "The three execution backends - Charter Agent, GitHub Actions, and Docker - plus capability matching, agent registration, runner images and caching, and honest performance numbers."
---

# Runners

A runner is where agent sessions actually execute. Charter's control plane never runs agent code
itself — it dispatches to a runner behind the `IAgentRunner` interface.

Choose a backend with `CHARTER_RUNNER`:

```bash
CHARTER_RUNNER=agent
```

Enable several at once by comma-separating them, and the dispatcher routes each session to whichever
backend can actually run it:

```bash
CHARTER_RUNNER=agent,github-actions
```

## The three backends

| Backend | Value | How it works |
|---|---|---|
| **Charter Agent** | `agent` | A companion daemon on your own host. It connects **outbound** to the control plane and claims jobs. |
| **GitHub Actions** | `github-actions` | A `repository_dispatch` event triggers a workflow in the target repository; events stream back to a Charter webhook. |
| **Docker** | `docker` | The control plane uses a local Docker socket to spawn sibling containers. |

### Charter Agent — the primary backend

Use this unless you have a reason not to. It is required for anything that is not a plain Linux web
project, and it is the fastest backend for everything else.

- **No inbound ports.** The agent dials out over a WebSocket. It works behind NAT, CGNAT, and
  corporate firewalls with no port forwarding and no firewall changes.
- **The Docker socket never leaves the host.** The control plane needs no privileges on your execution
  host and no knowledge of its network.
- **Toolchains and caches persist.** A long-lived machine keeps its SDKs installed, its package caches
  warm, and a bare git mirror per repository on local disk. Sessions fetch and create a worktree rather
  than cloning from scratch.
- **It is the only way to reach hardware.** A physical STM32 on a USB port, a GNSS receiver under a
  live sky view, a Unity licence you have paid for, or a Mac with Xcode — none of these can come from
  a hosted backend.

### GitHub Actions — the zero-infrastructure default

The default when you have no machine to spare, and the reason Charter works on Railway, Render, and Fly
at all. It needs nothing from you beyond the workflow file in the target repository.

**It is the slowest backend, by a wide margin, and this does not go away.** Every run gets a fresh VM:
no warm caches, no persistent toolchains, no git mirror. Mitigations reduce the cost but do not remove
it:

- Run the job inside a prebuilt Charter runner image using the workflow's `container:` key, so the
  session never installs a language runtime.
- Use `actions/cache` keyed on lockfile hashes for package restores.

Accept that sessions take minutes longer here than on a Charter Agent, and budget wall-clock
expectations accordingly. If build latency is the thing your users complain about, this is the fix.

### Docker — the Compose case

For a VPS where the control plane and Docker share a host. The image is warm and caches live in named
volumes, so it sits between the other two on speed.

**Mounting the Docker socket into the Charter container grants that container root-equivalent access to
the host.** Treat the machine as dedicated to Charter.

Charter also supports pointing `DockerRunner` at a remote Docker daemon over TCP.
**Do not do this.** A network-reachable Docker API is root-equivalent access to that host and a
permanent target, and mTLS does not change what an attacker gets once they are through it. The support
exists for completeness. If you need to run jobs on a different machine, run a Charter Agent there
instead — it gives you the same thing with an outbound connection and no exposed daemon.

## Which project types need which backend

Not every project can be verified by clicking a URL, and the runner requirements follow from that
([spec §27.2](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)):

| Project type | Runner capabilities required | Charter Agent required? |
|---|---|---|
| `web` | `linux` | No |
| `api` | `linux` | No |
| `library` | `linux` | No |
| `game_server` | `linux` | No |
| `mobile_expo` | `linux` or `macos` | Not strictly |
| `mobile_ios` | `macos`, `xcode` | **Yes** |
| `desktop_mac` | `macos`, `signing` | **Yes** |
| `desktop_win` | `windows` | **Yes** in practice |
| `maui` | `windows`, `macos` | **Yes** in practice |
| `unity` | `linux`, `unity_license`, `gpu` | **Yes** |
| `embedded` | `linux`, `toolchain`, `usb_device` | **Yes** |

GitHub Actions does offer hosted macOS and Windows runners, so `desktop_win` and `mobile_ios` are not
categorically impossible there. What it cannot offer is a licence you own, a GPU you have configured,
or a device on a USB port. **Anything hardware-attached or licence-bound needs a runner on a machine
you control.**

Charter ships no licences and never installs entitlements. Unity licences, Apple developer
certificates, notarisation credentials, Android keystores, and private registry tokens are provisioned
by you, locally on the agent host. The agent may trigger a signed build; it can never read the signing
material.

## Capability advertisement and matching

Runners are not told what they have. They **probe and report** at registration, and again on restart
and daily:

```
dotnet --list-sdks     -> "dotnet:10.0.100"
node --version         -> "node:22.11.0"
xcodebuild -version    -> "xcode:16.2"
probe-rs list          -> "usb_device:stm32f4"
```

Charter stores the resulting capability set. Sessions declare what they require, and the dispatcher
matches:

```
Runner advertises: ["linux", "docker", "dotnet:10", "node:22"]
Session requires:  ["macos", "xcode:16"]
-> queued: "No runner available with macOS and Xcode. Register one in Settings -> Runners."
```

**A session with no eligible runner queues with that explanation rather than failing.** It starts as
soon as a matching runner comes online.

Daily re-probing matters more than it sounds. A Mac mini that took an Xcode update overnight must not
keep advertising the old version, or sessions get dispatched to a runner that cannot build them.

## Registering a Charter Agent

The agent is a single static binary, published to GitHub Releases for linux/amd64, linux/arm64,
darwin/arm64, and windows/amd64, plus a container image for Docker mode.

1. **Generate a pairing token.** In Charter, go to Settings -> Runners -> Add runner. The token is
   single-use and short-lived.

2. **Run the agent on your execution host**, pointing it at your instance:

   ```bash
   charter-agent --server https://charter.example.com --token pair_9fK2mQx7RvT4bN1s --mode docker
   ```

   The agent dials out, exchanges the pairing token for a long-lived agent credential, registers
   itself, and probes its capabilities. The pairing token is spent at that point.

   **`--token` is only needed on the first run.** The credential is written to the state directory
   with owner-only permissions and reused on restart, so your service definition does not have to
   carry a secret. It is bound to the server URL: pointing the same host at a different instance
   forces a re-pair.

3. **Confirm it appears online** in Settings -> Runners, with its mode, version, advertised
   capabilities, and concurrency limit.

### Options

| Option | Default | What it does |
|---|---|---|
| `--server` | required | Control-plane base URL |
| `--token` | first run only | Single-use pairing token |
| `--mode` | `docker` | `docker` or `native` |
| `--name` | machine name | Label shown in the runners list |
| `--concurrency` | `1` | Maximum concurrently claimed jobs |
| `--state-dir` | `~/.charter-agent` | Where the agent credential is stored |
| `--work-dir` | `<state-dir>/work` | Root of the per-job working directories |
| `--native-user` | `charter-runner` | Dedicated unprivileged account for native jobs |
| `--docker-socket` | `/var/run/docker.sock` | Local socket path; never exposed off this host |
| `--reprobe-hours` | `24` | How often to re-probe host capabilities |
| `--auto-update` | off | Install a newer build when the control plane offers one |
| `--verbose` | off | Debug-level logging |

Passing `--native-user self` runs jobs as the agent's own user. That is weaker isolation than a
dedicated account, and the agent says so at startup rather than leaving you to notice.

Invalid options are all reported together, followed by usage, rather than one per run.

The agent heartbeats on an interval. Missed heartbeats mark it offline, and its in-flight jobs return
to the queue after the lease expires. You can revoke it instantly from the UI — revocation kills
in-flight jobs and invalidates the credential.

### Execution modes

```bash
charter-agent --mode docker    # spawn ephemeral containers via the local socket
charter-agent --mode native    # run jobs directly on the host
```

`docker` is the default and the one to use where it is possible.

`native` exists because containers are not universally possible. **macOS with Xcode cannot be
containerised**, and USB-attached embedded targets are awkward to pass through into a container. In
native mode the agent runs jobs under a dedicated unprivileged user account with a scoped working
directory.

**Isolation in native mode is weaker than container mode, and you should plan around that.** It is
process-level isolation, not container-level: a session shares the host's filesystem outside its
working directory, its installed software, and its network position. The dedicated user account limits
the blast radius; it does not eliminate it.

Run native agents on a **dedicated machine or VM**, not on an engineer's daily driver. A laptop with
SSH keys, browser sessions, cloud CLI credentials, and company documents on it is the wrong host for a
process running agent-authored code.

### Version compatibility

The agent and the control plane negotiate a protocol version when they connect. A mismatch produces a
clear message and a refusal to claim work, rather than subtle failures three sessions later. The agent
auto-updates only if you opt in; the default is to warn and let you upgrade deliberately.

## How jobs reach a runner

The agent **claims** work; the control plane never pushes to it. That inversion is what makes
outbound-only connections possible.

- Claims carry a lease with a TTL, renewed by heartbeat. A crashed agent's jobs return to the queue
  automatically.
- Claims are filtered by capability, so an agent only ever sees jobs it can actually run.
- Concurrency is limited per agent and defaults conservatively.

## Runner images and caching

Sessions never install a language runtime. Toolchains are provisioned ahead of time in versioned base
images published to GHCR, and you can build your own from the Dockerfiles in `runners/`:

| Image | Contains |
|---|---|
| `charter-runner-base` | git, curl, jq, the agent CLIs, the event-streaming shim |
| `charter-runner-dotnet` | base plus .NET SDK 10 and a warm NuGet cache |
| `charter-runner-node` | base plus Node 22, npm and pnpm |
| `charter-runner-fullstack` | base plus .NET and Node |
| `charter-runner-python` | base plus uv and Python 3.12 |
| `charter-runner-embedded` | base plus arm-none-eabi, OpenOCD, probe-rs, udev rules |
| `charter-runner-unity` | base plus Unity Hub — the licence is yours to supply |

Select one per repository in `.charter/config.yml`:

```yaml
runner_image: ghcr.io/binn/charter-runner-fullstack:1
```

If the image is missing something the repository declares it needs, the session fails immediately with
an actionable message rather than quietly `apt-get`-ing its way to a working state. That is a security
control as much as a speed one — see [security.md](security.md).

Package caches (NuGet, npm, pnpm, Cargo, Gradle, Maven, Go) persist across sessions and are **scoped per
repository**. That scoping is a security requirement, not an optimisation: a cache shared between
repositories is a path for a poisoned transitive dependency pulled in one repo to persist into another.
Sandbox-org and production-org caches are never shared either.

Build-output caches (`obj/`, `bin/`, `node_modules`) are opt-in per repository:

```yaml
cache:
  build_output: true
```

They are off by default because stale intermediates produce failures that look exactly like agent
errors, and burn a review cycle before anyone suspects the cache.

Every session can be run as a **cold run** that ignores all caches. It is the first thing to try when a
session fails inexplicably, and the way to tell a real failure from a stale cache.

For dependencies no shared image can carry — a native library, a private feed, a codegen step —
declare a setup step:

```yaml
setup:
  run: "apt-get install -y libgpiod-dev && dotnet restore"
  cache_key: "packages.lock.json"
```

It runs once per distinct `cache_key` value and is skipped while the key is unchanged.

## Speed, honestly

| Backend | Warm state |
|---|---|
| Charter Agent | Best case. Long-lived machine, tools installed once, caches and git mirrors persist indefinitely. |
| Docker | Image is warm; caches and mirrors live in named volumes on the host. |
| GitHub Actions | Worst case. Fresh VM every run. Mitigable, not fixable. |

Build times for non-web projects run from minutes to an hour regardless of backend. Charter never shows
an ETA for this reason — only elapsed time. Budgets for these project types need a wall-clock cap
alongside the token cap.

## Related

- [configuration.md](configuration.md) — `CHARTER_RUNNER` and related variables
- [self-hosting.md](self-hosting.md) — which backend fits which platform
- [charter-folder.md](charter-folder.md) — `runner_image`, `setup`, and `cache` in `.charter/config.yml`
- [security.md](security.md) — what a runner receives and what it cannot see
- [spec §2.2, §27.3, §32, §33](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full runner specification
