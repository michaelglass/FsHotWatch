# Changelog — TestPrune.Core

## Unreleased
- fix: add semantic-tagger.json with CLI under core's shared tag
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
