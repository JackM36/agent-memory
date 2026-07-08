## Project Memory

This repository uses a local memory system in `.agent-memory/`. The full protocol is
`.agent-memory/protocol.md`; this section is the short form. Skip the whole workflow for small,
very specific, or trivial edits (a one-line fix, a comment, a rename).

Before non-trivial work:

1. Read `.agent-memory/manifest.md`. If its indexes are empty or list nothing related to the task, skip lookup and start working.
2. Read the always-read rules plus the rules, memories, and uncertainties matching the task by tags, files, or summary. If a relevant uncertainty is `blocking: true`, ask the user before changing that behavior.
3. Delegate lookup to the `memory-scout` subagent (or run `.agent-memory/agents/memory-scout.md` inline) only when the manifest shows several relevant entries for unfamiliar work. Do not read reports unless deeper history is needed.

After work that created durable context (a behavior/architecture change, a decision, a user-confirmed fact, an investigation that explains how something works):

4. Prepare a handoff per `.agent-memory/templates/memory-maintenance-handoff.md` and delegate curation to the `memory-maintainer` subagent, or follow `.agent-memory/agents/memory-maintainer.md` inline. Save durable knowledge into memories/rules, record unresolved questions as uncertainties, write a report only when the rationale is not recoverable from git, then run `node .agent-memory/tools/refresh-manifest.js`.
5. Save first, then briefly tell the user what was saved — do not ask permission. If work created no durable context, skip maintenance entirely.

Knowledge discovered while planning or investigating (system behavior, user answers, decisions) is saved the same way, when it surfaces — and refined later if implementation changes it. Memory upkeep is background housekeeping: never include memory writes, reports, or manifest updates as steps in a task plan or todo list presented to the user.

Briefly note memory reads and writes as part of normal progress updates.
