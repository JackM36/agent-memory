# Rules

Active behavioral and project-specific instructions belong here.

Rules are prescriptive. They tell agents what to do, avoid, prefer, or ask before doing.

Memory-system operating procedure does not belong here. Keep instructions like "read the manifest first" and "do not read reports by default" in `.agent-memory/protocol.md`.

Use `.agent-memory/templates/rule.md` when creating a rule.

## Rules Vs Memories

- Rules are prescriptive: do this, avoid that, ask before changing this.
- Memories are descriptive: this system works this way, this decision was made, this bug happened before.

## What Belongs Here

- Project coding style.
- Testing policy.
- Architecture constraints.
- User preferences that affect future work.
- File or system-specific instructions.

## What Does Not Belong Here

- Memory-system protocol.
- Historical explanations without an active instruction.
- Unverified guesses.
- Temporary task notes.

## Loading Rules

Agents discover rules through `.agent-memory/manifest.md`.

Rules marked `always_read: true` should be loaded for routed work. Other rules should be loaded only when relevant by tag, file path, or summary.
