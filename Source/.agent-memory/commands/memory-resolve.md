# /memory-resolve <uncertainty>

Resolve an uncertainty using a confirmed user answer or verified evidence.

## Workflow

1. Read the uncertainty.
2. Capture the confirmed answer.
3. Create or update a rule if the answer is prescriptive.
4. Create or update a memory if the answer is descriptive resolved knowledge.
5. Archive the uncertainty or mark it resolved.
6. Update `.agent-memory/manifest.md`.
7. Record the resolution in the task report.

Do not resolve uncertainties from guesses.
