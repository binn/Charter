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

Note that `charter-agent --mode docker` is a different thing: that is the Charter Agent spawning
containers through the socket on *its* host, which is still the recommended way to get containerised
execution on a machine that is not the one running the control plane.

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

**The workflow file is part of the trust boundary, so keep it current.** Its first step exchanges two
things for the session's credentials: `secrets.CHARTER_SESSION_SECRET`, which Charter writes once per
repository, and `client_payload.session_token`, which Charter mints per session and sends only in that
session's dispatch. Both are required. The repository secret proves the caller is a workflow run in
this repository — every run in it reads the same value — and the session token is what says *which*
session is asking; without it, any run in the repository could mint credentials for every other live
session in it. If you are running a workflow file from before this was added, its first step now fails
with a message telling you to update it. See [upgrading.md](upgrading.md).

### Docker — the Compose case

For a VPS where the control plane and Docker share a host. The image is warm and caches live in named
volumes, so it sits between the other two on speed.

**Mounting the Docker socket into the Charter container grants that container root-equivalent access to
the host.** Treat the machine as dedicated to Charter.

Charter looks for the socket at `/var/run/docker.sock`. Set `CHARTER_DOCKER_SOCKET` to override it, or
set `DOCKER_HOST` to a `unix://` URL and Charter will honour that instead.

**Only a unix socket.** Charter will not talk to a Docker daemon over TCP, even with mTLS: a
network-reachable Docker API is root-equivalent access to that host and a permanent target. If you need
to run jobs on a different machine, run a Charter Agent there — it gives you the same thing with an
outbound connection and no exposed daemon.

Two limitations worth knowing before you choose this backend:

- **Capabilities are declared, not probed.** [Spec §32.2](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)
  has runners probe and report what they have, and this backend cannot: the machine that will run the
  work is a container that does not exist until a session is dispatched to it. It advertises
  `linux, docker, dotnet:10, node:22` — what the default `charter-runner-fullstack` image carries. A
  Charter Agent probes properly.
- **No socket means no runner, and Charter says so.** An instance configured for `docker` on a host
  with no reachable daemon registers a backend that describes itself as offline, so a session that
  cannot be routed is explained on the request rather than left in a queue nobody is watching.

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

### Registering from the UI

Settings -> Runners is the whole flow:

1. **Generate a pairing token.** Single-use and short-lived. Charter shows the exact
   `charter-agent --server ... --token ...` command with your instance's URL already filled in.
2. **Run it on the execution host.** The token is shown once and cannot be shown again; generate
   another if you lose it.
3. **Watch it appear.** The agent probes its own capabilities and reports them, so the list shows
   what the host actually has rather than what you told Charter it has.

The capability list is the useful part of that screen. Pick a queued session and every agent's
capabilities are re-rendered in that session's terms - one row per requirement, showing which
capability satisfies it or that nothing does. That answers the question you actually have when a
session is not running, which is *why is nothing picking this up*.

**Revoking is immediate and kills in-flight jobs.** The screen says so before you confirm. A revoked
credential cannot be reinstated; pair the host again.

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
- **A lease left out of a heartbeat acknowledgement is a lease that is gone**, and the agent stops
  that job at once rather than waiting for its TTL to run down. That is how the control plane tells an
  agent to stop work whose claim it has lost — to a sweep, to a cancellation, or to another worker —
  and it is what keeps two runners off one session when a lease changes hands.
- **A job already running on an agent is never started twice there.** If the same job is granted again
  — clock skew between the two sides is enough to produce it — the agent keeps the copy it is running,
  extends its lease, and starts nothing new. Two shims for one session on one host would both push the
  same branch, and only one of them would be reachable by a cancel.
- **Credentials are minted at the moment of the claim, and only for a live session.** An agent
  claiming work whose session has ended, has been cancelled, or was never dispatched is given no
  credentials and no job; the queue row is settled rather than handed back, so dead work is not
  re-offered on the next claim.
- Claims are filtered by capability, so an agent only ever sees jobs it can actually run.
- **An agent only ever claims work addressed to a runner.** Charter runs a good deal of work on the
  control plane itself — refining a request, writing the engineer recap, the daily release check,
  onboarding a repository. That work is queued alongside runner jobs, and it requires no capabilities
  at all, so capability filtering on its own would not keep it away from an idle agent. Runner jobs
  carry a routing marker and agents claim only rows that carry it. Nothing you configure changes this,
  and no capability you advertise can widen it: an agent that advertises everything still claims only
  runner jobs.
- Concurrency is limited per agent and defaults conservatively.

## What a runner tells Charter, and what Charter believes

A runner reports back over three callbacks — `/credentials`, `/events`, `/result` — and it holds a
token for all three. Everything in those bodies is written by a process that also runs a coding agent
over repository content nobody vetted, so Charter treats the token as saying *which session is
speaking* and nothing at all about whether what it says is true.

That matters most for **`run_url`**, the one field a runner reports that Charter later uses to address
something: it is how the workflow tells Charter which run to cancel. Charter validates it against the
repository the session belongs to, read from its own record, and refuses the callback if the two do
not match.

- On GitHub Actions the workflow sends `run_url` twice — on the credential exchange and again as the
  `session_started` event — and both are checked. A mismatch at the exchange also means **no
  credentials are issued**, so the step fails loudly with a message rather than running on with a
  reference Charter will not use.
- The check compares `owner/name` case-insensitively and does not care which host serves it, so GitHub
  Enterprise works unchanged.
- **If you rename a repository on GitHub without reconnecting it in Charter, sessions in it will fail
  at their first callback.** The refusal names the repository Charter has on record. Reconnect the
  repository so the two agree; there is no setting to relax the check.
- Anything that is not an absolute `http(s)` run URL is refused, including a bare container id or an
  internal `charter-agent:job:…` handle. Those are handles Charter mints for itself, and a runner has
  no business reporting one.

The other field Charter would otherwise act on is **the branch in `branch_pushed`**. A runner reports
where it pushed, and Charter moves that ref to open the pull request — so Charter publishes only the
branch it named itself.

- The session's branch is `charter/session-<session id, hex, no dashes>`. Every backend computes it
  the same way, from the session id, so a runner never has to be told it.
- A `branch_pushed` naming anything else is refused, and **nothing is published**: no ref is moved, no
  branch is created, no pull request is opened. The session ends as failed with the reason on its
  transcript, and the refusal is logged at warning level with the branch that was reported.
- Fast-forward-only is not a substitute for this. An agent's commit sits on top of your base branch in
  the ordinary case, so a believed report could advance `main` — the ref your branch protection exists
  to hold — without anything being merged or reviewed.
- Reporting no branch at all is still fine. A backend that only knows how to `git push` leaves the
  field out, and the convention applies.

**If you write your own runner, push to `charter/session-<id>` and nowhere else.** There is no setting
that relaxes this, and a runner that pushes elsewhere will have its sessions refused even though the
work is sitting on the provider.

The same rule holds at the other end. Cancelling a session will not kill a container that does not
carry that session's label, and will not cancel an agent job whose payload names a different session
— whichever handle happens to be recorded against it. When a cancel cannot reach the run, it says so:
Charter settles the session either way, but it does not report a run stopped that it did not stop, and
the failure is logged at warning level with the reason. If you see one, check the repository's Actions
tab or `docker ps` for something still running.

## What a session does, in order

All three backends run the same program — `charter-runner-shim`, baked into the runner image. Only how
it is started differs: a workflow step, a sibling container, or a child process of `charter-agent`.

0. **Get a checkout**, if the backend did not already provide one. GitHub Actions runs
   `actions/checkout` at the base commit and the Charter Agent clones into the job's directory; a
   Docker container starts empty, so the shim clones for itself at the exact base commit.
1. **Verify the toolchain.** A session never installs a language runtime. If the image lacks something
   the session declared, it stops here with a message naming an image that has it.
2. **Install dependencies from lockfiles only** — `npm ci --ignore-scripts`, `dotnet restore
   --locked-mode`. Install scripts are off unless the repository opted in.
3. **Run the agent CLI** and stream every mapped event back as it happens.
4. **Refuse any write outside the path scope**, and stop the run.
5. **Run the repository's checks** — the named commands in `.charter/config.yml`, in the order they are
   declared, against the work the agent just did. Each one's outcome goes on the transcript and into
   the change request. Skipped when the agent changed nothing.
6. **Publish the work.** Everything changed is staged and every staged path is checked against the path
   scope again — this time against what is actually about to be committed, not against what the agent
   said it wrote. Then it commits, pushes the session branch, and reports the branch and revision. The
   control plane opens the change request from that report.

Two things about step 5 are worth stating plainly:

- **A failing check does not stop the push.** The change request opens, with the failure at the top of
  the body and on the transcript. Charter has no merge button — a red change request cannot ship,
  because the merge gate is branch protection and CODEOWNERS on your provider — so the useful thing
  Charter can do with a failure is put it in front of the engineer, along with the branch they need in
  order to fix or take over the work. Discarding a session because one test failed would burn
  everything it cost and leave nobody anything to read.
- **A check whose toolchain is missing stops the session before the agent starts.** If `.charter/`
  declares `dotnet build` and the runner image has no .NET SDK, the session fails immediately with a
  message naming an image that has one. It does not install .NET, and it does not spend a model's time
  producing work that could never have been validated.

Three things about step 6 are worth stating plainly:

- **The commit is authored by the requester.** The person who asked for the change is the author and
  the committer. Charter adds no machine account, no bot identity, and no attribution trailers; the
  commit message describes the change and nothing else. That Charter produced it is recorded on the
  change request, in words.
- **A session that changed nothing is not a failure.** It reports "no changes", the request ends as
  *Nothing needed changing*, and no change request is opened. A push that could not happen is a
  different outcome and fails the session loudly — the two are never conflated.
- **An out-of-scope path fails the session and commits nothing at all**, including the parts of the
  work that were in scope. A session that wrote where it was told it could not is not a session whose
  output should be reviewed piecemeal.

The push uses the short-TTL, single-repository token the runner exchanged its session secret for. On
GitHub Actions that token is the one `actions/checkout` persisted into the checkout, which is why
`GITHUB_TOKEN` needs no write permission in the shipped workflow.

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
