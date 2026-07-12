# Integration guide

The `test-prune` CLI is a reference implementation: it shows how the
pieces fit, but it re-analyzes serially and isn't tuned for large
codebases. For real workflows, embed
[`TestPrune.Core`](https://www.nuget.org/packages/TestPrune.Core)
directly in your build system or editor, where you can cache
aggressively and parallelize across projects.

```bash
dotnet add package TestPrune.Core
```

All the snippets below are drawn from how the CLI itself wires the
library (see `src/TestPrune/Orchestration.fs`). FSharp.Compiler.Service
type-checking is not instant, so plan on caching.

## 1. Index your project

First, build a dependency graph of your code. This parses every `.fs`
file with FCS and stores the results in a local SQLite database:

<!-- This block is sourced from a real, compiled example — see
     examples/UsageExample/UsageExample.fs. Do not edit it here; edit the source
     file and run `mise run sync-docs`. CI compiles the example and
     `sync-docs-check` fails if this block drifts. -->
<!-- sync:index:start src=examples/UsageExample/UsageExample.fs -->
```fsharp
open TestPrune.AstAnalyzer
open TestPrune.Database

let checker = FSharpChecker.Create()
let db = Database.create ".test-prune.db"

let fileName = "/abs/path/to/src/Lib.fs"
let source = System.IO.File.ReadAllText fileName
let projectName = "MyProject"

let projOptions = getScriptOptions checker fileName source |> Async.RunSynchronously

match
    analyzeSource checker fileName source projOptions projectName
    |> Async.RunSynchronously
with
| Ok result ->
    let normalized =
        { result with
            Symbols = normalizeSymbolPaths repoRoot result.Symbols }

    db.RebuildProjects([ normalized ])
| Error msg -> eprintfn $"Failed: %s{msg}"
```
<!-- sync:index:end -->

`analyzeSource` takes `checker source-file source project-options
project-name` and returns `Result<AnalysisResult, string>`.
`getScriptOptions` is a convenient way to get project options for a
single file; in a real build you'll usually have full project options
already. `normalizeSymbolPaths repoRoot` rewrites absolute source paths
to repo-relative ones so the graph is stable across machines.

## 2. Cache for speed

Re-analysis is the expensive part, so skip it whenever you can.
TestPrune supports two cache levels — project and file — both keyed on
content hashes that you supply. Read them with `db.GetProjectKey` /
`db.GetFileKey`, and write them in the same transaction as the symbols
via `RebuildProjects`'s optional `fileKeys` / `projectKeys` arguments:

<!-- sync:cache:start src=examples/UsageExample/UsageExample.fs -->
```fsharp
match db.GetProjectKey("MyProject") with
| Some key when key = currentKey -> () // unchanged — skip the whole project
| _ ->
    // For files that haven't changed, reuse cached rows instead of re-analyzing:
    match db.GetFileKey("src/Lib.fs") with
    | Some key when key = currentFileKey ->
        let symbols = db.GetSymbolsInFile("src/Lib.fs")
        let deps = db.GetDependenciesFromFile("src/Lib.fs")
        let tests = db.GetTestMethodsInFile("src/Lib.fs")
        () // ... use cached data
    | _ -> () // file changed — run analyzeSource as above

    // Write symbols and both sets of cache keys atomically.
    db.RebuildProjects(
        [ combined ],
        fileKeys = [ "src/Lib.fs", currentFileKey ],
        projectKeys = [ "MyProject", currentKey ]
    )
```
<!-- sync:cache:end -->

Cache keys can be anything that changes when source files change. Good
options:

- **VCS tree/commit hash** (recommended) — a content-addressed hash
  (e.g. from `jj` or `git`) that changes exactly when files change.
  Fast and correct across branch switches.
- **File metadata** — path + size + mtime. What the CLI uses by
  default. Simple, but can be wrong after a checkout (mtime updates
  even when content is identical).

## 3. Find affected tests

When you're ready to test, compare the current code against the index
to find what changed, then ask which tests are affected. `selectTests`
returns a `TestSelection * AnalysisEvent list` tuple (the events are an
audit trail you can ignore):

<!-- sync:select:start src=examples/UsageExample/UsageExample.fs -->
```fsharp
open TestPrune.Ports
open TestPrune.ImpactAnalysis

let store = toSymbolStore db

let selection, _events = selectTests store changedFiles currentSymbolsByFile

match selection with
| RunSubset tests -> () // only these test methods need to run
| RunAll reason -> () // can't analyze the change — run everything
```
<!-- sync:select:end -->

`RunSubset` carries a list of specific test methods. `RunAll` is the
safe fallback for `.fsproj` changes, brand-new files, or analysis
failures — anything where TestPrune can't be sure what's affected. The
`reason` says which.

## 4. (Bonus) Find dead code

The same dependency graph can find code that's never reached from your
entry points. Resolve entry-point patterns to symbol names, compute the
reachable set, then call `findDeadCode`:

<!-- sync:dead-code:start src=examples/UsageExample/UsageExample.fs -->
```fsharp
open TestPrune.DeadCode

let store = toSymbolStore db
let allSymbols = store.GetAllSymbols()
let allNames = store.GetAllSymbolNames()

let entryPatterns = [ "*.main"; "*.Program.*" ]
let entryPoints = findEntryPoints allNames entryPatterns
let reachable = store.GetReachableSymbols(entryPoints)
let testMethodNames = store.GetTestMethodSymbolNames()

// result.UnreachableSymbols — symbols nothing reaches from the entry points
let result, _events = findDeadCode allSymbols reachable testMethodNames false
```
<!-- sync:dead-code:end -->

The last argument is `includeTests`. By default (`false`) symbols in
test files are excluded from the report; pass `true` to find dead code
in your test suite too, e.g. unused test helpers. For per-symbol "why
is this unreachable" detail, use `findDeadCodeVerbose`, which also takes
a `getIncomingEdgesBatch` (available as `store.GetIncomingEdgesBatch`).

## Extensions

Some dependencies don't show up in code — like HTTP routes mapping to
handler files. Extensions let you teach TestPrune about these by
implementing `ITestPruneExtension` to inject extra dependency edges.

Build those edges with `EdgeEmission.edgesTo`: emit an edge from each
dependent to the *specific* symbol it depends on across the boundary,
scoped precisely when the fact names a symbol and degraded to the whole
changed file when it doesn't. Both bugs TestPrune has shipped came from
an extension hand-rolling this step — one over-selected (a cross-product
of every test and every symbol in the file), one under-selected (a
scoping filter that kept only the first match). Scoping to the direct
symbol is safe because `QueryAffectedTests` walks the graph transitively
in reverse, so an edge to a handler already selects that test when
anything the handler calls changes:

<!-- sync:extension:start src=examples/UsageExample/UsageExample.fs -->
```fsharp
open TestPrune.EdgeEmission
open TestPrune.Extensions

type ExampleExtension() =
    interface ITestPruneExtension with
        member _.Name = "example"

        member _.AnalyzeEdges
            (symbolStore: SymbolStore)
            (changedFiles: string list)
            (repoRoot: string)
            : Dependency list =
            // The out-of-band fact this extension knows and the AST cannot: the tests in
            // `tests/ApiTests.fs` exercise the handler `Handlers.getUser`.
            let dependents = symbolStore.GetSymbolsInFile "tests/ApiTests.fs"

            changedFiles
            |> List.collect (fun changedFile ->
                let candidates = symbolStore.GetSymbolsInFile changedFile

                // `edgesTo` scopes the edge to the symbol the fact names. A fact that
                // names none (`UnnamedSymbol`), or names one that no longer resolves,
                // falls back to every symbol in the changed file — coarse, but never
                // empty: a missing edge is a test that silently stops being re-run.
                // Never hand-roll a cross-product of all tests x all symbols.
                //
                // The DIRECT symbol is enough. Core's `QueryAffectedTests` is a recursive
                // TRANSITIVE reverse-walk of the graph, so `test -> getUser` already
                // re-selects the test when anything getUser calls changes.
                edgesTo "example" SharedState candidates (NamedSymbol "Handlers.getUser") dependents)
```
<!-- sync:extension:end -->

[`TestPrune.Falco`](https://www.nuget.org/packages/TestPrune.Falco) is
an extension for Falco web apps that maps URL routes to integration
tests.

### Extension-owned storage

Most extensions derive their facts from the symbol graph and need no
storage. An extension whose facts come from *outside* the AST — Falco's
routes live in a route DU plus runtime wiring, so they're seeded by a
separate build step — can own a table inside TestPrune's cache database
via `Ports.toPluginStore db`, which hands it a connection. Core never
learns what the table means.

The contract runs both ways. Core owns the file: opening it checks the
schema version and, on a mismatch, deletes and recreates it — dropping
extension tables with it, because core cannot migrate a table it knows
nothing about. So an extension owns its tables but must never assume they
exist: issue `CREATE TABLE IF NOT EXISTS` before every read and write, and
store only what you can re-derive (Falco re-seeds its routes each run).

## Dependency-change fanout

When a project's *dependency fingerprint* changes — a NuGet /
`PackageReference` bump, or a `ProjectReference`d project rebuilt
against a changed dependency — every test in the projects that
transitively reference it is selected, even though no source symbol
changed (the `ProjectFanout` module). This catches behavior changes the
symbol graph can't see.

## Analyzer (opt-in)

Anonymous records (`{| Year = d.Year |}` and the matching
`{| Year: int |}` type annotations) have no stable cross-build name, so
TestPrune's AST impact analysis **skips** them. A test or symbol
coupled to a change *only* through an anonymous record is therefore
invisible to impact selection.

[`TestPrune.Analyzers`](https://www.nuget.org/packages/TestPrune.Analyzers)
is an opt-in
[FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK)
analyzer that flags every anonymous-record occurrence (diagnostic
`TP001`, `TestPrune.AnonymousRecord`, severity *Warning*) so
precision-sensitive repos can steer that coupling to a tracked
alternative — a named record, or an explicit
`[<TestPrune.DependsOnFile>]` / `[<TestPrune.DependsOnGlob>]` edge.
It's opt-in by construction: nothing changes unless you load the
analyzer into your analyzer host (Ionide or `fsharp-analyzers`).
