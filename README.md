<img src="assets/charter-mark.svg" alt="" width="56" height="56">

# Charter

**Charter your projects.**

Charter turns "hey, can the quote tool remember the last thing I picked?" into a pull request, a live preview link, and a plain-English explanation of what changed — without the person who asked ever opening GitHub.

It's a self-hosted web app. Your code stays in your repos, your data stays in your Postgres, and Charter never phones home.

> ⚠️ **Early.** Charter is pre-1.0 and under active development. Expect breaking changes.

<!-- TODO: 40-second GIF of the requester flow -->

---

## The problem

Every small company has the same bottleneck.

Someone in ops knows exactly what's wrong with the internal tool they use forty times a day. They mention it in Slack. It gets a 👍. Three weeks later nobody has done anything, because the two engineers are shipping the thing that actually makes money, and the request was never written down anywhere they'd see it.

The requests aren't hard. A column in the wrong order. A field that should default to last week's value. Copy that says "Submit" when it should say "Send to installer." Individually trivial, collectively the difference between software people tolerate and software people like.

AI coding agents can do this work. But handing an agent to a non-engineer means one of two bad outcomes: nothing happens because they can't run a terminal, or something terrible happens because they can.

Charter is the layer in between.

---

## How it works

```
  Someone describes                Charter asks the                 An engineer
  what they want          →        questions a good PM      →       approves the
  in plain English                 would ask                        scoped spec
                                                                          ↓
  They click "Try it"     ←        A preview environment    ←       An agent builds
  and tell you if it's             spins up on your PaaS            it in a sandbox
  right                                                             and opens a PR
                                          ↓
                          Everyone learns something:
                    the requester gets a walkthrough of what
                    changed and why. The engineer gets a
                    risk-ranked recap instead of 5,000 lines.
```

Charter doesn't implement its own coding agent. It drives the ones you already use — Claude Code, Codex — and owns the parts they don't: turning vague asks into buildable specs, enforcing what the agent may touch, and making the result legible to whoever needs to read it.

---

## What makes it different

**Approval is optional, not architectural.** Two gates exist and only one moves. The spend gate — is this worth building — is fully configurable: nobody in a five-person team, everyone in a fifty-person one, or "trust Ayesha up to $2 in this directory." The merge gate never moves, because it isn't Charter's to move. So letting someone skip approval risks a wasted PR, never shipped code.

**It refuses to build vague things.** Refinement is a conversation, not a form. If the request is still ambiguous, no agent runs and no tokens burn. The requester approves a written spec with acceptance criteria before anything starts — so when a preview is wrong, the conversation is "the spec said X," not "the AI misunderstood."

**There is no merge button. There will never be a merge button.** Charter opens pull requests. Your branch protection and CODEOWNERS decide what ships. Merge authority lives entirely outside Charter's trust boundary, which means a bug in our authorization code cannot put code in your main branch.

**Guardrails live in your repo, not our database.** Path scopes, denied directories, and validation commands live in a committed `.charter/config.yml`. Widening what the agent can touch requires a pull request and a review — using machinery you already trust.

**Ask, plan, then build.** Chat mode answers questions about a project without touching anything — and a surprising number of requests die there, because the answer is "it already does that." Plan mode explores options and tradeoffs before a single token gets spent building. Only approved plans become sessions.

**Specs you can actually read.** An AI-written spec precise enough for an agent is too dense for the person who asked for it. Charter renders every spec twice from one structured source: plain-language outcome and acceptance criteria for the requester, full technical detail for the engineer. You approve what you can judge.

**House rules, enforced.** Declare your stack once — .NET 10, React + Vite, Postgres, S3, OpenRouter, Railway, whatever yours is — and Charter scaffolds new projects from your own templates, keeps the refiner from proposing anything off-policy, and audits existing repos for drift. Deviations are allowed, but they're written down and signed off.

**Not just web apps.** Charter generalises "preview environment" into whatever lets a human judge the change: a URL for web, a TestFlight build for iOS, a simulator capture for WPF or Unity, a connect string for a game server, a hardware-in-the-loop test report for firmware. Where a requester genuinely can't self-verify, Charter says so instead of pretending.

**It teaches.** Optionally, Charter spends a few extra tokens explaining what happened, grounded in the actual session — not generic tutorials. Calibrated from "explain everything" to "just the decisions," and it remembers what it has already taught you, so you graduate without ever changing a setting.

**Engineers get a recap, not a diff dump.** A risk-ranked file list, where the agent deviated from the spec, and what it couldn't verify — posted as a PR comment. It will never tell you the code looks good. That's your job.

**Bring your own everything.** Anthropic, OpenAI, Gemini, Grok, OpenRouter, or any OpenAI-compatible endpoint. Claude Code, Codex, Gemini CLI, opencode, Pi, Cursor, aider — agent support is declarative config, so a new one is a YAML file, not a release. Link a subscription, an API key, or OpenRouter — per person, not just per instance. Charter tries the requester's own plan first, falls back through any pooled team credentials, then to metered API keys, then OpenRouter. Hit a limit and it waits for the reset instead of failing. OpenRouter support means you're not locked to one model vendor.

**It collects nothing.** No usage analytics, no phone-home, no opt-out required. Observability data (Seq, OpenTelemetry) goes only where you point it. The one exception is a daily check against GitHub for a new release, so your instance doesn't quietly rot on a version with a known vulnerability — it sends nothing about you, and `CHARTER_UPDATE_CHECK=false` turns it off.

---

## Who it's for

Charter is built for the ten-to-fifty person company with one or two engineers and a lot of internal software.

- **Ops and sales teams** get their own tooling fixed in hours instead of quarters.
- **Engineers** stop being a ticket queue for one-line changes, and review with real context instead of archaeology.
- **Everyone else** gradually learns how the product they use is actually built.

It is *not* for letting non-engineers ship unreviewed code. It makes **asking** cheap and **reviewing** fast. Merging stays exactly as expensive as you want it to be.

---

## Quick start

### Docker Compose

```bash
git clone https://github.com/binn/Charter.git
cd Charter
cp .env.example .env     # fill in ANTHROPIC_API_KEY and your GitHub App details
docker compose up
```

Charter comes up on `http://localhost:8080` in **personal mode** — one user, all roles, approval gates auto-satisfied. Inviting a second person is the only thing that changes, and it needs no migration.

### PaaS (Railway, Render, Fly)

Charter's control plane needs exactly two things: an HTTP port and a Postgres URL.

Point your platform at this repo, attach a Postgres instance, and set the required environment variables. Agent execution runs in GitHub Actions by default, so Charter works on platforms that don't allow container spawning.

```bash
DATABASE_URL=postgres://...        # standard postgres:// URL, not an EF connection string
CHARTER_BASE_URL=https://charter.yourcompany.com
CHARTER_SECRET_KEY=...             # 32+ bytes
CHARTER_CREDENTIAL_KEY=...         # 32+ bytes, encrypts linked accounts
ANTHROPIC_API_KEY=...              # or OPENROUTER_API_KEY, or link accounts in-app
GITHUB_APP_ID=...
GITHUB_APP_PRIVATE_KEY=...
GITHUB_WEBHOOK_SECRET=...
```

Configuration is entirely flat environment variables — no `appsettings.json`, no `Section__Nested__Keys`. Charter validates everything at startup and, if something's wrong, tells you all of it at once and exits.

See [`docs/configuration.md`](docs/configuration.md) for the full list.

### Connecting your first repo

Onboarding is a wizard that ends in proof, not a config screen:

1. Install the GitHub App and pick a base branch
2. Charter runs a read-only recon pass and proposes a scope config
3. You confirm what the agent may touch — it opens a PR with `.charter/config.yml`
4. Charter files a canned trivial request and runs the whole loop end to end
5. The repo becomes visible to requesters **only after that smoke test passes**

---

## Architecture

```
┌──────────────────────────────────────────────┐
│  Control plane — charter-app                 │
│  ASP.NET Core 10 · React + Vite · SignalR    │
│  Needs: one HTTP port + Postgres             │
└─────────────────────┬────────────────────────┘
                      │  IAgentRunner
      ┌───────────────┼────────────────┐
      ▼               ▼                ▼
 GitHub Actions   Docker          Detached runner
 (default,        (VPS /          (your hardware,
  any PaaS)       Compose)         planned)
```

The control plane is deliberately boring: stateless, restart-safe, Postgres-backed job queue, no Redis, no second container to babysit. Every session is fully resumable from the database, because PaaS containers restart whenever they feel like it.

The React frontend is bundled into the same application — one container, one port. In development it runs through the ASP.NET Core SPA proxy against the Vite dev server, so hot reload works from a single `dotnet run`.

Execution is pluggable because platforms like Railway prohibit privileged containers and block Docker daemon access — so Charter can't assume it's allowed to spawn sandboxes locally.

Full detail in [`agent-docs/spec.md`](agent-docs/spec.md).

---

## Stack

.NET 10 · PostgreSQL · EF Core · SignalR · React · Vite · TypeScript · Tailwind · shadcn/ui · Monaco · Serilog · OpenTelemetry

Models via Anthropic or any OpenAI-compatible provider, including OpenRouter.

---

## Security

Charter runs an AI agent against your source code on behalf of people who can't read it. We take that seriously.

- The agent never sees raw user input — only a **model-authored, human-approved spec**. Refinement is a sanitization boundary.
- Runners get a short-TTL GitHub App token scoped to **one repo**, and cannot read the control plane's environment.
- Path scopes are enforced in the runner, not the UI.
- Transcript and code panes are gated on **repo read access**, not user preference.
- Destructive schema migrations halt the session and require a human to author them.
- Every agent action is attributable to a named person in the audit log.

Read the full threat model in [`SECURITY.md`](SECURITY.md). Report vulnerabilities privately — please don't open a public issue.

---

## Status and roadmap

| Phase | | |
|---|---|---|
| 1 | Refinement, specs, approvals | 🚧 In progress |
| 2 | Agent execution, live sessions | Planned |
| 3 | PRs, preview binding, engineer recap | Planned |
| 4 | Teaching, code pane, budgets, linked accounts | Planned |
| 4b | Org standards, project scaffolding | Planned |
| 5 | Slack/Discord inbound, SAML, detached runners | Planned |

---

## Contributing

Contributions are genuinely wanted. Charter is early and moving fast, so please **open an issue before starting anything large** — the architecture is still settling and I'd hate for you to waste a weekend.

First-time contributors will be asked to sign a CLA (handled automatically on your first PR). You keep your copyright.

Read [`CONTRIBUTING.md`](CONTRIBUTING.md) first.

---

## License

**AGPL-3.0-only.** No warranty, no guarantees, no support obligations.

In plain terms:

- **Self-host it for your own company, modify it however you like.** Nothing here gets in your way.
- **Run a modified Charter as a service for others?** You must publish your modifications.
- **The name isn't part of the license.** "Charter" and the logo aren't covered by the AGPL — forks need their own name. See [`TRADEMARK.md`](TRADEMARK.md).

Contributions require signing a [CLA](CLA.md) — you keep your copyright, and it keeps the project's licensing options open.

---

Built because the people who use internal software all day usually have better ideas about it than the roadmap does.
