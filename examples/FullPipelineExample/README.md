# Full Pipeline Example

Demonstrates wiring every FsHotWatch plugin into one daemon in a consumer
application.

<!-- This block is sourced from a real, compiled example — see
     examples/FullPipelineExample/FullPipelineExample.fs. Do not edit it here;
     edit the source file and run `mise run sync-docs`. The example is a member
     of FsHotWatch.slnx, so the solution build (CI included) compiles it with
     TreatWarningsAsErrors; `mise run ci` additionally runs `sync-docs-check`,
     which fails if this block drifts from the source.
     It used to be hand-written, and it HAD rotted: it taught a `Daemon.create`
     and a `LintPlugin.create`/`AnalyzersPlugin.create` whose signatures no
     longer existed. Nothing compiled it, so nothing noticed. -->
<!-- sync:full-pipeline:start src=examples/FullPipelineExample/FullPipelineExample.fs -->
```fsharp
open FsHotWatch.Daemon
open FsHotWatch.ErrorLedger
open FsHotWatch.PluginFramework
open FsHotWatch.Build
open FsHotWatch.TestPrune
open FsHotWatch.Lint
open FsHotWatch.Analyzers
open FsHotWatch.Fantomas
open FsHotWatch.FileCommand
open FsHotWatch.Coverage
// The plugins' own types (TestConfig, CommandTrigger, FormatPreprocessor) live
// inside each plugin's module, so open the ones whose types you construct.
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.FileCommand.FileCommandPlugin

/// Wire every FsHotWatch plugin into one daemon and run it with IPC, so the
/// `fshw` CLI can talk to it.
let runFullPipeline (repoRoot: string) =
    // `DaemonOptions.defaults` and override only what you need.
    let daemon = Daemon.create repoRoot Daemon.DaemonOptions.defaults

    // Preprocessor: format-on-save runs BEFORE events dispatch, so formatting
    // a file on save doesn't re-trigger the whole pipeline.
    daemon.RegisterPreprocessor(FormatPreprocessor())

    // Read-only format check (reports unformatted files). Runs the Fantomas the
    // repository pins in `.config/dotnet-tools.json`, from `repoRoot`.
    daemon.RegisterHandler(
        FormatCheckPlugin.createFormatCheck repoRoot None // timeoutSec
    )

    // FSharpLint, using the warm checker's results.
    daemon.RegisterHandler(
        LintPlugin.create
            (Some repoRoot) // repoRoot
            (Some "fsharplint.json") // config path
            None // lintRunner override
            None // timeoutSec
    )

    // F# analyzers (warm FCS check results).
    daemon.RegisterHandler(
        AnalyzersPlugin.create
            (Some repoRoot) // repoRoot
            [ "examples/ExampleAnalyzer/bin/Debug/net10.0" ] // analyzer paths
            None // timeoutSec
            DiagnosticSeverity.Hint // failOnSeverity
    )

    // Build.
    daemon.RegisterHandler(
        BuildPlugin.create
            "dotnet" // command
            "build" // args
            [] // env vars
            daemon.Graph // IProjectGraphReader
            [ "MyApp.Tests" ] // test project names (skip build on test-only edits)
            None // build template
            [] // dependsOn
            None // timeoutSec
    )

    // Test impact analysis + execution.
    daemon.RegisterHandler(
        TestPrunePlugin.create
            ".fshw/test-impact.db" // database path
            repoRoot // repo root
            (Some
                [ { Project = "MyApp.Tests"
                    Command = "dotnet"
                    Args = "run --project tests/MyApp.Tests --no-build --"
                    Group = "unit"
                    Environment = []
                    FilterTemplate = Some "--filter-class {classes}"
                    ClassJoin = " "
                    TimeoutSec = None
                    ReportVerificationFormat = AutoDetect } ]) // testConfigs
            None // buildExtensions
            None // beforeRun
            None // afterRun
            None // coveragePaths
            [] // dependsOn — globs of external test inputs (migrations, schemas) that salt the test cache key
    )

    // Custom file command — type-check .fsx scripts on save.
    let fsxTrigger: CommandTrigger =
        { FilePattern = Some(fun f -> f.EndsWith ".fsx")
          AfterTests = None }

    daemon.RegisterHandler(
        FileCommandPlugin.create
            (PluginName.create "scripts") // plugin name
            fsxTrigger // CommandTrigger
            "dotnet" // command
            "fsi --typecheck-only build.fsx" // args
            repoRoot // for resolving relative arg-file paths
            None // timeoutSec
    )

    // Coverage thresholds — checked after each test run. Reads the Cobertura XML
    // the runner wrote into `searchDir` and compares against the per-file floors
    // in `configPath`.
    daemon.RegisterHandler(
        CoveragePlugin.create
            (System.IO.Path.Combine(repoRoot, "coverage-ratchet.json")) // configPath
            (System.IO.Path.Combine(repoRoot, "coverage")) // searchDir
    )

    // Start with IPC so the `fshw` CLI can reach this daemon.
    let cts = new System.Threading.CancellationTokenSource()
    let pipeName = $"fshw-{System.IO.Path.GetFileName repoRoot}"
    daemon.RunWithIpc(pipeName, cts) |> Async.RunSynchronously
```
<!-- sync:full-pipeline:end -->

See each package's README for per-plugin details:

- [FsHotWatch.Build](../../src/FsHotWatch.Build/)
- [FsHotWatch.TestPrune](../../src/FsHotWatch.TestPrune/)
- [FsHotWatch.Analyzers](../../src/FsHotWatch.Analyzers/)
- [FsHotWatch.Lint](../../src/FsHotWatch.Lint/)
- [FsHotWatch.Fantomas](../../src/FsHotWatch.Fantomas/)
- [FsHotWatch.Coverage](../../src/FsHotWatch.Coverage/)
- [FsHotWatch.FileCommand](../../src/FsHotWatch.FileCommand/)
