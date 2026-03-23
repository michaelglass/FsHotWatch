# TestPrune.Falco

Route-based integration test filtering extension for [TestPrune](https://github.com/michaelglass/TestPrune).

When a Falco route handler changes, only integration tests that reference
that handler's URL patterns are re-run.

## Usage

```fsharp
open TestPrune.Falco
open TestPrune.Extensions

let extension =
    FalcoRouteExtension(
        integrationTestProject = "MyApp.IntegrationTests",
        integrationTestDir = "tests/MyApp.IntegrationTests"
    )

let affected =
    (extension :> ITestPruneExtension)
        .FindAffectedTests db changedFiles repoRoot
```

## How it works

1. Checks if any changed file is a known handler source file
2. Looks up URL patterns for those handlers (from the `route_handlers` table)
3. Scans integration test `.fs` files for matching URL patterns
4. Returns test classes that reference affected routes

Store route mappings during indexing:

```fsharp
db.RebuildRouteHandlers [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
]
```

## Documentation

- [Full documentation](https://michaelglass.github.io/TestPrune/Falco/)
- [API reference](https://michaelglass.github.io/TestPrune/reference/testprune-falco.html)
