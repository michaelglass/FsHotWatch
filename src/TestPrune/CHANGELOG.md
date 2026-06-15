# Changelog — TestPrune

## Unreleased

## 4.2.3 - 2026-06-16

- fix: editing a test's own body now re-selects that test, so `test-prune run`
  re-executes a test you just changed instead of skipping it as unaffected (via
  TestPrune.Core's `QueryAffectedTests` seed-inclusion fix).

## 4.2.2 - 2026-06-12

- fix: `test-prune` impact analysis no longer mis-attributes or drops dependency
  edges when two bindings share a short name across sibling nested modules in one
  file, which could silently skip affected tests (via TestPrune.Core).
- fix: comment-only edits next to triple-quoted strings containing embedded `"` no
  longer produce phantom "changed" signals in impact analysis (via TestPrune.Core's
  `stripComments` fix).
- fix: failures reading a test process's redirected output now surface the original
  IO exception instead of an `AggregateException` wrapper (via TestPrune.Core).

## 4.2.1 - 2026-06-07

- chore: release alongside TestPrune.Core (public `Database.SchemaVersion` for external
  read-only compatibility probes). No CLI-facing changes.

## 4.2.0 - 2026-06-05

- chore: bundle TestPrune.Core 4.2.0 (`Database.WasRecreated`, which lets downstream
  consumers invalidate stale sibling caches after a schema-bump DB rebuild). No change
  to CLI behavior.

## 4.1.0 - 2026-06-04

- chore: version bump alongside TestPrune.Core 4.1.0 (which adds edit-aware coverage
  storage). No `test-prune` CLI-facing changes — the coverage API is library-level.

## 4.0.3 - 2026-06-02

- fix: `test-prune` impact analysis no longer crashes on un-nameable F# symbols (e.g. anonymous-record projections) in analyzed sources — the AST walk skips the un-nameable symbol and continues (via TestPrune.Core).

## 4.0.2 - 2026-05-27

- chore: update external NuGet dependencies — Microsoft.Data.Sqlite 10.0.5→10.0.8,
  Microsoft.SourceLink.GitHub 10.0.201→10.0.300, Microsoft.Testing.Extensions.CodeCoverage
  18.6.2→18.7.0. Pinned FSharp.Core to 10.1.204 (was floating `10.1.*`, which drifted to
  10.1.300 and broke restore: FSharp.Compiler.Service 43.12.204 hard-pins FSharp.Core to `[10.1.204]`).

## 4.0.1 - 2026-05-04

- fix: detectChanges now filters extern symbols from both sides internally, eliminating phantom diffs on warm FCS restart
- fix: namespace entities are no longer misclassified as Type symbols in tryClassifyEntity, eliminating +1 phantom symbol rows

## 4.0.0 - 2026-04-25
- feat: indexer captures entity-level attributes and containment edges for
  TestPrune.Core's aggregate-type invalidation. No CLI surface changes;
  databases auto-migrate from v4 to v5 on open.
- chore: initial changelog; bump upstream tool versions
