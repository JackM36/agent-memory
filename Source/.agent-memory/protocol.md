# Project Memory Protocol

This file is the single source of truth for how agents use `.agent-memory/`. The `AGENTS.md`
section, skills, and native wrappers summarize and point here; when wording differs, this file wins.

Core model: the manifest routes context. Rules guide behavior. Memories store resolved knowledge.
Uncertainties block unsafe assumptions. Reports preserve history.

## When To Engage

- Skip the entire workflow for small, very specific, or trivial edits (one-line fixes, comments, renames).
- Skip lookup when the `.agent-memory/manifest.md` indexes are empty or contain nothing related to the task — start working immediately.
- Engage lookup for behavior-changing, unfamiliar, file/path-driven, historical, or decision-sensitive work.

## Before Work

1. Read `.agent-memory/manifest.md` and the always-read rules it lists.
2. Select task-relevant rules and memories by request terms, affected files, tags, and systems. Read only those.
3. Check relevant uncertainties. If one marked `blocking: true` affects the task, ask the user before modifying that behavior.
4. Use the `memory-scout` subagent (or `.agent-memory/agents/memory-scout.md` inline) only when the manifest shows several entries relevant to unfamiliar or memory-heavy work. For one or two obvious entries, just read them directly.
5. Do not read reports by default. Read a linked report only when deeper historical or debugging context is needed.

## During Work

- Follow loaded rules unless the user request or higher-priority project instructions conflict.
- Prefer existing memories over assumptions. If code contradicts a memory, mention it and investigate.
- Use memory `Code Navigation` sections before broad code searches.
- If intent is unclear, create or update an uncertainty instead of guessing.

## Planning And Investigation

When planning or investigating uncovers durable knowledge — how a system works, a decision made
with the user, a user-confirmed answer — save it as a memory, rule, or uncertainty at that point,
and refine it after implementation if needed.

Memory upkeep is background housekeeping, never a deliverable: do not include memory writes,
reports, or manifest updates as steps in a task plan, estimate, or todo list presented to the user.

## After Work

Run maintenance only when the work created durable context: a behavior, architecture, configuration,
test, or workflow change; a decision; a user-confirmed fact; or an investigation worth keeping.
Otherwise stop — no report, no handoff.

1. Prepare a compact handoff using `.agent-memory/templates/memory-maintenance-handoff.md` and delegate to the `memory-maintainer` subagent. Follow `.agent-memory/agents/memory-maintainer.md` inline when delegation is unavailable or excessive for the task.
2. Write or update a report only when the rationale, investigation, or validation detail would not be recoverable from git history plus the memories themselves.
3. Extract durable instructions into rules and durable knowledge into memories. Save first, then briefly tell the user what was saved — do not ask permission.
4. Create uncertainties for unresolved questions; resolve any the user answered.
5. Regenerate the manifest with `node .agent-memory/tools/refresh-manifest.js` — do not hand-edit the generated index blocks. Run `node .agent-memory/tools/validate-memory.js` after structural changes.
6. Review the maintainer result before the final response. The main agent stays accountable for implementation, validation, and user-visible reporting; the maintainer only curates `.agent-memory/`.

## Classification

- Prescriptive instruction, preference, policy, or constraint -> rule.
- Resolved fact, architecture detail, gotcha, user-confirmed answer, or navigation hint -> memory.
- Unresolved question, risky assumption, or unclear intent -> uncertainty.
- Historical narrative, tradeoffs, or validation detail -> report.
- Cannot be classified safely yet -> `.agent-memory/notes/pending.md` (not authoritative until curated).

Never convert guesses into memories. Never store secrets, credentials, or sensitive personal data.

## Uncertainty Resolution

When the user answers an uncertainty:

1. Record the answer as a memory (or a rule if prescriptive).
2. Mark the uncertainty resolved or move it to `.agent-memory/archive/resolved-uncertainties/`.
3. Regenerate the manifest with `node .agent-memory/tools/refresh-manifest.js`.
4. Note the resolution in the task report if one exists.

## Authoring Standards

- Markdown files with YAML frontmatter are the source of truth. Generated JSON is derived, never authoritative.
- Lowercase kebab-case filenames; reports use `YYYY-MM-DD_HHMM_short-name.md`.
- Rules: ~10-40 lines, prescriptive.
- Memories: ~10-40 lines, compact and focused; split around 80 lines.
- Reports: structured; capture rationale, not diffs git already records.
- Manifest: keep under ~150 lines; split into domain indexes if it grows past that.
- Frontmatter requirements live in the templates and are enforced by `validate-memory.js`.

## Visibility

Briefly note memory reads and writes as part of normal progress updates — one short factual line,
nothing more.
