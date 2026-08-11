# Where Charter actually stands

Written 2026-08-11. This file exists so a new engineer — or a coding agent with no memory of how
the code got this way — can pick the work up cold without re-deriving it. `agent-docs/spec.md` says
what Charter *should* be; this says what it *is*, which is a different and shorter document.

Update it when the answer changes. A stale honest file is worse than no file.

## The one thing to know first

**The loop has never run against a real repository with a real model.**

Every green result below comes from stubs, fakes, a local Postgres, and a real git binary pushing to
a bare repo in a temp directory. That is enough to prove the wiring is connected and the events line
up. It is not enough to prove Charter works, and no amount of additional testing in this style will
be. The next genuinely informative thing anyone can do is point it at one real repository with one
real credential and watch where it falls over.

Treat the test count as a regression net, not as evidence. See *How the last defects were found*.

## Verified green as of the last commit

- `dotnet build Charter.sln --no-incremental` — 0 warnings, 0 errors (warnings-as-errors is on)
- `dotnet test Charter.sln` — 2821 passed, 0 failed, 0 skipped
- `npm run test` in `ClientApp` — 194 passed across 21 files; typecheck and build clean, one
  pre-existing lint warning
- `dotnet format Charter.sln --verify-no-changes` — clean
- A real boot with `CHARTER_DEMO=true`: both seeded accounts sign in, an engineer-only endpoint
  answers 403 to the requester and 200 to the admin, and preflight explains every check it ran
- The VitePress docs site builds with no dead links
- History is clean: every commit authored by the maintainer, no attribution trailers. Scan with
  `git log --format='%B' | grep -inE '^(co-authored-by|signed-off-by|generated with)'` — anchor it to
  the line start, or it matches ordinary prose in a commit body and reports a false positive.

**Run the suite with nothing else touching the tree.** Concurrent `dotnet build` / `dotnet test` runs
against one `obj/` and `bin/` produce results read from assemblies that do not match the source — a
composition test failed twice naming an entry the file no longer contained, with fresh timestamps, and
passed after a forced rebuild. Tests that look flaky under parallel agents are usually this, or a
shared Postgres; `AgentPlaneFixture.CreateAsync(isolated: true)` gives a private schema when it is the
database.

## What works end to end

A request goes from plain-English text through refinement, guardrails, dispatch, agent execution,
commit, push, and change-request creation, with the status thread and three-pane view following
along. The pieces that took the longest to get right, and that you should not casually refactor:

- **`CharterHost`** is the composition root. Tests build the same graph production builds, with
  `ValidateOnBuild` and `ValidateScopes` on. `Program.cs` is 55 lines and does nothing but parse,
  build, migrate, configure, run. Keep it that way — the alternative caused a whole class of bugs
  where tests composed a graph that production never built.
- **`ShimPublish`** commits, checks scope, pushes, and emits `branch_pushed`. Before it existed
  nothing pushed the branch and no code wrote that event; the end-to-end test injected it by hand
  and the suite was green anyway. `RunnerPublishTests` now runs the real shim against a real git
  repository and a real bare remote.
- **The Postgres job queue** — `FOR UPDATE SKIP LOCKED` plus `pg_try_advisory_lock`, lease TTL.
  No Redis, no in-memory orchestration state. A session is fully resumable from Postgres alone.
- **Mock API tree-shaking.** `src/Charter/ClientApp/src/api/mock/mockApi.ts` keeps its state behind
  a lazy `mockState()` accessor because module-scope initialisation is a side effect that defeats
  tree-shaking — the mock shipped in the production bundle until this was fixed. The `Dockerfile`
  sets `VITE_CHARTER_LIVE_API=true` before `npm run build`. Both halves are load-bearing.

## The pattern worth knowing before you change anything

Six defects in one sweep had a single shape: **a value the execution plane supplied was trusted, then
used to address something.** The rule they violated is now written down as **spec §16.3** — read it
before touching any code that consumes a runner event, a shim callback, or a webhook body.

The instances found and closed: a repository-derived secret authenticating the credential exchange
(so any workflow in a repo could mint for any other live session in it); a `run_url` the shim reported
being parsed for a repository name and used to cancel runs in *other* repositories; a job-claim filter
where the empty capability set is a subset of everything, letting a daemon eat control-plane jobs
including the control plane's own build rows; a pushed branch name taken at the runner's word, which
could advance `main` instead of the session branch; a `file_write` path that `..` could walk out of;
check output spliced unescaped into a pull request body; and an unauthenticated deployment webhook
whose URL became both a repeating server-side fetch with no SSRF filter and a preview button shown to
a requester under Charter's own promise that the link is safe.

Two lessons from that sweep are worth more than the list. First, **the agent is untrusted, so
everything it can influence is untrusted input** — including fields that look like plumbing. Second,
one of these was *pinned by a passing test*: `TheBranchTheRunnerReportedWinsOverTheConvention`
asserted the vulnerable behaviour as intended design. A green test is not evidence that behaviour is
correct; it is evidence somebody once believed it was.

## What is not done

Grouped by how much it would mislead someone who trusted the docs.

### Settings accepted and ignored

**The list is empty.** Every `CHARTER_*` variable Charter accepts now reaches something that reads it.
`ConfigReachabilityTests` guards this in both directions: it fails when a new value stops being
consumed, and it fails when an entry claims a value is ignored after somebody quietly wired it up.
That second guard exists because the list only ever grew — a stale entry went on asserting a value was
dead, and the next reader believed it.

### Unreachable states and missing paths

- `steering` is claimed by no adapter; answers to `NeedsInput` never reach a running agent
- Auto-rebase (§17) is not implemented — staleness is detected and reported, but a rebase needs a
  checkout, which needs a runner, which means a new job type and shim mode end to end
- Refinement deferral is silent — no "waiting for capacity" frame reaches the requester
- `ICredentialResolver.ReportSuccessAsync` is called by nothing, so a credential's `last_used_at`
  never advances. Wiring it needs a `ModelCompletion` that `RefinementResult` does not carry; the
  honest fix threads it through the refiner rather than fabricating one to satisfy the signature
- §27.1 verification artifacts (`build_artifact`, `capture`, `hil_report`) have no producer. They
  arrive with the project types that emit them, which is why object storage is wired to transcript
  offload instead — an abstraction with no caller is the defect this sweep existed to remove
- OAuth sign-in is built and registered but its callback route is not mapped, so password is the only
  usable identity provider

### Unverified against reality

- Railway GraphQL field names were written from documentation, never called
- The `codex`, `gemini-cli`, and `cursor-agent` adapter event maps are unverified
- `GitHubActionsRunner` needs a real `IGitHubRepositoryDispatcher`
- Charter Agent's pre-clone must carry a pushable credential or the push fails
- Recap evidence comes from the transcript rather than the provider's `CompareAsync`
- S3 storage is verified against a real MinIO container only. R2, B2 and Wasabi are untested
- Charter only tracks change requests it opened itself; one a human opens carrying the same work is
  invisible to it, so it never reaches `InReview` or `Merged`
- The GitHub App must be subscribed to **Pull request** and **Pull request review** or those two
  states never arrive, however correct the code is

## How the last defects were found

Nine defects were found in one sweep. The suite was green before and after — 2425 tests saw none of
them. They were found by **reading the implementation against what the documentation claimed**.

The reason is worth internalising, because it will happen again: tests compose their own object
graph and supply their own events. A test cannot see a code path that nothing calls. `branch_pushed`
was written by no code in the entire repository, and the end-to-end test passed because it supplied
the event itself. The onboarding grant was never called, so every onboarded repo was requestable by
nobody. The mock API shipped to production. The quick-start in the README could not work because
compose needed a `POSTGRES_PASSWORD` with no default.

So when verifying: **boot the real application and read the production path.** Green tests are
necessary and nowhere near sufficient. Two habits that paid off — boot in *both* Development and
Production (`ValidateScopes` differs, and a scoped-service-from-root bug hid in that gap), and after
fixing a wiring defect, delete the registration and confirm tests fail with the verbatim production
error.

## Constraints that are not negotiable

`AGENTS.md` has the full list and the reasoning. The four that get violated by accident:

1. **Commit attribution.** Every commit appears to come solely from the maintainer. No co-author
   trailers, no "generated with", no narration of how the work was produced. This bans *attribution*,
   not *vocabulary* — vendor and product names are ordinary technical terms and belong in a commit
   message when they are what the change is about. The test is whether a reader would take it as a
   byline. Applies to subagents identically; re-verify after any subagent commits.
2. **One instance, one organisation** (§7.2a). Personal mode is not a mode — it is an Organization
   with one Member holding every role, on the same authorisation code path. Never write
   `if (personalMode) skipPermissionCheck`. Multi-org means running two Charters.
3. **Authorisation is server-side omission** (§7.4). Engineer-only fields are absent keys — not
   nulls, not CSS-hidden. Test by asserting the key is missing.
4. **No browser storage APIs.** No `localStorage`, `sessionStorage`, `IndexedDB`, or JS-written
   cookies. Preferences live server-side so they survive devices and cannot desynchronise from
   server-enforced permissions.

Also: no merge button ever, no Redis, no `appsettings.json`, no ETAs in the UI (elapsed time only),
and sessions never install language runtimes.

## Working conventions that avoided trouble

Most of this codebase was built by parallel agents. What kept that from turning into a mess:

- **Partition by directory ownership**, stated explicitly in every agent prompt, with the list of
  directories other agents hold concurrently.
- **Agents never run git write commands.** One human-owned commit at the end of a wave.
- **One person owns `Program.cs` and `Hosting/`.** Concurrent edits to the composition root are how
  you get a graph nobody understands.
- **Pre-install shared NuGet packages** before a wave; agents editing the same `.csproj` conflict.
- **Require reports to state real command outcomes**, including what was left undone. An agent that
  reports success it did not verify is worse than one that reports a gap.

There is exactly one EF migration, `20260810023234_InitialCreate`, hand-edited with its Designer file
and snapshot, verified against an empty database. Migrations run automatically on boot (§2.3).

## Repository map

```
src/Charter/              ASP.NET Core control plane; ClientApp/ is the React SPA, bundled in
src/Charter.Agent/        Charter Agent daemon — dials outbound, claims jobs (§33)
src/Charter.DetachedRunner/  Detached runner host (§2.2, §27.3)
tests/Charter.Tests/      Everything
adapters/                 Agent adapter YAML — data, not code (§12b)
docs/                     User-facing only. VitePress → GitHub Pages
agent-docs/               Specs, briefs, planning. This file
```

`docs/` versus `agent-docs/` is a real boundary, not a preference: working notes in `docs/` get
published to operators. See §24 and §29.
