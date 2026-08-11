---
title: "The .charter/ folder"
description: "The committed guardrail folder in the repository Charter builds against: config.yml scopes, conventions, primer, glossary, templates, checks, and migration policy."
---

# The `.charter/` folder

`.charter/` lives in the repository Charter builds against, not in Charter's database.

Everything in it except `cache/` is committed. That is the design: **changing a guardrail requires a
pull request and a code review**, using machinery your team already trusts, rather than an approval flow
invented inside Charter.

```
.charter/
  config.yml          # scopes, base branch, seed command, runner image, limits
  conventions.md      # agent guidance layered on CLAUDE.md, not duplicating it
  primer.md           # requester-facing "how this app is put together"
  glossary.yml        # domain term -> plain English
  templates/          # request templates: bug, copy change, new field
  checks/             # named validation commands the agent must pass
  policies/
    migrations.yml    # destructive-operation rules
  cache/              # generated recon output — gitignored
```

You do not write this folder by hand. Repository onboarding runs a read-only recon pass, proposes a
scope configuration, and opens a pull request containing it. What follows is what to look for when you
review that pull request and what to change later.

## config.yml

The one required file. Everything else is optional.

```yaml
version: 1
base_branch: main
runner_image: ghcr.io/binn/charter-runner-dotnet:1
seed: "dotnet run --project tools/Seed"
scopes:
  allow:
    - "src/Features/**"
    - "src/Web/Components/**"
  deny:
    - "src/Auth/**"
    - "**/Migrations/**"
    - ".github/**"
    - "infra/**"
    - "**/appsettings*.json"
checks:
  - name: build
    run: "dotnet build"
  - name: test
    run: "dotnet test"
limits:
  max_session_usd: 5.00
  max_files_changed: 40
```

| Key | Required | Meaning |
|---|---|---|
| `version` | yes | Schema version of this file. `1` today. |
| `base_branch` | yes | Branch sessions branch from and open pull requests against. |
| `runner_image` | no | Prebuilt runner image for this repository's toolchain. See [runners.md](runners.md). |
| `seed` | no | Command that populates a preview environment with data. Optional — the smoke test warns rather than blocks when a preview appears empty. |
| `scopes.allow` | yes | Glob patterns the agent may write to. |
| `scopes.deny` | yes | Glob patterns the agent may never write to. Deny wins over allow. |
| `checks` | no | Named validation commands the agent must pass before a session is considered successful. |
| `limits.max_session_usd` | no | Per-session ceiling for this repository. |
| `limits.max_files_changed` | no | Sessions exceeding this halt for engineer attention. |

**Path scope is enforced in the runner, not the UI.** A compromised or confused session cannot widen
its own scope, because the enforcement is not on the side that the agent influences.

Deny defaults matter more than allow defaults. Onboarding proposes migrations, auth, CI configuration,
infrastructure, and settings files as denied, and you should have a specific reason before removing any
of them.

### Optional blocks

Several other features add blocks to the same file.

**Verification** — how changes to this project are verified, defaulting from the project type in
`standards.yml` and overridable here ([standards.md](standards.md)):

```yaml
verification:
  kinds: [build_artifact, capture]
  audience: engineer_only
```

**Model overrides**, per task, so refinement can use a cheap model and builds a strong one:

```yaml
models:
  refine: "openrouter/deepseek/deepseek-r1"
  build:  "anthropic/claude-opus-5"
```

**Caching**, where build-output caching is opt-in because stale intermediates produce failures that look
like agent errors:

```yaml
cache:
  build_output: true
```

**Repository-specific setup**, for dependencies no shared runner image can carry. It runs once per
distinct `cache_key` value and is skipped while the key is unchanged:

```yaml
setup:
  run: "apt-get install -y libgpiod-dev && dotnet restore"
  cache_key: "packages.lock.json"
```

**Auto-dispatch policy.** A repository may **only tighten** what the organisation allows, never loosen
it. A sensitive repository can require approval regardless of organisation policy, and no admin setting
overrides that:

```yaml
auto_dispatch:
  enabled: false
```

**Deviations** from organisation standards, each justified and signed off
([standards.md](standards.md)):

```yaml
deviations:
  - rule: "services.database.engine"
    value: "mongodb"
    justification: "Ingesting heterogeneous telematics payloads with no stable schema across vendors."
    approved_by: "usr_7fQ2mNp9RtVx"
    approved_at: "2026-08-10T00:00:00Z"
```

## conventions.md

Agent guidance specific to this repository, **layered on** `CLAUDE.md` or `AGENTS.md` rather than
replacing them. If your repository already has agent guidance, onboarding imports and extends it and
never overwrites it.

Put here what an agent would otherwise get wrong: naming conventions, which layer owns what, patterns
you have deliberately moved away from, the test style you expect.

Do not duplicate what is already in `CLAUDE.md`. Two documents saying nearly the same thing drift, and
the agent reads both.

## primer.md

Requester-facing. One page explaining how this application is put together, in the vocabulary of the
people who use it rather than the people who build it.

New requesters read it once, separately from any session. It is also loaded into refinement, so it
grounds the refiner in the shape of your codebase.

An agent drafts it during onboarding, an engineer edits it, and then it is published. The draft is a
starting point, not the deliverable — the editing is where the value is.

## glossary.yml

The file that punches furthest above its weight.

Domain vocabulary means nothing to a general model. One file, two consumers: it disambiguates the
**spec refiner**, and it grounds the **teaching** pass so explanations use your words.

```yaml
BOQ: "Bill of Quantities — the itemised list of equipment and materials in a quote."
derate: "Reducing a rated output to account for real-world losses like heat or shading."
interconnection: "The utility approval and physical connection that lets a system export to the grid."
```

Add a term the first time you see a refinement conversation go sideways over it. That is the cheapest
quality improvement available in this folder.

## templates/

Request templates a requester picks instead of typing into an empty box: a bug report, a copy change, a
new field. A requester who picks a template skips roughly half the refinement round-trips.

Give each template the two or three questions that always get asked for that kind of request, so they
are answered up front.

## checks/

Named validation commands the agent must pass. Simple cases go inline in `config.yml` under `checks`;
this folder is for anything that needs a script.

Checks are what turn "the agent says it is done" into evidence. A repository with no test command gets
far less value from Charter, because there is nothing to fail.

The session runs them after the agent exits and before the branch is pushed, in the order you declare
them, from the root of the checkout. Each one's outcome lands on the transcript and in the change
request body — including "this repository declares no checks, so nothing was verified automatically",
which is said out loud rather than left as a blank.

Four rules govern how they run:

- **A failing check is reported, not fatal.** The branch is still pushed and the change request still
  opens, with the failure at the top of the body. Charter has no merge button, so a red change request
  cannot ship on its own; what a failure changes is what the engineer reads first, not whether they get
  anything to read. This also means Charter stays usable on a repository whose main branch is already
  red, or that has one flaky test.
- **Each check is one command, run directly, with no shell.** `run: "dotnet build"` works;
  `run: "dotnet build && dotnet test"` does not, and is reported as a check that could not be run
  rather than silently passing. Split it into two checks, or put it in a script in this folder and
  point `run:` at that.
- **A check whose toolchain the image lacks stops the session before the agent starts.** If you declare
  `dotnet build` and `runner_image` has no .NET SDK, the session fails immediately with a message
  naming an image that has one. Sessions never install a language runtime. Charter can only detect this
  for the toolchains runners probe for — .NET, Node, Python, uv, git and Xcode — so a check that runs
  `make` is started and allowed to fail on its own terms.
- **Checks are skipped entirely when the agent changed nothing.** There is nothing to validate, and a
  full test run to prove it is minutes of somebody's time spent on an empty diff.

A check's own output reaches the change request body, and it reaches it **as quoted text**. Whatever a
check prints has passed through a sandbox running an agent over repository content nobody vetted, and
the body is a document Charter signs, so a name and a summary appear there as inline code: flattened to
one line, cut at 100 and 300 characters, with at most twenty checks listed and the rest counted. Write
summaries that are worth reading on one line — markdown in them will be shown rather than rendered, and
a long one is truncated with an ellipsis. The full output stays on the transcript, where the pane
shows it in full.

## policies/migrations.yml

Rules for classifying schema migrations. Preview databases are disposable, so the risk is not data loss
during a session — it is a bad migration merging.

Charter classifies migrations **structurally**, by parsing the generated migration and inspecting the
operations, not by pattern-matching text:

| Class | Operations | Behaviour |
|---|---|---|
| **Additive** | New table, nullable column, index, new foreign key on an empty table | Flows normally. The pull request is labelled `schema-change`. |
| **Ambiguous** | Rename, type change, non-null **with** a default | Engineer review required. The pull request is blocked until approved. |
| **Destructive** | Drop column or table, truncate, non-null **without** a default | **The session halts.** The agent writes down what it intended; an engineer authors the migration by hand. |

`policies/migrations.yml` is where you adjust those rules for your project. Independently of Charter,
put CODEOWNERS on your migrations directory — that makes engineer approval structurally required
regardless of what any policy file says.

## cache/

Generated recon output. **Gitignore it:**

```gitignore
.charter/cache/
```

It is regenerated on demand and safe to delete at any time. Nothing in it is a guardrail, and nothing in
it needs backing up.

## Files added by other flows

Two more files appear in `.charter/` when the relevant feature is used:

- `charter.md` — the Project Charter for a project created through the new-project flow: problem, users,
  in-scope, explicitly out-of-scope, rough data model, integrations, and stack.
- The pinned `standards.yml` version the repository was created under, recorded in `config.yml` so drift
  audits report against the right baseline ([standards.md](standards.md)).

## Versioning and forward compatibility

Two rules, and they exist so that a repository and a Charter instance can drift apart without breaking.

**Every YAML file carries `version: 1` at the top.** From day one, before it is needed. A file without
a version is treated as version 1 and produces a warning.

**Unknown keys warn, never fail.** A repository written for a newer Charter version keeps working on an
older instance — the keys it does not recognise are logged as warnings and ignored. The alternative,
failing closed on an unrecognised key, means every operator has to upgrade in lockstep with every
repository, which is not how self-hosted software gets used.

What that means in practice:

- Adding a key to `config.yml` for a feature your instance does not have yet is safe. It warns.
- Removing a key does not break an older instance either. Defaults apply.
- **A typo in a key name warns rather than erroring.** This is the cost of the rule. If a setting seems
  to have no effect, check your startup logs for a warning about an unrecognised key before assuming it
  is a bug.
- Version bumps are reserved for changes that cannot be handled by the warn-and-ignore rule. A `version:
  2` file will state plainly what changed and what an older instance does with it.

The extension mechanism is folder conventions — new files and new keys in known places. **There is no
plugin system**, and none is planned for v1.

## Related

- [runners.md](runners.md) — `runner_image`, `setup`, and `cache`
- [standards.md](standards.md) — deviations, pinning, and project types
- [adapters.md](adapters.md) — choosing an agent and models per repository
- [security.md](security.md) — why path scope is enforced in the runner
- [spec §8, §15, §32](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full specification for this folder
