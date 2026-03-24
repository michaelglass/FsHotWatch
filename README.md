<!-- sync:intro:start -->
# FsHotWatch

F# file watcher daemon that keeps FSharpChecker warm for instant re-analysis,
linting, and test selection on file changes.
<!-- sync:intro:end -->

## Why?

Every F# tool cold-starts its own compiler: analyzers, linters, test impact
tools each parse and type-check your entire codebase from scratch. FsHotWatch
keeps one warm FSharpChecker alive and shares it with plugins — so checks
that took minutes happen in milliseconds.

## How it works

1. A daemon watches your source files for changes
2. On change, it re-checks affected files with the warm FSharpChecker
3. Plugins receive check results and produce diagnostics, lint warnings,
   affected test lists, format check results
4. Query results via CLI or StreamJsonRpc

## Packages

| Package | What it's for |
|---------|---------------|
| `FsHotWatch` | Core library — daemon, events, plugin host, IPC |
| `FsHotWatch.Cli` | CLI tool (`fs-hot-watch start/stop/status`) |
| `FsHotWatch.TestPrune` | Plugin: test impact analysis via TestPrune |
| `FsHotWatch.Analyzers` | Plugin: F# analyzer host (loads custom analyzer DLLs) |
| `FsHotWatch.Lint` | Plugin: FSharpLint with warm checker results |
| `FsHotWatch.Fantomas` | Plugin: Fantomas format checking |

## Quick start

```bash
# Install the CLI tool
dotnet tool install -g FsHotWatch.Cli

# Start the daemon in your repo
fs-hot-watch start

# Query status from another terminal
fs-hot-watch status
```

## Writing a plugin

Plugins implement `IFsHotWatchPlugin` and subscribe to events:

```fsharp
type MyPlugin() =
    interface IFsHotWatchPlugin with
        member _.Name = "my-plugin"
        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                // result.ParseResults and result.CheckResults
                // come from the warm FSharpChecker
                ctx.ReportStatus(Completed(box "done", DateTime.UtcNow)))
            ctx.RegisterCommand("my-command", fun args ->
                async { return "result" })
        member _.Dispose() = ()
```

## Architecture

See [docs/plans/2026-03-24-architecture-design.md](docs/plans/2026-03-24-architecture-design.md).
