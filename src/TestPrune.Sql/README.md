<!-- sync:testprune-sql-readme:start -->
# TestPrune.Sql

Add explicit database shared-state dependencies to a TestPrune symbol graph.
When one symbol writes a table or column and another reads it, the extension
adds a `SharedState` edge so a writer change selects tests that reach the
reader. A test method is never a writer: rows a test seeds exist only inside that
test, so no reader depends on them. A test that reads a table still depends on its
writers.

```bash
dotnet add package TestPrune.Sql
```

## Attribute discovery

Annotate module functions or values, then register `AutoSqlExtension` with the
host that invokes `ITestPruneExtension` after indexing:

```fsharp
open TestPrune.Sql
open TestPrune.Extensions

[<WritesTo("public.articles", "status")>]
let markComplete connection articleId =
    // ...

[<ReadsFrom("public.articles", "status")>]
let listComplete connection =
    // ...

let extension: ITestPruneExtension =
    AutoSqlExtension() :> ITestPruneExtension
```

The one-argument attribute form uses the wildcard column `*`. A wildcard
matches every column on the same table. Use schema-qualified table identities
such as `public.articles` when facts must interoperate with
`TestPrune.SqlHydra`; equal table names in different schemas otherwise cannot
be distinguished.

For facts obtained outside source attributes, construct `SqlFact` values and
register `SqlExtension(facts)` instead. Registration is explicit: merely
referencing this package does not run either extension. The host stores their
edges with `refreshExtensionEdges` on every index build.

## Tests with their own database state

When every test starts from its own database state (truncated and re-seeded per
test or per fixture), rows a writer stores are visible only to the test that ran
the writer. A changed writer can then affect a test that reads its table only if
that test also executes the writer. Open the index with the reachability-gated
policy to select on that basis:

```fsharp
let db = TestPrune.Database.Database.create (dbPath, SqlCoupling.reachabilityGated)
```

A writer change then selects a test through a reader only when the test reaches
the writer through its other edges: calls, routes, dispatch, or a fixture that
seeds through it. The stored edges are the same under either policy. Keep the
default (`WalkPolicy.FollowAll`) when tests share database state, for example rows
written by another test or a seed snapshot built outside any test.

See the repository's [integration guide](https://github.com/michaelglass/TestPrune/blob/main/docs/integration.md#extensions)
for extension-host wiring and edge direction.
<!-- sync:testprune-sql-readme:end -->
