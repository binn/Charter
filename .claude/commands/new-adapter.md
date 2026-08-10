---
description: Scaffold an agent adapter YAML per spec §12b
argument-hint: "<adapter-id> [CLI name or install hint]"
allowed-tools: Read, Write, Edit, Glob, Grep, Bash(ls:*)
---

Scaffold a new agent adapter in `adapters/` per `agent-docs/spec.md` §12b.

Adapter: $ARGUMENTS

## Rules

- **Adapters are data, not code.** Supporting a new agent must be a configuration PR, never a code
  change. If you find yourself needing C# to support the agent, stop and report why — that is a gap
  in the adapter schema and should be discussed before it is worked around.
- `version: 1` at the top. Unknown keys warn, never fail.
- File is `adapters/<id>.yml`, lowercase and hyphenated, matching the `id` field.

## Steps

1. Read §12b of `agent-docs/spec.md`.
2. Read an existing adapter in `adapters/` for the current conventions before writing a new one.
3. Determine, and ask the user if you cannot establish it from the CLI's documentation:
   - The non-interactive/headless invocation. Anything requiring a TTY prompt cannot be dispatched.
   - How the prompt is passed (stdin or an argument).
   - The streaming machine-readable output format and how to map its records onto Charter events.
   - Which environment variables carry which provider credentials.
   - Whether it reports cost, supports steering, and supports resume.
4. Write the file.
5. Report the model × adapter compatibility implications: which of the configured providers this
   adapter can actually reach, so the UI can resolve
   *(available credentials) × (adapter's supported providers) × (repo policy)*.

## Template

```yaml
# adapters/<id>.yml
id: <id>
display_name: "<Display Name>"
version: 1
install:
  check: "<id> --version"
  hint: "<install command>"
invoke:
  command: ["<id>", "--print", "--output-format", "jsonl"]
  prompt: stdin            # stdin | arg
auth:
  anthropic_api_key:  { env: "ANTHROPIC_API_KEY" }
  openai_api_key:     { env: "OPENAI_API_KEY" }
  openrouter_key:     { env: "OPENROUTER_API_KEY" }
model_arg: ["--model", "{model}"]
events:
  format: jsonl            # jsonl | text
  map:
    tool_use:   "$.type == 'tool_call'"
    file_write: "$.tool == 'edit' || $.tool == 'write'"
    message:    "$.type == 'assistant'"
capabilities: [steering, resume, cost_reporting]
```

## Degraded adapters

If the CLI can only emit human-formatted text, set `events.format: text` and state plainly in the
report that pane 2 degrades to a raw log and milestone promotion will not work for this adapter.
Do not pretend parity, and do not invent a parser for unstructured output.

## Finally

Add the adapter to the table in `docs/adapters.md`, including any limitation you found.
