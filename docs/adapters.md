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

| Adapter | Output | Reaches OpenRouter | Notes |
|---|---|---|---|
| `claude-code` | `jsonl` | no | Subscription OAuth or API key. `ANTHROPIC_BASE_URL` points it at a gateway, but that gateway has to present the Anthropic API — it is not a route to an aggregator's own API. Reports cost and resumes. |
| `codex` | `jsonl` | no | OpenAI-compatible endpoints. Reports token counts but not a dollar figure, so cost is estimated. |
| `gemini-cli` | `text` | no | Its JSON output mode prints one aggregate object when the run finishes, which is not a stream Charter can classify while the agent works. |
| `opencode` | `text` | yes | Multi-provider, and its `--model` takes Charter's `provider/model` form. Prints a human transcript rather than an event stream. |
| `pi` | `jsonl` | **yes** | A minimal core over 20-plus providers, with subscription login. The widest model coverage from a single adapter, and the one adapter that makes an OpenRouter model usable for builds rather than only for refinement. |
| `cursor-agent` | `jsonl` | no | Authenticates against a Cursor account rather than a model provider, so it needs a `cursor_api_key` credential. That kind buys agent runs only — the control plane never resolves a refinement or recap call onto it. |
| `aider` | `text` | yes | Resolves models through LiteLLM, so it reads the standard provider keys. No machine-readable output mode. |

The "reaches OpenRouter" column is not decoration: it is the difference between a model you can build
with and a model you can only refine with. It is derived from each adapter's `auth` block, so it
cannot drift away from what Charter will actually inject.

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
  hint: "npm install -g --ignore-scripts @earendil-works/pi-coding-agent"
invoke:
  command: ["pi", "--mode", "json"]
  prompt: "{prompt}"
auth:
  anthropic_api_key:  { env: "ANTHROPIC_API_KEY" }
  openai_api_key:     { env: "OPENAI_API_KEY" }
  openrouter_key:     { env: "OPENROUTER_API_KEY" }
  google_api_key:     { env: "GEMINI_API_KEY" }
  xai_api_key:        { env: "XAI_API_KEY" }
model_arg: ["--provider", "{provider}", "--model", "{model}"]
model_format: bare
events:
  format: jsonl
  map:
    tool_use:   "$.type == 'tool_execution_start'"
    file_write: "$.type == 'tool_execution_start' && ($.toolName == 'edit' || $.toolName == 'write')"
    message:    "$.type == 'message_end' && $.message.role == 'assistant'"
capabilities: [resume, cost_reporting]
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
| `model_arg` | no | Arguments appended to select a model. `{model}` is substituted with the resolved model, rendered per `model_format`; `{provider}` with its provider segment, for a CLI that selects the provider with its own flag. Omit if the CLI takes its model from configuration only — but if you include it, one of the arguments must contain `{model}`, or the model you chose would never reach the CLI. |
| `model_format` | no | The form of identifier this CLI expects: `bare` (default), `qualified`, or `verbatim`. See below. |
| `events.format` | yes | `jsonl` or `text`. See below — this is the field that decides how good the experience is. |
| `events.map` | for `jsonl` | Predicates mapping the agent's output lines onto Charter's event types. Required when the format is `jsonl`, and rejected when it is `text` — a text stream has no structured lines to match. |
| `capabilities` | yes | What the adapter supports: `steering`, `resume`, `cost_reporting`. Anything absent is treated as unsupported. Declare an empty list rather than omitting the key. |

### `model_format`: the CLIs disagree about how to spell a model

Charter resolves one canonical identifier — `anthropic/claude-opus-5`,
`openrouter/deepseek/deepseek-r1` — and each adapter says how its CLI wants it written. The
disagreement is real and it fails silently: `claude --model anthropic/claude-opus-5` is not a model
Claude Code knows, and `opencode run --model claude-opus-5` is not a model OpenCode knows.

| `model_format` | `anthropic/claude-opus-5` becomes | `openrouter/deepseek/deepseek-r1` becomes | Used by |
|---|---|---|---|
| `bare` (default) | `claude-opus-5` | `deepseek/deepseek-r1` | `claude-code`, `codex`, `gemini-cli`, `cursor-agent`, `pi` |
| `qualified` | `anthropic/claude-opus-5` | `openrouter/deepseek/deepseek-r1` | `opencode` |
| `verbatim` | unchanged | unchanged | `aider` |

`bare` strips only Charter's provider prefix. An OpenRouter model id contains its own vendor segment
and keeps it — losing that would leave a name no provider can route. Because an unqualified name is
already Anthropic's, `bare` is idempotent: a caller may pass either form and the dispatched command is
the same. That also makes `bare` the safe default, and an adapter file written before this key existed
keeps producing exactly the command it did before.

`verbatim` is for a CLI whose model names follow a scheme Charter cannot derive. Aider resolves through
LiteLLM, which wants `claude-sonnet-4-5` for Anthropic but `openrouter/anthropic/claude-sonnet-4.5` for
OpenRouter — neither Charter form is right for every provider, so the operator writes the exact string.
`{provider}` cannot be used with `verbatim`: naming the provider means interpreting the identifier, and
`verbatim` is the instruction not to.

Where a CLI takes the provider separately, use both placeholders. Pi does, and it removes any question
of how it would split a model id that contains a slash of its own:

```yaml
model_arg: ["--provider", "{provider}", "--model", "{model}"]
model_format: bare
# openrouter/deepseek/deepseek-r1 dispatches as:
#   pi --mode json --provider openrouter --model deepseek/deepseek-r1
```

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

The `steering` check is necessary, not sufficient — stdin being open is not the same as the CLI
accepting further instructions on it. Neither shipped Phase 1 adapter claims steering today, and both
refusals are for that reason: Claude Code needs `--input-format stream-json`, which also turns the
prompt on stdin into a JSON envelope rather than the text the shim writes, and pi steers through
`--mode rpc`, a command protocol rather than an event stream. Both are additive changes to the shim,
not to the schema. Until then, declaring the capability would only mean a steering box in the UI that
does nothing.

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

#### The consequence of having no wildcard, and why it is survivable

There is no `[*]`, so a predicate can only ask about a content block at a fixed index. The shipped
`claude-code` mapping matches `$.message.content[0]`, and that is correct rather than merely
convenient: Claude Code emits a *separate* `{"type":"assistant"}` line per content block, each with a
single-element `content` array. A turn that produced text, a tool call, thinking, and a second tool
call arrives as four lines, and each classifies on its own.

Check this before you write `content[0]` for a new agent. If a CLI batches several blocks into one
line, index `0` silently classifies the first and drops the rest — the worst kind of mapping bug,
because the stream looks like it is working. The honest options are to map an event the CLI emits once
per block, or to declare `events.format: text` and take the documented degradation.

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
   pi --mode json "list the files in this directory"
   ```

   Read the actual lines it emits. Build `events.map` from those, not from its documentation — and
   not from an example in this file. Every shipped adapter has been checked against its CLI's own
   published reference, and the first version of `pi.yml` was wrong in four places precisely because
   it was written from a plausible-looking sketch.

3. **Check the install detection.** `install.check` must exit zero when the CLI is present and
   non-zero when it is not. `pi --version` is right; `which pi` is usually right; anything that prints
   a usage message and exits zero is wrong.

4. **Verify it is headless.** Run it with no TTY attached and confirm it completes without prompting:

   ```bash
   pi --mode json "add a comment to README.md" < /dev/null
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
  Azure OpenAI, Ollama, OpenRouter, or any OpenAI-compatible endpoint. This is why
  `CHARTER_MODEL_REFINE` and `CHARTER_MODEL_TEACH` default to an OpenRouter identifier.
- **Agent runs** — the actual build — are limited to what the agent CLI itself supports. This is why
  `CHARTER_MODEL_BUILD` does *not* default to one.

Concretely, for the two adapters a Phase 1 instance runs on:

| | `claude-code` | `pi` |
|---|---|---|
| `anthropic/claude-opus-5` | offered | offered |
| `openrouter/deepseek/deepseek-r1` | **not offered** | offered |

Claude Code authenticates against the Anthropic API. `ANTHROPIC_BASE_URL` moves that to a gateway, but
the gateway must present the Anthropic API — pointing it at an aggregator needs a translating proxy,
and Charter does not ship one, so it will not pretend the pairing works. Pi is provider-agnostic and
reads `OPENROUTER_API_KEY` directly, which is what makes Kimi, DeepSeek, or GLM usable for builds and
not only for refinement.

Charter tells you this at the point you choose, and names the alternative:

> the 'claude-code' adapter cannot authenticate against the 'openrouter' provider, so it cannot build
> with 'openrouter/deepseek/deepseek-r1'. 'pi' can.

So "OpenRouter means any model" is true for refinement and teaching, and only partly true for builds.
Builds are also where the money goes — refinement is cheaper per call but higher volume — so the
configuration that actually moves the bill is a cheap build model through `pi` with a strong model
kept for recap:

```yaml
# .charter/config.yml — with the pi adapter selected for this repository
models:
  refine: "openrouter/anthropic/claude-sonnet-5"
  build:  "openrouter/deepseek/deepseek-r1"
```

Or, staying on Claude Code for the build and taking the saving on the higher-volume surface:

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
