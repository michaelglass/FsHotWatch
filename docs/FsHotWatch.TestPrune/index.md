# FsHotWatch.TestPrune

Plugin for test impact analysis. When you change a source file,
TestPrune figures out which tests are affected and runs only those --
instead of your entire test suite.

## Why

Running all tests after every save is slow. If you change a single
function, you probably only need to run 3 tests out of 500. TestPrune
uses the warm FSharpChecker's symbol analysis to track which tests
depend on which symbols, so it can tell you exactly what to re-run.

## How it works

1. You save a file
2. TestPrune receives `FileChecked` with the warm compiler's results
3. It analyzes which symbols changed
4. It looks up which test classes reference those symbols
5. If `testConfigs` are provided, it runs only the affected tests
6. It emits `TestCompleted` for downstream plugins (like Coverage)

## Configuration

In `.fshw.json`:

```json
{
  "tests": {
    "beforeRun": "dotnet build",
    "projects": [
      {
        "project": "MyApp.Tests",
        "command": "dotnet",
        "args": "run --project tests/MyApp.Tests --no-build --",
        "filterTemplate": "--filter-class {classes}",
        "classJoin": " ",
        "group": "unit"
      }
    ]
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `beforeRun` | `string` | -- | Command to run before each test run (e.g. `"dotnet build"`). |
| `projects[].project` | `string` | `"unknown"` | Project name (for filtering and display). |
| `projects[].command` | `string` | `"dotnet"` | Test runner command. |
| `projects[].args` | `string` | `"test --project <name>"` | Arguments to the test runner. |
| `projects[].group` | `string` | `"default"` | Group name (for running subsets via `fshw test -p`). |
| `projects[].environment` | `object` | `{}` | Extra environment variables as `"KEY": "VALUE"` pairs. |
| `projects[].filterTemplate` | `string` | -- | Template for class-based filtering. `{classes}` is replaced with affected test class names. |
| `projects[].classJoin` | `string` | `" "` | Separator for joining class names in the filter. |

## CLI

```bash
# Run all affected tests
fshw test

# Run tests for a specific project group
fshw test -p MyApp.Tests

# Run only previously-failed tests
fshw test --only-failed

# Query which tests are affected by recent changes
fshw affected-tests

# Reset coverage baseline — next full run rebuilds coverage.baseline.json
fshw coverage refresh-baseline
```

## Coverage

When `projects[].coverage` is `true` (the default), TestPrune asks coverlet to
emit its native JSON format per test project under
`<repoRoot>/<tests.coverageDir>/<project>/`:

- **`coverage.baseline.json`** — written by every *full* run. Authoritative
  snapshot of the whole suite's coverage.
- **`coverage.partial.json`** — written by *impact-filtered* runs. Only the
  subset of lines the filtered tests touched.
- **`coverage.cobertura.xml`** — always derived; downstream gating
  (`coverageratchet`, editor badges, etc.) reads this file.

After each test run, TestPrune either converts the baseline directly (full
run) or merges the partial into the baseline per-line (max of hit counts)
before rewriting the cobertura file. Partial runs **never lower** the reported
coverage.

**Bootstrap.** If no `coverage.baseline.json` exists and the run was filtered,
TestPrune skips cobertura emission entirely. Run `fshw test` (or any
full-suite invocation) once to produce a baseline; subsequent filtered runs
will merge against it.

**Caveat.** Coverlet's merge keys by file path + line number, not by content
hash. Edits between a baseline and a partial can misattribute hits at the line
level. The aggregate coverage ratio stays correct.

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
daemon.RegisterHandler(
    TestPrunePlugin.create
        ".fshw/test-impact.db"   // database path
        repoRoot                  // repo root
        (Some [                   // test configs
            { Project = "MyApp.Tests"
              Command = "dotnet"
              Args = "run --project tests/MyApp.Tests --no-build --"
              Group = "unit"
              Environment = []
              FilterTemplate = Some "--filter-class {classes}"
              ClassJoin = " " }
        ])
        None                      // symbol snapshot
        None                      // beforeRun callback
        None                      // afterRun callback
        None                      // coveragePaths: project -> CoveragePaths option
        None                      // dirtyTracker (optional)
        None                      // stalenessCheck (optional)
)
```

## Install

```bash
dotnet add package FsHotWatch.TestPrune
```
