---
title: "Configuration"
description: "Every environment variable Charter reads, the flat-variable convention that replaces appsettings.json, and exactly what startup validation checks before the app will boot."
---

# Configuration

Charter is configured entirely through flat environment variables. There is no `appsettings.json`, no
`Section__Nested__Key` convention, and no config file to mount.

Every variable is read once at startup into an immutable config object. If anything is missing or
malformed, Charter prints **all** of the problems at once and exits with a non-zero status. It never
starts in a half-working state and never fails lazily on first use. See [spec §4.1](https://github.com/binn/Charter/blob/master/agent-docs/spec.md).

## Minimum viable configuration

This is enough to boot:

```bash
DATABASE_URL=postgres://charter:8mK2vQ9xLp@postgres:5432/charter
CHARTER_BASE_URL=https://charter.example.com
CHARTER_SECRET_KEY=Yb7dGq1sPz4hRk9wXn2vTc6mAe8jUf3L
CHARTER_CREDENTIAL_KEY=Qh4tNw8bVr2kZs6yEd1pMg9xLc7uJa5F
ANTHROPIC_API_KEY=sk-ant-api03-EXAMPLEKEYNOTREAL
GITHUB_APP_ID=1234567
GITHUB_APP_PRIVATE_KEY=LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQo=
GITHUB_WEBHOOK_SECRET=whsec_7fQ2mNp9RtVx4Kc1
```

Generate the two key values with something that produces at least 32 bytes:

```bash
openssl rand -base64 48
```

Use a different value for each. They are not interchangeable.

## Core

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_BASE_URL` | yes | — | Public URL of this instance. Used to build webhook targets and links in notifications. Must be reachable by GitHub. |
| `CHARTER_SECRET_KEY` | yes | — | At least 32 bytes. Signs cookies and tokens. |
| `PORT` | no | `8080` | The single HTTP port. PaaS platforms set this for you. |
| `CHARTER_MODE` | no | `personal` | `personal` or `organization`. Personal mode is an organisation with one member holding every role and approval gates auto-satisfied — not a separate code path. |
| `CHARTER_DEMO` | no | `false` | Seeds a fake organisation, repo, and completed sessions, and disables all outbound calls. For evaluating Charter without connecting a GitHub App or spending a token. |

## Database

| Variable | Required | Default | Notes |
|---|---|---|---|
| `DATABASE_URL` | yes | — | A `postgres://` or `postgresql://` URL. Not an ADO.NET connection string. |

PostgreSQL 16 or newer. It is the only external dependency — no Redis, no separate queue service.
EF Core migrations run automatically on boot.

### DATABASE_URL parsing

Npgsql does not accept URI-form connection strings, so Charter converts the URL itself. The rules
([spec §4.3](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)):

- Both `postgres://` and `postgresql://` schemes are accepted. Anything else is a startup error.
- Username and password are URL-decoded, so passwords containing `@`, `/`, or `:` work if they are
  percent-encoded in the URL. A password of `p@ss/word` is written `p%40ss%2Fword`.
- The port defaults to `5432` when absent.
- `sslmode` is mapped to the Npgsql `SSL Mode` setting:

  | `sslmode` | Result |
  |---|---|
  | `disable` | `SSL Mode=Disable` |
  | `require` | `SSL Mode=Require` plus `Trust Server Certificate=true` |
  | `verify-full` | `SSL Mode=VerifyFull` — the certificate chain and hostname are checked |
  | absent or empty | Treated as `require` |
  | anything else | Startup error naming the unsupported value |

- A missing scheme, host, or database name is rejected with a message saying which part is missing.

Working examples:

```bash
# Local Compose, no TLS between containers on a private network
DATABASE_URL=postgres://charter:8mK2vQ9xLp@postgres:5432/charter

# Managed Postgres over the public internet, certificate fully verified
DATABASE_URL=postgresql://charter:8mK2vQ9xLp@db.us-east.example.com:5432/charter?sslmode=verify-full

# Password containing reserved characters, percent-encoded
DATABASE_URL=postgres://charter:p%40ss%2Fword@postgres:5432/charter
```

`sslmode=require` trusts whatever certificate the server presents. That protects against passive
sniffing but not against an active man-in-the-middle. Use `verify-full` when the database is reachable
over an untrusted network.

On Railway, use `DATABASE_URL` — it resolves to the private network address. `DATABASE_PUBLIC_URL`
also exists and should not be used by the application; it routes traffic out over the public internet
and is billed as egress. See [self-hosting.md](self-hosting.md).

## Authentication

Email and password is always available and needs no configuration. Each OAuth provider turns on when
both halves of its pair are set; setting only one is a startup error.

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_OAUTH_GITHUB_ID` / `CHARTER_OAUTH_GITHUB_SECRET` | no | — | Enables GitHub sign-in when both are set. |
| `CHARTER_OAUTH_GOOGLE_ID` / `CHARTER_OAUTH_GOOGLE_SECRET` | no | — | |
| `CHARTER_OAUTH_DISCORD_ID` / `CHARTER_OAUTH_DISCORD_SECRET` | no | — | Also links a Discord user to a Charter requester, which is what makes inbound Discord requests work. |
| `CHARTER_OAUTH_SLACK_ID` / `CHARTER_OAUTH_SLACK_SECRET` | no | — | Same double duty for Slack. |
| `CHARTER_SAML_METADATA_URL` | no | — | Organisation mode only. |

Password reset needs email. See [Email](#email) below, including what happens when you have no mail
server.

## Email

Charter uses email for four things: invitations, notifications, two-factor recovery, and password
reset. One provider is supported — SMTP — which covers Amazon SES, Postmark, Mailgun, Resend's SMTP
endpoint, Google Workspace, Fastmail, and a relay you run yourself.

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_EMAIL_PROVIDER` | no | `smtp` when `CHARTER_SMTP_URL` is set, otherwise `none` | `smtp` or `none`. `resend` is recognised and rejected — it is not implemented in this build. |
| `CHARTER_SMTP_URL` | when `smtp` | — | `smtp://user:pass@host:port`. `smtps://` selects implicit TLS. Port defaults to 587 for `smtp` and 465 for `smtps`. |
| `CHARTER_SMTP_TLS` | no | `starttls`, or `implicit` for an `smtps://` URL | `none` \| `starttls` \| `implicit`. |
| `CHARTER_EMAIL_FROM` | no, but set it | guessed from the SMTP endpoint, with a startup warning | The address mail is sent as. Most providers reject a sender they were not configured for. |
| `CHARTER_EMAIL_FROM_NAME` | no | `Charter` | The display name beside the address. |
| `CHARTER_EMAIL_REPLY_TO` | no | — | Where replies go, if not to the sending address. |
| `CHARTER_EMAIL_MAX_PER_HOUR` | no | `20` | Messages per recipient per hour, counted separately for account mail and notifications. |

A working SMTP example:

```bash
CHARTER_EMAIL_PROVIDER=smtp
CHARTER_SMTP_URL=smtp://charter%40example.com:9jFq2XmT4bR@smtp.example.com:587
CHARTER_EMAIL_FROM=charter@example.com
CHARTER_EMAIL_FROM_NAME=Charter
```

The username and password are URL-decoded the same way as `DATABASE_URL`, so an address used as a
username needs its `@` percent-encoded as `%40`.

Send a test email from the email settings page once it is configured. Email misconfiguration is
otherwise discovered when an invitation silently fails and a new hire cannot log in.

### Running with no mail server

Charter runs without a mail server, and every feature that would have emailed you says so rather
than failing quietly:

- **Invitations** still work. An administrator creating an account is shown the one-time setup link
  to pass on themselves.
- **Password reset by email** is turned off, and the form says so. An administrator can generate a
  reset link from the members page. The link is never shown to whoever typed an address into the
  forgot-password form — anybody can type anybody's address into it.
- **Notifications** fall back to in-app only.

Every setting that depends on email is disabled with an explanation naming the variables to set.

### What is rate-limited, and why

Outbound mail is capped per recipient, in a sliding one-hour window, in two separate buckets:

| Bucket | Contains | Why it is separate |
|---|---|---|
| Account | invitations, password resets, test sends | Somebody is waiting for it |
| Notification | the two status emails Charter sends | Useful, but nobody is blocked on it |

Counting them together would let a burst of status mail starve the invitation a new hire is waiting
for. The counters are in memory, so a restart clears them — after a restart the correct behaviour is
to allow mail again, because the storm that justified holding it back is over.

### TLS is not downgraded silently

If `CHARTER_SMTP_TLS` is `starttls` and the server does not advertise `STARTTLS`, Charter stops
rather than continuing unencrypted. Use `implicit` for a TLS-only port, or `none` for a relay you
reach over a private network — and note that `none` with credentials in the URL warns at startup,
because the password crosses the wire in the clear.

The SMTP password is never written to a log. Log lines name the endpoint as `host:port`, never the
URL.

## Models and credentials

| Variable | Required | Default | Notes |
|---|---|---|---|
| `ANTHROPIC_API_KEY` | see below | — | Instance-level fallback credential. |
| `OPENROUTER_API_KEY` | see below | — | Instance-level fallback credential. |
| `CHARTER_CREDENTIAL_KEY` | yes | — | At least 32 bytes. Encrypts stored credentials at rest. Deliberately separate from `CHARTER_SECRET_KEY` so rotating the cookie key does not invalidate every linked account. |
| `CHARTER_ALLOW_SHARED_POOL` | no | `false` | Permits users to pool subscription credentials for other people's requests. Read [credentials.md](credentials.md) before enabling. |
| `CHARTER_MODEL_REFINE` | no | `openrouter/anthropic/claude-sonnet-5` | Model used for spec refinement, chat, and plan mode. |
| `CHARTER_MODEL_BUILD` | no | `claude-opus-5` | Model passed to the agent CLI for build sessions. Unqualified, so Anthropic's — see below. |
| `CHARTER_MODEL_TEACH` | no | `openrouter/anthropic/claude-sonnet-5` | Model used for walkthroughs, annotations, and the engineer recap. |

**At least one model credential must be resolvable at startup** — either `ANTHROPIC_API_KEY`,
`OPENROUTER_API_KEY`, or a credential grant already linked in the database. If none exists, startup
validation fails.

The `CHARTER_MODEL_*` variables accept provider-qualified identifiers:

```bash
CHARTER_MODEL_REFINE=anthropic/claude-sonnet-5
CHARTER_MODEL_BUILD=openrouter/deepseek/deepseek-r1
CHARTER_MODEL_TEACH=anthropic/claude-sonnet-5
```

An unqualified name such as `claude-sonnet-5` is treated as Anthropic. Only the first segment is the
provider, so `openrouter/deepseek/deepseek-r1` selects OpenRouter and asks it for
`deepseek/deepseek-r1`.

### Why two of the three defaults name OpenRouter and one does not

The defaults are not inconsistent — they follow the two surfaces Charter consumes models on, and the
distinction decides which credential each one needs:

| Variable | Who calls the model | Reached through |
|---|---|---|
| `CHARTER_MODEL_REFINE`, `CHARTER_MODEL_TEACH` | Charter itself, over HTTP | Any provider Charter has a credential for. Defaults to OpenRouter, which reaches every model from one key. |
| `CHARTER_MODEL_BUILD` | The agent CLI on the runner | Only what that CLI can authenticate against. |

So the build model defaults to a bare Anthropic name because the default adapter, Claude Code,
authenticates against the Anthropic API (or a gateway presenting it) and cannot present an OpenRouter
key. Pointing builds at OpenRouter is a supported and often cheaper choice — it needs an adapter that
speaks to OpenRouter natively:

```bash
CHARTER_MODEL_BUILD=openrouter/deepseek/deepseek-r1   # with the `pi` adapter
```

Charter refuses an impossible pairing when you assemble it, not when the session dispatches. See
[adapters.md](adapters.md) for which adapter reaches which provider.

**If your instance has only `ANTHROPIC_API_KEY`,** set the two control-plane variables explicitly —
`CHARTER_MODEL_REFINE=claude-sonnet-5` and `CHARTER_MODEL_TEACH=claude-sonnet-5` — or add an
`OPENROUTER_API_KEY`. The defaults assume the OpenRouter key because one key covers every model,
which is what makes it the cheaper starting point.

## GitHub

| Variable | Required | Default | Notes |
|---|---|---|---|
| `GITHUB_APP_ID` | yes | — | Numeric App ID from the GitHub App settings page. |
| `GITHUB_APP_PRIVATE_KEY` | yes | — | PEM private key. Base64-encoded PEM is also accepted, which is easier to pass through PaaS environment variable UIs that mangle newlines. |
| `GITHUB_WEBHOOK_SECRET` | yes | — | Must match the secret configured on the App's webhook. |
| `CHARTER_ALLOW_REPO_CREATION` | no | `false` | Allows Charter to create repositories from templates. Repo creation is a privilege escalation and is off by default. See [standards.md](standards.md). |

To pass the private key as a single-line value:

```bash
base64 -i charter-app.2026-08-10.private-key.pem | tr -d '\n'
```

## Runners

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_RUNNER` | no | `github-actions` | `agent`, `github-actions`, or `docker`. Comma-separate to enable several at once; the dispatcher then routes each session by capability match. |

Every backend, what it can run, and how to register a Charter Agent are in [runners.md](runners.md).

## Agent adapters

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_ADAPTERS_PATH` | no | — | Colon-separated directories of your own adapter YAML. Loaded after the shipped adapters; later directories win by adapter id. |

Adapters are data rather than code, so supporting a new coding agent is a YAML file instead of a
Charter release. The shipped set lives at `/app/adapters` in the container.

Set this to add your own adapters or to override a shipped one without forking:

```bash
CHARTER_ADAPTERS_PATH=/etc/charter/adapters:/opt/charter/local-adapters
```

A directory listed here that does not exist fails startup rather than being skipped. A typo would
otherwise present as an adapter that silently vanished, which is far harder to diagnose.

Two files in the same directory claiming the same adapter id is also an error, and names both files.
Overriding is something you do across directories, deliberately, not by accident within one.

The schema, the shipped adapters, and what each one can do are in [adapters.md](adapters.md).

## Preview environments

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_DEPLOYMENT_PROVIDER` | no | `none` | `none` or `railway`. |
| `CHARTER_RAILWAY_TOKEN` | when `railway` | — | Railway account or team token. |
| `CHARTER_RAILWAY_PROJECT_ID` | when `railway` | — | |
| `CHARTER_RAILWAY_BASE_ENVIRONMENT` | when `railway` | — | The environment previews are cloned from. Never defaulted. |
| `CHARTER_RAILWAY_API_URL` | no | `https://backboard.railway.com/graphql/v2` | |
| `CHARTER_PREVIEW_TTL_HOURS` | no | `72` | Preview lifetime where the platform does not expire it itself. `0` means never. |

Half-configuring a provider fails startup: setting `CHARTER_DEPLOYMENT_PROVIDER=railway` without a
token, project, and base environment reports all of the missing values at once.

### `none` is a supported configuration, not a gap

With no provider set, Charter still binds previews, produces verification artifacts, and expires
them. It simply does not create the deployment itself. Report your own:

```bash
curl -X POST https://charter.example.com/api/deployments/a3f9c21e4b7d8f0c1a2b3c4d5e6f7a8b9c0d1e2f \
  -H 'Content-Type: application/json' \
  -d '{"url": "https://pr-142.preview.example.com", "state": "ready", "provider": "render"}'
```

The head SHA is the authorisation: a report for a commit no change request carries returns 404.
Put it behind your own gateway if you want more than that.

### Point Railway at staging, not production

`CHARTER_RAILWAY_BASE_ENVIRONMENT` has no default on purpose.

Railway's PR environments replicate every service, variable, and database from the base environment.
Base them on production and each preview receives a copy of your production secrets — then runs an
agent-authored branch against them. Base them on staging and a leaked preview costs you nothing real.

Charter warns at startup when the value looks like production. That warning is a courtesy, not a
control; nothing stops you from naming production deliberately.

One Railway behaviour worth knowing: it will not deploy a change request branch from someone outside
the workspace unless they have been invited with that account. Charter detects this by absence — the
preview simply never arrives — and says so rather than leaving the requester waiting.

## Observability

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_LOG_LEVEL` | no | `information` | |
| `LOGGING_MODE` | no | `DEFAULT` | Console sink formatting. Values below. |
| `CHARTER_SEQ_URL` | no | — | Enables the Seq sink when set. The primary structured log target and the first place to look when debugging a session. |
| `CHARTER_SEQ_API_KEY` | no | — | |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | — | Standard OpenTelemetry variable. Enables OTLP export of logs, traces, and metrics when set. |
| `OTEL_EXPORTER_OTLP_HEADERS` | no | — | Standard OpenTelemetry variable, e.g. authentication headers for your collector. |
| `OTEL_SERVICE_NAME` | no | `charter` | Standard OpenTelemetry variable. |
| `CHARTER_LOG_INCLUDE_TRANSCRIPTS` | no | `false` | Writes agent transcript bodies into log properties. Read the warning below first. |

Seq and OTLP are complementary rather than alternatives. Run both if you have both.

### LOGGING_MODE

The console sink always exists, so its format has to suit wherever Charter is running.

| Value | Format | Use when |
|---|---|---|
| `DEFAULT` | Serilog's human-readable console template, coloured when the terminal supports it | Local development, `docker compose up`, reading logs by eye |
| `JSON` | One compact JSON object per line | Any platform that ingests stdout and parses JSON — Loki, Vector, Fluent Bit, CloudWatch |
| `RAILWAY_JSON` | One JSON object per line, shaped for Railway's structured log parser: a `message` string, a `level` string Railway recognises, and the remaining properties flattened alongside | Railway, which only renders structured fields as filterable attributes when they arrive in this shape |

An unrecognised value fails startup validation and lists the accepted ones. It does not silently fall
back, because discovering a logging misconfiguration during an incident is the worst possible time to
find it.

### Transcript leak warning

Agent transcripts contain repository content and the requester's business context. If they flow into
structured log properties, your source code has been exported into your log platform.

By default Charter logs event **metadata** only — type, timing, file paths, cost. Setting
`CHARTER_LOG_INCLUDE_TRANSCRIPTS=true` sends transcript bodies to every enabled sink, including Seq
and OTLP. Turn it on for a specific debugging session, then turn it off.

## Budgets

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_DEFAULT_SESSION_BUDGET_USD` | no | `5.00` | Per-session ceiling applied when no more specific budget exists. |
| `CHARTER_DEFAULT_MONTHLY_BUDGET_USD` | no | `100.00` | |

These are starting defaults, not the budget system. Nested budgets by team, repo, project, user, and
role are configured in the admin UI and stored in the database ([spec §34](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)).

## Privacy and updates

| Variable | Required | Default | Notes |
|---|---|---|---|
| `CHARTER_UPDATE_CHECK` | no | `true` | A once-daily unauthenticated read of the GitHub Releases API. The only outbound request Charter initiates on its own. |
| `CHARTER_UPDATE_CHANNEL` | no | `stable` | `stable` or `prerelease`. |

Set `CHARTER_UPDATE_CHECK=false` for air-gapped or privacy-strict deployments. Full disclosure of what
is and is not sent is in [privacy.md](privacy.md).

## Naming convention

Variables use the `CHARTER_` prefix except where an ecosystem-standard name already exists —
`DATABASE_URL`, `PORT`, `ANTHROPIC_API_KEY`, and the `OTEL_*` family. The standard name always wins,
so an existing OpenTelemetry collector configuration works unchanged.

## What startup validation checks

Charter validates the whole configuration before serving traffic and reports every problem in one
pass, then exits non-zero. Among the checks:

- `DATABASE_URL` is present, parses, and names a scheme, host, and database.
- `CHARTER_BASE_URL` is present and is an absolute URL.
- `CHARTER_SECRET_KEY` and `CHARTER_CREDENTIAL_KEY` are present and at least 32 bytes.
- At least one model credential is resolvable, from environment or database.
- All three GitHub App variables are present and the private key parses as PEM.
- `LOGGING_MODE`, `CHARTER_MODE`, `CHARTER_RUNNER`, and `CHARTER_UPDATE_CHANNEL` hold recognised values.
- OAuth provider pairs are complete — an ID without a secret is an error, not a silently disabled provider.

Separately, first-run preflight checks report on database reachability, applied migrations, whether
`CHARTER_BASE_URL` resolves, and whether at least one model credential actually works
([spec §30.1](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)). Preflight failures name the failing check and what to change.

## Related

- [self-hosting.md](self-hosting.md) — platform-specific deployment
- [privacy.md](privacy.md) — the outbound update check
- [credentials.md](credentials.md) — linking accounts instead of using instance-level keys
- [runners.md](runners.md) — choosing `CHARTER_RUNNER`
- [spec §4](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full configuration specification
