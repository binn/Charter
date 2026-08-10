---
description: Report implementation progress against the spec §23 build order
argument-hint: "[phase number to focus on, e.g. 2]"
allowed-tools: Bash(git log:*), Bash(git status:*), Bash(dotnet sln list:*), Read, Grep, Glob
---

Report where the implementation currently stands against the build order in `agent-docs/spec.md` §23.

Focus: $ARGUMENTS (if empty, report on all five phases).

## Steps

1. Read §23 of `agent-docs/spec.md` in full for the authoritative phase definitions.

2. Survey what exists. Do not assume — check:
   - `dotnet sln list` and the contents of `src/`, `tests/`, and `adapters/`.
   - Entities present in the `DbContext` and the applied migrations, against the §5 data model.
   - HTTP endpoints and SignalR hubs that are actually registered.
   - React routes and panes under `src/Charter/ClientApp/src`.
   - Test coverage for each area — a feature with no tests is `in progress`, not `done`.
   - `git log --oneline -30` for recent direction.

3. Classify every deliverable in each phase as `done`, `in progress`, `not started`, or `blocked`.
   A deliverable is `done` only when it builds, has tests, and matches its spec section.

## Phase reference (spec §23)

| Phase | Scope |
|---|---|
| 1 | Refinement only, no agent: request → refinement → spec confirmation → approval queue |
| 2 | Execution: `IAgentRunner`, Charter Agent (§33) **and** `GitHubActionsRunner`, capability matching, session orchestration, event streaming, SignalR, pane 2 |
| 3 | The loop closes: PR creation, verification artifact binding, "what to check", feedback buttons, engineer recap |
| 4 | Legibility and control: teaching, concept ledger, pane 3, budgets, triage rules, `DockerRunner` |
| 5 | Reach: Slack/Discord inbound, SAML, remote Docker socket, demo mode polish |

## Output format

```
## Build order status

### Phase N — <name>  [not started | N% — X of Y done | complete]
- [x] <deliverable> — <evidence: path or test name>
- [~] <deliverable> — <what exists, what is missing>
- [ ] <deliverable>

### Current phase
Phase N. Remaining to complete it: <short list>.

### Next action
<the single highest-value next piece of work, and why>

### Ordering risks
<anything being built out of order, especially anything from a later phase that
would force a rewrite if the Phase 2 job-claim protocol or Postgres-only
resumability were deferred>
```

Be blunt about what is not done. An optimistic status report is worse than none.
