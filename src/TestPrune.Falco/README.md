<!-- sync:falco-readme:start -->
# TestPrune.Falco

When you change a route handler, only re-run the integration tests that
hit that route.

This is an extension for [TestPrune](https://github.com/michaelglass/TestPrune)
that connects Falco URL routes to integration tests. If you change the
handler for `/api/users/{id}`, it finds the tests that make requests to
that URL and runs just those.

> **Status: early alpha.** This is a young project, substantially
> AI-written, and still finding its shape. Behavior and APIs shift
> between versions, so pin a version and expect surprises.

## Installation

```bash
dotnet add package TestPrune.Falco
```

## How to use it

### 1. Store your route mappings during indexing

Routes are the one thing TestPrune cannot read out of your code — they
live in a route DU plus runtime wiring, not in the symbol graph — so you
seed them. `RouteStore` owns that table: it lives inside TestPrune's cache
database, but TestPrune.Core knows nothing about it (it just hands out a
connection, via `toPluginStore`).

Each entry is a `RouteHandlerEntry`; `Rebuild` clears and rewrites the
whole route table, so re-seed it on every indexing run. Set
`HandlerFunction` to the short `Module.function` serving the route so a
route's tests link only to that function (a one-function change to a
multi-route file selects only that route's tests); `None` falls back to a
whole-file match (every function in the file):

```fsharp
open TestPrune.Ports  // toPluginStore
open TestPrune.Falco  // RouteStore, RouteHandlerEntry

let routeStore = RouteStore(toPluginStore db)

routeStore.Rebuild [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs"
      HandlerFunction = Some "Users.get" }
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "PUT"
      HandlerSourceFile = "src/Web/Handlers/Users.fs"
      HandlerFunction = Some "Users.update" }
]
```

### 2. Create the extension and query affected tests

The extension reads routes through that same `RouteStore`:

```fsharp
open TestPrune.Ports        // toSymbolStore
open TestPrune.Extensions   // ITestPruneExtension

let extension =
    FalcoRouteExtension(
        integrationTestProject = "MyApp.IntegrationTests",
        integrationTestDir = "tests/MyApp.IntegrationTests",
        routeStore = routeStore
    )

// Affected test classes, directly:
let affected = extension.FindAffectedTestClasses(changedFiles, repoRoot)
// -> [{ TestProject = "MyApp.IntegrationTests"; TestClass = "UsersTests" }]
```

To feed those couplings into TestPrune's dependency graph instead, use
the `ITestPruneExtension` interface, which returns edges to inject:

```fsharp
let edges =
    (extension :> ITestPruneExtension)
        .AnalyzeEdges (toSymbolStore db) changedFiles repoRoot
// -> Dependency list (test symbol -> handler symbol, kind SharedState)
```

## How it works

1. Checks if any changed file is a known handler (from the route table)
2. Looks up which URL patterns that handler serves
3. Scans your integration test `.fs` files for those URLs
   (`/api/users/{id}` matches `/api/users/123` in your test code)
4. Returns the test classes from files that reference affected routes
   (`FindAffectedTestClasses`), or those couplings as graph edges
   (`AnalyzeEdges`)

## Documentation

- [Full documentation](https://michaelglass.github.io/TestPrune/Falco/)
- [API reference](https://michaelglass.github.io/TestPrune/reference/testprune-falco.html)
<!-- sync:falco-readme:end -->
