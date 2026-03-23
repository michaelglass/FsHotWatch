<!-- sync:falco-intro -->
# TestPrune.Falco

When you change a route handler, only re-run the integration tests that
hit that route.

This is an extension for [TestPrune](https://github.com/michaelglass/TestPrune)
that connects Falco URL routes to integration tests. If you change the
handler for `/api/users/{id}`, it finds the tests that make requests to
that URL and runs just those.
<!-- sync:falco-intro:end -->

## Installation

```bash
dotnet add package TestPrune.Falco
```

## API Reference

See the [API reference](../reference/testprune-falco.html) for full type documentation.
