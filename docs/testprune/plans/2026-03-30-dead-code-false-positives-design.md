# Dead Code False Positives: Design

## Problem

`test-prune dead-code` reports ~108 potentially unreachable symbols on real projects, but only ~1 is genuinely dead. The dependency graph built by `AstAnalyzer.fs` is missing edges for several common F# patterns.

## Approach

Two workstreams: add verbose diagnostics for observability, and fix the highest-impact missing edge patterns.

## 1. Verbose Diagnostics

Add `--verbose` flag to dead-code output. For each unreachable symbol, report WHY:

```fsharp
type UnreachabilityReason =
    | NoIncomingEdges
    | DisconnectedFromEntryPoints of incomingFrom: string list
```

- **NoIncomingEdges**: No symbol in the graph depends on this one. Likely genuinely dead, or a missing edge.
- **DisconnectedFromEntryPoints**: Has incoming edges but the chain doesn't reach any entry point. Missing edge is upstream.

Implementation: In `DeadCode.fs`, query the DB for incoming edges to each unreachable symbol. Add reason to `DeadCodeResult.UnreachableSymbols` entries.

## 2. Edge Fixes in AstAnalyzer

### 2a. DU parent type from case usage

When an `FSharpUnionCase` is used (pattern match or construction), also emit a `UsesType` edge to the parent DU type via `ReturnType.TypeDefinition`.

### 2b. Generic type parameters

When a symbol use involves a generic type instantiation, inspect the `FSharpType` for type arguments and emit `UsesType` edges to each concrete type argument's definition entity.

### 2c. Record type from field usage

When a record field is used (an `FSharpMemberOrFunctionOrValue` that's a property with a `DeclaringEntity` that `IsFSharpRecord`), emit an additional `UsesType` edge to the record type.

### 2d. DllImport exclusion

Exclude symbols with `[<DllImport>]` attribute from dead-code reporting rather than fixing edges. Add an `IsExtern` flag to `SymbolInfo` during indexing, filter in `DeadCode.fs`.

## 3. Deferred

- Interface dispatch (interface method call -> implementors): Hard, requires tracking all implementors. Verbose diagnostics will help investigate.
- Module binding attribution (#4 from TODO): Needs deeper investigation of closure naming.

## 4. Test Suite

### AstAnalyzer-level (edge extraction from real F# source)

- Generic type params extracted as edges
- Multiple generic args each get edges
- Nested generics (e.g., `Option<List<MyType>>`)
- DU case usage creates edge to parent type
- Record field usage creates edge to record type

### Dead-code integration (end-to-end reachability)

- DU type reachable when only cases are used
- Record type reachable when only fields are used
- Generic type arg reachable through instantiation
- DllImport functions excluded
- Verbose: NoIncomingEdges vs DisconnectedFromEntryPoints

### Test impact (changed symbol -> affected tests)

- 1 changed symbol -> 1 affected test
- 1 changed symbol -> 2 affected tests (shared dep)
- Generic type param changed -> test using generic affected
- DU type changed -> test pattern-matching cases affected
- Record type changed -> test constructing records affected

## Validation

Re-run against real project after each fix. Target: 108 false positives -> <10.
