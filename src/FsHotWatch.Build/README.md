# FsHotWatch.Build

Plugin that runs a build command when source files change and emits
`BuildCompleted` events for downstream plugins (like test runners and
coverage checkers).

## Why

Without this plugin, each tool would need to trigger its own build.
The BuildPlugin centralizes builds so they happen once, and downstream
plugins (TestPrune) react to the result.

## How it works

1. You save a file
2. BuildPlugin receives `FileChanged`
3. It runs `dotnet build` (or your custom command)
4. On completion, it emits `BuildCompleted` (success or failure)
5. Downstream plugins like TestPrune react to the build result

The plugin guards against concurrent builds -- if a build is already
running and you save again, it skips. It also skips the build entirely
if only test files changed (and emits `BuildSucceeded` directly).

## Configuration

In `.fs-hot-watch.json`:

```json
{
  "build": {
    "command": "dotnet",
    "args": "build",
    "buildTemplate": "dotnet build {projects}"
  }
}
```

Set `"build": false` to disable the build plugin entirely.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `command` | `string` | `"dotnet"` | Build command. |
| `args` | `string` | `"build"` | Arguments to the build command. |
| `buildTemplate` | `string` | -- | Template for incremental builds. `{projects}` is replaced with changed project paths. |

## CLI

```bash
# Trigger a build and wait for it
fs-hot-watch build

# Check build status
fs-hot-watch build-status
```

## Programmatic usage

From the [FullPipelineExample](../../examples/FullPipelineExample/):

```fsharp
daemon.RegisterHandler(
    BuildPlugin.create
        "dotnet"          // command
        "build"           // args
        []                // environment variables
        daemon.Graph      // project graph
        []                // test project names (to skip build-only-test-changes)
        None              // build template
        []                // dependsOn — plugins this one waits for
        None              // timeoutSec (None → no timeout)
        None              // dirtyTracker (optional)
)
```

## Install

```bash
dotnet add package FsHotWatch.Build
```
