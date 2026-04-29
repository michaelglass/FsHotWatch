# Full Pipeline Example

Demonstrates wiring every FsHotWatch plugin in a consumer application against
the current `create`-style API (post-jj-removal, post-artifact-freshness).

```fsharp
open FsHotWatch
open FsHotWatch.PluginFramework
open FsHotWatch.ErrorLedger
open FsHotWatch.Build
open FsHotWatch.TestPrune
open FsHotWatch.Lint
open FsHotWatch.Analyzers
open FsHotWatch.Fantomas
open FsHotWatch.FileCommand

let repoRoot = System.IO.Directory.GetCurrentDirectory()
let daemon = Daemon.create repoRoot

// Preprocessor: format-on-save runs before events dispatch
daemon.RegisterPreprocessor(FormatPreprocessor())

// Read-only format check (reports unformatted files)
daemon.RegisterHandler(
    FormatCheckPlugin.createFormatCheck
        None    // timeoutSec
)

// FSharpLint
daemon.RegisterHandler(
    LintPlugin.create
        (Some "fsharplint.json")    // config path
        None                         // lintRunner override
        None                         // timeoutSec
)

// F# analyzers (warm FCS check results)
daemon.RegisterHandler(
    AnalyzersPlugin.create
        [ "examples/ExampleAnalyzer/bin/Debug/net10.0" ]   // analyzer paths
        None                                                // timeoutSec
        DiagnosticSeverity.Hint                             // failOnSeverity
)

// Build
daemon.RegisterHandler(
    BuildPlugin.create
        "dotnet"                 // command
        "build"                  // args
        []                       // env vars
        daemon.Graph             // IProjectGraphReader
        [ "MyApp.Tests" ]        // test project names (skip build on test-only edits)
        None                     // build template
        []                       // dependsOn
        None                     // timeoutSec
)

// Test impact analysis + execution
daemon.RegisterHandler(
    TestPrunePlugin.create
        ".fshw/test-impact.db"   // database path
        repoRoot                 // repo root
        (Some [
            { Project = "MyApp.Tests"
              Command = "dotnet"
              Args = "run --project tests/MyApp.Tests --no-build --"
              Group = "unit"
              Environment = []
              FilterTemplate = Some "--filter-class {classes}"
              ClassJoin = " " }
        ])                       // testConfigs
        None                     // buildExtensions
        None                     // beforeRun
        None                     // afterRun
        None                     // coveragePaths
)

// Custom file command — type-check .fsx scripts on save
let fsxTrigger: CommandTrigger =
    { FilePattern = Some(fun f -> f.EndsWith(".fsx"))
      AfterTests = None }

daemon.RegisterHandler(
    FileCommandPlugin.create
        (PluginName.create "scripts")           // plugin name
        fsxTrigger                              // CommandTrigger
        "dotnet"                                // command
        "fsi --typecheck-only build.fsx"        // args
        repoRoot                                // for resolving relative arg-file paths
        None                                    // timeoutSec
)

// Coverage ratchet — fires after every test run AND when its config file changes
let coverageTrigger: CommandTrigger =
    { FilePattern = Some(fun f -> f.EndsWith("coverage-ratchet.json"))
      AfterTests = Some AnyTest }

daemon.RegisterHandler(
    FileCommandPlugin.create
        (PluginName.create "coverage-ratchet")
        coverageTrigger
        "dotnet"
        "tool run coverageratchet check coverage-ratchet.json"
        repoRoot
        None
)

// Start with IPC
let cts = new System.Threading.CancellationTokenSource()
let pipeName = $"fshw-{System.IO.Path.GetFileName repoRoot}"
daemon.RunWithIpc(pipeName, cts) |> Async.RunSynchronously
```

See each package's README for per-plugin details:

- [FsHotWatch.Build](../../src/FsHotWatch.Build/)
- [FsHotWatch.TestPrune](../../src/FsHotWatch.TestPrune/)
- [FsHotWatch.Analyzers](../../src/FsHotWatch.Analyzers/)
- [FsHotWatch.Lint](../../src/FsHotWatch.Lint/)
- [FsHotWatch.Fantomas](../../src/FsHotWatch.Fantomas/)
- [FsHotWatch.FileCommand](../../src/FsHotWatch.FileCommand/)
