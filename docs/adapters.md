---
title: "Agent adapters"
description: "How Charter drives existing coding-agent CLIs through declarative YAML adapters: the schema, event mapping, how text-format adapters degrade, and how to add your own."
---

# Agent adapters

Charter does not implement a coding agent. It drives the ones that already exist — Claude Code, Codex,
Gemini CLI, and others — through **adapters**.

An adapter is a YAML file, not code. Adding support for a new agent is a configuration change, not a
Charter release, and you can drop one into your own instance without forking.

## Adapters shipped in-tree

| Adapter | Notes |
|---|---|
| `claude-code` | Subscription OAuth or API key. Point it at a gateway with `ANTHROPIC_BASE_URL`. |
| `codex` | OpenAI-compatible endpoints. |
| `gemini-cli` | |
| `opencode` | Multi-provider. |
| `pi` | A minimal four-tool core over 20-plus providers, with subscription login. The widest model coverage from a single adapter — a good default when you want provider flexibility. |
| `cursor-agent` | |
| `aider` | |

Select one per repository, or per session where the repository permits a choice.

## The schema

```yaml
# adapters/pi.yml
id: pi
display_name: "Pi"
version: 1
install:
  check: "pi --version"
  hint: "npx @earendil-works/pi-coding-agent"
invoke:
  command: ["pi", "--print", "--output-format", "jsonl"]
  prompt: stdin
auth:
  anthropic_api_key:  { env: "ANTHROPIC_API_KEY" }
  openai_api_key:     { env: "OPENAI_API_KEY" }
  openrouter_key:     { env: "OPENROUTER_API_KEY" }
  google_api_key:     { env: "GEMINI_API_KEY" }
  xai_api_key:        { env: "XAI_API_KEY" }
model_arg: ["--model", "{model}"]
events:
  format: jsonl
  map:
    tool_use:   "$.type == 'tool_call'"
    file_write: "$.tool == 'edit' || $.tool == 'write'"
    message:    "$.type == 'assistant'"
capabilities: [steering, resume, cost_reporting]
```

### Fields

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable identifier. Referenced from `.charter/config.yml` and the UI. Lowercase, hyphenated. |
| `display_name` | yes | What the UI shows. |
| `version` | yes | Adapter schema version. Always `1` today. |
| `install.check` | yes | Command run to detect the CLI. A zero exit means installed. |
| `install.hint` | yes | Shown to an engineer when the check fails. Tell them how to install it. |
| `invoke.command` | yes | Argument vector used to start the agent. Not a shell string — no shell is involved, so quoting and `&&` do not work. |
| `invoke.prompt` | yes | How the spec reaches the agent: `stdin`, or an argument placeholder. |
| `auth` | yes | Maps credential kinds Charter knows about to the environment variable this CLI reads. |
| `model_arg` | no | Arguments appended to select a model. `{model}` is substituted with the resolved model identifier. Omit if the CLI takes its model from configuration only. |
| `events.format` | yes | `jsonl` or `text`. See below — this is the field that decides how good the experience is. |
| `events.map` | for `jsonl` | JSONPath-style predicates mapping the agent's output lines onto Charter's event types. |
| `capabilities` | yes | What the adapter supports: `steering`, `resume`, `cost_reporting`. Anything absent is treated as unsupported. |

### Event mapping

Charter's three-pane view, milestone promotion, the diff viewer, and the engineer recap are all built
on a normalised event stream. `events.map` is how an agent's own output shape becomes that stream.

Each key is a Charter event type; each value is a predicate evaluated against one output line. The
minimum useful set is `tool_use`, `file_write`, and `message`. An adapter that maps `file_write`
accurately is one that can drive the code pane; one that does not, cannot.

## Requirements on an adapter

**Streaming, machine-readable output.** Charter reads the agent's output line by line while it runs.

**Non-interactive, headless mode.** Anything that requires a TTY prompt cannot be dispatched. If the
CLI stops to ask a question with no way to answer non-interactively, the session hangs until the
wall-clock cap kills it.

**Cost reporting where available.** Declare `cost_reporting` in `capabilities` when the CLI reports
what a run cost. Without it, Charter estimates from token counts and the provider price table, and the
figure in your budget dashboard is an estimate rather than a measurement.

### Text-format adapters degrade, and here is exactly how

An agent that can only emit human-formatted text gets `events.format: text`. It still runs, and the
resulting pull request is no worse. What you lose:

| Feature | With `jsonl` | With `text` |
|---|---|---|
| Pane 2 (event stream) | Structured, filterable, virtualized event list | A raw log |
| Milestone promotion into the requester's thread | Works — the requester sees plain-English progress | Does not work. The status thread shows the session is running, and little else. |
| Pane 3 (diff viewer) linked from events | Click a file-write event, land on that hunk | No file-write events to click. The diff is still viewable from the pull request. |
| Engineer recap quality | Grounded in structured tool calls and file writes | Degraded — the recap is built from unstructured log text |
| Cost accuracy | Reported by the CLI where supported | Estimated |

This is a real gap for the requester, who is the person Charter exists for. Prefer a `jsonl` adapter
where you have the choice, and use `text` adapters knowing what the requester will not see.

## Adding an adapter

1. **Write the YAML.** Put it in `adapters/` next to the in-tree ones.

2. **Run the agent by hand first** and capture its output:

   ```bash
   echo "list the files in this directory" | pi --print --output-format jsonl
   ```

   Read the actual lines it emits. Build `events.map` from those, not from its documentation.

3. **Check the install detection.** `install.check` must exit zero when the CLI is present and
   non-zero when it is not. `pi --version` is right; `which pi` is usually right; anything that prints
   a usage message and exits zero is wrong.

4. **Verify it is headless.** Run it with no TTY attached and confirm it completes without prompting:

   ```bash
   echo "add a comment to README.md" | pi --print --output-format jsonl < /dev/null
   ```

5. **Make it available to your instance.** A local adapter file is loaded from the same `adapters/`
   directory as the shipped ones, so mount or bake it in alongside them. It is picked up on startup.

6. **Confirm the CLI is installed on the runner.** The adapter describes how to invoke an agent; it
   does not install one. Prebuilt Charter runner images carry the common CLIs. A Charter Agent in
   native mode uses whatever is installed on that host.

7. **Test on a real repository** before pointing requesters at it. The smoke test in repo onboarding
   exercises the whole loop and is the fastest way to find out whether the event mapping works.

Contributing an adapter upstream is a pull request adding one file. That is the intended path — the
coding-agent landscape changes monthly and Charter should not need a release to keep up.

## Model and adapter compatibility

**Not every model works with every agent.** A credential for a provider does not imply that your chosen
agent CLI can use it.

Charter resolves the intersection of three things and shows only combinations that exist:

```
(credentials available to this user) x (providers this adapter supports) x (repo policy)
```

Anything outside that intersection is not offered. Silently accepting an impossible pairing and failing
at dispatch is the worst outcome, so the UI does not let you assemble one.

The constraint worth understanding before you plan around OpenRouter:

- **Control-plane calls** — refinement, chat, plan mode, teaching, recap, recon — go through Charter's
  own model client. Here you have full model freedom: Anthropic, OpenAI, Gemini, xAI, DeepSeek, Groq,
  Azure OpenAI, Ollama, OpenRouter, or any OpenAI-compatible endpoint.
- **Agent runs** — the actual build — are limited to what the agent CLI itself supports. Claude Code
  can be pointed at a compatible gateway through `ANTHROPIC_BASE_URL`. Codex accepts OpenAI-compatible
  endpoints. Beyond that, a model needs a shim, and Charter does not ship one.

So "OpenRouter means any model" is true for refinement and teaching, and only partly true for builds.
Choose the adapter for the models you want to build with, and use a cheap model for refinement
independently:

```yaml
models:
  refine: "openrouter/deepseek/deepseek-r1"
  build:  "anthropic/claude-opus-5"
```

## Related

- [credentials.md](credentials.md) — what fills the `auth` block at run time
- [runners.md](runners.md) — where the agent CLI is actually installed
- [charter-folder.md](charter-folder.md) — selecting an adapter and model per repository
- [spec §12b and §20b.6](https://github.com/binn/Charter/blob/master/agent-docs/spec.md) — the full adapter specification
