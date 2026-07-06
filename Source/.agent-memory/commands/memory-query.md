# /memory-query <topic>

Find the smallest relevant memory context for a topic.

## Workflow

1. Read `.agent-memory/manifest.md`.
2. Start with compact manifest entries.
3. List candidate rules, memories, uncertainties, and reports with one-line reasons.
4. Read only selected rules and memories needed to answer the query.
5. Read reports only if the user asks for deeper history or selected memories are insufficient.
6. Respect token budgets in `.agent-memory/config.md`.

## Output

- Relevant rules.
- Relevant memories.
- Relevant uncertainties, with blocking status.
- Optional reports and why they might be read.
- Code entry points from selected memory `Code Navigation` sections.
