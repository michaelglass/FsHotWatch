<!-- sync:falco-readme -->
# TestPrune.Falco

When you change a route handler, only re-run the integration tests that
hit that route.

This is an extension for [TestPrune](https://github.com/michaelglass/TestPrune)
that connects Falco URL routes to integration tests. If you change the
handler for `/api/users/{id}`, it finds the tests that make requests to
that URL and runs just those.

## Installation

```bash
dotnet add package TestPrune.Falco
```

## How to use it

### 1. Store your route mappings during indexing

Tell TestPrune which source files handle which URLs:

```fsharp
db.RebuildRouteHandlers [
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "GET"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
    { UrlPattern = "/api/users/{id}"
      HttpMethod = "PUT"
      HandlerSourceFile = "src/Web/Handlers/Users.fs" }
]
```

### 2. Create the extension and query affected tests

```fsharp
let extension =
    FalcoRouteExtension(
        integrationTestProject = "MyApp.IntegrationTests",
        integrationTestDir = "tests/MyApp.IntegrationTests"
    )

let affected =
    (extension :> ITestPruneExtension)
        .FindAffectedTests db changedFiles repoRoot
// -> [{ TestProject = "MyApp.IntegrationTests"; TestClass = "UsersTests" }]
```

## How it works

1. Checks if any changed file is a known handler (from the route table)
2. Looks up which URL patterns that handler serves
3. Scans your integration test `.fs` files for those URLs
   (`/api/users/{id}` matches `/api/users/123` in your test code)
4. Returns the test classes from files that reference affected routes

## Documentation

- [Full documentation](https://michaelglass.github.io/TestPrune/Falco/)
- [API reference](https://michaelglass.github.io/TestPrune/reference/testprune-falco.html)
<!-- sync:falco-readme:end -->
