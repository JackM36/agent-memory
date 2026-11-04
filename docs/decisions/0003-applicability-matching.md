# 0003 — Applicability matching: versions, globs, symbols, admission contract

- **Status:** accepted (owner review pending at the Phase 1 gate)
- **Date:** 2026-11-02
- **Plan:** §5.5, §5.6, §6.3

## Version ranges (`VersionValue`, `VersionRange`)

- Versions: dotted numeric core (up to six segments) plus an optional suffix: `2022.3.10f1`,
  `1.0.0-preview.3`. Ordering uses the zero-padded core only; the suffix matters only for exact matches
  (`=2022.3.10f1` does not match `2022.3.10f2`).
- Ranges: comparators `>= <= > < =` (a bare version means `=`), separated by spaces for AND, `||` for
  OR. Spaces after an operator are tolerated. `^` and `~` are **not** supported and are rejected rather
  than guessed.
- `VersionRange.Check` returns `Unknown` whenever either side is missing or unparseable. Unknown never
  excludes and never counts as compatible.
- The engine is a package: Unity versions use the package name `unity`.

## File globs (`GlobPattern`)

Project-relative, `/` only. `**` crosses directories, `*` and `?` stay in a segment, a trailing `/`
means everything below, a pattern without `/` matches a file name at any depth, a leading `/` anchors
at the root. Case-insensitive everywhere. `..`, empty segments, drive letters and backslashes are
rejected. Character classes are not supported and match literally. Compiled with
`RegexOptions.NonBacktracking`, so patterns cannot cause catastrophic backtracking.

## Symbols (`SymbolMatcher`)

Exact ordinal match is strong evidence, ordinal-ignore-case is weak. Splitting identifiers into words
(`SaveSystemV2` → `save system v2`) is a query-analysis concern for Phase 2.

## Admission contract (`Admission`)

Thresholds `T(class, relaxation)` interpolate linearly between a strict and a broad value per
applicability class. Tests pin the contract (non-increasing in relaxation; other-project > general ≥
same-project at every relaxation; other-project needs a descriptive-field hit below `broad`). The
constants and the preset values (`default` 0.35, `broad` 0.8) are calibration items for Phase 2.
