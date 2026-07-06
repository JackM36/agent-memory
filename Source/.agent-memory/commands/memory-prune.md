# /memory-prune

Find stale, duplicate, oversized, or superseded memory-system entries.

## Workflow

1. Review manifest entries.
2. Identify stale or superseded rules and memories.
3. Identify oversized memories that should be split.
4. Identify resolved uncertainties that should be archived.
5. Move obsolete files to `.agent-memory/archive/` or update their status.
6. Update `.agent-memory/manifest.md`.

Never delete useful historical context if archiving is safer.
