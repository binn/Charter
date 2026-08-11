---
title: "The loop"
description: "What actually happens between a typed request and a preview link: every state, the plain-language label a requester reads, what moves a request from one state to the next, and what happens when it goes wrong."
---

# The loop

One request, from somebody typing a sentence to somebody clicking a preview and saying whether it
worked. This page is the mechanism, in order, as implemented.

```
request -> refinement -> spec -> approval -> session -> change request -> preview
        -> "what to check" -> Works / Not quite
```

Read [getting-started.md](getting-started.md) if you want to stand an instance up. Read this if you
want to decide whether to trust one.

## The state machine

```
Draft -> Refining -> SpecReady -> Queued -> Running <-> NeedsInput
                        |                      |
                        v                      v
                    Rejected              PrOpen -> PreviewReady -> InReview -> Merged
                                               |
                                               +-> Failed / Cancelled / Stale

                         Running -> NoChangesNeeded
```

Two of these states notify anybody. Exactly two. Notifying on all of them gets Charter muted within a
week, so the notify-worthy set is a closed list checked in one place rather than a decision made at
each call site.

| Internal | What the requester reads | Notifies |
|---|---|---|
| `Draft` | Not sent yet | no |
| `Refining` | Let's figure out what you need | no |
| `SpecReady` | Waiting on {name} to approve | no |
| `Rejected` | Sent back for another look | no |
| `Queued`, `Running`, `PrOpen` | Building this now | no |
| `NeedsInput` | Question for you | **yes** |
| `PreviewReady` | Ready to try | **yes** |
| `InReview` | An engineer is checking it | no |
| `Merged` | This is live | no |
| `NoChangesNeeded` | Nothing needed changing | no |
| `Failed` | This turned out to be bigger than expected | no |
| `Cancelled` | You stopped this | no |
| `Stale` | This needs redoing against the latest code | no |

**No state ever carries an ETA.** Elapsed time only, computed backwards from a start timestamp. Agent
runs are wildly variable, and one blown estimate costs more trust than ten honest slow ones.

`Draft` exists in the model but is not observable through the API: intake creates the request and moves
it to `Refining` in the same operation.

## 1. Intake

`POST /api/requests` with a project id and the requester's own words, capped at 8,000 characters.

Charter checks, in order, that the project id parses, that the text is not blank and not too long, and
that this member may file against that repository. That last check is deny by default: a repository is
requestable by nobody until somebody is explicitly scoped to it, and a repository that has not passed
its smoke test is not requestable by anyone at all. An unknown repository id and an unscoped one get
the same wording, so the API is never an existence oracle.

The request lands in `Refining` and a refine job is enqueued. **Nothing that could write to a
repository is queued**, because nothing has been through refinement yet.

Intake is rate-limited per user and per organisation. It is the one endpoint a script could use to
queue four hundred sessions.

## 2. Refinement

The core novel component, and a security boundary.

A background handler claims the refine job, loads the conversation, and advances it by one turn against
a model. Each turn ends in one of four outcomes:

- **A clarifying question.** Back to `Refining`, waiting on the requester.
- **An answer.** Chat-shaped turns that resolve a question without producing a spec.
- **A refusal.** The request would touch a denied path, or the repository has no scope configuration at
  all. The requester gets a plain-English explanation naming *areas*, never paths, and the request moves
  to `Rejected` — which reads as *Sent back for another look*, not as an error.
- **A proposed spec.** A new spec version is written and the request moves to `SpecReady`.

### It refuses to produce a spec while anything is ambiguous

This is not left to the model's judgement. After the model returns a spec, Charter runs its own
ambiguity guard over it: any open question the model left, plus any acceptance criterion containing a
hedge — `tbd`, `etc.`, `as appropriate`, `as needed`, `maybe`, `???`. If the guard finds anything, **no
spec row is written**, and the conversation continues with the outstanding questions.

Those questions are the one engineer-adjacent field that reaches the requester, deliberately. They are
what blocks the confirm button, they are written in plain language, and they carry no path, no SHA and
no cost. Withholding them would leave somebody looking at a disabled button with no way to learn why.

### What the model is given, and what it is not

Refinement loads your `glossary.yml` for domain vocabulary and the repository's committed scope
configuration for what may be touched. It reads that configuration from a stored snapshot rather than
from a live repository fetch, and it **fails closed**: a repository with no readable configuration is
treated as allowing nothing, which is why an un-onboarded repository refuses every request rather than
guessing.

The requester's words are carried in a type that refuses to be interpolated into a prompt. There is one
reveal method, with one call site, in the prompt builder — where the text is fenced and labelled as
data. What comes out the other end, and what the agent eventually receives, is a document written by a
model and approved by a human. See [security.md](security.md).

### The confirmation card

The spec is a structured object with two renderings from one source. The requester sees the title, the
plain-language outcome, the acceptance criteria, and any outstanding questions. The engineer sees those
plus the technical approach, the file scope, and the risks.

The acceptance criteria are shared **verbatim** between the two, and rendered verbatim again as the
"what to check" list beside the preview button. They are the contract. If the two renderings could
drift, *"the spec said X"* would stop meaning anything.

The requester approves the acceptance criteria, not the technical approach. That is the thing they can
meaningfully judge.

## 3. Approval

The spec appears in the approval queue with an estimated cost. Approving moves the request from
`SpecReady` to `Queued` and enqueues a build job naming the spec.

**This is the spend gate and nothing else.** It asks whether the work is worth burning tokens on. The
merge gate — is this code fit to ship — is your branch protection and your CODEOWNERS, it lives
entirely outside Charter, and it is not represented in Charter's data model. Because the merge gate
cannot move, loosening the spend gate is safe: the worst case is wasted tokens and a pull request
nobody wanted, never shipped code.

Who may approve: any member holding the approver role, on a repository in the same organisation, for a
spec that is not already approved. **Self-approval is allowed** — in a small team the requester and the
approver are the same person, and pretending otherwise just means nobody can ever dispatch anything.

Sending it back requires a note saying what needs changing. That returns the request to `Refining` and
starts another refinement turn; the previous spec version stays on the record un-approved, and the next
proposal is version + 1.

### Auto-dispatch

`SpecReady` can be skipped. The policy is conditional rather than a boolean — trust this person, up to
this much, in this area — and it is resolved from policy rows, not from a flag:

- **No applicable policy means no auto-dispatch.** The default is to wait for an approver.
- The most specific policy wins: a rule naming a user outranks one naming a role, which outranks one
  naming only a repository. Ties are folded by taking the tighter of the two.
- **A repository may only tighten what the organisation allows**, never loosen it. There is no way for a
  repository-level setting to enable auto-dispatch that the organisation did not.
- A requester who has left the organisation blocks it outright.
- A spec whose refinement turn raised an injection flag is never auto-dispatched.

An auto-dispatched session is labelled `unreviewed-spec` on the change request, and the engineer recap
leads with the fact that nobody vetted the spec. Where the provider has no labels, the fact is written
into the change request body rather than dropped.

## 4. Dispatch

Approval does not create a session. It writes a job row, and the dispatcher claims that row.

That indirection is the whole restart story. One replica holds a Postgres advisory lock and does the
claiming; claims are `SELECT ... FOR UPDATE SKIP LOCKED` in batches, with a lease that expires if the
worker dies. A control plane that restarts between approval and dispatch still dispatches — exactly
once, because the session's id is derived deterministically from the spec id, so a duplicate insert
loses on the primary key rather than creating a second session.

The dispatch event is written to the session journal **before** the backend is called, keyed on a
generation counter. A second dispatch attempt collides on that key and stops. A backend that genuinely
refused bumps the generation, so an honest retry gets a fresh key.

Routing picks a runner that is online and advertises the capabilities the session needs. See
[runners.md](runners.md).

## 5. The session

The runner executes the agent inside a sandbox and streams events back to
`/api/runners/sessions/{id}/events`. Every write is idempotent, because a runner that loses its
connection retries and a control plane that restarted cannot know whether it saw a delivery before.

The requester sees four translated milestones, not the transcript: *understanding the current setup*,
*making the changes*, *checking it works*, *putting it together*. Everything else stays in the engineer
view, which is gated on repository read access rather than on a preference — transcripts leak file
paths, environment variable names and error output.

An agent that needs an answer moves the request to `NeedsInput` — *Question for you* — one of the two
states that notify.

Four post-hoc actions exist for an engineer watching a session: approve it after the fact, steer it with
a new instruction on the same branch, revise the spec onto a fresh session, or take it over. **Taking
over is irreversible server-side** and stops all further agent writes to the branch, because concurrent
human and agent edits in one worktree is the single genuinely destructive failure mode in this design.

## 6. The change request

When a session reports a clean completion, a reconciliation pass — not the result callback — publishes
the session branch and opens a change request against the repository's base branch.

Running it from reconciliation rather than from the callback is deliberate: a control plane that died
between the two still opens it, and a second pass is a no-op rather than a second pull request.

The change request records its number, URL, head branch, head commit and author. The author matters
because some preview platforms refuse to deploy a branch from an account outside their workspace, and
a preview that never arrives with no explanation is the worst version of that failure — the warning has
to be able to name who to invite.

Labels applied where the provider supports them: `unreviewed-spec` when the session was auto-dispatched,
`schema-change` when the session's migration classification says so.

The body states what the repository's own checks made of the change, and says so plainly when the
repository declares none. A check that failed does not stop the change request being opened: Charter
cannot merge it, so the useful thing to do with a failure is put it in front of the engineer along with
the branch they need to fix or take over the work.

Charter learns about the change request's life afterwards from the GitHub webhook: state changes,
reviews, and staleness.

- A review, or a review request, moves the request to `InReview` — *An engineer is checking it*. Any
  review does: approved, changes requested, or a comment. Charter never reports a verdict on the code,
  because that is not its to report.
- A merge moves it to `Merged` — **This is live**. That is the end of the loop, and the only thing the
  requester ever needed to be told about it.
- A change request is marked stale only when it is **behind the base branch and overlaps it on changed
  files**. Merely being behind is not stale — most open change requests are behind most of the time,
  and a flag that fires on all of them is one everybody ignores. Staleness is recorded on the session's
  transcript; the change request still needs a rebase, and Charter does not do it for you today.

Neither `InReview` nor `Merged` notifies. Both need the GitHub App subscribed to **Pull request review**
and **Pull request** respectively; without those subscriptions the thread simply stops at the last thing
Charter was told.

**There is no merge button, and there will not be one.**

## 7. The preview

Charter does not create preview environments. It binds whatever your platform created back to the
change request, keyed on the head commit SHA. Three paths exist, and the first needs no configuration
at all:

| Path | Enabled by | Notes |
|---|---|---|
| Generic webhook | always on | `POST /api/deployments/{prSha}` with `{ url, state, provider }`. See [api.md](api.md). |
| Change request comment | `CHARTER_DEPLOYMENT_PROVIDER=railway` | Reads comments from Railway's bot accounts only, caps the body it will parse, and runs its regex under a timeout. Fragile but universal. |
| Provider polling | `CHARTER_DEPLOYMENT_PROVIDER=railway` | A background loop asks Railway directly, about once a minute per change request. |

`CHARTER_DEPLOYMENT_PROVIDER=none` is the default and a first-class configuration, not a broken one: it
means webhook-only, with no polling, no comment parsing, and no teardown.

A `ready` report writes a verification artifact, probes the URL for reachability, and moves both the
session and the request to `PreviewReady`. The requester is notified **once** — a later reconcile pass
that sees the same news tells nobody again. The notification carries the acceptance criteria as the
"what to check" list and a link back to the thread. It carries no repository, branch, commit or cost;
that is checked by a test, because an email is the easiest place to leak them.

Without that list a preview URL is a dead end. It is the criteria the requester approved, verbatim, not
regenerated per surface.

## 8. Works, or Not quite

Two buttons. Do not make them write a bug report.

- **Works** records the verdict and asks for the engineer recap. It does not start another build.
- **Not quite** opens a box, records the verdict and the note, and starts **a new session on the same
  spec, in the same thread**. The session id is derived from the feedback row, so it is a genuinely new
  session rather than a resumed rejected one.

One thread per request, forever. Multiple sessions, revisions and follow-ups collapse inside it, so a
requester never wonders which of three cards is live.

## When it goes wrong

### The session changed nothing

`NoChangesNeeded` is a **success**, and its copy reads like one:

> Nothing needed changing here. Most often that means what you asked for already works the way you
> wanted. Nothing went wrong, and there is nothing for you to do.

It notifies nobody and pages no engineer. The agent ran, ran correctly, and found nothing to change —
usually because the thing being asked for already works. Reusing the failure wording here would tell a
requester their request "turned out to be bigger than expected" when in fact it turned out to be
nothing at all, which is the single most misleading sentence the state machine could produce.

Charter reaches it three ways: the branch was never pushed and no revision was reported; the branch head
is still the base commit; or comparing base to head found no changed files.

### The request is still ambiguous and never dispatches

It sits in `Refining` with outstanding questions, and it stays there until somebody answers them. That
is working as designed — a request that dies in conversation because the answer is *it already does
that* is the cheapest possible outcome Charter has.

It is also a dead end if nobody is watching, because `Refining` does not notify. If a request has sat
there for days, the questions on the spec card are what is blocking it.

### Nothing is watching the approval queue

`SpecReady` does not notify either. A spec with no approver in the organisation and no auto-dispatch
policy waits indefinitely, and the header reads *Waiting to be approved* with nobody named. If you run
a one-person instance, either hold the approver role yourself or configure auto-dispatch.

### No runner can take the session

The session **queues with a written explanation** rather than failing. The runners view shows what each
waiting session requires and which agents, if any, could satisfy it. An empty eligible list means
nothing on this instance can run it — register an agent, or widen the backends in `CHARTER_RUNNER`.

`CHARTER_RUNNER=docker` on a host with no reachable Docker daemon registers a runner that reports
itself **offline**, so the session queues with *Every runner that can build this is offline* rather
than with silence. Start Docker, point `CHARTER_DOCKER_SOCKET` at the right path, or use `agent`.

### The preview expires

Previews are ephemeral by design. `CHARTER_PREVIEW_TTL_HOURS` defaults to 72; `0` means never expire.

- The expiry is stamped **once**, when a preview first becomes ready or when its URL changes.
  Reconciliation does not extend it.
- The card shows *expiring* and then *expired* from the clock, not from a sweep, so it is never stale in
  the browser.
- A sweep marks expired artifacts and tears the environment down where a provider supports teardown.
  With `CHARTER_DEPLOYMENT_PROVIDER=none` there is nothing to tear down and the artifact is simply
  marked expired.
- **Closing the change request expires the preview immediately**, merged or not.

The requester's primary action on an expired preview is Rebuild.

### The session fails

Budget exhaustion, a stuck agent, and failing checks all arrive in the requester's thread as one
sentence:

> This turned out to be bigger than expected. An engineer has been told and will pick it up — you do
> not need to do anything.

The real detail goes to the engineer view. A non-engineer who sees a stack trace once never files
again.

If the runner never reports at all, the job's lease lapses and it is retried, up to three attempts,
before being marked terminal.

### Somebody presses Cancel

Cancel has to actually stop the work, not just change a label. It requests cancellation on the session,
tells the runner, cancels any pending queue rows naming that session, and settles the token cost. Each
half is idempotent, so a control plane that crashed mid-cancel resumes it.

What "tell the runner" means depends on the backend:

- **Charter Agent** — a cancel frame down the agent's own socket. If the agent is not connected, its
  lease lapses instead.
- **GitHub Actions** — cancels the workflow run. **If the workflow has not yet reported its run URL
  there is nothing to cancel**, and the run continues to completion; only the session is settled.

## Where the loop stops today

Stated plainly, because a docs set that oversells gets discovered.

**A stale change request is not rebased for you.** §17 asks for an attempted auto-rebase — clean rebase
plus green checks means the change request is quietly brought up to date, and a conflict becomes a new
session with the conflict as context. Charter detects and records staleness today and stops there. The
rebase itself needs a checkout, which means a runner, which means dispatching work that is not a
session; until that exists, a stale change request is rebased by whoever is reviewing it.

Three smaller gaps elsewhere in the loop:

- **`CHARTER_MODEL_REFINE` is parsed and validated, then ignored.** Refinement runs on a hard-coded
  model identifier that resolves to the Anthropic provider. An instance holding only an OpenRouter key
  passes credential resolution and then sends that key to Anthropic, which rejects it and marks the
  credential invalid. Set `ANTHROPIC_API_KEY` if you want refinement to work today.
- **With no usable model credential, a refine job defers rather than failing.** The request stays in
  `Refining` indefinitely and the requester is told nothing, because deferral does not consume an
  attempt and nothing writes a turn explaining the wait.
- **A check runs with no timeout of its own.** A check command that hangs hangs until the session is
  cancelled. Keep the checks a repository declares to things that finish.

## Related

- [getting-started.md](getting-started.md) — standing an instance up and walking this path
- [api.md](api.md) — the endpoints behind each step
- [security.md](security.md) — why refinement is a boundary and not a filter
- [runners.md](runners.md) — what executes a session
- [charter-folder.md](charter-folder.md) — the scope configuration refinement reads
- [spec §6, §10, §10b, §11, §17 and §18](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the state machine, refinement, the status thread, staleness and preview binding
