# Evaluation corpus

Fixtures for measuring retrieval quality (plan §9.2, spec §15.1). The entries are plausible technical
notes written for testing; they are not authoritative guidance.

```text
eval/
├─ corpus/            five collections, 45 entries
├─ corpus-mirror/     a second "source" exposing unity.kb (one identical copy, one changed copy)
└─ cases/             query cases with expectations, plus named query contexts
```

`AgentBrain.Infrastructure.Markdown.Tests` validates every entry against the real schema, checks that
links resolve, and checks that every case refers to existing entries.

## Running the evaluation

```bash
dotnet run --project eval/AgentBrain.Evaluation -c Release
```

Options: `--tokenizer porter|unicode61`, `--verbose` (print every case's results), `--out <file>` (write
a baseline), `--min-pass <0..1>` and `--max-irrelevant <n>` (exit 1 below the gate, as CI does).
The runner opens a temporary project per named context, indexes `corpus/` from scratch and runs every
case through the real runtime. It reports pass rate, recall of expected entries, irrelevant results,
response size, latency and relaxation monotonicity. Accepted baselines live in `baselines/`; ADR 0006
explains the current numbers and the remaining misses.

`dotnet run --project eval/AgentBrain.Evaluation -c Release -- scan [sizes…]` measures index cost on
synthetic collections (default 100, 1,000 and 10,000 entries).

## Collections

| Collection | Scope | Entries | Purpose |
|---|---|---|---|
| `general.engineering` | general, curated, read-only | 11 | Clean-code guidance usable from any technology; one draft |
| `unity.kb` | general, Unity, curated, read-only | 10 | Unity-only material, package and engine version ranges, aliases (`ecs`, `srp`, `urp`) |
| `web.kb` | general, web, curated, read-only | 6 | Web-only material |
| `space-game.memory` | project `space-game` (Unity) | 11 | Same-project decisions and gotchas, a blocking uncertainty, a resolved uncertainty, an archived/superseded entry, a report, a note |
| `web-shop.memory` | project `web-shop` (web) | 7 | The "other project": one strongly relevant experience, weak tag-only matches, a blocking uncertainty that must never block `space-game` |

## Coverage of spec §15.1

| Category | Cases |
|---|---|
| General guidance usable from Unity and web | `general-from-unity`, `general-from-web`, `general-naming` |
| Unity-only and web-only material | `unity-polymorphic-lists`, `web-stale-effect`, `web-search-as-you-type`, `explicit-other-technology`, `unity-content-hidden-from-web` |
| Same-project decisions, package guidance | `same-project-save-change`, `same-project-level-loading`, `same-project-new-item`, `same-project-paraphrase` |
| Strong other-project experience qualifies | `strong-other-project` |
| Weak other-project tag matches do not | `weak-other-project-tags`, `other-project-blocking-never-blocks` |
| Unknown and incompatible versions | `incompatible-excluded`, `incompatible-included-on-request`, `unknown-version-not-excluded`, `engine-version-incompatible` |
| No-match queries | `no-match-ops`, `no-match-unrelated`, `drafts-hidden` |
| Aliases and exact identifiers | `alias-ecs`, `alias-srp`, `alias-dip`, `identifier-jsonutility`, `identifier-split`, `same-project-new-item` |
| Relaxation variants | `relaxation-input`, `relaxation-logging` |
| Blocking uncertainties (A4) | `same-project-save-change`, `blocking-by-symbol-only`, `other-project-blocking-never-blocks` |
| Lifecycle defaults | `history-excluded-by-default`, `history-included-on-request`, `resolved-uncertainty-excluded`, `reports-need-their-kind`, `reports-by-kind` |
| Paraphrases | `unity-polymorphic-lists`, `web-search-as-you-type`, `same-project-paraphrase`, `paraphrase-addressables-leak`, `paraphrase-retry`, `paraphrase-sigterm` |
| Differently sized sources, duplicates, conflicting revisions (Phase 5) | `differently-sized-sources`, `duplicate-identity-collapsed`, `conflicting-revisions-visible` |

## Case format

```jsonc
{
  "id": "same-project-save-change",          // unique
  "categories": ["same-project", "blocking"],
  "phase": 5,                                 // optional: first phase that can evaluate it (default 2)
  "query": "change how save slots are written to disk",
  "context": "space-game",                    // a name from contexts.json
  "files": ["Assets/Scripts/Save/SaveSystem.cs"],   // optional, added to the context
  "symbols": ["SaveSystem"],                  // optional, added to the context
  "relaxation": "default",                    // optional
  "include": { "history": false, "incompatible": false },  // optional
  "filters": { "kinds": ["report"] },         // optional exact filters
  "sources": ["corpus", "corpus-mirror"],     // optional, Phase 5 multi-source cases
  "expect": {
    "relevant": [],      // must appear in results
    "irrelevant": [],    // must not appear in results
    "blocking": [],      // must appear in the blocking section
    "notBlocking": [],   // must not appear in the blocking section
    "conflicts": [],     // must report differing revisions (Phase 5)
    "maxOccurrences": 1, // a ref may appear at most this often (Phase 5)
    "first": "…",        // must be the top result
    "empty": true        // no results at all
  },
  "variants": [ { "relaxation": "strict", "expect": { } } ],  // instead of a single expect
  "notes": "why the expectation holds"
}
```

## Known hard cases

`same-project-paraphrase` ("bullets" vs "projectiles") and the paraphrase cases have no lexical
overlap on their key terms. They exist to measure the vocabulary-mismatch risk honestly; Phase 2
calibration decides whether stemming, identifier splitting or aliases close them, or whether they
remain documented misses.

## Comparing decision implementations

`--relevance heuristic|jev|hybrid` runs the cases with that relevance judge (ADR 0010); `--compare heuristic,jev,hybrid`
runs each in turn and prints pass rate, recall, irrelevant results, latency, and for model runs the calls, input tokens and
estimated cost. A `jev` or `hybrid` run needs `TYPESAFE_API_KEY`; without it that row is skipped, never filled with
heuristic numbers. The corpus is fixture data, so the runner marks every corpus collection `allowExternalDecisions` for a
model run. Nothing in this repository has been measured against the live service yet: the adoption rule in ADR 0010
(a model-backed port is recommended only if it beats the heuristic by a margin that justifies its latency and cost,
with no new irrelevant results and no monotonicity violations) is still to be applied with a real key.
