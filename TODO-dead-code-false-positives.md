# Dead Code Analysis: Reducing False Positives

## Problem

Running `test-prune dead-code` on FsHotWatch reports 108 "potentially unreachable" symbols,
but only 1 was genuinely dead (`PluginResult`). The rest are false positives — core types
and functions that ARE reachable but whose dependency edges aren't captured by the analyzer.

## Root Cause

The analyzer (`AstAnalyzer.fs`) builds a dependency graph from `GetAllUsesOfAllSymbolsInFile()`.
Reachability analysis (`GetReachableSymbols`) does transitive closure on forward edges from
entry points. Symbols not reached are reported as dead.

False positives occur when the dependency graph is **missing edges**. The analyzer captures
direct symbol uses (function calls, type annotations, pattern matches) but misses several
common F# dependency patterns.

## Specific Missing Edge Patterns (found empirically)

### 1. Type used as generic parameter → no edge to the type

When `Agent<BuildState, BuildMsg>` is constructed, the analyzer records an edge to `Agent`
but NOT to `BuildState` or `BuildMsg`. These types appear unreachable even though they're
essential to the generic instantiation.

**Fix:** When processing a symbol use that's a generic type (e.g., `Agent<'S, 'M>`),
also record edges to each concrete type argument.

### 2. Record types used only via construction → edges to fields but not the type

When `{ Phase = IdlePhase(...) }` is written, the analyzer may record edges to `Phase`
and `IdlePhase` but not to the record TYPE that contains `Phase` (e.g., `BuildState`).
The type itself appears unreachable.

**Fix:** When a record field is used, also add an edge to the containing record type.

### 3. DU cases filtered out but parent type has no direct edge

The analyzer correctly filters out DU cases from dead code reports (line 60: `s.Kind <> DuCase`),
but if the DU TYPE is only ever referenced via its cases (pattern matching or construction),
the type itself may not have a direct edge.

**Fix:** When a DU case is used, add an edge to the parent DU type.

### 4. Module-level `let` bindings used from another module → missing edge

Private module functions like `reportFcsDiagnostics`, `discoverAndRegisterProjects`, etc.
are called from within `Daemon.fs` but appear unreachable because the edges go to/from
the symbol's full name, and the caller's full name may not match what's in the DB.

**Diagnosis needed:** Check if `fromSymbol` names in the dependency table match the
caller's full name. F# module functions have names like `FsHotWatch.Daemon.createWith`
but the let binding inside might be `processChanges` (a local closure) — closures don't
get their own symbol entry but DO create dependency edges. If the closure's full name
isn't in the symbols table, edges FROM it don't contribute to reachability.

**Fix:** Ensure that closures and local functions within a module-level binding create
edges that are attributed to the enclosing binding.

### 5. P/Invoke DllImport functions → always appear unreachable

Native interop functions (MacFsEvents.fs DllImports) are called from F# code but
`GetAllUsesOfAllSymbolsInFile()` may not report the extern function as a "use" at the
call site because it's a P/Invoke stub.

**Fix:** Either mark DllImport functions as entry points, or detect the `[<DllImport>]`
attribute and exclude them from dead code reporting.

### 6. Interface implementations → edge to interface but not to implementing type

When `IFsHotWatchPreprocessor` is implemented by `FormatPreprocessor`, the analyzer
records that `FormatPreprocessor` implements the interface, but may not record that
using the interface (calling `.Process()`) creates an edge to the implementor.

**Fix:** When an interface method is called, add edges to all known implementors of
that interface.

## Suggested Approach (incremental)

1. Start by adding diagnostic output: `--verbose` flag that shows WHY a symbol is
   unreachable (no incoming edges, vs. edges exist but don't connect to entry points).
   This distinguishes "missing edges" from "genuinely isolated."

2. Fix the highest-impact edge pattern first: **generic type parameters** (#1) and
   **DU type from case usage** (#3). These alone would eliminate ~50% of false positives.

3. Add `--exclude-extern` flag to skip DllImport functions (#5).

4. Fix the module-level binding edge attribution (#4) — this is the hardest but most
   impactful for the reachability algorithm.

## Validation

After each fix, re-run against FsHotWatch. The target: only `PluginResult` (and possibly
`ProjectCheckResult`) should be reported as unreachable. Current count: 108 → target: <5.
