# Entries and collections

## Collections

A collection is a directory with a `collection.json`
([schema/collection.v1.schema.json](schema/collection.v1.schema.json)) and Markdown entries anywhere
below it. A root `README.md` and directories starting with `.` are ignored.

```json
{
  "schema": 1,
  "id": "unity.kb",
  "title": "Unity knowledge",
  "readOnly": true,
  "defaults": { "applies": { "scope": "general", "technologies": ["unity"] }, "source": "curated" },
  "limits": { "summaryMax": 200 },
  "aliases": { "srp": ["scriptable render pipeline"] }
}
```

The default applicability is required, so an entry never becomes general by omission. Collection IDs:
lowercase segments joined by `.` or `-`, at most 64 characters.

## Entries

YAML frontmatter plus a Markdown body ([schema/frontmatter.v1.schema.json](schema/frontmatter.v1.schema.json)).

```markdown
---
schema: 1
id: save-system
kind: memory
title: "Save system: slots, JSON payloads and atomic writes"
summary: "Saves are per-slot JSON files written atomically by SaveSystem."
tags: [save, serialization]
files: ["Assets/Scripts/Save/**"]
symbols: [SaveSystem, SaveSlot]
evidence: ["Assets/Scripts/Save/SaveSystem.cs", "user confirmed 2026-10-14"]
verified: { on: 2026-10-14, by: code }
---
# Save system

`SaveSystem.WriteSlot` writes `slot-N.json.tmp`, then renames it. See [the open question](entry://space-game.memory/save-format-v2-undecided).
```

| Field | Required | Notes |
|---|---|---|
| `schema` | yes | `1` |
| `id` | yes | lowercase words joined by `-`, ≤ 80; identity, independent of the file name |
| `kind` | yes | `knowledge`, `memory`, `note`, `uncertainty`, `report` |
| `title` | yes | single line, ≤ 120 |
| `summary` | yes | single line, ≤ 240 (a collection may lower it); shown in search results |
| `tags` | | ≤ 12, normalised to kebab-case |
| `applies` | | `scope` (general/project), `projects`, `technologies`, `packages` (`name`, `versions`), `platforms`; each falls back to the collection default |
| `files` | | project-relative globs: `**`, `*`, `?`; a pattern without `/` matches a file name anywhere |
| `symbols` | | code symbols (exact match is strong evidence) |
| `source` | | `curated` or `observed` |
| `evidence` | | ≤ 20 short strings |
| `verified` | | `{ on: date, by: code\|user\|tests\|repeated-use\|unverified }` |
| `publication` | | `draft` or `published` (drafts are hidden from search) |
| `archived` | | date; retires the entry from default search |
| `superseded-by` | | `entry://` link to the replacement |
| `origin` | | `entry://` link for intentional forks |
| `resolution` | uncertainties | `unresolved` or `resolved` |
| `blocking` | uncertainties | surfaces first for matching files and symbols |
| `resolved-by` | | `entry://` link to the answering memory |
| `created`, `updated` | | `yyyy-MM-dd`, set by the tool |

Unknown fields are errors; prefix custom fields with `x-` to keep them.

## Version ranges

`>=1.2 <2.0` (AND), `>=1.0 <1.5 || >=3.1` (OR), `=2022.3.10f1` or a bare version (exact). Versions
compare by their numeric core; a suffix (`f1`, `-preview.3`) matters only for exact matches. If either
side is missing or unparseable, compatibility is unknown: neither excluded nor credited. The engine is a
package (`unity`).

## Links and sections

`[label](entry://collection-id/entry-id#section)`. Anchors are GitHub-style (lowercase, punctuation
removed except `-` and `_`, spaces to `-`; duplicates get `-1`, `-2`). A section runs to the next heading
of the same or higher level; text before the first heading is `#_intro`. Renaming a heading breaks links
to it (`section_missing` in `brain refs`). Get canonical links with `brain link`.

## Revisions

`r1-` plus 26 base32 characters of a SHA-256 over the canonical content. Formatting, field order, line
endings and `x-` fields do not change it; any content change does. Equal content has an equal revision
everywhere.
