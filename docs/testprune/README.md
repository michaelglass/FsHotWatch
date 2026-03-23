<!-- sync:intro:start -->
# TestPrune

F# test impact analysis. Uses FSharp.Compiler.Service to build a symbol
dependency graph, then determines which tests are affected by a code change.
Only affected tests run — unchanged code is skipped.
<!-- sync:intro:end -->

## How it works

Index parses every `.fs` file with FCS, extracting symbols and their
dependencies into a SQLite graph (`.test-prune.db`). On each run, the VCS diff
is parsed, changed symbols are compared against the index, and the graph is
walked transitively to find affected test methods. Only those tests execute.
`.fsproj` changes or unindexed files trigger a conservative full run.

## Quick example

Given `Math.fs` and its tests from [`examples/SampleSolution`](examples/SampleSolution):

```fsharp
// src/SampleLib/Math.fs
module SampleLib.Math

let add x y = x + y
let multiply x y = x * y
```

```fsharp
// tests/SampleLib.Tests/MathTests.fs
[<Fact>]
let ``add returns sum`` () = Assert.Equal(5, add 2 3)

[<Fact>]
let ``multiply returns product`` () = Assert.Equal(12, multiply 3 4)
```

After indexing, change `multiply`. Only the multiply test runs:

```fsharp
let db = Database.create ".test-prune.db"
let stored = db.GetSymbolsInFile "src/SampleLib/Math.fs"
let changes = detectChanges currentSymbols stored
let changedNames = changedSymbolNames changes  // ["SampleLib.Math.multiply"]

match selectTests db ["src/SampleLib/Math.fs"] currentSymbolsByFile with
| RunSubset tests -> // ["multiply returns product"]
| RunAll reason   -> // conservative fallback
```

## Installation

```bash
dotnet add package TestPrune.Core
```

## Library usage

`TestPrune.Core` is the primary API.

### Indexing

```fsharp
let checker = FSharpChecker.Create()
let db = Database.create ".test-prune.db"
let projOptions = getScriptOptions checker fileName source |> Async.RunSynchronously

match analyzeSource checker fileName source projOptions |> Async.RunSynchronously with
| Ok result ->
    let normalized = { result with Symbols = normalizeSymbolPaths repoRoot result.Symbols }
    db.RebuildForProject("MyProject", normalized)
| Error msg -> eprintfn $"Failed: %s{msg}"
```

### Querying affected tests

```fsharp
let changes = detectChanges currentSymbols (db.GetSymbolsInFile file)
let affected = db.QueryAffectedTests (changedSymbolNames changes)

// Or use the higher-level API
match selectTests db changedFiles currentSymbolsByFile with
| RunSubset tests -> // run only these
| RunAll reason   -> // conservative fallback
```

### Dead code detection

```fsharp
let result = findDeadCode db [ "*.main"; "*.Program.*" ]
// result.UnreachableSymbols : SymbolInfo list
```

## Extensions

Extensions add dependency sources beyond AST analysis — e.g., mapping HTTP
routes to handler files. See `TestPrune.Falco` for an example.

```fsharp
type ITestPruneExtension =
    abstract Name: string
    abstract FindAffectedTests:
        db: Database -> changedFiles: string list -> repoRoot: string -> AffectedTest list
```

## Packages

| Package | Description |
|---------|-------------|
| `TestPrune.Core` | Core library: AST analysis, SQLite graph, symbol diffing, impact selection |
| `TestPrune.Falco` | Falco route-based integration test filtering extension |
| `TestPrune` | CLI (convenience wrapper) |

## CLI

```
test-prune index       # Build the dependency graph
test-prune run         # Run only affected tests
test-prune status      # Dry-run: show what would run
test-prune dead-code   # Detect unreachable symbols
```

## Design decisions

**AST over coverage** — static analysis, not test execution. Fast indexing, no
flaky-coverage problem. May over-select but won't miss affected tests.

**Conservative fallback** — new files or `.fsproj` changes trigger full suite.
False negatives are worse than false positives.

**SQLite storage** — single `.test-prune.db` file, transactional per-project
rebuilds.

**Extension API** — `ITestPruneExtension` for framework-specific implicit
dependencies (routes, DI, config).
