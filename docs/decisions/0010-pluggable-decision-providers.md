# 0010 — Pluggable decision providers (heuristic and Jev)

- **Status:** accepted; Phases 1–4 implemented and tested against fakes. No live Jev call has been made, so no Jev implementation is recommended yet (see Implementation notes)
- **Date:** 2026-11-04
- **Plan:** extends §5.7–§5.10 (retrieval) and §5.11 (writes); relaxes the "no model calls" statement in ADR 0008

## Context

Retrieval and writing are deterministic today. Search quality depends on a handful of judgements made by
heuristics, and write-time classification is left to the calling agent:

| Judgement | Today |
|---|---|
| Which entries are candidates | BM25 top-N plus structural matches (ADR 0006) |
| Does a candidate answer the query | Rarity-weighted concept coverage against a threshold per applicability class |
| Does it apply to this project or technology | Glob, symbol, package and technology matching |
| Which kind, and which destination (`memory` or `knowledge`) | The agent chooses; the router follows the kind (ADR 0008) |
| Is a new entry a duplicate | `SimilarEntryFinder`, lexical, informational only |
| Is something worth capturing | The agent decides; no auto-capture gate |

These heuristics are fast, free and reproducible, and they have a ceiling: paraphrases miss (aliases patch
this by hand), near-misses are admitted, and a lexical duplicate check cannot see a reworded duplicate.
Raising quality means making these judgements better, not only faster.

TypeSafe's Jev is a "System One" decision model. A caller sends a `state` (text or JSON) and named typed
questions (`choice`, `score`, `noul`) and receives typed answers with probabilities (`choice` and `score` also
return a confidence). Facts taken from its documentation (docs.typesafe.ai, read 2026-11-04):

- Endpoint `POST https://api.typesafe.ai/v1/systemone`, Bearer key, `TYPESAFE_API_KEY` by convention.
- Questions in one request are evaluated in parallel and in isolation against the same state.
- Input is priced at $0.042 per million tokens; output is free. Context is 64k per request, 32k per question.
- Models: `jev-1.14.0`, `jev-1.13.0`; `jev-latest` is an alias that moves.
- No .NET SDK (Python and JavaScript only); an OpenAPI spec exists.
- Rate limits are per organisation, not published as numbers; `429` and `529` are retried with backoff.
- Their re-rank cookbook reports top-10 hit rate 38% → 62% on a legal corpus. That is their own benchmark.
  No latency figure is published.

Jev is a per-pair judge, not an index. It cannot find entries that candidate generation never returned.

## Decision

Introduce narrow **decision ports** in `AgentBrain.Application`. Each has the current behaviour as its
`Heuristic` implementation and, where it adds value, a `Jev` implementation. Each port is selected
independently by configuration. Heuristics stay the default; no model is contacted unless configured.

### Ports

| Port | Heuristic (today) | Jev | Other |
|---|---|---|---|
| `ICandidateGenerator` | BM25 + structural matches; wide pool (top 100) and "all eligible" when the filtered set is small | none | local embeddings (later) |
| `IRelevanceJudge`: admit, rank, applicability | coverage + threshold + current ranker | `noul` "does it answer the query", `noul`/`score` for applicability; blended with BM25 rank in code | |
| `IQueryUnderstanding`: intent, kind and scope hints as boosts, never hard filters | `QueryAnalyzer` | optional `choice` | |
| `IEntryClassifier`: kind, destination, technology-specific | the agent's explicit choice | `choice` plus a companion `noul` for "none fit" | |
| `IDuplicateJudge`: new / update / supersede / duplicate | `SimilarEntryFinder` | `choice` over the similar entries | |
| `ICaptureGate`: worth remembering | off | `noul`, as in TypeSafe's memory cookbook | |
| `IMaintenanceAnalyzer`: stale, misfiled, promotion candidates | none | bulk `choice`/`noul` | |

The following stay deterministic and are **not** ports: the SQL filter (collections, kinds, tags,
publication, retirement, version compatibility), the byte budget, authorization, `SecretScanner`, identity
and revision handling. Hard rules never become probabilistic.

### Accuracy applies to the first round, not only the second

A judge on a narrow shortlist inherits BM25's recall ceiling. Because input is cheap (about $0.0006 for 100
candidates of ~150 tokens), the design gives the judge a **wide pool**:

- candidate generation returns up to 100 candidates instead of the current small N;
- when the SQL-filtered set is small enough, every eligible entry is scored and BM25 is skipped;
- when BM25 returns few or weak hits, a recall rescue widens the pool, optionally with Jev-proposed query
  reformulations followed by a second BM25 pass;
- relevance and applicability questions are asked together, in parallel, per candidate.

A corpus too large to score per query needs a better candidate generator (embeddings), not a different
judge. That is why `ICandidateGenerator` is its own port.

Requests are made per (query, candidate) pair, as in the cookbook. Packing all candidates into one `state`
with one `noul` per candidate is attractive (one request) but unproven; the evaluation decides (Phase 3).

### Selection and configuration

Added to the layered configuration (user file, then project file; the project wins, as elsewhere):

```json
"decisions": {
  "jev": {
    "apiKeyEnv": "TYPESAFE_API_KEY",
    "model": "jev-1.14.0",
    "timeoutMs": 1500,
    "maxConcurrency": 12
  },
  "relevance": "heuristic | jev | hybrid",
  "classifier": "heuristic | jev",
  "duplicates": "heuristic | jev",
  "captureGate": "off | jev"
}
```

- Defaults are `heuristic` / `off`. `hybrid` blends Jev's answers with the heuristic score; the weights are
  constants documented next to the ranking constants in ADR 0006.
- The composition root resolves each port to an implementation. Application depends only on the ports; the
  Jev client lives in a new `AgentBrain.Infrastructure.Jev` project, so the dependency rules in
  `AgentBrain.Architecture.Tests` hold (infrastructure projects never reference each other).
- **Where a judge runs.** A relevance judge runs where retrieval runs: on the client for local collections
  and on `brain-server` for remote ones, so the server's configuration decides. Write-time classification
  and duplicate checks run client-side, because they choose the destination before `WriteRouter` acts.
  An explicit kind or destination from the caller always wins over the classifier.

### The API key

- The key is read from the environment variable named by `decisions.jev.apiKeyEnv`. The configuration
  stores the variable name, never the key, so nothing secret is written to a collection or committed.
- `brain doctor` reports whether the variable is set and whether the model responds. `brain-server` reads
  it from its environment like its other secrets.
- Logs, the audit log and the query log never contain the key, and never contain entry text sent to Jev.

### Privacy

Sending entry text to a third party is a real change. It is controlled per collection with
`allowExternalDecisions` on the collection's registration in the brain configuration (default `false`; see Implementation notes). Entries from a collection without it are
never sent; such a collection is judged by the heuristic even when `relevance` is `jev`, and the response
says so in a warning. Query text is sent only when at least one eligible collection allows it.

### Reliability, determinism and cost

- **Fallback.** On timeout, `429`/`529` after SDK-style backoff, an invalid response, or confidence below a
  configured threshold, the port falls back to its heuristic and the response carries a warning with the
  reason. A save never fails because a classifier was unavailable.
- **Pinned model.** The configured model is a versioned ID. `jev-latest` is allowed but warned about by
  `brain doctor`, because an alias moving would invalidate evaluation baselines.
- **Cache.** Answers are cached by hash of (model, question, state). Repeats are free and reproducible;
  the evaluation can be replayed offline.
- **Budgets.** A per-operation cap on calls and tokens; exceeding it uses the heuristic for the remainder.
- **AOT.** The shipped `brain.exe` is native AOT with no trim warnings. The Jev client uses `HttpClient` and
  source-generated `System.Text.Json` contexts, with no reflection and no third-party SDK.
- **Retrieval stays correct without Jev.** Filtering, budgeting and authorization are unchanged, so a Jev
  outage degrades ranking quality, never safety.

### Model calls and ADR 0008

ADR 0008 states that Agent Brain makes no model calls. That remains true by default and for every
installation that does not configure `decisions`. When configured, calls go only to the decision model, for
the judgements in the table above, never for text generation. ADR 0008's "generalising is the agent's job"
is unchanged.

## Evaluation: the gate for adopting any Jev implementation

`eval/` already holds the deterministic cases (`lexical`, `applicability`, `contexts`,
`versions-and-lifecycle`) and baselines (38/43 variants, 0 irrelevant, 0 monotonicity violations).

1. **Baseline the heuristic first.** List the cases it gets wrong or ranks poorly and classify each miss:
   vocabulary mismatch, near-miss admission, applicability, threshold tuning. If most misses are tuning,
   fix them deterministically; the judge only has headroom where the misses are semantic.
2. **Add cases the heuristics cannot see:** paraphrases, reworded duplicates, misfiled kinds, ambiguous
   knowledge-vs-memory examples, with labelled expectations.
3. **`--compare heuristic,jev,hybrid` in the evaluation runner** runs the same cases per port and reports accuracy,
   latency (p50/p95), calls and tokens, and estimated cost.
4. **Adoption rule.** A Jev implementation becomes a documented recommendation only if it beats the heuristic
   on accuracy by a margin that justifies its latency and cost, with no new irrelevant admissions and no
   monotonicity violations. Results are recorded in this ADR.

## Phases

| Phase | Work | Needs Jev |
|---|---|---|
| 1 | Ports and `Heuristic` implementations behind them, with no behaviour change; `decisions` config and schema; wide candidate pool and exhaustive-when-small as options; baseline miss analysis | no |
| 2 | `AgentBrain.Infrastructure.Jev`: client, key handling, cache, fallback, budgets; `brain doctor` checks; per-collection privacy flag | yes |
| 3 | `IEntryClassifier`, `IDuplicateJudge` and `ICaptureGate` on Jev; `brain eval --compare`; new labelled cases | yes |
| 4 | `IRelevanceJudge` (wide pool, parallel relevance and applicability) and recall rescue; the one-request packed variant | yes |
| 5 | Embedding `ICandidateGenerator` if the miss analysis shows recall is the limit; `IMaintenanceAnalyzer` | optional |

Phase 1 is useful on its own and carries no external dependency.

## Open questions

- Rate limits and latency are unpublished. Phase 2 measures both before concurrency defaults are fixed.
- Whether `noul` probabilities are calibrated enough to share one threshold across collections or need one
  per collection; the evaluation decides.
- Whether the packed one-request form matches per-pair accuracy.
- Whether to expose a decision endpoint on `brain-server`, so clients without their own key can use the
  server's.

## Not changed

- Filtering, budgeting, authorization and secret scanning stay deterministic.
- Search works with no network and no key; Jev is never required.
- The protocol stays at 1; decisions add configuration and optional warnings only.

## Implementation notes (2026-11-04)

What was built, and where it differs from the decision above.

**Built**
- `IDecisionModel`, a low-level port the ADR did not list: `AskAsync(state, questions)` returning typed answers or
  `provider_unavailable`. Every Jev-backed port consumes it, so the cache, the call budget, the timeout, the retries and
  the usage meter wrap one interface instead of being repeated per port, and tests use a scripted fake.
- Ports in `AgentBrain.Application/Decisions`: `IRelevanceJudge`, `IEntryClassifier`, `IDuplicateJudge`, `ICaptureGate`,
  each with its heuristic. `LocalKnowledgeProvider` calls the relevance judge; `HeuristicRelevanceJudge` is `Ranker.Rank`
  unchanged. `Ranker.Evaluate` exposes every candidate with the heuristic's verdict for model-backed judges.
- `AgentBrain.Infrastructure.Jev`: `JevDecisionModel` (HTTP, retry, deadline, concurrency limit), `CachingDecisionModel`
  (memory plus `local/state/decisions/cache.jsonl`, keyed by hash, never storing state text), the four Jev ports and
  `JevDecisionServices.Create`. The JSON is written and read without reflection, so the AOT build is unaffected.
- Configuration: the `decisions` section and `collections.<id>.allowExternalDecisions`, validated and layered like the
  rest, with `brain config show`, schema and docs. `brain decisions status|classify|gate|allow|deny`, and a `decisions`
  line in `brain doctor`.
- `--relevance` and `--compare` in the evaluation runner, with a `DecisionMeter` for calls, tokens and estimated cost.
- Fallback notes: a `note:` line in search output (and `notes` in JSON) and on writes.

**Differences from the decision**
- **Not built:** `ICandidateGenerator`, `IQueryUnderstanding` and `IMaintenanceAnalyzer`. The first had no second
  implementation (the wide pool is `search.candidatesPerSource` raised to `decisions.jev.relevancePool` when the
  relevance port uses a model); the other two have no consumer yet. The one-request packed variant and the recall rescue
  are not built either, since only a live evaluation can decide them.
- **The classifier is advisory.** `NewEntry.Kind` is always supplied, so the classifier returns a suggestion that
  becomes a `note:` on the save when it disagrees with enough confidence. It runs on direct local saves; proposals and
  remote saves are not classified.
- **`allowExternalDecisions` lives on the collection registration**, not in `collection.json`. The person holding the
  key is the one who opts in, and it needs no change to the collection format. A higher configuration file replaces a
  registration whole, so the flag is set in the file that registers the collection (`brain decisions allow` does this).
- **`brain eval --compare` is the evaluation runner's `--compare`**; the evaluation is not a `brain` command.
- **`brain-server`** (`HostRuntime`) is not wired: it keeps the heuristics. Serving decisions from a host is a follow-up.
- **The capture gate** has no automatic caller yet; it is available as `brain decisions gate`.
- **The classifier and duplicate judge are not gated on a heuristic first**, so a model-backed duplicate check asks once
  per similar entry (at most five).

**Verification**
- 596 tests pass (514 before), including every Jev port against a scripted model, the HTTP client against a stub
  handler, the runtime wiring, privacy filtering, fallback, and that no key or entry body is sent or printed.
- With every port on its heuristic, the evaluation output is identical to the output before the change, case by case.
- **No live Jev call was made.** The prompts, thresholds (`JevRelevanceProfile`) and blend weights are starting values
  taken from the documentation and have not been measured. The adoption rule above is unapplied.
