# Changelog

All notable changes to Charter are recorded here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Charter has no released versions yet. Development is in **Phase 1 — refinement only, no agent execution**: request intake, the refinement conversation, the spec confirmation card, and the approval queue. Agent execution, pull request creation, preview binding, teaching, and budgets arrive in later phases. The full build order is section 23 of [`agent-docs/spec.md`](agent-docs/spec.md).

Expect breaking changes without deprecation cycles until 1.0.

### Added

- Repository groundwork: license, contributor license agreement, trademark policy, security policy, contribution guide, and code of conduct.
- Identity behind a single provider seam: email and password (always available), plus GitHub, Google, Discord, and Slack OAuth when configured. Password verifiers use ASP.NET Core's `PasswordHasher`; the plaintext is never stored or logged. A federated sign-in links to an existing account and never creates one.
- Authorization: repository scope with deny by default, additive roles, transcript and code visibility gated on repository read access, and auto-dispatch policy resolution where a repository may only tighten organisation policy.
- First-run setup mode: an instance with zero users serves only the setup route and prints a one-time setup token to stdout. The token creates exactly one admin account, then setup ends permanently.
- Audit log write path, with authorization grants carrying the verb they should be recorded under.
- Refinement conversations are persisted, so a conversation survives the container restarting under it. Requester turns keep their type through storage: a reloaded turn refuses to be read as model-authored text.
- Model credentials: a `cursor_api_key` kind for the `cursor-agent` adapter, a separately tracked overflow allowance so the resolution chain's second tier can fire, a recorded reason when a credential is marked invalid, and a `429` with no reset header now records no reset time rather than a far-future placeholder.
- A refinement conversation read back out of Postgres becomes the live aggregate again, rules and all: the promotion gate, the unread-flag gate, and the confirmation. A confirmation is honoured only while the stored fingerprint still matches the stored spec, so a spec edited behind one comes back unconfirmed rather than dispatchable.
- The refinement thread is rebuilt from the stored turns. A clarifying question, an answer to it, or a turn submitted through `POST /api/requests/{id}/refinement` now survives a refetch instead of existing only in the live broadcast.
- User preferences are columns: theme, pane, and the requester onboarding timestamp join teaching level, so `PATCH /api/me/preferences` writes something instead of accepting the change and dropping it. An unchosen pane is stored as unchosen, which is what lets §12's role default — requesters to pane 1, engineers to pane 3 — apply until somebody picks.
- Requester feedback is recorded. `POST /api/requests/{id}/feedback` writes a row per verdict rather than only enqueueing a job, and the status thread renders the latest one back. Two buttons, *Works* and *Not quite*, as §11 specifies — there is no third.
- `pull_requests` records the head branch, so the engineer `Details` disclosure on the verification artifact card names it instead of showing a blank.
- Verification artifacts carry a jsonb payload for §27.7's kind-specific bodies: checksums and sizes for a build, the capture list for screenshots, counts and assertion text for a test report, device identifier and traces for a hardware-in-the-loop run. Nothing is invented at read time — an unrecorded field stays empty, an unprobed preview reports `unknown`, and a hardware run with no recorded outcome does not claim it passed.

### Changed

- **Spec §10b amended.** The requester view of a Spec now renders `open_questions` alongside title, outcome and acceptance criteria. The spec previously listed open questions on the structured Spec and then said the requester view rendered "nothing else"; both could not hold. Open questions are the one field written *to* the requester, they are what blocks the confirm button, and hiding them left a person with a disabled button and no explanation. `technical_approach`, `scope` and `risks` remain engineer-only and are still absent from the requester payload entirely.

## Versioning

Charter follows Semantic Versioning, with the standard pre-1.0 caveat: **while the version is `0.x`, a minor bump may break things.** Breaking changes are called out under a `Changed` or `Removed` heading and in the release notes.

Two schemas version independently of the application, because a six-month-old instance must still upgrade cleanly:

- The database schema, versioned by EF Core migrations.
- The `.charter/` folder schema, versioned by the `version:` key in each YAML file.

Upgrades that include database migrations are flagged in the release notes so you know to take a backup first. See [`docs/upgrading.md`](docs/upgrading.md).

## Release tags

The in-app update checker reads the GitHub Releases API and compares `tag_name` against the version compiled into the running build, so tags are not free-form:

- **Stable releases** are tagged `vMAJOR.MINOR.PATCH` — for example `v0.4.0`. The leading `v` is required.
- **Prereleases** are tagged `vMAJOR.MINOR.PATCH-<prerelease>` — for example `v0.5.0-rc.1` — and are marked as prereleases on GitHub. Instances running `CHARTER_UPDATE_CHANNEL=prerelease` are offered these; instances on the default `stable` channel are not.
- **Security releases** carry a `[SECURITY]` prefix in the release title. Charter renders those as a persistent, non-dismissible notice to admins and engineers rather than a dismissible banner.

Any tag that does not parse as a version is ignored by the update checker, which means a mistyped tag fails silently rather than loudly. Check the tag before you push it.

[Unreleased]: https://github.com/binn/Charter/commits/main
