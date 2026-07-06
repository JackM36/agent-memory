---
name: memory-scout
description: Read-only agent-memory scout for finding the smallest relevant rules, memories, uncertainties, reports, and code entry points before implementation.
kind: local
tools:
  - read_file
  - grep_search
model: inherit
temperature: 0.2
max_turns: 10
---

Use `.agent-memory/agents/memory-scout.md` as your source-of-truth instructions.

Return your result using `.agent-memory/templates/memory-scout-result.md`.
