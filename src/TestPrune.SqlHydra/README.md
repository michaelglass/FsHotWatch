<!-- sync:testprune-sqlhydra-readme:start -->
# TestPrune.SqlHydra

Automatically add table-level database shared-state dependencies to a
TestPrune graph for typed SqlHydra queries.

```bash
dotnet add package TestPrune.SqlHydra
```

Construct the extension with the fully qualified module prefix from the
generated `DbTypes.fs`, then register it with the host that invokes
`ITestPruneExtension` after core indexing:

```fsharp
open TestPrune.Extensions
open TestPrune.SqlHydra

let extension: ITestPruneExtension =
    SqlHydraExtension("MyApp.Database.Generated") :> ITestPruneExtension
```

Registration is explicit: installing the package does not make the extension
run. The prefix must be a dot-separated qualified name without empty segments;
invalid input fails during construction rather than silently disabling SQL
attribution.

The extension recognizes the generated table value named exactly
`<prefix>.<schema>.<table>` and classifies calls owned by SqlHydra's select,
insert, update, and delete builders. Its state identity retains the schema
(`public.articles`), and attribution is table-level. A query that performs
several access kinds or touches several tables is handled conservatively so it
may select extra tests but cannot discard a genuine reader/writer edge.

Broad generated-module and enum type uses are deliberately ignored; they are
not tables. Manual `TestPrune.Sql` facts intended to join generated facts must
use the same schema-qualified table identity.

See the repository's [SqlHydra integration guide](https://github.com/michaelglass/TestPrune/blob/main/docs/integration.md#sqlhydra-table-coupling)
for the graph contract and host persistence sequence.
<!-- sync:testprune-sqlhydra-readme:end -->
