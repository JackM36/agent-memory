# 0002 — Content model: identifiers, frontmatter, revisions, sections

- **Status:** accepted (owner review pending at the Phase 1 gate)
- **Date:** 2026-11-02
- **Plan:** §5.2, §5.3, §5.4, §5.11, §5.13

## Decisions

### Identifiers (`AgentBrain.Domain.Identity`)

- Collection IDs: `^[a-z0-9]+([.-][a-z0-9]+)*$`, max 64. Project IDs and connection names use the same grammar.
- Entry IDs: `^[a-z0-9]+(-[a-z0-9]+)*$`, max 80.
- Identifiers are validated, never normalised. Generated IDs come from `Slug`: Latin letters with
  diacritics fold to ASCII through an explicit table (`ü`→`u`, `ß`→`ss`, `æ`→`ae`), other characters
  become `-`. Non-Latin scripts produce no characters, so a title in such a script falls back to `entry`
  (with collision suffixes). This keeps IDs URI-safe and identical on every platform; Unicode
  normalisation is unavailable under invariant globalization (ADR 0001).
- Collisions append `-2`, `-3`, … and shorten the stem to stay within 80 characters.

### Frontmatter schema 1 (`docs/schema/frontmatter.v1.schema.json`)

- Parsed into a format-neutral `MetadataNode` tree, then mapped by `EntryMetadataReader`. YAML is one
  adapter; the SQLite store will use the same mapping.
- Unknown fields are errors; `x-` fields are preserved and ignored. A newer `schema` is reported on its
  own (`unsupported_schema`) without flagging the fields that version may legitimately add.
- Values are normalised on read: tags to kebab-case, technology/platform/package names to lowercase,
  lists de-duplicated.
- `applies` fields fall back to the collection default **field by field**. A collection must declare a
  default scope, so a missing field never makes an entry general.
- No `confidence` field.
- `EntryValidator` enforces cross-field rules: length limits, single-line title and summary,
  uncertainty-only fields, `resolved` vs `blocking`, `general` vs `projects`, version range syntax,
  safe file globs, no self-links, whole-entry links only for lifecycle links.
- Dates are `yyyy-MM-dd` only.

### Revisions (`RevisionCalculator`)

`r1-` + 26 lowercase base32 characters of SHA-256 over:
`canonical metadata JSON` + `"\n---\n"` + `normalised body`, where canonical metadata is the
`EntryMetadataWriter` tree serialised with ordinally sorted keys and `x-` fields excluded, and the body
has its BOM removed, line endings converted to LF and exactly one trailing newline.

Consequences: field order, YAML quoting style, CRLF checkouts and extension fields do not change a
revision; any content change does. Equal content has an equal revision in every adapter and copy.

### Sections (`HeadingAnchor`, `AnchorAssigner`, `SectionMap`)

GitHub-style anchors (lowercase, punctuation removed except `-` and `_`, spaces to `-`, Unicode letters
kept); duplicates get `-1`, `-2`, …; `_intro` is reserved for text before the first heading and only
exists when that text is not blank. A section runs to the next heading of the same or higher level.
Finding headings (and skipping fenced code) is the Markdown adapter's job.

Accepted cost: renaming a heading breaks links to that section (`section_not_found`).

### Entry links (`EntryLink`)

`entry://collection-id/entry-id[#anchor]`, scheme lowercase, no host, query or path beyond the two
segments. Non-ASCII anchors are percent-encoded in the URI and decoded on parse.
