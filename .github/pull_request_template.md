# Summary

<!-- What changes, and why. One paragraph is usually enough. -->

## Linked issue

<!--
Charter prefers an issue before anything large. Link it here.
Use "Closes #123" so the issue closes on merge.
-->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation
- [ ] Build, CI, or tooling
- [ ] Refactor with no behaviour change

## Testing done

<!--
What you actually ran, and what you observed. "CI is green" is not testing done.
If the change touches a session path, say which runner backend and adapter you
exercised it against.
-->

## Database migrations

- [ ] This change adds no EF Core migration
- [ ] Additive only (new table, nullable column, index)
- [ ] Ambiguous (rename, type change, non-null with default) - needs maintainer review
- [ ] Destructive (drop, truncate, non-null without default) - explain why below

<!-- If a migration is included, describe what it does to existing data. -->

## Checklist

- [ ] I have read `CONTRIBUTING.md`.
- [ ] `dotnet build` and `dotnet test` pass locally.
- [ ] `dotnet format --verify-no-changes` reports no changes.
- [ ] The SPA typechecks, lints, and builds (`npm run typecheck`, `npm run lint`, `npm run build`).
- [ ] Documentation is updated where behaviour changed, in `docs/` for user-facing docs.
- [ ] New configuration is added to `.env.example` and `docs/configuration.md`.
- [ ] No new external runtime dependency (no Redis, no second container).
- [ ] No secret, token, or real credential appears in the diff.
- [ ] Commit messages follow Conventional Commits and describe the change only:
      no co-author trailers, no "generated with" footers, no tool or model
      attribution of any kind, in the commit message, PR title, or PR body.

## Contributor License Agreement

Charter requires a CLA (`CLA.md`). Nothing to do in advance: on your first pull
request the CLA Assistant bot comments with a link, and you sign by replying to
it once. It applies to every future contribution, so you will not be asked
again.
