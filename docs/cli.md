# CLI reference

`brain <command> [options]`. Every command accepts `--project <dir>`, `--format compact|human|json`
(default: `human` on a terminal, `compact` otherwise) and `--collection-root <dir>` (a read-only
collection for this call only). Run `brain <command> --help` for every option.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (partial results include a notice) |
| 1 | Internal error |
| 2 | Usage, validation or configuration error |
| 3 | Not found / section not found |
| 4 | Revision conflict or lock timeout |
| 5 | Unauthorized, forbidden, read-only or unsupported |
| 6 | No source available |
| 7 | Budget too small |
| 8 | Secret detected |

## Finding

| Command | Purpose |
|---|---|
| `brain search "<task>" [--file p] [--symbol s] [--tech t] [--package name@ver] [--hint-tag t] [--kind k] [--tag t] [--tags-any] [--collection c] [--relax strict\|default\|broad\|0..1] [--limit n] [--max-tokens n] [--max-bytes n] [--include-history] [--include-incompatible] [--explain]` | Ranked, budgeted discovery |
| `brain list [--kind k] [--tag t] [--collection c] [--blocking] [--include-archived] [--include-drafts] [--cursor c] [--page-size n]` | Exact listing, stable order, every match |
| `brain get <ref>[#section]… [--max-bytes n] [--with-refs]` | Fetch entries or sections (batch: up to 10) |
| `brain refs <ref>` / `brain backlinks <ref> [--cursor c]` | Outgoing references / incoming links |
| `brain link <ref> [--section text] [--label text]` | Canonical Markdown link |
| `brain collections` | Configured collections and capabilities |

`--include-history` adds archived and superseded entries and resolved uncertainties. Reports and notes
are not history: include them with `--kind report` / `--kind note`. `--explain` prints the evidence behind
every rank.

## Writing

| Command | Purpose |
|---|---|
| `brain similar --title … [--summary …] [--tag …]` | Existing entries that may already cover it |
| `brain new <kind> --title … --summary … [--body … \| --body-file path\|-] [--tag t] [--file glob] [--symbol s] [--evidence e] [--tech t] [--id id] [--blocking] [--collection c]` | Create |
| `brain update <ref> --base-revision r1-… [same content options]` | Change only the given fields |
| `brain archive <ref> --base-revision …` | Retire from default search |
| `brain supersede <ref> --by <ref> --base-revision …` | Point to a replacement |
| `brain resolve <uncertainty> --by <memory> --base-revision …` | Answer a question |
| `brain delete <ref> --base-revision …` | Delete (prefer archive) |
| `brain promote <ref> [--to c] [--title …] [--summary …] [--body-file …] [--tech t] [--supersede]` | Copy an entry into the knowledge collection as general knowledge |

**Where writes go.** Without `--collection`, `knowledge` goes to `writes.knowledge` and every other kind to
`writes.memory`. Local or remote, the destination decides how: with `maintain` the entry is written; with
only `propose` the command records a proposal instead and says so (exit 0). Read-only collections refuse.
`promote` copies the text as it is and drops files and symbols; pass `--title`, `--summary` or `--body-file`
to generalise it. `--supersede` marks the original superseded by the copy when the copy was written.

Get the base revision from `brain get <ref> --format json` (`entries[0].entry.rev`). A secret in the
content fails with exit 8; `--allow-secret-rule <rule>` allows one rule for one write, only after the
user confirms a false positive.

## Proposals and publishing

| Command | Purpose |
|---|---|
| `brain propose create\|update\|delete …` | Record a proposed change; nothing changes yet |
| `brain proposals list [--status pending\|accepted\|rejected]` / `show <id>` | Review: local proposals and those on every connection; remote IDs read `connection:id` |
| `brain proposals accept <id> [--note …]` / `reject <id> [--reason …]` | Decide (accept needs `publish`) |
| `brain publish create\|update\|delete …` | Apply immediately (only where `immediatePublish` is on) |
| `brain publish --batch changes.json` | Several changes to one collection together |
| `brain batch status` / `resume <id>` / `discard <id>` | Recover an interrupted Markdown batch |

Batch file:

```json
{
  "changes": [
    { "kind": "create", "collection": "team.kb", "entryKind": "knowledge", "title": "…", "summary": "…", "body": "…", "tags": ["…"] },
    { "kind": "update", "collection": "team.kb", "entry": "naming", "baseRevision": "r1-…", "summary": "…" },
    { "kind": "delete", "collection": "team.kb", "entry": "old-note", "baseRevision": "r1-…" }
  ]
}
```

## Setup and maintenance

| Command | Purpose |
|---|---|
| `brain init [--project-id id] [--integrations claude,codex,gemini,opencode\|all\|none] [--no-hooks] [--claude-md import\|none] [--tech t]` | Set up a project |
| `brain doctor` | Check configuration, collections, index, batches, integrations |
| `brain index status\|reindex\|verify\|reset` | Inspect or rebuild the index |
| `brain config show [--origin]` / `config validate` | Effective configuration and where each value came from |
| `brain connection add <name> --url … --credential-env VAR [--collections a,b] [--project]` / `connection test <name>` | Register a remote host |
| `brain collection create <id> [--path dir] [--title …] [--tech t] [--review] [--knowledge] [--project]` | Create a general-knowledge collection folder and register it (user file by default; `--review` makes saves proposals; `--knowledge` sets `writes.knowledge`) |
| `brain collection add <path> [--access read\|review\|maintain] [--knowledge] [--project]` | Register an existing collection, e.g. another project's memory for cross-project search (read-only by default) |
| `brain mcp` | MCP server on stdin/stdout |
| `brain upgrade [--check] [--version x] [--force]` | Upgrade brain in place to the latest GitHub release, checksum-verified; `--check` only reports |
| `brain hook stop --host claude\|codex` | Stop-hook entry point (installed by `init`) |
