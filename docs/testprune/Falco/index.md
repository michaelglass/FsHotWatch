# TestPrune.Falco

Route-based integration test filtering extension for TestPrune.

Scans integration test source files for URL patterns that map to changed
handler files. When a handler changes, only the integration tests that
reference that handler's routes are re-run.

## Installation

```bash
dotnet add package TestPrune.Falco
```

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

1. Checks if any changed file is a known handler source file (from the
   `route_handlers` table populated during indexing)
2. Gets the URL patterns served by those handlers
3. Converts URL patterns to regexes (`/api/users/{id}` matches `/api/users/123`)
4. Scans integration test `.fs` files for those URL patterns
5. Returns test classes from files containing matching URLs

## Storing route mappings

Populate the route handler table during indexing:

```fsharp
db.RebuildRouteHandlers [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
]
```

## API Reference

See the [API reference](../reference/testprune-falco.html) for full type documentation.
