# Changelog

All notable changes to Charter are recorded here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Charter has no released versions yet. Development is in **Phase 1 — refinement only, no agent execution**: request intake, the refinement conversation, the spec confirmation card, and the approval queue. Agent execution, pull request creation, preview binding, teaching, and budgets arrive in later phases. The full build order is section 23 of [`agent-docs/spec.md`](agent-docs/spec.md).

Expect breaking changes without deprecation cycles until 1.0.

### Added

- Repository groundwork: license, contributor license agreement, trademark policy, security policy, contribution guide, and code of conduct.

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
