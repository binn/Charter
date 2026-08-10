# Upgrading

Charter is pre-1.0 and moving quickly. Expect breaking changes, read the release notes, and take a
backup before any upgrade that touches the database.

## Before you upgrade

1. **Read the release notes.** They are on the GitHub release and, if update checks are on, rendered
   inline in Charter's settings. Two things to look for: whether the release is flagged as a security
   release, and whether it includes schema migrations.

2. **Back up Postgres.**

   ```bash
   pg_dump --format=custom --no-owner --file=charter-pre-upgrade-2026-08-10.dump \
     "postgres://charter:8mK2vQ9xLp@localhost:5432/charter"
   ```

   With Compose:

   ```bash
   docker compose exec -T postgres \
     pg_dump --format=custom --no-owner -U charter charter > charter-pre-upgrade-2026-08-10.dump
   ```

3. **Confirm you have `CHARTER_CREDENTIAL_KEY` stored somewhere other than the machine you are
   upgrading.** A dump restored without it contains credential ciphertext you cannot decrypt.

4. **Let running sessions finish**, or accept that they will be re-queued. In-flight jobs are marked
   for retry on graceful shutdown, but a session halfway through a build restarts rather than resuming
   from where it stopped.

## Upgrading

### Docker Compose

```bash
cd /srv/charter
docker compose pull
docker compose up -d
```

Migrations run automatically on boot. Watch the logs until the application reports it is listening:

```bash
docker compose logs -f charter-app
```

### PaaS

Redeploy from your platform's UI or CLI, or let it deploy on push if you have that configured.
Migrations run on boot in exactly the same way.

Give the platform a shutdown grace period long enough for the old container to drain — Charter releases
its advisory locks and marks claimed jobs for retry on `SIGTERM`, and cutting it short strands work that
then waits for a lease timeout.

### Charter Agents

Agents are upgraded separately from the control plane. They negotiate a protocol version on connect: a
mismatch produces a clear message and a refusal to claim work, rather than subtle failures three
sessions later.

An agent that refuses to claim work after a control-plane upgrade needs its binary replaced. Agents
auto-update only if you opt in; the default is to warn and let you upgrade deliberately.

## Migrations

**EF Core migrations run automatically at boot.** There is no separate migration command to run and no
maintenance window to schedule for the normal case.

- **Migrations must run cleanly on a six-month-old instance.** You can skip versions. Upgrading from an
  instance several releases behind applies the intervening migrations in order.
- **Migrations are forward-only.** There is no automated downgrade. Rolling back a release that applied
  a migration means restoring your backup, which is the reason step 2 above is not optional.
- **When a release includes schema migrations, the update notification says so** before you pull, so
  you know a backup is warranted. If you have disabled update checks, the release notes carry the same
  flag.

### If a migration fails

Charter fails to start rather than serving traffic against a half-migrated database. The startup logs
name the migration that failed.

1. Do not repeatedly restart the container hoping it clears. It will not.
2. Restore your pre-upgrade dump to a scratch database and confirm it is intact.
3. Roll back to the previous image against your restored database.
4. Open an issue with the migration name and the error.

## Rolling back

Rolling back the application image alone is safe **only if the release applied no migrations**. Check
the release notes first.

If migrations ran, roll back both:

```bash
# 1. Stop the application
docker compose stop charter-app

# 2. Restore the pre-upgrade dump into a fresh database
docker compose exec -T postgres createdb -U charter charter_rollback
docker compose exec -T postgres pg_restore --no-owner -U charter -d charter_rollback \
  < charter-pre-upgrade-2026-08-10.dump

# 3. Point DATABASE_URL at charter_rollback, pin the previous image tag, and start
docker compose up -d
```

Use the same `CHARTER_CREDENTIAL_KEY` as before, or every stored credential will read as `invalid`.

Anything created after the backup is lost by a rollback: requests, specs, sessions, events, and audit
entries. Guardrails are not, because they live committed in your repositories rather than in Charter's
database.

## Version conventions

Charter follows semantic versioning, and `CHANGELOG.md` follows Keep a Changelog. Every release has a
changelog entry before it has a tag.

| Change | Version bump | What to expect |
|---|---|---|
| Bug fix, no configuration or schema change | Patch | Pull and restart. |
| New feature, backward-compatible configuration, additive migrations | Minor | Pull and restart. New variables have defaults. |
| Removed or renamed environment variable, changed default, removed API, incompatible schema change | Major | Read the release notes in full. Migration steps are listed there. |

Pre-1.0, breaking changes can land in minor releases. That is what pre-1.0 means, and the release notes
carry the detail. Pin an exact image tag rather than `latest` if you would rather choose when to
absorb a change.

Breaking-change conventions:

- **A removed environment variable is a startup failure, not a silent default.** Charter validates the
  whole configuration up front and reports every problem at once, so a variable that has been renamed
  tells you at boot rather than at first use.
- **A renamed variable is honoured under its old name for at least one minor release**, with a startup
  warning naming the replacement, before it becomes an error.
- **Security releases are marked distinctly** in the release, rendered non-dismissibly in Charter's
  settings, and state the severity plainly. Treat them as out-of-cycle.

## Two schemas, versioned independently

Charter has two schemas that change at different rates and for different reasons. **They are versioned
independently and neither implies the other** ([spec §24](../agent-docs/spec.md)).

| Schema | Where it lives | How it changes |
|---|---|---|
| **Database schema** | Your Postgres | EF Core migrations, applied automatically on boot |
| **`.charter/` schema** | Committed in each target repository | `version:` key at the top of each YAML file |

The practical consequences:

- Upgrading Charter does not require touching any repository's `.charter/` folder. A `version: 1`
  config keeps working.
- Adding a `.charter/` key for a feature your instance does not have yet is safe. **Unknown keys warn
  and never fail**, so a repository written for a newer Charter runs on an older instance.
- The cost of that rule is that a mistyped key warns rather than erroring. If a setting appears to do
  nothing, check your startup logs for a warning about an unrecognised key before assuming a bug.
- A `.charter/` schema version bump is reserved for changes the warn-and-ignore rule cannot absorb, and
  will state what an older instance does with the file.

The standards repository is a third, separate case: `standards.yml` is versioned, and each repository
pins the version it was created under, so tightening a standard does not retroactively mark existing
repositories non-compliant. See [standards.md](standards.md).

## Update notifications

Charter checks GitHub once a day for a new release and surfaces it to admins and engineers only. A
requester has no action to take and never sees it.

- The badge and banner are dismissible **per version** — dismissing one release does not suppress the
  next.
- Release notes render inline, sanitised, with a link to the full release.
- Security releases render persistently and cannot be dismissed.
- Releases including schema migrations are flagged as such, so you know to take a backup first.

Turn the check off with `CHARTER_UPDATE_CHECK=false`. What it does and does not send is documented in
[privacy.md](privacy.md).

## Related

- [self-hosting.md](self-hosting.md) — backup, restore, and verifying a restore
- [configuration.md](configuration.md) — variables referenced here
- [privacy.md](privacy.md) — the update check
- [charter-folder.md](charter-folder.md) — `.charter/` versioning and forward compatibility
- [spec §24 and §28](../agent-docs/spec.md) — repository conventions and update notification
