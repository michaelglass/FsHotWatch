# FsHotWatch

Core library for the FsHotWatch daemon. This package provides the event system,
plugin framework, file watcher, project graph, check pipeline, and IPC server
that all plugins build on top of.

## What it does

FsHotWatch keeps a single FSharpChecker instance warm in the background.
When you save a file, it re-checks just that file (milliseconds instead
of minutes) and dispatches the results to all registered plugins.

You don't use this package directly unless you're building a custom host
or writing your own plugin. Most users should install
[FsHotWatch.Cli](../FsHotWatch.Cli/) instead.

## Key types

| Type | Purpose |
|------|---------|
| `Daemon` | Ties together the warm FSharpChecker, file watcher, plugin host, and IPC server. |
| `PluginHost` | Manages plugin lifecycle, event dispatch, error ledger, and IPC commands. |
| `PluginHandler<'State, 'Msg>` | Declarative plugin definition: name, init state, update function, commands, subscriptions. |
| `PluginCtx<'Msg>` | What plugins receive during Update -- report status, report errors, emit events. |
| `CheckPipeline` | Incremental file checking with the warm FSharpChecker. |
| `ProjectGraph` | Tracks project dependencies and file ownership for transitive invalidation. |
| `FileWatcher` | Debounced file system monitoring (500ms source, 200ms project). |
| `IpcServer` / `IpcClient` | StreamJsonRpc over named pipes for CLI-to-daemon communication. |

## Event pipeline

```
File change -> FormatPreprocessor (rewrites) -> FileChanged
  +-- BuildPlugin -> dotnet build -> BuildCompleted
  |     +-- TestPrunePlugin -> affected tests -> TestCompleted
  |           +-- CoveragePlugin -> threshold check
  +-- FCS Check -> FileChecked
  |     +-- LintPlugin (warm AST + check results)
  |     +-- AnalyzersPlugin (warm check results)
  |     +-- TestPrunePlugin (symbol analysis)
  +-- FileCommandPlugin (matching files -> command)
```

## Writing a plugin

See the [main README](../../README.md#writing-your-own-plugin) for a
complete example using the declarative `PluginHandler` framework.

## Install

```bash
dotnet add package FsHotWatch
```
