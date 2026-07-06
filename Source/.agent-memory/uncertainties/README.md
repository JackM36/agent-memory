# Uncertainties

Unresolved questions, unclear decisions, suspicious legacy behavior, or areas where agents must not assume intent belong here.

Use `.agent-memory/templates/uncertainty.md` when creating an uncertainty.

## Blocking Rule

If an uncertainty is relevant to the current task and marked `blocking: true`, the agent must ask the user before proceeding with the affected work.

## Resolution Flow

1. User answers the question.
2. Agent creates or updates a memory with the confirmed answer.
3. Agent moves the uncertainty to `.agent-memory/archive/resolved-uncertainties/` or marks it archived.
4. Agent updates `.agent-memory/manifest.md`.
5. Agent records the resolution in the task report.
