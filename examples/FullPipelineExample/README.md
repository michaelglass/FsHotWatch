# Full Pipeline Example

Demonstrates setting up all FsHotWatch plugins in a consumer application.

```fsharp
let daemon = Daemon.create repoRoot

// Preprocessor: auto-format on save
daemon.RegisterPreprocessor(FormatPreprocessor())

// Analysis plugins (use warm FSharpChecker)
daemon.Register(LintPlugin(configPath = "fsharplint.json"))
daemon.Register(AnalyzersPlugin([ analyzerPath ]))

// Build + test pipeline
daemon.Register(BuildPlugin())
daemon.Register(TestPrunePlugin(
    dbPath = ".test-prune.db",
    repoRoot = repoRoot,
    testConfigs = [
        { Project = "MyApp.Tests"
          Command = "dotnet"
          Args = "run --project tests/MyApp.Tests --no-build"
          Group = "unit"
          Environment = [] }
    ]
))

// Custom file command
daemon.Register(FileCommandPlugin(
    "scripts",
    (fun f -> f.EndsWith(".fsx")),
    "dotnet",
    "fsi --typecheck-only build.fsx"
))

// Start with IPC
let cts = new CancellationTokenSource()
daemon.RunWithIpc(pipeName, cts) |> Async.RunSynchronously
```
