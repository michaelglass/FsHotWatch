<!-- sync:readme -->
# TestPrune

Run only the tests your change could have affected.

TestPrune analyzes your F# code to work out which functions depend on
which, then uses that map to skip tests that couldn't have been touched
by what you changed. The aim: when your suite takes minutes but you
changed one function, you wait seconds.

> **Status: early alpha.** This is a young project, substantially
> AI-written, and still finding its shape. Behavior and APIs shift
> between versions, so pin a version and expect surprises. Issues and
> PRs are very welcome.

## Why?

When your test suite takes minutes but you only changed one function,
running everything is wasteful. TestPrune builds a map of your code —
which functions call which, which tests cover which code — and tries to
pick just the tests that matter.

Change `multiply`? Ideally only the multiply tests run. Change a type
that three modules depend on? Those three modules' tests run. Add a new
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

You change `multiply`. TestPrune works out that only
`multiply returns product` needs to run — and skips `add returns sum`.

## Try the CLI

The quickest way to see it work is the `test-prune` CLI, a reference
implementation that wires the library up for you:

```
test-prune index       # Build the dependency graph
test-prune run         # Run only affected tests
test-prune status      # Show what would run (dry-run)
test-prune dead-code   # Find unreachable production code
```

It detects changes from your version control (`jj` or `git`), so run
`index` once, then `run`/`status` after each edit.

Global options: `--repo <path>` (repo root, default: auto-detect),
`--parallelism <n>` (max parallel analyses, default: processor count).

The CLI re-analyzes serially and isn't tuned for big codebases —
FSharp.Compiler.Service type-checking is slow. For real workflows,
embed `TestPrune.Core` in your build tooling, where you can cache and
parallelize. See the [integration guide](docs/integration.md).

## How it works

1. **Index** — Parse every `.fs` file, record which functions/types
   exist and what they depend on. Store in SQLite.
2. **Diff** — Look at what files changed since last commit.
3. **Compare** — Figure out which specific functions changed (added,
   removed, or modified).
4. **Walk** — Follow the dependency graph from changed functions to
   find every test that transitively depends on them.
5. **Run** — Execute only those tests.

If anything looks uncertain (new files, project-file changes), it falls
back to running everything. Better to run too many tests than miss a
broken one.

## Declarative dependencies

For edges the analyzer can't see — reflection, DI-by-type, or non-F#
files like snapshots, migrations, or config —
[`TestPrune.Attributes`](https://www.nuget.org/packages/TestPrune.Attributes)
lets you declare them:

```fsharp
open TestPrune

[<DependsOn(typeof<PluginRegistry>)>]                    // reflection target
let registerPlugins () = ...

[<DependsOnFile("tests/snapshots/api.snap.json")>]       // specific file
[<Fact>]
let ``api snapshot`` () = ...

[<DependsOnGlob("migrations/*.sql")>]                    // glob
type DbIntegrationTests() = ...
```

Glob dialect: `**` crosses path segments, `*` stays within one, `?` is
a single non-`/` char. Paths are repo-relative and case-sensitive. The
attributes are metadata — no runtime behavior.

## Packages

| Package | What it's for |
|---------|---------------|
| [`TestPrune.Core`](https://www.nuget.org/packages/TestPrune.Core) | The library — use this in your build system or editor |
| [`TestPrune.Attributes`](https://www.nuget.org/packages/TestPrune.Attributes) | Consumer-side markers: `[<DependsOn>]`, `[<DependsOnFile>]`, `[<DependsOnGlob>]` |
| [`TestPrune.Falco`](https://www.nuget.org/packages/TestPrune.Falco) | Extension for Falco web apps (route → test mapping) |
| [`TestPrune.Analyzers`](https://www.nuget.org/packages/TestPrune.Analyzers) | Opt-in F# analyzer that flags anonymous records (invisible to impact analysis) |
| `TestPrune` | CLI tool (reference implementation) |

## Going deeper

- [Integration guide](docs/integration.md) — embed `TestPrune.Core`:
  indexing, two-level caching, finding affected tests, dead-code
  detection, extensions, the analyzer, and dependency-change fanout.
- [Full documentation](https://michaelglass.github.io/TestPrune/)
- [API reference](https://michaelglass.github.io/TestPrune/reference/testprune.html)

## Design choices

**Static analysis, not coverage.** TestPrune reads your code's AST
instead of instrumenting test runs. So you don't need to run tests to
build the graph, and there's no flaky-coverage problem. The tradeoff:
it may run a few extra tests, but it aims never to miss a broken one.

**Safe by default.** When in doubt, run everything. A missed broken
test is much worse than running a few unnecessary ones.

**Single-file storage.** The dependency graph is one `.test-prune.db`
file. No servers, no services. Rebuilds are atomic.
<!-- sync:readme:end -->
