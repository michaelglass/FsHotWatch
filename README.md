<!-- sync:intro:start -->
# FsHotWatch

Speed up your F# development feedback loop.

FsHotWatch is a background daemon that watches your source files and
keeps the F# compiler warm. When you save a file, it instantly re-checks
it and tells your tools (linters, analyzers, test runners) what changed
— without restarting the compiler from scratch each time.
<!-- sync:intro:end -->

## The problem

F# tools are slow because they each start their own compiler from zero.
A 15-project solution takes ~2 minutes to analyze. Every time you save a
file, your linter restarts, your analyzer restarts, your test runner
restarts — all parsing and type-checking the same 500 files again.

## How FsHotWatch helps

FsHotWatch runs one compiler in the background and shares it with all
your tools:

1. **You save a file** — FsHotWatch notices the change
2. **It re-checks just that file** using the compiler that's already warm
   (milliseconds, not minutes)
3. **Plugins get the results instantly** — your linter, analyzer, and test
   runner all see the updated check results without re-parsing anything
4. **You query the results** — `fs-hot-watch status` shows what each tool found

Changes are debounced — if you save 10 files in quick succession (like
a formatter running), FsHotWatch waits for things to settle, then
processes them all in one batch.

## Quick start

```bash
# Install the CLI tool
dotnet tool install -g FsHotWatch.Cli

# Start the daemon in your repo (runs in foreground, Ctrl+C to stop)
fs-hot-watch start

# From another terminal, check what's happening
fs-hot-watch status
```

## Packages

FsHotWatch is split into small packages so you only install what you need:

| Package | What it does |
|---------|-------------|
| [`FsHotWatch`](src/FsHotWatch/) | Core library — the daemon, file watcher, plugin system, IPC |
| [`FsHotWatch.Cli`](src/FsHotWatch.Cli/) | CLI tool — `fs-hot-watch start/stop/status` |
| [`FsHotWatch.TestPrune`](src/FsHotWatch.TestPrune/) | Plugin: figures out which tests to run when code changes |
| [`FsHotWatch.Analyzers`](src/FsHotWatch.Analyzers/) | Plugin: runs F# analyzers (like [G-Research](https://github.com/G-Research/fsharp-analyzers) or your own) |
| [`FsHotWatch.Lint`](src/FsHotWatch.Lint/) | Plugin: runs FSharpLint using the warm compiler's results |
| [`FsHotWatch.Fantomas`](src/FsHotWatch.Fantomas/) | Plugin: checks if your files are formatted with Fantomas |

## Writing your own plugin

A plugin is an F# class that subscribes to events from the daemon.
Here's the simplest possible plugin:

```fsharp
open FsHotWatch.Events
open FsHotWatch.Plugin

type MyPlugin() =
    interface IFsHotWatchPlugin with
        member _.Name = "my-plugin"

        member _.Initialize(ctx) =
            // Called once when the daemon starts.
            // Subscribe to events you care about:
            ctx.OnFileChecked.Add(fun result ->
                // 'result' has .ParseResults and .CheckResults from the warm compiler.
                // Do your analysis here (lint, analyze, check tests, etc.)
                printfn "Checked: %s" result.File
                ctx.ReportStatus(Completed(box "done", System.DateTime.UtcNow)))

            // Register a command that users can query via CLI:
            ctx.RegisterCommand("my-status", fun _args ->
                async { return "everything is fine" })

        member _.Dispose() = ()
```

**Available events:**
- `ctx.OnFileChanged` — a source file was saved (you get the file path)
- `ctx.OnBuildCompleted` — `dotnet build` finished (success or failure)
- `ctx.OnFileChecked` — a file was type-checked (you get parse + check results)
- `ctx.OnProjectChecked` — all files in a project were checked

**What you can do in Initialize:**
- `ctx.Checker` — the warm FSharpChecker (reuse it for your own analysis)
- `ctx.RepoRoot` — path to the repository root
- `ctx.ReportStatus(status)` — tell the daemon your current status
- `ctx.RegisterCommand(name, handler)` — add a CLI command

## Architecture

See the [architecture design doc](docs/plans/2026-03-24-architecture-design.md)
for the full technical design.
