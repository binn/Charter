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

- `dotnet build Charter.sln` — 0 warnings, 0 errors (warnings-as-errors is on)
- `dotnet test Charter.sln` — 2425 passed, 0 failed, 0 skipped
- `npm run test` in `ClientApp` — 159 passed across 19 files; typecheck, lint, and build clean
- Boots in 6 configurations: Development and Production × `CHARTER_RUNNER` of
  `github-actions` / `agent` / `docker`. Roughly 4s each, preflight runs, zero fatals,
  `GET /api/setup/status` returns 200
- The VitePress docs site builds with no dead links
- History is clean: every commit authored by the maintainer, no attribution trailers
  (`git log --format='%B' | grep -iE 'co-authored|generated with|authored by'` returns nothing)

Known red: `dotnet format --verify-no-changes` fails repo-wide. Pre-existing, never triaged.

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

## What is not done

Grouped by how much it would mislead someone who trusted the docs.

### Settings accepted and ignored

Tracked in `ConfigReachabilityTests.AcceptedAndIgnored`, which fails when a new one appears. §4.1
says Charter never accepts configuration it ignores, so each entry has exactly two honest endings:
wire it, or refuse it at startup. Shortening that list is real work; the list itself is the map.

### Unreachable states and missing paths

- `InReview` and `Merged` (§6) — no merge webhook, so a requester is never told their change shipped
- `steering` is claimed by no adapter; answers to `NeedsInput` never reach a running agent
- Migration classification (§15) runs, but confirm a destructive change actually halts a session
- The shim runs no build or test step, so §27.1 verification artifacts do not exist
- Refinement deferral is silent — no "waiting for capacity" frame reaches the requester

### Optimistic frontend types

The SPA types `proposedScope`, `checkpoints`, and `primerDraftMd`; no endpoint returns them.
`GET`/`POST /api/repos/{id}/access` exist with no UI, and role administration has an audit verb with
no writer.

### Unverified against reality

- Railway GraphQL field names were written from documentation, never called
- The `codex`, `gemini-cli`, and `cursor-agent` adapter event maps are unverified
- `GitHubActionsRunner` needs a real `IGitHubRepositoryDispatcher`
- Charter Agent's pre-clone must carry a pushable credential or the push fails
- Recap evidence comes from the transcript rather than the provider's `CompareAsync`

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
