# Charter — Change Spec 001

**Status:** Draft — complete, pending implementation review
**Amends:** `agent-docs/spec.md` v1
**Scope:** Multi-provider version control · two-factor authentication · email delivery · configuration in database · work management, sprints, design mode, analytics, external issue sync · multimodal input and artifacts · public API

Ten parts. A–D are infrastructure. E–H expand what Charter *is*, and are separable — see the scope note below. Part I is multimodal input, small enough to land early. Part J is the public API, which should be built into the product rather than bolted on afterwards.

## Contents

| Part | Subject | Block |
|---|---|---|
| A | Version control providers | Later |
| B | Two-factor authentication | Infrastructure |
| C | Email delivery | Infrastructure |
| D | Configuration in the database | Infrastructure |
| E | Work items and agile planning | E.1 infrastructure, rest deferred |
| F | Design mode | Deferred |
| G | Analytics | Deferred |
| H | External issue synchronisation | H one-way early, inbound last |
| I | Multimodal input and artifacts | I.3 / I.6 early |
| J | Public API | Continuous — see J.10 |

---

## Implementation discipline — read before starting anything

This change spec describes many providers, adapters, and backends. **Almost none of them are built early.**

### The rule: build the seam, ship one implementation

Every pluggable point gets its interface defined up front, because retrofitting a seam is expensive. Every one of them ships with exactly **one** concrete implementation until the core loop works end to end in real use.

| Seam | Interface exists from | Phase 1 implementation | Everything else |
|---|---|---|---|
| `IVersionControlProvider` | Phase 1 | **GitHub only** | Part A, later |
| `IDeploymentProvider` | Phase 1 | **Railway only** | Generic webhook, later |
| `IAgentRunner` | Phase 1 | **Charter Agent only** (§33) | `CiDispatchRunner` and `DockerRunner` later |
| `IModelClient` | Phase 1 | **`OpenAiCompatibleModelClient`, pointed at OpenRouter** | Native Anthropic and Gemini clients later |
| Agent adapter | Phase 1 | **Claude Code + Pi** — see note | Codex, Gemini, opencode, Cursor later |
| `IEmailProvider` | Phase 1 | **One only** | The other, later |
| `IIdentityProvider` | Phase 1 | **Email/password + GitHub OAuth** | Google, Discord, Slack, SAML later |

### Why Charter Agent first

Reversing the earlier ordering. The Agent is the flagship runner, not the fallback, and shipping it first is better on three counts:

- **It is the only runner that works everywhere** — every VCS provider, every project type, hardware-attached or not (§33, §27.3). Building the CI-dispatch runner first optimises for the configuration that is least representative of real use.
- **Toolchains and caches persist** (§32.5), so it is substantially faster for everything.
- **It makes local development simpler, not harder.** The Agent dials *outbound* to the control plane, so a developer running Charter on `localhost` needs no tunnel for runner traffic. A CI-dispatch runner requires a publicly reachable endpoint for its event callbacks, which means running a tunnel throughout development.

The cost is honest: Phase 1 now includes a daemon — pairing, lease-based claiming, heartbeat, capability probing, event streaming, and both `docker` and `native` modes. That is real work that `repository_dispatch` would have avoided. It buys a runner that does not need replacing later.

Ship the Agent as a **single-file .NET binary** so the project stays one language and one toolchain. Version-control webhooks are still required for change-request and deployment state, so a tunnel is still needed during local development — just for a much smaller surface.

### Why OpenRouter first

`OpenAiCompatibleModelClient` against OpenRouter is a *simpler* Phase 1 than a native first-party client, not a more complex one. One implementation reaches OpenRouter, OpenAI, xAI, Groq, DeepSeek, Moonshot, and local endpoints — including Claude models routed through OpenRouter. The native first-party clients become later optimisations rather than prerequisites.

**But note which surface this covers.** Per §20b, Charter consumes models in two places:

| Surface | Calls | OpenRouter in Phase 1? |
|---|---|---|
| Control plane | refinement, teaching, recap, recon | **Yes, directly.** Any OpenRouter model works immediately. |
| Agent runs | the actual build | **Depends on the adapter.** |

Claude Code authenticates against its own provider or a compatible gateway; pointing it at OpenRouter requires a proxy and is finicky. **Pi is provider-agnostic by design** and reaches OpenRouter natively, which is what makes Kimi, DeepSeek, or GLM usable for the expensive surface — builds — rather than only for refinement.

Shipping both adapters in Phase 1 does not violate the one-implementation rule, because **adapters are declarative YAML, not code** (§12b). A second adapter is configuration and a test, not a second code path.

Cost note worth acting on: builds are where the money goes. Refinement and teaching are cheaper per call but higher volume. Routing builds to a cheap model via Pi and keeping a stronger model for recap is the configuration that actually moves the bill.

### Validating a seam without building a second implementation

An interface with one implementation is usually shaped like that implementation. Before considering a seam done, **write down on paper how the second provider would map to it** — a page, not code. GitLab against `IVersionControlProvider`, Render against `IDeploymentProvider`, Codex against the adapter schema.

If the mapping requires changing the interface, change it now. This costs an hour and it is the only cheap moment to find out that an abstraction is provider-shaped.

Two known traps, both already flagged: do not assume a change request is a branch (§A.7), and do not assume the runner is reachable inbound (§33.1).

### Phase 1 is a vertical slice, not a layer

Phase 1 is done when this works end to end, for one web repository, on GitHub, with Railway previews:

```
request → refinement → spec → approval → session → PR → Railway preview
        → "what to check" → Works / Not quite
```

Not "the data model is done." Not "the API is done." The whole loop, working, for one person, once. Everything in this change spec and the v1 specification is scaffolding around that path, and nothing else is worth building until it runs.

### Explicitly out of scope for Phase 1

`CiDispatchRunner` (GitHub Actions) · `DockerRunner` · any non-GitHub provider · any non-Railway deployment target · native first-party model clients · adapters beyond Claude Code and Pi · non-web project types · work management in any mode · design mode · analytics · MCP server · SDKs · CLI · 2FA · attachments of any kind.

Charter Agent `native` mode ships in Phase 1 only if a project type needs it; otherwise `docker` mode alone is sufficient for web repos and `native` follows in Phase 2.

Each of these has a home in a later phase. None of them make the Phase 1 loop work, and every one of them delays finding out whether the loop is any good.

---

## Scope note — read before planning E–H

Parts E through H turn Charter from a request-to-PR pipeline into a work management platform. That is a defensible direction, but it should be entered deliberately.

**The thesis that makes it coherent:** Charter already produces the artifact that other trackers are merely containers for — a scoped unit of work with acceptance criteria, an owner, and a cost. Every other tool requires a human to write that by hand. In Charter the backlog is a *byproduct of refinement*, and each item arrives with a build path already attached. That is a real difference, not a feature-list expansion.

**The risk:** "we also added issues and boards" is a well-populated graveyard. Most teams will not migrate off their existing tracker, and a weaker version of one is worse than none. The differentiator is not the board — it is that Charter knows what an item *cost*, what an agent *did*, and whether the requester could *verify* it. No planning tool can answer those.

**Therefore:** the work item model (§E.1) is foundational and should be built early, because retrofitting it is expensive. The planning *surfaces* — boards, sprints, burndown — should follow the core loop being proven, and every one of them must work in `synced` mode against an external tool, not only in `native` mode.

---

## Part A — Version control providers

### A.1 Problem

The v1 specification hardcodes GitHub throughout: GitHub App authentication, pull requests, `repository_dispatch`, branch protection, CODEOWNERS, repo transfer. Charter is unusable on GitLab, Gitea, Bitbucket, Azure DevOps, or a bare Git remote, and unusable for the Perforce-based studios implied by §27's Unity support.

### A.2 `IVersionControlProvider`

Every GitHub-specific operation moves behind an interface:

```
IVersionControlProvider
  Capabilities                      -- see A.3
  AuthenticateRepo(repoConfig)      -> scoped, short-TTL credential
  Clone / Fetch / Worktree
  CreateBranch, Push
  OpenChangeRequest(...)            -> ChangeRequest
  CommentOnChangeRequest(...)       -- engineer recap lands here
  GetChangeRequestState(...)
  RegisterWebhook(...)
  CreateRepository(...)             -- optional
  TransferRepository(...)           -- optional
  ApplyBranchProtection(...)        -- optional
```

`PullRequest` in the data model is renamed **`ChangeRequest`**, with a provider-supplied display term so the UI reads correctly: *pull request* on GitHub and Gitea, *merge request* on GitLab, *changelist* on Perforce.

### A.3 Capability model

Providers differ enormously. Charter must degrade explicitly rather than assume.

```
Capabilities {
  change_requests        bool
  webhooks               bool
  app_style_auth         bool     -- scoped installation tokens vs long-lived PAT
  branch_protection      bool
  code_owners            bool
  repo_creation          bool
  repo_transfer          bool
  ci_dispatch            bool     -- can trigger provider-native CI
  merge_gate_enforcement enum     -- provider_enforced | advisory
}
```

### A.4 Providers

| Provider | Change requests | App auth | Protection | Notes |
|---|---|---|---|---|
| **GitHub** / GHES | PR | GitHub App | yes | Reference implementation |
| **GitLab** SaaS / self-managed | MR | Project/group access token | yes | CODEOWNERS is a paid tier — detect and warn |
| **Gitea / Forgejo** | PR | OAuth2 app or token | yes | Important for self-hosters; likely the second most-used |
| **Bitbucket** Cloud / DC | PR | App password / OAuth | yes | |
| **Azure DevOps** | PR | PAT / Entra | branch policies | |
| **Generic Git remote** | **none** | SSH key / token | **none** | Fallback — see A.6 |
| **Perforce Helix Core** | shelved changelist | ticket auth | none native | Deferred — see A.7 |

### A.5 The merge gate, restated

**This is the most important part of Part A.**

Specification §7.4 states that Charter has no merge button and that merge authority lives in provider-side branch protection, outside Charter's trust boundary. That guarantee is only as strong as the provider makes it.

- Where `merge_gate_enforcement = provider_enforced`, the v1 guarantee holds unchanged.
- Where it is `advisory`, **Charter must say so loudly** — at repo onboarding, in the repo settings, and in `docs/security.md`. The wording should be plain: *this provider cannot enforce review; nothing stops a person from merging agent-written code without review. Charter will not do it, but Charter cannot prevent it either.*

A repo whose provider is advisory-only is still usable. It simply carries a different risk posture, and the operator must be told rather than left to assume the v1 guarantee applies.

Onboarding (§9) gains a step: **verify protection is actually configured**, not merely supported. A GitHub repo with no branch protection rule is functionally advisory too, and should be flagged as such.

### A.6 Generic Git fallback

For a plain remote with no code-review surface, Charter pushes the branch and produces the review artifact itself: the diff in pane 3, the engineer recap in the session view rather than as a comment, and a downloadable patch or bundle.

This is a real degradation and is documented as one. It exists so Charter works against a self-hosted bare repo, not as a recommended configuration.

### A.7 Perforce — deferred, with a design note

Genuinely different, not merely a different API: centralised, no cheap branching, streams instead of branches, shelved changelists instead of pull requests, and workspace state that matters.

The mapping that would work: session → workspace, agent changes → shelved changelist, review → the shelf, "merge" → submit by a human. Runner requirements grow (a persistent workspace per repo, which suits the Charter Agent's long-lived hosts).

Not in this change spec. Recorded so the `IVersionControlProvider` shape doesn't foreclose it — specifically, **do not assume branches are cheap or that a change request is a branch**.

### A.8 Consequence for runners

`GitHubActionsRunner` becomes `CiDispatchRunner` with provider-specific implementations (GitHub Actions, GitLab CI, Gitea Actions, Azure Pipelines), available only where `ci_dispatch` is supported.

**The Charter Agent (§33) works with every provider.** This makes it not merely the fastest backend but the universal one, and strengthens its Phase 1 position.

---

## Part B — Two-factor authentication

### B.1 Independent toggles

2FA is configured **per authentication method**, not globally. An organisation whose IdP already enforces MFA should not force a second factor on top of SSO, while still requiring one for local accounts.

```yaml
auth:
  email_password:
    enabled: true
    require_2fa: true          # independent
  oauth:
    github:   { enabled: true, require_2fa: false }
    google:   { enabled: true, require_2fa: false }
    discord:  { enabled: false }
    slack:    { enabled: true, require_2fa: false }
  saml:
    enabled: true
    require_2fa: false         # IdP typically handles this
  policy:
    require_2fa_for_roles: [admin, engineer]
    enrollment_grace_days: 7
    step_up_for_sensitive_actions: true
```

`require_2fa_for_roles` composes with the per-method setting: a factor is required if **either** the method demands it or the user holds a listed role.

### B.2 Factors

- **TOTP** (RFC 6238) — baseline, works everywhere
- **WebAuthn / passkeys** — offered and preferred where available; phishing-resistant in a way TOTP is not
- **Recovery codes** — ten single-use, shown once, regenerable

SMS is not supported. It is weaker than the alternatives and adds a delivery dependency for no security gain.

### B.3 Enrolment and recovery

- Grace period on first login, with a persistent prompt; access is blocked once it expires.
- Lost device → admin-initiated reset, always audited, always notified to the affected user by email.
- **The last remaining admin cannot lock themselves out**: if an admin has no second factor enrolled and no other admin exists, the reset path is the `CHARTER_RECOVERY_TOKEN` bootstrap mechanism (§D.7), not a support request to nobody.

### B.4 Step-up authentication

Re-prompt for a factor before genuinely sensitive actions, regardless of session age:

- Creating or revoking a model credential
- Granting `can_create_repo` or changing an auto-dispatch policy
- Budget override or emergency bypass
- Changing authentication settings
- Revoking a Charter Agent

These are the actions where a stolen session cookie does real damage.

---

## Part C — Email delivery

### C.1 `IEmailProvider`

| Provider | Configuration |
|---|---|
| `resend` | API key |
| `smtp` | Host, port, credentials, TLS mode — covers SES, Postmark, Mailgun, and self-hosted |
| `none` | Email disabled |

Email is required for invitations, notifications, 2FA recovery, and password reset. Under `none`, Charter must degrade cleanly rather than fail: admins create users directly with a one-time link surfaced in the UI, and email-dependent settings are disabled with an explanation rather than silently failing to send.

### C.2 Configuration

```yaml
email:
  provider: resend            # resend | smtp | none
  from_address: "charter@example.com"
  from_name: "Charter"
  reply_to: null
  resend:
    api_key: <secret>
  smtp:
    url: <secret>             # smtp://user:pass@host:port
    tls: starttls             # none | starttls | implicit
```

### C.3 Requirements

- **Send a test email** from the settings UI. Email misconfiguration is otherwise discovered when an invitation silently fails and a new hire cannot log in.
- Templates in both HTML and plain text.
- Delivery failures are logged and surfaced in admin settings, never swallowed.
- Rate-limit outbound mail per recipient to prevent notification storms.

---

## Part D — Configuration in the database

The largest change. It supersedes §4 in part.

### D.1 The dividing line

Two tiers, and the boundary is not arbitrary.

**Bootstrap configuration — environment only, immutable at runtime.** Required before the database can be read, or logically circular to store there.

| Variable | Why it must be env |
|---|---|
| `DATABASE_URL` | Needed to reach the database at all |
| `CHARTER_SECRET_KEY` | Signs sessions; needed before any user context |
| `CHARTER_CREDENTIAL_KEY` | Decrypts stored secrets — cannot live inside what it decrypts |
| `CHARTER_BASE_URL` | Needed for OAuth callbacks during first-run |
| `PORT` | Platform contract |

**Runtime configuration — database, UI-editable.** Everything else: model selection, adapters, auth providers, email, notifications, budgets, update checks, log levels, retention, feature toggles.

### D.2 Environment as seed and lock

Environment variables retain two roles:

- **Seed.** On first boot, any recognised variable populates the corresponding database setting. Existing deployments upgrade with no manual re-entry.
- **Lock.** `CHARTER_LOCKED_SETTINGS=email.provider,auth.saml.enabled` pins settings to their environment values. The UI renders them read-only with *set by environment*. This is what makes Charter viable for GitOps-managed and regulated deployments.

After first boot, unlocked environment variables are ignored. Seeding happens once; drift between env and database is otherwise a permanent source of confusion.

### D.3 The settings UI is the flagship

**Every setting must be reachable and editable in the UI.** Nothing is raw-editor-only. If a setting is too awkward to present in a form, that is a signal the setting is wrong, not that it belongs in YAML.

Requirements: grouped by concern, searchable, inline help explaining consequence rather than restating the field name, and validation on the field rather than only on save.

### D.4 The raw editor is a power tool

A YAML/JSON view of the same tree, for bulk edits, diffing, and copying configuration between instances.

- **Lossless round-trip** with the UI. Same underlying document.
- **Schema-validated** with inline errors before apply.
- **Diff preview** against current state before applying — this is the difference between a power tool and a footgun.
- **Secrets redacted on read.** Write-only: a secret can be set but never rendered back.

### D.5 Versioning and audit

Every change creates a version: actor, timestamp, structured diff, optional note. Full history, and **one-click rollback** to any prior version.

Configuration now governs authentication and spending. It needs the same audit treatment as any other security-relevant action.

### D.6 Apply semantics

- Each setting is marked **hot** (takes effect immediately) or **restart-required**, and the UI says which.
- Validation runs before persistence. A configuration that fails validation is never stored.
- **Last-known-good** is retained. If the current configuration fails to load at boot, Charter starts on the last-known-good and shows a prominent banner rather than refusing to start.

### D.7 Lockout prevention

Configuration can now break authentication. Three guards:

1. **Charter refuses to disable the last authentication method that has an active admin.** Enforced server-side, not in the UI.
2. **Changing authentication settings requires step-up auth** (§B.4).
3. **`CHARTER_CONFIG_SAFE_MODE=true`** boots ignoring database configuration, using environment and defaults only, and enables a `CHARTER_RECOVERY_TOKEN` bootstrap login. This is the escape hatch for an operator who has locked themselves out. Document it in `docs/self-hosting.md` under a heading people can find while panicking.

### D.8 Export and import

Full configuration export as YAML, secrets excluded, for backup and for standing up staging alongside production. Import validates against the schema and shows a diff before applying.

---

## Part E — Work items and agile planning

### E.1 The work item model

`Request` is generalised into `WorkItem`. A request is one way an item is born; a bug report, a planning conversation, or a synced external issue are others.

```
WorkItem   id, org_id, project_id, key,          -- human key, e.g. SPEC-142
           type(story | bug | task | spike | epic | initiative),
           title, description_md,
           parent_id,                            -- max 3 levels: initiative > epic > story
           status_id, workflow_id,
           reporter_id, assignee_type(human | agent), assignee_id,
           priority, estimate, estimate_unit(points | hours),
           labels[], sprint_id,
           origin(request | chat | plan | design | sync | manual),
           external_ref,                         -- sync linkage
           spec_id,                              -- the refined spec, if any
           created_at, updated_at
```

`Request` and `Spec` from v1 §5 survive: a `WorkItem` of type `story` may have a `Spec`, and a `Spec` may have sessions. The chain becomes **WorkItem → Spec → Session → ChangeRequest → VerificationArtifact**.

### E.2 Status is derived, not maintained

**The most valuable difference from existing trackers.** In those tools a human drags a card because the tool cannot know what is happening. Charter knows.

| Signal | Derived status |
|---|---|
| Session running | In progress |
| Change request open, checks green | In review |
| Verification artifact ready, unverified | Ready to test |
| Requester marked *Works* | Verified |
| Merged | Done |

Manual override is always available and always recorded as a manual override. Nobody should ever have to tell Charter that work it is currently performing is in progress.

Workflows are configurable per project but ship with a short default. Resist unbounded configurability; that is where these products become unusable.

### E.3 Agents as assignees

Assigning an item to an agent dispatches a session, subject to §7.5 auto-dispatch policy and §34 budgets. Unassigning or reassigning to a human triggers the `take over` path from §7.5 and stops agent writes.

This is the cleanest expression of the whole product: **the backlog and the workforce are the same interface.**

### E.4 Sprints and cycles

```
Sprint   id, project_id, name, goal, starts_at, ends_at,
         human_capacity, human_capacity_unit,
         agent_budget_id,              -- a §34 budget scoped to this sprint
         status(planned | active | closed)
```

**Sprint capacity has two currencies.** Human capacity in points or hours, and an agent budget in USD or quota sessions. An agent-assisted sprint is constrained by spend as genuinely as by headcount, and no existing tool models that. Planning shows both, and commitment warns when either is exceeded.

Burndown plots both lines. A sprint that is on track for scope but has burned 90% of its agent budget by Wednesday is a real signal that no current tool surfaces.

### E.5 Boards

- **Sprint board** and **backlog**, with swimlanes by epic or assignee.
- **A simplified board for requesters** — their own items only, in the plain-language statuses of v1 §6, with no estimates or workflow vocabulary. The engineer board and the requester board are the same data at different densities.
- Agent-assigned cards show live session state and accrued cost inline.

### E.6 Creating items from Chat and Plan

Chat and Plan modes (v1 §10b) gain **promote to work item**. A planning conversation that produces three options and picks one should end by creating the item, with the conversation linked as context. Today that context is lost the moment the tab closes.

Items created this way carry `origin`, which feeds analytics: how much of the backlog originated in planning conversations versus ad-hoc requests.

---

## Part F — Design mode

A fourth mode alongside Chat, Plan, and Build, sitting between Plan and Build.

### F.1 Purpose

For anything user-facing, the expensive failure is building the wrong interface correctly. Design mode makes the visual decision before a build session starts, and attaches the outcome to the work item as an input to the spec.

### F.2 Scope for v1 — deliberately small

- Generate **static HTML/CSS prototypes** against the project's design tokens, rendered inline and iterated by conversation
- Accept **reference images** — screenshots, sketches, competitor examples — as input
- Produce a `DesignArtifact` attached to the work item, versioned
- The approved artifact is injected into the build spec as a constraint, alongside acceptance criteria

### F.3 What it is not

Not a design tool. No canvas, no vector editing, no component library management, no design-file import. Those are entire products, and attempting them would consume the roadmap.

If a team has real designers, the correct integration is **reference in, prototype out** — they work in their tool and attach outputs here.

### F.4 Constraint

Design mode reads the project's design tokens (its own design-system equivalent, or a token file in `.charter/`). A prototype that ignores the project's actual visual language is worse than no prototype, because it sets expectations the build cannot meet.

---

## Part G — Analytics

Local only. No phone-home (v1 §19) — this is the organisation's own data, shown to the organisation.

### G.1 Delivery metrics

Standard, and expected by anyone coming from an existing tracker: cycle time, lead time, throughput, WIP, flow efficiency, sprint predictability, carry-over rate, estimate accuracy.

These are table stakes. They are not why anyone would choose Charter.

### G.2 The metrics no other tool can produce

This cluster is the actual product of Part G, and it should be the default dashboard:

| Metric | Why it matters |
|---|---|
| **Resolved in chat** | Requests answered without a build. Pure saved cost, and the single best argument for the tool. |
| **Spec revision count** | How many refinement rounds before approval. Falls as glossary, primer, and teaching improve — this is how you prove teaching works. |
| **Agent rework rate** | Share of agent change requests needing a second session or human takeover. The honest quality number. |
| **Cost per merged change** | By repo, project type, and model. The number a CFO asks for. |
| **Requester verification rate** | Share of artifacts actually opened and marked by the requester. Measures whether the core loop is real or theatre. |
| **Human vs agent authorship** | Lines and change requests, over time. |
| **Deviation rate** | How often the agent departed from the approved spec (§14). |
| **Teaching engagement vs rework** | Correlate walkthrough reads against later spec quality for the same requester. |

### G.3 Surfaces

- **Persona dashboards.** Admin sees spend and adoption; engineer sees rework and review load; approver sees queue age and cost; requester sees only their own items.
- **Per-repo health** — drift from standards (§26.3), agent success rate, average cycle time.
- **Export** to CSV and a read-only SQL view. Analytics people will want their own tooling, and blocking that produces shadow spreadsheets.

### G.4 Honesty requirement

Metrics that make the tool look good must not be privileged in the default view. If agent rework rate is 40%, the dashboard shows 40% prominently. A tool that flatters itself gets distrusted the first time someone checks, and every number here is checkable.

---

## Part H — External issue synchronisation

Most organisations will not migrate. Being an excellent citizen alongside an existing tracker is worth more than being a worse replacement.

### H.1 Modes

```yaml
work_management:
  mode: off           # off (default) | synced | native
```

| Mode | Behaviour |
|---|---|
| `off` | **Default.** No work management. Requests and sessions only — v1 behaviour, unchanged. |
| `synced` | An external tool is the source of truth. Charter mirrors items, adds its own fields, and pushes status back. |
| `native` | Charter is the source of truth. Full Part E surfaces. |

### H.1a Off is the default, and stays first-class

`off` is not a degraded mode or a migration waypoint. It is the configuration most instances should run.

Requirements that follow from that:

- **No Part E vocabulary anywhere in `off` mode.** No sprint, epic, story point, backlog, or board appears in navigation, settings, empty states, or notifications. A five-person team must be able to use Charter for a year without learning that sprints exist.
- **No upsell.** Charter never prompts an `off`-mode instance to enable work management. It is offered once, during onboarding, and then only in settings.
- **Every core flow works identically in all three modes.** Request → spec → session → artifact → merge is unchanged. Work management adds a layer above it; it never sits inside the path.
- **Switching modes is non-destructive and reversible.** `off → synced` backfills. `synced → off` retains links and stops syncing. `native → off` hides surfaces without deleting items.

### H.1b The onboarding choice

Presented once, in the admin setup checklist (v1 §30.2), after the first repository is connected — not before. Someone who has not yet seen a request go through has no basis for the decision.

Three options, framed by what the organisation already does:

| Option | Copy |
|---|---|
| **Nothing, for now** *(default)* | *Requests and previews only. You can add planning later.* |
| **Connect what we already use** | *Keep your existing tracker as the source of truth. Charter pushes refined stories there and keeps status in sync.* |
| **Use Charter for planning** | *Boards, sprints, and backlog inside Charter.* |

The default is preselected and the step is skippable in one click. The middle option is the one most organisations should take, and the copy should make it the obvious read for anyone who names an existing tool — but Charter must not choose for them.

Re-offered only once, if an admin later connects an external tracker credential for another purpose.

### H.2 Integrations

**Jira and Linear are the priority integrations**, ahead of the VCS-native issue trackers. They are what teams who decline native work management are actually using, and `synced` mode is the intended path for most organisations — which makes these integrations more load-bearing than Part E's own surfaces.

| System | Priority | Direction |
|---|---|---|
| **Jira** | first | two-way |
| **Linear** | first | two-way |
| GitHub Issues / Projects | second | two-way |
| GitLab Issues | second | two-way |
| Gitea / Forgejo Issues | third | two-way |
| Azure Boards | third | two-way |

Each integration must map, at minimum: title, description, status, assignee, labels, and a link back to the Charter item. Charter-owned fields (§H.3) are pushed as a structured comment or custom fields where the target supports them, and never invented as new required fields in someone else's project schema.

Where the external tool supports agent delegation of its own, Charter must not fight it — if an external issue is delegated elsewhere, Charter mirrors the state rather than dispatching a competing session.

### H.3 Sync design

**Ship one-way first — Charter to external.** Two-way sync is the hardest thing in this category and the most common source of data corruption. One-way is genuinely useful on its own: a request refined in Charter appears in the tracker as a properly written story with acceptance criteria.

When inbound sync arrives:

- **Field-level ownership.** Each field has a declared owner; the non-owner's writes are ignored rather than merged.
- **Charter-owned always:** session state, cost, verification artifact, agent assignment. These have no external equivalent and must never be overwritten.
- **Idempotency keys** on every sync operation. Webhook redelivery must not duplicate items.
- **Conflicts are surfaced, not resolved silently.** A visible conflict is recoverable; a silent merge is not.
- **Loop prevention** — sync-originated writes must not re-trigger outbound sync.
- **Backfill** on connection, with a dry-run preview showing what would be created.

---

## Part I — Multimodal input and artifacts

### I.1 Why this matters more here than elsewhere

Charter's hardest problem is getting a precise specification out of someone who cannot describe software precisely. A screenshot with an arrow drawn on it, a ten-second screen recording of the bug, or thirty seconds of talking are all **higher-fidelity than the same person's written description** — and dramatically faster to produce.

This is not a convenience feature. It directly improves the input to refinement, which is the quality bottleneck for everything downstream.

### I.2 Attachment model

```
Attachment   id, org_id, uploaded_by,
             parent_type(request | work_item | session | spec | message),
             parent_id,
             kind(image | document | video | audio | archive),
             filename, mime_type, size_bytes,
             storage_key,              -- S3-compatible object storage, never Postgres
             extracted_text,           -- OCR / parse output, searchable
             derived_refs[],           -- keyframes, transcript, page renders
             processing_state, virus_scan_state,
             created_at, expires_at
```

Storage is the same S3-compatible bucket as verification artifacts (v1 §27.5). Blobs never go in Postgres.

### I.3 Images

The highest-value case and the easiest.

- Attached to a request, a chat message, a work item, or a session steering message
- Passed directly to the refiner as image content — every provider in the v1 §20b.1 client set supports images
- **Annotation before upload.** A lightweight draw layer — arrow, box, freehand, text — applied client-side on paste or drop. *"This button, here"* is worth more than three paragraphs, and it is the single cheapest UX win in this change spec.
- Paste from clipboard must work. Requesters take screenshots; they do not save files.

### I.4 Documents

PDFs, word processor documents, spreadsheets, CSVs.

- **Extract text server-side rather than passing raw files to the model.** Cheaper, provider-agnostic, and the extracted text becomes searchable.
- PDFs: text layer where present, page rasterisation where not, OCR as fallback
- Spreadsheets and CSVs: parse to structured text; sample large files rather than truncating arbitrarily
- The extracted text is what enters the refinement context, with page or sheet references preserved so the model can cite location

Common real uses: a requirements document, an exported report showing wrong numbers, a spreadsheet defining the calculation a feature should implement.

### I.5 Video

**Do not depend on native video input.**

Only some frontier families accept video as a first-class content type. Building the pipeline around that would tie a core Charter capability to one provider and break the `IModelClient` abstraction.

**Charter extracts, then reasons over the extraction:**

```
video -> ffmpeg keyframe extraction (scene-change detection, not fixed interval)
      -> deduplicated frames + timestamps
      -> speech-to-text transcript, if an audio track exists
      -> frames + transcript + manifest into the model
```

Scene-change extraction matters more than frame rate. A screen recording of a bug has perhaps six moments where anything changes; sampling at a fixed 1fps produces sixty near-identical frames and buries the signal.

This pipeline works with every provider, runs locally on a Charter Agent for privacy-sensitive deployments, and costs a fraction of streaming a full video into context.

**Where a provider does support native video** and the operator has selected it, allow it as an optimisation — configurable, never assumed.

**This also serves v1 §27.1.** The `capture` verification artifact — Unity play-mode recordings, WPF UI automation runs, simulator captures — uses the same extraction pipeline. A capture is evaluated by keyframes at the moments something changed, not by watching the whole clip. One implementation, two consumers.

### I.6 Voice input

Quick capture for chat, requests, and session steering. The mobile case especially: someone notices a problem while using the tool and should be able to describe it in fifteen seconds without typing.

- **Hold-to-record** in the composer, on every text input in Chat, Plan, and request creation
- Transcribed by a speech-to-text model — locally on a Charter Agent where available, otherwise a configured provider
- **The transcript is editable before sending.** Never send raw transcription output as a request; transcription errors become specification errors, and a requester who cannot see what was heard cannot correct it.
- Original audio retained as an attachment, subject to retention policy
- Language configured per user, since transcription quality degrades badly on the wrong language assumption

Voice is an **input accelerant, not a channel.** It produces text that enters the normal flow. There is no voice-only path, no spoken responses, and no separate conversation mode.

### I.7 Refinement with multimodal input

The refiner must reference attachments explicitly in its clarifying questions — *"in the screenshot, the total on the right is wrong — should it exclude tax?"* — and the resulting spec must record which attachment supported which acceptance criterion.

An attachment that informed a decision is part of the contract (v1 §10b) and must remain visible on the spec, not buried in the original request thread.

### I.8 Security

Attachments are **untrusted input from users**, which is the same threat surface as §16 with more file formats.

- **Virus scanning** before any processing; quarantine on failure
- **Parse in an isolated process** with resource limits. Image and PDF parsers have a long history of memory-safety issues, and this one runs on user-supplied files by design.
- **Strip EXIF** on upload — screenshots and phone photos routinely carry location data
- **Size and duration caps**, configurable, with sane defaults
- **Prompt injection via image text is real.** A screenshot containing instructions aimed at the model is a live vector, and it bypasses the text-based flagging in §16. Mitigation is unchanged and structural: the agent never sees the raw attachment, only the human-approved spec. State this explicitly in `docs/security.md`.
- Signed, expiring URLs for retrieval; never public bucket objects

### I.9 Configuration

```yaml
attachments:
  enabled: true
  max_size_mb: 100
  max_video_duration_seconds: 300
  allowed_kinds: [image, document, video, audio]
  retention_days: 365
  virus_scan: clamav          # clamav | none
transcription:
  provider: local             # local | hosted | none
  model: whisper-large-v3
video:
  extraction: scene_change    # scene_change | interval | native
  max_frames: 24
```

### I.10 Where it lands

**Images and voice ship early** — with Part C, before the work management parts. Both are small, both directly improve refinement quality, and refinement is the component every other part depends on.

Documents follow. Video last, since it shares the extraction pipeline with §27.1's `capture` artifact and is best built alongside it.

---

## Part J — Public API

### J.1 The rule that makes an API good

**The Charter web application consumes the public API and nothing else.** There are no private endpoints, no internal-only routes, no shortcuts the SPA takes that a consumer cannot.

Every other decision in this part is downstream of that one. An API the product itself depends on cannot silently rot, cannot lag behind features, and cannot have undocumented gaps — because the UI would break first. APIs built alongside a privileged internal interface always decay, and no amount of documentation discipline prevents it.

This is also what makes `off` mode credible (§H.1a): an organisation that declines Charter's own surfaces integrates through the same interface the UI uses.

### J.2 Shape

REST over JSON, with OpenAPI 3.1 generated from the .NET minimal API definitions — not hand-maintained. A hand-written spec drifts within two releases.

```
/api/v1/requests
/api/v1/work-items
/api/v1/specs
/api/v1/sessions
/api/v1/sessions/{id}/events        SSE stream
/api/v1/artifacts
/api/v1/repos
/api/v1/agents                      Charter Agents
/api/v1/budgets
/api/v1/settings
/api/v1/webhooks
```

Conventions, all of them boring on purpose:

- **Cursor pagination** — `?cursor=&limit=`, never offset. Offsets break under concurrent writes, which is Charter's normal state.
- **Expansion over N+1** — `?expand=spec,sessions.artifacts`, bounded in depth.
- **RFC 9457 Problem Details** for every error, with a stable `type` URI. .NET supports this natively; use it rather than inventing an error envelope.
- **Idempotency keys** required on every POST that creates or spends. Filing a request twice because a mobile connection dropped is unacceptable, and dispatching a session twice costs real money.
- **ETags and conditional requests** on mutable resources.
- **Standard rate-limit headers** on every response, not only on rejection.
- **ISO 8601 with offsets** for every timestamp. No epoch integers, no naive local times.

### J.3 Authentication

| Type | Use | Notes |
|---|---|---|
| **Personal access token** | Scripts, CLI, personal automation | Scoped, expiring, revocable, last-used tracked |
| **Service token** | Machine integrations | Not tied to a person; survives their departure |
| **OAuth 2.1 app** | Third-party applications | Authorisation code with PKCE |
| **Session cookie** | The web application | Same endpoints, different credential |

**Tokens scope down, never up.** A token can never exceed the permissions of the user or service that created it, and revoking the underlying identity revokes every token derived from it. Step-up-protected actions (§B.4) are unavailable to tokens entirely — a leaked personal access token must not be able to change authentication settings or override a budget.

### J.4 Streaming

Session events over **Server-Sent Events**, with `Last-Event-ID` resumption:

```
GET /api/v1/sessions/{id}/events
Accept: text/event-stream
```

SSE rather than WebSockets for the public interface: it survives proxies, needs no special client, resumes cleanly after a drop, and works from curl. SignalR remains available for the SPA, but the SSE stream is the contract and must carry the same events.

### J.5 Webhooks

Outbound delivery for every meaningful state change: spec approved, session started, artifact ready, change request opened, budget threshold crossed, session failed.

- **HMAC-SHA256 signatures** over the raw body, with a timestamp header and a replay window
- **Secret rotation** with an overlap period, so rotation is not an outage
- **Retries with exponential backoff and jitter**, then automatic disable after sustained failure, with notification
- **A delivery log in the UI** showing payload, response, status, and timing, with **one-click redelivery**. This is the difference between webhooks that are debuggable and webhooks that generate support tickets.
- **Per-endpoint event filtering**, so a consumer subscribes to what it needs
- Payloads carry an event ID and are documented as **at-least-once**; consumers are told to deduplicate

### J.6 MCP server

**The differentiated piece.** Charter exposes itself as an MCP server, so other agents and assistants can use it as a tool.

```
charter.list_requests        charter.get_session
charter.create_request       charter.get_artifact
charter.get_spec             charter.search
charter.approve_spec         charter.get_repo_primer
```

This is coherent with what Charter is. An engineer's coding agent should be able to ask *what has been requested against this repo and what did the last session change*. An assistant should be able to file a request on someone's behalf from wherever they already are.

Constraints, following from v1 §7:

- MCP tools respect the calling token's permissions exactly — **no ambient authority**
- `approve_spec` and anything that spends require an explicitly scoped token; they are not available by default
- Every MCP-originated action is attributed in the audit log to the human whose token was used, and marked as MCP-originated

### J.7 SDKs and CLI

- **Generated from OpenAPI**, published for TypeScript, .NET, and Python. Generated clients stay current; hand-written ones do not.
- **A CLI built on the public API**, distributed as a single binary. It is the most useful artifact for engineers and it is also the proof that §J.1 holds — a CLI that needs an internal endpoint has found a gap in the API.

### J.8 Documentation

- **Generated reference** from OpenAPI, always current
- **Hand-written guides** for the flows that matter: file a request, watch a session, receive a webhook, wire up the MCP server
- **Runnable examples** — curl for every endpoint, working code for each SDK
- **Demo mode** (v1 §30.6) doubles as an API sandbox: a fully populated instance to develop against without connecting a repository or spending a token

### J.9 Versioning

- `/api/v1` in the path. A breaking change means `/v2`, not a silent alteration.
- **Additive changes are not breaking** — clients must tolerate unknown fields, and this is stated in the documentation.
- **Deprecation policy in writing**: `Deprecation` and `Sunset` headers, a minimum notice period, and a changelog entry. Publish the policy before it is needed; retrofitting one during a deprecation looks arbitrary.

### J.10 Where it lands

The API is not a phase. **Every phase from Phase 2 onwards ships its endpoints as part of the feature**, because §J.1 makes that automatic — the UI cannot be built otherwise.

What is scheduled separately: OpenAPI publication and generated SDKs after Phase 3, webhooks alongside notifications, the MCP server after Phase 3, and the CLI once the surface has stabilised.

---

## Sections amended

| Section | Change |
|---|---|
| §2.2 | `GitHubActionsRunner` → `CiDispatchRunner`; Charter Agent noted as universal |
| §4 | Split into bootstrap (env) and runtime (database) tiers |
| §5 | `PullRequest` → `ChangeRequest`; add `Setting`, `SettingVersion`, `UserFactor`, `RecoveryCode` |
| §7.4 | Merge gate guarantee qualified by provider capability |
| §9 | Onboarding verifies branch protection is configured, not merely supported |
| §14 | Recap posts as a provider comment where supported, in-app where not |
| §18 | Deployment binding already provider-agnostic — no change |
| §21 | Replaced by Part B |
| §22 | Email delivery replaced by Part C |
| §23 | Rewritten — Charter Agent and OpenRouter move to Phase 1; Phase 5 gains additional VCS providers; Part D lands in Phase 4 |
| §26.9 | Repo transfer is capability-gated |
| §28 | Update check settings move to the database |
| §33 | Charter Agent noted as the only provider-agnostic runner |

## Build order

**Phase 1 comes first and is defined above.** Nothing in this change spec is built until the vertical slice runs on GitHub + Railway + Charter Agent + OpenRouter.

**Then, infrastructure — these unblock everything else.**

1. **Part C** — email, one provider only. Small, self-contained, unblocks invitations.
2. **Part D** — configuration. Do it before the settings surface grows; retrofitting is far worse than building it early.
3. **Part B** — 2FA. Depends on D for its settings surface.
3b. **Part I.3 and I.6** — images and voice input. Small, and they improve the quality of every specification produced from that point on. Do these before anything in E–H.
4. **Part E.1 only** — the `WorkItem` model and derived status. **Build the model early even if no board exists yet.** Generalising `Request` after boards, sprints, and sync depend on it is the single most expensive refactor available in this design.

**Then, in either order:**

5. **Part A** — providers. GitLab first, then Gitea. One at a time, each validated against the seam before the next begins. Second deployment provider (generic webhook) lands here too.
6. **Part H in one-way mode, Jira and Linear first** — push refined stories out to whatever the org already uses. High value, low risk, validates the work item model against real external schemas, and serves the majority configuration. This ships *before* any Part E surface.

**Only after the core loop is proven in real use:**

7. **Part G.2** — the AI-specific metrics. These are the differentiated ones and need real session history to be meaningful.
8. **Part E.4–E.6** — sprints, boards, promotion from Chat and Plan. Lowest priority of the three modes by design: `synced` is the expected configuration and `off` is the default, so native planning surfaces serve the smallest slice of users.
9. **Part F** — design mode.
10. **Part H inbound sync** — the hardest and most corruption-prone piece. Last, deliberately.
11. **Part G.1** — standard delivery metrics. Table stakes, lowest differentiation, cheapest to add once the model exists.

## Open questions

- Does the Charter Agent need provider credentials locally for Perforce-style workspaces, or can everything remain per-job?
- Should `merge_gate_enforcement: advisory` repos be blocked from auto-dispatch (§7.5) by default? Leaning yes.
- Configuration export granularity — whole instance, or per organisation in multi-org deployments?
- Does a `WorkItem` in `synced` mode keep a Charter key, or display only the external one? Two keys for one item confuses people; no key breaks Charter-side references.
- Should sprint agent budgets roll over, or expire with the sprint? Leaning expire — a rolling budget defeats the purpose of sprint-level capacity planning.
- Is `epic` a `WorkItem` type or a separate entity? Type is simpler; separate is what every tool eventually regrets not doing.
