# /memory-add <context>

Add user-provided project context to the memory system.

## Workflow

1. Decide whether the context is durable and clear.
2. If it is an instruction or preference, create or update a rule.
3. If it is resolved project knowledge, create or update a memory.
4. If it is unclear or risky to assume, create or update an uncertainty.
5. If immediate curation is unsafe, unclear, or disruptive, append it to `.agent-memory/notes/pending.md`.
6. Update `.agent-memory/manifest.md` when active memory-system files change.

Do not store secrets, credentials, private keys, tokens, or sensitive personal data.
