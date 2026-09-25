# Changelog — TestPrune.Sql

## Unreleased

## 0.3.0 - 2026-09-25

- chore!: `SqlExtension` and `AutoSqlExtension` implement TestPrune.Core's
  `AnalyzeEdges: SymbolStore -> repoRoot` (no `changedFiles`). They already returned
  edges for the whole tree, so the edges themselves do not change.

## 0.2.0 - 2026-09-24

- fix!: a test method is never a shared-state writer. A test that seeds its own rows
  writes them only for itself, yet every reader of the table was coupled to it, so the
  impact walk, reaching the test through anything it calls, went on from it into every
  reader of every table it seeds and all of their tests. In one application a change
  to one case of a seven-case union selected 1527 tests; with test methods excluded as
  writers the same walk selects 325. A test method is still a reader: a test that reads
  a table depends on its writers. BREAKING: `SqlCoupling.buildEdges` takes the test
  method names first; `SqlExtension` and `AutoSqlExtension` pass their store's
  (`SymbolStore.GetTestMethodSymbolNames`).

## 0.1.0 - 2026-08-28
- feat: initial release of manual read/write facts, declaration attributes, and
  schema-aware shared-state coupling for extension hosts.
- chore: initial changelog; bump upstream tool versions
