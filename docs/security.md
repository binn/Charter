---
title: "Security"
description: "Charter's full threat model: the trust boundary, why the agent never sees raw user input, prompt injection, toolchain supply chain, secrets, audit, and what it does not protect against."
---

# Security

Charter runs an AI coding agent against your source code on behalf of people who cannot read it. This
page is the full threat model: what Charter is structurally protected against, what it mitigates in
layers, and what it does not fix.

To report a vulnerability, follow the private disclosure instructions in
[`SECURITY.md`](https://github.com/binn/Charter/blob/master/SECURITY.md) at the repository root. Please do not open a public issue.

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

### How the boundary is enforced in code

The property above is only worth as much as its enforcement, so it is enforced by the type system
rather than by reviewer discipline:

- **Requester input is not a string.** It is carried in a dedicated type whose `ToString()` returns a
  placeholder, and whose characters are reachable only through a narrowly scoped reveal method with a
  single call site — the prompt builder. Logging it, concatenating it, or interpolating it into a
  prompt does not produce the text.
- **There is one door to dispatch.** The agent briefing is constructible only from an approved
  specification, and an approved specification is produced only by confirming a spec. Neither accepts
  a raw string or a request. Adding "just append what they actually said" means adding a new API and
  defending it in review, rather than editing one line.
- **A requester conversation turn refuses to yield its text.** Building a transcript to paste into a
  prompt fails loudly instead of quietly leaking.
- **Instruction-shaped input is flagged on ingest** — role-override phrasing, imperatives addressed to
  an agent, base64 blobs, URLs, and zero-width or bidirectional characters. A flag blocks both
  confirmation and promotion to a build until an engineer clears it.

The system prompt does also fence requester text as data, but that is a layer and not the defence.
Charter does not rely on instructing a model to ignore injected instructions.

Two caveats, stated plainly:

- Auto-dispatch can skip the human step ([spec §7.5](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)). The model-authored
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

**A runner's credentials are scoped to one session, not to its repository.** This distinction is
easy to lose and expensive to get wrong. On GitHub Actions the workflow authenticates to Charter with
`secrets.CHARTER_SESSION_SECRET`, which is written once per repository — every workflow run in that
repository reads the same value. Proving you are a run in a repository is therefore *not* proving
which session you are, so the exchange requires a second factor: a per-session token minted at
dispatch and delivered only in the dispatch for that session. A run started for one session cannot
produce another session's, and so cannot obtain another session's repository token, another session's
callback token, or the ability to write another session's transcript and declare it failed. The
session token is not a credential and grants nothing on its own, which is why it is safe in a dispatch
payload that anyone with repository read access can see, and the repository secret never appears in
one.

**Credentials are minted only while a session is genuinely running.** Every mint — the HTTP exchange
and the Charter Agent's claim alike — is refused for a session that has ended, has had cancel pressed
on it, or that no backend has dispatched. The credentials a session receives are short-lived by
design, and a token issued for work that has already been called off would outlive the thing that
justified it by up to twelve hours. The repository a token is scoped to is read from the session's own
record rather than taken from whatever the caller named.

**Nothing a runner reports about where it is running is taken at its word.** A runner tells Charter
the URL of the run it is executing as — `run_url` — so that the status thread can link to it and so
that pressing Cancel has something to cancel. That value originates inside the sandbox, in the same
process as an agent reading repository content Charter does not control, so it is treated as a claim
rather than a fact. Charter checks it against the repository the session belongs to, read from the
session's own record, and **refuses the callback outright if the two disagree** rather than
normalising it: a reference naming another repository is a lie, and there is nothing to sanitise.
The credential exchange refuses to mint at all in that case, because a run that has just misreported
where it is running is not one to lend a contribute-scoped token to. The same check runs again when a
run is cancelled, so a reference recorded by an older version of Charter cannot be acted on either.
Without it, cancelling one session could issue a write against **any other repository connected to
the instance**, using the instance's own credential — and report success while the session it was
supposed to stop kept running and kept spending. The same rule covers the other backends' handles:
Charter will not kill a container, or cancel an agent job, that does not belong to the session being
cancelled.

**Nor which branch it pushed to.** A runner reports the branch its work landed on, and Charter then
moves that ref on the provider. That report is written in the same sandbox as everything else, so
Charter publishes **only** the session's own branch — `charter/session-<id>`, computed from the session
id and nothing a callback contains — and refuses the publication outright when a `branch_pushed` names
anything else. The push is fast-forward-only, which is no protection here at all: an agent's commit is
a descendant of your base branch in exactly the ordinary case, so a believed report could advance
`main` without a review, a merge, or a pull request. A refused session ends as failed with the reason
on its transcript and a warning in the operator's log; whatever the runner really pushed is still on
the provider for an engineer to look at, and nothing is quietly rewritten to look correct.

**A file path in a transcript is not a path in your repository.** The code pane reads one file at a
time from the provider, and both the path in the request and the list of paths it is checked against
come from the session's own `file_write` events. A path that climbs out of the repository — a `..`
segment, a leading `/`, a backslash, a percent sequence — is refused rather than cleaned up, at the
pane and again in the GitHub client, where every path, ref and revision that goes into a URL is
checked. Without that, a path an agent chose could address a different repository on the API host with
this session's installation token attached. The reader sees the same *no such file* the pane already
gives for a file outside the change.

**What a check said is quoted, never rendered.** Charter writes the pull request body, and an engineer
reads it as Charter's account of the session. The check names and summaries in it come from the
session, so they appear as inline code — one line each, length-bounded, at most twenty checks listed
with the rest counted — and never as live markdown. A summary cannot become a link somebody clicks, a
mention that pages a real person, a heading that hides the failing-check block, or a blockquote that
fakes the *no human approved this specification* disclosure. It also cannot grow the body past the
provider's size limit, which would otherwise let a session stop its own work being reviewed by
talking too much.

**Transcript and code panes are gated on repository read access, not on user preference.** A requester
toggling to the detailed view would otherwise be a permission bypass: transcripts leak file paths,
environment variable names, dependency versions, and error output. The API omits engineer-only fields
server-side rather than hiding them in the client — authorization is not a rendering concern.

**Guardrails live in your repository.** Path scopes, denied directories, and validation commands are in
a committed `.charter/config.yml`. Widening what an agent may touch requires a pull request and a
review. See [charter-folder.md](charter-folder.md).

**Path scope is enforced in the runner, not the UI.** A session cannot widen its own scope, because
enforcement does not sit on the side the agent can influence.

**Stored objects are proxied, not linked.** When `CHARTER_STORAGE_BACKEND` is set, offloaded
transcript output is read back at `GET /api/requests/{id}/blobs/{key}`, behind the same repository
read check that governs the transcript pane. Charter generates no public bucket URLs and no presigned
links, and has no setting that would: a link that authorises whoever holds it is a permission bypass
that never reaches Charter to be refused. Object keys built from untrusted strings — a check name out
of a repository's `.charter/config.yml`, a file path out of an agent's tool call — are reduced to a
single safe path segment, and every read is checked against the session the caller was authorised
for, so a key copied from one request cannot address another's bytes. Objects are served as
attachments with `X-Content-Type-Options: nosniff` and a content type derived from the key Charter
chose, never from what the store reports.

## The merge gate is only as strong as your provider makes it

The guarantee above — no merge button, merge authority outside Charter — rests on something Charter
does not control: your version control provider actually refusing an unreviewed merge. Charter
records what each provider can do, and what your repository has actually configured, because those
are two different facts.

| Enforcement | What it means |
|---|---|
| `provider_enforced` | The provider refuses the merge itself. The guarantee above holds unchanged. |
| `advisory` | Nothing stops a person from merging agent-written code without review. **Charter will not do it, but Charter cannot prevent it either.** |

A repository is `provider_enforced` only when **both** are true:

1. The provider supports branch protection and honours it — GitHub, GitLab, Gitea, Bitbucket and
   Azure DevOps all do; a plain git remote does not.
2. **That repository has a rule configured on its base branch that requires a review before merge.**

The second condition is the one that catches people out. A GitHub repository with no branch
protection rule is *functionally advisory*, however capable GitHub is. Charter therefore checks the
rule rather than the provider: during repository onboarding it reads the base branch's protection and
reports the result, the check is written to the audit log whichever way it comes out, and an
unprotected repository is flagged in the onboarding wizard and in the repository's settings with this
wording:

> No branch protection rule covers `main`, so nothing stops a person from merging agent-written code
> without review. Charter will not do it, but Charter cannot prevent it either. Add a rule requiring
> review before merge to get the guarantee Charter's security model describes.

Two further notes on what counts:

- **A rule that only blocks force pushes is not a merge gate.** Charter reports a repository as
  enforced only when the rule requires an approving review or a CODEOWNERS review.
- **If Charter cannot read the rule, it reports "not verified", never "protected".** An installation
  without permission to read branch protection produces a warning, not a reassurance.

An advisory repository is still usable. It simply carries a different risk posture, and the operator
is told what it is rather than left to assume otherwise. If you want the strong guarantee, the fix is
a protection rule on the base branch requiring review, plus CODEOWNERS on anything sensitive —
`.charter/`, migrations, auth, and infrastructure.

## Prompt injection

The agent consumes untrusted text from two directions: non-engineers filing requests, and the
repository's own content — dependency READMEs, issue text, code comments, fixture data.

The primary mitigation is structural, described above. Layered on top:

- **Egress allowlist in the runner.** Package registries and the model API, and nothing else.
  Exfiltration needs somewhere to send data.
- **The runner sees no control-plane environment.** Short-TTL, single-repository token, nothing else.
- **Instruction-shaped language is flagged for review** before dispatch: imperatives addressed to an
  agent, base64 blobs, URLs. Flagged requests go to an engineer. The flags are stored, so a container
  restart mid-conversation does not quietly clear a review that had not happened yet — and a
  conversation reloaded from the database refuses to be read as model-authored text just as the live
  one does.
- **Every file write and network call is logged**, attributable to a session and a named human.
- **The agent never acts on its own initiative.** No schedulers, no infinite auto-retry. Every session
  traces back to a person who asked for something.

**Charter does not rely on telling the model to ignore injected instructions.** That is a layer, not a
defence, and treating it as one is how these systems fail.

**Pull request comments are untrusted too, and they reach one narrow path.** Charter reads comments on
its own change requests to find preview environments, because most hosting platforms announce a preview
by commenting and nowhere else. That text never reaches an agent and is never shown to a requester: it
is truncated on arrival, read only for a URL and a state word, and the result binds to the head commit
of a change request Charter itself opened. The configured deployment provider also checks the comment
came from its own bot before anything is believed. The worst a convincing comment achieves is a wrong
preview URL on a change request its author could already comment on — so if that matters to you, report
previews through `POST /api/deployments/{prSha}` instead and do not subscribe the App to comment events.

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

**The SMTP password.** `CHARTER_SMTP_URL` carries a credential, so it is treated as one. It is
wrapped in the same redacting type as every other configuration secret, log lines name the mail
server as `host:port` rather than as a URL, and a rejected sign-in to the mail server is reported by
status code rather than by echoing the server's response — some servers quote back what was
submitted. If `CHARTER_SMTP_TLS` is `starttls` and the server does not offer it, Charter stops rather
than sending the password over an unencrypted connection.

## Sign-in

Every sign-in method sits behind one identity provider seam. Email and password is always available;
GitHub, Google, Discord, and Slack appear only when both halves of their credential pair are
configured. A user may hold several linked identities at once — one row per provider.

**Passwords are never stored and never logged.** What is stored is an ASP.NET Core `PasswordHasher`
verifier — PBKDF2-HMAC-SHA256, per-password salt, 600,000 iterations — on the password provider's
identity row. Nothing hand-rolled, and no password reaches a log statement: the value is carried in a
type whose string representation is a placeholder. Passwords must be at least 12 characters, and there
are no composition rules, following NIST 800-63B.

**Sign-in refusals are indistinguishable.** A wrong password and an unknown address return the same
message, and an attempt against an address with no account still performs the full hashing work, so
response time is not a user-enumeration oracle.

**Repeated failures are throttled** per address and client, in process. This is a brake on guessing,
not a distributed rate limiter; a horizontally scaled deployment throttles per instance.

**An invitation link is a credential, and is stored like one.** The invitation row holds a SHA-256
digest of the emailed token, never the token. Nobody with a database dump — yours or an attacker's —
can replay one against the redemption endpoint, and nobody inside Charter can retrieve a link that
was lost; it is reissued instead. An invitation is **single use**, enforced by the database rather
than by a check in front of it: redemption is one conditional update, so two clicks on the same link
at the same instant produce one account and one refusal. Links expire after seven days, and an admin
can withdraw one that has not been spent.

The digest is a plain hash rather than a PBKDF2 verifier, unlike a password. A password is
low-entropy and needs a slow hash to survive an offline attack; an invitation token is 256 bits of
CSPRNG output, where the cheapest attack is guessing the entire keyspace. What the plain digest buys
is the indexed lookup — a redemption arrives with a token and no identity, so the token has to find
its own row.

**Signing in with a configured OAuth provider never creates an account.** A verified external subject
resolves to an existing user by its provider subject id, or by an email address that already belongs to
someone here — otherwise it is refused. Accounts come from the setup token or from an invitation, both
of which are deliberate acts by a named human. Open registration through a side door would undo the
first-run guarantee below.

**SAML is not available in this build.** The seam is shaped for it, and `CHARTER_SAML_METADATA_URL` is
parsed and validated, but no SAML button is offered and a warning is logged at startup if the variable
is set.

The session cookie is `HttpOnly`, `SameSite=Lax`, and — on an `https` deployment — `Secure` and
`__Host-` prefixed. The frontend never reads it: Charter writes no browser storage and no cookies from
JavaScript. Roles ride in the cookie for cheap rendering decisions, but nothing security-relevant trusts
them; the member row is re-read for every authorization decision, so a cookie that outlives a revoked
role grants nothing.

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
distinct `can_create_repo` role capability. All three are checked together, and the refusal names
whichever gate stopped it. The standards repository and template repositories are outside every
agent's write scope, always. See [standards.md](standards.md).

There is nothing in Charter today that creates a repository — the new-project flow of §26.4 is not
built — so the gate protects a door with nothing behind it yet. Leave the variable off until the flow
exists; there is no reason to grant the App the scope in the meantime.

**Transcripts contain your source code.** Setting `CHARTER_LOG_INCLUDE_TRANSCRIPTS=true` exports
transcript bodies to every enabled log sink. If any of those is a third-party SaaS, that is your code
leaving your infrastructure. It is off by default. See [privacy.md](privacy.md).

## The deployment webhook takes a secret

`POST /api/deployments/{prSha}` is how a hosting platform tells Charter a preview is ready. It is
reachable by anyone who can reach your instance, and it is **refused entirely** until you set
`CHARTER_DEPLOYMENT_WEBHOOK_SECRET`.

The head commit SHA in the path is not a credential and was never issued to anybody. It is authored
inside the session that produced the branch, and from the moment the pull request exists it is on the
pull request page, in every fork of the repository, in CI logs, and in notification emails. Anybody
holding it could previously attach a URL of their choosing to a real request — a URL Charter then
fetched from inside its own network, and showed the requester as a link Charter's own copy calls safe.

The SHA still decides *which* pull request a report binds to, which is what keeps the endpoint from
being an enumeration tool. Admission is the secret. Missing and wrong produce the same `401`.
Configuration, and the three ways to present it, are in
[configuration.md](configuration.md#the-deployment-webhook-needs-a-secret).

## Preview URLs are validated before Charter stores, fetches, or shows one

A preview URL comes from outside the trust boundary — a platform's webhook, or a bot comment on a
pull request that anybody with repository access can write. Charter does two things with it that make
an unchecked value dangerous:

- **It fetches the URL** on a loop, from inside the control-plane container, for the "responding" dot
  on the preview card. An unchecked URL makes that a repeating request against anything your container
  can reach: your database's admin port, an internal service, or your cloud provider's instance
  metadata endpoint at `169.254.169.254`.
- **It shows the URL to a requester** as a button, underneath Charter's own sentence — *"Nothing you
  do here touches the real one."* That reassurance is the product's promise to the person least
  equipped to evaluate a link themselves.

So every URL is checked once, before it is stored, on the path both ingestion routes share. Refused:

| Refused | Why |
|---|---|
| Anything that is not `http` or `https` | `file:`, `gopher:` and friends are not previews |
| Credentials in the URL (`https://user:pass@host/`) | A link that reads as one host and authenticates to another is exactly what a requester cannot evaluate |
| Loopback — `127.0.0.0/8`, `::1`, `localhost`, `*.localhost` | Means "this container" to Charter and "this laptop" to the requester; never the same machine |
| Link-local — `169.254.0.0/16`, `fe80::/10` | Where every cloud provider parks instance metadata |
| Private — RFC 1918, CGNAT `100.64.0.0/10`, IPv6 unique local | Unless you opt in; see below |
| A hostname that **resolves** to any of the above | A name can point anywhere, so resolution decides rather than the spelling |

A refused report is recorded as a failed deployment with no URL. The requester's card settles on the
designed failure state — an honest sentence and no button — rather than spinning forever on a preview
that is not coming, and an engineer sees the refusal, the URL and the reason in the log.

Three further defences, because one check is never enough for a value that can change under you:

- **The URL is checked again before it is rendered**, so a row written by an older version of Charter
  cannot become a button after an upgrade. Upgrades do not rewrite existing rows.
- **The fetch checks the address at the socket.** DNS can answer differently between the moment a URL
  is validated and the moment it is fetched. Charter's probe resolves the name itself, connects only
  to an address that survives the same rules, follows no redirects — a redirect is a second URL nobody
  checked — bounds the response, and sends no cookies, credentials, or proxy.
- **The browser checks too.** The card refuses to render a link, a copy button, or a QR code for a URL
  that fails the structural rules, whatever the API sent.

### If your previews live on a private network

Set `CHARTER_PREVIEW_ALLOW_PRIVATE_HOSTS=true`. It re-admits RFC 1918, carrier-grade NAT, and IPv6
unique local addresses — the ranges a homelab or a VPC-only preview legitimately sits in, where your
requesters are on that network too and the link genuinely works for them.

It does **not** re-admit loopback or link-local, with no override, because no preview has ever lived
at `127.0.0.1` or `169.254.169.254` and both are Charter fetching from itself.

Understand what you are trading: with it on, anyone who can reach the deployment webhook can aim
Charter's probe at services on your private network and learn, from the reachability dot, whether
something answers on a given host and port. Charter warns about this at startup whenever it is on.

**A stated limitation.** A hostname that does not resolve at all is accepted rather than refused — a
transient DNS failure must not permanently mark a working preview as broken — and nothing rests on
that leniency, because the socket-level check runs when the URL is actually fetched.

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

- [`SECURITY.md`](https://github.com/binn/Charter/blob/master/SECURITY.md) — supported versions and private vulnerability reporting
- [runners.md](runners.md) — execution modes and isolation
- [credentials.md](credentials.md) — credential storage and the resolution chain
- [charter-folder.md](charter-folder.md) — path scope and migration policy
- [privacy.md](privacy.md) — what leaves your infrastructure
- [spec §7.4, §15, §16, §20b.2, §32, §33](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full specification
