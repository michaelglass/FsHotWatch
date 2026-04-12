# FCS Integration & Status Provenance Design

## Problem

The SQL coupling infrastructure (types, DB, coupling engine, extension interface) is in
place, but two key automation pieces are missing:

1. The Sql extension requires manually-provided `SqlFact` lists — it can't automatically
   discover `[<ReadsFrom>]`/`[<WritesTo>]` attributes in source code.
2. The SqlHydra extension has DSL classification and table parsing but doesn't actually
   analyze the dependency graph to produce `SqlFact` lists from SqlHydra query patterns.
3. The `status` command doesn't show provenance — users can't see which edge sources
   (core, sql, falco) contributed to test selection.

## Design Principle

**Extensions never touch FCS.** Core provides all the raw data (symbols, dependencies,
attributes), extensions post-process it. FCS stays a core-only concern.

## A: Core — Generic Attribute Extraction

### New table

```sql
CREATE TABLE IF NOT EXISTS symbol_attributes (
    symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    attribute_name TEXT NOT NULL,
    args_json TEXT NOT NULL DEFAULT '[]',
    PRIMARY KEY (symbol_id, attribute_name, args_json)
);
```

### New SymbolStore query

```fsharp
GetAttributesForSymbol: string -> (string * string) list
```

Returns `(attributeName, argsJson)` pairs for a given symbol full name.

### Core AstAnalyzer change

During `extractResults`, for every `FSharpMemberOrFunctionOrValue` symbol, extract all
attributes and their constructor arguments. Store as
`(symbolFullName, attributeName, argsJson)` triples. `argsJson` is a JSON array of
constructor arg values.

Core already iterates all symbols and reads attributes for test detection. This
generalizes that to capture all attribute data.

SchemaVersion bumps to 3.

## B: Sql Extension — Automatic Attribute-Based Fact Extraction

With attribute data in the DB, `SqlExtension.AnalyzeEdges` discovers facts automatically:

1. Receives `SymbolStore`
2. Queries all symbols with `ReadsFromAttribute` or `WritesToAttribute` attributes
3. Parses `argsJson` to extract table/column args
4. Builds `SqlFact` list
5. Feeds into `SqlCoupling.buildEdges`

`SqlExtension()` takes no constructor arguments. Facts are discovered, not provided.

## C: SqlHydra Extension — Graph-Based Fact Extraction

SqlHydra post-processes the existing dependency graph. For each function symbol, checks
its outgoing edges:

1. Does it have a `Calls` edge to a SqlHydra DSL function (`selectTask`, `insertTask`,
   `updateTask`, `deleteTask`)?
2. Does it have a `UsesType` edge to a SqlHydra-generated table type (matching
   `Generated.schema.table` pattern)?

If both: create a `SqlFact` with the function as the symbol, the table from the type
reference, and the access kind from the DSL function name.

**Example:** If the graph has:
- `BriefQueries.getActiveBriefs` → `Calls` → `selectTask`
- `BriefQueries.getActiveBriefs` → `UsesType` → `Generated.public.briefs`

Produces: `{ Symbol = "BriefQueries.getActiveBriefs"; Table = "briefs"; Column = "*"; Access = Read }`

Column-level tracking deferred — start with table-level (`*`). Constructor takes a
pattern to identify SqlHydra generated modules (e.g., `"Generated"` or configurable).

## D: Status Provenance

When the `status` command lists affected tests, show the set of edge sources involved
in the selection path. For each affected test, query all edges in the transitive closure
between changed symbols and the test, collect distinct `source` values:

```
Would run 1 test(s):
  MyTests:
    Tests
      - testA  [sources: core, sql]
```

Full path tracing (showing the exact edge chain) is a follow-up.

## Implementation Notes

- All three streams (A/B, C, D) are independent and can be implemented in parallel.
- A must land before B (B depends on attribute data in DB).
- C depends only on existing `SymbolStore` queries.
- D depends only on the `source` field already in the `dependencies` table.
- Use TDD throughout.
