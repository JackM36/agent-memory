# /memory-scout <task>

Run the memory scout workflow for a task.

Use `.agent-memory/agents/memory-scout.md` as the source-of-truth agent specification and `.agent-memory/templates/memory-scout-result.md` as the output shape.

The scout is read-only and returns a compact context plan. It should not modify files.
