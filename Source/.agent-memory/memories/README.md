# Memories

Resolved durable project knowledge belongs here.

A memory is not a report. Keep it compact, reusable, and focused on facts, decisions, conventions, gotchas, and user-confirmed answers.

When a memory describes a code system, include a compact code navigation map. The goal is to help future agents inspect relevant code first instead of rediscovering the whole project.

Use `.agent-memory/templates/memory.md` when creating a memory.

## Quality Rules

- Prefer multiple focused memories over one giant memory.
- Avoid full implementation history.
- Avoid duplicating reports.
- Do not include uncertain information unless explicitly marked.
- If something is unresolved, create an uncertainty instead.
- Include evidence for important claims: source files, user confirmation, tests, or related reports.
- Include code entry points, key symbols, and useful searches when they reduce future codebase scanning.
- Do not turn memories into full code listings or broad implementation history.
- Target 10-40 lines. Split around 80 lines.

## Verification

`last_verified` and `verified_by` are optional frontmatter — add them when verification matters and
keep them current when updating. Allowed `verified_by` values: `code`, `user`, `tests`,
`repeated-use`, `report`, `unverified`.

Use `confidence: low` for unverified memories that are still worth preserving as leads. Create an uncertainty instead when acting on the information would be risky.
