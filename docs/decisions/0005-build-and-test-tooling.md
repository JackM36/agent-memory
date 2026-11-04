# 0005 — Build and test tooling

- **Status:** accepted
- **Date:** 2026-11-02
- **Plan:** §2, §3, §11

## Decisions

- **Test runner: xUnit v3 on Microsoft.Testing.Platform.** xUnit v3 4.x no longer runs under the
  VSTest bridge with the .NET 10 SDK, so `global.json` opts into the MTP `dotnet test` experience
  (`"test": { "runner": "Microsoft.Testing.Platform" }`). Run tests with
  `dotnet test --solution AgentBrain.slnx`. `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`
  are not used.
- **Test projects exist only once they have tests.** MTP fails a test project that runs zero tests
  (exit code 8), so the plan's empty Phase 0 test projects for ProviderContract, Sqlite and
  Integrations are created in the phases that fill them. `AgentBrain.Contracts.Tests` and
  `AgentBrain.Infrastructure.Markdown.Tests` were added in Phase 1 (they were not in plan §3).
- **Dependency rules** are enforced by `AgentBrain.Architecture.Tests`, which reads each `src`
  project's declared project and package references. It was verified to fail on an injected
  `Domain → Markdig` package reference and a `Domain → Cli` project reference, and to pass after removal.
- **Analyzer adjustments** (`.editorconfig`), each with its reason:
  - CA1716 off: the product is C# only; a type named `Error` is fine.
  - CA1000 off: `Result<T>.Success/Failure` factories are the idiomatic result-type shape.
  - CA1711 off: "collection" is a domain noun (a knowledge collection).
  - CA2007 off: no host has a synchronization context.
  - Test projects additionally relax CA1707 (underscored test names), CA1515, CA1861.
- **Phase 2 work pulled into Phase 1:** `FrontmatterParser` and `CollectionFileReader` in
  `Infrastructure.Markdown`, so a test can validate the evaluation corpus against the real schema
  before the gate review.
