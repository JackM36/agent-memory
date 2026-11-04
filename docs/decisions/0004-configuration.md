# 0004 — Configuration files and layering

- **Status:** accepted (owner review pending at the Phase 1 gate)
- **Date:** 2026-11-02
- **Plan:** §5.1, §7.4, amendment A1

## Format and locations

JSON with comments and trailing commas (`docs/schema/config.v1.schema.json`), parsed with a
source-generated context; unknown properties are errors.

| Level | File |
|---|---|
| Per call | CLI flags / request fields |
| Project local | `<project>/.agent-brain/config.local.json` (Git-ignored) |
| Project | `<project>/.agent-brain/config.json` |
| User | `AGENT_BRAIN_CONFIG_DIR`, else `%APPDATA%\agent-brain\config.json` (Windows) or `$XDG_CONFIG_HOME/agent-brain/config.json`, default `~/.config/agent-brain/config.json` |
| Built in | `ConfigDefaults` |

Project discovery: explicit `--project` → `AGENT_BRAIN_PROJECT` → nearest ancestor containing
`.agent-brain/config.json`. An explicit directory without a project file is an error.

## Layering (`ConfigLayering`)

As planned: single values from the highest level that sets them; collections and connections merged
by name with whole replacement and `enabled: false` removal; aliases unioned by term with the higher
level winning; project-only fields ignored with a warning in the user file; `inheritUser: false` drops
the user file; paths resolved against the declaring file's directory (per-call paths against the
working directory). Every effective value records its origin.

## Decisions made while implementing

- **`forwardContext` (new, single value, default true).** Whole replacement of connections means a
  project could otherwise only restrict forwarding for a user-level connection by copying its whole
  definition. `forwardContext: false` at project level turns off forwarding for every connection.
  Per-connection `forward` settings still apply when it is true, and default to all false.
- **Capabilities default to `["read"]`.** A registration listing capabilities without `read` gets
  `read` added with a warning, because an unreadable collection cannot be searched.
- **`writes.defaultCollection` must name a configured collection with `maintain`.** Otherwise it is
  an error; the destination is never inferred.
- **Connection URLs** must be absolute `https`, or `http` on a loopback address.
- **Out-of-range numbers** are errors; the effective value is clamped only so the rest of the
  configuration can still be reported.
- **Configuration `schema`** other than 1 is an error.
