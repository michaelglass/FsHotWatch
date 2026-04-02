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

In `.fs-hot-watch.json`:

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
| `projects[].group` | `string` | `"default"` | Group name (for running subsets via `fs-hot-watch test -p`). |
| `projects[].environment` | `object` | `{}` | Extra environment variables as `"KEY": "VALUE"` pairs. |
| `projects[].filterTemplate` | `string` | -- | Template for class-based filtering. `{classes}` is replaced with affected test class names. |
| `projects[].classJoin` | `string` | `" "` | Separator for joining class names in the filter. |

## CLI

```bash
# Run all affected tests
fs-hot-watch test

# Run tests for a specific project group
fs-hot-watch test -p MyApp.Tests

# Run only previously-failed tests
fs-hot-watch test --only-failed

# Query which tests are affected by recent changes
fs-hot-watch affected-tests
```

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
        None                      // coverage args
        None                      // coverage args generator
        None                      // getCommitId for caching
)
```

## Install

```bash
dotnet add package FsHotWatch.TestPrune
```
