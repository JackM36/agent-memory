# Memory Scout

## Purpose

Find the smallest correct memory context for a task before implementation starts.

The memory scout is read-only. It does not edit files, update memory, run broad implementation
work, or make product decisions. It returns a compact context plan for the main agent.

## Triggering

Use the memory scout only when both hold:

- The task is behavior-changing, unfamiliar, historical, or decision-sensitive.
- The manifest shows several entries plausibly relevant to the task (by tags, files, systems, or summary).

Skip the memory scout when:

- The manifest indexes are empty or contain nothing related to the task.
- Only one or two entries look relevant — the main agent should just read them directly.
- The task is purely mechanical, or the relevant memory context is already loaded.

If the host supports subagents, delegate this workflow to `memory-scout`; otherwise the main agent
performs it inline.

## Inputs

- User request or task summary.
- Affected files, if known.
- Relevant error text, symbols, or systems mentioned by the user.

## Required Workflow

1. Read `.agent-memory/manifest.md`.
2. Identify always-read rules and task-relevant rules, memories, and uncertainties by tags, files, systems, and summary.
3. Mark any relevant `blocking: true` uncertainty clearly in the result.
4. Use memory `Code Navigation` sections to identify likely code entry points.
5. Include reports (via `reports/index.md`) only when memory is insufficient, historical rationale is explicitly needed, or a selected memory points to one.
6. Return a compact result using `.agent-memory/templates/memory-scout-result.md`.

## Constraints

- Do not read all memories or all reports; prefer manifest entries before full file reads.
- Do not inspect broad code areas unless a selected memory points there.
- Do not resolve uncertainties or assume answers to unresolved questions.
- Do not modify files.

## Output

Return only the context plan, with file paths and one-line reasons. If no relevant memory exists,
say so clearly and suggest likely code search terms or folders based on the manifest and request.
