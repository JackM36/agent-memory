# Project Memory Protocol

## Session Start Protocol

Read `.agent-memory/manifest.md` before work that is behavior-changing, unfamiliar, file/path-driven, historical, or decision-sensitive.

1. Read `.agent-memory/manifest.md`.
2. Read always-read rules listed in the manifest.
3. Identify relevant rules and memories by user request terms, affected files, tags, systems mentioned, and likely code areas.
4. Read only selected rules and memories.
5. Check selected uncertainties.
6. If a blocking uncertainty affects the task, ask the user before modifying that behavior.
7. Continue only after enough context is loaded.

Memory lookup may be skipped for purely mechanical edits.

- Still update rules or memory if the change creates durable guidance or knowledge.

## Memory Visibility Protocol

Mention memory access and memory updates briefly as user-visible progress.

Announce when:

- Reading `.agent-memory/manifest.md` for routed work.
- Using `memory-scout` or performing the memory scout workflow inline.
- Reading task-relevant rules, memories, or uncertainties.
- Reading reports for deeper history.
- Creating or updating rules, memories, uncertainties, reports, pending notes, or `.agent-memory/manifest.md`.

Keep announcements short and factual. Do not reveal private reasoning or hidden chain-of-thought.

## During Work

- Follow loaded rules unless they conflict with the user request or higher-priority project instructions.
- Rules in `.agent-memory/rules/` are project instructions. Memory-system operating procedure belongs in `protocol.md`.
- Prefer existing memories over assumptions.
- If rules and memories conflict, follow the rule for behavior and investigate the memory conflict.
- If code contradicts memory, mention the contradiction and investigate.
- If intent is unclear, create or update an uncertainty.
- Do not convert preferences or instructions into memories when they should be rules.
- Do not convert guesses into memories.
- Do not treat reports as default context.
- Curate durable context immediately when classification is clear.
- Use `.agent-memory/notes/pending.md` only when immediate curation is unsafe, unclear, or disruptive.

## End Of Work Protocol

After behavior-changing or decision-sensitive changes:

1. Prepare a compact maintenance handoff using `.agent-memory/templates/memory-maintenance-handoff.md`.
2. If a write-capable `memory-maintainer` subagent is available, delegate post-work memory maintenance to it.
3. If delegation is unavailable or excessive for the task, follow `.agent-memory/agents/memory-maintainer.md` inline.
4. Create or update a report in `.agent-memory/reports/`.
5. Extract durable behavioral instructions into rules.
6. Extract durable knowledge into memories.
7. Create uncertainties for unresolved questions.
8. Resolve uncertainties if the user answered them.
9. Update `.agent-memory/manifest.md`.
10. Review the maintenance result before the final user response.
11. Keep rules prescriptive, memories compact, and reports detailed.

The main agent remains accountable for implementation, validation, handoff quality, and final user-visible reporting. The memory maintainer handles only `.agent-memory/` curation.

## Lesson Handling

Do not create a separate lesson by default.

- Prescriptive lessons become rules.
- Descriptive lessons become memories.
- Unclear or risky lessons become uncertainties.
- Temporary lessons stay in reports only.

## Note Handling

Do not use notes as the default destination for durable context.

- Clear instructions become rules immediately.
- Clear resolved knowledge becomes memories immediately.
- Unclear or risky context becomes uncertainties immediately.
- Context that cannot be safely classified yet goes to `.agent-memory/notes/pending.md`.
- Pending notes are not authoritative until curated.

## Data Format Protocol

- Markdown files with YAML frontmatter are the source of truth.
- Generated JSON may be used for dashboards or indexes, but must be derived from Markdown.
- Do not treat generated JSON as authoritative project memory.

## Uncertainty Resolution Protocol

When the user answers an uncertainty:

1. Create or update a memory with the confirmed answer.
2. Move the uncertainty to `.agent-memory/archive/resolved-uncertainties/` or mark it archived.
3. Update `.agent-memory/manifest.md`.
4. Record the resolution in the task report.

## Token Efficiency Rules

- Do not read all memories.
- Do not read all rules.
- Do not read all reports.
- Use manifest routing first.
- Prefer relevant rules over broad context loading.
- Use memory code-navigation sections before broad code searches.
- Use `/memory-file-history <path>` style lookup when the task starts from a file path.
- Prefer memory summaries over report history.
- Read reports only when needed for deeper context.
- Keep new rules clear and prescriptive.
- Keep new memories focused and compact.
