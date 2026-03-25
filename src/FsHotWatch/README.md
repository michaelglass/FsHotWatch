# FsHotWatch

Core library for the FsHotWatch daemon. Provides the event system, plugin host,
file watcher, project graph, check pipeline, and IPC server.

## Key Types

- `Daemon` — ties together warm FSharpChecker, file watcher, plugin host
- `PluginHost` — manages plugin lifecycle, event dispatch, commands
- `PluginContext` — what plugins receive during initialization
- `IFsHotWatchPlugin` — interface for analysis plugins
- `IFsHotWatchPreprocessor` — interface for format-on-save preprocessors
- `CheckPipeline` — incremental file checking with warm FSharpChecker
- `ProjectGraph` — tracks project dependencies and file ownership
- `FileWatcher` — debounced file system monitoring
- `IpcServer` / `IpcClient` — StreamJsonRpc over named pipes

## Usage

See the [main README](../../README.md) for getting started.
