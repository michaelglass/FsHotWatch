# FsHotWatch.Build

FsHotWatch plugin that runs a build command when source files change
and emits `BuildCompleted` events for downstream plugins (like test runners).

## Usage

```fsharp
// Default: runs "dotnet build --no-restore"
daemon.Register(BuildPlugin())

// Custom command
daemon.Register(BuildPlugin(command = "dotnet", args = "build -c Release --no-restore"))
```
