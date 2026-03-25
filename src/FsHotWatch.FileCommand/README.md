# FsHotWatch.FileCommand

FsHotWatch plugin that runs a command when specific files change.
Register multiple instances for multiple file patterns.

## Usage

```fsharp
// Type-check build.fsx when any .fsx file changes
daemon.Register(FileCommandPlugin(
    "scripts",
    (fun f -> f.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase)),
    "dotnet",
    "fsi --typecheck-only build.fsx"
))
```
