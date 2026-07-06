---
name: agent-memory
description: Use when working with `.agent-memory`, memory commands, rules, memories, uncertainties, reports, memory scout, or memory maintainer workflows.
---

# Agent Memory

Use `.agent-memory/protocol.md` for the operating protocol.

Use `.agent-memory/commands/` for command workflows.

Use `.agent-memory/agents/memory-scout.md` for the read-only memory scout role.

Use `.agent-memory/agents/memory-maintainer.md` for the post-work memory maintainer role.

For Codex, invoke this skill explicitly with `$agent-memory` or ask for a generic memory command by name. Codex custom slash commands are not represented here because the current Codex docs describe built-in slash commands, skills, and custom agents, not repo-scoped custom slash command files.

Do not read every memory or report by default. Start with `.agent-memory/manifest.md` and use progressive disclosure.

Mention memory access and memory updates briefly as user-visible progress. Keep announcements short and factual; do not reveal private reasoning or hidden chain-of-thought.
