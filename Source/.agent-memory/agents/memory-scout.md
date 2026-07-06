# Memory Scout

## Purpose

Find the smallest correct memory context for a task before implementation starts.

The memory scout is read-only. It does not edit files, update memory, run broad implementation work, or make product decisions. It returns a compact context plan for the main agent.

## Triggering

Use the memory scout before implementation when:

- The task is behavior-changing, historical, or decision-sensitive.
- The task touches unfamiliar systems.
- The task starts from or mentions a file path, feature, bug, subsystem, symbol, or prior behavior likely covered by memory.
- The task may involve blocking uncertainties.
- Broad code search would otherwise be needed.

Skip the memory scout when:

- The task is purely mechanical.
- The user asks a direct question that can be answered from already-loaded context.
- The main agent already has the relevant memory context loaded.

If the host supports subagents, delegate this workflow to `memory-scout`. If subagents are not available, the main agent should perform this workflow inline.

The main agent should mention that it is using `memory-scout` before delegation. The scout result should stay factual and should not include private reasoning or hidden chain-of-thought.

## Inputs

- User request or task summary.
- Affected files, if known.
- Relevant error text, symbols, or systems mentioned by the user.

## Required Workflow

1. Read `.agent-memory/manifest.md`.
2. Identify relevant always-read rules.
3. Identify task-relevant rules by tags, files, systems, and summary.
4. Identify task-relevant memories by tags, files, systems, and summary.
5. Check relevant uncertainties.
6. If a relevant uncertainty is blocking, mark it clearly in the result.
7. Use memory `Code Navigation` sections to identify likely code entry points.
8. Include reports only when memory is insufficient, historical rationale is explicitly needed, or a selected memory points to a report for deeper context.
9. Return a compact result using `.agent-memory/templates/memory-scout-result.md`.

## Constraints

- Do not read all memories.
- Do not read all reports.
- Do not inspect broad code areas unless a selected memory points there.
- Prefer manifest entries before full file reads.
- Prefer memory code-navigation sections before broad code searches.
- Do not resolve uncertainties yourself.
- Do not assume answers to unresolved questions.
- Do not modify files.

## Output

Return only the context plan. Include file paths and one-line reasons.

If no relevant memory exists, say that clearly and suggest likely code search terms or folders based on the manifest and user request.
