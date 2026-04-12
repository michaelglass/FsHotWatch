# Changelog — TestPrune.Falco

## [Unreleased]
- refactor: adapt to revised ITestPruneExtension edge-injection interface

## [1.0.2] - 2026-04-11
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

## 0.1.0-alpha.9
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.8
- test: add FalcoRouteExtension unit tests for multi-class and multi-handler scenarios
- refactor: FalcoRouteExtension uses RouteStore port type; extensions take RouteStore instead of Database

## 0.1.0-alpha.7
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.6
- refactor: eliminate mutable accumulators in FalcoRouteAnalysis; hoist regex patterns to module level
- refactor: FalcoRouteAnalysis address code smells — narrow broad catches, fix connection leak
- feat: add SymbolStore/SymbolSink port types used by extension interface

## 0.1.0-alpha.5
- (no Falco-specific changes; version bumped in lock-step with Core)

## 0.1.0-alpha.4
- (no Falco-specific changes; version bumped in lock-step with Core)
