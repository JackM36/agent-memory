# 0006 — Local retrieval: pipeline, calibration and measurements (Phase 2)

- **Status:** accepted
- **Date:** 2026-11-02
- **Plan:** §5.7–§5.10, §5.14, §6, §7.1–§7.3, §9.3

## Design decisions made while implementing

- **One local provider over one shared index.** All local collections are searched through a single
  SQLite index and ranked together, so their scores are directly comparable. Rank fusion is only needed
  between sources with separate indexes (local vs remote, Phase 5). The coordinator already merges by
  rank, de-duplicates identities at their best rank and reports per-source state; it is unit-tested
  with fake providers, including timeouts.
- **Eligibility lives in the index.** Collections, kinds, tags, publication, retirement, resolved
  uncertainties and version incompatibility are applied in SQL before the candidate limit. Version
  checks run in C# (`VersionRange.Check`) over the few package constraints whose names appear in the context.
- **Candidates** are the BM25 top-N (column weights title 10, summary 6, tags 6, identifiers 4, body 1)
  unioned with structural matches (symbols, file globs, packages) and the project's blocking uncertainties.
- **Concept hits** are computed with FTS itself (a descriptive-column query and an any-column query per
  concept, restricted to the candidates), so stemming applies identically to retrieval and evidence.
- **Coverage** weights each concept by rarity `ln(1 + N/(df+1))`; a hit in title/summary/tags/identifiers
  counts fully, body-only counts half. A concept absent from the corpus counts with the mean weight of the
  known concepts (absent words neither vanish nor dominate).
- **Stopwords** include common task filler ("after", "again", "try", "use", "make", "need").
- **Technology mismatch** adds 0.45 to the admission threshold for general and same-project entries, but
  only 0.05 for other-project entries, which already face the stricter other-project threshold. Without
  this, strong cross-project relevance (spec §7.3) could never qualify.
- **Collection aliases** apply to the whole query in Phase 2 (plan §5.7 scopes them per collection).
  Expansions only add equivalent terms, so the effect is limited to matching.
- **Duplicate entry IDs** within a collection: the ordinally first path wins; others are excluded and
  reported as `duplicate_id` diagnostics.
- **Output** shows `sources: N ok` when every source succeeded and lists each source only when the
  search is partial, to keep the header small.

## Calibration (evaluation corpus, 40 case variants)

| Setting | Value |
|---|---|
| Tokenizer | `porter unicode61 remove_diacritics 2` |
| Thresholds strict → broad | same project 0.45 → 0.05 · general 0.55 → 0.15 · other project 0.75 → 0.35 |
| Presets | strict 0 · default 0.35 · broad 0.8 |
| Bytes per token | 4.0 (unchanged; not yet measured against real tokenizers) |
| Default budgets | 10 results · ~1,000 tokens · 4,000 bytes (unchanged) |

| Tokenizer | Variants passed | Recall of expected entries | Irrelevant returned | Mean response |
|---|---|---|---|---|
| **Porter** | **35/40 (87.5%)** | **33/38 (86.8%)** | **0/16** | 427 bytes (~107 tokens) |
| Unicode61 | 31/40 (77.5%) | 29/38 (76.3%) | 0/16 | 345 bytes |

Relaxation monotonicity: no violations (every case checked at strict and broad without count or byte limits).
Baselines are in `eval/baselines/`. CI fails below a 0.85 pass rate, on any irrelevant result, or on any
monotonicity violation.

### Remaining misses (documented, not tuned away)

| Case | Why |
|---|---|
| `unity-polymorphic-lists` | Paraphrase with no shared vocabulary ("derived classes lose their subclass type" vs "polymorphic … [SerializeReference]"). |
| `general-naming` | "pick clear variable names" shares only "names" with the entry. |
| `strong-other-project` (same-project part), `history-excluded-by-default`, `reports-need-their-kind` | `space-game.memory/save-system` shares one or two words with these queries (coverage 0.17–0.28) and stays below the same-project threshold. The cross-project part of `strong-other-project` now passes. |

These are the vocabulary-mismatch risk the plan predicted (plan §12). Lowering thresholds further would
admit these at the cost of noise on every query; the query log (`search.log: true`) will show whether
real agent queries hit this pattern before anything else changes.

## Measurements (win-x64, owner's machine)

Scan cost with synthetic entries (`dotnet run --project eval/AgentBrain.Evaluation -c Release -- scan`):

| Entries | Rebuild | No-change refresh (every search) | Refresh after one edit | Search |
|---|---|---|---|---|
| 100 | 108 ms | 1.5 ms | 2.7 ms | 36 ms (first, JIT) |
| 1,000 | 292 ms | 3.3 ms | 5.6 ms | 8 ms |
| 10,000 | 2,256 ms | 40 ms | 50 ms | 44 ms |

The first implementation took 23 s to rebuild and 16 s to refresh one edit at 10,000 entries: a correlated
duplicate-ID update and missing indexes on child tables made both quadratic. Replaced by a set-based update
and per-child indexes (index schema 3).

Native AOT `brain.exe` (with MCP): 17.2 MB, no trim or AOT warnings. A warm `brain search` against the
45-entry corpus takes about 64 ms wall time including process start and the freshness scan. MCP
`tools/list` for the four read tools is 2,928 bytes (about 730 tokens), within the ~1,200-token target.
macOS and Linux numbers come from CI once it runs.

## Deferred

- The shared provider contract suite (`AgentBrain.ProviderContract.Tests`) moves to Phase 5, when a
  second adapter (SQLite authoritative storage, remote) exists to run it against.
- Federated list cursors (plan §5.10) arrive with remote sources; Phase 2 lists the single local source.
- `bytesPerToken` calibration against real tokenizers needs an offline tokenizer comparison; 4.0 stays.
- `list` and `get` do not yet mark entries as `incompatible` (plan §5.5 says they always show them with the
  flag). Listing carries no query context today; it needs the project's package versions passed through.
