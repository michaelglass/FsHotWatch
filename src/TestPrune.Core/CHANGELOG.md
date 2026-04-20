# Changelog — TestPrune.Core

## [Unreleased]
- fix: bump `SchemaVersion` 3 → 4. The 3.0.0 release introduced
  `dependencies.source`, `symbol_attributes`, and `symbols.is_extern` under
  the same v3 stamp that 2.0.0 used, so any DB written by 2.0.0 survived
  `openCheckedConnection` (version matched) and then crashed on the first
  INSERT with `"table dependencies has no column named source"`. Plugin
  hosts (FsHotWatch, etc.) deadlocked because the plugin never reached
  terminal status. Bumping forces auto-recreate of any stamped-v3 DB on
  open.
- fix: checkpoint the WAL after `RebuildProjects` commits so fresh connections
  in the same process don't momentarily observe an empty DB.
- **BREAKING** — `SymbolSink.RebuildProjects` signature changed from
  `AnalysisResult list -> (string * string) list -> (string * string) list -> unit` to
  `AnalysisResult list -> CacheKeys -> unit`, where `CacheKeys = { FileKeys; ProjectKeys }`.
  Prevents accidentally swapping file keys and project keys at call sites (both were
  `(string * string) list`). Use `CacheKeys.Empty` when neither is relevant.
- fix: `RebuildProjects` now preserves incoming dependency edges when a file is re-indexed
  incrementally. The old code did `DELETE FROM symbols WHERE source_file IN (...)` which,
  combined with `ON DELETE CASCADE` on `dependencies.to_symbol_id`, destroyed every edge
  from other (non-re-indexed) files pointing into the re-indexed file's symbols — causing
  `QueryAffectedTests` to return 0 even when dependent tests clearly existed. Now uses
  UPSERT (`INSERT … ON CONFLICT(full_name) DO UPDATE SET …`) to preserve row ids for
  surviving symbols. Orphan cleanup is timestamp-driven: every symbol touched this pass
  gets `indexed_at = now`; a single `DELETE … WHERE source_file IN (…) AND indexed_at < @now`
  sweeps away symbols that genuinely disappeared from source. Extern inserts use a
  conditional UPSERT (`ON CONFLICT DO UPDATE SET indexed_at = excluded.indexed_at WHERE
  symbols.is_extern = 1`) so they bump their own timestamps without overwriting real
  symbols. Includes regression test `re-indexing library file preserves incoming edges
  from non-re-indexed tests`.
- refactor: add `DiffParser.isFsproj` helper; remove duplicated `.fsproj` extension checks
  across `DiffParser`, `ImpactAnalysis`, and `Orchestration`.
- feat: auto-recreate database when schema version is incompatible with current build
- feat: add SharedState dependency kind for cross-test coupling via shared resources
- feat: revise ITestPruneExtension to inject edges into dependency graph
- feat: add TestPrune.Sql package with ReadsFrom/WritesTo attributes and SQL coupling engine
- feat: add TestPrune.SqlHydra package with graph-based SqlHydra query pattern detection
- feat: generic symbol attribute extraction from FCS during analysis (schema v3)
- feat: AutoSqlExtension auto-discovers ReadsFrom/WritesTo from indexed attributes
- feat: show edge source provenance (core, sql, sql-hydra, falco) in status output
- refactor: extract DB placeholder helpers, batch attribute queries, single-pass extraction
- chore: add SourceLink, symbol packages, and NuGet packaging metadata to Sql and SqlHydra projects

## [2.0.0] - 2026-04-11
- feat: cross-project dependency extraction via extern symbols
- feat: add ExternRef SymbolKind for honest extern symbol classification
- feat: add TestExecutor DI record for injectable test execution in runRunWith
- fix: exit code bug where later test project results overwrote earlier failures
- fix: add warnings for unknown DB enum deserialization instead of silent fallback
- refactor: move warnedUnknownKinds to Database instance for proper test isolation
- refactor: simplify extern symbol handling (HashSet dedup, ExternSourceFile constant)
- test: improve coverage across Orchestration (86%→98%), Program (39%→50%), Database, AstAnalyzer
- test: fix test parallelization — Console-mutating tests use xUnit Collection to serialize
- fix: add semantic-tagger.json with CLI under core's shared tag
- fix: trigger docs deploy on release tags, not push to main
- chore: update NuGet dependencies to latest versions
- chore: bump local tool versions (coverageratchet, fssemantictagger, syncdocs, fsprojlint) to latest alpha

## 1.0.1
- fix: replace bespoke CI with shared NuGet tools and reusable workflows
- fix: workflow cleanup from code review
- chore: add NuGet Trusted Publishing comment, set check-docs: false for AnalyzerShim
- chore: remove leftover scripts/ directory replaced by shared tools
- feat: use auto-discovering example-projects in CI workflow
- note: version bumped to 1.0.1 to avoid accidental publication of reserved 1.0.0

## 0.1.0-beta.1
- feat: add indexing benchmarks and enable TransparentCompiler
- feat: add bench tasks to CLAUDE.md

## 0.1.0-alpha.9
- fix: use CLR nested type separator (+) for test classes inside modules
- refactor: extract printTestResult helper, route stderr to eprintfn
- refactor: separate stdout and stderr in TestResult

## 0.1.0-alpha.8
- fix: include type definition ranges in findEnclosing for interface edges
- test: add dead code false positive regression tests
- feat: track type member functions in impact graph + add analysis diagnostics
- test: add regression tests for this self-identifier and cross-project type member chain

## 0.1.0-alpha.7
- feat: surface build stdout on failure for better diagnostics
- feat: print build stderr on failure for better diagnostics
- feat: add process duration logging to runProcess in TestRunner; use async reads to prevent deadlock
- fix: add 10-minute timeout and duration logging to dotnetBuildRunner; use async reads to prevent deadlock
- fix: stop stopwatch before stream drain in runProcess for accurate timing
- fix: serialize GetProjectOptionsFromScript with SemaphoreSlim to prevent FCS corruption
- fix: pass CancellationToken to SemaphoreSlim.WaitAsync; strengthen concurrency test
- fix: resolve relative paths to absolute before passing to FCS in getScriptOptions
- fix: guard null baseDir and empty path in resolveToAbsolute; add edge case tests
- refactor: simplify — remove new on SemaphoreSlim, WHY comments, avoid alloc in resolveReferenceOptions

## 0.1.0-alpha.6
- feat: comment-insensitive and layout-normalized content hashing
- feat: SQLite audit trail — persist analysis events with run ID
- feat: add InMemoryStore and migrate ImpactAnalysisTests to pure in-memory tests
- feat: TestSelection uses SelectionReason DU instead of raw strings
- feat: add SymbolStore/SymbolSink port types and adapter in Ports.fs
- feat: wire AuditSink into orchestration — events flow from pure core through sink
- feat: add --parallelism flag for configurable concurrent analysis
- feat: add AuditSink with MailboxProcessor-based event persistence
- feat: SymbolDiff.detectChanges, DeadCode.findDeadCode, ImpactAnalysis.selectTests now emit AnalysisEvents
- feat: add Domain.fs with typed errors, selection reasons, and analysis events
- refactor: functional core — eliminate shared mutable state, use immutable ProjectResult + fold
- refactor: orchestration uses port types (SymbolStore/SymbolSink) instead of Database directly
- test: add real-source E2E integration tests for SymbolDiff, impact analysis, and dead code

## 0.1.0-alpha.5
- feat: detect cross-file dependencies by analyzing open statements
- test: validate cross-file dependency detection; improve coverage

## 0.1.0-alpha.4
- feat: cross-file dependency detection via open statement analysis (initial)
