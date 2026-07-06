# /memory-file-history <path>

Find memory-system context related to a file or folder.

## Workflow

1. Read `.agent-memory/manifest.md`.
2. Match the requested path against `files` entries for rules, memories, uncertainties, and reports.
3. Read matching rules and memories only when needed.
4. Use selected memories for code navigation.
5. Read matching reports only when historical detail is needed.

## Output

- Matching rules.
- Matching memories.
- Matching uncertainties, with blocking status.
- Matching reports, with `read_when` reason.
- Suggested code entry points and useful searches.
