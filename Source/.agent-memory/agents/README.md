# Agents

Generic agent specifications live here.

These files are source-of-truth prompts that describe reusable agent roles in an agent-agnostic way. Native wrappers for Claude, Codex, OpenCode, Gemini, and other hosts should point back to these specifications rather than duplicating behavior.

## Available Agents

- `memory-scout.md` - read-only context scout that routes a task to the smallest relevant rules, memories, uncertainties, reports, and code entry points.
- `memory-maintainer.md` - post-work memory curator that writes reports, memories, rules, uncertainties, and manifest updates from a main-agent handoff.
