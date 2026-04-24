# Changelog — TestPrune

## [Unreleased]
- feat: indexer now captures entity-level attributes and the containment graph
  needed by TestPrune.Core's aggregate-type invalidation. No user-visible CLI
  changes; a re-index is required on upgrade (auto-recreate handles v4 → v5).
- chore: initial changelog; bump upstream tool versions
