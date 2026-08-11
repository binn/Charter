---
title: "Budgets"
description: "How Charter governs spend: two currencies, conjunctive nesting, reserve-then-settle accounting, the five behaviours at a limit, and what the defaults are in personal and organisation mode."
---

# Budgets

Charter meters what its agent runs cost and can hold that spend against caps you set. Nothing is
capped until you say so.

Budgets answer one question — *is this worth burning tokens and quota on?* They have nothing to do
with whether code is fit to ship. That gate lives in your provider's branch protection and
CODEOWNERS, outside Charter entirely, and no budget setting can move it. Because the merge gate
cannot move, loosening a budget is safe: the worst case is wasted tokens and a change request nobody
wanted.

## What you get without configuring anything

| Mode | Default |
|---|---|
| Personal | **No budgets.** One person, their own credentials, nothing to govern. |
| Organisation | One org-wide monthly budget set to **require approval** above a modest per-session threshold. |

The organisation default does not refuse work. Spend under the threshold runs; spend over it waits
for someone to approve it. Blocking is available and is rarely what you want as a default — a
requester who hits a wall stops filing requests, and the requests you stop getting are invisible.

Personal mode has no budget rows at all. There is no switch to find and nothing to turn off.

## Two currencies, never mixed

| Unit | Where it comes from | What it means |
|---|---|---|
| `usd` | Metered API keys and OpenRouter | Real marginal cost |
| `quota_sessions` | Subscription-backed credentials | No marginal cost, but a scarce shared resource |

A budget is denominated in one or the other. A session served by a subscription credential costs
**nothing** in dollars and still consumes a session of quota, so it passes a dollar cap untouched and
is governed by a quota cap.

That is not a rounding decision. Reporting a subscription session as `$0.00` makes a dashboard lie
about where your capacity is going, so every ledger row also carries an **imputed USD** figure: what
the same work would have cost on a metered API. Use it to compare the two and to see what your
subscription is actually worth. Never add it to a dollar budget — it is not money that was spent.

## Budgets nest, and all of them have to agree

A session needs headroom in **every** budget that applies to it, not the most specific one.

```
Org: $5,000/mo
 └─ Team "Ops Tooling": $1,500/mo
     └─ Repo "spectra": $800/mo
         └─ User "ayesha": $200/mo, reserved $50
```

Ayesha having $200 left does not help her if the team pool is exhausted. That is deliberate — a
per-person budget is a ceiling on her, not an entitlement carved out of everyone else's.

`reserved_amount` is the entitlement. It guarantees a floor: Ayesha's first $50 each period is hers
whatever the pools above her are doing. Above her reserve she competes for the shared pool like
everyone else. Give people a small reserve and a shared pool above it, rather than slicing the whole
budget into per-person shares that mostly go unused.

A budget can also target specific cost categories — `build`, `refine`, `teach`, `recap`, `recon`,
`scaffold`, `chat` — or all of them.

**Fund chat generously, or leave it uncapped.** It is by far the cheapest way to resolve a request,
and a chat that answers *it already does that* saves an entire build. Rationing it pushes people
straight to building, which costs more. If you cap one thing, do not let it be this.

## Reserve, then settle

Charter does not check a budget and hope. It holds the money.

1. **Estimate** the work before it is dispatched.
2. **Reserve** the estimate against every applicable budget, inside a database transaction that has
   locked those budget rows.
3. **Settle** when the work finishes, replacing the estimate with the actual and releasing the
   difference back to the budget immediately.
4. **Release** the whole hold if the session is cancelled or fails.

Without step 2, ten sessions started at the same moment each read the same headroom, each decide
there is room, and collectively spend ten times what the cap allows. The row locks are what makes the
tenth session see the first nine.

Reservations expire after **two hours** by default. If the control plane dies between reserving and
settling, that budget comes back on its own rather than staying held until somebody goes looking in
the database. Expired holds stop counting against headroom immediately; a background sweep then tidies
the rows.

### How good is the estimate?

Honestly: rough, and rough on purpose.

- With **three or more settled sessions** for the same repository and category, Charter uses the
  median of what those actually cost. The median, not the mean — one runaway session should not
  inflate every estimate for the rest of the month.
- Below that, it derives token counts from the specification's size and number of acceptance criteria
  and prices them from the model's published rates. Those starting figures are sized from the shape
  of each kind of pass, not measured, and a build is priced an order of magnitude above a chat.
- Both paths are scaled by the same scope factor, centred on an ordinary spec and clamped between
  0.5× and 3×, because spec length correlates with cost loosely enough that an unclamped multiplier
  produces confident nonsense.

The estimate is a hold, not a quote. What makes it safe is the settlement immediately afterwards. An
estimate that is 3× too high costs you a temporarily smaller cap for the length of one session; an
estimate that is 3× too low is corrected the moment the session ends. Every row keeps both the
estimate and the actual, so the accuracy is measurable rather than a matter of opinion.

**Known limitation: an unpriced model cannot be governed.** If no catalog knows what your model
costs — a self-hosted endpoint, or a gateway Charter cannot price — the estimate is zero and is
labelled *unpriced*. Sessions on that model pass every dollar budget. Any limit message you do see
says so outright rather than presenting the work as free. If you run unpriced models and need a cap,
use a `quota_sessions` budget, which counts sessions rather than dollars.

## What happens at a limit

| Behaviour | Effect |
|---|---|
| `warn` | Runs anyway; the budget's owner is notified. |
| `require_approval` | Goes to the approval queue instead of failing. |
| `downgrade_model` | Runs on a cheaper model and is labelled as having done so. |
| `queue_until_reset` | Held until the period rolls over, showing the date. |
| `block` | Refused, with the exact figure. |

When several budgets are over at once, the strictest behaviour among them wins.

`require_approval` is the best default for an organisation that spends freely. Work does not stop; it
acquires a human decision. `block` is the crudest option and is worth choosing deliberately — for a
hard external spending limit, not as a general setting.

**Every message a limit produces names who can raise it**, by name and email where Charter can
resolve one, and by role otherwise. A dead end that does not say who to ask is the fastest way to
make people stop using the tool: the requester cannot tell *wait five minutes* from *this will never
work*, so they assume the latter.

## Periods

`daily`, `weekly`, `monthly`, `quarterly`, `rolling_30d`, `fiscal_year`, or `one_off` for a campaign
with its own start and end.

All period arithmetic is UTC. A `period_anchor` sets the billing day, the week's first day, or the
fiscal year's start; without one, months start on the 1st, weeks on Monday, and years in January. An
anchor day past the 28th is clamped, so a budget anchored on the 31st still rolls over in February.

`rollover` carries unspent budget into the next period — `full`, or `capped` at an amount you set.
Carry is **one period only**, never a running total. A budget that accumulates every unspent month
forever is not a monthly budget.

## What budgets do not do

- **They do not stop a merge.** Nothing in Charter does.
- **They are not a permission system.** Repo scope decides who may file against what; budgets decide
  what it costs. A budget with headroom does not grant access to a repository.
- **They do not bypass auto-dispatch, and auto-dispatch does not bypass them.** A request that
  auto-dispatches still has to have budget. Auto-dispatch answers *does a human need to read this
  spec first*; the budget answers *is there money to run it*.
- **They do not throttle.** Rate limits are separate.
