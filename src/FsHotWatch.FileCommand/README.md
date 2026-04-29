# FsHotWatch.FileCommand

Plugin that runs a custom command when files matching a pattern change.
Register multiple instances for different file patterns.

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

In `.fs-hot-watch.json`:

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

- `FSHW_RAN_FULL_SUITE`: `"true"` if every project in the test run executed
  without an impact filter (i.e., the entire test suite ran), `"false"` if at
  least one project was filtered to a subset. Use this to gate baseline
  refreshes or threshold tightening — partial runs should not lower a
  coverage baseline or tighten a ratchet.

## CLI

```bash
# Force a plugin to re-run, clearing its cached state
fs-hot-watch rerun coverage-ratchet

# Query a plugin's last-run status
fs-hot-watch coverage-ratchet-status
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
