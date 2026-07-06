# /memory-refresh

Curate recent memory-system inputs into durable rules, memories, uncertainties, and manifest entries.

## Workflow

1. Review recent reports.
2. Review `.agent-memory/notes/pending.md`.
3. Extract durable instructions into rules.
4. Extract durable resolved knowledge into memories.
5. Convert unresolved or risky questions into uncertainties.
6. Archive stale, duplicate, or superseded entries.
7. Update `.agent-memory/manifest.md` manually or with `node .agent-memory/tools/refresh-manifest.js`.
8. Create a refresh report if meaningful context changed.

## Lesson Handling

- Prescriptive lessons become rules.
- Descriptive lessons become memories.
- Unclear or risky lessons become uncertainties.
- Temporary task notes stay out of active memory.
