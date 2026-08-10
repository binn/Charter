---
description: Verify recent changes against the relevant agent-docs/spec.md section and report divergences
argument-hint: "[section number, file path, or git ref — defaults to the working tree diff]"
allowed-tools: Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git show:*), Read, Grep, Glob
---

Check recent changes against the specification. **Report only — do not fix anything.**

Target: $ARGUMENTS (if empty, use the uncommitted working tree changes; if that is also empty, use
the most recent commit).

## Steps

1. Establish the change set.
   - `git status --short`
   - `git diff` and `git diff --staged`
   - If both are empty, `git show --stat HEAD` and `git show HEAD`.
   - If the argument is a section number (e.g. `§15` or `15`), review the whole implementation of
     that section instead of a diff.

2. Identify which spec sections govern the change. Read them **in full** from
   `agent-docs/spec.md` — do not work from memory or from the section index in `AGENTS.md`.
   Use the lookup table at the end of `AGENTS.md` to map topic to section, then grep the spec for
   the exact heading.

3. Compare the implementation to the specification, checking specifically for:
   - Behaviour the spec mandates that the code does not implement.
   - Behaviour the code implements that the spec does not describe (scope creep is a divergence too).
   - Violations of the hard constraints in `AGENTS.md`: merge button, browser storage APIs,
     in-memory orchestration state, extra runtime services, client-side authorisation, runtime
     installs in a session.
   - Violations of the configuration rules (§4.1): any `appsettings.json`, any nested
     double-underscore config key, any lazily-validated configuration.
   - Personal-mode branching in authorisation code (§7.2).
   - ETAs shown anywhere in the UI (§6).

4. Report.

## Output format

```
## Spec check: <what was reviewed>
Sections consulted: §X, §Y

### Divergences
1. [severity] <one line> — spec §X says <quote or paraphrase>; the code at
   path/to/file.cs:LINE does <what it does>. Suggested direction: <one line>.

### Ambiguities
- <where the spec does not decide, and which reading the code took>

### Verdict
<Conforms / Conforms with minor divergences / Diverges materially>
```

Severity is `blocking` for hard-constraint violations and anything that would force a rewrite later,
`major` for missing mandated behaviour, `minor` for wording, naming, and ordering. If there are no
divergences, say so in one line — do not pad the report.
