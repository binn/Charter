# Security Policy

Charter runs an AI coding agent against your source code on behalf of people who cannot read it. The strongest property in the design is that **the agent never sees raw user input**. A requester's words go to the refinement model, which produces a structured specification; a human approves that specification; the agent receives only the approved spec. Refinement is a sanitisation boundary, and approval is a human review of exactly what the agent will be told.

That property is structural, not a prompt instruction. Everything else in this document layers on top of it.

## Reporting a vulnerability

**Please do not open a public issue for a security vulnerability.**

Use GitHub's private vulnerability reporting on [`binn/Charter`](https://github.com/binn/Charter/security/advisories/new). It keeps the report, the discussion, and the eventual advisory in one place.

If that is unavailable to you, email **me@bin.moe**. Put "Charter security" in the subject line.

Include what you have: affected version or commit, a description of the issue, reproduction steps, and the impact you believe it has. A rough report you send today is worth more than a polished one you never finish.

You are welcome to disclose publicly once a fix has shipped, or after 90 days, whichever comes first. Tell me if you plan to publish so the advisory and your write-up can go out together.

## Supported versions

Charter is pre-1.0. **Only the latest release is supported.** There are no backports to older tags, and there is no long-term-support line.

If you self-host, upgrade to the newest release before reporting a bug you suspect is security-relevant. Charter checks GitHub daily for new releases and flags security ones distinctly, so you are told when this matters — see [`docs/privacy.md`](docs/privacy.md) for what that check sends (nothing about your instance) and how to turn it off.

## Response expectations

Charter is a personal project maintained by one person. There is no on-call rota and no security team. Treat the following as honest intent rather than a service level agreement:

| Stage | Target |
|---|---|
| Acknowledge your report | Within 5 business days |
| Initial assessment and severity call | Within 10 business days |
| Fix for a confirmed high-severity issue | As fast as I can, and I will tell you where it stands |

If you have heard nothing after 10 business days, send a follow-up — assume a missed notification rather than a dismissal.

There is no bug bounty. Reporters are credited in the advisory and the changelog unless you would rather not be.

## Threat model in brief

The full document is [`docs/security.md`](docs/security.md). The short version:

- **Prompt injection is the primary threat.** The agent consumes untrusted requester text and untrusted repository content, including dependency READMEs and issue bodies. The refinement boundary handles the first; the second is mitigated but not solved.
- **The agent cannot merge.** Charter has no merge button and never will. Merge authority lives in GitHub branch protection and CODEOWNERS, outside Charter's trust boundary, so a bug in Charter's authorization code cannot put code in your default branch.
- **Runners are isolated from the control plane.** A runner receives a short-TTL GitHub App installation token scoped to one repository, plus a scoped model credential. It never receives refresh tokens or the control plane's environment.
- **Path scope is enforced in the runner**, not the UI, so a compromised session cannot widen what it may touch.
- **Egress from runners is allowlisted** to package registries and model APIs, and toolchains come from prebuilt images rather than being installed per session.
- **Transcript and code views are gated on repository read access**, not on a user preference. A requester toggling a view is not a permission bypass.
- **Destructive schema migrations halt the session.** A human authors them.
- **Every agent action is attributable to a named person** in the audit log. The agent never acts on its own initiative.

### What this does not close

Stated plainly, because the mitigations above can read as stronger than they are:

- **Project dependencies are still installed.** Charter runs `npm ci` or `dotnet restore` against your repository's own manifests. A compromised transitive dependency in your project is a live risk that locked toolchains do not fix. Charter uses lockfile-only installs and disables install scripts by default, and flags dependency changes in the engineer recap.
- **Repository content injection is only partly mitigated.** The final answer remains that the agent cannot merge and a human reviews the pull request.
- **Native-mode runners provide process isolation, not container isolation.** Run them on a dedicated machine or VM, not on a daily driver.
- **`DockerRunner` grants root-equivalent access to its host.** That is inherent to a Docker socket, and it is documented rather than worked around.
