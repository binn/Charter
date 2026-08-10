---
description: Create an EF Core migration and classify it per spec §15
argument-hint: "<MigrationName in PascalCase>"
allowed-tools: Bash(dotnet ef migrations:*), Bash(dotnet build:*), Read, Write, Edit, Glob, Grep
---

Create an EF Core migration and classify it per `agent-docs/spec.md` §15.

Migration name: $ARGUMENTS

## Steps

1. Confirm the entity changes are already made and the solution builds:
   ```bash
   dotnet build Charter.sln
   ```

2. Create the migration:
   ```bash
   dotnet ef migrations add $ARGUMENTS --project src/Charter
   ```

3. **Read the generated `Up` method and classify it structurally**, by inspecting the EF Core
   migration operations. Do not classify by guessing from the migration name or the entity diff.

4. Report the classification and act on it.

## Classification (spec §15)

| Class | Operations | Required behaviour |
|---|---|---|
| **Additive** | `CreateTable`, nullable `AddColumn`, `CreateIndex`, `AddForeignKey` on an empty table | Flows normally. PR labelled `schema-change`. |
| **Ambiguous** | `RenameColumn`, `RenameTable`, `AlterColumn` changing type, `AddColumn` non-null **with** a default | Engineer review required; the PR is blocked until approved. |
| **Destructive** | `DropColumn`, `DropTable`, `Sql` containing `TRUNCATE`/`DELETE`, `AlterColumn` to non-null **without** a default | **Session halts.** Write the intent down; an engineer authors the migration by hand. Do not attempt a clever workaround. |

A single migration takes the classification of its most severe operation.

## Then

- Verify the migration is reversible: the `Down` method must actually undo `Up`, or say explicitly
  in the report that it cannot be.
- Check the generated SQL for anything that locks a large table:
  ```bash
  dotnet ef migrations script --idempotent --project src/Charter
  ```
- Confirm the migration runs cleanly from an older schema — migrations must apply to a six-month-old
  instance (spec §24).
- Note that `.charter/policies/migrations.yml` makes these rules configurable per target repo, and
  CODEOWNERS on the migrations directory makes engineer approval structurally required regardless.

## Output format

```
## Migration: <Name>
Classification: additive | ambiguous | destructive

Operations:
- <operation> on <table>.<column> — <why it lands in that class>

Reversible: yes | no — <reason>
Lock risk: <none | table rewrite on X | index build on Y>
Required next step: <label the PR | request engineer review | halt and hand to an engineer>
```
