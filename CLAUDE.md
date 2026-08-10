# Charter

See @AGENTS.md for project guidance, conventions, and constraints.

## Claude Code specifics

Slash commands in `.claude/commands/`:

- `/spec-check` — verify recent changes against the relevant `agent-docs/spec.md` section
- `/phase-status` — report progress against the spec §23 build order
- `/new-adapter` — scaffold an agent adapter YAML per spec §12b
- `/new-migration` — create an EF Core migration and classify it per spec §15

Subagents in `.claude/agents/`:

- `spec-reviewer` — checks implementation against `agent-docs/spec.md`
- `docs-writer` — writes and maintains the spec §29 documentation set

`.claude/settings.json` is a committed, conservative permission allowlist. Put personal overrides in
`.claude/settings.local.json`, which is gitignored — do not loosen the committed file.
