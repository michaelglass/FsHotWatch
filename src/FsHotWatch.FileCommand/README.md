# FsHotWatch.FileCommand

Plugin that runs a custom command when files matching a pattern change.
Register multiple instances for different file patterns.

> **Status: early alpha, and a lot of it is AI-written.** APIs shift between
> versions and rough edges are expected — your mileage may vary. Issues and PRs
> are very welcome.

## Why

Sometimes you want to run a specific command when certain files change --
type-check your `.fsx` scripts, validate SQL migrations, regenerate
code from `.proto` files, etc. FileCommand lets you do this without
writing a full plugin.

## How it works

1. You save a file
2. FileCommandPlugin checks if the file matches its pattern
3. If it matches, it runs the configured command
4. Success/failure is reported to the error ledger

## Configuration

In `.fshw.json`:

```json
{
  "fileCommands": [
    {
      "pattern": "*.fsx",
      "command": "dotnet",
      "args": "fsi --typecheck-only"
    },
    {
      "pattern": "*.sql",
      "command": "sqlfluff",
      "args": "lint"
    }
  ]
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `name` | `string` | derived from `pattern` | Plugin identifier. Required when `afterTests` is set. |
| `pattern` | `string` | — | File pattern to match. `*.ext` matches any path ending with `.ext`. A literal filename (e.g. `coverage-ratchet.json`) matches only files with that exact basename. |
| `afterTests` | `true` or `string[]` | — | Fire after a test run completes. `true` fires on any completed run; an array fires only when all named projects complete. Requires `name`. |
| `command` | `string` | `"echo"` | Command to run when triggered. |
| `args` | `string` | `""` | Arguments to the command. |

At least one of `pattern` or `afterTests` must be specified. Both can
be set on the same entry — e.g. a coverage ratchet that should re-run
whenever tests complete OR when its config file changes:

```json
{
  "fileCommands": [
    {
      "name": "coverage-ratchet",
      "pattern": "coverage-ratchet.json",
      "afterTests": true,
      "command": "dotnet",
      "args": "tool run coverageratchet"
    }
  ]
}
```

When `pattern` targets a non-source file (e.g. `*.ratchet.json` or
`coverage-ratchet.json`), the daemon automatically extends its file
watcher to cover that pattern so real edits trigger the plugin.

### Environment variables on `afterTests` commands

The following environment variables are set on every `afterTests` command:

- `FSHW_RAN_FULL_SUITE`: how much of the suite the triggering run covered.
  **Three values, not two:**

  | value | meaning |
  |---|---|
  | `"true"` | The run FINISHED, executed at least one test, and no project was impact-filtered — the entire suite ran. |
  | `"false"` | At least one project was filtered to a subset. |
  | `"unknown"` | Can't be established. The command fired mid-run (see below), or the run executed no tests at all. |

  Use it to gate baseline refreshes or threshold tightening — partial runs
  should not lower a coverage baseline or tighten a ratchet. **Gate on
  `= "true"`, never on `!= "false"`:**

  ```sh
  if [ "$FSHW_RAN_FULL_SUITE" = "true" ]; then
      refresh-coverage-baseline
  fi
  ```

  `"unknown"` exists because a boolean here has to lie. An `afterTests` command
  fires as soon as its trigger is satisfied, which for `afterTests: true` is the
  first completed *group* of a multi-`group` run — at that moment later groups
  may still be impact-filtered, so "the whole suite ran" is not yet knowable.
  Likewise a run that executed nothing (impact analysis found no affected tests,
  or a `beforeRun` hook aborted the run) filtered nothing, but proved nothing
  either. Reporting `"true"` there would hand a hook a licence to refresh a
  baseline off no evidence; reporting `"false"` would assert a filtered run that
  never happened. `"unknown"` says neither.

## CLI

```bash
# Force a plugin to re-run, clearing its cached state
fshw rerun coverage-ratchet

# Query a plugin's last-run status
fshw coverage-ratchet-status
```

## Programmatic usage

```fsharp
open FsHotWatch.PluginFramework
open FsHotWatch.FileCommand.FileCommandPlugin

// Type-check .fsx scripts when they change
let trigger: CommandTrigger =
    { FilePattern = Some(fun f -> f.EndsWith(".fsx"))
      AfterTests = None }

daemon.RegisterHandler(
    create
        (PluginName.create "scripts")       // plugin name
        trigger                             // CommandTrigger (pattern and/or afterTests)
        "dotnet"                            // command
        "fsi --typecheck-only build.fsx"    // args
        repoRoot                            // for resolving relative arg-file paths
        None                                // timeoutSec (None → no timeout)
)
```

For the combined trigger case (fires on file changes AND test completion):

```fsharp
let trigger: CommandTrigger =
    { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
      AfterTests = Some AnyTest }
```

See the [FullPipelineExample](../../examples/FullPipelineExample/) for a complete setup.

## Install

```bash
dotnet add package FsHotWatch.FileCommand
```
