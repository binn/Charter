# Contributing to Charter

Thanks for looking. Read this page before you write code — it will save you time.

## Read this first: the current stance on scope

Charter is **pre-1.0 and early**. Phase 1 (refinement, specs, approvals — no agent execution yet) is in progress; everything after it is planned. The architecture is still settling, and interfaces that look stable today may not be next month.

Practical consequences for you:

- **Open an issue before starting anything large.** Anything beyond a bug fix, a docs correction, or a small self-contained improvement should start as an issue so we can agree on the shape before you spend a weekend on it. Pull requests that arrive without that conversation may be closed, not because the work is bad but because it conflicts with a direction already decided in [`agent-docs/spec.md`](agent-docs/spec.md).
- **`agent-docs/spec.md` is the source of truth.** If your change contradicts it, the change needs to argue with the spec first. If your change is already described in it, say which section in your pull request.
- **Expect few external pull requests, and expect review to be slow.** This is a personal project maintained by one person. That is not a discouragement so much as calibration: a queue of unreviewed stranger patches would be worse than none, so I would rather agree on a small number of things and actually merge them.
- **The stack is .NET and TypeScript.** Contributions in either half are welcome; the backend half is where the novelty is.

## Development environment

You need:

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 or newer | `dotnet --version` |
| Node.js | 22 or newer | `node --version`. The SPA is built with Vite. |
| Docker | any recent version | Only used to run PostgreSQL locally |
| PostgreSQL | 16 or newer | Charter's only external dependency |

### 1. Clone and start Postgres

```bash
git clone https://github.com/binn/Charter.git
cd Charter
docker run -d --name charter-postgres \
  -e POSTGRES_USER=charter \
  -e POSTGRES_PASSWORD=charter \
  -e POSTGRES_DB=charter \
  -p 5432:5432 \
  postgres:16
```

If you would rather use the bundled Compose file, `docker compose up -d postgres` starts the same database.

### 2. Configure

```bash
cp .env.example .env
```

Then edit `.env`. For local development you need `DATABASE_URL`, `CHARTER_BASE_URL`, `CHARTER_SECRET_KEY`, `CHARTER_CREDENTIAL_KEY`, and at least one model credential. Generate the two keys separately:

```bash
openssl rand -base64 32
```

Point `DATABASE_URL` at the container you just started:

```bash
DATABASE_URL=postgres://charter:charter@localhost:5432/charter
```

Charter validates all configuration at startup and, if something is wrong, prints every problem at once and exits non-zero. There is no `appsettings.json`.

### 3. Run

```bash
dotnet run --project src/Charter
```

That is the whole command. The frontend lives at `src/Charter/ClientApp` and is bundled into the same application; in development it runs through the ASP.NET Core SPA proxy, so `dotnet run` starts Kestrel, launches the Vite dev server, and proxies unmatched requests to it. Hot module reload works. There is no second terminal to keep open.

Charter comes up on `http://localhost:8080`. On first boot with zero users it enters setup mode and writes a one-time setup token to stdout — read it from the console and use it to create the first admin account.

If npm packages have not been restored yet, install them once:

```bash
npm --prefix src/Charter/ClientApp install
```

### 4. Build and test

```bash
dotnet build Charter.sln
dotnet test Charter.sln
```

Tests live in `tests/Charter.Tests`. To run only that project:

```bash
dotnet test tests/Charter.Tests
```

Frontend checks run from the SPA directory:

```bash
npm --prefix src/Charter/ClientApp run lint
npm --prefix src/Charter/ClientApp run build
```

Formatting for the .NET side:

```bash
dotnet format Charter.sln
```

CI builds and tests both halves in the same job and runs EF Core migrations against a throwaway Postgres service. If it passes locally it should pass there.

### Solution layout

```
Charter.sln
src/
  Charter/                  ASP.NET Core control plane
    ClientApp/              React + Vite + TypeScript SPA
  Charter.Agent/            Charter Agent daemon
  Charter.DetachedRunner/   Detached runner host
tests/
  Charter.Tests/
```

Two directories that look similar and are not:

- `docs/` is user-facing documentation shipped to operators and contributors.
- `agent-docs/` holds briefs, planning notes, and specifications for engineers and coding agents.

Never put one kind of document in the other directory.

## Making a change

1. **Open an issue** if the change is more than trivial. Wait for a reply before building.
2. **Branch from `main`.**
3. **Keep the pull request small.** One concern per pull request. A 40-file refactor bundled with a bug fix gets neither reviewed.
4. **Add tests** for behaviour you change. Configuration parsing, authorization checks, and state transitions are the areas where a test is not optional.
5. **Update the docs in the same pull request** if you changed something an operator configures or a contributor relies on.
6. **Describe what changed and why** in the pull request body, and link the issue.

### Documentation style

If you touch anything in `docs/` or the root markdown files:

- Second person, present tense. "Set `DATABASE_URL` to..." not "The user should set...".
- Lead with the thing the reader came for. Rationale goes after instructions, never before.
- Every code block must be copy-pasteable and correct. No placeholder tokens inside a command someone would run verbatim.
- State limitations plainly. Overselling gets discovered and costs more trust than the limitation itself.
- No emoji in documentation. The README may use them sparingly.

## Commit conventions

Charter uses [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
feat(refine): reject specs that touch denied paths
fix(config): url-decode DATABASE_URL passwords containing @
docs(security): document the dependency install risk
test(budget): cover reservation expiry
chore(deps): bump Npgsql to 10.0.1
```

Common types: `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`. Breaking changes take a `!` before the colon and a `BREAKING CHANGE:` footer.

### Commit attribution

**Commit messages describe the change and nothing else.** No co-author trailers, no generated-with footers, no tool or model attribution of any kind — not in commit messages, not in pull request titles, not in pull request descriptions. This applies regardless of what wrote the code. If you use an AI assistant, that is entirely your business and it does not belong in the history.

## Contributor License Agreement

Charter requires a CLA. It is **automated**: on your first pull request a bot comments with a link, you sign once, and every subsequent contribution is covered. There is nothing to do in advance.

**You keep the copyright to your work.** The CLA grants a license alongside it, which is what preserves the project's ability to relicense as a whole. Read [`CLA.md`](CLA.md) for the terms. If you are contributing on behalf of an employer, make sure you have the authority to sign before you do.

Questions about the agreement are welcome — open a discussion before signing rather than after.

## Licensing

Charter is AGPL-3.0-only. Contributions are accepted under that license plus the CLA above. The Charter name and logo are not covered by the AGPL; see [`TRADEMARK.md`](TRADEMARK.md).

## Security issues

Do not open a public issue. Follow [`SECURITY.md`](SECURITY.md).

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Report unacceptable behaviour to me@bin.moe.
