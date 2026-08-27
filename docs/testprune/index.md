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
files like snapshots, migrations, or config — declare them with marker
attributes:

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
attributes are metadata — no runtime behavior. `run` and `status` match them against
every path in the diff, including both the old and new name of a renamed file; Git's
C-quoted path names are decoded before matching.

### You declare the attributes yourself

TestPrune matches these **by type name**, read off the syntax tree. It
never loads your assemblies, and the namespace is ignored — so there is
nothing to install. Declare the ones you use anywhere in your own code:

```fsharp
namespace TestPrune   // any namespace; only the type name is matched

open System

type DependsOnAttribute(target: Type) =
    inherit Attribute()
    member _.Target = target

type DependsOnFileAttribute(path: string) =
    inherit Attribute()
    member _.Path = path

type DependsOnGlobAttribute(pattern: string) =
    inherit Attribute()
    member _.Pattern = pattern

type CompositionRootAttribute() =
    inherit Attribute()
```

Both spellings match, with and without the `Attribute` suffix — that is
what lets you write `[<DependsOnFile ...>]`. The attribute does have to
resolve for the compiler, so it must be declared somewhere.

> This repo carries the same definitions in `src/TestPrune.Attributes`
> for its own tests and examples, but **that package is not published to
> NuGet.** Declaring them yourself is the supported route.

## Composition roots

A routing table or DI registration block names *every* handler in the
codebase in order to wire them up, and an integration-test fixture that
boots the app depends on it. So the walk reaches every fixture-using test
from every handler: change one handler, select the whole integration
suite. Every edge on that path is real — the conclusion isn't. Nothing in
the graph distinguishes "wires X up" from "calls X", so you say which
symbol is the wiring:

```fsharp
[<TestPrune.CompositionRoot>]
let endpointsFor (route: Route) : HttpHandler =
    match route with
    | Home -> Handlers.home
    | Admin -> Handlers.admin
    // ... names every handler
```

The rule is **one-directional**, and both halves matter:

- **Reached *through* it** — relevance stops. The root is still reported
  affected, but tests reachable only by continuing past it are not
  selected.
- **Changed *itself*** — relevance flows on as usual. "The app is wired
  differently now" is what host-booting tests exist to check, so they run.

This is the one setting that makes TestPrune run *fewer* tests than the
graph implies, so it is the one that can hide a real failure. Annotate
only a symbol whose references are pure composition: if callers depend on
what it *computes* rather than on which parts it *wires together*,
annotating it drops real tests. Before annotating, make sure the coupling
it carried is covered some other way —
[`TestPrune.Falco`](https://www.nuget.org/packages/TestPrune.Falco)
attributes each route to its own tests directly, which is the worked
example.

A per-project fail-safe bounds the blast radius: a marked root may
**narrow** a test project's selection, never **empty** it. If the barrier
leaves a project with no tests at all, that project's full selection is
restored. That is a bound, not a completeness guarantee — a route with one
test that names its URL and another that only clicks through the UI still
drops the second. **Until your browser tests name the URLs they visit,
don't mark a composition root.**

## Packages

| Package | What it's for |
|---------|---------------|
| [`TestPrune.Core`](https://www.nuget.org/packages/TestPrune.Core) | The library — use this in your build system or editor |
| `TestPrune.Attributes` | Consumer-side markers: `[<DependsOn>]`, `[<DependsOnFile>]`, `[<DependsOnGlob>]`, `[<CompositionRoot>]`. **Not published** — the attributes are matched by name, so [declare them yourself](#you-declare-the-attributes-yourself) |
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
