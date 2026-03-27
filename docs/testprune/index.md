<!-- sync:readme -->
# TestPrune

Only run the tests affected by your change.

TestPrune analyzes your F# code to figure out which functions depend on
which, then uses that to skip tests that couldn't possibly be affected
by what you changed.

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
    db.RebuildProjects([ "MyProject", normalized ])
| Error msg -> eprintfn $"Failed: %s{msg}"
```

Caching works at two levels — project and file — to skip expensive
re-analysis for unchanged code:

```fsharp
// Project-level: skip the entire project if nothing changed
match db.GetProjectKey("MyProject") with
| Some key when key = currentKey -> () // skip
| _ ->
    // File-level: skip individual files within a changed project
    match db.GetFileKey("src/Lib.fs") with
    | Some key when key = currentFileKey ->
        // Load cached results from DB instead of re-analyzing
        let symbols = db.GetSymbolsInFile("src/Lib.fs")
        let deps = db.GetDependenciesFromFile("src/Lib.fs")
        let tests = db.GetTestMethodsInFile("src/Lib.fs")
        // ... use cached data
    | _ ->
        // File changed — run FCS analysis
        // ... analyzeSource, then db.SetFileKey(...)

    db.RebuildProjects([ "MyProject", combined ])
    db.SetProjectKey("MyProject", currentKey)
```

Cache keys can be anything that changes when source files change.
Good options:

- **VCS tree hash** (recommended) — `jj log -r @ -T commit_id` or
  `git rev-parse HEAD` gives a content-addressed hash that changes
  exactly when files change. Fast and correct across branch switches.
- **File metadata** — path + size + mtime. The CLI uses this by default.
  Simple but can be wrong after `git checkout` (mtime updates even if
  content is identical).

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
let result = findDeadCode db [ "*.main"; "*.Program.*" ] false
// result.UnreachableSymbols — functions nothing calls
```

By default, symbols in test files are excluded from the report. Pass
`true` for `includeTests` to find dead code in your test suite too
(e.g. unused test helpers):

```fsharp
let result = findDeadCode db [ "Tests.MyTests.*" ] true
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
test-prune dead-code   # Find unreachable production code
test-prune dead-code --include-tests  # Include test files in report
```

## Documentation

- [Full documentation](https://michaelglass.github.io/TestPrune/)
- [API reference](https://michaelglass.github.io/TestPrune/reference/testprune.html)

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
<!-- sync:readme:end -->
