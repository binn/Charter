---
title: "HTTP API"
description: "Every route Charter serves today, grouped by who calls it: the requester and engineer endpoints the app itself uses, runner registration, the sandbox callbacks, the GitHub and deployment webhooks, and the agent pairing routes."
---

# HTTP API

Charter serves one HTTP port. Everything below hangs off it: the API the bundled web app calls, the
callbacks a sandbox makes while a session runs, the webhooks GitHub and your hosting platform post to,
and the two routes a Charter Agent dials.

**The API is not versioned and is not stable.** There is no `/v1` prefix, no deprecation window, and no
compatibility promise before 1.0. Routes and payload shapes change between releases, and the
[changelog](https://github.com/binn/Charter/blob/master/CHANGELOG.md) is the only notice you get. Build
against it if you like, but pin an image tag and read the release notes.

## The rule that governs every payload

**A field a caller may not see is absent from the response, not hidden in it.**

Authorisation is not a rendering concern. Charter does not send a requester the commit SHA with a flag
saying "do not draw this" — it does not send the commit SHA. `GET /api/requests/{id}` returns a
different set of keys depending on who asked, and the client's only test is whether a key is present.

For a requester, these keys are **absent from the JSON object entirely**:

| Absent key | What it carries |
|---|---|
| `transcript` | The raw event stream. Leaks file paths, environment variable names, and error output. |
| `changes` | The changed-file list, with risk ranking. |
| `recap` | The engineer recap — files, branches, deviations. |
| `sessionActions` | Steer, revise, take over, approve. Absent rather than disabled. |
| `artifacts[].details` | Change request number and URL, commit SHA, branch, runner, duration, cost. |

The same rule applies within the spec object. A requester's `spec` carries `title`, `outcome`,
`acceptanceCriteria`, `openQuestions` and `glossary`. It has no `technicalApproach`, `scope`, or
`risks` property — not an empty one, not a null one. `openQuestions` is the single field written *to*
the requester rather than about the implementation, which is why it crosses the line: it is what blocks
the confirm button, and withholding it leaves somebody staring at a disabled button with no way to
learn why.

Two endpoints enforce the same boundary with a status code rather than an omission, because a fetch has
nowhere to put an absence: `GET /api/requests/{id}/transcript` and `GET /api/requests/{id}/changes/{path}`
answer `403` for a caller without repository read access.

If you are reading this to audit the boundary, the enforcement lives in `Charter.Api.Requests`
(projection and visibility) and `Charter.Auth.Authorization`, not in the endpoint lambdas. See
[security.md](security.md) and
[spec §7.4](https://github.com/binn/Charter/blob/master/agent-docs/spec.md).

## Conventions

- **JSON is camelCase.** Timestamps are ISO 8601 with an offset. Ids are opaque strings — UUIDs
  server-side; do not parse them.
- **Authentication is a cookie**, issued under Charter's own scheme. Everything under `/api` requires
  it, except the routes listed under [Machine-to-machine](#machine-to-machine) below, which carry their
  own bearer credentials.
- **Errors are `application/problem+json`** with a `title` and a `detail` written for the person
  reading it. No stack traces, no SQL, no internal identifiers.
- **`404` also means "not yours."** A record in another organisation and a record that does not exist
  return the same body, so the API is never an existence oracle.
- **No response carries an ETA.** Timestamps are starts, ends, and expiries only.

### Error statuses

| Status | Means |
|---|---|
| `400` | The body did not make sense. |
| `401` | Not signed in, or the session expired. |
| `403` | Signed in, but this is not yours to see or do. The `detail` says why. |
| `404` | Gone, or never yours. |
| `409` | The state machine does not allow this here — approving a spec twice, steering a finished session. |
| `429` | Intake rate limit. Nothing you already sent was lost. |
| `503` | Either the instance has not been claimed yet, or this instance is not configured for what you asked. |

### Rate limits

Intake is limited per user **and** per organisation, on four chained windows, so ten accounts cannot do
the damage one was stopped from doing:

| Window | Default |
|---|---|
| Per user, per minute | 6 |
| Per user, per hour | 40 |
| Per organisation, per minute | 30 |
| Per organisation, per hour | 200 |

Partitioning is by authenticated member, never by IP — a shared office NAT would otherwise throttle a
whole company because one person was busy.

Six endpoints count as intake, because each one spends model tokens: creating a request, sending a
refinement message, steering a session, revising a session, rebuilding an artifact, and sending a test
email.

## Instance and probes

Unauthenticated. These answer even while the instance is in setup mode, because a platform that cannot
probe a container will kill it before anyone can claim it.

| Route | Returns |
|---|---|
| `GET /health` | `{ status, version, commit }`. Liveness. Touches no dependency, so a database blip cannot make your platform restart a healthy container. |
| `GET /ready` | `{ status, reason }`. Readiness. Opens a Postgres connection with a five-second timeout; `503` with a reason when it cannot. |
| `GET /api/instance` | `{ version, commit, buildDate, sourceUrl, license, serviceName }`. AGPL section 13 compliance data — the footer renders `sourceUrl` on every page as a licence obligation, not a credit link. |

```bash
curl -s https://charter.example.com/health
```

## Requester endpoints

Everything a person who cannot read code touches. All of it is cookie-authenticated, and all of it is
subject to the omission rule above.

| Route | What it does |
|---|---|
| `GET /api/me` | The viewer: display name, organisation, roles, server-computed capabilities, preferences. Capabilities drive navigation and affordances only — they never gate the rendering of data, because data you may not see is not in the payload. |
| `PATCH /api/me/preferences` | Accepts a partial `{ theme, pane, teachingLevel }`, returns the full resolved set. Preferences are columns on the user record; there is no browser storage in this application. |
| `POST /api/me/onboarding/requester/complete` | Marks the three requester onboarding screens done. Returns the viewer. |
| `GET /api/projects` | Projects this viewer is scoped to **and** that have passed their smoke test. A repository that has not is not in this list; a repository you are not scoped to is not in this list. Absence is the enforcement. Carries the operator's display name and the repo primer, never `owner/repo`. |
| `GET /api/requests` | Request summaries. Each carries `status`, the latest translated milestone label, and `needsAttention` — true only for the two states that notify. |
| `GET /api/requests/{id}` | One request in full: raw text, the requester rendering of the spec, the refinement thread, the status thread, verification artifacts, and whether it can be cancelled. Plus the engineer keys, for an engineer. |
| `POST /api/requests` | Files a request. Body `{ projectId, rawText, templateId? }`. Returns `201` with the same detail body. Intake-limited. |
| `POST /api/requests/{id}/refinement` | One turn of the refinement conversation. Body `{ body, choiceId? }`. `204`. Intake-limited. |
| `POST /api/requests/{id}/spec/{version}/approve` | The ownership moment. Approves a specific spec version. `204`. |
| `POST /api/requests/{id}/spec/{version}/changes-requested` | Sends the spec back. Body `{ note }`. `204`. |
| `POST /api/requests/{id}/feedback` | Body `{ verdict, note? }` where `verdict` is `works` or `not_quite`. Two buttons, and there is no third. `204`. |
| `POST /api/requests/{id}/cancel` | Stops the work and settles token cost. `204`. |
| `POST /api/requests/{id}/artifacts/{artifactId}/rebuild` | The primary action on an expired preview. Intake-limited. `204`. |

```bash
curl -s https://charter.example.com/api/requests \
  -H 'Content-Type: application/json' \
  -b charter-session.txt \
  -d '{"projectId":"0f6b0b9c-3a0d-4f2f-9a5a-2c9d0f7f1b21","rawText":"every time I start a new quote it makes me pick solar again"}'
```

### Request status values

The wire values, and the words the requester actually reads. The mapping is server-side and lives in
one table, so the badge, the email, and the list row cannot tell three different stories.

| Wire value | Shown to a requester | Notifies |
|---|---|---|
| `draft` | Not sent yet | no |
| `refining` | Let's figure out what you need | no |
| `spec_ready` | Waiting on {name} to approve | no |
| `rejected` | Sent back for another look | no |
| `queued`, `running`, `pr_open` | Building this now | no |
| `needs_input` | Question for you | **yes** |
| `preview_ready` | Ready to try | **yes** |
| `in_review` | An engineer is checking it | no |
| `merged` | This is live | no |
| `no_changes_needed` | Nothing needed changing | no |
| `failed` | This turned out to be bigger than expected | no |
| `cancelled` | You stopped this | no |
| `stale` | This needs redoing against the latest code | no |

[the-loop.md](the-loop.md) walks through what moves a request between these.

## Approver endpoints

| Route | What it does |
|---|---|
| `GET /api/approvals` | The spend-gate queue: request id, spec id, title, plain-language outcome, requester, project, estimated cost, submitted time. Nothing about code quality — that gate is not Charter's and is not in its data model. |

## Engineer and admin endpoints

Panes 2 and 3, the four post-hoc session actions, runner administration, and settings.

| Route | What it does |
|---|---|
| `GET /api/requests/{id}/transcript` | Pane 2. Query `cursor`, `aroundSeq`, `limit` — mutually exclusive. Pages backwards from the tail; `aroundSeq` centres the window on one event so a pane-1 milestone can jump to event 12 of 12,480. Returns `{ events, nextCursor, totalCount }`. **`403` without repository read access.** |
| `GET /api/requests/{id}/changes/{path}` | Pane 3. One file's before and after for the diff viewer, plus hunks, a resolved language id, and `binary` / `truncated` flags. Per file rather than bundled into the detail body, because a session can touch a hundred files. **`403` without repository read access**, `503` when this instance has no version-control client wired. |
| `GET /api/requests/{id}/blobs/{key}` | One stored object — the full text behind a transcript event that was offloaded to object storage, whose payload carries the key as `{property}_ref`. Served as an attachment with `nosniff`. **`403` without repository read access**, the same check pane 2 makes; `404` for a key belonging to another session, which is indistinguishable from one that does not exist; `503` when this instance keeps everything in Postgres. There is no public or presigned URL alternative — see [security.md](security.md). |
| `POST /api/requests/{id}/session/approve` | Approves a session after the fact. `204`. |
| `POST /api/requests/{id}/session/steer` | Body `{ instruction }`. Continues the existing session — same branch, same thread. Intake-limited. |
| `POST /api/requests/{id}/session/revise` | Body `{ revisedSpecMd }`. Forks the spec onto a fresh session on the same branch. Sent in full, because forking a spec means replacing it. Intake-limited. |
| `POST /api/requests/{id}/session/take-over` | Marks the session handed off and stops all further agent writes to the branch. **Irreversible server-side**, because concurrent human and agent edits in one worktree is the one genuinely destructive failure mode in this design. |
| `GET /api/setup/checklist` | The admin setup checklist. Resolves to `null` — not `403` — for anyone who is not an admin, because a requester's dashboard has no checklist on it and that is not an error state a page should have to render. |
| `POST /api/setup/checklist/dismiss` | Allowed only once every task is done. |
| `GET /api/settings/email` | Whether mail is configured, and the recent delivery log. Admin only. |
| `POST /api/settings/email/test` | Body `{ to }`. Sends through the real send path. A mail server that refused the message is a `200` carrying `sent: false` and the server's own words — the only non-2xx here is "you are not an administrator". Intake-limited. |

## Repository onboarding

The five steps of the onboarding wizard and the gate that ends it. Every route here is engineer or
admin only, and refuses everybody else outright — a requester never sees a repository name, and the
way that stays true is that these endpoints say no rather than that a projection hides fields.

**Nothing here makes a repository requestable.** There is no route that sets `ready`: the only path
to it is a passing smoke test reported by the execution plane, because readiness is earned.

| Route | What it does |
|---|---|
| `GET /api/repos` | Every connected repository, with the onboarding state each has reached. |
| `POST /api/repos` | Connects one. Body `{ fullName, installationId, baseBranch? }`. Admin only. Returns `201` with the whole wizard state — the repository, its steps, and anything the previous runs recorded. The one scope grant it writes is for the person who connected it; everybody else is added deliberately. |
| `GET /api/repos/{id}` | Where this repository is: `steps`, `scopeConfigPullRequest`, `lastSmokeTest`, `mergeGate`, `proposedScope`, `primerDraftMd`. Each of the last four is **absent until the run that produces it has happened**. |
| `POST /api/repos/{id}/recon` | Queues the read-only recon run. Intake-limited. |
| `POST /api/repos/{id}/scope` | Confirms the scope. Body `{ allow?, deny? }`; sending neither accepts what recon proposed. Opens `.charter/config.yml` as a pull request **and queues the smoke test**. Whatever arrives is filtered through the deny-by-default floor again, so a client cannot widen scope past it. Intake-limited. |
| `GET /api/repos/{id}/smoke-test` | The last run. A read, never a trigger: a `GET` that could spend money on a refresh would be a mistake. Answers `null` — not `404` — when nothing has run, because the repository plainly exists and the run is a step still to do. |
| `POST /api/repos/{id}/primer` | Publishes the primer the engineer edited. Body `{ markdown }`. |

`proposedScope` is what recon found and proposed: the detected stack, the build and test commands, any
`CLAUDE.md` / `AGENTS.md` it imported, and one entry per path with `allowed`, an optional `reason`,
and `locked` on the deny-by-default floor. A locked entry is rendered as a denial with its reason and
never as a toggle, because whatever the client sends back is filtered through that floor anyway.

`lastSmokeTest.checkpoints` carries all six integration points — `request_filed`, `agent_ran`,
`checks_passed`, `pull_request`, `preview_deployed`, `url_bound` — each `passed`, `failed` or
`skipped`. Everything after the first failure is `skipped`: a preview that was never asked to deploy
did not break, and saying it did sends an engineer to the wrong subsystem. `warnings` carries the
empty-preview caveat, which warns and never blocks.

`primerDraftMd` is what the primer editor opens with: the published primer once there is one, and
before that a draft scaffolded from what recon verified. It is deliberately a scaffold with headings
rather than finished prose — the sentences that matter in a primer are the domain ones only your team
knows, and a draft that reads as finished gets published unedited.

## Repository access

Who may file against a repository. **Deny by default**: a newly connected repository is requestable by
nobody, and the absence of a grant *is* the refusal. There is no synthesised "denied" row for everyone
without a grant, because that would be a list of your whole organisation and would read as a policy
rather than as the default.

| Route | Who | What it does |
|---|---|---|
| `GET /api/repos/{id}/access` | Engineer or admin | `{ grants, requesterVisible }`. Each grant names either one member — with their display name and email, so the screen is auditable rather than a column of ids — or one role. `requesterVisible` is carried beside them because the two conditions are independent: a repository can be fully granted and still invisible because its smoke test has not passed. |
| `POST /api/repos/{id}/access` | Admin | Body `{ memberId? , role?, canRequest }`, **exactly one** of `memberId` and `role`. Returns the new list. `canRequest: false` writes a withholding row rather than deleting the grant, and a withholding row beats a granting one at the same level — which is what makes "why can this person not file?" answerable. |

An engineer configures a repository; only an admin decides who may ask it for things.

## Members, roles and the audit log

| Route | What it does |
|---|---|
| `GET /api/members` | Everybody in the organisation with their roles, whether they may create repositories, and which one is you. Admin only. |
| `POST /api/members/{id}/roles` | Body `{ role, granted }` — one role per call, never a whole set, because a set replaces state the caller read minutes ago and a single verb is what the audit log records. Writes `member.role.granted` or `member.role.revoked` naming you. Refuses with `409` and a sentence in two cases: leaving somebody with no role at all, and removing the last administrator on the instance. |
| `GET /api/audit` | The most recent entries, newest first, each with the dotted verb, a plain-English sentence, the acting human, and short metadata. Admin only. An entry with no actor is Charter itself and is shown rather than hidden — those are the ones worth being able to find. |

Roles are additive: a member may hold several, and granting one never removes another.

## Model credentials

| Route | Who | What it does |
|---|---|---|
| `GET /api/credentials` | Engineer or admin | Every credential this instance can authenticate with: stored grants for the organisation, plus the instance-level `ANTHROPIC_API_KEY` and `OPENROUTER_API_KEY` marked `source: "environment"`. Also carries `sharedPoolAllowed` and, per configured control-plane model, whether anything present can serve it and the remedy if not. |
| `POST /api/credentials` | Engineer or admin | Body `{ kind, secret, scope?, baseUrl?, priority?, maxSessionsPerDayFromOthers? }`. `201`. The credential is owned by the caller, always — an administrator cannot upload somebody else's key on their behalf, because §20b.5's consent mechanics rest on the owner having offered it. `scope: "shared_pool"` returns a `warning` carrying the terms-of-service caution. |
| `POST /api/credentials/{id}/revoke` | Engineer or admin | `204`. Immediate: the next resolution skips it. Revoking leaves the row, so the credential list still shows what happened. |

**No route returns a secret, at any point, including immediately after creating one.** There is no
property on any response that could carry one and there is no reveal endpoint — the list shows
provider, owner, scope, status, why it was marked invalid, and when it was last used. Instance-level
entries have no owner and cannot be revoked over HTTP; they are environment variables, and they change
when the deployment does.

Both writes are audited, as `credential.linked` and `credential.revoked`.

## Runner registration

Two surfaces exist, and they are not duplicates of each other. The `/api/runners` group is what
Settings → Runners reads; the `/api/agent/*` group is the agent plane, and it is **only mapped when
`CHARTER_RUNNER` includes `agent`**. With the agent backend disabled those routes are absent rather
than present and broken.

| Route | Who | What it does |
|---|---|---|
| `GET /api/runners` | Admin | Registered agents plus the sessions currently waiting on one, with what each session requires and why it is queued. Admin only; anyone else gets `403`. |
| `POST /api/runners/pairing-tokens` | Admin | Issues a single-use, short-TTL pairing token. **Returned exactly once** — there is no endpoint that reads it back, so nothing may offer to show it again. `201`. |
| `DELETE /api/runners/{agentId}` | Admin | Revokes an agent. Kills in-flight jobs and invalidates the credential. |
| `GET /api/agent/agents` | Engineer or admin | The same agent list from the agent plane. Engineers can read it because the "queued with no eligible runner" explanation is addressed to them; only admins can change it. |
| `POST /api/agent/agents` | Admin | Body `{ name?, expiresInMinutes? }`, clamped to 1–1440 minutes. Returns `201` with `{ agentId, pairingToken, expiresAt, command }`, where `command` is the exact `charter-agent --server … --token …` line to run. |
| `POST /api/agent/agents/{agentId}/revoke` | Admin | Body `{ reason? }`. |

See [runners.md](runners.md) for what a runner is and which one you want.

## Machine-to-machine

None of these use a cookie. Each carries its own admission rule, stated in its own handler rather than
delegated to middleware a future refactor could reorder away.

### Agent pairing and connect

The two routes `charter-agent` talks to. Both are anonymous to the cookie pipeline and authenticate on
the agent's own credential instead.

| Route | What it does |
|---|---|
| `POST /api/agent/pair` | Trades a single-use pairing token for the long-lived agent credential. Body carries `pairingToken`, `name`, `mode` (`docker` or `native`), `agentVersion`, `protocolVersion`, `concurrency`, `platform`, and probed `capabilities`. Returns `{ agentId, agentToken, protocolVersion }` and a `Charter-Agent-Protocol` header. |
| `GET /api/agent/connect` | A WebSocket, authenticated with an `Authorization: Bearer` header carrying the agent token from pairing. The agent dials outbound and holds the socket open to claim work, heartbeat, and stream events. |

The credential is returned exactly once and held server-side only as a PBKDF2 verifier. Capabilities
are **probed by the agent and reported**, never declared in configuration — the difference between a
claim and a measurement.

Pairing status codes are a contract the daemon implements, and getting one wrong is the difference
between an operator being told to generate a fresh token and an agent retrying a dead one forever:

| Status | Means | Agent retries? |
|---|---|---|
| `401` | Token rejected | no |
| `403` | Agent revoked | no |
| `410` | Token expired or already spent | no |
| `426` | Protocol version refused | no |
| `408`, any `5xx` | Transient | yes |

`GET /api/agent/connect` deliberately accepts the socket even when the protocol version does not match,
then closes it with an explanatory `protocol.mismatch`. A refused upgrade reaches the operator as
`WebSocketException: server returned 426`, which names neither version.

### Session callbacks

What a sandbox posts back while it runs. The prefix is `/api/runners/sessions/{sessionId}`, which is
exactly what Charter builds `callback_url` from, so these routes hang off the URL the workflow already
has. Authentication is a bearer token: the repository's session secret for the first call, then the
short-lived event token that call returns.

| Route | What it does |
|---|---|
| `POST …/credentials` | Exchanges the repository session secret **and this session's dispatch token** for scoped, short-TTL credentials. Body `{ session_id, session_token, run_url? }`, with the secret in `Authorization`. Returns `{ github_token, model_api_key, event_token }`. Both factors are required: the secret is one value per repository, so on its own it authenticates a repository rather than a session, and `session_token` — minted per session and sent only in that session's dispatch payload — is what says which session is asking. A request without it is refused `403`. Gated further on the session genuinely running: a session that has ended or has a cancel in flight answers `404`, and one no backend has dispatched answers `409`. `run_url` is checked against the repository the session belongs to and a mismatch is refused `403` with nothing minted — the value comes from inside the sandbox, and Charter later reads a repository back out of it. |
| `POST …/events` | One streamed event: `{ session_id, type, payload, index? }`. Returns `{ seq, appended }`. A `session_started` event whose payload carries a `run_url` is subject to the same check as the exchange and is refused `403` on a mismatch. |
| `POST …/result` | The terminal report, always sent, even on failure: `{ session_id, state, run_url?, message? }`. A `run_url` that does not pass the check is dropped and the result still stands — refusing a terminal report would leave the session running in Charter's eyes, which is worse than a missing link. |
| `GET …/spec` | The approved spec as `text/markdown`. This is what the agent is told — a model-authored, human-approved document, never the requester's own words. |

Every write is idempotent. A runner that loses its connection retries, and a control plane that
restarted has no way to know whether it saw a delivery before, so duplicate suppression is a property
of storage rather than of anybody's memory. An event with an `index` is keyed on it; an event without
one is keyed on a hash of its type and payload.

### GitHub webhook

```
POST /api/github/webhook
```

Verified against `GITHUB_WEBHOOK_SECRET` over the exact bytes GitHub sent, before anything parses them.
An oversized body is refused with `413` before it is buffered. A missing signature and a wrong one
produce the same `401`, so the response cannot be used to tell them apart; the log distinguishes them,
because an operator legitimately needs to.

A verified delivery is `202 Accepted` and acted on asynchronously. One listener failing does not deny
the others their delivery and does not turn a real delivery into a `500` GitHub will simply send again.

Subscribe the App to **Push**, **Pull request**, **Check suite**, and — if your platform announces
preview environments by commenting on the pull request, as Railway does — **Issue comment**. Anything
else is acknowledged and ignored.

### Deployment webhook

```
POST /api/deployments/{prSha}
```

The provider-agnostic way to report a preview environment. This is what makes a Render, Fly, or Coolify
self-hoster first-class rather than a port of the Railway path.

```bash
curl -s -X POST https://charter.example.com/api/deployments/9f2c41b7d8e05a3c6b12f4a7e8d0c5b3a16d47e2 \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $CHARTER_DEPLOYMENT_WEBHOOK_SECRET" \
  -d '{"url":"https://myapp-pr-142.onrender.com","state":"ready","provider":"render"}'
```

`prSha` is the **head commit SHA** of the pull request, not a Charter identifier — it is the one value
every hosting provider already knows about a preview build without being told. It decides which pull
request a report binds to. It is **not** what admits the caller; see below.

| Field | Notes |
|---|---|
| `url` | Required when `state` is `ready`. A ready deployment with no URL is refused, and so is one Charter will not vouch for — see [security.md](security.md#preview-urls-are-validated-before-charter-stores-fetches-or-shows-one). |
| `state` | One of `pending`, `building`, `ready`, `failed`, `cancelled`, `expired`. |
| `provider` | Free text: `railway`, `render`, `fly`, `coolify`. Defaults to `unknown`. |

Common provider vocabulary is accepted as synonyms, so you can usually forward your platform's own
state verbatim: `queued` and `waiting` map to `pending`; `deploying`, `in_progress` and `initializing`
to `building`; `success`, `succeeded`, `active` and `deployed` to `ready`; `failure`, `error` and
`crashed` to `failed`; `canceled` and `skipped` to `cancelled`; `removed` and `destroyed` to `expired`.

| Response | Means |
|---|---|
| `202` | Recorded, and the verification artifact was published in the same call. |
| `401` | The instance's deployment secret was absent, wrong, or not configured at all. |
| `404` | No pull request in this instance carries that head commit. |
| `400` | The body named no state Charter understands, a ready state with no URL, or a URL Charter will not accept. |

**This endpoint takes a secret, and refuses everything without one.**
`CHARTER_DEPLOYMENT_WEBHOOK_SECRET` is checked before the body is read. Present it as
`Authorization: Bearer <secret>`, as `X-Charter-Deployment-Secret: <secret>`, or — for a platform
whose post-deploy hook is a URL field and nothing else — as `?token=<secret>`. The first carrier
present is the one checked, so send exactly one. A missing secret and a wrong one produce the same
`401`, so the response cannot be used to tell them apart.

The commit SHA is not the credential and never was. It is authored inside the session that produced
the branch, and afterwards it is on the pull request page, in every fork, in CI logs and in
notification emails — a value strangers legitimately know cannot decide which strangers may write.
Setting up the secret is in [configuration.md](configuration.md#the-deployment-webhook-needs-a-secret).

## Realtime

```
/hub/requests
```

A SignalR hub, joined per request, pushing `milestone`, `milestone_updated`, `status`,
`refinement_message`, `charter_thinking`, `spec_proposed`, `artifact`, `artifact_state`, `failed`, and
`ended`. Every event is idempotent by id, because the container can restart mid-session and a client
resubscribes by refetching.

The hub is mapped and publishes. **The bundled web app does not subscribe to it yet** — its
`subscribeToRequest` is a no-op, and the app refetches instead. See the limitations below.

## What is not here yet

Stated plainly, because a docs set that oversells gets discovered and costs more trust than the
limitation itself. All of the following are true of the code as it stands.

- **OAuth sign-in is not mapped.** Password sign-in, sign-out, invitations and password reset are all
  mapped under `/api/auth`, and `/api/setup` redeems the first-run token — you can claim an instance
  and sign into it. What is missing is the OAuth half: the exchange is built and registered, but the
  callback URI `/api/auth/{provider}/callback` that the provider constructs is not mapped, so any
  identity provider other than a password is unreachable.
- **Recon and the smoke test have no execution plane on a single-container instance.** The onboarding
  routes above are all mapped, and the wizard reads back everything a completed run recorded — but
  the two runs themselves are dispatched as jobs, and a job needs a runner. Until one is registered,
  `POST /api/repos/{id}/recon` queues work nothing claims, so `proposedScope`, `primerDraftMd` and
  `lastSmokeTest` stay absent and the repository stays requestable by nobody. That is the guardrail
  working, not a bug, but it is the wall you will hit first.
- **Re-confirming a scope does not update `proposedScope`.** The proposal is recorded when recon
  reports, and confirming the scope opens a new commit on the pull request rather than rewriting what
  the wizard shows. The pull request is the source of truth for what was agreed; the wizard is showing
  you what recon suggested.
- **The mock survives only in a development build.** The container image sets
  `VITE_CHARTER_LIVE_API=true` before bundling, so the shipped UI calls the API described on this page
  and the in-memory mock is compiled out entirely. Running the Vite dev server without that variable
  still gets you the mock.
- **There is no HTTP surface for budgets.** Members, roles, repository scopes, the audit log and model
  credentials all have one now; budgets are still configured through the environment and the database.
- **Model credentials have routes but no screen.** `/api/credentials` works and is tested; the web app
  does not call it yet, so linking a credential today means `curl` with a session cookie.

Beyond the missing routes, the loop itself stops short of a preview on a real instance: nothing pushes
the session branch, so no change request is opened and no deployment report has a commit to bind to.
[the-loop.md](the-loop.md) explains exactly where and why, and
[getting-started.md](getting-started.md) says which parts of the walkthrough you can drive today.

## Related

- [the-loop.md](the-loop.md) — what these endpoints move a request through, in order
- [getting-started.md](getting-started.md) — standing an instance up and driving the loop
- [security.md](security.md) — the trust boundary these rules implement
- [runners.md](runners.md) — backends, capability matching, registering an agent
- [self-hosting.md](self-hosting.md) — deployment, TLS, and the base URL webhooks need
- [spec §7.4, §18 and §33](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — trust boundary, preview binding, and the agent protocol
