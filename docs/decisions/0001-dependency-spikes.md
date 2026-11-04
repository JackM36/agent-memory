# 0001 — Dependency spikes (Phase 0)

- **Status:** accepted
- **Date:** 2026-11-02
- **Plan:** §4, §9.1

## Context

Every risky dependency had to be confirmed under Native AOT before code relied on it (plan §9.1).
The spikes were throwaway console apps published with `PublishAot=true` for `win-x64` on the owner's
Windows 11 machine, .NET SDK 10.0.403, MSVC Build Tools 18. They were deleted after this record was written.

## Results

| Dependency | Version | AOT result | Notes |
|---|---|---|---|
| Microsoft.Data.Sqlite (bundled e_sqlite3) | 10.0.14 | Works, no warnings | SQLite 3.53.3. FTS5 is compiled in: `fts5` virtual table, `porter unicode61 remove_diacritics 2` tokenizer, `bm25()` with column weights all work. A porter query for "serializing" matched "serialization". |
| YamlDotNet (representation model) | 18.1.0 | Works, no warnings | `YamlStream` node model only. |
| Markdig | 1.5.1 | Works, no warnings | Headings inside fenced code are not reported as headings. |
| System.CommandLine | 2.0.14 | Works, no warnings | |
| Tomlyn | 2.10.1 | Works with the syntax API | The 2.x serializer (`TomlSerializer.Deserialize`) is reflection-based and produces IL2026/IL3050. `Tomlyn.Parsing.SyntaxParser.Parse` returns a round-trippable syntax tree and is AOT-clean; it also preserves formatting, which is what merging into a user's `.codex/config.toml` needs. |
| ModelContextProtocol (C# SDK) | 2.3.0 (stable) | Works, no warnings | Stdio server with `WithTools<T>(JsonSerializerOptions)` and a source-generated `JsonSerializerContext`. `initialize`, `tools/list` and `tools/call` round-tripped; stdout carried only protocol frames, all logging went to stderr. The hand-rolled JSON-RPC fallback is not needed. |
| Microsoft.Extensions.Hosting | 10.0.14 | Works, no warnings | Used by the MCP host. |

## Measurements (win-x64, same machine)

| Binary | Size | Time |
|---|---|---|
| Core spike (SQLite open + FTS5 query + YAML + Markdig + TOML + command line) | 5.4 MB | First run 142 ms wall, then 55 ms wall per run (includes Git Bash process launch); in-process work 2–31 ms |
| MCP stdio spike | 11.3 MB | — |

These are baselines, not targets. Phase 2 re-measures with real indexes (plan §5.14).

## Findings that change implementation

1. **Windows AOT needs `vswhere.exe` on `PATH`.** The ILCompiler targets call `vswhere` to find the
   MSVC linker. With Build Tools installed but `C:\Program Files (x86)\Microsoft Visual Studio\Installer`
   missing from `PATH`, publishing fails with "'vswhere.exe' is not recognized". Developer machines
   need that directory on `PATH` (or a Developer PowerShell). CI images already have it.
2. **Atomic replace fails while another process has the target open.** `File.Move(tmp, target, overwrite: true)`
   threw `UnauthorizedAccessException` both with a reader holding `FileShare.Read` and with
   `FileShare.ReadWrite | FileShare.Delete`. The bounded retry in plan §5.12 is required, and after the
   retry window the write must fail with a clear error instead of falling back to an in-place write.
3. **Invariant globalization does not decompose Unicode.** `string.Normalize(FormKD)` left `ü` and `ß`
   intact with `InvariantGlobalization=true`. Slugs therefore use an explicit Latin folding table
   (`LatinFolding`), which also makes results identical on every platform (ADR 0002).

## Decision

Adopt the versions above in `Directory.Packages.props`. Use Tomlyn only through its syntax API.
Keep `InvariantGlobalization=true` (smaller binaries, no ICU dependency in containers).

## Not verified here

- macOS and Linux AOT publishes: covered by CI (`.github/workflows/ci.yml`), not run locally.
- AOT for the HTTP server: not needed (plan §4); the server is published framework-dependent.
