---
title: "Upgrading"
description: "Pre-upgrade checks, upgrading on Docker Compose and PaaS, Charter Agents, migrations and what to do when one fails, rolling back, version conventions, and update notifications."
---

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

### The GitHub Actions workflow file: a breaking change before 1.0

**If you use `CHARTER_RUNNER=github-actions`, every target repository needs the current
`.github/workflows/agent-session.yml`.** The credential exchange now requires two things instead of
one, and a workflow file from before this change sends only the first.

What changed, and why it is not something that can be phased in gently:

- `secrets.CHARTER_SESSION_SECRET` is unchanged. It is still derived the same way, still written once
  per repository, and **you do not need to rotate it**. Nothing you have stored becomes invalid.
- The exchange additionally requires `client_payload.session_token`, which Charter mints per session
  and sends only in that session's dispatch. The workflow forwards it in the request body as
  `session_token`.

The reason both are needed is that the repository secret is the same value for every workflow run in
the repository. It proves the caller is a run in that repository; it cannot prove which session is
asking. Until this change, any run in a repository could name any other live session of the same
repository and be handed that session's repository token, its callback token, and with it the ability
to write that session's transcript and report it failed. No derivation of the repository secret can fix
that — every holder of the secret could compute the same value — so the second factor has to come from
the control plane, per dispatch. Continuing to accept the old one-factor request during a transition
would have left the hole open for exactly as long as the transition lasted, which is why there is no
compatibility window.

**What you will see if you miss a repository.** The workflow's first step fails immediately with a
`403` naming the file to update. Nothing runs, nothing is half-done, and no credential is issued. Fix
it by copying the current `agent-session.yml` into the repository.

This is a pre-1.0 breaking change of the kind the [changelog](https://github.com/binn/Charter/blob/master/CHANGELOG.md)
warns about: until 1.0, breaking changes ship without a deprecation cycle.

### Repositories renamed on GitHub must be reconnected

Charter now checks the run URL a runner reports against the repository the session belongs to, and
refuses the callback when the two disagree — see [runners.md](runners.md) and
[security.md](security.md) for why. One honest case trips that check: **a repository renamed on GitHub
whose new name was never recorded in Charter.** The workflow reports the new name, Charter has the old
one, and the session fails at its first callback with a message naming the repository on record.

Before upgrading, check that every connected repository's full name matches what GitHub calls it
today. Where it does not, reconnect the repository in Charter's settings. There is no setting that
relaxes the check, and there is no migration for it: the fix is to make the two agree.

Sessions that already ran are unaffected. References recorded by an older version are not rewritten,
and they are not acted on either — cancelling such a session reports that nothing was stopped rather
than issuing a call, which is visible as a warning in the log.

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

### Pre-1.0: the initial migration is amended in place

Charter has no released versions yet, so the single `InitialCreate` migration is **edited rather than
superseded** while the schema is still settling. A development database created before such an edit
will not pick the change up — the migration is already recorded as applied — and Charter will run
against a schema that no longer matches the model.

Until 1.0, if you are tracking `master` and the schema changed, drop and recreate your development
database rather than trying to migrate it:

```bash
docker compose exec -T postgres psql -U charter -d postgres \
  -c 'DROP DATABASE charter;' -c 'CREATE DATABASE charter OWNER charter;'
```

This applies to development instances only. From 1.0, migrations are additive and forward-only, and
this section goes away.

#### Tables added by the latest amendment

Four tables joined `InitialCreate`. All four are additive — nothing was renamed or dropped, and no
existing row needs a backfill — but three of them change what an instance keeps between restarts, so
they are worth knowing about.

| Table | What it holds | What you would notice |
|---|---|---|
| `notification_channels` | One row per user per channel, keyed on the pair | Notification preferences survive a restart. **A user with no rows keeps the default — email on, Slack and Discord off** — so nothing needs backfilling and nobody's notifications change on upgrade |
| `email_deliveries` | Recent attempted sends: recipient, template name, outcome, and the mail server's own words on a failure | The admin settings delivery list is no longer empty after a redeploy. Retention is enforced by Charter — 30 days or 2,000 rows, whichever comes first — and pruning is automatic |
| `invitations` | Outstanding and spent invitations (§30.2): email, roles, inviter, expiry, and a **SHA-256 digest of the token, never the token** | An invitation link works across a restart. It is single-use and expires after 7 days; a lost link is reissued, never recovered — nothing in the database can reproduce it |
| `explain_this_usage` | One counter row per user per UTC day for the *explain this* teaching cap | A reader's daily allowance is no longer reset by a deploy. Rows older than 30 days are dropped as the reader's next explanation is counted |

`concept_ledger` also gained `last_referenced_at`. It is what orders the capped "most recent
concepts" window Charter passes into a teaching prompt; without it, teaching re-explains what it has
already taught.

**On backups and privacy.** `email_deliveries` records the address each message went to, and
`invitations` records the address each invitation was sent to. Both are now in your database dumps.
Neither holds message content, and both are pruned. See [privacy.md](privacy.md).

#### Columns added by the latest amendment

Three columns joined `InitialCreate`. All three are additive, and none needs a backfill — but two of
them change behaviour you will see, so drop and recreate your development database as above rather
than assuming the old schema still works.

| Column | What it holds | What you would notice |
|---|---|---|
| `recaps.payload` | The engineer recap as structured data — summary, deviations, what could not be verified, and the specification in full for an auto-dispatched session | Nothing, immediately. `body_md` is unchanged and is still what gets posted as a change request comment. A recap row written before this column existed stores `{}` and reads as *absent*, so a pre-existing recap keeps rendering from its prose rather than coming back empty |
| `ledger_entries.estimated_usd` | What a budget hold predicted, kept after settlement replaces it with the actual | Estimate-versus-actual becomes measurable, which is what lets the estimator improve. Zero on rows written before budgets were enforced — that is *no estimate was recorded*, not *it was free* |
| `ledger_entries.estimated_quota_sessions` | The same, for subscription-backed work | As above |

`recaps.risk_items` gained `additions`, `deletions` and `kind` per file. Older rows do not have them
and render as they did before: a zero count means **nobody counted**, never that nothing changed.

### Budgets start enforcing on this upgrade

If your instance is in **personal mode, nothing changes.** Personal mode has no budgets — one person,
their own credentials, nothing to govern — and there are no budget rows to create.

If your instance is in **organisation mode**, spend is now estimated before each session and held
against every budget that applies to it, then settled against the actual when the session ends.
Budgets you have not created do not exist and do not cap anything; an instance with no `budgets` rows
behaves exactly as it did.

Two things are worth knowing before you add your first budget:

- **Reservations are held for two hours.** A session that starts and never finishes — a control plane
  killed mid-run — holds its estimate against your caps until the TTL passes, at which point it stops
  counting and is released automatically. You do not need to intervene.
- **A model nothing can price cannot be capped in dollars.** Self-hosted and gateway models with no
  published rates estimate zero and pass every `usd` budget. Use a `quota_sessions` budget if you need
  a limit on those. See [budgets.md](budgets.md).

### Object storage needs `CHARTER_STORAGE_BACKEND` to be set

Object storage works now — a filesystem directory or any S3-compatible bucket. There is one change to
make if you already had the bucket variables set.

**Charter no longer infers the backend from which variables are set.** If your instance has
`CHARTER_STORAGE_ENDPOINT` and friends, add:

```bash
CHARTER_STORAGE_BACKEND=s3
```

Without it the boot stops, on purpose:

```
[FAIL] object storage: CHARTER_STORAGE_ENDPOINT is set and CHARTER_STORAGE_BACKEND is not, so
nothing would read it -> set CHARTER_STORAGE_BACKEND to the backend you meant (filesystem or s3),
or unset CHARTER_STORAGE_ENDPOINT. ...
```

Inference is what makes a typo dangerous: `CHARTER_STORAGE_BUCKETT` under a rule that guesses the
backend produces an instance that boots, selects something else, and tells nobody.

Nothing is lost either way. No previous release ever wrote to your bucket — the variables parsed,
validated, and reached no client — so there is nothing in it to migrate. Verification artifacts
continue to live in Postgres; what moves to the store from this release on is oversized transcript
output, and only on the sessions that run after you configure it.

If you do not want object storage, leave `CHARTER_STORAGE_BACKEND` unset and unset the rest of the
block. That is the supported configuration, and the only correct one on a platform whose filesystem
does not survive a deploy. See [configuration.md](configuration.md).

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
independently and neither implies the other** ([spec §24](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)).

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
- Security releases render persistently and cannot be dismissed. They carry `[SECURITY]` in the title.
- Releases including schema migrations are flagged as such, so you know to take a backup first. They
  carry `[MIGRATIONS]` in the title or in the release body.
- The check runs on Charter's job queue, so one replica performs it and a restart does not lose the
  schedule. An instance that cannot reach GitHub keeps the answer it last had and logs nothing above
  debug — there is no daily error to filter out of an air-gapped deployment's logs.

Turn the check off with `CHARTER_UPDATE_CHECK=false`. What it does and does not send is documented in
[privacy.md](privacy.md).

## Related

- [self-hosting.md](self-hosting.md) — backup, restore, and verifying a restore
- [configuration.md](configuration.md) — variables referenced here
- [budgets.md](budgets.md) — what the budget columns above are for
- [privacy.md](privacy.md) — the update check
- [charter-folder.md](charter-folder.md) — `.charter/` versioning and forward compatibility
- [spec §24 and §28](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — repository conventions and update notification
