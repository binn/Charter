---
title: "Getting started"
description: "From nothing to a working Charter instance: bring the container up, claim it with the one-time setup token, connect a repository, watch onboarding end in a smoke test, and take one request through refinement, approval and a preview."
---

# Getting started

This page takes you from an empty directory to a Charter instance with one repository connected and one
request driven through the whole loop: refined into a spec, approved, built by an agent, opened as a
pull request, and returned as a preview link somebody can click.

Budget about half an hour, most of it spent creating a GitHub App.

## What you need

- **Docker** with Compose v2. `docker compose version` should print something.
- **A GitHub account** with admin rights on one repository you are willing to let an agent open pull
  requests against. A scratch repository is a better first choice than your main product.
- **A GitHub App.** Not optional — Charter refuses to start without an App ID, a private key, and a
  webhook secret. Creating one takes five minutes and is covered below.
- **A model key.** Refinement calls a model on the very first request, so there is no way to see the
  loop without one. An `OPENROUTER_API_KEY` on its own is enough and is what the defaults assume — one
  key reaches every model. An `ANTHROPIC_API_KEY` on its own also works, but then you must set
  `CHARTER_MODEL_REFINE` and `CHARTER_MODEL_TEACH` to `anthropic/` identifiers, because the defaults
  name an OpenRouter model and only an OpenRouter key can serve one.
- **A publicly reachable HTTPS URL**, if you want GitHub to deliver webhooks. On a laptop that means a
  tunnel. You can get most of the way without one; the parts that need it are called out as you reach
  them.

## Before you start: what runs today

Charter is pre-1.0 and the loop is younger than the interface around it. Read this before you spend
half an hour, because it is the difference between "this is broken" and "this is early".

| Step | State |
|---|---|
| Container boots, validates configuration, applies migrations | Works |
| Setup mode, one-time token printed to stdout, redeeming it over HTTP | Works |
| Signing in | Works, with a password. OAuth is built but its callback route is not mapped, so an external identity provider is not usable. |
| Connecting a repository, recon, scope confirmation, smoke test | Routes exist and the wizard reads back what each run recorded. The runs themselves need a registered runner to claim them. |
| Granting somebody access to a repository, members and roles, the audit log | Works, over HTTP, and every change is audited. |
| Request, refinement, spec, approval, dispatch, session | Works, driven by the real services. |
| Committing the agent's work and pushing the branch | Works. The session runs your repository's `checks:` first and reports each result. |
| Pull request, review and merge tracking, preview, feedback | Implemented. A review moves the request to *In review* and a merge to *Merged*, so the requester is told their change shipped. |
| The bundled web app | Talks to the real API. The in-memory mock is compiled out of the production bundle. |

**The caveat that matters more than any row above: none of this has been run against a real
repository with a real model.** Every part of it is verified against a local database, stubbed
providers, and a real `git` pushing to a local remote — which proves the wiring is connected and the
events line up, and proves nothing about the day you point it at your own code. You are, realistically,
the first person to do that. Expect to find things, and please report them.

Two narrower limits worth knowing before you start. Recon and the smoke test are dispatched as jobs, so
on an instance with no runner registered they stay pending and the onboarding wizard waits. And Charter
only tracks pull requests it opened itself — one a human opens carrying the same work is invisible to
it.

If you would rather drive the loop from a terminal than a browser, jump to
[Driving the loop without a browser](#driving-the-loop-without-a-browser).

## 1. Bring it up

```bash
git clone https://github.com/binn/Charter.git
cd Charter
cp .env.example .env
```

### Fill in `.env`

Four things need real values before the container will start.

**Two secret keys.** Generate them separately — they are not interchangeable, and one of them cannot be
rotated without losing every stored credential.

```bash
openssl rand -base64 48    # paste as CHARTER_SECRET_KEY
openssl rand -base64 48    # paste as CHARTER_CREDENTIAL_KEY
```

**A Postgres password.** `docker-compose.yml` requires `POSTGRES_PASSWORD` and `.env.example` does not
contain it, so add the line yourself. Without it Compose refuses to render the file at all:

```bash
printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 16)" >> .env
```

**A GitHub App.** Go to **Settings → Developer settings → GitHub Apps → New GitHub App** on
github.com, or the same path under your organisation.

- **Homepage URL** and **Webhook URL**: your instance's public URL. The webhook path is
  `/api/github/webhook`.
- **Webhook secret**: generate one with `openssl rand -hex 32` and put the same value in
  `GITHUB_WEBHOOK_SECRET`.
- **Repository permissions**: Contents read and write, Pull requests read and write, Metadata read,
  Checks read. Add Administration read if you want Charter to report whether your base branch actually
  requires review.
- **Subscribe to events**: Push, Pull request, Check suite. Add Issue comment if your hosting platform
  announces preview environments by commenting on the pull request, as Railway does.
- Generate a private key, download the `.pem`, and put it in `GITHUB_APP_PRIVATE_KEY`. Charter accepts
  raw PEM, PEM with literal `\n` escapes, or the whole PEM base64-encoded — whichever your secret store
  tolerates. Base64 is easiest in a `.env` file:

```bash
base64 -i ~/Downloads/your-app.private-key.pem | tr -d '\n'
```

**A model key.** Uncomment `OPENROUTER_API_KEY` and paste yours. That is all this needs: the key is
consulted every time a credential is resolved, and `CHARTER_MODEL_REFINE` and `CHARTER_MODEL_TEACH`
both default to an OpenRouter model it can serve. You do not have to link anything in the database.

If you would rather use `ANTHROPIC_API_KEY`, set the two model variables to match — otherwise the
default OpenRouter identifiers name a model your key cannot serve, and the first-run report says so:

```bash
ANTHROPIC_API_KEY=sk-ant-api03-...
CHARTER_MODEL_REFINE=claude-sonnet-5
CHARTER_MODEL_TEACH=claude-sonnet-5
```

See [credentials.md](credentials.md).

Finally, set `CHARTER_BASE_URL` to the URL GitHub will reach. Not `localhost` — webhook delivery and
OAuth callbacks both depend on it being correct and publicly resolvable.

### Start it

```bash
docker compose up -d --build
docker compose logs -f charter-app
```

The Compose file builds from source rather than pulling a published image, so the first run takes a few
minutes.

**What this step proves.** Charter reads every environment variable once at startup, validates all of
them, and prints *every* problem at once before exiting non-zero. A container that is running is a
container whose configuration parsed, whose Postgres was reachable, and whose migrations applied. It
never boots into a half-working state to fail later on first use.

If it exits immediately, read the output — it names each variable and what it wanted. The two most
common first-run failures are a `GITHUB_APP_PRIVATE_KEY` that is neither PEM nor base64-encoded PEM,
and a `DATABASE_URL` that is an ADO.NET connection string rather than a `postgres://` URL.

Check it is alive:

```bash
curl -s http://localhost:8080/health
curl -s http://localhost:8080/ready
```

`/health` never touches the database, so it stays up through a Postgres blip. `/ready` opens a real
connection and reports `503` with a reason when it cannot.

## 2. Claim the instance

A self-hosted app that boots with open registration gets hijacked by whoever finds it first, so Charter
does not have open registration. On boot with zero users it enters **setup mode**, refuses every API
route except the setup one and the platform probes, and writes a **one-time token to stdout**.

```bash
docker compose logs charter-app | grep -A 6 "setup mode"
```

You will see a boxed message with a token and an expiry. That token creates exactly one admin account
and then expires. Setup mode ends permanently and cannot be re-entered while a user exists. There is no
default password and no open registration form.

Lost it? Restart the container while no user exists and a fresh one is issued.

**What this step proves.** An unclaimed instance exposes no API at all. Try it — every `/api` route
except `/api/instance`, `/api/setup` and the runner callbacks answers `503` with a problem document
telling you to read the token from the logs.

```bash
curl -s http://localhost:8080/api/me
```

Redeem the token to claim the instance, then sign in with the account you just created. Both routes
are mapped: `/api/setup` redeems, and `/api/auth/sign-in` issues the cookie. Only password sign-in
works — the OAuth callback is built but not mapped, so an external identity provider is not yet
usable.

## 3. Connect a repository

Install the GitHub App you created onto the repository you picked, and pick a base branch. Connecting a
repository is deliberately not the end of anything: **a newly connected repository is requestable by
nobody**. Deny by default is a guardrail primitive, not a default setting, and readiness is earned.

Connecting is `POST /api/repos`, and it is an administrator action. The one scope grant it writes is
for the person who connected it — enough to drive the rest of onboarding, and nothing more.

**Who can file against it is a separate decision from whether it is ready**, and both have to be true
before anybody sees it. Settings → Repositories → *(the repository)* has the access list: grant a whole
role, or one person at a time. Withholding somebody writes a refusal rather than deleting their grant,
and a refusal beats a grant at the same level — so "why can this person not file?" always has an
answer you can point at.

## 4. Onboarding ends in proof

The wizard is not a configuration screen. It ends in a working loop, because if connecting a repository
is a manual engineer chore then adoption stalls at repository one.

1. **Connect.** GitHub App install, base branch.
2. **Recon.** A read-only agent run over the repository. It reports the detected stack, a structure map,
   your test and build commands, existing conventions, and a *proposed* scope configuration. An existing
   `CLAUDE.md` or `AGENTS.md` is imported and extended, never overwritten.
3. **Scope confirmation.** A file tree with allow and deny toggles. Migrations, auth, CI configuration,
   infrastructure and secrets start denied. Confirming writes `.charter/config.yml` **as a pull
   request**, never as a direct commit — changing a guardrail should cost a review, and a tool that
   commits its own guardrails straight to the base branch has quietly exempted itself from the rule it
   asks everyone else to follow. See [charter-folder.md](charter-folder.md).
4. **Smoke test.** Charter files a canned trivial request and runs the entire loop: the agent runs,
   checks pass, a pull request opens, a preview deploys, the URL binds back.
5. **Primer.** The agent drafts `.charter/primer.md` — how this application is put together, in language
   a requester can read. An engineer edits it and publishes.
6. **Merge gate check.** Charter reads your base branch's protection rules and reports whether review is
   actually required. A repository with no rule is `advisory`: Charter still will not merge, and it says
   plainly that it cannot stop anyone else from doing so. This warns; it never blocks.

**What the smoke test proves.** Nothing else validates all six integration points at once — the GitHub
App's token, the runner, the agent adapter, the check commands, the pull request, and preview binding.
A repository becomes visible to requesters only after it passes. If your preview deploys but has no
data in it, the smoke test warns rather than blocking: `seed` is optional, and a codebase with no dev
seed path probably is not ready for non-engineer requests anyway.

Repositories drift, so re-recon is offered on demand. Re-running it on a ready repository does not
un-ready it — that would make every requester's project vanish from their list mid-afternoon.

**Recon and the smoke test both need a runner.** They are queued as jobs, and a job with nothing to
claim it stays queued: the wizard will sit on step two, the proposed scope and the primer draft will
stay absent, and the repository will stay requestable by nobody. That is the guardrail working rather
than a bug, but it is the wall you hit first on a single-container instance. See
[runners.md](runners.md).

**The primer draft is a scaffold, not finished prose.** Charter fills in what recon verified — the
stack, the commands, what is in scope — and leaves headings where the paragraphs only your team can
write belong, above all the domain vocabulary. A draft that read as finished would get published
unedited, and the primer is the one page every requester reads before their first request.

## 5. File a request

Someone types what they want in plain English against a project. No repository name, no branch, no
diff. `POST /api/requests` with `{ projectId, rawText }`.

The request lands in **Refining** and a refine job is queued. Nothing that could write to a repository
is queued, because nothing has been through refinement yet.

**What this proves.** Intake is the one endpoint a script could use to queue four hundred sessions, so
it is rate-limited per user *and* per organisation. It is also the boundary the security model rests
on: from here on, the requester's own words are carried in a type that refuses to be interpolated into
a prompt.

## 6. Refine it

Refinement is a conversation, not a form. Charter loads your `glossary.yml` for domain vocabulary and
`primer.md` for codebase shape, asks the questions a good product manager would ask, and **refuses to
produce a spec while anything is still ambiguous**. Unanswered questions come back as `openQuestions` on
the spec, and they are what blocks the confirm button.

It ends in a spec confirmation card: the request restated in the requester's own words, with acceptance
criteria as bullets. That is the ownership moment. Later, when a preview is wrong, the conversation is
"the spec said X" rather than "the AI misunderstood".

**What this proves.** A meaningful share of requests should die here, because the answer is often *it
already does that*. A request that never becomes a session is the cheapest possible outcome.

It also proves the sanitisation boundary: what reaches the agent is a model-authored, human-approved
document. A request that would touch a denied path is refused in plain English and routed to an
engineer rather than being quietly narrowed.

If a credential cannot be resolved, the request does not sit there. It ends as *This turned out to be
bigger than expected*, with a sentence in the thread saying Charter could not reach a model — and the
container log and the job's recorded error name the exact variable to set for the exact model that
could not be served. The one case that legitimately waits is a rate limit with a reset time from the
provider, which resumes on its own.

## 7. Approve it

The refined spec appears in the approval queue with an estimated cost. `POST
/api/requests/{id}/spec/{version}/approve` moves it from **SpecReady** to **Queued**.

This is the **spend gate** and only the spend gate. It asks whether the work is worth burning tokens
on. It never asks whether the code may ship — that gate is your branch protection and your CODEOWNERS,
it lives outside Charter entirely, and it is not represented in Charter's data model at all. Because
the merge gate cannot move, loosening the spend gate is safe: the worst case is wasted tokens and a
pull request nobody wanted.

In personal mode the gate is auto-satisfied by policy, not by an `if` statement somewhere. Personal
mode is an organisation with one member holding every role.

## 8. Watch it build

Approval writes a build job naming the spec. The dispatcher claims that row and turns it into a
session — nothing constructs a session at approval time, which is why a control plane that restarts
between the two still dispatches exactly once.

The requester sees *Building this now* and four translated milestones: understanding the current setup,
making the changes, checking it works, putting it together. They never see the transcript. **There is
never an ETA anywhere** — elapsed time only. Agent runs are wildly variable, and one blown estimate
costs more trust than ten honest slow ones.

When the session reports a clean completion, the reconciliation pass publishes the branch and opens a
pull request. It runs from reconciliation rather than from the result callback, so a control plane that
died between the two still opens it, and a second pass is a no-op rather than a second pull request.

Before it publishes, the session runs whatever your repository declares under `checks:` in
`.charter/config.yml` and records each result. A failing check does not stop the push — Charter has no
merge button, so a red pull request cannot ship anyway, and discarding the work would leave your
engineer nothing to read or take over. The failure is reported at the top of the pull request and on
the transcript instead. [the-loop.md](the-loop.md) has the detail.

## 9. Get a preview

Charter does not create preview environments. It binds whatever your platform created back to the pull
request, by head commit SHA. Two paths exist:

**The generic webhook.** Point your platform's post-deploy hook at your instance:

```bash
curl -s -X POST https://charter.example.com/api/deployments/9f2c41b7d8e05a3c6b12f4a7e8d0c5b3a16d47e2 \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://myapp-pr-142.onrender.com","state":"ready","provider":"render"}'
```

The SHA in the path is the pull request's head commit. That is the one value every hosting provider
already knows about a preview build without being told, and it is also the authorisation: a report for
a SHA no pull request carries is refused.

**Pull request comment parsing.** Fragile but universal, and it is how Railway works — its GitHub bot
comments when the PR environment is ready. This needs the App subscribed to **Issue comment**.

Either way the requester gets *Ready to try*, a link, and a **"what to check" list beside it** — the
acceptance criteria they approved, verbatim, not regenerated. Without that list a preview URL is a dead
end.

If you use Railway: base PR environments off a **staging** environment, not production, so preview
secrets are never real ones. Railway also will not deploy a PR branch from an account outside the
workspace, which is why Charter records the pull request author — so the warning can name who to invite.

## 10. Works, or Not quite

Two buttons. `POST /api/requests/{id}/feedback` with `works` or `not_quite`.

*Works* does not start another build; it asks for the engineer recap. *Not quite* opens a box and
becomes a new session on the same spec, in the same thread. Nobody is asked to write a bug report.

**What this proves.** The loop closed. A requester who has completed it once, including clicking a
preview, will file real requests. One who has only read about it will not.

## What needs real credentials, and what you can skip

| Thing | Needed to boot? | Needed for the loop? |
|---|---|---|
| Postgres | yes | yes |
| `CHARTER_SECRET_KEY`, `CHARTER_CREDENTIAL_KEY` | yes | yes |
| GitHub App ID, private key, webhook secret | **yes** | yes |
| A model key (`OPENROUTER_API_KEY` or `ANTHROPIC_API_KEY`) | no | **yes** — refinement calls a model on the first request. The environment key is enough; nothing needs linking in the database. |
| A public HTTPS URL | no | yes, for webhook delivery and preview binding |
| Railway, Render, Fly, or any preview platform | no | no — the deployment webhook accepts a report from anything, including `curl` |
| A Charter Agent, or any runner of your own | no | no — `CHARTER_RUNNER` defaults to `github-actions`, which needs no infrastructure from you |
| Object storage — a durable directory or an S3-compatible bucket | no | no — `CHARTER_STORAGE_BACKEND` defaults to `none` and everything stays in Postgres. Set it only if you want oversized transcript output kept outside the database. See [configuration.md](configuration.md). |
| SMTP | no | no. With no mail server, notifications are simply not sent and every email-dependent setting says why it is off. |
| OAuth provider credentials | no | no |

`CHARTER_DEMO=true` seeds a demonstration organisation, repository and completed sessions on first
boot, and blocks every outbound call, so you can look around without a GitHub App or a model key:

```bash
CHARTER_DEMO=true docker compose up
```

It seeds two accounts and prints them at startup, so read the container log. Both use the password
`charter-demo-password`:

| Email | Roles |
|---|---|
| `priya@northwind.example` | Requester |
| `ada@northwind.example` | Admin, Engineer, Approver |

Sign in at `/sign-in` as each in turn. That is the fastest way to understand what Charter is: the
same request looks completely different to the two of them, and the difference is enforced by the
API, not by hiding fields in the page.

It is a demonstration, not a sandbox for real work — nothing in that instance can reach a model
provider or a code host. Because the password is documented it is public, so do not expose a demo
instance on an address you would not hand out, and start a fresh database when you move on to real
work. See [configuration.md](configuration.md#demo-mode).

## Driving the loop without a browser

The full loop is covered by an integration test that walks intake, refinement, the spend gate, dispatch,
the change request row, the preview artifact, the "what to check" list and both feedback buttons through
the real services, in order, against a real Postgres. Three things are stubbed and nothing else: the
model client, so refinement spends no tokens; the version-control provider, so no pull request is
opened anywhere; and the deployment provider, so no environment is created.

If you want to watch the loop work today, that is the honest way to do it:

```bash
docker compose up -d postgres
CHARTER_TEST_DATABASE_URL=postgres://charter:$(grep POSTGRES_PASSWORD .env | cut -d= -f2)@localhost:5432/charter \
  dotnet test tests/Charter.Tests --filter FullyQualifiedName~ApiPhaseOneLoopTests
```

The Postgres service is not published to the host by default — uncomment the `ports` block in
`docker-compose.yml` first, or point the variable at a Postgres you already have. Without
`CHARTER_TEST_DATABASE_URL` the database-backed tests skip rather than fail, and a green run tells you
nothing.

## Where to go next

- [the-loop.md](the-loop.md) — every state, every plain-language label, and what happens when it goes
  wrong
- [api.md](api.md) — the HTTP surface, grouped by who calls it
- [configuration.md](configuration.md) — every environment variable
- [self-hosting.md](self-hosting.md) — Railway, Render, Fly, TLS, backup and restore
- [runners.md](runners.md) — when the default backend is not enough
- [security.md](security.md) — read this before pointing Charter at code that matters
