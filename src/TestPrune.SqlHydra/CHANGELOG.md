# Changelog — TestPrune.SqlHydra

## [Unreleased]
- fix: derive tables from the generated table value's `Calls` edge instead of
  broad `UsesType` edges. Real SqlHydra graphs also carry type-use edges to the
  schema module and generated enums; the old heuristic interpreted those as
  tables and could couple nearly every query through a bogus `public` resource.
  Generated values must now match the configured prefix on a namespace boundary
  with exactly `<schema>.<table>` beneath it. Shared-state identities retain the
  schema (`public.articles`), preventing equal table names in different schemas
  from coupling. Consumer-shaped intervention coverage proves a changed writer
  selects the same-schema reader test and not the other-schema test. DSL access
  calls must now be owned by the supported `SqlHydra.Query` builder namespace;
  an unrelated method that merely ends in `updateTask` no longer manufactures a
  write fact. Manual `TestPrune.Sql` facts that should join these generated facts
  must use the same schema-qualified identity.
- fix: keep *every* SQL access a symbol performs, not just the first. `extractFacts`
  took `List.tryHead` over a symbol's DSL calls, so a symbol that both reads and
  writes (an upsert-style `select`-then-`insert`) was recorded with only one access
  and the other was silently dropped — and which one survived was decided by SQLite
  row order, since `GetDependenciesFromFile` has no `ORDER BY`. When the dropped
  access was the *write*, the table had no writer at all, its readers got no
  `SharedState` edge, and changing the writer selected **none** of the tests that
  read the table (under-selection). Now exact for the common single-access symbol and
  conservatively (access × table) for a genuinely mixed one; edges are only ever
  added, so no affected test can be dropped.
- chore: initial changelog; bump upstream tool versions
