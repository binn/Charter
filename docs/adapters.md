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

| Adapter | Output | Notes |
|---|---|---|
| `claude-code` | `jsonl` | Subscription OAuth or API key. Point it at a gateway with `ANTHROPIC_BASE_URL`. Reports cost, resumes, and steers. |
| `codex` | `jsonl` | OpenAI-compatible endpoints. Reports token counts but not a dollar figure, so cost is estimated. |
| `gemini-cli` | `text` | Its JSON output mode prints one aggregate object when the run finishes, which is not a stream Charter can classify while the agent works. |
| `opencode` | `text` | Multi-provider, and its `--model` already takes Charter's `provider/model` form. Prints a human transcript rather than an event stream. |
| `pi` | `jsonl` | A minimal four-tool core over 20-plus providers, with subscription login. The widest model coverage from a single adapter — a good default when you want provider flexibility. |
| `cursor-agent` | `jsonl` | Authenticates against a Cursor account rather than a model provider, so it needs a `cursor_api_key` credential. Charter does not store that kind yet, and offers no models for this adapter until it does. |
| `aider` | `text` | Resolves models through LiteLLM, so it reads the standard provider keys. No machine-readable output mode. |

Select one per repository, or per session where the repository permits a choice. The `text` adapters
still build and still open pull requests — see
[what they cost the requester](#text-format-adapters-degrade-and-here-is-exactly-how) before choosing
one.

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
| `invoke.prompt` | yes | How the spec reaches the agent. Either the literal `stdin`, or a template containing `{prompt}` that is appended to the argument vector — `"{prompt}"` for a positional argument, `"--message={prompt}"` for a flag. |
| `auth` | yes | Maps credential kinds Charter knows about to the environment variable this CLI reads. |
| `model_arg` | no | Arguments appended to select a model. `{model}` is substituted with the resolved model identifier. Omit if the CLI takes its model from configuration only — but if you include it, one of the arguments must contain `{model}`, or the model you chose would never reach the CLI. |
| `events.format` | yes | `jsonl` or `text`. See below — this is the field that decides how good the experience is. |
| `events.map` | for `jsonl` | Predicates mapping the agent's output lines onto Charter's event types. Required when the format is `jsonl`, and rejected when it is `text` — a text stream has no structured lines to match. |
| `capabilities` | yes | What the adapter supports: `steering`, `resume`, `cost_reporting`. Anything absent is treated as unsupported. Declare an empty list rather than omitting the key. |

### Versioning and unknown keys

Every Charter YAML file carries `version: 1`, and an adapter file is no exception. A missing version,
or a version this Charter does not support, fails at load with a message naming the version it does
support. This is the one field Charter refuses to guess at: a file written for a schema it does not
know is not safe to interpret under the rules it does know.

**Unknown keys warn and are ignored, never fail.** That applies to top-level keys, keys inside
`install`, `invoke`, `events` and each `auth` entry, credential kinds Charter does not recognise,
capabilities it does not recognise, and event types it does not classify. An adapter file written for
a newer Charter keeps working on an older one, minus the parts the older one cannot do. The warnings
are logged once at startup rather than swallowed, so you can see what was skipped.

Everything else is a hard failure at load, naming the file and the field: a missing required key, a
value of the wrong shape, an `events.map` predicate that does not parse, or a capability the declared
invocation cannot support. Charter reports every problem in the file at once rather than one per
restart.

### Capabilities have to be true

Two of the three are checked against the rest of the file, because section 12b is explicit that
pretending parity is worse than documenting a degraded experience:

| Capability | Requires | Why |
|---|---|---|
| `cost_reporting` | `events.format: jsonl` | There is no machine-readable line to read a cost from otherwise. Drop the capability and Charter estimates from token counts instead. |
| `steering` | `invoke.prompt: stdin` | An argument-delivered prompt leaves no open channel to send further instructions down. |
| `resume` | nothing | Charter takes your word for it. |

### Event mapping

Charter's three-pane view, milestone promotion, the diff viewer, and the engineer recap are all built
on a normalised event stream. `events.map` is how an agent's own output shape becomes that stream.

Each key is a Charter event type; each value is a predicate evaluated against one output line. The
minimum useful set is `tool_use`, `file_write`, and `message`. An adapter that maps `file_write`
accurately is one that can drive the code pane; one that does not, cannot.

A line may match more than one type, and that is intended: `{"type":"tool_call","tool":"edit"}` is
both a `tool_use` and a `file_write`. Charter records every type the line matched, most specific
first, so `file_write` wins where the two disagree.

#### What you can write in a predicate

The expression language is deliberately small — enough to classify a line, and no more. It is not
JSONPath, and it does not become JSONPath if you write more of it: anything outside this list is a
load-time error naming the file and the event type, not a predicate that quietly never matches.

| You can write | Example |
|---|---|
| A path from `$`, the whole parsed line | `$.type` |
| Nested keys, array indexes, quoted keys | `$.message.content[0].name`, `$['msg']['type']` |
| `==` and `!=` against a string, number, `true`, `false` or `null` | `$.type == 'tool_call'`, `$.exit_code != 0` |
| `&&`, `\|\|`, `!`, and parentheses | `$.type == 'a' && ($.tool == 'edit' \|\| $.tool == 'write')` |
| A bare path, as a truthiness test | `$.tool` is true when the key is present and not `null`, `false`, `0`, or `""` |

Strings take single or double quotes, with `\\`, `\'`, `\"`, `\n`, `\r` and `\t` escapes.

Three behaviours are worth knowing before you debug a mapping that matches nothing:

- **A missing key and a JSON `null` are the same thing.** Both equal `null`, and both are falsey. So
  `$.parent == null` matches a line that has no `parent` key at all.
- **Comparison is strict about type.** `$.n == 1` does not match `{"n":"1"}`. If the agent quotes its
  numbers, quote them in the predicate too.
- **Objects and arrays are never equal to anything.** Compare something inside them instead.

Not supported, and rejected at load: `>`, `<`, `>=`, `<=`, `=~`, `=`, recursive descent (`..`),
wildcards (`[*]`), filter expressions (`[?(...)]`), and functions. Bare identifiers are not paths —
write `$.type`, not `type`.

Predicates are parsed once when the adapter loads. After that, evaluation cannot fail: a line that is
blank, unmatched, or not valid JSON at all is classified as such rather than ending the session.

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

5. **Make it available to your instance.** See [where adapters are loaded from](#where-adapters-are-loaded-from)
   below. Adding a new agent means dropping the file into `adapters/`; changing a shipped one means a
   local directory on `CHARTER_ADAPTERS_PATH`. Either way it is picked up on startup.

6. **Confirm the CLI is installed on the runner.** The adapter describes how to invoke an agent; it
   does not install one. Prebuilt Charter runner images carry the common CLIs. A Charter Agent in
   native mode uses whatever is installed on that host.

7. **Test on a real repository** before pointing requesters at it. The smoke test in repo onboarding
   exercises the whole loop and is the fastest way to find out whether the event mapping works.

Contributing an adapter upstream is a pull request adding one file. That is the intended path — the
coding-agent landscape changes monthly and Charter should not need a release to keep up.

## Where adapters are loaded from

Charter loads adapter files from a list of directories, in order, at startup:

1. **`adapters/`**, the directory that ships with Charter. Found by looking beside the application and
   in its parent directories, so it works from a source checkout and from a published build.
2. **Every directory in `CHARTER_ADAPTERS_PATH`**, separated by `:`, in the order you list them.

**Later wins, by `id`.** A file on `CHARTER_ADAPTERS_PATH` whose `id` matches a shipped adapter
replaces it entirely — not field by field — and Charter logs which file replaced which at startup. A
file with a new `id` adds an adapter. That is the mechanism for changing a shipped adapter without
forking: copy it, edit it, and mount your copy.

```bash
CHARTER_ADAPTERS_PATH=/etc/charter/adapters
```

Two rules that exist to stop silent surprises:

- **Two files in the same directory claiming one `id` is an error** naming both files. Within one
  directory there is no defensible winner, and picking one quietly is how you end up debugging an
  adapter that is not the one you edited. Put the override in a `CHARTER_ADAPTERS_PATH` directory
  instead.
- **A directory on `CHARTER_ADAPTERS_PATH` that does not exist is an error**, not a silent skip. A
  mistyped mount path should tell you, rather than leaving your override doing nothing.

If no adapter directory resolves at all, Charter refuses to start: an instance with no adapters cannot
dispatch a session, and finding that out at boot beats finding it out with a requester waiting.

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
