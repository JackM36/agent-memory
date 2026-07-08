# /memory-maintain

Run post-work memory maintenance from a main-agent handoff.

## Arguments

Provide a completed handoff using `.agent-memory/templates/memory-maintenance-handoff.md`, or describe the completed work, changed files, validation, decisions, durable context candidates, unresolved questions, and items not to remember.

## Workflow

1. Prefer delegating to a write-capable `memory-maintainer` subagent when the host supports it.
2. If delegation is unavailable, follow `.agent-memory/agents/memory-maintainer.md` inline.
3. Curate durable instructions into rules and durable resolved knowledge into memories.
4. Create or resolve uncertainties as evidence allows.
5. Create or update a report only when rationale is not recoverable from git plus the memories.
6. Run `node .agent-memory/tools/refresh-manifest.js`.
7. Run `node .agent-memory/tools/validate-memory.js` when available.
8. Return the result using `.agent-memory/templates/memory-maintenance-result.md`.

## Constraints

- Edit only `.agent-memory/` files.
- Do not invent facts from incomplete handoffs.
- Use uncertainties or pending notes when evidence is weak.
- Keep memories compact and reports detailed.

Arguments: $ARGUMENTS
