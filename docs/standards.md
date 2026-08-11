---
title: "Organisation standards"
description: "Declaring how your organisation builds software in a designated standards repository: standards.yml, project types, deviations, versioning, templates, and the new project flow."
---

# Organisation standards

Standards are a declarative statement of how your organisation builds software. They live in a
**designated standards repository**, not in Charter's database.

That placement is the whole point: changing a standard requires a pull request and a review. An admin
cannot quietly loosen your engineering standards from a settings page.

```
charter-standards/            # a repo you designate
  standards.yml
  templates/
    dotnet-web/
    dotnet-worker/
  policies/
    security.md
```

Designate the repository in Charter's admin settings. **The standards repository and every template
repository are outside every agent's write scope, always.** No session can modify them.

## What standards feed

One file, three consumers:

1. **Project scaffolding.** The stack for a new project is chosen from standards, not asked about.
2. **The spec refiner.** Refinement will not propose a library, service, or database outside policy.
   This is the highest-value consumer and it costs nothing extra — standards are injected into the
   refinement context.
3. **Drift audit.** An on-demand pass over existing repositories reporting where they diverge. It
   reports only. Charter never auto-remediates a repository to match a standard.

## standards.yml

```yaml
version: 1
stacks:
  web:
    backend:   { runtime: "dotnet", version: "10", required: true }
    frontend:  { framework: "react", bundler: "vite", ui: "shadcn/ui" }
    database:  { engine: "postgres", min_version: "16" }
    template:  "example-org/template-dotnet-web"
services:
  ai:      { provider: "openrouter", required: true }
  storage: { provider: "s3-compatible", required: true }
  hosting: { provider: "railway" }
  vcs:     { provider: "git", host: "github" }
required_files:
  - ".charter/config.yml"
  - "README.md"
  - ".github/workflows/ci.yml"
conventions:
  branch: "main"
  commits: "conventional"
deviations:
  requires_role: "admin"
  must_be_justified: true
```

| Block | Purpose |
|---|---|
| `version` | Schema version of this file. Always `1` today. |
| `stacks` | Named stacks, each describing runtime, framework, database, and the template repository to instantiate. |
| `services` | Organisation-wide service choices. `required: true` means a new project must provision it. |
| `required_files` | Files every conforming repository must contain. Checked by drift audit and applied at scaffold time. |
| `conventions` | Branch naming and commit message convention. |
| `deviations` | Who may approve a deviation and whether a written justification is mandatory. |

## Project types

Project types declare how a change is verified and what a runner must provide to build it. Charter
generalises "preview environment" into "verification artifact" — whatever lets a human judge whether
the change is right.

```yaml
project_types:
  web:         { verification: [hosted_preview],                    runner: [linux] }
  api:         { verification: [hosted_preview, test_report],       runner: [linux] }
  mobile_ios:  { verification: [distribution_channel, capture],     runner: [macos, xcode] }
  mobile_expo: { verification: [distribution_channel, capture],     runner: [linux, macos] }
  desktop_win: { verification: [build_artifact, capture],           runner: [windows] }
  desktop_mac: { verification: [build_artifact, capture],           runner: [macos, signing] }
  maui:        { verification: [build_artifact, capture],           runner: [windows, macos] }
  unity:       { verification: [build_artifact, capture],           runner: [linux, unity_license, gpu] }
  game_server: { verification: [ephemeral_instance, test_report],   runner: [linux] }
  embedded:    { verification: [test_report, hil_report],           runner: [linux, toolchain, usb_device] }
  library:     { verification: [test_report],                       runner: [linux] }
```

Verification kinds:

| Kind | What the requester gets |
|---|---|
| `hosted_preview` | An ephemeral deployed URL |
| `build_artifact` | A downloadable binary — APK, IPA, `.exe`, `.app`, `.elf`, `.uf2` |
| `distribution_channel` | TestFlight, Play Internal Testing, Firebase App Distribution, Expo EAS Update |
| `capture` | Screenshots or video from a simulator, emulator, UI automation run, or Unity play mode |
| `ephemeral_instance` | A running server plus a connect string |
| `test_report` | Structured pass/fail with logs and captured signals |
| `hil_report` | A hardware-in-the-loop run against a real device |
| `none` | Engineer review only |

A session can produce several. An Expo project yields an EAS Update channel and a simulator capture.

### What degrades, stated plainly

Charter's core promise — the person who asked evaluates the change themselves — is strongest for web
and mobile and weakens from there:

| Class | Requester experience |
|---|---|
| Web, API | Click a link. The full loop. |
| Mobile | Install a build. The full loop, with a delay and an install step. |
| Desktop, Unity | Screenshots or a video clip. Good for UI changes, poor for interaction. |
| Game servers | A connect string, if they know how to use one. Usually engineer-mediated. |
| Embedded, GNSS | A test report. Effectively engineer-only. |

Where an artifact is marked `engineer_only`, the requester's thread says so honestly rather than
implying they can check it themselves, and the engineer recap becomes the primary review surface. The
rest of Charter — refinement, standards, scoping, budgets, recap — applies unchanged. Only the
click-to-verify loop is missing.

The runner requirements in `project_types` map directly onto capability matching. A project type
requiring `macos` and `xcode` needs a Charter Agent on a Mac; there is no way around it. See
[runners.md](runners.md).

Signing identity is never agent-accessible. iOS certificates, notarisation credentials, Android
keystores, and code-signing certs are human-provisioned secrets held by the runner environment. The
agent may trigger a signed build; it can never read the signing material.

## Deviations

Standards are defaults plus justified exceptions, not walls. A project that genuinely needs something
else records it in **its own** `.charter/config.yml`:

```yaml
deviations:
  - rule: "services.database.engine"
    value: "mongodb"
    justification: "Ingesting heterogeneous telematics payloads with no stable schema across vendors."
    approved_by: "usr_7fQ2mNp9RtVx"
    approved_at: "2026-08-10T00:00:00Z"
```

The `deviations` block in `standards.yml` sets the policy — who may approve one and whether a
justification is required. The entries themselves live with the project they apply to.

Deviations are committed, surfaced in every drift audit, and never silent. That is the trade: the
exception is allowed, and it is written down with a name against it.

## Versioning and pinning

`standards.yml` is versioned, and **each repository pins the version it was created under**.

Tightening a standard must not retroactively mark every existing repository non-compliant. A drift
audit reports a repository against its pinned version, with an optional "compare to latest" view for
when you want to know what adopting the current standard would involve.

Raising a repository's pinned version is a deliberate act, and the drift audit is what tells you what
it will cost.

## Templates and generation

Both are supported, with a declared preference order. Templates give consistency; generation gives
coverage. An organisation building its first Unity project has no template yet, and refusing to help
until someone writes one is a dead end.

```yaml
scaffolding:
  policy: template_preferred    # template_required | template_preferred | generation_allowed
  harvest: true
```

| Policy | Behaviour |
|---|---|
| `template_required` | Only project types with a template can be created. Maximum consistency, no coverage for new ground. |
| `template_preferred` | The default. Use a template when one exists, generate when none does, then offer to harvest it. |
| `generation_allowed` | Always allow generation, even where a template exists. |

### Template harvesting

Harvesting is what makes a template library converge on reality.

The first Unity project gets generated from scratch — slow and expensive. Once it has been reviewed and
is working, Charter offers to **extract it into a template repository**, parameterising names and
stripping project-specific content. The second Unity project is a template instantiation and costs a
fraction as much.

Your template library grows out of real, working projects rather than out of someone finding a free
afternoon to write one.

Harvesting requires engineer approval and produces a pull request against the standards repository —
the same review path as any other guardrail change.

When Charter generates rather than instantiates, the agent still receives `standards.yml`. A generated
project is not an unconstrained one. Generated scaffolds are marked as such in the project's
`.charter/config.yml`, so drift audits can flag them as candidates for template promotion.

## Sandbox and production organisations

New projects should not be born in your production GitHub organisation. Charter supports a two-org
model:

```yaml
github:
  sandbox_org: "example-labs"
  production_org: "example-corp"
  create_in: sandbox
  promotion_requires_role: admin
```

New repositories are created in the sandbox organisation. Experiments that go nowhere die there without
polluting the main org, and the blast radius of repo-creation permissions stays contained.

### Promotion is a checklist, not a button

GitHub repository transfer preserves history, issues, and pull requests, and redirects old URLs. Several
things **do not** transfer and have to be re-applied:

- Branch protection rules and rulesets
- CODEOWNERS enforcement — the file moves, the requirement to honour it is a repository setting
- Repository secrets and variables
- Webhooks
- The GitHub App installation — the App must already be installed on the target organisation

So promotion runs as a sequence:

1. Verify the GitHub App is installed on the target organisation.
2. Transfer the repository.
3. Re-apply branch protection, rulesets, and CODEOWNERS enforcement.
4. Re-create secrets. **Charter emits the list of names; you set the values.** Charter never generates
   or copies secret values.
5. Relink the hosting project.
6. **Re-run the smoke test.** The repository is not visible to requesters in its new home until it
   passes again.

## New project flow

1. **Propose.** Anyone may propose a project in Plan mode.
2. **Planning conversation.** Produces a Project Charter: problem, users, in-scope, explicitly
   out-of-scope, rough data model, integrations, and a stack section auto-populated from
   `standards.yml`.
3. **Approve.** Admin or engineer only. **Requesters may propose; they may not create.** Without that
   gate you accumulate forty abandoned repositories a quarter.
4. **Scaffold.** Charter creates the repository from the template, applies standards, generates
   `.charter/config.yml`, commits the Project Charter to `.charter/charter.md`, and opens the initial
   pull request.
5. **Provision.** Hosting project, database, and preview environments per `services`. **Charter never
   generates secrets**; it emits a checklist of what a human must set.
6. **Onboard.** Falls into the normal repository onboarding flow, ending at the smoke test.

Scaffolding plus a first build is far more expensive than a feature tweak. Give it a separate budget
line and a separate cap.

## Repository creation is gated three ways

Creating repositories is a privilege escalation. The GitHub App needs organisation-level repository
creation scope, which is a permission you grant deliberately and can revoke. All three gates must be
open:

1. **Instance opt-in.** `CHARTER_ALLOW_REPO_CREATION=false` by default. Leave it off unless you are
   using the new-project flow.
2. **GitHub App scope.** Organisation-level repository creation is a separate permission the operator
   must grant explicitly. Granting it is what makes the escalation real, so grant it to an App
   installed on your sandbox organisation rather than your production one.
3. **Role.** A distinct `can_create_repo` capability, admin-only by default and grantable to engineers.
   The first admin an instance seeds holds it; being an admin is not by itself enough, which is why it
   is a capability rather than a role.

All three are evaluated together and the refusal names the gate that stopped it, so an operator is
never left guessing which of the three to change.

**Not yet reachable.** The new-project flow that would call this is not built, so nothing in Charter
currently creates a repository. The gate is in place and tested; the door behind it is not. Leave
`CHARTER_ALLOW_REPO_CREATION` off and do not grant the App the scope until the flow ships.

## A note on naming

The product is Charter. The document a project begins with is its Project Charter. Both words are
correct and they are not the same thing — Charter the application, Project Charter the document,
capitalised, never a bare lowercase "charter".

## Related

- [charter-folder.md](charter-folder.md) — where a project's own config, deviations, and pinned version live
- [runners.md](runners.md) — capability matching for project types
- [configuration.md](configuration.md) — `CHARTER_ALLOW_REPO_CREATION`
- [spec §26 and §27](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full standards and project-type specification
