# Changelog — TestPrune

## Unreleased

- fix: detectChanges now filters extern symbols from both sides internally, eliminating phantom diffs on warm FCS restart
- fix: namespace entities are no longer misclassified as Type symbols in tryClassifyEntity, eliminating +1 phantom symbol rows

## 4.0.0 - 2026-04-25
- feat: indexer captures entity-level attributes and containment edges for
  TestPrune.Core's aggregate-type invalidation. No CLI surface changes;
  databases auto-migrate from v4 to v5 on open.
- chore: initial changelog; bump upstream tool versions
