# Security

Charter runs an AI coding agent against your source code on behalf of people who cannot read it. This
page is the full threat model: what Charter is structurally protected against, what it mitigates in
layers, and what it does not fix.

To report a vulnerability, follow the private disclosure instructions in
[`SECURITY.md`](../SECURITY.md) at the repository root. Please do not open a public issue.

## The strongest property: the agent never sees raw user input

**A requester's words never reach the agent.**

What reaches the agent is a **refined specification, authored by a model and approved by a human**.
Between the two sits Charter's refinement conversation, and it does two jobs at once:

- **Refinement is a sanitisation boundary.** The requester's text is input to a conversation that
  produces a structured specification. The specification is generated, not copied. Instruction-shaped
  text embedded in a feature request does not survive into the artifact the agent consumes, because
  that artifact is written by a different model in a different context with a different objective.
- **Approval is a human reading what the agent will be told.** The specification is what gets approved,
  and it is the same object the agent receives. There is no second, hidden prompt.

This is a structural property rather than a filter. It does not depend on detecting an attack, and it
does not weaken when someone invents a new phrasing.

Two caveats, stated plainly:

- Auto-dispatch can skip the human step ([spec §7.5](../agent-docs/spec.md)). The model-authored
  sanitisation boundary still applies; the human review of it does not. Auto-dispatched sessions are
  flagged `auto_dispatched`, their pull requests are labelled `unreviewed-spec`, and the engineer recap
  **leads** with the fact that no human approved the specification, including it in full rather than
  summarised.
- This protects against injection through the **request**. It does nothing about injection through
  **repository content**, which is covered below.

## Trust boundary

The line Charter draws around itself, and what it deliberately keeps on the other side.

**There is no merge button, and there will never be one.** Charter opens pull requests. Your branch
protection rules and CODEOWNERS decide what ships. Merge authority lives entirely outside Charter's
trust boundary, which means **a bug in Charter's authorization code cannot put code in your main
branch**. Charter's data model does not represent merge authority at all.

That is what makes the rest of the design safe to loosen. Relaxing the spend gate risks wasted tokens
and a pull request nobody wanted. It cannot risk shipped code.

**The runner is outside the control plane.** A runner executing a session receives:

- A **short-TTL GitHub App installation token, scoped to exactly one repository**.
- A **scoped model credential** for that job, as an access token with a short life.

It does not receive, and cannot read:

- The control plane's environment or configuration
- `CHARTER_SECRET_KEY`, `CHARTER_CREDENTIAL_KEY`, or the database connection string
- OAuth refresh tokens of any kind
- Credentials for any repository other than the one in the job

**Transcript and code panes are gated on repository read access, not on user preference.** A requester
toggling to the detailed view would otherwise be a permission bypass: transcripts leak file paths,
environment variable names, dependency versions, and error output. The API omits engineer-only fields
server-side rather than hiding them in the client — authorization is not a rendering concern.

**Guardrails live in your repository.** Path scopes, denied directories, and validation commands are in
a committed `.charter/config.yml`. Widening what an agent may touch requires a pull request and a
review. See [charter-folder.md](charter-folder.md).

**Path scope is enforced in the runner, not the UI.** A session cannot widen its own scope, because
enforcement does not sit on the side the agent can influence.

## Prompt injection

The agent consumes untrusted text from two directions: non-engineers filing requests, and the
repository's own content — dependency READMEs, issue text, code comments, fixture data.

The primary mitigation is structural, described above. Layered on top:

- **Egress allowlist in the runner.** Package registries and the model API, and nothing else.
  Exfiltration needs somewhere to send data.
- **The runner sees no control-plane environment.** Short-TTL, single-repository token, nothing else.
- **Instruction-shaped language is flagged for review** before dispatch: imperatives addressed to an
  agent, base64 blobs, URLs. Flagged requests go to an engineer.
- **Every file write and network call is logged**, attributable to a session and a named human.
- **The agent never acts on its own initiative.** No schedulers, no infinite auto-retry. Every session
  traces back to a person who asked for something.

**Charter does not rely on telling the model to ignore injected instructions.** That is a layer, not a
defence, and treating it as one is how these systems fail.

Repository-content injection is the harder half of the problem, and Charter does not claim to solve it.
The answer is the same as everywhere else in this document: **the agent cannot merge.** A successful
injection produces a pull request that a human reviews.

## Toolchain supply chain

Locking toolchains into prebuilt runner images is a **security control**, not only a speed
optimisation.

A session permitted to install its own tooling can:

- Fetch a typosquatted or compromised package that reads the workspace and exfiltrates source
- Read environment variables and any credential material present in the process
- **Reveal the runner's public IP and network position** to an attacker-controlled endpoint. On a
  Charter Agent, that is your own network, not a disposable cloud VM
- Persist into a shared cache and affect subsequent sessions

So: **a session never installs a language runtime.** Toolchains are provisioned ahead of time in
versioned images. If an image lacks something the repository declares it needs, the session fails fast
with an actionable message rather than quietly `apt-get`-ing its way to a working state.

Prebuilt images plus the egress allowlist close the **tooling** vector.

**Caches are scoped per repository**, and that is a security requirement rather than an optimisation. A
cache shared across repositories is a cross-repository contamination path: a poisoned transitive
dependency pulled in one repository persists into another. Sandbox-organisation and
production-organisation caches are never shared either.

## What this does not close

**Project dependencies are still installed, and a compromised one is still a live risk.**

Charter runs `npm ci` or `dotnet restore` against your repository's own manifests. A compromised
transitive dependency in your project executes in the runner exactly as it would in your CI.
**Locked toolchains do not fix this.** Do not read the section above as saying otherwise.

What Charter does about it:

- **Lockfile-only installs.** `npm ci`, `dotnet restore --locked-mode`,
  `pnpm install --frozen-lockfile`. Fresh versions are never resolved during a session, so what runs is
  what your lockfile already committed to.
- **Install scripts disabled by default.** `npm ci --ignore-scripts`. A repository that genuinely needs
  postinstall scripts opts in explicitly, per repository.
- **Egress allowlist**, so an exfiltration attempt has nowhere to send data.
- **Optional registry proxy.** Point runners at an internal Artifactory, Verdaccio, or BaGet so
  dependency fetches never touch the public internet directly.
- **Dependency changes are a flagged diff category** in the engineer recap, ranked alongside auth and
  migrations rather than buried in an alphabetical file list.

This reduces the exposure. It does not eliminate it, and no configuration of Charter will.

## Schema migrations

Preview databases are disposable, so the risk is not data loss during a session. It is a **bad
migration merging**.

Charter classifies rather than blanket-gating, and classifies **structurally** — by parsing the
generated migration and inspecting its operations, not by pattern-matching on text:

| Class | Operations | Behaviour |
|---|---|---|
| **Additive** | New table, nullable column, index, new foreign key on an empty table | Flows normally; the pull request is labelled `schema-change` |
| **Ambiguous** | Rename, type change, non-null **with** a default | Engineer review required; the pull request is blocked until approved |
| **Destructive** | Drop column or table, truncate, non-null **without** a default | **The session halts.** The agent writes down its intent; an engineer authors the migration by hand |

Destructive migrations are one of the things auto-dispatch never bypasses. Rules are tunable per
repository in `.charter/policies/migrations.yml`.

Put CODEOWNERS on your migrations directory as well. That makes engineer approval structurally required
regardless of any policy file.

## Secrets

**At rest.** Model credentials are encrypted with a dedicated `CHARTER_CREDENTIAL_KEY`, deliberately
separate from `CHARTER_SECRET_KEY` so that rotating your cookie signing key does not invalidate every
stored credential.

**In the application.** A token is never logged, at any level, in any sink. A secret is never returned
to the UI after creation — the credential list shows provider, owner, status, and last used, and there
is no reveal button and no API that returns the value.

**In transit to a runner.** The control plane owns OAuth refresh. Runners receive a short-TTL access
token per job and **never a refresh token**. Revocation is immediate and kills in-flight sessions using
that grant.

**Never transmitted at all.** Signing identities, licences, and private registry tokens are configured
locally on the runner host by you. iOS certificates, macOS notarisation credentials, Android keystores,
Unity licences, and private feed tokens are never sent by the control plane. The agent may trigger a
signed build; it can never read the signing material. Charter's images contain toolchains, never
entitlements.

**Charter never generates secrets.** When provisioning a new project, it emits a checklist of what a
human must set. When promoting a repository between organisations, it emits the list of secret names
and you set the values.

## Deployment security

**First run is closed by default.** A self-hosted application that boots with open registration gets
hijacked by whoever finds it first. Charter boots with zero users into **setup mode**, serves nothing
but the setup route, and writes a **one-time setup token to stdout**. You read it from your container
logs. That token creates exactly one admin account and expires. Setup mode ends permanently and cannot
be re-entered while a user exists. There is no default password.

**Do not expose the Docker API over TCP.** Charter supports pointing its Docker runner at a remote
daemon, and you should not use it. A network-reachable Docker daemon is root-equivalent access to that
host and a permanent target; mTLS changes who can reach it, not what they get once through. Run a
Charter Agent on that machine instead — it dials outbound, needs no inbound ports, and keeps the socket
on the host.

**Mounting the Docker socket into the Charter container** grants that container root-equivalent access
to its host. Acceptable on a machine dedicated to Charter; a bad trade on a machine running anything
else.

**Native-mode agents have weaker isolation than container mode**, and that is worth planning around. A
Charter Agent in `--mode native` runs jobs under a dedicated unprivileged user with a scoped working
directory, but the isolation is process-level, not container-level: the session shares the host's
filesystem outside its working directory, its installed software, and its network position. Native mode
exists because macOS with Xcode cannot be containerised and USB-attached targets are awkward to pass
through — not because it is equivalent. **Run native agents on a dedicated machine or VM, not on an
engineer's daily driver.**

**Repository creation is a privilege escalation** and is gated three ways: instance opt-in
(`CHARTER_ALLOW_REPO_CREATION=false` by default), a GitHub App scope you grant deliberately, and a
distinct `can_create_repo` role capability. The standards repository and template repositories are
outside every agent's write scope, always. See [standards.md](standards.md).

**Transcripts contain your source code.** Setting `CHARTER_LOG_INCLUDE_TRANSCRIPTS=true` exports
transcript bodies to every enabled log sink. If any of those is a third-party SaaS, that is your code
leaving your infrastructure. It is off by default. See [privacy.md](privacy.md).

## Audit and accountability

Every agent action is attributable to a named human. The audit log records the actor, the action, the
target, and the metadata for every privileged operation — scope changes, budget changes, credential
grants, repository connections, role assignments, auto-dispatch policy edits.

Two supporting properties:

- **The agent has no initiative.** There is no scheduler that starts sessions and no automatic retry
  loop. Every session begins with a person.
- **Take-over stops agent writes.** When an engineer takes over a branch by hand, Charter marks the
  session `handed_off` and stops touching it. An agent and a human editing the same branch concurrently
  is the one genuinely destructive failure mode in this design, and it is prevented explicitly rather
  than by convention.

## What Charter does not protect against

Stated so you can plan for it:

- **A compromised dependency in your own project.** Covered above. Lockfile installs and disabled
  install scripts reduce it; nothing here removes it.
- **A malicious or compromised model provider.** Your specifications and repository content are sent to
  whichever provider you configured. Charter does not inspect or restrict what a provider does with
  them.
- **A compromised agent CLI.** Adapters describe how to invoke a third-party binary. That binary runs
  with the runner's access. Pin the CLIs you use and get them from the images you build.
- **An operator with database access.** Anyone who can read your Postgres and holds
  `CHARTER_CREDENTIAL_KEY` can decrypt stored credentials. Treat the database as the sensitive asset it
  is.
- **A malicious approver.** Someone with approval rights can dispatch expensive or ill-conceived work.
  Budgets bound the cost; the merge gate bounds the consequence.
- **Denial of the requester's own time.** Rate limits at intake bound queue abuse, but a determined
  insider can still waste tokens up to their budget.

## Related

- [`SECURITY.md`](../SECURITY.md) — supported versions and private vulnerability reporting
- [runners.md](runners.md) — execution modes and isolation
- [credentials.md](credentials.md) — credential storage and the resolution chain
- [charter-folder.md](charter-folder.md) — path scope and migration policy
- [privacy.md](privacy.md) — what leaves your infrastructure
- [spec §7.4, §15, §16, §20b.2, §32, §33](../agent-docs/spec.md) — the full specification
