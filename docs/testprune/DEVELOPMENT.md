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
mise run coverage-check   # Check per-file coverage thresholds
mise run coverage-ratchet # Tighten coverage thresholds to current values
mise run sync-docs        # Sync README.md to docs/index.md
mise run sync-docs-check  # Check docs sync
mise run dead-code      # Find unreachable production code
mise run dead-code-tests # Find unreachable test code
mise run bench          # Run benchmarks with dotnet-trace profiling
mise run bench-raw      # Run benchmarks without profiling (JSON metrics)
mise run release        # Tag a release based on API changes
mise run release-alpha  # Tag an alpha pre-release
```

## Project Structure

- `src/TestPrune.Core/` — Core library: AST analysis, SQLite graph, symbol diffing, impact selection
- `src/TestPrune/` — CLI tool (index, run, status, dead-code)
- `src/TestPrune.Falco/` — Falco route-based integration test filtering extension
- `tests/TestPrune.Tests/` — All tests (xUnit v3 + MTP v2)
- `examples/SampleSolution/` — Example F# solution for smoke testing

## Conventions

- F# code formatted with Fantomas, linted with FSharpLint
- 4-space indentation
- VCS: jj (Jujutsu), not git
- Tests use xUnit v3 with Microsoft Testing Platform v2
- Package versions managed centrally in `Directory.Packages.props` (CPM) with
  floats enabled and transitive pinning on
- Restore uses lockfiles (`packages.lock.json` per project); CI runs in
  locked mode — update locks locally with `dotnet restore --force-evaluate`
- NU1605 suppressed across projects (FSharp.Core version mismatch with SDK)

## Shared Tooling

Uses NuGet tools from michaelglass/MichaelsWackyFsPackageTools:
- `coverageratchet` — per-file coverage enforcement with automatic threshold ratcheting
- `syncdocs` — README-to-docs section syncing
- `fssemantictagger` — semantic versioning with API change detection

CI uses reusable GitHub workflows from the same repo.

## Package Publishing

TestPrune.Core and TestPrune.Falco are NuGet packages with separate release tags:
- `core-v*` — TestPrune.Core + CLI
- `falco-v*` — TestPrune.Falco
