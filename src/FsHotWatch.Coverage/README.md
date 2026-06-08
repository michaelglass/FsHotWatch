# FsHotWatch.Coverage

Plugin that checks per-file line and branch coverage thresholds after each
test run, using Cobertura XML reports produced by the test runner.

## Why

Coverage enforcement is usually a separate CI step that runs after all tests.
With FsHotWatch, CoveragePlugin reacts to `TestRunCompleted` events and checks
thresholds immediately, giving you instant feedback when a change drops coverage
below the minimum.

## How it works

1. TestPrune runs your tests (with coverage flags wired via `coveragePaths`)
2. Tests produce `coverage.cobertura.xml` reports under `searchDir`
3. CoveragePlugin receives `TestRunCompleted`
4. It merges partial-run coverage with the baseline, then parses the Cobertura XML
5. Files below their per-file threshold are reported as errors (surfaced by `fshw check` / `fshw status`)

## Configuration

In `.fshw.json`:

```json
{
  "coverage": {
    "configPath": "coverage-ratchet.json",
    "searchDir": "coverage"
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configPath` | `string` | `"coverage-ratchet.json"` | Path to the coverage-ratchet thresholds file (relative to repo root or absolute). |
| `searchDir` | `string` | `"."` | Directory tree to search for `coverage.cobertura.xml` files after each test run. |

Thresholds are managed by the `coverage-ratchet` IPC command (or `fshw coverage-ratchet`).
The format is a JSON file maintained by the
[coverageratchet](https://github.com/michaelglass/MichaelsWackyFsPackageTools) tool.

## CLI

```bash
# Check current coverage status
fshw coverage-status

# Update thresholds to match current coverage (ratchet up)
fshw coverage-ratchet

# Show all errors (including coverage violations) without triggering a run
fshw status
```

## Programmatic usage

```fsharp
daemon.RegisterHandler(
    CoveragePlugin.create
        (System.IO.Path.Combine(repoRoot, "coverage-ratchet.json"))  // configPath
        (System.IO.Path.Combine(repoRoot, "coverage"))               // searchDir
)
```

## Install

```bash
dotnet add package FsHotWatch.Coverage
```
