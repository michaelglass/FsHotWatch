<!-- sync:intro:start -->
# TestPrune

F# test impact analysis. Uses FSharp.Compiler.Service to build a symbol
dependency graph, then determines which tests are affected by a code change.
Only affected tests run — unchanged code is skipped.
<!-- sync:intro:end -->

## Quick example

Given a library (`Math.fs`) and its tests (`MathTests.fs`):

```fsharp
// src/SampleLib/Math.fs
module SampleLib.Math

let add x y = x + y
let multiply x y = x * y
```

```fsharp
// tests/SampleLib.Tests/MathTests.fs
module SampleLib.Tests.MathTests

open Xunit
open SampleLib.Math

[<Fact>]
let ``add returns sum`` () = Assert.Equal(5, add 2 3)

[<Fact>]
let ``multiply returns product`` () = Assert.Equal(12, multiply 3 4)
```

Index the project, then change `multiply`. Only the multiply test runs:

```fsharp
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff
open TestPrune.ImpactAnalysis

let db = Database.create ".test-prune.db"

// After indexing, detect changes and select tests
let stored = db.GetSymbolsInFile "src/SampleLib/Math.fs"
let changes = detectChanges currentSymbols stored
let changedNames = changedSymbolNames changes
// changedNames = ["SampleLib.Math.multiply"]

match selectTests db ["src/SampleLib/Math.fs"] currentSymbolsByFile with
| RunSubset tests -> // ["multiply returns product"]
| RunAll reason   -> // conservative fallback
```

## Installation

```bash
dotnet add package TestPrune.Core
```

## Library usage

`TestPrune.Core` is the primary API. Use it directly in custom build systems,
editors, or CI pipelines.

### Indexing

Parse F# source files with FCS and store the dependency graph in SQLite:

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
    let normalized = { result with Symbols = normalizeSymbolPaths repoRoot result.Symbols }
    db.RebuildForProject("MyProject", normalized)
| Error msg ->
    eprintfn $"Failed: %s{msg}"
```

### Querying affected tests

```fsharp
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

let storedSymbols = db.GetSymbolsInFile "src/SampleLib/Math.fs"
let changes = detectChanges currentSymbols storedSymbols
let changedNames = changedSymbolNames changes

// Query transitively affected tests
let affected = db.QueryAffectedTests changedNames
// -> TestMethodInfo list with TestProject, TestClass, TestMethod

// Or use the higher-level API
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

## How it works

1. **Index** — Parses every `.fs` file with FCS, extracting symbol declarations
   and their dependencies. Stores the graph in `.test-prune.db`.

2. **Diff** — Gets the VCS diff (`jj diff --git`), parses changed file paths,
   and re-parses those files to get current symbols.

3. **Compare** — Diffs current symbols against the stored index. A symbol is
   "changed" if its line range shifted, or if it was added/removed.

4. **Walk** — Walks the dependency graph transitively from changed symbols to
   find all affected test methods.

5. **Run** — Executes only affected test classes via xUnit v3 `--filter-class`.

Conservative fallback: `.fsproj` changes or new unindexed files trigger a full
test run.

## Key types

```fsharp
type SymbolInfo =
    { FullName: string        // e.g. "SampleLib.Math.add"
      Kind: SymbolKind        // Function | Type | DuCase | Module | Value | Property
      SourceFile: string      // repo-relative path
      LineStart: int
      LineEnd: int }

type Dependency =
    { FromSymbol: string      // caller
      ToSymbol: string        // callee
      Kind: DependencyKind }  // Calls | UsesType | PatternMatches | References

type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: string
```

## Writing extensions

Extensions add custom dependency sources beyond AST analysis — for example,
mapping HTTP routes to handler files so that changing a handler triggers
integration tests that hit that route.

```fsharp
open TestPrune.Database
open TestPrune.Extensions

type ITestPruneExtension =
    abstract Name: string
    abstract FindAffectedTests:
        db: Database -> changedFiles: string list -> repoRoot: string -> AffectedTest list
```

The `TestPrune.Falco` package ships a built-in extension for Falco routes:

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

Store route mappings during indexing:

```fsharp
db.RebuildRouteHandlers [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
]
```

## Packages

| Package | Description |
|---------|-------------|
| `TestPrune.Core` | Core library: AST analysis, SQLite graph, symbol diffing, impact selection, extension API |
| `TestPrune.Falco` | Extension for Falco route-based integration test filtering |
| `TestPrune` | Standalone CLI (convenience wrapper around the library) |

## CLI usage

The CLI is a convenience wrapper around the library, included for completeness.

```
test-prune index              # Build the dependency graph
test-prune run                # Run only affected tests
test-prune status             # Show what would run (dry-run)
test-prune dead-code          # Detect unreachable symbols
test-prune dead-code --entry "*.main" --entry "*.Routes.*"
```

## Design decisions

**AST over coverage.** The graph is built from static analysis, not test
execution. Indexing is fast (no tests need to run), structural changes are
caught even if they don't change runtime behavior, and there is no
flaky-coverage problem. The tradeoff: AST analysis may over-select tests,
but it won't miss them.

**Conservative fallback.** When impact can't be determined precisely (new
files, `.fsproj` changes), fall back to running the full suite. False
negatives are worse than false positives.

**SQLite storage.** The dependency graph lives in a single `.test-prune.db`
file. Rebuilds are transactional and per-project.

**Extension API.** Frameworks create implicit dependencies that don't appear
in the AST — HTTP routes, DI registrations, config bindings. The
`ITestPruneExtension` interface plugs in custom dependency sources.
