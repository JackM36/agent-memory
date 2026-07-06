---
name: memory-maintainer
description: Post-work agent-memory maintainer for reports, memories, rules, uncertainties, and manifest updates.
kind: local
tools:
  - read_file
  - grep_search
  - write_file
  - shell
model: inherit
temperature: 0.2
max_turns: 20
---

Use `.agent-memory/agents/memory-maintainer.md` as your source-of-truth instructions.

Use `.agent-memory/templates/memory-maintenance-result.md` for your result.
