# AGENTS.md

Guidance for coding agents and engineers working in this repository. Tool-agnostic — read this
regardless of which assistant or CLI you are.

## What Charter is

Charter is a self-hostable web application that lets non-engineers file feature requests against
existing codebases, has an AI agent implement them, and returns a working preview environment the
requester can click and evaluate — without ever seeing GitHub. Agent execution is delegated to
existing coding-agent CLIs; Charter is not an agent harness. What Charter owns is spec refinement,
guardrails, and legibility. See `agent-docs/spec.md` §1.

## Stack

.NET 10 / ASP.NET Core (minimal APIs) · EF Core 10 with Npgsql · PostgreSQL 16+ · SignalR ·
React + Vite + TypeScript · Tailwind + shadcn/ui · Monaco (`DiffEditor`, lazy-loaded) · Serilog ·
OpenTelemetry. PostgreSQL is the only external dependency. Spec §3.

## Repository layout

```
Charter.sln
src/
  Charter/                       # ASP.NET Core control plane
    ClientApp/                   # React + Vite + TypeScript SPA
    Charter.csproj
  Charter.Agent/                 # Charter Agent daemon (spec §33)
  Charter.DetachedRunner/        # Detached runner host (spec §2.2, §27.3)
tests/
  Charter.Tests/
adapters/                        # Agent adapter YAML (spec §12b)
docs/                            # User-facing documentation only
agent-docs/                      # Briefs, planning notes, specifications
```

The frontend is bundled with the application: one container, one port, one artifact. Development
uses the Microsoft SPA Proxy, so `dotnet run` starts Kestrel *and* the Vite dev server. Publish runs
`npm ci` / `npm run build` as an MSBuild target into `wwwroot/`. Spec §3.1.

### `docs/` versus `agent-docs/`

`docs/` is reserved for actual user-facing documentation shipped to operators and contributors.
`agent-docs/` is for briefs, planning notes, specs, and anything written for engineers or coding
agents. Never put working notes, scratch analysis, or agent instructions in `docs/`. Spec §24, §29.

## Commands

```bash
# Restore
dotnet restore Charter.sln

# Build everything
dotnet build Charter.sln

# Run the app (Kestrel + Vite dev server via SPA proxy)
dotnet run --project src/Charter

# Test
dotnet test Charter.sln
dotnet test tests/Charter.Tests --filter FullyQualifiedName~SpecRefinement

# Format / lint (.NET)
dotnet format Charter.sln --verify-no-changes   # check
dotnet format Charter.sln                       # fix

# Frontend (run from src/Charter/ClientApp)
npm ci
npm run build
npm run lint
npm run test

# EF Core migrations (run from the repository root)
dotnet ef migrations add <Name> --project src/Charter
dotnet ef migrations remove --project src/Charter
dotnet ef database update --project src/Charter
dotnet ef migrations script --idempotent --project src/Charter

# Local Postgres
docker compose up -d postgres

# Seed demo data (fake org, repo, and completed sessions; disables outbound calls)
CHARTER_DEMO=true dotnet run --project src/Charter
```

Migrations run automatically on boot (spec §2.3), so `database update` is only for inspecting a
local database out-of-band.

## Conventions

- **Nullable reference types enabled; warnings as errors.** Both are set in `Directory.Build.props`
  — do not suppress per-project.
- **Minimal APIs**, not MVC controllers.
- **Records for configuration.** Config is parsed once at startup into an immutable `CharterConfig`
  record registered as a singleton.
- **No `appsettings.json`, ever.** No `Section__Nested__Key` double-underscore convention. All
  configuration comes from flat environment variables (`DATABASE_URL`, `CHARTER_*`, `OTEL_*`). Spec §4.1.
- **Fail fast and loud on startup.** Validate all configuration at once, print every problem, exit
  non-zero. Never fail lazily on first use.
- Serilog and OpenTelemetry are configured in code from `CharterConfig`, never from a config file.
- Conventional Commits. Documentation is second person, present tense, no emoji.

## Commit attribution

**Every commit must appear to come solely from the human maintainer.**

Nothing in the history may suggest a change was written by a tool rather than by the maintainer.

- Never add a `Co-Authored-By` trailer naming any tool, assistant, or model.
- Never add "Generated with" lines, attribution badges, footers, or signatures to any commit, pull
  request, issue, or release note.
- Never set `--author`, `GIT_AUTHOR_NAME`, `GIT_AUTHOR_EMAIL`, `GIT_COMMITTER_NAME`, or
  `GIT_COMMITTER_EMAIL` to anything other than the repository's configured user.
- Never describe the authoring process — no "as requested", "per the instructions", "automated
  change", or narration of how the work was produced.
- Commit messages describe the change, and nothing else.

**This bans attribution, not vocabulary.** Product and vendor names are ordinary technical terms and
belong in a commit message whenever they are what the change is about. `feat(models): qualify bare
identifiers as Anthropic` and `chore: scope the Claude Code permission allowlist` are both correct
and should not be reworded — they describe the change, and no reader would take them as a byline.
The test is not whether a name appears; it is whether the message implies who or what wrote the code.

This rule is **absolute**. It has no exceptions, it does not lapse over a long session, and it
applies to subagents exactly as it applies to you. After any commit made by a subagent, re-verify:

```bash
git log -1 --format='%an <%ae>%n%b'
```

Grep the log for trailers rather than for vendor names — the former are the actual failure:

```bash
git log --format='%B' | grep -iE 'co-authored|generated with|authored by'
```

A good commit message:

```
feat(refinement): refuse specs that touch denied paths

The refiner now loads the repo's scope config and rejects any spec whose
file scope intersects `scopes.deny`, returning a plain-English explanation
and routing the request to an engineer instead of dispatching.

Closes #142
```

## Hard constraints

Each of these exists for a reason; the reason is why it is not negotiable.

- **No merge button, ever.** Merge authority lives in GitHub branch protection and CODEOWNERS,
  outside Charter's trust boundary. Because the merge gate cannot move, loosening the spend gate is
  safe. Spec §1, §7.4, §7.5.
- **No browser storage APIs in the frontend.** No `localStorage`, `sessionStorage`, `IndexedDB`, or
  document cookies written from JS. User preferences live server-side against the user record, so
  they survive devices and cannot desynchronise from server-enforced permissions.
- **No in-memory orchestration state.** The container can restart mid-session; every session must be
  fully resumable from Postgres alone. Deferring this forces a rewrite. Spec §2.3.
- **No Redis or additional runtime services.** The job queue is Postgres with
  `SELECT ... FOR UPDATE SKIP LOCKED` and `pg_try_advisory_lock`. Every extra container is a
  self-host support burden. Spec §2.3.
- **Authorisation is enforced server-side; never hide data with CSS.** Engineer-only fields are
  omitted by the API, not rendered and hidden. A requester toggling views must never become a
  permission bypass. Spec §7.4, §27.7.
- **Sessions never install language runtimes.** Toolchains come from prebuilt runner images; a
  session that finds its image lacks a declared requirement fails fast with an actionable message.
  This is a supply-chain control, not a speed optimisation. Spec §16.1, §32.1.

Two further rules worth internalising early: personal mode is *not* a mode — it is an Organization
with one Member holding every role, on the same authorisation code path, so never write
`if (personalMode) skipPermissionCheck` (spec §7.2). And never show an ETA anywhere in the UI;
elapsed time only (spec §6).

## Where to look things up

`agent-docs/spec.md` is the source of truth. Read the relevant section before implementing — do not
infer the design from surrounding code.

| Topic | Section |
|---|---|
| Architecture, PaaS constraints, runner backends | §2 |
| Solution layout | §3.1 |
| Configuration and environment variables | §4 |
| Data model | §5 |
| State machine and requester-facing labels | §6 |
| Roles, guardrails, approval and auto-dispatch policy | §7 |
| `.charter/` folder in target repos | §8 |
| Repo onboarding and smoke test | §9 |
| Spec refinement; chat / plan / build modes | §10, §10b |
| Status thread, three-pane view | §11, §12 |
| Agent adapters (YAML) | §12b |
| Teaching and engineer recap | §13, §14 |
| Migration classification | §15 |
| Prompt injection and supply chain | §16 |
| Observability, logging modes, OpenTelemetry | §19 |
| Model credentials and providers | §20b |
| Build order (phases 1–5) | §23 |
| Repository conventions and deliverables | §24, §29 |
| Org standards and new projects | §26 |
| Project types beyond web; verification artifacts | §27 |
| First run, onboarding, demo mode | §30 |
| Runner provisioning and caching | §32 |
| Charter Agent daemon | §33 |
| Budgets and cost governance | §34 |
