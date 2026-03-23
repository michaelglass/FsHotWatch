# TestPrune

F# test impact analysis. Uses FSharp.Compiler.Service to build a symbol
dependency graph, then determines which tests are affected by a code change.
Only affected tests run — unchanged code is skipped.

## How it works

1. **Index** — Parses every `.fs` file with FCS, extracting symbol declarations
   (functions, types, DU cases, modules, values, properties) and their
   dependencies (calls, type usage, pattern matches, references). Stores the
   graph in a local SQLite database (`.test-prune.db`).

2. **Diff** — On each run, gets the VCS diff (`jj diff --git`), parses changed
   file paths, and re-parses those files to get current symbols.

3. **Compare** — Diffs current symbols against the stored index. A symbol is
   "changed" if its line range shifted, or if it was added/removed.

4. **Walk** — Walks the dependency graph transitively from changed symbols to
   find all affected test methods.

5. **Run** — Executes only affected test classes via xUnit v3 `--filter-class`.

Conservative fallback: `.fsproj` changes or new unindexed files trigger a full
test run.

## Packages

| Package | Description |
|---------|-------------|
| `TestPrune.Core` | Core library: AST analysis, SQLite graph, symbol diffing, impact selection, extension API |
| `TestPrune.Falco` | Extension for Falco route-based integration test filtering |
| `TestPrune` | Standalone CLI |

## CLI usage

```
test-prune index              # Build the dependency graph
test-prune run                # Run only affected tests
test-prune status             # Show what would run (dry-run)
test-prune dead-code          # Detect unreachable symbols
test-prune dead-code --entry "*.main" --entry "*.Routes.*"
```

### `index`

Builds every project in the solution, then parses all `.fs` files with real
project options (so FCS resolves cross-project references). Stores symbols,
dependencies, and test method metadata in `.test-prune.db`.

### `run`

Diffs working copy against the last commit, determines affected tests, and
runs them. Prints test output to stdout. Exit code reflects test results.

### `status`

Same analysis as `run`, but prints what would execute without running anything.
Useful for CI dry-runs or debugging the graph.

### `dead-code`

Walks the graph forward from entry points to find symbols that are never
reached. Excludes test methods, module declarations, and DU cases.

Default entry points: `*.main`, `*.Program.*`, `*.Routes.*`, `*.Scheduler.*`.
Override with `--entry`:

```
test-prune dead-code --entry "MyApp.Program.*" --entry "MyApp.Routes.*"
```

## Library integration

Use `TestPrune.Core` directly for custom build systems or editors.

### Indexing

```fsharp
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database

let checker = FSharpChecker.Create()
let db = Database.create ".test-prune.db"

// Get project options (from your build system or FCS script options)
let projOptions = getScriptOptions checker fileName source |> Async.RunSynchronously

// Analyze a source file
match analyzeSource checker fileName source projOptions |> Async.RunSynchronously with
| Ok result ->
    // result.Symbols     : SymbolInfo list
    // result.Dependencies : Dependency list
    // result.TestMethods  : TestMethodInfo list
    let normalized = { result with Symbols = normalizeSymbolPaths repoRoot result.Symbols }
    db.RebuildForProject("MyProject", normalized)
| Error msg ->
    eprintfn $"Failed: %s{msg}"
```

### Querying affected tests

```fsharp
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

// Get stored vs current symbols for changed files
let storedSymbols = db.GetSymbolsInFile "src/MyModule.fs"
let changes = detectChanges currentSymbols storedSymbols
let changedNames = changedSymbolNames changes

// Query transitively affected tests
let affected = db.QueryAffectedTests changedNames
// -> TestMethodInfo list with TestProject, TestClass, TestMethod

// Or use the higher-level API
let currentSymbolsByFile = Map.ofList [ "src/MyModule.fs", currentSymbols ]
match selectTests db changedFiles currentSymbolsByFile with
| RunSubset tests -> // run only these
| RunAll reason   -> // conservative fallback
```

### Dead code detection

```fsharp
open TestPrune.DeadCode

let result = findDeadCode db [ "*.main"; "*.Program.*" ]
// result.TotalSymbols       : int
// result.ReachableSymbols   : int
// result.UnreachableSymbols : SymbolInfo list
```

## Writing extensions

Extensions add custom dependency sources beyond AST analysis — for example,
mapping HTTP routes to handler files so that changing a handler triggers
integration tests that hit that route.

```fsharp
open TestPrune.Database
open TestPrune.Extensions

type ITestPruneExtension =
    /// Unique name for this extension (used in logging).
    abstract Name: string

    /// Given changed source files (repo-relative paths),
    /// return test classes that should be re-run.
    abstract FindAffectedTests:
        db: Database -> changedFiles: string list -> repoRoot: string -> AffectedTest list
```

`AffectedTest` is a simple record:

```fsharp
type AffectedTest =
    { TestProject: string
      TestClass: string }
```

### Example: FalcoRouteExtension

The `TestPrune.Falco` package ships a built-in extension that maps Falco
routes to integration tests:

```fsharp
open TestPrune.Falco

let extension = FalcoRouteExtension(
    integrationTestProject = "MyApp.IntegrationTests",
    integrationTestDir = "tests/MyApp.IntegrationTests"
)

let affected =
    (extension :> ITestPruneExtension)
        .FindAffectedTests db changedFiles repoRoot
```

It works by:

1. Checking if any changed file is a known handler source file (stored in the
   `route_handlers` table during indexing).
2. Looking up which URL patterns that handler serves.
3. Scanning integration test source files for those URL patterns.
4. Returning the test classes that reference affected routes.

Store route mappings during indexing:

```fsharp
db.RebuildRouteHandlers [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
]
```

## Key types

```fsharp
// Symbol declaration
type SymbolInfo =
    { FullName: string        // e.g. "MyApp.Domain.User.create"
      Kind: SymbolKind        // Function | Type | DuCase | Module | Value | Property
      SourceFile: string      // repo-relative path
      LineStart: int
      LineEnd: int }

// Dependency edge
type Dependency =
    { FromSymbol: string      // caller/user
      ToSymbol: string        // callee/used
      Kind: DependencyKind }  // Calls | UsesType | PatternMatches | References

// Test method metadata
type TestMethodInfo =
    { SymbolFullName: string
      TestProject: string
      TestClass: string
      TestMethod: string }

// Impact analysis result
type TestSelection =
    | RunSubset of TestMethodInfo list   // only these tests
    | RunAll of reason: string           // conservative fallback

// Symbol diff result
type SymbolChange =
    | Modified of symbolName: string
    | Added of symbolName: string
    | Removed of symbolName: string
```

## Design decisions

**AST over coverage.** The graph is built from static analysis, not test
execution. This means indexing is fast (no tests need to run), structural
changes are caught even if they don't change runtime behavior, and there is
no flaky-coverage problem. The tradeoff is that AST analysis is less precise
than actual call graphs — it may over-select tests, but it won't miss them.

**Conservative fallback.** When the tool can't determine impact precisely
(new files, `.fsproj` changes, unknown situations), it falls back to running
the full suite. False negatives (missing a broken test) are worse than false
positives (running extra tests).

**SQLite storage.** The dependency graph lives in a single `.test-prune.db`
file. No external services needed. Rebuilds are transactional and
per-project, so a failed index doesn't corrupt existing data.

**Extension API.** Frameworks often create implicit dependencies that don't
appear in the AST — HTTP routes, DI registrations, config bindings. The
`ITestPruneExtension` interface lets you plug in custom dependency sources
without modifying core analysis.

**xUnit v3 filtering.** Test selection uses `--filter-class` which is
supported by xUnit v3's standalone executable model (`dotnet exec`). Multiple
class filters are ORed together.
