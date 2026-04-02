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
| `pattern` | `string` | `"*.fsx"` | File extension pattern to match (e.g. `"*.fsx"`, `"*.sql"`). |
| `command` | `string` | `"echo"` | Command to run when a matching file changes. |
| `args` | `string` | `""` | Arguments to the command. |

Each entry in the array creates a separate plugin instance. You can
have as many file commands as you want.

## CLI

```bash
# Check the status of a file command (named by pattern)
fs-hot-watch file-cmd-*.fsx-status
```

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
// Type-check .fsx scripts when they change
daemon.RegisterHandler(
    FileCommandPlugin.create
        "scripts"                           // plugin name
        (fun f -> f.EndsWith(".fsx"))       // file filter predicate
        "dotnet"                            // command
        "fsi --typecheck-only build.fsx"    // args
        None                                // getCommitId for caching
)
```

## Install

```bash
dotnet add package FsHotWatch.FileCommand
```
