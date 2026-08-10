# Self-hosting

Charter's control plane needs exactly two things: one HTTP port and a Postgres URL. Everything else is
optional or pluggable.

This page covers Docker Compose on your own machine or VPS, and the three PaaS platforms Charter is
designed to run on: Railway, Render, and Fly. It ends with backup, restore, and shutdown behaviour.

## Before you start

You need:

- PostgreSQL 16 or newer.
- A GitHub App (App ID, private key, webhook secret). Charter uses it to read repositories, open pull
  requests, and receive webhooks.
- At least one model credential — an Anthropic API key, an OpenRouter key, or an account linked in the
  app after first boot.
- Two random secrets of at least 32 bytes each:

  ```bash
  openssl rand -base64 48    # CHARTER_SECRET_KEY
  openssl rand -base64 48    # CHARTER_CREDENTIAL_KEY
  ```

Full variable reference: [configuration.md](configuration.md).

## First run

However you deploy, the first boot is the same. Charter starts with zero users, enters **setup mode**,
serves nothing but the setup route, and writes a **one-time setup token to stdout**. Read it from your
container logs:

```bash
docker compose logs charter-app | grep -i "setup token"
```

That token creates exactly one admin account and then expires. Setup mode ends permanently and cannot
be re-entered while a user exists. There is no default password and no open registration form.

If the setup token scrolls past and you lose it, restart the container while no user exists — a new
one is issued.

## Docker Compose

The repository ships a `docker-compose.yml` with two services. Clone, fill in `.env`, and start:

```bash
git clone https://github.com/binn/Charter.git
cd Charter
cp .env.example .env
docker compose up -d
```

Charter comes up on `http://localhost:8080` in personal mode.

If you are writing your own Compose file, this is the shape:

```yaml
services:
  charter-app:
    image: ghcr.io/binn/charter:latest
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      DATABASE_URL: postgres://charter:8mK2vQ9xLp@postgres:5432/charter
      CHARTER_BASE_URL: https://charter.example.com
      CHARTER_SECRET_KEY: Yb7dGq1sPz4hRk9wXn2vTc6mAe8jUf3L
      CHARTER_CREDENTIAL_KEY: Qh4tNw8bVr2kZs6yEd1pMg9xLc7uJa5F
      CHARTER_RUNNER: docker
      LOGGING_MODE: DEFAULT
    depends_on:
      postgres:
        condition: service_healthy
    stop_grace_period: 120s

  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: charter
      POSTGRES_PASSWORD: 8mK2vQ9xLp
      POSTGRES_DB: charter
    volumes:
      - charter-pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U charter -d charter"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  charter-pgdata:
```

Replace the passwords and keys with your own. Only Postgres gets a volume — the application container
holds no durable state.

### Putting it behind TLS

`CHARTER_BASE_URL` must be the public HTTPS URL GitHub can reach, not `localhost`. Terminate TLS in
Caddy, nginx, or your existing reverse proxy and forward to port 8080. GitHub webhook delivery and
OAuth callbacks both depend on that URL being correct and publicly resolvable.

### Runner choice on Compose

Compose deployments can use any backend. `CHARTER_RUNNER=docker` is the simplest: Charter spawns
sibling containers through the host's Docker socket.

**Mounting the Docker socket into the application container grants that container root-equivalent
access to the host.** That is an acceptable trade on a machine dedicated to Charter, and a bad one on
a machine that runs anything else. The alternative is `CHARTER_RUNNER=agent`, which keeps the socket on
a separate host entirely. See [runners.md](runners.md).

## PaaS platforms

### What PaaS constrains

Railway and comparable platforms prohibit privileged containers and block Docker daemon access, and
these constraints shape how Charter behaves everywhere ([spec §2.3](../agent-docs/spec.md)):

- **No durable local disk.** Transcripts, diffs, and artifacts go to Postgres or S3-compatible storage,
  never the container filesystem. Anything written to the container is gone on the next deploy.
- **The container can restart mid-session.** Charter holds no orchestration state in memory; every
  session is fully resumable from Postgres alone.
- **The job queue is Postgres**, claimed with `SELECT ... FOR UPDATE SKIP LOCKED`. No Redis, no second
  service to babysit.
- **Scaling to two replicas is safe.** The dispatcher takes a Postgres advisory lock so replicas do not
  double-dispatch.
- **One HTTP port**, all configuration in environment variables, migrations on boot.

The direct consequence: **Charter cannot spawn its own sandbox containers on a PaaS.** Agent execution
has to happen somewhere else, which is why `CHARTER_RUNNER` defaults to `github-actions` — the only
backend that needs no infrastructure of yours at all. If you want faster builds, or you need macOS,
Windows, GPU, or hardware access, run a Charter Agent on your own machine and point it at your PaaS
instance; it dials outbound, so the PaaS never needs to reach in ([runners.md](runners.md)).

Health endpoints for platform probes: `/health` and `/ready`.

### Railway

1. Create a project and add a **PostgreSQL** service.
2. Add a service from the Charter repository or the published image.
3. Set the variables below. `PORT` is provided by Railway — do not set it.

```bash
DATABASE_URL=${{Postgres.DATABASE_URL}}
CHARTER_BASE_URL=https://charter.up.railway.app
CHARTER_SECRET_KEY=Yb7dGq1sPz4hRk9wXn2vTc6mAe8jUf3L
CHARTER_CREDENTIAL_KEY=Qh4tNw8bVr2kZs6yEd1pMg9xLc7uJa5F
CHARTER_RUNNER=github-actions
LOGGING_MODE=RAILWAY_JSON
ANTHROPIC_API_KEY=sk-ant-api03-EXAMPLEKEYNOTREAL
GITHUB_APP_ID=1234567
GITHUB_APP_PRIVATE_KEY=LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQo=
GITHUB_WEBHOOK_SECRET=whsec_7fQ2mNp9RtVx4Kc1
```

**Use `DATABASE_URL`, not `DATABASE_PUBLIC_URL`.** Railway exposes both. `DATABASE_URL` resolves to the
private network address; `DATABASE_PUBLIC_URL` routes over the public internet, is slower, and is
billed as egress. The application should never use the public one.

Set `LOGGING_MODE=RAILWAY_JSON` so Railway renders Serilog properties as filterable log attributes.

Railway PR Environments replicate every service, database, and variable from a base environment into an
isolated ephemeral environment with fresh URLs — which is what makes preview links work. **Base them
off a staging environment, not production**, so preview secrets are never real ones. Note also that
Railway will not deploy a PR branch from someone outside the workspace unless they have been invited
with that GitHub account.

### Render

1. Create a **PostgreSQL** instance and copy its internal connection string.
2. Create a **Web Service** from the repository, using the Dockerfile.
3. Set the health check path to `/health`.
4. Set the same variables as above, with `LOGGING_MODE=JSON` and `CHARTER_RUNNER=github-actions`.

Render provides `PORT`; Charter honours it. Preview environments report back through the generic
deployment webhook rather than PR comment parsing:

```
POST /api/deployments/{prSha}
{ "url": "https://charter-pr-142.onrender.com", "state": "ready", "provider": "render" }
```

Point a post-deploy hook at that endpoint on your instance and preview binding works the same as it
does on Railway.

### Fly

1. `fly postgres create`, then `fly postgres attach` to set `DATABASE_URL` on the app.
2. Deploy the Dockerfile with `fly deploy`.
3. Set secrets rather than plain environment variables for anything sensitive:

```bash
fly secrets set CHARTER_SECRET_KEY=Yb7dGq1sPz4hRk9wXn2vTc6mAe8jUf3L \
                CHARTER_CREDENTIAL_KEY=Qh4tNw8bVr2kZs6yEd1pMg9xLc7uJa5F \
                GITHUB_WEBHOOK_SECRET=whsec_7fQ2mNp9RtVx4Kc1
```

In `fly.toml`, set the internal port to `8080`, add an HTTP health check against `/health`, and set
`auto_stop_machines = false`. Charter runs background hosted services — the session orchestrator and
the queue dispatcher — and a machine that suspends on idle will not dispatch queued work.

Fly can run privileged workloads, so `CHARTER_RUNNER=docker` is technically possible with a
Docker-in-Docker setup. It is not the recommended path; a Charter Agent on a machine you control is
simpler to reason about and faster.

## Which runner backend, by platform

| Platform | Default | Why |
|---|---|---|
| Docker Compose on a VPS | `docker` or `agent` | The host already has Docker. `agent` keeps the socket off the app container. |
| Railway | `github-actions` | Privileged containers prohibited, Docker daemon unavailable. |
| Render | `github-actions` | Same constraint. |
| Fly | `github-actions` | Same by default; `agent` if you have a machine to spare. |
| Any platform, non-web projects | `agent` | macOS, Windows, GPU, licensed toolchains, and USB hardware cannot come from a hosted backend. |

`CHARTER_RUNNER` accepts several values at once. Enabling `github-actions` and `agent` together lets
the dispatcher route each session to whichever backend advertises the capabilities it needs.

## Backup and restore

### What to back up

**Postgres is the entire state of your instance.** Requests, specs, sessions, events, credentials,
budgets, and the audit log all live there. Back it up and you can rebuild everything else.

```bash
pg_dump --format=custom --no-owner --file=charter-2026-08-10.dump \
  "postgres://charter:8mK2vQ9xLp@localhost:5432/charter"
```

With Compose, run it against the database container:

```bash
docker compose exec -T postgres \
  pg_dump --format=custom --no-owner -U charter charter > charter-2026-08-10.dump
```

Also keep, outside the database:

- `CHARTER_CREDENTIAL_KEY`. **Lose this and every stored credential in the dump is unrecoverable** —
  the ciphertext is intact and worthless. Store it where you store your other secrets, not only in the
  environment of the machine you are backing up.
- `CHARTER_SECRET_KEY`. Losing it only signs everyone out.
- Your S3 bucket, if you store verification artifacts there. Build artifacts have an `expires_at` and
  are pruned by design, so they are usually not worth backing up.

### What is safe to lose

- The `.charter/cache/` folder in target repositories — generated recon output, gitignored, regenerated
  on demand.
- Runner caches, git mirrors, and prebuilt images. All rebuild themselves, slowly.
- Expired verification artifacts. They are ephemeral by design.
- `Event` rows past your retention window. They are the largest table by orders of magnitude and are
  pruned on a schedule anyway. Losing old events costs you transcript history for closed sessions, not
  functionality.

Everything that constitutes a guardrail — path scopes, checks, conventions, glossary, standards —
lives committed in your own Git repositories, not in Charter. That is deliberate. Your repos are your
backup for policy ([charter-folder.md](charter-folder.md)).

### Restoring

```bash
createdb -h localhost -U charter charter_restored
pg_restore --no-owner --dbname=charter_restored charter-2026-08-10.dump
```

Then point a Charter instance at the restored database with the **same** `CHARTER_CREDENTIAL_KEY` and
start it. Migrations run on boot, so a dump from an older version upgrades forward automatically.

### Verifying a restore

A backup you have never restored is a hypothesis. Check, in this order:

1. The instance boots and does not enter setup mode. Setup mode means it found zero users, which means
   the restore did not land.
2. Sign in as an existing admin. That exercises the identity tables.
3. Open Settings and confirm your linked credentials show as `active` with their provider and owner.
   If they show as `invalid`, your `CHARTER_CREDENTIAL_KEY` does not match the one used at backup time.
4. Open a completed session and confirm the event stream renders. That exercises the largest table.
5. Open the audit log and confirm entries predate the backup.

Do this against a restored copy on a spare database, not your live one.

## Graceful shutdown

On `SIGTERM`, Charter drains rather than dropping work ([spec §31](../agent-docs/spec.md)):

- Stops claiming new jobs from the queue.
- Lets in-flight control-plane work finish.
- Releases its Postgres advisory locks so another replica can take over dispatching immediately.
- Marks jobs it had claimed for retry, so they are picked up rather than stranded.

Give the container time to do this. In Compose, `stop_grace_period: 120s`; on PaaS, raise the platform's
shutdown grace period if it defaults to something short.

Sessions already executing on a runner are unaffected by a control-plane restart. The Charter Agent
holds a lease renewed by heartbeat, so a control plane that comes back within the lease window picks
the session straight back up. A control plane that stays down past the lease TTL causes the job to be
re-queued.

## Upgrading

Read [upgrading.md](upgrading.md) before pulling a new image, particularly when the release notes flag
schema migrations.

## Related

- [configuration.md](configuration.md) — every environment variable
- [runners.md](runners.md) — backends and the Charter Agent
- [upgrading.md](upgrading.md) — migrations and backups before an upgrade
- [security.md](security.md) — threat model and trust boundary
- [spec §2 and §31](../agent-docs/spec.md) — architecture and operational requirements
