# SQL Coupling & Extension System Design

## Problem

TestPrune's AST-based symbol dependency graph tracks direct code references
(calls, type usage, pattern matches). This misses implicit coupling through
shared database state: when function A writes to a table and function B reads
from it, there's no symbol edge between them. In codebases with many query
modules communicating through database tables (e.g., job pipelines), this is
the largest gap in test impact analysis.

A secondary gap is that the extension interface (`ITestPruneExtension`)
currently returns `AffectedTest` lists, bypassing the graph. Extensions can't
inject edges, so their contributions aren't traceable through provenance.

## Design

### Core Changes

**New DependencyKind variant:**

```fsharp
type DependencyKind =
    | Calls
    | UsesType
    | PatternMatches
    | References
    | SharedState        // new: coupling through shared external state
```

**Source attribution on edges:**

Add a `source: string` field to `Dependency`. Every edge records who produced
it:
- `"core"` — AST analysis
- `"falco"` — Falco route extension
- `"sql"` — manual SQL attributes
- `"sql-hydra"` — automated SqlHydra analysis

Store this in the SQLite `dependencies` table. Surface it in `status` command
output so users can trace why a test was selected.

**Revised extension interface:**

```fsharp
type ITestPruneExtension =
    abstract Name: string
    /// Inject additional edges during indexing.
    abstract AnalyzeEdges:
        symbolStore: SymbolStore -> changedFiles: string list -> repoRoot: string -> Dependency list
```

Extensions return `Dependency` lists (with kind and source) instead of
`AffectedTest` lists. Core's existing transitive closure walks all edges
uniformly regardless of source.

### TestPrune.Sql

A new package defining attributes for manual database access declaration and
the coupling engine that processes them.

**Attributes:**

```fsharp
[<ReadsFrom("articles", "status")>]
[<ReadsFrom("articles", "user_id")>]
let getActiveArticles conn = ...

[<WritesTo("articles", "status")>]
let markArticleComplete conn articleId = ...
```

- One attribute per table/column pair. Stack multiple attributes for multiple
  accesses.
- Table-only shorthand: `[<ReadsFrom("articles")>]` (no column) means "reads
  any column" — coarser, fewer annotations.
- Attributes go on `let`-bound functions or module-level values.

**Coupling engine:**

1. During indexing, scan AST for `ReadsFrom` / `WritesTo` attributes.
2. Build a map: `(table, column) -> {readers: symbol list, writers: symbol list}`.
3. For each (table, column) pair, inject `SharedState` edges between every
   writer and every reader.
4. Edges carry `source = "sql"`.

**Programmatic API:**

TestPrune.Sql exposes an API for other plugins to submit read/write facts
without requiring attributes in source code. This is the seam SqlHydra uses.

### TestPrune.SqlHydra

A new package that automates what the manual attributes declare. References
TestPrune.Sql.

**How it works:**

1. Identify the generated `DbTypes.fs` module (by convention or config).
2. Scan query modules for references to SqlHydra table types
   (e.g., `` `public`.briefs ``) and column accesses (e.g., `d.status`).
3. Classify read vs. write by enclosing DSL context:
   - `selectTask` / `selectAsync` -> ReadsFrom
   - `insertTask` / `insertAsync` -> WritesTo
   - `updateTask` / `updateAsync` -> WritesTo (columns from `set` clauses)
   - `deleteTask` / `deleteAsync` -> WritesTo
4. Feed facts into TestPrune.Sql's programmatic API.
5. Edges carry `source = "sql-hydra"`.

**Deferred refinement:** Distinguishing columns in `set` vs. `where` clauses
for updates (where-columns are reads, set-columns are writes). Start with
table-level write attribution for updates — conservative and safe.

### Falco Migration

Refactor `TestPrune.Falco` from returning `AffectedTest` lists to returning
`Dependency` lists with `source = "falco"`. This is a breaking change to the
extension interface, but there are only two consumers (Falco and soon Sql).

Benefits:
- Falco contributions become traceable through provenance.
- Route-to-handler relationships participate in the transitive closure
  naturally.
- Falco benefits from any core improvements to the graph (e.g., SQL coupling
  may reduce what Falco needs to cover).

### Granularity

Two levels of table access tracking:

- **Table-level:** "if any query writing to `articles` changes, run all tests
  reading from `articles`." Coarse, zero false negatives, more false positives.
- **Column-level:** Track specific columns per query module. More precise,
  fewer false positives on hot tables that everything touches.

Both levels are supported. Column-level is opt-in refinement over table-level.

## Implementation Notes

- Use TDD throughout.
- Core changes (source field, SharedState kind, revised extension interface)
  come first since Sql, SqlHydra, and Falco migration all depend on them.
- TestPrune.Sql depends only on TestPrune.Core.
- TestPrune.SqlHydra depends on TestPrune.Sql and TestPrune.Core.
- Falco migration can happen in parallel with Sql plugin work.
