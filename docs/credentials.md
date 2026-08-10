---
title: "Model credentials"
description: "Providers, subscriptions versus API keys, how a credential is chosen per session, failover on exhaustion, shared pools, storage and handling, and provider terms of service."
---

# Model credentials

Charter consumes models on two distinct surfaces, and they authenticate differently. Keeping them
straight is the thing that makes this page make sense.

| Surface | What it does | How it authenticates |
|---|---|---|
| **Control plane** | Refinement, chat, plan mode, teaching, walkthroughs, engineer recap, repo recon | Charter's own HTTP client, using a credential resolved per session |
| **Agent runs** | The actual build | Environment variables injected into the runner process and read by the agent CLI |

The control plane can talk to anything. Agent runs are limited to what the agent CLI supports — see
[adapters.md](adapters.md).

## Providers

Three client implementations cover every supported provider:

| Implementation | Covers |
|---|---|
| `AnthropicModelClient` | Anthropic, through the official `Anthropic` NuGet package |
| `OpenAiCompatibleModelClient` | OpenAI, OpenRouter, xAI/Grok, DeepSeek, Groq, Azure OpenAI, Ollama, and anything else exposing `/chat/completions` |
| `GeminiModelClient` | Google Gemini's native API |

Most providers are OpenAI-compatible; only Anthropic and Gemini need bespoke clients. Gemini does
expose a compatibility endpoint, but Charter prefers the native client for features the shim does not
cover.

Model identifiers are provider-qualified strings:

```bash
CHARTER_MODEL_REFINE=anthropic/claude-sonnet-5
CHARTER_MODEL_BUILD=anthropic/claude-opus-5
CHARTER_MODEL_TEACH=openrouter/deepseek/deepseek-r1
```

A self-hosted or proxied endpoint is configured per credential with a base URL, so an internal gateway
or an Ollama instance works the same way as a hosted provider.

## Two ways to supply a credential

**Instance-level environment variables** are the fallback for the whole instance:

```bash
ANTHROPIC_API_KEY=sk-ant-api03-EXAMPLEKEYNOTREAL
OPENROUTER_API_KEY=sk-or-v1-EXAMPLEKEYNOTREAL
```

At least one model credential must be resolvable at startup — either one of these or a credential
already linked in the database. Startup fails if neither exists.

**Linked accounts** are per person. A user links their own subscription or API key in Settings, and
their requests run on it. This is what makes Charter usable in a team where people have their own
plans, and it is what the resolution chain below is for.

## How a credential is chosen

Resolved per session, in this order, skipping anything marked `exhausted` or `invalid`:

1. **The requester's own linked subscription credential.**
2. **Remaining overflow usage on that credential**, where the provider exposes a paid overflow tier
   beyond the subscription allowance. Overflow is tracked separately from the subscription itself:
   the monthly allowance running out does not spend the overflow, and exhausting the overflow does
   not change when the subscription resets.
3. **The organisation shared pool**, in `priority` order — subscription credentials whose owners have
   explicitly opted them in.
4. **The organisation's metered API key.**
5. **OpenRouter.**

If everything is exhausted, the session does **not** fail. It moves to `Queued`, and the requester sees
*waiting for capacity* with the earliest reset time. It starts on its own when capacity returns.

## Exhaustion and failover

- A `429` marks the grant `exhausted` and records a reset time from the provider's response header.
  Charter does not blind-retry into a rate limit.
- **A `429` with no reset header records no reset time at all.** The credential stays exhausted until
  something clears it — a token refresh, a re-link, or an operator — rather than being given an
  invented far-future date. *Waiting for capacity* shows a time only when a provider gave one.
- **Charter never fails over mid-session.** A session that swaps models halfway produces incoherent
  work, because half the reasoning came from a different model with different conventions.
- On mid-session exhaustion, Charter checkpoints and does one of two things, configurable per
  repository:

  | Behaviour | Effect |
  |---|---|
  | `pause_and_resume` (default) | The session waits for the credential's reset and continues where it stopped |
  | `restart_step` | The current step is restarted under the next credential in the chain |

- Failover **between** sessions is free, silent, and needs no configuration.

## Storage and handling

Credentials are encrypted at rest with `CHARTER_CREDENTIAL_KEY`, which is deliberately separate from
`CHARTER_SECRET_KEY`. Rotating your cookie signing key must not invalidate every stored credential in
the database.

**Keep `CHARTER_CREDENTIAL_KEY` with your backups.** Restore a database dump without it and every
stored credential is unrecoverable ciphertext.

The rules Charter follows:

- **Tokens are never logged**, at any log level, in any sink.
- **A secret is never returned to the UI after creation.** The credential list shows provider, owner,
  status, and last used. There is no reveal button, and there is no API that returns the value.
- **The control plane owns OAuth refresh.** Runners receive a short-TTL access token for the job they
  are running and never a refresh token.
- **Revocation is immediate** and kills in-flight sessions using that grant.
- Each credential carries a status of `active`, `exhausted`, `invalid`, or `revoked`, and Charter acts
  on it rather than retrying blindly.
- **An `invalid` credential records why**, in short plain language — *"provider rejected the
  credential with 401"* — so an admin can see what needs fixing without reading container logs. The
  reason never contains any part of the credential itself.

For what the runner does and does not receive, see [security.md](security.md).

## Shared pools

By default, a linked credential is personal: it runs that person's requests and nobody else's.

A shared pool lets one person's credential pay for another person's request. Enable the capability at
the instance level first:

```bash
CHARTER_ALLOW_SHARED_POOL=true
```

Then each owner opts their own credential in individually. That is an explicit action per credential,
never a default and never something an admin can do on someone's behalf.

Consent mechanics, because this spends someone else's quota:

- **The owner sees who used it, for what, and how much.** Attribution is per session.
- **`max_sessions_per_day_from_others`** caps exposure so one busy colleague cannot drain a plan.
- **One-click withdrawal.** Removing a credential from the pool takes effect immediately.

### Subscription sessions are not free

A subscription-backed session has no marginal dollar cost, but it consumes scarce quota belonging to a
named person. Charter tracks both units and never reports the second as zero dollars:

| Unit | Source | Meaning |
|---|---|---|
| `usd` | Metered API keys, OpenRouter | Real marginal cost |
| `quota_sessions` | Subscription credentials | No cash cost, but a limited shared resource charged to the owner |

Reporting also shows an **imputed USD** figure for subscription sessions — what the same work would
have cost on metered API — so the two are comparable and the value of the subscription is visible.
Budgets can be denominated in either unit ([spec §34](https://github.com/binn/Charter/blob/master/agent-docs/spec.md)).

## Terms of service: check your provider's

**Read this before enabling `CHARTER_ALLOW_SHARED_POOL`.**

There is a meaningful difference between two things Charter can do:

- **A person uses their own subscription for their own agent runs.** This is ordinary use of a coding
  agent CLI with a personal plan.
- **A person's subscription pays for other people's requests**, through a shared pool. Functionally,
  other people are consuming an account licensed to one individual.

Consumer and individual subscription plans commonly include terms about account sharing and about who
may use the service under a given seat. Whether the second pattern is permitted is determined by your
provider's terms, your plan, and your jurisdiction — not by Charter.

**Check your provider's terms of service before enabling shared pools, and check them again for each
provider you pool.** If you are deploying Charter for an organisation, this is a question for whoever
owns your vendor contracts.

Charter's position is to make the distinction visible and leave the decision with you:

- `CHARTER_ALLOW_SHARED_POOL` defaults to `false`.
- Opting a credential into a pool shows a one-time notice pointing at this question.
- Metered API keys and OpenRouter keys are billed per token and are the straightforward option where
  you would rather not have to reason about seat terms at all.

Charter does not tell you what to conclude, and it will not make this call silently on your behalf.

## OpenRouter specifics

- Charter fetches the model catalog and per-token pricing from OpenRouter's models endpoint and caches
  it. The budget estimator cannot work from a hardcoded price table when the model is user-selectable.
- Per-repository and per-task model overrides let you use a cheap model for refinement and a strong one
  for builds ([charter-folder.md](charter-folder.md)).
- OpenRouter gives full model freedom for **control-plane** calls. For **agent runs**, your choice is
  bounded by the agent CLI. See [adapters.md](adapters.md) — this is the most common misunderstanding
  about what OpenRouter support buys you.

## Related

- [configuration.md](configuration.md) — `CHARTER_CREDENTIAL_KEY`, `CHARTER_ALLOW_SHARED_POOL`, `CHARTER_MODEL_*`
- [adapters.md](adapters.md) — which models your chosen agent can actually build with
- [security.md](security.md) — what reaches a runner and what does not
- [spec §20b](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full credentials specification
