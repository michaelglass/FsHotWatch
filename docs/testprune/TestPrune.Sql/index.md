<!-- sync:testprune-sql-readme -->
# TestPrune.Sql

Add explicit database shared-state dependencies to a TestPrune symbol graph.
When one symbol writes a table or column and another reads it, the extension
adds a `SharedState` edge so a writer change selects tests that reach the
reader.

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
referencing this package does not run either extension. The host persists the
edges returned by `AnalyzeEdges` alongside the core graph.

See the repository's [integration guide](https://github.com/michaelglass/TestPrune/blob/main/docs/integration.md#extensions)
for extension-host wiring and edge direction.
<!-- sync:testprune-sql-readme:end -->
