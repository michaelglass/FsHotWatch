# TestPrune

F# test impact analysis tool. Uses FSharp.Compiler.Service to build a symbol
dependency graph from AST analysis, then determines which tests are affected
by code changes.

## Build & Test

```bash
dotnet build
dotnet test
# or run tests directly:
dotnet exec tests/TestPrune.Tests/bin/Debug/net10.0/TestPrune.Tests.dll
```

## Mise Tasks

```bash
mise run build          # Build the solution
mise run test           # Run tests with coverage
mise run check          # Run all checks with auto-fix
mise run ci             # Run all CI checks (no auto-fix)
mise run format         # Format with Fantomas
mise run lint           # Run FSharpLint
mise run example        # Run test-prune against example solution
mise run example-build  # Build the example solution
mise run docs           # Generate API documentation
mise run coverage-check # Check per-file coverage thresholds
```

## Project Structure

- `src/TestPrune.Core/` — Core library: AST analysis, SQLite graph, symbol diffing, impact selection
- `src/TestPrune/` — CLI tool (index, run, status, dead-code)
- `src/TestPrune.Falco/` — Falco route-based integration test filtering extension
- `tests/TestPrune.Tests/` — All tests (xUnit v3 + MTP v2)
- `examples/SampleSolution/` — Example F# solution for smoke testing
- `scripts/` — Release, coverage, API check scripts

## Conventions

- F# code formatted with Fantomas, linted with FSharpLint
- 4-space indentation
- VCS: jj (Jujutsu), not git
- Tests use xUnit v3 with Microsoft Testing Platform v2
- FSharp.Core 10.1.x pinned explicitly (FCS 43.12.201 dependency)
- NU1605 suppressed across projects (FSharp.Core version mismatch with SDK)

## Package Publishing

TestPrune.Core and TestPrune.Falco are NuGet packages. Release via:
```bash
dotnet fsi scripts/release.fsx        # auto-bump based on API changes
dotnet fsi scripts/release.fsx alpha  # first release
```
