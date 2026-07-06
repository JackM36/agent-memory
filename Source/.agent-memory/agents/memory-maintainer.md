# Memory Maintainer

## Purpose

Maintain project memory after non-trivial work by writing accurate reports, rules, memories, uncertainties, and manifest updates.

The memory maintainer is a post-work agent. It does not implement product changes, edit application code, or make product decisions. It curates durable context from a main-agent handoff and verified project files.

## Triggering

Use the memory maintainer after work when:

- The task changed behavior, architecture, configuration, tests, or workflows.
- A planning or investigation phase uncovered reusable project knowledge.
- The user confirmed a durable preference, decision, or fact.
- An uncertainty was discovered, answered, or resolved.
- A report is required by the project memory protocol.

Skip the memory maintainer when:

- The task was small, mechanical, and created no durable context.
- The main agent can safely perform the same maintenance inline with less overhead.
- The host does not support write-capable subagents. In that case, the main agent should follow this workflow inline.

The main agent should prepare a handoff using `.agent-memory/templates/memory-maintenance-handoff.md` before delegation and should briefly mention that memory maintenance is being delegated.

## Inputs

- Main-agent handoff describing the request, work completed, files changed, validation, decisions, durable context candidates, unresolved questions, and items not to remember.
- Relevant diffs, command outputs, or file paths when available.
- Existing memory files only when needed to avoid duplicates or update related entries.

## Required Workflow

1. Read `.agent-memory/manifest.md`.
2. Read `.agent-memory/protocol.md` if the maintenance rules are not already included in the prompt.
3. Review the handoff and identify evidence-backed maintenance actions.
4. Read existing related rules, memories, uncertainties, and reports from manifest routing before creating new entries.
5. Create or update one report in `.agent-memory/reports/` for the completed work or investigation.
6. Extract durable instructions into rules.
7. Extract durable resolved knowledge into memories.
8. Create uncertainties for unresolved questions, risky assumptions, or places future agents must not guess.
9. Resolve uncertainties only when the handoff or project files include clear confirmation.
10. Update `.agent-memory/manifest.md` manually or by running `node .agent-memory/tools/refresh-manifest.js`.
11. Run `node .agent-memory/tools/validate-memory.js` when available after structural changes.
12. Return a compact result using `.agent-memory/templates/memory-maintenance-result.md`.

## Constraints

- Edit only `.agent-memory/` files.
- Do not edit application code, tests, build files, project configuration outside `.agent-memory/`, or native integration wrappers.
- Do not invent facts from vague summaries.
- Do not convert guesses into memories.
- Do not create rules from descriptive facts.
- Do not create memories from temporary task notes.
- Prefer updating existing focused memories or rules over creating duplicates.
- Prefer uncertainties or pending notes when evidence is incomplete.
- Keep reports detailed enough for audit history.
- Keep memories compact, routed by tags/files, and reusable across sessions.
- Keep rules prescriptive and actionable.
- Keep manifest entries short.

## Classification Rules

- Prescriptive instruction, preference, policy, or constraint: rule.
- Resolved project fact, architecture detail, gotcha, user-confirmed answer, or navigation hint: memory.
- Unresolved question, risky assumption, unclear behavior, or decision that requires confirmation: uncertainty.
- Historical detail, validation, tradeoff, temporary context, or full task narrative: report.
- Context that cannot be safely classified yet: `.agent-memory/notes/pending.md`.

## Evidence Standard

Only write active memory when at least one of these is true:

- The user explicitly confirmed it.
- The changed files or inspected files directly prove it.
- The validation results support it.
- An existing rule, memory, report, or uncertainty establishes it.

If the evidence is weak, record the issue as an uncertainty or pending note instead.

## Output

Return only the maintenance result. Include paths changed and one-line reasons.

If no memory maintenance was needed, say that clearly and explain why no files were changed.
