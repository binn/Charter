# Charter — v1 Specification

**Status:** Draft for implementation
**Audience:** Engineers implementing v1 (and coding agents working from this doc)
**License:** AGPL-3.0-only, with contributor CLA
**Copyright:** Sarmad — personal project, developed independently

> **Location note.** This specification lives in `agent-docs/`, not `docs/`. `docs/` is reserved for
> user-facing documentation shipped to operators and contributors. `agent-docs/` holds briefs,
> planning notes, and specifications written for engineers and coding agents. See §29.

---

## 1. What Charter is

Charter is a self-hostable web application that lets non-engineers file feature requests against existing codebases, has an AI agent implement them, and returns a working preview environment the requester can click and evaluate — without the requester ever seeing GitHub.

The differentiated part of Charter is **not** the agent. Agent execution is delegated to existing coding agent CLIs (Claude Code, Codex). What Charter owns is:

1. **Spec refinement** — turning a vague human request into a scoped, buildable, human-approved specification.
2. **Guardrails** — who may request what, against which repos, touching which paths, at what cost.
3. **Legibility** — teaching requesters what happened to their product, and giving engineers a risk-ranked recap so review doesn't mean reading 5,000 lines.

### Non-goals for v1

- Charter is **not** a merge tool. It has no merge button and never will. Merge authority lives in GitHub branch protection, outside Charter's trust boundary.
- Charter is **not** an IDE. The code pane is a viewer, not an editor.
- Charter is **not** an agent harness. It does not implement an agent loop; it invokes agent CLIs.
- Charter does **not** collect usage analytics. It never phones home. (See §19.)

---

## 2. Architecture

### 2.1 Control plane / execution plane split

The single most important structural decision. Charter must deploy to PaaS platforms (Railway, Render, Fly) as well as to a VPS via Docker Compose.

Railway and comparable platforms prohibit privileged containers and block Docker daemon access. Charter therefore **cannot** assume it can spawn sibling containers. Agent execution is pluggable and lives behind `IAgentRunner`.

```
┌─────────────────────────────────────────────┐
│ CONTROL PLANE (charter-app)                 │
│ ASP.NET Core 10                             │
│  - REST API                                 │
│  - SignalR hub (live session events)        │
│  - Static SPA (React + Vite)                │
│  - IHostedService: session orchestrator     │
│  - IHostedService: queue dispatcher         │
│ Requires: HTTP port + Postgres. Nothing else.│
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│ EXECUTION PLANE (IAgentRunner)              │
│  - GitHubActionsRunner  (default on PaaS)   │
│  - DockerRunner         (VPS / Compose)     │
│  - DetachedRunner       (v2 — see §21)      │
└─────────────────────────────────────────────┘
```

### 2.2 Runner backends

| Backend | Mechanism | Use case |
|---|---|---|
| `AgentRunner` (**Charter Agent**) | Companion daemon on the operator's host, connects **outbound** to the control plane and claims jobs (§33) | **Primary backend.** Required for hardware, macOS/Xcode, and licensed toolchains. Fastest, because toolchains and caches persist. Works behind NAT with no inbound ports. |
| `GitHubActionsRunner` | `repository_dispatch` triggers a workflow; events stream back to a Charter webhook | Zero-infrastructure default for web projects on PaaS. Slowest — fresh VM every run. |
| `DockerRunner` | Local Docker socket; spawns sibling containers | Compose self-host where app and Docker share a host. Documented as root-equivalent host access. |

Selected via `CHARTER_RUNNER=agent|github-actions|docker`. Multiple may be enabled simultaneously; the dispatcher routes by capability match (§27.3).

### 2.3 PaaS constraints (these shape v1, not a later refactor)

- **No durable local disk.** Transcripts, diffs, and artifacts go to Postgres or S3-compatible storage. Never the container filesystem.
- **The container can restart mid-session.** No in-memory orchestration state. Every session must be fully resumable from Postgres alone. This is the constraint that forces a rewrite if deferred.
- **Postgres-backed job queue**, claimed with `SELECT ... FOR UPDATE SKIP LOCKED`. No Redis. Every additional container is a self-host support burden.
- **Advisory locks on the dispatcher** (`pg_try_advisory_lock`) so scaling to two replicas doesn't double-dispatch.
- **One HTTP port**, all config via environment variables, EF Core migrations run on boot.

---

## 3. Stack

| Layer | Choice | Notes |
|---|---|---|
| Backend | .NET 10, ASP.NET Core | Minimal APIs |
| ORM | EF Core 10 | Npgsql provider |
| DB | PostgreSQL 16+ | Only external dependency |
| Realtime | SignalR | Same port as HTTP |
| LLM | `Anthropic` NuGet (official C# SDK, v10+) | Currently beta; pin the version. **Do not** use `Anthropic` v3.x — that's the unrelated tryAGI community package, now at `tryAGI.Anthropic`. |
| Frontend | React + Vite + TypeScript | Bundled with the app — see §3.1 |
| UI | shadcn/ui + Tailwind | Plus custom components |
| Code viewer | Monaco (`DiffEditor`) | Lazy-loaded route split; never in the requester bundle |
| Virtualization | TanStack Virtual | Pane 2 event stream |
| Logging | Serilog | Seq sink (primary) + console (`LOGGING_MODE`) + OTLP. Configured in code, never from a config file. §19 |
| Tracing/metrics | OpenTelemetry | OTLP exporter for traces, metrics, and logs. §19.2 |

### 3.1 Solution layout

The frontend is **bundled with the application**, not deployed separately. One container, one port, one artifact — consistent with §2.3's single-HTTP-port requirement.

```
Charter.sln
src/
  Charter/                       # ASP.NET Core control plane
    ClientApp/                   # React + Vite + TypeScript SPA
      src/
      index.html
      package.json
      vite.config.ts
    Charter.csproj
  Charter.Agent/                 # Charter Agent daemon (§33)
  Charter.DetachedRunner/        # Detached runner host (§2.2, §27.3)
tests/
  Charter.Tests/
```

- **Development** uses the Microsoft SPA Proxy (`Microsoft.AspNetCore.SpaProxy`). `dotnet run` starts Kestrel and launches the Vite dev server, proxying unmatched requests to it. HMR works; there is no second command to remember.
- **Publish** runs `npm ci` and `npm run build` as an MSBuild target, emitting Vite output into `wwwroot/` so the published app serves API and SPA together from one origin.
- **CI** builds both halves in the same job (§29, `.github/workflows/ci.yml`).
- `ClientApp/node_modules` and Vite output are gitignored; the built assets are a publish artifact, never committed.

---

## 4. Configuration

### 4.1 Rules

- **No `appsettings.json`.** No `Section__Nested__Key` double-underscore convention.
- All configuration comes from flat, conventional environment variables.
- A hand-written parser loads and validates config **once at startup** into an immutable `CharterConfig` record, registered as a singleton.
- **Fail fast and loud.** On startup, validate everything and, if invalid, print *all* problems at once and exit non-zero. Never fail lazily on first use.

### 4.2 Environment variables

| Variable | Required | Default | Notes |
|---|---|---|---|
| `DATABASE_URL` | yes | — | `postgres://` or `postgresql://` URL |
| `PORT` | no | `8080` | PaaS convention |
| `CHARTER_BASE_URL` | yes | — | Public URL, for webhooks and links |
| `CHARTER_MODE` | no | `personal` | `personal` \| `organization` |
| `CHARTER_RUNNER` | no | `github-actions` | `github-actions` \| `docker` |
| `CHARTER_SECRET_KEY` | yes | — | ≥32 bytes, for cookie/token signing |
| `ANTHROPIC_API_KEY` | no* | — | Instance-level fallback credential |
| `OPENROUTER_API_KEY` | no* | — | Instance-level fallback credential |
| `CHARTER_CREDENTIAL_KEY` | yes | — | ≥32 bytes. Encrypts stored credentials. **Separate from `CHARTER_SECRET_KEY`** so cookie-key rotation doesn't invalidate them. |
| `CHARTER_ALLOW_SHARED_POOL` | no | `false` | Permits users to pool subscription credentials (§20b.7) |
| `CHARTER_MODEL_REFINE` | no | `claude-sonnet-5` | |
| `CHARTER_MODEL_BUILD` | no | `claude-opus-5` | Passed to the agent CLI |
| `CHARTER_MODEL_TEACH` | no | `claude-sonnet-5` | |
| `GITHUB_APP_ID` | yes | — | |
| `GITHUB_APP_PRIVATE_KEY` | yes | — | PEM, base64 accepted |
| `GITHUB_WEBHOOK_SECRET` | yes | — | |
| `CHARTER_LOG_LEVEL` | no | `information` | |
| `LOGGING_MODE` | no | `DEFAULT` | `DEFAULT` \| `JSON` \| `RAILWAY_JSON` — console sink formatting, see §19.1 |
| `CHARTER_SEQ_URL` | no | — | Enables Seq sink when set. Primary structured log target. |
| `CHARTER_SEQ_API_KEY` | no | — | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | — | Standard OTEL var; enables OTLP logs, traces, and metrics when set |
| `OTEL_EXPORTER_OTLP_HEADERS` | no | — | Standard OTEL var; e.g. auth headers for the collector |
| `OTEL_SERVICE_NAME` | no | `charter` | Standard OTEL var |
| `CHARTER_LOG_INCLUDE_TRANSCRIPTS` | no | `false` | See §19 — leaks source into log platform |
| `CHARTER_OAUTH_GITHUB_ID` / `_SECRET` | no | — | Enables provider when both set |
| `CHARTER_OAUTH_GOOGLE_ID` / `_SECRET` | no | — | |
| `CHARTER_OAUTH_DISCORD_ID` / `_SECRET` | no | — | |
| `CHARTER_OAUTH_SLACK_ID` / `_SECRET` | no | — | |
| `CHARTER_SAML_METADATA_URL` | no | — | Org mode only |
| `CHARTER_SMTP_URL` | no | — | `smtp://user:pass@host:port` |
| `CHARTER_DEFAULT_SESSION_BUDGET_USD` | no | `5.00` | |
| `CHARTER_DEFAULT_MONTHLY_BUDGET_USD` | no | `100.00` | |
| `CHARTER_UPDATE_CHECK` | no | `true` | §28 — the only outbound call Charter initiates |
| `CHARTER_UPDATE_CHANNEL` | no | `stable` | `stable` \| `prerelease` |
| `CHARTER_ALLOW_REPO_CREATION` | no | `false` | §26.10 — repo creation is a privilege escalation |
| `CHARTER_DEMO` | no | `false` | §30.6 — seeds a fake org, disables outbound calls |

\* At least one model credential must be resolvable at startup — either an instance-level key here or a linked `CredentialGrant` in the database. Startup validation fails if neither exists.

Convention: `CHARTER_` prefix except where an ecosystem-standard name already exists (`DATABASE_URL`, `PORT`, `ANTHROPIC_API_KEY`, `OTEL_*`). Prefer the standard name.

### 4.3 DATABASE_URL parsing

Npgsql does **not** natively accept URI-form connection strings. Charter must convert. Requirements:

- Accept both `postgres://` and `postgresql://` schemes.
- URL-decode username and password (passwords routinely contain `@`, `/`, `:`).
- Default port to `5432` when absent.
- Map query params: `sslmode=require|verify-full|disable` → Npgsql `SSL Mode`; treat `require` as `SSL Mode=Require;Trust Server Certificate=true` unless `verify-full`.
- Reject with a clear error if scheme, host, or database is missing.

```csharp
public static string ToNpgsql(string url)
{
    var uri = new Uri(url);
    if (uri.Scheme is not ("postgres" or "postgresql"))
        throw new ConfigException("DATABASE_URL must use postgres:// or postgresql://");

    var userInfo = uri.UserInfo.Split(':', 2);
    var db = uri.AbsolutePath.TrimStart('/');
    if (string.IsNullOrEmpty(db))
        throw new ConfigException("DATABASE_URL is missing a database name");

    var b = new NpgsqlConnectionStringBuilder
    {
        Host     = uri.Host,
        Port     = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = db,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
    };

    var q = HttpUtility.ParseQueryString(uri.Query);
    b.SslMode = q["sslmode"] switch
    {
        "disable"      => SslMode.Disable,
        "verify-full"  => SslMode.VerifyFull,
        "require" or null or "" => SslMode.Require,
        var other => throw new ConfigException($"Unsupported sslmode: {other}")
    };
    if (b.SslMode == SslMode.Require) b.TrustServerCertificate = true;

    return b.ConnectionString;
}
```

Note for Railway: `DATABASE_URL` resolves to the private network address. `DATABASE_PUBLIC_URL` exists but should not be used by the app.

---

## 5. Data model

```
Organization      id, name, mode, created_at
Member            org_id, user_id, role[]              -- multiple roles per member
User              id, email, display_name, teaching_level, created_at
Identity          user_id, provider, provider_user_id  -- one row per linked OAuth/SAML identity

Repo              org_id, github_installation_id, full_name, base_branch,
                  status(pending|recon|configuring|smoke_test|ready|disabled),
                  charter_config_snapshot (jsonb), primer_md
RepoScope         repo_id, member_id|role, can_request  -- deny by default

Request           id, org_id, repo_id, requester_id, raw_text, template_id,
                  status, created_at
Spec              id, request_id, version, title, body_md, acceptance_criteria (jsonb),
                  approved_by, approved_at
Session           id, spec_id, runner, agent_model, base_commit_sha,
                  status, started_at, ended_at, cost_usd, cancel_requested_at
Event             id, session_id, seq, type, payload (jsonb), created_at
                  -- append-only; the streamed transcript
Milestone         session_id, event_id, label, annotation_md
                  -- promoted, requester-facing subset of Event

PullRequest       session_id, number, url, head_sha, state, is_stale
Deployment        pull_request_id, provider, url, state, reported_at

Walkthrough       session_id, level, body_md, generated_at, cost_usd
Recap             session_id, body_md, risk_items (jsonb), generated_at, cost_usd
ConceptLedger     user_id, concept, first_explained_at, times_referenced

LedgerEntry       org_id, user_id, session_id, budget_ids[],
                  category(build|teach|refine|recap|recon|scaffold|chat),
                  unit(usd|quota_sessions), amount, imputed_usd,
                  state(reserved|settled|released), reserved_until,
                  credential_grant_id, created_at
AuditLog          org_id, actor_user_id, action, target_type, target_id,
                  metadata (jsonb), created_at
Job               id, type, payload, status, claimed_at, claimed_by, attempts
                  -- the FOR UPDATE SKIP LOCKED queue
```

`Event` will be the largest table by orders of magnitude. Partition by month or plan for retention pruning (§20).

---

## 6. State machine

```
Draft → Refining → SpecReady → Queued → Running ⇄ NeedsInput
                       ↑                   ↓
                       └─── Rejected    PROpen → PreviewReady → InReview → Merged
                                            ↓
                                        Failed / Cancelled / Stale
```

Requester-facing labels:

| Internal | Shown to requester | Notifies? |
|---|---|---|
| Refining | *Let's figure out what you need* | no |
| SpecReady | *Waiting on {approver} to approve* — skipped entirely when auto-dispatch applies (§7.5) | no |
| Queued / Running | *Building this now* | no |
| NeedsInput | *Question for you* | **yes** |
| PROpen | *Building this now* (unchanged) | no |
| PreviewReady | *Ready to try* | **yes** |
| InReview | *An engineer is checking it* | no |
| Merged | *This is live* | no |
| Failed | *This turned out to be bigger than expected — an engineer has been notified* | no |

Only two states notify. Notifying on all of them gets Charter muted within a week.

**Never show an ETA.** Elapsed time only. Agent runs are wildly variable; one blown estimate costs more trust than ten honest slow ones.

---

## 7. Personas and permissions

### 7.1 Roles

| Role | Sees |
|---|---|
| **Requester** | Text box, refinement conversation, status thread, preview button. Never a repo name, branch, diff, or token count. |
| **Approver** | Queue of refined specs with estimated cost. Approves/rejects. Gates *spend*, not code quality (§7.5). In small teams this role may go unused entirely — budgets do the governing. |
| **Engineer** | Sessions, transcripts, diffs, steering. Configures repos and scopes. Reviews on GitHub. |
| **Admin** | Members, roles, budgets, repo connections, model selection, audit log. |

Roles are additive — a member may hold several.

### 7.2 Personal mode is not a mode

**Critical.** Personal mode is an Organization with one Member holding all roles, with approval gates auto-satisfied by policy.

Same tables. Same authorization code path. Same checks. Only the seeded defaults differ.

Do **not** write `if (personalMode) skipPermissionCheck`. That branch is how org mode becomes the untested special case, and it is the exact failure this design exists to avoid. Inviting a second user must be the only thing that changes, and it must require no migration.

### 7.3 Guardrail primitives

1. **Repo scope** — who may file against which repo. **Deny by default.** A newly connected repo is requestable by nobody.
2. **Path scope** — applies to the *agent*, not the user. Enforced in the runner, not the UI, so a compromised session cannot widen it.
3. **Spend caps** — per requester, per period, pre-authorized. Sessions refuse to *start* rather than dying halfway.
4. **Approval gates** — expressed as role-based state transition rules. This is the entire policy engine; resist building more.
5. **Audit log** — every agent action attributable to a named human. The agent never acts on its own initiative: no schedulers, no infinite auto-retry.

### 7.4 Trust boundary

- Charter has **no merge button**. Branch protection + CODEOWNERS enforce this on GitHub.
- The runner receives a **short-TTL GitHub App installation token scoped to one repo** and cannot read the control plane's environment.
- Pane 2 (transcript) and pane 3 (code) render **only if that user has repo read access**. Otherwise a requester toggling views becomes a permission bypass — transcripts leak file paths, env var names, and error output.

### 7.5 Approval policy

**Two gates exist, and conflating them is a design error.**

| Gate | Question | Configurable? |
|---|---|---|
| **Spend gate** | Is this worth burning tokens and quota on? | **Yes** — fully |
| **Merge gate** | Is this code fit to ship? | **Never** — branch protection, CODEOWNERS, outside Charter |

Everything below concerns the spend gate only. The merge gate does not move, cannot be delegated, and is not represented in Charter's data model at all.

Because the merge gate is immovable, loosening the spend gate is safe. The worst case is wasted tokens and a PR nobody wanted — not shipped code.

#### Auto-dispatch

`SpecReady → Queued` can be automatic. The policy is **conditional, not a boolean** — "trust this person, up to this much, in this area":

```
AutoDispatchPolicy   org_id, repo_id?, role? , user_id?,
                     enabled,
                     max_cost_usd,              -- per session
                     max_concurrent_sessions,
                     allowed_paths[],           -- subset of repo scope, never a superset
                     project_types[],
                     require_approval_above_usd
```

Resolution is most-specific-wins: user override → role → repo default → org default.

Examples that should all be expressible:

- *Personal mode* — everything auto-dispatches. There is nobody to approve.
- *Small team* — everyone auto-dispatches up to $2; anything larger queues for approval.
- *Ops team on the internal tool* — auto-dispatch within `src/Features/**` only.
- *New hire* — approval required for their first month.
- *Large org* — approval required by default, with named exceptions.

#### What auto-dispatch never bypasses

These are agent-level or org-level controls and are unaffected by who filed the request:

- **Path scope** (§8) — enforced in the runner
- **Migration classification** (§15) — destructive changes still halt
- **Budget caps** (§7.3) — the monthly ceiling is the real governor in a loosened configuration
- **Rate limits** (§31) — no one queues 400 sessions
- **Repo readiness** — a repo that hasn't passed its smoke test (§9) is never auto-dispatchable
- **Standards injection** (§26.3) — refinement is still policy-constrained

**Budget replaces approval as the primary control in small teams.** A person with a $50 monthly cap self-limits without anyone reviewing anything, which is exactly the behaviour a five-person company wants.

#### Composition rule

Org-level policy lives in Charter's database, editable by admins and fully audited. Per-repo policy lives in `.charter/config.yml`.

**A repo may only tighten, never loosen.** A sensitive repository can require approval regardless of org policy; no admin setting can override the repo's own restriction. This keeps the §8 principle intact — the strictest guardrail always lives in the reviewable file.

#### Post-hoc review

When a session is auto-dispatched, nobody vetted the spec, and the engineer must know that:

- `Session.auto_dispatched = true`
- The PR is labelled `unreviewed-spec`
- The engineer recap (§14) **leads** with the fact that no human approved the specification before the build, and the spec is included in full rather than summarised

Post-hoc, the engineer has four actions, all first-class:

| Action | Behaviour |
|---|---|
| **Approve** | Normal review path; merge as usual |
| **Steer** | Continue the existing session with a new instruction; same branch, same thread |
| **Revise and rebuild** | Fork the spec, edit it, dispatch a fresh session onto the same branch |
| **Take over** | Check out the branch and finish by hand. Charter marks the session `handed_off` and stops touching it — no further agent writes to that branch. |

**Take over must be explicit and must stop agent writes.** An agent and a human editing the same branch concurrently is the one genuinely destructive failure mode in this design.

#### Suggested, never automatic, trust

Charter may *suggest* loosening: *"Eleven of Ayesha's requests were approved without changes. Allow auto-dispatch up to $2 in `src/Features/**`?"*

An admin decides. Charter never grants itself permissions, and trust never escalates on its own.

---

## 8. `.charter/` folder

Lives in the target repo. Everything except `cache/` is committed, so **changing a guardrail requires a PR and code review** — reusing machinery that already exists rather than inventing an in-app approval flow.

```
.charter/
  config.yml          # scopes, base branch, seed cmd, runner image, limits
  conventions.md      # agent guidance layered on CLAUDE.md, not duplicating it
  primer.md           # requester-facing "how this app is put together"
  glossary.yml        # domain term → plain English
  templates/          # request templates: bug, copy change, new field
  checks/             # named validation commands the agent must pass
  policies/
    migrations.yml    # destructive-operation rules
  cache/              # generated recon output — gitignored
```

### config.yml

```yaml
version: 1
base_branch: main
runner_image: ghcr.io/binn/charter-runner-dotnet:1
seed: "dotnet run --project tools/Seed"     # optional
scopes:
  allow:
    - "src/Features/**"
    - "src/Web/Components/**"
  deny:
    - "src/Auth/**"
    - "**/Migrations/**"
    - ".github/**"
    - "infra/**"
    - "**/appsettings*.json"
checks:
  - name: build
    run: "dotnet build"
  - name: test
    run: "dotnet test"
limits:
  max_session_usd: 5.00
  max_files_changed: 40
```

### glossary.yml

Punches above its weight. Domain vocabulary ("BOQ", "derate", "interconnection") means nothing to a general model. One file, two consumers: it disambiguates the **spec refiner** and grounds the **teaching** pass.

```yaml
BOQ: "Bill of Quantities — the itemised list of equipment and materials in a quote."
derate: "Reducing a rated output to account for real-world losses like heat or shading."
```

### templates/

A requester picking "change some text" instead of free-typing skips half the refinement round-trips. Cheapest quality win available.

### Extensibility rules

- `version: 1` at the top of every YAML file from day one.
- **Unknown keys warn, never fail** — so an old Charter version doesn't break on a repo written for a newer one.
- Folder conventions are the extension mechanism. **No plugin system in v1.**

---

## 9. Repo onboarding

A wizard that ends in **proof**, not configuration. If connecting a repo is a manual engineer chore, adoption stalls at repo one.

1. **Connect** — GitHub App install, pick base branch.
2. **Recon session** — read-only agent run over the repo. Outputs detected stack, structure map, test/build commands, existing conventions, and a *proposed* scope config. If `CLAUDE.md` / `AGENTS.md` exists, import and extend — never overwrite.
3. **Scope confirmation** — visual file tree with allow/deny toggles. Defaults denied: migrations, auth, CI config, infra, secrets. Writes `.charter/config.yml` as a PR.
4. **Smoke test** — Charter files a canned trivial request and runs the entire loop: agent runs → checks pass → PR opens → preview deploys → URL binds back. Nothing else validates all six integration points at once.
5. **Primer** — agent drafts `.charter/primer.md`, engineer edits, publish.

**A repo is invisible to requesters until the smoke test passes.** This ties directly into §7.3: repo scope defaults to nobody, and "ready" is earned.

Offer re-recon on demand — repos drift.

### Seed data

Not all software needs it, and a codebase without a dev seed path probably isn't mature enough for non-engineer requests. So: `seed` is optional in `config.yml`, and the **smoke test warns rather than blocks** when it detects an empty preview:

> Preview deployed but appears to have no data — requesters may not be able to evaluate changes.

---

## 10. Spec refinement

The core novel component, and a **security boundary** (§16).

Flow: raw request → clarifying conversation → refusal to dispatch anything still ambiguous → **spec confirmation card**.

The confirmation card restates the request in the requester's own words with acceptance criteria as bullets, and they confirm. This is the ownership moment: later, when a preview is wrong, the conversation is "the spec said X" rather than "the AI misunderstood."

Refinement must:
- Load `glossary.yml` for domain vocabulary.
- Load `primer.md` for codebase shape.
- Refuse to produce a spec touching denied paths — explain in plain English and route to an engineer.
- Emit structured acceptance criteria (used by the "what to check" list and by the recap).

---

## 10b. Interaction modes

Charter exposes three modes over a project. They are modes of one conversation surface, not separate apps, and a conversation can be promoted upward (`chat → plan → build`) without losing history.

| Mode | Purpose | Produces | Dispatches an agent? |
|---|---|---|---|
| **Chat** | Ask questions about an existing project | Nothing | No |
| **Plan** | Explore a change or a new project before committing | A Spec or a Project Charter | No |
| **Build** | Implement an approved Spec | PR + preview | Yes |

### Chat mode

Read-only Q&A grounded in `primer.md`, `glossary.yml`, the repo structure, and prior session history. *"How does the quote wizard decide which vertical to show?"*

Cheap, and it is where a meaningful share of requests should die — because the answer is often *it already does that* or *that would break X*. A request that never becomes a session is the cheapest possible outcome. Instrument this: **questions resolved in chat** is a real success metric, not a vanity one.

Chat has no repo write access and cannot promote itself to Build without passing through Plan.

### Plan mode

Multi-turn exploration that produces **options with tradeoffs**, not a single answer. Ends in one of: a Spec ready for approval, a Project Charter (§26), or nothing at all — abandoning a plan must be a normal, unpenalised outcome.

Plan mode is where cost is saved. Ten minutes of planning tokens is orders of magnitude cheaper than a build session against a misconceived spec.

### Explain — a lens, not a mode

**The problem:** an AI-refined spec written to be precise enough for an agent is, by construction, too dense for the person who asked. If the requester cannot understand the thing they are approving, approval is theatre and the accountability model in §10 collapses.

**The solution: the Spec is a structured object with two renderings.**

```
Spec {
  title
  outcome            -- plain language, what the requester will see change
  acceptance_criteria[] -- authored in plain language, SHARED by both views
  technical_approach -- engineer-facing
  scope { files, paths }
  risks[]
  open_questions[]
}
```

- **Requester view** renders `title`, `outcome`, `acceptance_criteria`. Nothing else.
- **Engineer view** renders everything.
- **Explain** expands any term inline, calibrated by the user's teaching level and concept ledger (§13). Same machinery, no new subsystem.

**Load-bearing rule: the structured Spec is the single source of truth.** The plain-language rendering is generated *from* it and regenerated whenever it changes. `acceptance_criteria` are authored in plain language first and shared verbatim between both views — they are the contract. If the two renderings can drift, *"the spec said X"* stops meaning anything and §10's accountability is lost.

The requester approves the **acceptance criteria**, not the technical approach. That is the thing they can meaningfully judge.

---

## 11. Status thread

**One thread per request, forever.** Multiple sessions, revisions, and follow-ups collapse inside it. A requester should never wonder which of three cards is live.

- **Translated milestones, not the raw transcript.** Promote ~4 event types into pane 1: *understanding the current setup*, *making changes*, *checking it works*, *putting it together*. Everything else stays in the engineer view. But **do** stream something — a 5–20 minute silent gap reads as broken.
- **"What to check" beside the preview button**, derived from acceptance criteria. Without it a preview URL is a dead end.
- **Feedback is two buttons** — *Works* / *Not quite*. The second opens a box and becomes a new session on the same spec, same thread. Don't make them write a bug report.
- **Failure has dignity.** Budget exhausted, agent stuck, checks failing → all render as *this turned out to be bigger than expected*. Real detail goes to the engineer view. A non-engineer who sees a stack trace once never files again.
- **Cancel button.** Must actually kill the runner and settle token cost. Easy to forget, awkward to retrofit.

---

## 12. Three-pane view

Progressive disclosure. Named for the user: **Simple / Detailed / Developer**.

| Pane | Content |
|---|---|
| 1 | Status thread, milestones, teaching annotations |
| 2 | Raw event stream, virtualized, cursor-paginated |
| 3 | Monaco diff viewer |

**Panes must be linked or it's three apps in a trenchcoat.** Clicking a milestone in pane 1 scrolls pane 2 to the events that produced it. Clicking a file-write event in pane 2 opens pane 3 at that hunk. Selection is shared state. This is the only reason to put them side-by-side rather than in tabs — and it's what makes the modes *teach*: a user in 2-pane starts to see which plain-English milestone maps to which tool call.

- Defaults by role (requester → 1, engineer → 3), then persisted per user as a preference.
- **Pane 2 and 3 availability is a permission, not a preference** (§7.4).
- **Viewer, not editor, in v1.** An editor raises an unanswerable question: what happens when a human edits a file the agent is concurrently writing in the same worktree? Escape hatch is "open in GitHub." In-app, you change code by steering the agent.
- Mobile collapses to a segmented control, one pane at a time.

---

## 12b. Agent adapters

The coding-agent landscape changes monthly. **Adapters are data, not code** — declarative YAML in `adapters/`, so supporting a new agent is a configuration PR, not a Charter release. Users can drop a local adapter into their instance without forking.

```yaml
# adapters/pi.yml
id: pi
display_name: "Pi"
version: 1
install:
  check: "pi --version"
  hint: "npx @earendil-works/pi-coding-agent"
invoke:
  command: ["pi", "--print", "--output-format", "jsonl"]
  prompt: stdin
auth:
  anthropic_api_key:  { env: "ANTHROPIC_API_KEY" }
  openai_api_key:     { env: "OPENAI_API_KEY" }
  openrouter_key:     { env: "OPENROUTER_API_KEY" }
  google_api_key:     { env: "GEMINI_API_KEY" }
  xai_api_key:        { env: "XAI_API_KEY" }
model_arg: ["--model", "{model}"]
events:
  format: jsonl
  map:
    tool_use:   "$.type == 'tool_call'"
    file_write: "$.tool == 'edit' || $.tool == 'write'"
    message:    "$.type == 'assistant'"
capabilities: [steering, resume, cost_reporting]
```

### Adapters shipped in-tree

| Adapter | Notes |
|---|---|
| `claude-code` | Subscription OAuth or API key; `ANTHROPIC_BASE_URL` for gateways |
| `codex` | OpenAI-compatible endpoints |
| `gemini-cli` | |
| `opencode` | Multi-provider |
| `pi` | Minimal four-tool core over 20+ providers, with subscription login. **The widest model coverage from a single adapter** — good default when the org wants provider flexibility. |
| `cursor-agent` | |
| `aider` | |

### Requirements on an adapter

- **Streaming, machine-readable output.** An adapter that can only emit human-formatted text degrades pane 2 to a raw log and breaks milestone promotion. Mark such adapters `events.format: text` and document the degraded experience rather than pretending parity.
- **Non-interactive/headless mode.** Anything requiring a TTY prompt cannot be dispatched.
- **Cost reporting** where available; otherwise Charter estimates from token counts and the provider price table.

### Model × adapter compatibility

Not every model works with every agent. The UI must resolve the intersection of *(available credentials) × (adapter's supported providers) × (repo policy)* and show only valid combinations. Silently accepting an impossible pairing and failing at dispatch is the worst outcome.

---

## 13. Teaching

Optional, costs extra tokens, and inverts the value proposition: Charter stops being "non-engineers get features built" and becomes "non-engineers gradually understand their own product."

Runs over the **completed session's real events**, so every explanation is grounded in what actually happened. Generic content is worthless; *"your quote wizard stores the selected vertical in a table called Quotes, and adding this meant one new column"* is not.

### Three surfaces, ascending cost

1. **Inline annotations** on pane-1 milestones — one sentence each. One call over the milestone list.
2. **The walkthrough** — post-session narrative of what changed and why, linking into pane 2. The main event.
3. **On-demand "explain this"** — click any event, file, or hunk. Unbounded, so this is the one that needs a per-user cap.

### Cost model

Teaching tokens are a **separate budget line** from build tokens, and generated **lazily** — only when the user opens the tab. Otherwise an admin trimming spend cuts teaching first, since it's the line item with no immediately visible output. Naming it separately is what protects it.

### Calibration

**Starting level**, named for what the reader *wants*, never for what they lack — never label a human "beginner" in a UI their colleagues can see:

- `explain_everything` — assumes no vocabulary; every term defined on first use
- `skip_the_basics` — knows what a database and a deploy are; wants the reasoning
- `just_the_decisions` — trade-offs and alternatives only, no mechanics

**Plus a per-user concept ledger.** Track every concept already explained to that person; next time it's referenced, not re-taught. An `explain_everything` requester organically graduates over fifteen sessions without touching a setting. Pass the ledger into the teaching prompt as *already knows: X, Y, Z*.

- Let them **reset** the ledger (people forget).
- **Cap injection** at a few dozen most-recent concepts or the prompt bloats and cost creeps.
- **Per-walkthrough override** always visible: *more detail* / *less detail* regenerates without changing their default.

### Traps

- **No quizzes, no progress bars, no streaks.** The moment it feels like corporate training, adoption dies.
- Also generate a **per-repo primer** that new requesters read once, separate from any session (§8).

---

## 14. Engineer recap

Structurally the same feature as the walkthrough, calibrated to the opposite audience. Same event stream, different prompt.

Contents:

1. **One-paragraph what and why**, tied back to the approved spec.
2. **Where the agent deviated** from the spec, or made a call the spec didn't cover. Highest-value section and the thing reviewers most often miss.
3. **Risk-ranked file list**, not alphabetical. Auth, migrations, money math, external calls, and denylist-adjacent paths float to the top; tests and formatting sink.
4. **What it couldn't verify** — tests not written, edge cases noticed and skipped.
5. **Suggested review order**, starting where the risk is.

Two rules:

- **Post it as a PR comment**, not just in Charter. Engineers review on GitHub.
- **It must never say "looks good."** It's an orientation aid, not a verdict. The moment it editorialises on quality, reviewers start trusting it instead of reading.

---

## 15. Migration classification

Preview databases are disposable, so the risk isn't data loss during a session — it's a **bad migration merging**. Classify rather than blanket-gate:

| Class | Operations | Behaviour |
|---|---|---|
| **Additive** | new table, nullable column, index, new FK on empty table | Flows normally, PR labeled `schema-change` |
| **Ambiguous** | rename, type change, non-null **with** default | Engineer review required; PR blocked until approved |
| **Destructive** | drop column/table, truncate, non-null **without** default | **Session halts.** Agent writes the intent; engineer authors the migration manually. |

Classify **structurally**, not heuristically: parse the generated migration and inspect the EF Core `Up` operations. Rules configurable in `.charter/policies/migrations.yml`.

CODEOWNERS on the migrations directory makes engineer approval structurally required regardless.

---

## 16. Prompt injection threat model

The agent consumes untrusted text from non-engineers *and* untrusted repo content (dependency READMEs, issue text).

**Primary mitigation, already structural:** the agent never sees raw requester text. It sees the **refined, human-approved spec**, which is model-authored. Refinement is a sanitisation boundary and approval is a human review of what the agent will be told. State this explicitly in `SECURITY.md` — it's the strongest property Charter has.

Layered on:

- **Egress allowlist in the runner** — package registries and the model API only. Exfiltration needs somewhere to go.
- **Runner sees no control-plane env.** Short-TTL, single-repo installation token, nothing else.
- **Flag instruction-shaped language** in submitted requests (imperatives addressed to the agent, base64 blobs, URLs) for engineer review before dispatch.
- **Log every file write and network call**, attributable to a session and a named human.
- Do **not** rely on "ignore injected instructions" in the system prompt. It's a layer, not the defence.

### 16.1 Toolchain supply chain

Locking toolchains into prebuilt images (§32) is a **security control**, not just a speed optimisation. A session permitted to install its own tooling can:

- Fetch a typosquatted or compromised package that reads the workspace and exfiltrates source
- Read environment variables and any credential material present in the process
- **Reveal the runner's public IP and network position** to an attacker-controlled endpoint — on a `DetachedRunner`, that is the operator's own network, not a disposable cloud VM
- Persist into a shared cache and affect subsequent sessions

Prebuilt images plus the egress allowlist close the *tooling* vector.

### 16.2 What this does not close — state it plainly

**Project dependencies are still installed.** Charter runs `npm ci` or `dotnet restore` against the repository's own manifests, and a compromised transitive dependency in the project remains a live risk. Locked toolchains do not fix this, and the docs must not imply they do.

Mitigations, all of which belong in the runner contract:

- **Lockfile-only installs.** `npm ci`, `dotnet restore --locked-mode`, `pnpm install --frozen-lockfile`. Never resolve fresh versions during a session.
- **Disable install scripts** by default — `npm ci --ignore-scripts`. Repos genuinely needing postinstall opt in per repo, explicitly.
- **Egress allowlist** so an exfiltration attempt has nowhere to send data.
- **Optional registry proxy** — point runners at an internal Artifactory/Verdaccio/BaGet so dependency fetches never touch the public internet directly.
- **Dependency changes are a flagged diff category** in the engineer recap (§14), ranked with auth and migrations.

Repo-content injection is the harder half. The answer is unchanged: **the agent cannot merge.**

---

## 17. Concurrency

- Record **base commit SHA** per session.
- On merge to the base branch, mark open PRs that are *behind* **and** *overlap on changed files* as stale.
- **Attempt auto-rebase.** Clean rebase + green checks → update silently, requester never knows. Conflict → new session with the conflict as context.
- **Optional per-repo serialization by path scope** — one active session per area, others queue. Enable for hot areas, not globally.
- **Zombie TTL** — PR open with no activity past N days auto-closes with a friendly note and one-click re-run. Otherwise you accumulate forty stale branches a quarter.

---

## 18. Preview environment binding

Provider-agnostic by design. Two ingestion paths:

1. **Generic webhook** — `POST /api/deployments/{prSha}` with `{ url, state, provider }`. Documented so Render/Fly/Coolify self-hosters are first-class.
2. **PR comment parsing** — fragile but universal fallback. Railway's GitHub bot comments when the PR environment is ready.

Railway-specific note: PR Environments replicate every service, database, and variable from the base environment into an isolated ephemeral environment with fresh URLs. **Base them off a staging environment, not production**, so preview secrets are never real ones. Railway also won't deploy a PR branch from a user outside the workspace unless they've been invited with that GitHub account.

---

## 19. Observability

Two things share the word "telemetry" and have **opposite** defaults. Keep them separate in the codebase.

### Observability — on by default

For the operator watching their own instance.

**Serilog is the logging pipeline.** It is configured entirely in code from `CharterConfig` (§4.1) — there is no `appsettings.json` to read a Serilog section from. Three sinks, independently enabled:

| Sink | Enabled by | Role |
|---|---|---|
| **Seq** | `CHARTER_SEQ_URL` set | **Primary structured log target.** Richest query experience, and the one to reach for first when debugging a session. |
| **Console** | always | Formatting controlled by `LOGGING_MODE` (§19.1). This is what a PaaS log drain scrapes. |
| **OTLP** | `OTEL_EXPORTER_OTLP_ENDPOINT` set | Vendor-neutral export of logs, traces, and metrics. |

- OTLP is the right vendor-neutral target: covers Grafana, Datadog, Honeycomb, and Signoz without writing exporters. Seq and OTLP are complementary, not alternatives — run both.
- Instrument what hurts: session lifecycle spans, token cost per session as a metric, runner queue depth, webhook failures, agent tool-call latency.
- **Correlate everything by session ID** so one Seq query pulls the whole story. The same value is set as an OpenTelemetry span attribute so traces and logs join up.
- Enrich every event with the trace and span ID so a Seq row links back to its OTLP trace.

### 19.1 `LOGGING_MODE`

The console sink is the only one that always exists, so its format has to suit wherever Charter is running.

| Value | Formatter | Use case |
|---|---|---|
| `DEFAULT` | Serilog's human-readable console template, with colour when the terminal supports it | Local development, `docker compose up`, reading logs by eye |
| `JSON` | One JSON object per line, `CompactJsonFormatter` | Any log platform that ingests stdout and parses JSON — Loki, Vector, Fluent Bit, CloudWatch |
| `RAILWAY_JSON` | One JSON object per line, shaped for Railway's structured log parser: a `message` string, a `level` string Railway recognises, and remaining Serilog properties flattened alongside | Railway, which renders structured fields as filterable attributes only when they arrive in this shape |

Invalid values fail startup validation with the list of accepted ones (§4.1) rather than silently falling back — a logging misconfiguration discovered during an incident is the worst possible time to find it.

### 19.2 OpenTelemetry

Traces, metrics, and logs all export over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Standard `OTEL_*` environment variables are honoured directly rather than re-prefixed (§4.2), so an operator's existing collector configuration works unchanged.

- **Traces** — ASP.NET Core, `HttpClient`, and Npgsql instrumentation, plus custom spans for the session lifecycle: refinement, dispatch, runner execution, PR creation, artifact binding.
- **Metrics** — token cost per session, queue depth, dispatch latency, runner claim latency, webhook failure count, sessions by terminal state.
- **Logs** — the Serilog OTLP sink, so log records carry the same resource attributes as traces.

The transcript leak warning below applies to every sink equally.

**Leak warning:** transcripts contain repo content and requester business context. If those flow into structured log properties, source code has been exported into the operator's log platform. Log event **metadata** by default — type, timing, file paths, cost. Transcript bodies only behind `CHARTER_LOG_INCLUDE_TRANSCRIPTS=true`.

### Phone-home analytics — does not exist

Charter collects nothing. No opt-in, no consent flow, no data policy needed.

**State this in the README explicitly.** Self-hosters look for the promise, and its absence reads as evasion even when nothing is collected:

> Charter never phones home. Observability data goes only where you configure it.

Accepted tradeoff: no aggregate feature-usage signal; problems surface only through issues.

---

## 20. Data handling

- **Retention policy** for `Event` — configurable, defaulted, and actually enforced by a pruning job.
- **Per-request delete** and **org export**.
- Transcripts contain business context; treat deletion as a first-class feature, not a support request.

---

## 20b. Model credentials and providers

Charter consumes models on **two distinct surfaces**, and they authenticate differently. Conflating them is the main implementation hazard here.

| Surface | Calls | Auth mechanism |
|---|---|---|
| **Control plane** | refinement, teaching, recap, recon | Charter's own HTTP client via `IModelClient` |
| **Agent runs** | the actual build | Env vars injected into the runner process, consumed by the agent CLI |

### 20b.1 `IModelClient`

The official Anthropic C# SDK only speaks Anthropic. Charter needs an abstraction. Three implementations cover every required provider:

| Implementation | Covers |
|---|---|
| `AnthropicModelClient` | Anthropic (official `Anthropic` NuGet) |
| `OpenAiCompatibleModelClient` | OpenAI, OpenRouter, xAI/Grok, DeepSeek, Groq, Azure OpenAI, Ollama, and anything else exposing `/chat/completions` |
| `GeminiModelClient` | Google Gemini native API |

Most providers are OpenAI-compatible; only Anthropic and Gemini need bespoke clients. Gemini also exposes a compatibility endpoint, but the native client is preferred for features the shim doesn't cover.

Model identifiers are **provider-qualified strings**: `anthropic/claude-opus-5`, `openrouter/deepseek/deepseek-r1`. The `CHARTER_MODEL_*` env vars accept this form.

### 20b.2 `CredentialGrant`

```
CredentialGrant   id, org_id, owner_user_id,
                  kind(anthropic_oauth | anthropic_api_key | openai_oauth |
                       openai_api_key | google_api_key | xai_api_key |
                       openrouter_key | custom_openai_compatible),
                  base_url,                     -- for self-hosted / proxied endpoints
                  scope(personal | shared_pool),
                  secret_encrypted, refresh_token_encrypted, expires_at,
                  status(active | exhausted | invalid | revoked),
                  exhausted_until, priority,
                  max_sessions_per_day_from_others,
                  created_at, last_used_at
```

**Handling rules:**

- Encrypt at rest with a **dedicated** `CHARTER_CREDENTIAL_KEY`, not `CHARTER_SECRET_KEY`. Rotating cookie signing must not invalidate every stored credential.
- Never log a token, never return one to the UI after creation — show provider, owner, status, last used.
- The control plane owns OAuth refresh. **Runners receive a short-TTL access token only, never a refresh token.** Consistent with §7.4.
- Revocation is immediate and kills in-flight sessions using that grant.

### 20b.3 Resolution chain

Resolved **per session**, in order, skipping anything `exhausted` or `invalid`:

1. Requester's own linked subscription credential
2. Remaining extra/overflow usage on that credential
3. Org shared pool, by `priority` — subscription grants explicitly opted in by their owners
4. Org metered API key
5. OpenRouter

If everything is exhausted, the session goes to `Queued` with the earliest `exhausted_until` shown as *waiting for capacity* — it does **not** fail.

### 20b.4 Exhaustion and failover

- A `429` marks the grant `exhausted` and records `exhausted_until` from the reset header. Do not blind-retry.
- **Never fail over mid-session.** A session that swaps models halfway produces incoherent work — half the reasoning came from a different model with different conventions. On mid-session exhaustion, checkpoint and either (a) pause and resume at reset, or (b) restart the current step under the next credential. Configurable per repo; default is pause-and-resume.
- Failover between *sessions* is free and silent.

### 20b.5 Consent and attribution

Subscription pooling means one person's quota gets spent on another person's request. That needs real consent mechanics:

- Opting a personal credential into `shared_pool` is an explicit action, never a default.
- The owner sees who used it, for what, and how much.
- `max_sessions_per_day_from_others` caps exposure.
- One-click withdrawal from the pool.

**Ledger units are not all dollars.** A subscription-backed session has no marginal cost but consumes scarce quota. Track both: `cost_usd` for metered grants, `quota_sessions` against the owner for subscription grants. Reporting a subscription session as `$0.00` makes budget dashboards lie.

### 20b.6 OpenRouter specifics

- Fetch the model catalog and per-token pricing from OpenRouter's models endpoint; cache it. The budget estimator cannot work with a hardcoded price table when the model is user-selectable.
- Per-repo and per-task model overrides in `.charter/config.yml` — cheap model for refinement, strong model for build.
- **Constraint to document clearly:** OpenRouter gives full model freedom for *control-plane* calls. For *agent runs*, model choice is limited to what the agent CLI supports. Claude Code can be pointed at a compatible gateway via `ANTHROPIC_BASE_URL`; Codex accepts OpenAI-compatible endpoints. Anything beyond that needs a shim, and the README should not imply otherwise.

### 20b.7 Terms-of-service caution

Using a personal Claude subscription for a *single user's own* agent runs is ordinary Claude Code usage. Routing *other people's* requests through it via `shared_pool` is closer to account sharing, and consumer plan terms may prohibit it.

Charter should:
- Surface a one-time warning when a user opts a subscription credential into a shared pool.
- Default `shared_pool` to off.
- Document the distinction in `docs/credentials.md` and tell operators to check their provider's terms.

This is the operator's call to make, but Charter should not make it silently on their behalf.

---

## 21. Auth

Behind a single `IIdentityProvider` seam **from the start**. Retrofitting SAML into an app that assumed OAuth is a genuinely miserable week.

- Email/password (default, always available)
- GitHub OAuth
- Google OAuth
- Discord OAuth
- Slack OAuth
- SAML SSO (org mode only)

Slack and Discord OAuth pull double duty: the identity link maps a Slack/Discord user to a Charter requester, which is what makes **inbound** requests from those platforms work.

---

## 22. Notifications

One outbound abstraction, per-user channel preference. Channels: **Email, Slack, Discord**.

Only the two notify-worthy states fire (§6).

**The bigger win is inbound.** Filing a request from Slack or Discord via slash command. The request that actually gets filed is the one someone can file without opening a new tab.

---

## 23. Build order

**Phase 1 — Refinement only, no agent.**
Request → refinement conversation → spec confirmation → approval queue. Shippable on its own, contains the actual novelty, de-risks everything downstream.

**Phase 2 — Execution.**
`IAgentRunner`, **Charter Agent (§33) and `GitHubActionsRunner` together**, capability matching, session orchestration, event streaming, SignalR, pane 2.

The Agent is built in Phase 2, not deferred. It is required for every non-web project type, it is the fastest backend, and its outbound job-claim protocol cannot be retrofitted onto a push-based dispatcher without a rewrite.

**Phase 3 — The loop closes.**
PR creation, preview URL binding, "what to check", feedback buttons, engineer recap.

**Phase 4 — Legibility and control.**
Teaching, concept ledger, pane 3, budgets, triage rules, `DockerRunner`.

**Phase 5 — Reach.**
Slack/Discord inbound, SAML, remote Docker socket support, demo mode polish.

---

## 24. Repository conventions

- **License:** AGPL-3.0-only (`LICENSE` at root). Network-use copyleft: anyone offering a modified Charter as a service must publish their modifications. Internal self-hosting is unaffected.
- **CLA required** (`CLA.md` + [CLA Assistant](https://github.com/cla-assistant/cla-assistant) GitHub Action). Apache-ICLA style: contributors retain copyright and grant the maintainer a perpetual, sublicensable license. **This is what preserves the ability to dual-license or run a commercial hosted version.** Without it, accepting a single external PR forecloses both permanently.
- **`TRADEMARK.md`** — the Charter name and logo are not licensed under the AGPL. Forks must rebrand. In practice this, not the license, is what prevents a competing hosted "Charter."
- **Note the AGPL adoption cost:** some large organisations prohibit AGPL software internally. Accepted tradeoff.
- **`CONTRIBUTING.md`** must set contribution expectations up front. This ecosystem's drive-by contributors are overwhelmingly TypeScript/Python; expect few external PRs, and a queue of unreviewed stranger patches looks worse than none.
- **`SECURITY.md`** with a contact address and the §16 threat model.
- **A named maintainer who is not the original author.** An abandoned repo — stale issues, unanswered security reports — is worse for recruiting than no repo at all.
- **README with a ~40 second GIF of the requester flow.** For a recruiting-oriented repo this does more than the architecture docs. Budget real time for it.
- **AGPL §13 compliance is a code requirement, not just a file.** Charter is network-interactive software, so the running instance must offer users a way to obtain its Corresponding Source. Ship a persistent "Source" link in the UI footer pointing at the instance's own version — including any operator modifications. Build the commit SHA and source URL in at compile time.
- **Independent versioning** for the DB schema and the `.charter/` schema. Migrations must run cleanly on a six-month-old instance.
- **Commit attribution.** Every commit is authored solely by the maintainer. No AI-attribution trailers, co-author lines, generated-with footers, or references to any assistant or model in commit messages, PR titles, or PR descriptions. Commit messages describe the change. Conventional Commits.
- **`docs/` versus `agent-docs/`.** `docs/` is user-facing documentation shipped to operators and contributors. `agent-docs/` holds briefs, planning notes, and specifications written for engineers and coding agents. Never mix them.

---

## 25. Open questions

- Retry semantics when a runner dies mid-session — resume from last event, or restart clean?
- Whether the Charter Agent should be able to hold long-lived local credentials for private registries, or always receive them per-job from the control plane.
- Multi-repo requests (a change spanning frontend and API repos) — out of scope for v1, but does the data model need to not preclude it?
- `Event` table partitioning strategy at scale.

---

## 26. Organisation standards and new projects

Two connected features: a declarative statement of how this organisation builds software, and a flow for starting new projects that conform to it.

### 26.1 Where standards live

In a **designated standards repo**, not in Charter's database:

```
charter-standards/            # org-designated repo
  standards.yml
  templates/
    dotnet-web/               # GitHub template repo reference
    dotnet-worker/
  policies/
    security.md
```

Same principle as §8: changing a guardrail requires a pull request and a review. An admin cannot silently loosen the org's engineering standards from a settings page.

### 26.2 standards.yml

```yaml
version: 1
stacks:
  web:
    backend:   { runtime: "dotnet", version: "10", required: true }
    frontend:  { framework: "react", bundler: "vite", ui: "shadcn/ui" }
    database:  { engine: "postgres", min_version: "16" }
    template:  "org/template-dotnet-web"
services:
  ai:      { provider: "openrouter", required: true }
  storage: { provider: "s3-compatible", required: true }
  hosting: { provider: "railway" }
  vcs:     { provider: "git", host: "github" }
required_files:
  - ".charter/config.yml"
  - "README.md"
  - ".github/workflows/ci.yml"
conventions:
  branch: "main"
  commits: "conventional"
deviations:
  requires_role: "admin"
  must_be_justified: true
```

### 26.3 Three consumers, one file

Standards are not just a scaffolding input. They feed:

1. **Project scaffolding** (§26.4) — the stack is chosen, not asked.
2. **The spec refiner** — refinement must not propose a library or service outside policy. This is the highest-value consumer and costs nothing extra: inject standards into the refinement context.
3. **Drift audit** — an on-demand pass over existing repos reporting where they diverge. Report only; never auto-remediate.

### 26.4 New project flow

1. **Propose** (Plan mode) — anyone may propose. *"We need an internal tool for tracking permit statuses."*
2. **Planning conversation** — produces a **Project Charter**: problem, users, in-scope, explicitly out-of-scope, rough data model, integrations, and a stack section auto-populated from `standards.yml`.
3. **Approve** — admin or engineer only. **Requesters may propose; they may not create.** Without this gate you accumulate forty abandoned repos in a quarter.
4. **Scaffold** — Charter creates the repo from the org's template, applies standards, generates `.charter/config.yml`, commits the Project Charter to `.charter/charter.md`, and opens the initial PR.
5. **Provision** — hosting project, database, and preview environments per `services`. Secrets are never generated by Charter; it emits a checklist of what a human must set.
6. **Onboard** — falls straight into the existing §9 flow, ending at the smoke test.

### 26.5 Templates and generation

Both, with a declared preference order. Templates give consistency; generation gives coverage. An org building its first Unity project or ESP32 firmware has no template yet, and refusing to help until someone writes one is a dead end.

```yaml
scaffolding:
  policy: template_preferred    # template_required | template_preferred | generation_allowed
  harvest: true
```

| Policy | Behaviour |
|---|---|
| `template_required` | Only project types with a template can be created. Maximum consistency, no coverage for new ground. |
| `template_preferred` | **Default.** Use a template when one exists; generate when none does, then offer to harvest it. |
| `generation_allowed` | Always allow generation, even where a template exists. |

**Template harvesting is the mechanism that makes this converge.** The first Unity project gets generated from scratch — slow and expensive. Once it's reviewed and working, Charter offers to extract it into a template repo, parameterising names and stripping project-specific content. The second Unity project is a template instantiation. The org's template library grows out of real, working projects rather than someone finding a free afternoon to write one.

Harvesting requires engineer approval and produces a PR against the standards repo, same as any other guardrail change.

**When generating**, the agent still receives `standards.yml` — a generated project is not an unconstrained one. And generated scaffolds are marked as such in `.charter/config.yml` so drift audits can flag them for template promotion.

### 26.6 Deviations

Standards are **defaults plus justified exceptions**, not walls. A project needing MongoDB records:

```yaml
deviations:
  - rule: "services.database.engine"
    value: "mongodb"
    justification: "Ingesting heterogeneous telematics payloads..."
    approved_by: "user_id"
    approved_at: "2026-08-10T00:00:00Z"
```

Committed to the project's `.charter/config.yml`, surfaced in drift audits, never silent.

### 26.7 Versioning

`standards.yml` is versioned, and each repo **pins the version it was created under**. Tightening standards must not retroactively mark every existing repo non-compliant. Drift audits report against the pinned version, with an optional "compare to latest" view.

### 26.8 Risks

- **Repo creation is a privilege escalation.** The GitHub App needs org-level repo creation scope. Document it prominently; make it an opt-in permission, and keep the standards repo and templates outside the agent's write scope.
- **Cost.** Scaffolding plus first build is far more expensive than a feature tweak. Separate budget line and a separate cap.
- **Naming.** The product is Charter; the document a project begins with is its charter. Intentional — but be consistent in the UI: *Project Charter* always capitalised, never bare "charter".

### 26.9 Sandbox organisation

New projects should not be born in the production org. Charter supports a **two-org model**:

```yaml
github:
  sandbox_org: "acme-labs"
  production_org: "acme"
  create_in: sandbox
  promotion_requires_role: admin
```

New repos are created in the sandbox org. Experiments that go nowhere die there without polluting the main org, and the blast radius of repo-creation permissions stays contained.

**Promotion** transfers the repo to the production org. GitHub repo transfer preserves history, issues, and PRs, and redirects old URLs — but several things **do not transfer** and must be re-applied automatically:

- Branch protection rules and rulesets
- CODEOWNERS enforcement (the file moves; the requirement to honour it is a repo setting)
- Repository secrets and variables
- Webhooks
- GitHub App installation — the App must already be installed on the target org

Promotion is therefore a checklist, not a button:

1. Verify App installation on target org
2. Transfer repository
3. Re-apply branch protection, rulesets, CODEOWNERS enforcement
4. Re-create secrets (Charter emits a list; **humans set values**)
5. Relink the hosting project
6. **Re-run the §9 smoke test** — the repo is not requester-visible in its new home until it passes again

### 26.10 Repo creation permissions

Repo creation is a privilege escalation and is gated three ways:

1. **Instance opt-in** — `CHARTER_ALLOW_REPO_CREATION=false` by default.
2. **GitHub App scope** — org-level repo creation is a separate permission the operator must grant deliberately.
3. **Role** — a distinct `can_create_repo` capability, admin-only by default and grantable to engineers.

Requesters may **propose** projects in Plan mode. They may not create them. The standards repo and template repos are outside every agent's write scope, always.

---

## 27. Project types beyond web

**An assumption is baked into §11, §18, and the entire requester loop: that a change can be verified by clicking a URL.** That holds for web apps and nothing else. Embedded firmware, Unity games, WinForms/WPF desktop apps, iOS/macOS apps, MAUI, Blazor, Expo, GNSS receivers, IoT devices, and game servers all break it.

Charter must generalise **"preview environment"** into **"verification artifact"** — whatever lets a human judge whether the change is right.

### 27.1 Verification artifacts

```
VerificationArtifact {
  session_id
  kind
  url | file_ref | connect_string
  instructions_md       -- how to actually use this thing
  expires_at
  audience(requester | engineer_only)
}
```

| Kind | What it is | Typical project types |
|---|---|---|
| `hosted_preview` | Ephemeral deployed URL | Web, API, Blazor Server |
| `build_artifact` | Downloadable binary — APK, IPA, .exe, .app, .elf, .uf2 | Desktop, embedded, sideloadable mobile |
| `distribution_channel` | TestFlight, Play Internal Testing, Firebase App Distribution, Expo EAS Update | iOS, Android, Expo |
| `capture` | Screenshots or video from a simulator, emulator, UI automation run, or Unity play-mode | WinForms, WPF, MAUI, Unity, mobile |
| `ephemeral_instance` | Running server plus a connect string | Game servers, MQTT brokers, gRPC services |
| `test_report` | Structured pass/fail plus logs and captured signals | Libraries, firmware, GNSS |
| `hil_report` | Hardware-in-the-loop run against a real device | Embedded, IoT |
| `none` | Engineer review only | Anything unverifiable automatically |

A session may produce several. Expo, for example, yields an EAS Update channel *and* a simulator capture.

### 27.2 Project types in standards

```yaml
project_types:
  web:         { verification: [hosted_preview],                    runner: [linux] }
  api:         { verification: [hosted_preview, test_report],       runner: [linux] }
  mobile_ios:  { verification: [distribution_channel, capture],     runner: [macos, xcode] }
  mobile_expo: { verification: [distribution_channel, capture],     runner: [linux, macos] }
  desktop_win: { verification: [build_artifact, capture],           runner: [windows] }
  desktop_mac: { verification: [build_artifact, capture],           runner: [macos, signing] }
  maui:        { verification: [build_artifact, capture],           runner: [windows, macos] }
  unity:       { verification: [build_artifact, capture],           runner: [linux, unity_license, gpu] }
  game_server: { verification: [ephemeral_instance, test_report],   runner: [linux] }
  embedded:    { verification: [test_report, hil_report],           runner: [linux, toolchain, usb_device] }
  library:     { verification: [test_report],                       runner: [linux] }
```

### 27.3 Runner capability matching

Sessions declare required capabilities; runners advertise what they have; the dispatcher matches. A session with no eligible runner **queues with a clear explanation** rather than failing.

```
Runner advertises: ["linux", "docker", "dotnet:10", "node:22"]
Session requires:  ["macos", "xcode:16"]
→ queued: "No runner available with macOS and Xcode. Register one in Settings → Runners."
```

**Consequence: `DetachedRunner` moves from Phase 5 to mandatory for these project types.** See also §32.5 — it is also by far the fastest backend, because toolchains and caches persist between sessions. GitHub Actions offers macOS and Windows runners, but it cannot offer a physical STM32 on a USB port, a GNSS receiver under a live sky view, or a Unity licence someone has paid for. Anything hardware-attached requires a runner on the operator's own machine. Design the capability schema and the job-claim protocol in Phase 2, as already noted in §23.

### 27.4 What degrades, stated plainly

The core promise — a non-engineer evaluates the change themselves — is strongest for web and mobile, weaker for desktop and games, and largely absent for firmware.

| Class | Requester experience |
|---|---|
| Web, API | Click a link. Full loop. |
| Mobile | Install a build. Full loop, with a delay and an install step. |
| Desktop, Unity | Screenshots or a video clip. Good enough for UI changes, poor for interaction. |
| Game servers | Connect string, if they know how to use it. Usually engineer-mediated. |
| Embedded, GNSS | Test report. Effectively engineer-only. |

**Do not pretend parity.** For `audience: engineer_only` artifacts, the requester's thread should say so honestly — *this kind of change is verified by an engineer; you'll be told when it's live* — and the engineer recap (§14) becomes the primary review surface rather than a supplement. Charter is still useful there: refinement, standards, scoping, and recap all apply. Only the click-to-verify loop is missing.

### 27.5 Practical consequences

- **Build times are minutes to an hour**, not seconds. The no-ETA rule (§6) matters more, streaming matters more, and **budgets need a wall-clock cap** alongside the token cap. Aggressive dependency caching is not optional.
- **Signing identity is never agent-accessible.** iOS certificates, macOS notarisation credentials, Android keystores, and code-signing certs are human-provisioned secrets held by the runner environment. The agent may trigger a signed build; it may never read the signing material.
- **Licence-bound toolchains** (Unity, some embedded IDEs) require the operator to supply credentials to their own runner. Charter ships no licences.
- **Artifact storage** must go to S3-compatible object storage, not Postgres. An IPA is not a database row. `expires_at` and a pruning job are mandatory or storage costs run away.
- **`.charter/config.yml` gains a `verification` block** per project, defaulting from the project type but overridable.

### 27.6 What this does not change

Everything structural survives: refinement, the spec object, scoping, the approval gate, the ledger, teaching, and the recap are all verification-agnostic. Only §18 changes shape — from *bind a preview URL* to *produce and bind whatever artifact this project type verifies with*.

### 27.7 The verification artifact card

The single most-looked-at component in Charter. It is the payoff for the entire pipeline, and it must read well to someone who has never seen a pull request.

#### Anatomy

```
┌────────────────────────────────────────────────┐
│ ● Ready to try            expires in 5h 12m    │   status + expiry
│                                                │
│ Remember last selected vertical                │   spec title
│                                                │
│ ┌────────────────────────────────────────────┐ │
│ │  [ kind-specific body ]                    │ │   polymorphic
│ └────────────────────────────────────────────┘ │
│                                                │
│ ┌──────────────────┐  ┌──────┐                 │
│ │  Open preview  → │  │  ⧉   │                 │   primary + secondary
│ └──────────────────┘  └──────┘                 │
│                                                │
│ What to check                                  │
│  ☐ Vertical is pre-selected on return          │   from acceptance_criteria
│  ☐ New quotes still default to Solar           │
│                                                │
│ ┌────────────┐ ┌──────────────┐                │
│ │  Works ✓   │ │  Not quite   │                │
│ └────────────┘ └──────────────┘                │
│                                                │
│ ▸ Details            PR #142 · a3f9c21 · 12m   │   engineers only, collapsed
└────────────────────────────────────────────────┘
```

#### Kind-specific bodies

| Kind | Body | Primary action |
|---|---|---|
| `hosted_preview` | URL chip (truncated, copyable), reachability dot, QR code for phone testing | **Open preview** |
| `build_artifact` | Platform icon, filename, size, short checksum, collapsible install instructions | **Download** |
| `distribution_channel` | Channel and build number ("TestFlight · Build 42"), QR, note if an invite is needed | **Open in TestFlight** (deep link) |
| `capture` | Inline image carousel or video player; before/after toggle when a baseline exists | **View full size** |
| `ephemeral_instance` | Connect string with copy button, protocol, region, expiry | **Copy connect string** |
| `test_report` | Pass/fail bar, counts, expandable list of failures with assertion text | **View full report** |
| `hil_report` | Device identifier, run duration, pass/fail, captured traces or scope output | **View run** |
| `none` | Plain explanation that this change type is engineer-verified | *(none)* |

#### States

| State | Rendering |
|---|---|
| `pending` | Skeleton, elapsed timer, current milestone. **Never an ETA.** |
| `ready` | Full card as above |
| `expiring` | Under 1h remaining — countdown turns amber |
| `expired` | Body replaced by *this preview has been cleaned up*, primary action becomes **Rebuild** |
| `failed` | Plain-language failure line; engineers additionally see a link to the recap |

**Expired previews are the number one source of confusion in tools like this** — someone opens a link from a three-day-old notification, gets a dead host, and reports it as broken. The countdown must be visible from first render, and expiry must be a designed state rather than a 404.

#### Rules

- **Multiple artifacts** per session render as tabs within one card, primary first. Never as separate cards — the requester must not have to work out which one is real.
- **Audience gating.** The `Details` disclosure (PR number, commit SHA, branch, runner, duration, cost) renders only for users with repo read. Requesters never see a SHA.
- **QR code for anything installable or mobile-testable.** Highest-value small feature in this component: it removes the "email myself the link" step entirely.
- **Pass/fail must not rely on colour alone** — pair every state with an icon and a text label.
- **Mobile:** full-bleed card, primary action pinned as a sticky footer button, checklist collapsed by default.
- The **"What to check"** list is rendered from `acceptance_criteria` verbatim (§10b). It is not regenerated, because it is the contract the requester approved.

#### Component contract

```ts
type ArtifactKind =
  | 'hosted_preview' | 'build_artifact' | 'distribution_channel'
  | 'capture' | 'ephemeral_instance' | 'test_report'
  | 'hil_report' | 'none';

interface VerificationArtifact {
  id: string;
  kind: ArtifactKind;
  state: 'pending' | 'ready' | 'expiring' | 'expired' | 'failed';
  audience: 'requester' | 'engineer_only';
  expiresAt?: string;
  payload: HostedPreview | BuildArtifact | Capture | TestReport;
  details?: EngineerDetails;      // omitted server-side when unauthorised
}
```

`details` is **omitted by the API**, not hidden by CSS. Authorisation is not a rendering concern.

---

## 28. Update notification

Self-hosted software that silently rots is a security liability. Charter checks for new releases and tells the people who can act on it.

### Mechanism

- Poll the GitHub Releases API for the Charter repo, compare `tag_name` against the compiled-in build version (§24).
- **Daily, with jitter**, result cached in Postgres. Unauthenticated GitHub API allows 60 requests/hour per IP; a daily check is far inside that.
- Failures — offline, air-gapped, rate-limited — degrade **silently**. Never log an error every day on an instance with no internet; that is how operators learn to ignore logs.

### Presentation

- Visible to **admins and engineers only.** A requester has no action to take and should never see it.
- Persistent but unobtrusive: a badge in settings and a dismissible banner. Dismissal is **per version** — it returns for the next release.
- Render the release notes inline (markdown, sanitised) with a link to the full release.
- **Security releases are distinct.** Flag them by a marker in the release (`[SECURITY]` prefix or a `security` label), render them in a persistent, non-dismissible style, and state the severity plainly.
- **Warn when the upgrade includes schema migrations**, so an operator knows a backup is warranted before pulling.

### Privacy

This is the **only outbound request Charter makes on its own initiative.** It must be documented as such, in both the README and `docs/privacy.md`:

> Charter checks GitHub once a day for a new release. It sends no data about your instance — it is an unauthenticated read of a public endpoint. GitHub will see the request's source IP, as with any HTTP request. Disable with `CHARTER_UPDATE_CHECK=false`.

Configuration:

| Variable | Default | Notes |
|---|---|---|
| `CHARTER_UPDATE_CHECK` | `true` | Set `false` for air-gapped or privacy-strict deployments |
| `CHARTER_UPDATE_CHANNEL` | `stable` | `stable` \| `prerelease` |

**The default is on.** An operator silently running a version with a known vulnerability is a far worse outcome than one outbound request a day. It must be a single flag to turn off, and the disclosure belongs in `docs/privacy.md` — a dedicated page, linked from the README, not a buried footnote. People looking for this go hunting for a privacy document; "we have one" reads very differently from "there's a sentence in the feature list."

`docs/privacy.md` has exactly three sections: what Charter never collects, the single outbound call and how to disable it, and where observability data goes.

---

## 29. Repository deliverables

Create every file below. Several are drafted already and should be used as-is or lightly adapted; the rest are to be written from this specification.

### Root

| File | Status | Contents |
|---|---|---|
| `README.md` | **drafted** | Use the provided draft. Fill the demo GIF, repo URLs, and security contact. |
| `LICENSE` | **provided** | Verbatim AGPL-3.0 plain text, unmodified. Do not reformat, do not convert to markdown. |
| `CLA.md` | **drafted** | Use the provided draft. |
| `TRADEMARK.md` | **drafted** | Use the provided draft. |
| `SECURITY.md` | write | Supported versions, private reporting instructions, response expectations, and the §16 threat model summary. Lead with the fact that the agent never sees raw user input. |
| `CONTRIBUTING.md` | write | Dev environment setup, running tests, the CLA requirement and that it is automated, "open an issue before anything large", commit conventions, and the project's current stance on scope. |
| `CODE_OF_CONDUCT.md` | write | Contributor Covenant 2.1, with a real contact address. |
| `CHANGELOG.md` | write | Keep a Changelog format, semver. Seed with an `Unreleased` section. |
| `.env.example` | write | Every variable from §4.2, commented, with safe example values. **Never a real secret.** Required variables uncommented; optional ones commented out. |
| `docker-compose.yml` | write | Two services per §2.1: `charter-app`, `postgres`. Healthchecks, volume for Postgres only. |
| `Dockerfile` | write | Multi-stage: SDK build → runtime. Non-root user. Build version and commit SHA injected as build args for §24 and §28. Node stage builds `ClientApp` (§3.1). |
| `.dockerignore`, `.gitignore`, `.editorconfig` | write | Standard for .NET + Node. |
| `Directory.Build.props` | write | Shared .NET properties: nullable enabled, warnings as errors, deterministic builds. |
| `AGENTS.md` | write | Single source of truth for agent guidance, tool-agnostic. Under ~200 lines. |
| `CLAUDE.md` | write | Thin pointer to `AGENTS.md`. Duplicates nothing. |

### `agent-docs/`

Briefs, planning notes, and specifications for engineers and coding agents. Not shipped documentation.

| File | Contents |
|---|---|
| `agent-docs/spec.md` | This document. |

### `docs/`

User-facing documentation only.

| File | Contents |
|---|---|
| `docs/configuration.md` | Full environment variable reference from §4.2, grouped by concern, with the `DATABASE_URL` parsing rules from §4.3. |
| `docs/privacy.md` | Three sections per §28: what is never collected, the single outbound call and its off switch, where observability data goes. |
| `docs/self-hosting.md` | Compose, Railway, Render, Fly. PaaS constraints from §2.3 and why the runner backend differs per platform. |
| `docs/runners.md` | Runner backends, capability advertisement and matching (§27.3), registering a detached runner, and which project types require one. |
| `docs/adapters.md` | The adapter YAML schema from §12b, how to add one, and the model × adapter compatibility rules. |
| `docs/credentials.md` | Providers, the resolution chain (§20b.3), shared pools, and the terms-of-service caution in §20b.7. |
| `docs/standards.md` | `standards.yml` schema, project types, deviations, template harvesting. |
| `docs/charter-folder.md` | The `.charter/` layout from §8, every file, versioning and forward-compatibility rules. |
| `docs/security.md` | Full threat model. `SECURITY.md` at root stays short and links here. |
| `docs/upgrading.md` | Migration policy, backup guidance, breaking-change conventions. |

### `.github/`

| File | Contents |
|---|---|
| `workflows/ci.yml` | Build, test, lint both projects. Run EF migrations against a throwaway Postgres service. |
| `workflows/release.yml` | Tag → build and publish container image → GitHub Release with generated notes. Tag format must match what §28 parses. |
| `workflows/agent-session.yml` | The `repository_dispatch` workflow the `GitHubActionsRunner` triggers. |
| `ISSUE_TEMPLATE/bug_report.yml`, `feature_request.yml` | Structured forms. |
| `pull_request_template.md` | |
| `CODEOWNERS` | |
| `dependabot.yml` | |

### `.claude/`

Committed shared configuration. `settings.local.json` and `**/local/` are gitignored.

| Path | Contents |
|---|---|
| `settings.json` | Conservative permission allowlist — build, test, lint, git. No blanket shell access. |
| `commands/spec-check.md` | Verify recent changes against the relevant spec section; report divergences without fixing them. |
| `commands/phase-status.md` | Report progress against the §23 build order. |
| `commands/new-adapter.md` | Scaffold an agent adapter YAML per §12b. |
| `commands/new-migration.md` | Create an EF Core migration and classify it per §15. |
| `agents/spec-reviewer.md` | Checks implementation against this specification. |
| `agents/docs-writer.md` | Writes and maintains the §29 documentation set. |

### Writing rules for all documentation

- **Second person, present tense.** "Set `DATABASE_URL` to…" not "The user should set…"
- **Lead with the thing the reader came for.** Rationale after instructions, never before.
- **Every code block must be copy-pasteable and correct.** No `<placeholder>` inside a command a reader would run verbatim without noticing.
- **State limitations plainly** where they exist — degraded verification for embedded (§27.4), adapters without structured output (§12b), AGPL adoption friction (§24). A docs set that oversells gets discovered and costs more trust than the limitation itself.
- **No emoji in documentation.** The README may use them sparingly.

---

## 30. First-run and onboarding

Charter is empty on day one and has four different people arriving at it for the first time. Each needs a different first five minutes.

### 30.1 Instance first run

**Security-critical.** A self-hosted app that boots with open registration gets hijacked by whoever finds it first.

1. On boot with zero users, Charter enters **setup mode** and serves nothing but the setup route.
2. A **one-time setup token** is generated and written to stdout — not a default password, not an open form. The operator reads it from container logs.
3. The token creates exactly one admin account, then expires. Setup mode ends permanently and cannot be re-entered while a user exists.
4. Preflight checks run and display results: database reachable, migrations applied, `CHARTER_BASE_URL` resolves, at least one model credential valid, secret keys of sufficient length.

If preflight fails, say which check failed and what to change. Never boot into a half-working state that fails later on first use.

### 30.2 Admin onboarding

A **persistent setup checklist** on the dashboard, not a modal wizard. Resumable, showing progress, dismissible once complete:

- [ ] Name your organisation
- [ ] Connect GitHub *(App install)*
- [ ] Add a model credential
- [ ] Connect your first repository → hands off to §9
- [ ] Set budgets
- [ ] Invite people
- [ ] Choose notification channels

Modal wizards trap people who need to go find a token. A checklist lets them leave and come back.

### 30.3 Engineer onboarding

Lands on the repo they were invited to. First run offers:

- Review the proposed scope config from recon (§9)
- Choose an agent adapter and verify it is installed
- Register a runner if the project type needs one (§27.3)
- Watch the smoke test run end to end — **this is the demo**; it is how an engineer decides whether to trust the tool
- Set their pane preference

### 30.4 Requester onboarding

The highest-stakes and shortest. Three screens maximum:

1. **What this is** — one paragraph, plus the repo primer (§8) for the project they've been given access to.
2. **How much explaining do you want?** — the teaching calibration from §13, asked once, changeable later.
3. **A practice request** — guided, against a demo repo or a real one, taken all the way to a verification artifact.

The practice request matters more than anything else here. A requester who has completed the full loop once, including clicking a preview, will file real requests. One who has only read about it will not.

### 30.5 Empty states

Every list view has a designed empty state that tells the user the single next action. No blank tables, no lonely spinner. This is the entire product on day one and is routinely left until last.

### 30.6 Demo mode

`CHARTER_DEMO=true` seeds a fake organisation, repo, and completed sessions with realistic transcripts and artifacts, and disables all outbound calls.

This exists so someone evaluating Charter can see the product **without connecting a GitHub App or spending a token**. For an open-source project, the gap between `docker compose up` and understanding what the thing does is the single biggest adoption cliff.

---

## 31. Deferred features

Not in v1, but the data model should not preclude them. Ordered by value.

| Feature | Why it matters |
|---|---|
| **Search** across requests, specs, and sessions | Conspicuously absent from v1. Becomes essential around request 50. |
| **Duplicate detection at intake** | Non-engineers will file the same request repeatedly. Semantic match against open and completed requests before refinement starts, saving the cost of building it twice. |
| **Demand signal** | "Me too" on an existing request. Approvers need to see that eleven people want a thing; currently they see eleven separate requests. |
| **Revert as a first-class request type** | A merged change turns out wrong. Today that means filing a fresh request describing the undo. It should be one button producing a revert PR. |
| **Internal analytics** | Not phone-home — the org's own dashboard: requests filed, resolved in chat without a build, cycle time, spend by team, spec revision counts. **This is how the tool justifies itself to a sceptical CFO**, and it's how you prove teaching reduces rework. |
| **Backlog import** | Pull existing GitHub Issues or Linear issues in as Requests, so the org doesn't start from an empty list. |
| **Digest notifications** | Weekly summary as an alternative to per-event notifications, for people who want visibility without interruption. |
| **Outbound webhooks + public REST API + CLI** | Lets orgs integrate Charter rather than adopt it wholesale. The CLI in particular is what engineers will actually want. |
| **Request aging and escalation** | Specs sitting unapproved for two weeks are the silent failure mode. Surface them; nudge the approver. |
| **i18n and timezone handling** | Charter's likely users are distributed teams. Timestamps and date formatting should be locale-aware from the start even if translation comes later — retrofitting timezone correctness is miserable. |
| **Session replay** | Scrub an event stream at speed for engineers debugging agent behaviour. |
| **Blast-radius estimate before dispatch** | Predicted files touched, shown to the approver alongside cost. Improves approval decisions considerably. |

### Operational essentials that are not optional

These are v1, not deferred, and are easy to forget:

- `/health` and `/ready` endpoints — PaaS platforms require them.
- **Rate limiting at intake** — per user and per org. An enthusiastic requester with a script should not be able to queue 400 sessions.
- **Backup and restore documentation** — what to dump, what is safe to lose, how to verify a restore.
- **Graceful shutdown** — drain in-flight work, release advisory locks, mark claimed jobs for retry.

---

## 32. Runner provisioning and caching

A runner that reinstalls .NET, Node, and npm packages on every session wastes minutes and money per request and makes the whole product feel slow. Toolchains are **provisioned ahead of time**; sessions assume them.

### 32.1 Prebuilt runner images

Charter publishes versioned base images to GHCR, and orgs can build their own from the documented Dockerfiles in `runners/`.

| Image | Contains |
|---|---|
| `charter-runner-base` | git, curl, jq, the agent CLIs, the event-streaming shim |
| `charter-runner-dotnet` | base + .NET SDK 10, NuGet warm cache |
| `charter-runner-node` | base + Node 22, pnpm/npm |
| `charter-runner-fullstack` | base + .NET + Node — the common case for this stack |
| `charter-runner-python` | base + uv, Python 3.12 |
| `charter-runner-embedded` | base + arm-none-eabi, OpenOCD, probe-rs, udev rules |
| `charter-runner-unity` | base + Unity Hub *(licence supplied by operator)* |

`.charter/config.yml` already carries `runner_image`. **A session never installs a language runtime.** If the image lacks a declared requirement, the session fails fast with an actionable message rather than silently apt-getting its way to a working state.

### 32.2 Capability probing at registration

When a runner registers, it **probes and reports** rather than being told what it has:

```
dotnet --list-sdks     → "dotnet:10.0.100"
node --version         → "node:22.11.0"
xcodebuild -version    → "xcode:16.2"
lsusb / probe-rs list  → "usb_device:stm32f4"
```

Charter stores the resulting capability set and matches sessions against it (§27.3). Re-probe on runner restart and daily — a Mac mini that got an Xcode update should not silently keep advertising the old version.

### 32.3 Persistent caches

Mounted per session, persisted across sessions, **scoped per repository**:

| Cache | Path |
|---|---|
| NuGet | `~/.nuget/packages` |
| npm / pnpm | `~/.npm`, pnpm store |
| Cargo | `~/.cargo/registry` |
| Gradle / Maven | `~/.gradle`, `~/.m2` |
| Go | `~/go/pkg/mod` |
| Build output | `obj/`, `bin/`, `node_modules` — opt-in, see below |

**Scoping is a security requirement, not an optimisation.** A cache shared across repositories is a cross-repo contamination path: a poisoned transitive dependency pulled in one repo persists into another. Sandbox-org and production-org caches are never shared either.

Package caches are safe to share across sessions of the same repo. **Build-output caches are opt-in** (`cache.build_output: true`) because stale intermediates produce failures that look like agent errors and burn a review cycle before anyone suspects the cache.

### 32.4 Git mirrors

Maintain a **bare mirror per repository** on each runner. Sessions `fetch` and create a worktree rather than cloning from scratch. On a large repo this alone is the difference between twenty seconds and several minutes, and it composes naturally with the worktree isolation already in the design.

### 32.5 Backend-specific behaviour

| Backend | Warm state |
|---|---|
| `DetachedRunner` | **Best case.** Long-lived machine, tools installed once by the operator, caches and mirrors persist on local disk indefinitely. |
| `DockerRunner` | Image is warm; caches and mirrors live in named volumes on the host. |
| `GitHubActionsRunner` | **Worst case.** Fresh VM per run. Mitigate with a container image on the job (`container:` key) so no runtime installs are needed, plus `actions/cache` keyed on lockfile hashes. Accept that this backend is inherently slower and say so in `docs/runners.md`. |

This is a further argument for `DetachedRunner`: it is not only required for hardware access (§27.3), it is substantially faster for everything.

### 32.6 Repo-specific setup

For dependencies no shared image can carry — a native library, a private feed, a codegen step:

```yaml
setup:
  run: "apt-get install -y libgpiod-dev && dotnet restore"
  cache_key: "packages.lock.json"
```

Runs once per `cache_key` value and is skipped on subsequent sessions while the key is unchanged. Repeated identical setup work is the thing to eliminate; a one-time cost per lockfile change is fine.

### 32.7 Invalidation and escape hatches

- Cache keys derive from **lockfile hashes**; a lockfile change invalidates naturally.
- **TTL** on unused caches, plus a size cap with LRU eviction. Unbounded caches fill a self-hoster's disk and they will blame Charter.
- **Manual purge**, per repo and per runner, in the admin UI.
- **"Cold run"** option on any session — ignore all caches and rebuild from scratch. Essential for diagnosing whether a failure is real or a stale cache, and the first thing to try when a session fails inexplicably.

### 32.8 What Charter never installs

Licences and signing identities are always operator-provisioned: Unity licences, Apple developer certificates, notarisation credentials, Android keystores, private registry tokens. Charter's images contain toolchains, never entitlements.

---

## 33. Charter Agent

The Portainer model: a lightweight daemon on each execution host that holds the local Docker socket (or runs jobs natively) and maintains an **outbound** connection back to the control plane.

### 33.1 Why outbound-only

```
   ┌──────────────────┐                    ┌──────────────────┐
   │  Control plane   │                    │  charter-agent   │
   │  (Railway, etc)  │ ◀───── wss ─────── │  (your Mac mini, │
   │                  │   agent dials out  │   Proxmox, VPS)  │
   └──────────────────┘                    └────────┬─────────┘
                                                    │ local only
                                           ┌────────▼─────────┐
                                           │  docker.sock     │
                                           │  or native host  │
                                           └──────────────────┘
```

- **No inbound ports, no port forwarding, no firewall changes.** Works behind NAT, CGNAT, and a corporate firewall.
- **The Docker socket never leaves the host.** Compare this with exposing the Docker API over TCP: even with mTLS, a network-reachable Docker daemon is root-equivalent access to that host and a permanent target. Charter supports it for completeness but `docs/runners.md` must recommend against it in plain language.
- The control plane needs no privileges on the execution host and no knowledge of its network.

### 33.2 Execution modes

```
charter-agent --mode docker    # spawn ephemeral containers via local socket
charter-agent --mode native    # run jobs directly on the host
```

`native` exists because containers are not universally possible: **macOS with Xcode cannot be containerised**, and USB-attached embedded targets are awkward to pass through. In native mode the agent runs jobs under a **dedicated unprivileged user account** with a scoped working directory.

**Isolation in native mode is weaker, and the docs must say so.** It is process-level, not container-level. Recommend a dedicated machine or VM for native agents rather than an engineer's daily driver.

### 33.3 Registration and lifecycle

1. Admin generates a **pairing token** in the UI, single-use, short-TTL.
2. `charter-agent --server https://charter.example.com --token <pairing>` — the agent dials out, exchanges the pairing token for a long-lived agent credential, and registers.
3. The agent **probes and reports capabilities** (§32.2), plus its mode, version, and resource limits.
4. Heartbeat on an interval; missed heartbeats mark it offline and its in-flight jobs are re-queued after a lease timeout.
5. **Revocable instantly** from the UI — revocation kills in-flight jobs and invalidates the credential.

### 33.4 Job claiming

- The agent **claims** work; the control plane never pushes. This is what allows outbound-only.
- Claims carry a **lease with a TTL**, renewed by heartbeat. A crashed agent's jobs return to the queue automatically.
- Claims are filtered by capability, so an agent only ever sees jobs it can actually run.
- Concurrency limit per agent, configurable, defaulting conservatively.

### 33.5 Secrets

Unchanged from §7.4 and reinforced here:

- The agent receives a **short-TTL, single-repo GitHub installation token** and a scoped model credential, **per job**.
- It never receives refresh tokens, the control plane's environment, or credentials for repositories other than the one in the job.
- Signing identities, licences, and registry tokens are configured **locally on the agent host** by the operator and are never transmitted by the control plane (§32.8).

### 33.6 Version compatibility

Agent and control plane negotiate a protocol version on connect. Mismatch produces a clear message and a refusal to claim work, rather than subtle failures three sessions later. The agent auto-updates only if the operator opts in; the default is to warn and let them upgrade deliberately.

### 33.7 Distribution

Single static binary per platform (linux/amd64, linux/arm64, darwin/arm64, windows/amd64), published to GitHub Releases, plus a container image for the Docker mode. Installation is one command; that is the adoption bar for a companion daemon.

---

## 34. Budgets and cost governance

A single monthly cap per user is the wrong shape for any organisation that spends seriously on AI. Budgets need to express real internal structure: departments, cost centres, projects with their own funding, one-off pushes, and people who should be trusted with far more than the default.

### 34.1 Two currencies

Never conflate them (§20b.5):

| Unit | Source | Behaviour |
|---|---|---|
| `usd` | Metered API keys and OpenRouter | Real marginal cost |
| `quota_sessions` | Subscription-backed credentials | No marginal cost, but a scarce shared resource |

Budgets can be denominated in either. A subscription-heavy org may cap `quota_sessions` per person while leaving `usd` uncapped, or the reverse. Reporting always shows both, plus an **imputed USD** figure for subscription sessions — what the same work would have cost on metered API — so the two are comparable and the value of the subscription is visible.

### 34.2 Budget object

```
Budget   id, org_id, name,
         scope_type(org | team | repo | project | user | role | tag),
         scope_id,
         unit(usd | quota_sessions),
         categories[],              -- build|refine|teach|recap|recon|scaffold|chat, or all
         period(daily | weekly | monthly | quarterly | rolling_30d | fiscal_year | one_off),
         period_anchor,             -- fiscal year start, billing day
         amount,
         behaviour(block | warn | require_approval | downgrade_model | queue_until_reset),
         approval_threshold,        -- spend above this needs an approver, below flows
         rollover(none | full | capped),
         rollover_cap,
         reserved_amount,           -- guaranteed floor before pooled spend is touched
         starts_at, ends_at,        -- one-off budgets and campaigns
         alert_thresholds[]         -- e.g. [0.5, 0.75, 0.9, 1.0]
```

### 34.3 Nesting semantics

**A session must have headroom in *every* applicable budget.** Not most-specific-wins — all of them, evaluated together. A user with $200 remaining inside a team whose pool is exhausted cannot spend.

```
Org: $5,000/mo
 └─ Team "Ops Tooling": $1,500/mo
     └─ Repo "spectra": $800/mo
         └─ User "ayesha": $200/mo, reserved $50
```

`reserved_amount` guarantees a floor: Ayesha always has $50 even if the team pool is drained by others. Above her reserve she competes for the shared pool. This is the pattern large teams actually need — everyone gets a guaranteed minimum, and the rest is first-come.

### 34.4 Reserve, then settle

Concurrency-safe accounting, borrowed from payment authorisation:

1. **Estimate** before dispatch — from the spec's scope, historical cost for similar work in this repo, and the selected model's price.
2. **Reserve** the estimate against every applicable budget, inside a transaction with row locks.
3. **Settle** on completion, releasing the difference between estimate and actual.
4. **Release** on cancellation or failure.

Without holds, ten concurrent sessions each pass the check and collectively blow the cap. Reservations expire on a TTL so a crashed orchestrator doesn't strand budget.

Estimates improve over time: store actual-versus-estimate per repo and per project type and use the running distribution rather than a fixed heuristic.

### 34.5 Behaviours at the limit

Blocking is the crudest option and rarely the right default:

| Behaviour | Effect |
|---|---|
| `warn` | Proceeds; notifies the budget owner |
| `require_approval` | Falls back to the approval queue instead of failing (§7.5) |
| `downgrade_model` | Routes to a cheaper model tier and labels the session accordingly |
| `queue_until_reset` | Holds the session until the period rolls over, showing the date |
| `block` | Refuses, with the exact figure and who can raise it |

`require_approval` is the best default for an org that spends freely: work doesn't stop, it just acquires a human decision above a threshold.

**Every limit message names who can raise it.** A dead end that doesn't say who to ask is the fastest way to make people stop using the tool.

### 34.6 Categories

Budgets can target specific cost categories, which is what protects the cheap-but-valuable work:

- **Chat should be generously funded or uncapped.** It is by far the cheapest way to resolve a request, and a chat that answers *it already does that* saves an entire build. Rationing chat pushes people straight to building. This is the single most important budget default in the product.
- **Teaching is its own line** (§13). It is the first thing an admin cuts when it's bundled with build spend, because it has no immediately visible output.
- **Scaffolding** is far more expensive than a feature tweak (§26.8) and deserves separate authorisation.

### 34.7 Overrides and top-ups

- **One-off top-up** — a named amount, added to a specific budget, with a reason and an audit entry.
- **Emergency override** — admin bypasses a budget for a defined window. Always time-boxed, always audited, never permanent. A permanent override is just a higher budget, and should be edited as one.
- **Campaign budgets** — `one_off` with `starts_at`/`ends_at`, for a push that shouldn't distort the recurring baseline.

### 34.8 Visibility

- **Burn rate and projection** — spend to date, run rate, projected period end, days of headroom remaining. A number without a trajectory is not actionable.
- **Showback by scope** — cost by team, repo, project, requester, category. This is also the raw material for §31's internal analytics.
- **Cost on the artifact.** Every session shows what it cost, visible to engineers and admins. Requesters see cost only where a budget is scoped to them personally — otherwise it's noise that makes people hesitate to ask.
- **Alerts** at configured thresholds, to the budget owner, over their chosen channel (§22).
- **Display currency** is configurable per org. Provider billing is USD; conversion is presentational only, with the rate and its date shown.

### 34.9 Defaults

| Mode | Default |
|---|---|
| Personal | No budgets. One person, their own credentials, nothing to govern. |
| Organization | Org-level `require_approval` above a modest per-session threshold. No per-user budgets until an admin adds them. |

Ship with governance available but not imposed. An org that wants structure builds it; an org that doesn't is never blocked by a limit it didn't set.
