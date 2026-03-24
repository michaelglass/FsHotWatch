<!-- sync:intro:start -->
# TestPrune

Only run the tests affected by your change.

TestPrune analyzes your F# code to figure out which functions depend on
which, then uses that to skip tests that couldn't possibly be affected
by what you changed.
<!-- sync:intro:end -->

## Why?

When your test suite takes minutes but you only changed one function,
running everything is wasteful. TestPrune builds a map of your code —
which functions call which, which tests cover which code — and uses it
to pick just the tests that matter.

Change `multiply`? Only the multiply tests run. Change a type that
three modules depend on? Those three modules' tests run. Add a new
file? Everything runs, just to be safe.

## Quick example

Say you have a math library and some tests
(from [`examples/SampleSolution`](examples/SampleSolution)):

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

You change `multiply`. TestPrune figures out that only
`multiply returns product` needs to run — and skips `add returns sum`.

## Getting started

```bash
dotnet add package TestPrune.Core
```

### 1. Index your project

First, build a dependency graph of your code. This parses every `.fs`
file and stores the results in a local SQLite database:

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

To skip re-indexing unchanged projects, pass a cache key:

```fsharp
// RebuildForProjectIfChanged compares the key against the stored value
// and skips the rebuild entirely if nothing changed.
let changed = db.RebuildForProjectIfChanged("MyProject", cacheKey, result)
// changed = false means the project was already up-to-date
```

The key can be anything that changes when source files change — a
checksum of file sizes and timestamps, a VCS tree hash, a version
string, etc. The CLI uses file metadata (path + size + mtime) by
default.

### 2. Find affected tests

When you're ready to test, compare the current code against the index
to find what changed, then ask which tests are affected:

```fsharp
match selectTests db changedFiles currentSymbolsByFile with
| RunSubset tests -> // only these tests need to run
| RunAll reason   -> // something changed that we can't analyze — run everything
```

`RunSubset` gives you a list of specific test methods. `RunAll` is the
safe fallback for situations like `.fsproj` changes or brand new files
where TestPrune can't be sure what's affected.

### 3. (Bonus) Find dead code

The same dependency graph can find code that's never reached from your
entry points:

```fsharp
let result = findDeadCode db [ "*.main"; "*.Program.*" ]
// result.UnreachableSymbols — functions nothing calls
```

## How it works

1. **Index** — Parse every `.fs` file, record which functions/types exist
   and what they depend on. Store in SQLite.
2. **Diff** — Look at what files changed since last commit.
3. **Compare** — Figure out which specific functions changed (added,
   removed, or modified).
4. **Walk** — Follow the dependency graph from changed functions to find
   every test that transitively depends on them.
5. **Run** — Execute only those tests.

If anything looks uncertain (new files, project file changes), it falls
back to running everything. Better to run too many tests than miss a
broken one.

## Extensions

Some dependencies don't show up in code — like HTTP routes mapping to
handler files. Extensions let you teach TestPrune about these:

```fsharp
type ITestPruneExtension =
    abstract Name: string
    abstract FindAffectedTests:
        db: Database -> changedFiles: string list -> repoRoot: string -> AffectedTest list
```

[`TestPrune.Falco`](src/TestPrune.Falco/) is an extension for Falco
web apps that maps URL routes to integration tests.

## Packages

| Package | What it's for |
|---------|---------------|
| [`TestPrune.Core`](https://www.nuget.org/packages/TestPrune.Core) | The library — use this in your build system or editor |
| [`TestPrune.Falco`](https://www.nuget.org/packages/TestPrune.Falco) | Extension for Falco web apps (route → test mapping) |
| `TestPrune` | CLI tool (convenience wrapper around the library) |

## CLI

If you just want to try it out without writing code:

```
test-prune index       # Build the dependency graph
test-prune run         # Run only affected tests
test-prune status      # Show what would run (dry-run)
test-prune dead-code   # Find unreachable code
```

## Design choices

**Static analysis, not coverage.** TestPrune reads your code's AST
instead of instrumenting test runs. This means indexing is fast, you
don't need to run tests to build the graph, and there's no
flaky-coverage problem. The tradeoff: it might run a few extra tests,
but it won't miss broken ones.

**Safe by default.** When in doubt, run everything. A missed broken test
is much worse than running a few unnecessary ones.

**Single-file storage.** The dependency graph is one `.test-prune.db`
file. No servers, no services. Rebuilds are atomic.
