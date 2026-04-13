# FsHotWatch.Coverage

Plugin that checks code coverage thresholds after tests complete. It reads
Cobertura XML coverage reports and compares line and branch coverage
against per-project thresholds.

## Why

Coverage checking is usually a separate CI step that runs after all tests.
With FsHotWatch, the CoveragePlugin reacts to `TestCompleted` events
and checks thresholds immediately, so you get instant feedback on whether
your changes dropped coverage below the minimum.

## How it works

1. TestPrune runs your tests (with coverage flags)
2. Tests produce Cobertura XML reports in the coverage directory
3. CoveragePlugin receives `TestCompleted`
4. It parses the XML reports and compares against thresholds
5. Projects below threshold are reported as errors

## Configuration

In `.fs-hot-watch.json`:

```json
{
  "coverage": {
    "directory": "./coverage",
    "thresholdsFile": "coverage-thresholds.json",
    "afterCheck": "dotnet tool run coverageratchet"
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `directory` | `string` | `"./coverage"` | Directory containing Cobertura XML coverage reports. |
| `thresholdsFile` | `string` | -- | Path to JSON file with per-project coverage thresholds. |
| `afterCheck` | `string` | -- | Shell command to run after coverage check. Non-zero exit reports to error ledger. |

### Thresholds file format

```json
{
  "MyApp.Tests.Unit": { "line": 85.0, "branch": 70.0 },
  "MyApp.Tests.Integration": { "line": 60.0, "branch": 50.0 }
}
```

Each key is a test project name. `line` and `branch` are minimum
percentages (0-100). Projects not listed in the thresholds file are
not checked.

## CLI

```bash
# Query coverage results
fs-hot-watch coverage
```

## Programmatic usage

```fsharp
daemon.RegisterHandler(
    CoveragePlugin.create
        "./coverage"                    // coverage report directory
        (Some "coverage-thresholds.json")  // thresholds file
        None                            // afterCheck hook: (unit -> bool * string) option
        None                            // getCommitId for caching
)
```

## Install

```bash
dotnet add package FsHotWatch.Coverage
```
