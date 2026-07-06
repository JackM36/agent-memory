## Project Memory

This repository uses a local agent memory system in `.agent-memory/`. Treat the steps below as
requirements for non-trivial work, not suggestions. **Skip the whole workflow only for small, very
specific, or trivial edits** (e.g. a one-line fix, a comment, a rename) — for those, do the edit
and move on.

### Before work (non-trivial tasks)

1. Read `.agent-memory/manifest.md` first, then the always-read rules it lists.
2. Consult `memory-scout` before implementing when the task is non-trivial, unfamiliar, file/path-driven, likely covered by memory, or could involve blocking uncertainties. If the host supports subagents, delegate a read-only lookup to `memory-scout` with the request and known affected files; otherwise run `.agent-memory/agents/memory-scout.md` inline.
3. Read only the rules and memories relevant to the task. If the task starts from a file path, use manifest `files` entries to find related rules, memories, uncertainties, and reports.
4. Check relevant uncertainties before changing behavior. If a relevant uncertainty is marked `blocking: true`, ask the user before continuing with that part of the work.
5. Do not read reports by default; read a linked report only when you need deeper historical/debugging context.

### After work (non-trivial tasks) — do this, don't ask whether to

6. **Prepare a memory maintenance handoff** using `.agent-memory/templates/memory-maintenance-handoff.md`. This applies both after implementation **and after a planning/investigation phase** that uncovers how a system works, records a decision, or surfaces anything worth knowing in future sessions.
7. **Delegate post-work curation to `memory-maintainer` when available.** The maintainer may write `.agent-memory/reports/`, `.agent-memory/memories/`, `.agent-memory/rules/`, `.agent-memory/uncertainties/`, `.agent-memory/notes/pending.md`, and `.agent-memory/manifest.md`. If no write-capable subagent is available, follow `.agent-memory/agents/memory-maintainer.md` inline.
8. **From the report, curate durable context immediately:** extract durable knowledge into one or more memories (or update existing related memories with the new info), and durable instructions into rules. When classification is clear, **save it and then briefly tell the user what you saved — do not ask permission first.** Park genuinely uncertain items in `.agent-memory/notes/pending.md`.
9. Create uncertainties for unresolved questions or places where future agents must not assume intent.
10. Update `.agent-memory/manifest.md` after any rule, memory, report, or uncertainty change.
11. Review the maintainer result before the final response. The main agent remains accountable for implementation, validation, handoff quality, and final user-visible reporting.

The memory scout returns a compact context plan before work. The memory maintainer performs post-work memory curation from a compact handoff. The main agent remains responsible for implementation, validation, and reviewing the maintainer result.

Memory visibility:

- Mention memory access and memory updates briefly as user-visible progress.
- Announce when reading the manifest, using `memory-scout`, reading reports, or creating/updating rules, memories, uncertainties, reports, pending notes, or the manifest.
- Keep announcements short and factual. Do not reveal private reasoning or hidden chain-of-thought.

Core principle:

> Manifest routes context. Rules guide behavior. Memories store resolved knowledge. Uncertainties block unsafe assumptions. Reports preserve history.
