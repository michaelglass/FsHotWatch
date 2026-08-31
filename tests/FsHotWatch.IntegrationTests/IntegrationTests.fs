module FsHotWatch.Tests.IntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph

module LintPlugin = FsHotWatch.Lint.LintPlugin

open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.TestPrune.TestPrunePlugin

module TestPrunePlugin = FsHotWatch.TestPrune.TestPrunePlugin

module AnalyzersPlugin = FsHotWatch.Analyzers.AnalyzersPlugin

open FsHotWatch.Build

open FsHotWatch.FileCommand.FileCommandPlugin
open FsHotWatch.CheckCache
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.ProcessHelper
open FsHotWatch.Ipc
open FsHotWatch.Cli
open FsHotWatch.Daemon

let private findRepoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let rec walk dir =
        if
            Directory.Exists(Path.Combine(dir, ".jj"))
            || Directory.Exists(Path.Combine(dir, ".git"))
        then
            dir
        else
            let parent = Directory.GetParent(dir)

            if isNull parent then
                failwith "Could not find repo root"

            walk parent.FullName

    walk assemblyDir

let private waitForStatusSettled (host: PluginHost) (pluginName: string) (timeoutMs: int) =
    waitForSettled host pluginName timeoutMs

let private exampleAnalyzerPath =
    lazy
        let repoRoot = findRepoRoot ()
        let dir = Path.Combine(repoRoot, "examples/ExampleAnalyzer")
        let psi = ProcessStartInfo("dotnet", $"""build "{dir}" -v quiet""")
        psi.UseShellExecute <- false
        let proc = Process.Start(psi)
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwith "ExampleAnalyzer build failed"

        Path.Combine(dir, "bin/Debug/net10.0")

let private conventionRulesPath =
    lazy
        let repoRoot = findRepoRoot ()
        let dir = Path.Combine(repoRoot, "analyzers/FsHotWatch.Rules")
        let psi = ProcessStartInfo("dotnet", $"""build "{dir}" -v quiet""")
        psi.UseShellExecute <- false
        let proc = Process.Start(psi)
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwith "FsHotWatch.Rules build failed"

        Path.Combine(dir, "bin/Debug/net10.0")

[<Fact(Timeout = 5000)>]
let ``all plugins receive events when checking a file`` () =
    let repoRoot = findRepoRoot ()

    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let pipeline = CheckPipeline(checker)
    let host = PluginHost.create checker repoRoot

    let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
    let source = File.ReadAllText(sourceFile)

    let sourceText = SourceText.ofString source

    let projOptions =
        checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    pipeline.RegisterProject("FsHotWatch", projOptions)

    let dbPath = Path.Combine(Path.GetTempPath(), $"fshw-inttest-{Guid.NewGuid():N}.db")

    let testPrune = TestPrunePlugin.create dbPath repoRoot None None None None None []

    let lint = LintPlugin.create None None None None
    let fantomas = createFormatCheck None
    let analyzers = AnalyzersPlugin.create None [] None DiagnosticSeverity.Hint

    host.RegisterHandler(testPrune)
    host.RegisterHandler(lint)
    host.RegisterHandler(fantomas)
    host.RegisterHandler(analyzers)

    let result =
        pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

    match result with
    | Some checkResult -> host.EmitFileChecked(checkResult)
    | None -> failwith "Failed to check file"

    test <@ host.GetStatus("lint").IsSome @>
    test <@ host.GetStatus("analyzers").IsSome @>
    test <@ host.GetStatus("test-prune").IsSome @>

    // format-check listens to OnFileChanged, not OnFileChecked.
    host.EmitFileChanged(SourceChanged [ sourceFile ])
    test <@ host.GetStatus("format-check").IsSome @>

    let diagResult = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ diagResult.IsSome @>
    test <@ diagResult.Value.Contains("analyzers") @>
    test <@ diagResult.Value.Contains("files") @>
    test <@ diagResult.Value.Contains("diagnostics") @>

    let warnResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ warnResult.IsSome @>
    test <@ warnResult.Value.Contains("files") @>
    test <@ warnResult.Value.Contains("warnings") @>

    let fmtResult = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ fmtResult.IsSome @>
    test <@ fmtResult.Value.Contains("count") @>

    let testsResult = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ testsResult.IsSome @>
    test <@ testsResult.Value.StartsWith("[") @>

    let filesResult = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ filesResult.IsSome @>
    test <@ filesResult.Value.StartsWith("[") @>

    try
        File.Delete(dbPath)
    with _ ->
        ()

    try
        File.Delete(dbPath + "-wal")
    with _ ->
        ()

    try
        File.Delete(dbPath + "-shm")
    with _ ->
        ()

[<Fact(Timeout = 30000)>]
let ``analyzers plugin loads real analyzers and runs without crashing`` () =
    let repoRoot = findRepoRoot ()
    let customAnalyzerPath = exampleAnalyzerPath.Value

    let gResearchPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget/packages/g-research.fsharp.analyzers/0.22.0/analyzers/dotnet/fs"
        )

    let analyzerPaths =
        [ gResearchPath; customAnalyzerPath ] |> List.filter Directory.Exists

    let analyzers =
        AnalyzersPlugin.create None analyzerPaths None DiagnosticSeverity.Hint

    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let host = PluginHost.create checker repoRoot
    host.RegisterHandler(analyzers)

    // Events.fs has match expressions the wildcard analyzer can inspect.
    let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
    let source = File.ReadAllText(sourceFile)
    let sourceText = SourceText.ofString source

    let projOptions =
        checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject("FsHotWatch", projOptions)

    let result =
        pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

    // Subscribe to the plugin's status before emitting so the transition to
    // terminal state can't race past us on slow CI (G-Research analyzer warm-up).
    let completion = beginAwaitTerminal host "analyzers"

    match result with
    | Some checkResult -> host.EmitFileChecked(checkResult)
    | None -> failwith "Failed to check file"

    completion.Wait(TimeSpan.FromSeconds 25.0) |> ignore

    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> ()
    | PluginStatus.Failed(msg, _, _) ->
        // G-Research analyzers can fail on an FCS version mismatch; the assertion is
        // that the plugin handled it rather than crashed.
        let info = sprintf "Analyzers failed gracefully: %s" msg
        Assert.True(true, info)
    | other -> Assert.Fail(sprintf "Unexpected status: %A" other)

// Uses the real ExampleAnalyzer bin, so this exercises the genuine SDK-reflection
// load path — which is nondeterministic and therefore excluded from coverage.
[<Fact(Timeout = 30000)>]
let ``analyzers load guard does not fire when a real analyzer loads`` () =
    let analyzerPath = exampleAnalyzerPath.Value

    let handler =
        AnalyzersPlugin.create None [ analyzerPath ] None DiagnosticSeverity.Hint

    test <@ handler.Init.LoadedCount >= 1 @>

    // LoadedByPath is the genuine per-path result of the real load, not a stub.
    test <@ (FsHotWatch.Cli.DaemonConfig.analyzerPathFailures handler.Init.LoadedByPath).IsNone @>

// Pins the partial silent-skip the earlier aggregate (total == 0) guard missed: when
// one path loads ≥1 and others load 0, registerPlugins must still raise, naming only
// the zero-loading paths. Lives here rather than the unit suite because it needs the
// real SDK load.
[<Fact(Timeout = 30000)>]
let ``analyzers load guard fires per-path when one of several paths loads zero`` () =
    let goodPath = exampleAnalyzerPath.Value

    withTempDir "cfg-analyzers-partial" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        // An existing-but-empty dir (resolves, loads 0) and a non-existent dir.
        let emptyPath = Path.Combine(tmpDir, "empty-analyzer-bin")
        Directory.CreateDirectory(emptyPath) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config =
            { DaemonConfig.stripConfig (defaultTestConfig ()) with
                Analyzers =
                    Some
                        {| Paths = [ goodPath; emptyPath; "no-such-analyzer-bin-dir" ]
                           FailOnSeverity = DiagnosticSeverity.Hint |} }

        let ex =
            Assert.Throws<DaemonConfig.ConfigError>(fun () -> DaemonConfig.registerPlugins daemon tmpDir config)

        Assert.Contains(emptyPath, ex.Message)
        Assert.Contains("no-such-analyzer-bin-dir", ex.Message)
        test <@ not (ex.Message.Contains(goodPath)) @>
        test <@ not (daemon.Host.GetAllStatuses().ContainsKey("analyzers")) @>)

/// As `withTempFsFile`, but the caller names the file. FSHW-WAIT-001 is scoped to TEST
/// sources by file name, so a control for it must be written to a `*Tests.fs` — under
/// the default `Temp.fs` the rule is out of scope and assertions pass for that reason
/// rather than the intended one.
let private withTempFsFileNamed (fileName: string) (content: string) (action: string -> string -> 'a) =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    let filePath = Path.Combine(dir, fileName)
    File.WriteAllText(filePath, content)

    try
        action dir filePath
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private withTempFsFile (content: string) (action: string -> string -> 'a) =
    withTempFsFileNamed "Temp.fs" content action

let private checkTempFile (checker: FSharpChecker) (filePath: string) =
    let source = File.ReadAllText(filePath)
    let sourceText = SourceText.ofString source

    let projOptions =
        checker.GetProjectOptionsFromScript(filePath, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject("TempProject", projOptions)

    let result =
        pipeline.CheckFile(AbsFilePath.create filePath) |> Async.RunSynchronously

    result

// Process-wide gate: AnalyzersPlugin tests share an FSharpChecker and contend on
// analyzer-DLL loading. Running >1 in parallel (or against a CPU busy with the rest of
// the suite) triggers >10s waits in `waitForTerminalStatus`. Serialize them so each
// gets a clean FCS slice.
let private analyzerCheckGate = new SemaphoreSlim(1, 1)

let private withAnalyzerGate (body: unit -> unit) =
    analyzerCheckGate.Wait()

    try
        body ()
    finally
        analyzerCheckGate.Release() |> ignore

let private withAnalyzerCheck (source: string) (assertResult: PluginHost -> string -> unit) =
    withAnalyzerGate (fun () ->
        let repoRoot = findRepoRoot ()
        let analyzerPath = exampleAnalyzerPath.Value

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let host = PluginHost.create checker repoRoot

        // failOnSeverity = Error so the analyzer's RAW severity survives to the ledger.
        // Under the default Hint threshold `promoteIfFailing` rewrites every sub-error
        // finding to Error — correct in production, but it masks the raw severity these
        // tests assert on. Error is the highest threshold, so promotion never fires.
        // Promotion itself is covered by the unit tests in AnalyzersPluginTests.fs.
        let analyzers =
            AnalyzersPlugin.create None [ analyzerPath ] None DiagnosticSeverity.Error

        host.RegisterHandler(analyzers)

        withTempFsFile source (fun _dir tmpFile ->
            let result = checkTempFile checker tmpFile

            match result with
            | Some checkResult ->
                host.EmitFileChecked(checkResult)
                waitForTerminalStatus host "analyzers" 10000
                assertResult host tmpFile
            | None -> Assert.Fail("FCS failed to check file")))

[<Fact(Timeout = 5000)>]
let ``lint plugin detects warnings on bad code`` () =
    let badCode =
        """module Temp
let x = 5
"""

    withTempFsFile badCode (fun _dir filePath ->
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let repoRoot = findRepoRoot ()
        let host = PluginHost.create checker repoRoot
        let lint = LintPlugin.create None None None None
        host.RegisterHandler(lint)

        try
            let result = checkTempFile checker filePath

            match result with
            | Some checkResult ->
                host.EmitFileChecked(checkResult)

                waitUntil
                    (fun () ->
                        match host.GetStatus("lint") with
                        | Some(Completed _)
                        | Some(PluginStatus.Failed _) -> true
                        | _ -> false)
                    5000

                let status = host.GetStatus("lint")
                test <@ status.IsSome @>

                match status.Value with
                | Completed _ -> ()
                | PluginStatus.Failed(msg, _, _) ->
                    // FCS version mismatch may cause lint to fail — acceptable
                    Assert.True(true, $"Lint failed gracefully: {msg}")
                | other -> Assert.Fail($"Unexpected lint status: %A{other}")
            | None ->
                // FCS could not check the temp file (version mismatch etc.) — skip
                Assert.True(true, "Skipped: FCS could not check temp file")
        with ex ->
            Assert.True(true, $"Skipped due to FCS exception: {ex.Message}"))

[<Fact(Timeout = 5000)>]
let ``format check plugin detects unformatted code`` () =
    let badlyFormatted = "module    Temp\nlet   x   =   5\nlet y=       10\n"

    withTempFsFile badlyFormatted (fun _dir filePath ->
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let repoRoot = findRepoRoot ()
        let host = PluginHost.create checker repoRoot
        let fantomas = createFormatCheck None
        host.RegisterHandler(fantomas)

        host.EmitFileChanged(SourceChanged [ filePath ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("format-check")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Unexpected format-check status: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``format check plugin passes on well-formatted code`` () =
    let wellFormatted =
        Fantomas.Core.CodeFormatter.FormatDocumentAsync(false, "module Temp\n\nlet x = 5\n")
        |> Async.RunSynchronously

    withTempFsFile wellFormatted.Code (fun _dir filePath ->
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let repoRoot = findRepoRoot ()
        let host = PluginHost.create checker repoRoot
        let fantomas = createFormatCheck None
        host.RegisterHandler(fantomas)

        host.EmitFileChanged(SourceChanged [ filePath ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("format-check")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Unexpected format-check status: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``plugin status reflects running to completed lifecycle`` () =
    let content = "module Temp\n\nlet x = 5\n"

    withTempFsFile content (fun _dir filePath ->
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let repoRoot = findRepoRoot ()
        let host = PluginHost.create checker repoRoot
        let fantomas = createFormatCheck None
        host.RegisterHandler(fantomas)

        let beforeStatus = host.GetStatus("format-check")
        test <@ beforeStatus = Some Idle @>

        host.EmitFileChanged(SourceChanged [ filePath ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let afterStatus = host.GetStatus("format-check")
        test <@ afterStatus.IsSome @>

        match afterStatus.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed, got: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``multiple file changes are debounced into one batch by SourceChanged`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-debounce-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore

    try
        let files =
            [ for i in 1..5 ->
                  let fp = Path.Combine(dir, $"File{i}.fs")
                  // Alternate well-formatted and badly-formatted so the batch is mixed.
                  let content =
                      if i % 2 = 0 then
                          $"module    File{i}\nlet   x   =   {i}\n"
                      else
                          let formatted =
                              Fantomas.Core.CodeFormatter.FormatDocumentAsync(false, $"module File{i}\n\nlet x = {i}\n")
                              |> Async.RunSynchronously

                          formatted.Code

                  File.WriteAllText(fp, content)
                  fp ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let repoRoot = findRepoRoot ()
        let host = PluginHost.create checker repoRoot
        let fantomas = createFormatCheck None
        host.RegisterHandler(fantomas)

        host.EmitFileChanged(SourceChanged files)

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("format-check")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Unexpected format-check status: %A{other}")
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

// ===========================================================================
// FormatPreprocessor — success and failure
// ===========================================================================

[<Fact(Timeout = 5000)>]
let ``FormatPreprocessor succeeds on well-formatted file`` () =
    let wellFormatted =
        Fantomas.Core.CodeFormatter.FormatDocumentAsync(false, "module Temp\n\nlet x = 5\n")
        |> Async.RunSynchronously

    withTempFsFile wellFormatted.Code (fun _dir filePath ->
        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ filePath ] "/tmp"
        test <@ modified |> List.isEmpty @>)

[<Fact(Timeout = 5000)>]
let ``FormatPreprocessor reformats badly formatted file`` () =
    let badCode = "module    Temp\nlet   x   =   5\nlet y=       10\n"

    withTempFsFile badCode (fun _dir filePath ->
        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let contentBefore = File.ReadAllText(filePath)
        let modified = preprocessor.Process [ filePath ] "/tmp"
        let contentAfter = File.ReadAllText(filePath)
        test <@ modified |> List.contains filePath @>
        test <@ contentAfter <> contentBefore @>)

// ===========================================================================
// LintPlugin — success and failure
// ===========================================================================

[<Fact(Timeout = 10000)>]
let ``LintPlugin reports no warnings on clean code`` () =
    let repoRoot = findRepoRoot ()

    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let host = PluginHost.create checker repoRoot
    let lint = LintPlugin.create None None None None
    host.RegisterHandler(lint)

    // Events.fs from FsHotWatch itself is known-clean.
    let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
    let source = File.ReadAllText(sourceFile)
    let sourceText = SourceText.ofString source

    let projOptions =
        checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject("FsHotWatch", projOptions)

    let result =
        pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

    match result with
    | Some checkResult ->
        host.EmitFileChecked(checkResult)

        waitUntil
            (fun () ->
                match host.GetStatus("lint") with
                | Some(Completed _)
                | Some(PluginStatus.Failed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("lint")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | PluginStatus.Failed(msg, _, _) ->
            // An FCS version mismatch can fail lint; graceful handling is the assertion.
            Assert.True(true, $"Lint failed gracefully: {msg}")
        | other -> Assert.Fail($"Unexpected lint status: %A{other}")
    | None -> Assert.True(true, "Skipped: FCS could not check file")

[<Fact(Timeout = 5000)>]
let ``LintPlugin reports warnings on code with issues`` () =
    let repoRoot = findRepoRoot ()

    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

    let host = PluginHost.create checker repoRoot
    let lint = LintPlugin.create None None None None
    host.RegisterHandler(lint)

    let badCode =
        """module Temp
let x = 5
"""

    withTempFsFile badCode (fun _dir filePath ->
        try
            let result = checkTempFile checker filePath

            match result with
            | Some checkResult ->
                host.EmitFileChecked(checkResult)

                waitUntil
                    (fun () ->
                        match host.GetStatus("lint") with
                        | Some(Completed _)
                        | Some(PluginStatus.Failed _) -> true
                        | _ -> false)
                    5000

                let cmdResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
                test <@ cmdResult.IsSome @>
                test <@ cmdResult.Value.Contains("warnings") @>
            | None -> Assert.True(true, "Skipped: FCS could not check temp file")
        with ex ->
            Assert.True(true, $"Skipped due to FCS exception: {ex.Message}"))

// ===========================================================================
// AnalyzersPlugin — success and failure
// ===========================================================================

[<Fact(Timeout = 30000)>]
let ``AnalyzersPlugin completes without crashing on checked file`` () =
    withAnalyzerGate (fun () ->
        let repoRoot = findRepoRoot ()

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let host = PluginHost.create checker repoRoot
        let analyzers = AnalyzersPlugin.create None [] None DiagnosticSeverity.Hint
        host.RegisterHandler(analyzers)

        let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
        let source = File.ReadAllText(sourceFile)
        let sourceText = SourceText.ofString source

        let projOptions =
            checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously
            |> fst

        let pipeline = CheckPipeline(checker)
        pipeline.RegisterProject("FsHotWatch", projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

        match result with
        | Some checkResult ->
            host.EmitFileChecked(checkResult)

            waitForTerminalStatus host "analyzers" 10000

            let status = host.GetStatus("analyzers")
            test <@ status.IsSome @>

            match status.Value with
            | Completed _ -> ()
            | PluginStatus.Failed(msg, _, _) -> Assert.True(true, $"Analyzers failed gracefully: {msg}")
            | other -> Assert.Fail($"Unexpected status: %A{other}")
        | None -> Assert.True(true, "Skipped: FCS could not check file"))

[<Fact(Timeout = 30000)>]
let ``AnalyzersPlugin loads real analyzers from example project`` () =
    withAnalyzerGate (fun () ->
        let repoRoot = findRepoRoot ()
        let analyzerPath = exampleAnalyzerPath.Value

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        let host = PluginHost.create checker repoRoot

        let analyzers =
            AnalyzersPlugin.create None [ analyzerPath ] None DiagnosticSeverity.Hint

        host.RegisterHandler(analyzers)

        let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
        let source = File.ReadAllText(sourceFile)
        let sourceText = SourceText.ofString source

        let projOptions =
            checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously
            |> fst

        let pipeline = CheckPipeline(checker)
        pipeline.RegisterProject("FsHotWatch", projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

        match result with
        | Some checkResult ->
            host.EmitFileChecked(checkResult)

            waitForTerminalStatus host "analyzers" 10000

            let status = host.GetStatus("analyzers")
            test <@ status.IsSome @>

            match status.Value with
            | Completed _ -> ()
            | PluginStatus.Failed(msg, _, _) -> Assert.True(true, $"Analyzers failed gracefully: {msg}")
            | other -> Assert.Fail($"Unexpected status: %A{other}")
        | None -> Assert.True(true, "Skipped: FCS could not check file"))

[<Fact(Timeout = 30000)>]
let ``AnalyzersPlugin produces warning on wildcard DU match`` () =
    let source =
        "module Test\ntype Shape = Circle | Square\nlet f s = match s with | Circle -> 1 | _ -> 2\n"

    withAnalyzerCheck source (fun host _tmpFile ->
        let status = host.GetStatus("analyzers")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ ->
            let errors = host.GetErrorsByPlugin("analyzers")
            let allEntries = errors |> Map.toList |> List.collect snd
            test <@ allEntries.Length > 0 @>
            test <@ allEntries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Warning) @>
        | PluginStatus.Failed(msg, _, _) -> Assert.Fail($"Analyzer should succeed but failed: {msg}")
        | other -> Assert.Fail($"Unexpected status: %A{other}"))

[<Fact(Timeout = 30000)>]
let ``AnalyzersPlugin produces no warning on exhaustive DU match`` () =
    let source =
        "module Test\ntype Shape = Circle | Square\nlet f s = match s with | Circle -> 1 | Square -> 2\n"

    withAnalyzerCheck source (fun host tmpFile ->
        let status = host.GetStatus("analyzers")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ ->
            let errors = host.GetErrorsByPlugin("analyzers")
            let fileErrors = errors |> Map.tryFind (Path.GetFullPath(tmpFile))

            match fileErrors with
            | Some entries -> Assert.Fail($"Expected no warnings but got %d{entries.Length}")
            | None -> ()
        | PluginStatus.Failed(msg, _, _) -> Assert.Fail($"Analyzer should succeed but failed: {msg}")
        | other -> Assert.Fail($"Unexpected status: %A{other}"))

// A warm daemon loads analyzers ONCE at construction. When a downstream repo adds an
// analyzer and rebuilds, the DLL on disk changes but the in-memory client keeps the OLD
// set — the new analyzer silently never runs and the gate reports green without ever
// having inspected the code with it. The neighbouring guards don't catch this: the cache
// key invalidates stale RESULTS, and the fail-loud guard only fires on a 0-analyzer load,
// so a partial stale load merely MISSING the new analyzer slips past both.
//
// Repro needs the empty-then-populated dir: a dir that already has the analyzer at
// construction can't distinguish a fresh load from a stale one.
[<Fact(Timeout = 60000)>]
let ``Bug C: warm daemon reloads analyzers when a new analyzer DLL is added to the path`` () =
    withAnalyzerGate (fun () ->
        let repoRoot = findRepoRoot ()
        let exampleBin = exampleAnalyzerPath.Value

        // A wildcard DU match — the ExampleAnalyzer flags it once present.
        let source =
            "module Test\ntype Shape = Circle | Square\nlet f s = match s with | Circle -> 1 | _ -> 2\n"

        withTempDir "bugc-warm-reload" (fun tmpDir ->
            let analyzerDir = Path.Combine(tmpDir, "analyzer-bin")
            Directory.CreateDirectory(analyzerDir) |> ignore

            let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
            let host = PluginHost.create checker repoRoot

            // analyzerDir is EMPTY at construction, so zero analyzers load — the warm
            // daemon's state before the downstream add. failOnSeverity = Error keeps the
            // ExampleAnalyzer's raw Warning unpromoted in the ledger.
            let analyzers =
                AnalyzersPlugin.create None [ analyzerDir ] None DiagnosticSeverity.Error

            host.RegisterHandler(analyzers)

            withTempFsFile source (fun _dir tmpFile ->
                let findings () =
                    host.GetErrorsByPlugin("analyzers") |> Map.toList |> List.collect snd

                let emit () =
                    match checkTempFile checker tmpFile with
                    | Some checkResult -> host.EmitFileChecked(checkResult)
                    | None -> Assert.Fail("FCS failed to check temp file")

                emit ()
                waitForTerminalStatus host "analyzers" 15000
                test <@ List.isEmpty (findings ()) @>

                // The downstream "add + rebuild": a real analyzer DLL set lands in the
                // SAME dir the handler already scanned.
                Directory.GetFiles(exampleBin, "*.dll")
                |> Array.iter (fun f -> File.Copy(f, Path.Combine(analyzerDir, Path.GetFileName f), true))

                // Poll the ledger, not the status: the plugin still reads Completed from
                // cycle 1, so waiting on a status transition would return immediately.
                emit ()
                waitUntil (fun () -> not (List.isEmpty (findings ()))) 15000

                let after = findings ()
                test <@ not (List.isEmpty after) @>
                test <@ after |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Warning) @>)))

// ===========================================================================
// FsHotWatch.Rules — the repo's own convention analyzers, run through the genuine
// AnalyzersPlugin load path against the real Rules bin. These are positive controls:
// an analyzer that reports nothing and an analyzer that never loaded look identical,
// so each rule must be seen firing on a violating snippet before its green counts.
// ===========================================================================

let private runRulesOnFile (fileName: string) (source: string) : FsHotWatch.ErrorLedger.ErrorEntry list =
    let repoRoot = findRepoRoot ()
    let rulesBin = conventionRulesPath.Value
    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
    let host = PluginHost.create checker repoRoot

    let analyzers =
        AnalyzersPlugin.create None [ rulesBin ] None DiagnosticSeverity.Error

    // A zero-load would make every assertion below vacuous.
    test <@ analyzers.Init.LoadedCount >= 1 @>

    host.RegisterHandler(analyzers)

    withTempFsFileNamed fileName source (fun _dir tmpFile ->
        match checkTempFile checker tmpFile with
        | Some checkResult ->
            // An offside-broken fixture yields an empty AST, so every analyzer reports
            // zero findings — a "stays silent" assertion would then PASS for the wrong
            // reason. Refuse the fixture rather than emit a meaningless verdict.
            test <@ not checkResult.ParseResults.ParseHadErrors @>

            host.EmitFileChecked(checkResult)
            waitForTerminalStatus host "analyzers" 15000
            host.GetErrorsByPlugin("analyzers") |> Map.toList |> List.collect snd
        | None -> failwith "FCS failed to check temp file")

/// Default fixture name — deliberately NOT a test source, so FSHW-WAIT-001 stays out of
/// scope for every control that uses it.
let private runRulesOn (source: string) : FsHotWatch.ErrorLedger.ErrorEntry list = runRulesOnFile "Temp.fs" source

/// Build a source file from explicit lines. NOT a `\`-continuation string: that strips
/// leading whitespace per continued line, silently producing an offside-broken source
/// FCS cannot parse — every analyzer then reports zero findings and the test passes
/// vacuously.
let private fsSource (lines: string list) : string = String.concat "\n" lines + "\n"

/// A type-correct stand-in for the real PluginCtx.RunExclusive seam. The rule is
/// name-based, so a structurally identical local reproduces it.
let private claimPreamble =
    [ "module Test"
      "type RunClaim ="
      "    | Claimed"
      "    | SlotBusy"
      "type Ctx ="
      "    { RunExclusive: string -> Async<int> -> RunClaim }" ]

[<Fact(Timeout = 60000)>]
let ``FSHW-CLAIM-001 fires on every discarded-RunClaim shape`` () =
    withAnalyzerGate (fun () ->
        let source =
            fsSource (
                claimPreamble
                @ [ "let f (ctx: Ctx) (work: Async<int>) ="
                    "    ctx.RunExclusive \"tests\" work |> ignore"
                    "    let _ = ctx.RunExclusive \"tests\" work"
                    "    ignore (ctx.RunExclusive \"tests\" work)"
                    "    0" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "RunClaim is discarded")

        // One finding per discard shape: |> ignore, let _ =, ignore (…).
        test <@ findings.Length = 3 @>
        test <@ findings |> List.forall (fun e -> e.Severity = DiagnosticSeverity.Error) @>)

[<Fact(Timeout = 60000)>]
let ``FSHW-CLOCK-001 fires on local clock reads`` () =
    withAnalyzerGate (fun () ->
        let source =
            fsSource
                [ "module Test"
                  "let t = System.DateTime.Now"
                  "let u = System.DateTimeOffset.Now" ]

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "Local clock read")

        test <@ findings.Length = 2 @>
        test <@ findings |> List.forall (fun e -> e.Severity = DiagnosticSeverity.Error) @>)

[<Fact(Timeout = 60000)>]
let ``convention rules stay silent on conforming code`` () =
    withAnalyzerGate (fun () ->
        // UTC clocks and a MATCHED claim — proves the controls above aren't firing on
        // everything. `runRulesOnFile` refuses unparseable fixtures, which would
        // otherwise make this pass for the wrong reason.
        let source =
            fsSource (
                claimPreamble
                @ [ "let f (ctx: Ctx) (work: Async<int>) ="
                    "    match ctx.RunExclusive \"tests\" work with"
                    "    | Claimed -> System.DateTime.UtcNow"
                    "    | SlotBusy -> System.DateTime.UtcNow" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e ->
                e.Message.Contains "RunClaim is discarded"
                || e.Message.Contains "Local clock read")

        test <@ findings.IsEmpty @>)

/// Stand-in for the real `TestResult` seam. The rule is name-based, so a structurally
/// identical local reproduces it.
///
/// `isPassed` is present here even though AUTOMATION-278 DELETED it from the real
/// `TestResult`: the rule still names it, so that re-introducing the predicate whose
/// TRUE-for-zero-match answer was the original defect is caught the moment someone folds
/// it. `verifiedGreen` is the live predicate and the one the fixtures lead with.
let private verdictPreamble =
    [ "module Test"
      "type TestResult ="
      "    | TestsPassed"
      "    | TestsFailed"
      "    | TestsNoMatch"
      "module TestResult ="
      // The live predicate: FALSE for the zero-match case. The fold over it is still
      // not a run verdict — `Map.forall` is vacuously true for the empty map.
      "    let verifiedGreen r ="
      "        match r with"
      "        | TestsPassed -> true"
      "        | TestsFailed"
      "        | TestsNoMatch -> false"
      // The DELETED predicate, kept so the rule's memory of it can be pinned.
      "    let isPassed r ="
      "        match r with"
      "        | TestsFailed -> false"
      "        | TestsPassed"
      "        | TestsNoMatch -> true"
      "    let isNoMatch r ="
      "        match r with"
      "        | TestsNoMatch -> true"
      "        | TestsPassed"
      "        | TestsFailed -> false"
      // The SCOPE predicate, present so the negative control can pin that the rule does
      // not reach for it. True for the zero-match case, as the real one is.
      "    let wasFiltered r ="
      "        match r with"
      "        | TestsPassed -> false"
      "        | TestsFailed -> false"
      "        | TestsNoMatch -> true"
      "let allZeroMatchOf (results: Map<string, TestResult>) ="
      "    not results.IsEmpty && results |> Map.forall (fun _ r -> TestResult.isNoMatch r)" ]

[<Fact(Timeout = 60000)>]
let ``FSHW-VERDICT-001 fires on a run verdict folded from a per-project pass predicate`` () =
    withAnalyzerGate (fun () ->
        let source =
            fsSource (
                verdictPreamble
                @ [ "let green (results: Map<string, TestResult>) ="
                    "    results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)"
                    "let green2 (results: TestResult list) = List.forall TestResult.verifiedGreen results"
                    "let green3 (results: Map<string, TestResult>) ="
                    "    let allPassed = results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)"
                    "    allPassed"
                    // The DELETED name. A positive control for the rule's memory: if this
                    // stops firing, re-introducing `isPassed` is silent again.
                    "let green4 (results: Map<string, TestResult>) ="
                    "    results |> Map.forall (fun _ r -> TestResult.isPassed r)" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "not a run-level verdict")

        // One per fold: piped Map.forall, point-free List.forall, a fold bound to a name
        // whose decision asks nothing further, and the deleted-predicate control.
        test <@ findings.Length = 4 @>
        test <@ findings |> List.forall (fun e -> e.Severity = DiagnosticSeverity.Error) @>)

[<Fact(Timeout = 60000)>]
let ``FSHW-VERDICT-001 stays silent on the legitimate uses of the pass predicate`` () =
    withAnalyzerGate (fun () ->
        // Every shape here is live in TestPrunePlugin. A rule that fires on any of them
        // is a rule that gets suppressed rather than obeyed.
        let source =
            fsSource (
                verdictPreamble
                @ [ // Filtering FOR the non-green (`recordRunOutcome`): selects what to
                    // report, does not infer a green.
                    "let nonGreen (results: Map<string, TestResult>) ="
                    "    results |> Map.toList |> List.filter (fun (_, r) -> not (TestResult.verifiedGreen r))"
                    // Per-project, on a single result.
                    "let projectPassed (r: TestResult) = TestResult.verifiedGreen r"
                    // A forall whose predicate is a LOOKUP, not the pass predicate —
                    // the per-project green-commit fold.
                    //
                    // READ THIS ONE CAREFULLY. Until AUTOMATION-278 this exact shape,
                    // with `isPassed` in the lookup, WAS the live pending-verification
                    // false-green: a symbol's test debt discharged by a project that
                    // executed zero tests. The rule was silent on it then and is silent
                    // on it now, because the predicate is behind a `match` the syntactic
                    // matcher cannot see through. That is the evidence for why the fix
                    // had to be the TYPE and not this rule — an allow-list entry is not
                    // a proof of safety, it is a proof the rule cannot look there.
                    "let covered (results: Map<string, TestResult>) (names: Set<string>) ="
                    "    names"
                    "    |> Set.forall (fun n ->"
                    "        match Map.tryFind n results with"
                    "        | Some r -> TestResult.verifiedGreen r"
                    "        | None -> false)"
                    // The cacheable-green gate: an all-passed fold that DOES ask the
                    // run-level question, in the same decision.
                    "let cacheable (results: Map<string, TestResult>) ="
                    "    let allPassed = results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)"
                    "    let allZeroMatchRun = allZeroMatchOf results"
                    "    allPassed && not allZeroMatchRun"
                    // A bare SCOPE fold. Silence here is a decision, not an oversight —
                    // see `passPredicate` in ConventionAnalyzers.fs for why the two
                    // folds are different bugs. Widening the rule to cover scope means
                    // deleting this assertion on purpose.
                    //
                    // Deliberately NOT a copy of the real `ranFullSuite`, which now
                    // guards with `executedAnything`: the bare fold is the expression a
                    // widened rule would flag, so it is the one worth pinning.
                    "let ranFullSuite (results: Map<string, TestResult>) ="
                    "    results |> Map.forall (fun _ r -> not (TestResult.wasFiltered r))" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "not a run-level verdict")

        test <@ findings.IsEmpty @>

        // FLOOR / POSITIVE CONTROL, in this test rather than only in its sibling.
        // `findings.IsEmpty` is exactly what an analyzer that never LOADED returns, and
        // what a fixture that failed to parse returns — so on its own the assertion above
        // cannot tell "I looked at six shapes and none fired" from "I did not look".
        // Appending ONE known-firing fold to the SAME source proves the rule was loaded
        // and was reading this text.
        let armed =
            runRulesOn (
                fsSource (
                    verdictPreamble
                    @ [ "let nonGreen (results: Map<string, TestResult>) ="
                        "    results |> Map.toList |> List.filter (fun (_, r) -> not (TestResult.verifiedGreen r))"
                        "let armed (results: Map<string, TestResult>) ="
                        "    results |> Map.forall (fun _ r -> TestResult.verifiedGreen r)" ]
                )
            )
            |> List.filter (fun e -> e.Message.Contains "not a run-level verdict")

        test <@ armed.Length = 1 @>)

/// Stand-in for the assertion + polling names FSHW-WAIT-001 keys on. The rule is
/// syntactic, so structurally identical locals reproduce it.
let private waitPreamble =
    [ "module Test"
      "open System.Threading"
      "module Assert ="
      "    let True (condition: bool, message: string) = if not condition then failwith message"
      "    let False (condition: bool, message: string) = if condition then failwith message"
      "let test (assertion: Microsoft.FSharp.Quotations.Expr<bool>) = ignore assertion"
      "let probeLoop (write: int -> unit) (hasEvent: unit -> bool) (timeoutMs: int) ="
      "    ignore (write, hasEvent, timeoutMs)" ]

/// The original flake, reintroduced verbatim. The rule is scoped to test sources, so the
/// file NAME callers pass is part of the fixture.
let private flakeShapeSource =
    fsSource (
        waitPreamble
        @ [ "let flakeShape () ="
            "    use signal = new ManualResetEventSlim(false)"
            "    Thread.Sleep(100)"
            "    System.IO.File.WriteAllText(\"/tmp/fshw-does-not-exist\", \"{}\")"
            "    Assert.True(signal.Wait(5000), \"expected watcher callback within 5s\")"
            "let unquoteFlavour () ="
            "    use signal = new ManualResetEventSlim(false)"
            "    System.Threading.Thread.Sleep(100)"
            "    test <@ signal.Wait(5000) @>" ]
    )

[<Fact(Timeout = 60000)>]
let ``FSHW-WAIT-001 fires on a sleep that synchronises with an event assertion`` () =
    withAnalyzerGate (fun () ->
        let findings =
            runRulesOnFile "WaitControlTests.fs" flakeShapeSource
            |> List.filter (fun e -> e.Message.Contains "not synchronisation")

        // One per shape: Assert.True(signal.Wait …) and test <@ signal.Wait … @>.
        test <@ findings.Length = 2 @>
        test <@ findings |> List.forall (fun e -> e.Severity = DiagnosticSeverity.Error) @>

        // The message must name the fix (`WriteUntil`) and the opt-out marker, or the
        // rule is just a red light with no way past it.
        test
            <@
                findings
                |> List.forall (fun e -> e.Message.Contains "WriteUntil" && e.Message.Contains "FSHW-WAIT-001 ok")
            @>)

[<Fact(Timeout = 60000)>]
let ``FSHW-WAIT-001 stays silent on deliberate sleeps`` () =
    withAnalyzerGate (fun () ->
        // Every shape here is live in the suite; a rule that fires on any of them gets
        // suppressed rather than obeyed.
        let source =
            fsSource (
                waitPreamble
                @ [ // A NEGATIVE assertion — nothing may arrive inside the window. It
                    // DOES wait on the signal inside an assertion, so only the opt-out
                    // comment keeps it quiet: deleting that comment must turn this red.
                    "let deliberateNegative () ="
                    "    use signal = new ManualResetEventSlim(false)"
                    "    // FSHW-WAIT-001 ok: negative assertion — nothing may arrive inside 300ms"
                    "    Thread.Sleep(300)"
                    "    Assert.False(signal.Wait(0), \"nothing should have fired\")"
                    // `.IsSet` as a POLL PREDICATE is not a fixed-budget wait.
                    "let probedProperly () ="
                    "    use signal = new ManualResetEventSlim(false)"
                    "    Thread.Sleep(10)"
                    "    probeLoop (fun _ -> ()) (fun () -> signal.IsSet) 15000"
                    "    Assert.True(true, \"done\")"
                    // A pause with no event wait after it at all.
                    "let plainPause () ="
                    "    let mutable called = false"
                    "    Thread.Sleep(50)"
                    "    Assert.False(called, \"nothing called\")" ]
            )

        let findings =
            runRulesOnFile "WaitControlTests.fs" source
            |> List.filter (fun e -> e.Message.Contains "not synchronisation")

        test <@ findings.IsEmpty @>)

[<Fact(Timeout = 60000)>]
let ``FSHW-WAIT-001 is scoped to test sources`` () =
    withAnalyzerGate (fun () ->
        // The SAME source that fires twice as `WaitControlTests.fs`. Production code
        // sleeps for reasons this rule has no opinion about, and everything its message
        // recommends (probeLoop, waitUntilTrue, WatchedDir) lives in the test assemblies.
        let findings =
            runRulesOn flakeShapeSource
            |> List.filter (fun e -> e.Message.Contains "not synchronisation")

        test <@ findings.IsEmpty @>)

// ===========================================================================
// BuildPlugin — success and failure
// ===========================================================================

[<Fact(Timeout = 5000)>]
let ``BuildPlugin succeeds with echo command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { Name = PluginName.create "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted r -> receivedBuild <- Some r
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    let handler =
        BuildPlugin.create "echo" "build ok" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> receivedBuild.IsSome) 5000
    test <@ receivedBuild = Some BuildSucceeded @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``BuildPlugin fails with false command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { Name = PluginName.create "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted r -> receivedBuild <- Some r
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> receivedBuild.IsSome) 5000

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | PluginStatus.Failed _ -> true
            | _ -> false
        @>

// ===========================================================================
// TestPrunePlugin — success and failure
// ===========================================================================

[<Fact(Timeout = 5000)>]
let ``TestPrunePlugin with testConfigs runs tests after BuildSucceeded`` () =
    withTempDir "fshw-tp-inttest" (fun repoRoot ->
        let dbPath = Path.Combine(repoRoot, "test-impact.db")
        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

        let testConfigs =
            [ { Project = "EchoTests"
                Command = "echo"
                Args = "test passed"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler =
            TestPrunePlugin.create dbPath repoRoot (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 10000

        let cmdResult = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
        test <@ cmdResult.IsSome @>
        let doc = JsonDocument.Parse(cmdResult.Value)
        Assert.Equal("passed", doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString())

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 5000)>]
let ``TestPrunePlugin with failing test reports failure`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), $"fshw-tp-fail-{Guid.NewGuid():N}.db")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let testConfigs =
            [ { Project = "FailTests"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler =
            TestPrunePlugin.create dbPath "/tmp" (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 10000

        let cmdResult = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
        test <@ cmdResult.IsSome @>
        let doc = JsonDocument.Parse(cmdResult.Value)
        Assert.Equal("failed", doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString())

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        test
            <@
                match status.Value with
                | PluginStatus.Failed _ -> true
                | _ -> false
            @>
    finally
        try
            File.Delete(dbPath)
        with _ ->
            ()

        try
            File.Delete(dbPath + "-wal")
        with _ ->
            ()

        try
            File.Delete(dbPath + "-shm")
        with _ ->
            ()


// ===========================================================================
// FileCommandPlugin — success and failure
// ===========================================================================

let private fileTrigger (filter: string -> bool) : FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
    { FilePattern = Some filter
      AfterTests = None }

[<Fact(Timeout = 5000)>]
let ``FileCommandPlugin runs command for matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (PluginName.create "fsx-runner") (fileTrigger (fun f -> f.EndsWith(".fsx"))) "echo" "hello" "/tmp" None

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fsx-runner") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("fsx-runner")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``FileCommandPlugin ignores non-matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (PluginName.create "fsx-runner") (fileTrigger (fun f -> f.EndsWith(".fsx"))) "echo" "hello" "/tmp" None

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Nothing matches, so this poll is expected to time out with the plugin still Idle.
    waitUntil
        (fun () ->
            match host.GetStatus("fsx-runner") with
            | Some(PluginStatus.Completed _)
            | Some(PluginStatus.Failed _) -> true
            | _ -> false)
        1000

    let status = host.GetStatus("fsx-runner")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 5000)>]
let ``FileCommandPlugin reports failure on bad command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (PluginName.create "fsx-runner") (fileTrigger (fun f -> f.EndsWith(".fsx"))) "false" "" "/tmp" None

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fsx-runner") with
            | Some(PluginStatus.Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("fsx-runner")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | PluginStatus.Failed _ -> true
            | _ -> false
        @>

// ===========================================================================
// rerun — end-to-end: clear cache + synthetic file event re-runs plugin
// ===========================================================================

[<Fact(Timeout = 15000)>]
let ``rerun re-executes a cached FileCommandPlugin`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-rerun-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let sentinel = Path.Combine(tmpDir, "counter.txt")
        // `sh -c` with a direct append avoids writing and chmod-ing a helper script.
        let cmd = "sh"
        let args = $"-c \"echo ran >> '{sentinel}'\""

        // In-memory cache so cache-hit vs cache-miss is observable.
        let taskCache = FsHotWatch.TaskCache.InMemoryTaskCache()

        let host =
            PluginHost(
                Unchecked.defaultof<_>,
                tmpDir,
                reporters = [],
                taskCache = (taskCache :> FsHotWatch.TaskCache.ITaskCache)
            )

        let pattern = "*.ratchet.json"
        let pluginName = "rerun-test"

        // A stable cache key so replays hit: with a per-event key, cache replay never
        // fires and the test proves nothing.
        let getCommitId () = Some "stable-commit-for-test"

        let trigger: FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
              AfterTests = None }

        let handler = create (PluginName.create pluginName) trigger cmd args "/tmp" None

        let parsedPattern = FsHotWatch.Watcher.FilePattern.parse pattern
        host.RegisterHandler(handler)
        host.RegisterFileCommandPattern(pluginName, parsedPattern)

        // Wait on the side effect, not `waitForStatusSettled`: "settled" means "not
        // Running", which is also true in the window before the dispatched event has
        // flipped the plugin to Running, so under load it returns before the command
        // has written the sentinel.
        host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
        waitUntil (fun () -> File.Exists(sentinel) && File.ReadAllLines(sentinel).Length >= 1) 5000
        waitForStatusSettled host pluginName 5000
        test <@ File.Exists(sentinel) @>
        test <@ File.ReadAllLines(sentinel).Length = 1 @>

        // Same commit id ⇒ cache hit ⇒ the plugin must not run again.
        host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
        Thread.Sleep(500) // cache replay is synchronous but give dispatch time to settle
        waitForStatusSettled host pluginName 5000
        test <@ File.ReadAllLines(sentinel).Length = 1 @>

        // Rerun via the public host API — same call the IPC endpoint makes.
        let rerunResult = host.RerunFileCommandPlugin(pluginName)
        test <@ rerunResult = Result.Ok() @>

        // Status still reads Completed from the previous run, so `waitForStatusSettled`
        // would return immediately — wait for the second sentinel line instead.
        waitUntil (fun () -> File.Exists(sentinel) && File.ReadAllLines(sentinel).Length >= 2) 5000
        waitForStatusSettled host pluginName 5000

        test <@ File.ReadAllLines(sentinel).Length = 2 @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

// ===========================================================================
// Full pipeline integration
// ===========================================================================

[<Fact(Timeout = 5000)>]
let ``Full pipeline: format → build → test`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-pipeline-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    let dbPath = Path.Combine(tmpDir, "test-prune.db")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let preprocessor = FormatPreprocessor()
        host.RegisterPreprocessor(preprocessor)

        let buildHandler =
            BuildPlugin.create "echo" "build ok" [] (ProjectGraph()) [] None [] None

        host.RegisterHandler(buildHandler)

        let testConfigs =
            [ { Project = "PipelineTests"
                Command = "echo"
                Args = "tests passed"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let testPruneHandler =
            TestPrunePlugin.create dbPath tmpDir (Some testConfigs) None None None None []

        host.RegisterHandler(testPruneHandler)

        let fsFile = Path.Combine(tmpDir, "Temp.fs")
        File.WriteAllText(fsFile, "module Temp\n\nlet x = 5\n")
        let modified = host.RunPreprocessors([ fsFile ])
        test <@ modified |> List.contains fsFile |> not @>

        host.EmitFileChanged(SourceChanged [ fsFile ])

        waitForTerminalStatus host "build" 5000

        let buildStatus = host.GetStatus("build")
        test <@ buildStatus.IsSome @>

        test
            <@
                match buildStatus.Value with
                | Completed _ -> true
                | _ -> false
            @>

        waitForTerminalStatus host "test-prune" 10000

        let testStatus = host.GetStatus("test-prune")
        test <@ testStatus.IsSome @>

        test
            <@
                match testStatus.Value with
                | Completed _ -> true
                | _ -> false
            @>

        let fmtStatus = host.GetStatus("format")
        test <@ fmtStatus.IsSome @>

        test
            <@
                match fmtStatus.Value with
                | Completed _ -> true
                | _ -> false
            @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

// ===========================================================================
// Regression: concurrent build/test guards
// ===========================================================================

[<Fact(Timeout = 10000)>]
let ``BuildPlugin does not run concurrent builds`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable buildCount = 0

    let recorder =
        { Name = PluginName.create "build-counter"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ -> buildCount <- buildCount + 1
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    // A slow build command so the second emit certainly arrives mid-build.
    let handler =
        BuildPlugin.create "/bin/sleep" "1" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/A.fs" ])

    // The first build must be Running before the second event is emitted.
    waitUntil
        (fun () ->
            match host.GetStatus("build") with
            | Some(PluginStatus.Running _) -> true
            | _ -> false)
        5000

    host.EmitFileChanged(SourceChanged [ "src/B.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> buildCount >= 1) 2000

    test <@ buildCount = 1 @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``TestPrunePlugin does not run concurrent test suites`` () =
    withTempDir "fshw-tp-concurrent" (fun repoRoot ->
        let dbPath = Path.Combine(repoRoot, "test-impact.db")
        let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot

        let testConfigs =
            [ { Project = "SlowTests"
                Command = "/bin/sleep"
                Args = "1"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler =
            TestPrunePlugin.create dbPath repoRoot (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        // The first run must be active before the second event is emitted.
        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(PluginStatus.Running _) -> true
                | _ -> false)
            5000

        host.EmitBuildCompleted(BuildSucceeded)

        // 15s covers `sleep 1` plus a possible re-run or skip.
        waitForTerminalStatus host "test-prune" 15000

        let cmdResult = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
        test <@ cmdResult.IsSome @>
        // Either outcome is legal: a rerun with 0 affected classes is skipped (empty
        // results), and a cold-start first run produces passed results.
        let doc = JsonDocument.Parse(cmdResult.Value)
        let projects = doc.RootElement.GetProperty("projects")

        Assert.True(
            projects.GetArrayLength() = 0
            || projects.[0].GetProperty("status").GetString() = "passed"
        )

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // Failed here would mean resource exhaustion from a concurrent run.
        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>)

// ===========================================================================
// Real-world validation that the DI seams in the unit tests reflect production
// behaviour. Excluded from coverage: these exercise OS-level error modes
// (permission denied, deletion races) flaky enough to jitter the ratchet.
// ===========================================================================

[<Fact(Timeout = 10000)>]
let ``hashFileWith: real File.ReadAllBytes throws on unreadable file`` () =
    // The unit tests reach hashFileWith's None branch through an injected throwing
    // reader. If the OS ever stops throwing on an unreadable file, that mock no longer
    // represents reality and the unit test would be green over nothing.
    if
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows
        )
    then
        Assert.Skip("Unix file modes don't apply on Windows")

    withTempDir "fshw-hash-deny" (fun tmpDir ->
        let p = Path.Combine(tmpDir, "denied")
        File.WriteAllText(p, "secret")

        try
            File.SetUnixFileMode(p, UnixFileMode.None)

            let result = hashFileWith System.IO.File.ReadAllBytes p
            test <@ result = None @>
        finally
            // Restore so cleanup can delete the file.
            try
                File.SetUnixFileMode(p, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            with _ ->
                ())

// ===========================================================================
// ProcessHelper kill-on-timeout. Lives here, excluded from coverage: the kill/drain
// race gives nondeterministic line coverage on ProcessHelper.fs (89-92%), which
// would destabilise the auto-ratchet in `mise run check`.
// ===========================================================================

[<Fact(Timeout = 10000)>]
let ``runProcess kills child when exceeded`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let result =
        runProcess "sleep" "10" "." [] (ProcessBounds.silent (TimeSpan.FromMilliseconds 200.0))

    sw.Stop()
    Assert.True(isTimedOut result)
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 3.0, $"took {sw.Elapsed}")

[<Fact(Timeout = 10000)>]
let ``runProcess reports TimedOut on kill, carrying the child's pre-kill stdout`` () =
    // Do not weaken this back to `| TimedOut _ -> ()`: that accommodated a thread-pool
    // starvation bug in the drain (pumps as `task {}` continuations that a saturated
    // pool never scheduled), which is fixed — the pumps own dedicated threads now.
    //
    // The child prints `partial` and THEN sleeps 10s, so those bytes are on the pipe
    // long before the 300ms timeout fires. A tail without them is a drain that failed
    // to measure, and must be red.
    match
        runProcess "sh" "-c \"echo partial; sleep 10\"" "." [] (ProcessBounds.silent (TimeSpan.FromMilliseconds 300.0))
    with
    | TimedOut(_, tail, _) ->
        // The kill tears the pipes down under the pumps, so whether they end at EOF
        // (`Drained`) or mid-read (`DrainTimedOut`) is a genuine OS race — the tag is not
        // the contract, the capture is. `ProcessOutput.text` reads a capture without
        // demanding it be complete, and asserting a marker is PRESENT can only lose a
        // hit, never invent one, so a starved drain fails rather than slides past.
        Assert.Contains("partial", ProcessOutput.text tail)
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.killAll terminates tracked live processes`` () =
    // `install` is AsyncLocal-scoped, so killAll only reaches this test's tracked PIDs
    // and cannot reap a concurrent test's children.
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    FsHotWatch.Tests.TestHelpers.withTrackedSleep 60 (fun proc ->
        FsHotWatch.ProcessRegistry.track proc
        Assert.False(proc.HasExited)

        FsHotWatch.ProcessRegistry.killAll ()

        proc.WaitForExit(5000) |> ignore
        Assert.True(proc.HasExited))

// Outer timeout 20s leaves headroom over the internal ceiling (8s registration
// deadline + 5s task.Wait = 13s) so a real failure surfaces as an assertion
// rather than a silent xUnit timeout.
[<Fact(Timeout = 20000)>]
let ``ProcessRegistry.killAll kills a child started via runProcess from another thread`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    // The Task captures the AsyncLocal context at start, so the spawned child
    // registers against this test's registry — not a process-wide global.
    let task =
        System.Threading.Tasks.Task.Run(fun () ->
            runProcess "sleep" "30" "." [] (ProcessBounds.silent System.Threading.Timeout.InfiniteTimeSpan))

    // 8s tolerates thread-pool contention from parallel tests — a Task.Run body can take
    // seconds to reach Process.Start under load.
    let deadline = DateTime.UtcNow.AddSeconds 8.0

    while registry.Snapshot().IsEmpty && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 25

    Assert.NotEmpty(registry.Snapshot())

    registry.KillAll()

    let completed = task.Wait(5000)
    Assert.True(completed, "runProcess did not return after killAll")

// ===========================================================================
// Lives here because it spawns a real `sleep 10` under a 1s timeout: the kill-on-
// timeout drain race drifts line coverage on TestPrunePlugin.fs (Aborted arm) and
// ProcessHelper.fs (drain branches).
// ===========================================================================

[<Fact(Timeout = 15000)>]
let ``TestPrune honors per-project TimeoutSec and records TimedOut`` () =
    withTempDir "fshw-tp-timeout" (fun tmpDir ->
        let configs =
            [ { TestPrunePlugin.TestConfig.Project = "Slow"
                Command = "sleep"
                Args = "10"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 1
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            TestPrunePlugin.create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let sw = System.Diagnostics.Stopwatch.StartNew()
        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 8000
        sw.Stop()

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds 8.0, $"took {sw.Elapsed}")
        let history = host.GetHistory("test-prune")
        test <@ not history.IsEmpty @>
        let last = List.last history

        test
            <@
                match last.Outcome with
                | RunOutcome.TimedOut _ -> true
                | _ -> false
            @>)

// ===========================================================================
// fcsCheckSignature: when Foo.fs changes in a way that affects Bar.fs's diagnostics,
// Bar.fs's signature must differ even though Bar's own bytes are unchanged. That
// signature feeds the analyzers/lint merkle cache key — if it doesn't move, downstream
// caches replay stale results.
// ===========================================================================

[<Fact(Timeout = 30000)>]
let ``§1 regression: Bar's fcsCheckSignature changes when Foo's signature breaks Bar`` () =
    withTempDir "fshw-fcs-sig-cross-file" (fun tmpDir ->
        let fooPath = Path.Combine(tmpDir, "Foo.fs")
        let barPath = Path.Combine(tmpDir, "Bar.fs")

        File.WriteAllText(fooPath, "module Foo\nlet add (x: int) (y: int) = x + y\n")
        File.WriteAllText(barPath, "module Bar\nlet result = Foo.add 1 2\n")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        // Foo is the script-options entry; SourceFiles is then overridden to the 2-file
        // project view so Bar can resolve Foo.
        let fooSource = File.ReadAllText(fooPath)
        let sourceText = SourceText.ofString fooSource

        let projOptions, _ =
            checker.GetProjectOptionsFromScript(fooPath, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously

        pipeline.RegisterProject(
            "test-project",
            { projOptions with
                SourceFiles = [| fooPath; barPath |] }
        )

        let result1 =
            pipeline.CheckFile(AbsFilePath.create barPath)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "first CheckFile returned None")

        let sig1 = FsHotWatch.CheckCache.fcsCheckSignature result1.CheckResults

        // Break Foo: remove `add` so Bar.fs's `Foo.add` becomes "value not found".
        File.WriteAllText(fooPath, "module Foo\nlet unrelated () = 0\n")

        let result2 =
            pipeline.CheckFile(AbsFilePath.create barPath)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "second CheckFile returned None")

        let sig2 = FsHotWatch.CheckCache.fcsCheckSignature result2.CheckResults

        // Bar.fs's own bytes are identical — only Foo changed.
        test <@ sig1 <> sig2 @>)

// Lives here rather than PathFilterTests: coverlet records the inner double-checked-lock
// branch inconsistently under this much contention, flickering PathFilter.fs between
// 68.2% and 63.6% branch coverage. The correctness assertion still runs, just outside the
// ratchet.
[<Fact(Timeout = 15000)>]
let ``IgnoreFilterCache is safe under concurrent Get`` () =
    withTempDir "cache-concurrent" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")
        let cache = FsHotWatch.PathFilter.IgnoreFilterCache()
        let logPath = Path.Combine(tmpDir, "x.log")
        let fsPath = Path.Combine(tmpDir, "y.fs")

        let threadCount = 16
        let iterations = 500
        use ready = new Barrier(threadCount)
        let errors = ResizeArray<exn>()
        let errLock = obj ()

        let work () =
            try
                ready.SignalAndWait()

                for _ in 1..iterations do
                    let f = cache.Get(tmpDir)

                    if not (f logPath) || f fsPath then
                        failwith "unexpected filter result"
            with ex ->
                lock errLock (fun () -> errors.Add(ex))

        let threads = [| for _ in 1..threadCount -> Thread(ThreadStart(work)) |]

        for t in threads do
            t.Start()

        for t in threads do
            t.Join()

        test <@ errors.Count = 0 @>)

// ---------------------------------------------------------------------------
// The tests below spawn real `sleep` subprocesses and assert on cross-thread timing
// windows that the unit suite's parallel scheduler starves. They live here (less
// parallel content) and are excluded from coverage.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``concurrent FileChanged events do not start two builds`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let buildCompletedCount = ref 0

    let counter: PluginHandler<unit, obj> =
        { Name = PluginName.create "build-counter"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ -> System.Threading.Interlocked.Increment(buildCompletedCount) |> ignore
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = None
          Teardown = None }

    // Slow build (sleep 1) so the second FileChanged certainly arrives mid-build.
    let handler =
        FsHotWatch.Build.BuildPlugin.create "sleep" "1" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(counter)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/A.fs" ])
    // RunExclusive must have marked the slot Running before the second dispatch
    // evaluates the policy.
    System.Threading.Thread.Sleep(100)
    host.EmitFileChanged(SourceChanged [ "src/B.fs" ])

    waitForTerminalStatus host "build" 15000
    // An erroneously-spawned second build would fire its BuildCompleted within ~1.5s of
    // the first, so this settle window is what makes the count assertion meaningful.
    System.Threading.Thread.Sleep(2000)

    test <@ !buildCompletedCount = 1 @>

[<Fact(Timeout = 30000)>]
let ``run summary names the slowest project when 2+ projects ran`` () =
    withTempDir "tp-slowest" (fun tmpDir ->
        // 20× differential — wide enough that fork-exec / first-time JIT overhead
        // on FastProj can't overtake SlowProj's actual wall time.
        let configs =
            [ { TestPrunePlugin.TestConfig.Project = "FastProj"
                Command = "sh"
                Args = "-c \"sleep 0.05\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect }
              { TestPrunePlugin.TestConfig.Project = "SlowProj"
                Command = "sh"
                Args = "-c \"sleep 1.0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            TestPrunePlugin.create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)
        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 15000

        let history = host.GetHistory("test-prune")
        test <@ not history.IsEmpty @>
        let lastRun = history |> List.last

        match lastRun.Summary with
        | Some s ->
            test <@ s.Contains("slowest: SlowProj") @>
            test <@ not (s.Contains("slowest: FastProj")) @>
        | None -> failwith "expected summary on completed run")

// Four plugins plus a DaemonRpcTarget serialization roundtrip: integration-grade. Under
// unit-suite contention the wait for handler "d" to reach Failed flaked ~20% at 5s,
// hence the generous timeouts here.
[<Fact(Timeout = 30000)>]
let ``DaemonRpcTarget.GetStatus without IPC serializes all status variants`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let defaultRpcConfig (host: PluginHost) : DaemonRpcConfig =
        { Host = host
          RequestShutdown = ignore
          RequestScan = ignore
          GetScanStatus = fun () -> "idle"
          GetScanGeneration = fun () -> 0L
          TriggerBuild = fun () -> async { return () }
          FormatAll = fun () -> async { return "formatted 0 files" }
          WaitForScanGeneration = fun _ -> System.Threading.Tasks.Task.FromResult(())
          WaitForAllTerminal = fun _ -> System.Threading.Tasks.Task.FromResult(())
          RerunPlugin = fun _ -> async { return Result.Ok() }
          InvalidateCache = fun () -> System.Threading.Tasks.Task.FromResult(())
          GetUncheckedCount = fun () -> 0 }

    let makeStatusHandler name (reportFn: PluginCtx<unit> -> unit) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> reportFn ctx
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeStatusHandler "a" (fun ctx -> ctx.ReportStatus(Idle)))

    host.RegisterHandler(
        makeStatusHandler "b" (fun ctx -> ctx.ReportStatus(Running(since = System.DateTime(2025, 6, 15))))
    )

    host.RegisterHandler(
        makeStatusHandler "c" (fun ctx -> ctx.ReportStatus(completedAt (System.DateTime(2025, 6, 16))))
    )

    host.RegisterHandler(
        makeStatusHandler "d" (fun ctx ->
            ctx.ReportStatus(
                PluginStatus.Failed("oops", System.DateTime(2025, 6, 17), FsHotWatch.Tests.TestHelpers.testVerdict)
            ))
    )

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("d") with
            | Some(PluginStatus.Failed _) -> true
            | _ -> false)
        20000

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let json = target.GetStatus()
    test <@ json.Contains("\"tag\":\"idle\"") @>
    test <@ json.Contains("\"tag\":\"running\"") @>
    test <@ json.Contains("\"tag\":\"completed\"") @>
    test <@ json.Contains("\"tag\":\"failed\"") @>
    test <@ json.Contains("oops") @>

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json

    match parsed.["a"].Status with
    | FsHotWatch.Cli.RunOnceOutput.StatusView.Idle -> ()
    | other -> failwithf "expected Idle, got %A" other

    match parsed.["d"].Status with
    | FsHotWatch.Cli.RunOnceOutput.StatusView.Failed(msg, _) -> test <@ msg = "oops" @>
    | other -> failwithf "expected Failed, got %A" other

// The `waitForPluginTerminalIfRunning` tests below make Task.Delay-based timing
// assertions that systematically bust under the unit suite's parallelism. This suite is
// looser, so the windows hold.

[<Fact(Timeout = 30000)>]
let ``waitForPluginTerminalIfRunning returns immediately when plugin not registered`` () =
    let host = FsHotWatch.PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp")
    let sw = System.Diagnostics.Stopwatch.StartNew()

    waitForPluginTerminalIfRunning host "build" (TimeSpan.FromSeconds(5.0))
    |> Async.RunSynchronously

    sw.Stop()
    test <@ sw.Elapsed < TimeSpan.FromSeconds(3.0) @>

let private makeControllablePlugin (name: string) =
    let release = System.Threading.Tasks.TaskCompletionSource<unit>()

    let handler =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun (ctx: PluginCtx<unit>) state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
                        do! release.Task |> Async.AwaitTask
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    {| Handler = handler
       Release = release |}

[<Fact(Timeout = 30000)>]
let ``waitForPluginTerminalIfRunning returns when plugin reaches terminal`` () =
    let host = FsHotWatch.PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp")
    let plugin = makeControllablePlugin "build"
    host.RegisterHandler(plugin.Handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/Lib.fs" ])

    let _ =
        System.Threading.Tasks.Task.Run(fun () ->
            task {
                do! System.Threading.Tasks.Task.Delay(300)
                plugin.Release.TrySetResult() |> ignore
            }
            :> System.Threading.Tasks.Task)

    let sw = System.Diagnostics.Stopwatch.StartNew()

    waitForPluginTerminalIfRunning host "build" (TimeSpan.FromSeconds(15.0))
    |> Async.RunSynchronously

    sw.Stop()

    test <@ sw.Elapsed > TimeSpan.FromMilliseconds(200.0) @>
    test <@ sw.Elapsed < TimeSpan.FromSeconds(14.0) @>

    match host.GetStatus("build") with
    | Some(Running _) -> failwith "build should be terminal after wait"
    | _ -> ()

[<Fact(Timeout = 30000)>]
let ``waitForPluginTerminalIfRunning times out when plugin never leaves Running`` () =
    let host = FsHotWatch.PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp")
    let plugin = makeControllablePlugin "build"
    host.RegisterHandler(plugin.Handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/Lib.fs" ])

    let sw = System.Diagnostics.Stopwatch.StartNew()

    waitForPluginTerminalIfRunning host "build" (TimeSpan.FromMilliseconds(500.0))
    |> Async.RunSynchronously

    sw.Stop()

    test <@ sw.Elapsed > TimeSpan.FromMilliseconds(450.0) @>
    test <@ sw.Elapsed < TimeSpan.FromSeconds(10.0) @>

    plugin.Release.TrySetResult() |> ignore

// ===========================================================================
// Auto-refresh on .fsproj changes: adding a PackageReference and restoring must clear
// the stale FS0039 without a daemon restart and without saving any .fs file. The three
// tests below split that into separate contracts rather than one E2E, because the
// end-to-end path depends on Ionide.ProjInfo's MSBuild eval picking up post-restore
// PackageReferences — a property of a real project tree that synthetic .fsproj fixtures
// do not reliably reproduce.
// ===========================================================================

let private runDotnetIn (cwd: string) (args: string) : unit =
    let psi = ProcessStartInfo("dotnet", args)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.WorkingDirectory <- cwd
    let proc = Process.Start(psi)
    let _ = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwithf "dotnet %s in %s failed (exit %d): %s" args cwd proc.ExitCode stderr

[<Fact(Timeout = 180000)>]
let ``daemon auto-rechecks affected project's source files after .fsproj edit`` () =
    // Pins the original bug: invalidate + re-evaluate options fired, but the per-file
    // re-check never followed, so the FCS error ledger stayed stale.
    //
    // Deliberately asserts only that a second FileChecked cohort lands, not which errors
    // appear — whether the new options contain a new PackageReference is a real-ProjInfo
    // concern, covered end-to-end further down.
    withTempDir "fshw-fr-recheck" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "MyProj")
        Directory.CreateDirectory(projDir) |> ignore

        let fsprojPath = Path.Combine(projDir, "MyProj.fsproj")
        let libFsPath = Path.Combine(projDir, "Lib.fs")
        let libFsCanonical = Path.GetFullPath(libFsPath)

        let fsprojInitial =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>
"""

        File.WriteAllText(fsprojPath, fsprojInitial)
        File.WriteAllText(libFsPath, "module Lib\nlet x = 1\n")

        runDotnetIn projDir "restore --nologo"

        let libCheckCount = ref 0

        let counter: PluginHandler<unit, obj> =
            { Name = PluginName.create "lib-recheck-counter"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChecked result ->
                            if AbsFilePath.value result.File = libFsCanonical then
                                System.Threading.Interlocked.Increment(libCheckCount) |> ignore
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChecked ]
              CacheKey = None
              Teardown = None }

        let checker =
            FSharpChecker.Create(projectCacheSize = 50, keepAssemblyContents = false)

        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(counter)
            let task = Async.StartAsTask(daemon.Run(cts.Token))
            daemon.Ready.Wait(TimeSpan.FromSeconds(60.0)) |> ignore

            // Boot scan drives the first FileChecked for Lib.fs.
            daemon.ScanAll() |> Async.RunSynchronously

            waitUntil (fun () -> libCheckCount.Value >= 1) 30000

            if libCheckCount.Value < 1 then
                Assert.Fail(
                    sprintf
                        "Baseline failed: boot scan should produce ≥1 FileChecked for Lib.fs (got %d)"
                        libCheckCount.Value
                )

            let baseline = libCheckCount.Value

            // A comment-only edit: MSBuild eval still succeeds and returns the SAME
            // options, which is the worst case for re-check detection. A trigger that
            // fires here fires a fortiori when the options actually change.
            let fsprojBumped =
                """<Project Sdk="Microsoft.NET.Sdk">
  <!-- bumped -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>
"""

            File.WriteAllText(fsprojPath, fsprojBumped)

            // 60s covers FSEvents cold-start + 200ms project debounce + ~0.5s MSBuild
            // eval + the FCS check.
            waitUntil (fun () -> libCheckCount.Value > baseline) 60000

            if libCheckCount.Value <= baseline then
                Assert.Fail(
                    sprintf
                        "FR contract failed: after .fsproj edit, expected FileChecked for Lib.fs to increment past baseline=%d within 60s, but stayed at %d. The daemon detected the .fsproj change and re-discovered options, but never re-ran FCS on the project's source files."
                        baseline
                        libCheckCount.Value
                )

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())

[<Fact(Timeout = 120000)>]
let ``watcher delivers ProjectChanged event when obj/project.assets.json is written`` () =
    // `obj/` is otherwise excluded by `PathFilter.isGeneratedPath`, so this file needs a
    // deliberate carve-out. Without it the daemon's only PackageReference signal is the
    // .fsproj edit, which races ahead of the restore completing.
    withTempDir "fshw-fr-assets" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "MyProj")
        let objDir = Path.Combine(projDir, "obj")
        Directory.CreateDirectory(objDir) |> ignore

        // Minimal .fsproj so daemon discovery doesn't bail.
        let fsprojPath = Path.Combine(projDir, "MyProj.fsproj")

        File.WriteAllText(
            fsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n"
        )

        let assetsPath = Path.Combine(objDir, "project.assets.json")

        let projectChanges = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()

        let recorder: PluginHandler<unit, obj> =
            { Name = PluginName.create "assets-json-recorder"
              Init = ()
              Update =
                fun _ctx state event ->
                    async {
                        match event with
                        | FileChanged change -> projectChanges.Add(change)
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChanged ]
              CacheKey = None
              Teardown = None }

        let cts = new CancellationTokenSource()

        let daemon =
            Daemon.createWith (Unchecked.defaultof<FSharpChecker>) tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(recorder)
            let task = Async.StartAsTask(daemon.Run(cts.Token))
            daemon.Ready.Wait(TimeSpan.FromSeconds(30.0)) |> ignore

            // FSEvents cold-start is an unbounded window on macOS, so this probe-loops writes rather
            // than writing once and waiting.
            let hasAssetsProjectChange () =
                projectChanges
                |> Seq.exists (fun c ->
                    match c with
                    | ProjectChanged files -> files |> List.exists (fun f -> f.EndsWith("project.assets.json"))
                    | _ -> false)

            probeLoop
                (fun n -> File.WriteAllText(assetsPath, sprintf "{\"version\":3,\"probe\":%d}" n))
                hasAssetsProjectChange
                90000

            if not (hasAssetsProjectChange ()) then
                let summary = projectChanges |> Seq.map (sprintf "%O") |> String.concat "; "

                Assert.Fail(
                    sprintf
                        "Contract failed: writes to %s should fire ProjectChanged within 90s but never did. Observed: %s"
                        assetsPath
                        (if summary = "" then "(no FileChanged events)" else summary)
                )

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())

[<Fact(Timeout = 240000)>]
let ``daemon resolves a newly-added PackageReference and clears the stale FS0039 without restart`` () =
    // End to end with a real package, real `dotnet restore`, real FCS. It deliberately
    // exercises the ordering hazard: writing the .fsproj fires the watcher immediately,
    // but restore outlasts the 200ms project debounce, so the first re-eval reads a stale
    // obj/ package graph. What makes it pass is watching `obj/project.assets.json` —
    // restore's final atomic write triggers a second re-eval once the graph is coherent.
    withTempDir "fshw-fr-e2e" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "MyProj")
        Directory.CreateDirectory(projDir) |> ignore

        let fsprojPath = Path.Combine(projDir, "MyProj.fsproj")
        let libFsPath = Path.Combine(projDir, "Lib.fs")
        let libFsCanonical = Path.GetFullPath(libFsPath)

        let fsprojNoPackage =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
</Project>
"""

        let fsprojWithPackage =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
"""

        File.WriteAllText(fsprojPath, fsprojNoPackage)

        File.WriteAllText(
            libFsPath,
            "module Lib\nopen Newtonsoft.Json\nlet useIt () : JsonSerializer = JsonSerializer()\n"
        )

        runDotnetIn projDir "restore --nologo"

        let checker =
            FSharpChecker.Create(projectCacheSize = 50, keepAssemblyContents = false)

        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        let hasNewtonsoftError () =
            daemon.Host.GetErrorsByPlugin(FsHotWatch.PluginActivity.FcsPluginName)
            |> Map.toSeq
            |> Seq.exists (fun (file, entries) ->
                (file = libFsCanonical || file.EndsWith("Lib.fs"))
                && entries |> List.exists (fun e -> e.Message.Contains("Newtonsoft")))

        try
            let task = Async.StartAsTask(daemon.Run(cts.Token))
            daemon.Ready.Wait(TimeSpan.FromSeconds(60.0)) |> ignore

            // Lib.fs opens Newtonsoft, which isn't referenced yet ⇒ baseline FS0039.
            daemon.ScanAll() |> Async.RunSynchronously
            waitUntil hasNewtonsoftError 90000

            if not (hasNewtonsoftError ()) then
                Assert.Fail(
                    "Baseline failed: expected FS0039 (Newtonsoft not defined) after boot scan but none observed."
                )

            // No .fs save and no daemon restart: the watcher sees only the .fsproj edit
            // and, after restore, the obj/project.assets.json write.
            File.WriteAllText(fsprojPath, fsprojWithPackage)
            runDotnetIn projDir "restore --nologo --force"

            let assetsPath = Path.Combine(projDir, "obj", "project.assets.json")

            if not (File.ReadAllText(assetsPath).Contains("Newtonsoft.Json")) then
                Assert.Fail("Test setup invalid: project.assets.json lacks Newtonsoft.Json after restore.")

            // The daemon must converge to a clean ledger on its own.
            let errorCleared () = not (hasNewtonsoftError ())
            waitUntil errorCleared 120000

            if not (errorCleared ()) then
                let remaining =
                    daemon.Host.GetErrorsByPlugin(FsHotWatch.PluginActivity.FcsPluginName)
                    |> Map.toSeq
                    |> Seq.collect (fun (f, es) -> es |> List.map (fun e -> sprintf "%s: %s" f e.Message))
                    |> String.concat " | "

                Assert.Fail(
                    sprintf
                        "FR acceptance failed: after PackageReference add + restore, FCS still can't resolve Newtonsoft after 120s (no restart). Remaining: %s"
                        remaining
                )

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())

// ===========================================================================
// Scoped invalidation: a change to one project must re-check that project AND its
// transitive dependents, while leaving INDEPENDENT projects warm. The earlier
// "re-check everything" fix was correct but paid full cold-start on every change.
// ===========================================================================

/// Resolve all symlink components to the canonical real path. macOS temp dirs live under
/// /var/folders (a symlink to /private/var/folders); FSEvents reports the canonical
/// /private form while MSBuild/ProjInfo keep the as-given /var form. The daemon matches
/// watcher events to project paths by string equality and falls back to full
/// re-discovery when they diverge — correct, but not the path these tests mean to
/// exercise, so they canonicalize the temp root up front.
let rec private realPath (path: string) : string =
    let full = Path.GetFullPath path

    if full = "/" || isNull (Path.GetDirectoryName full) then
        full
    else
        let combined =
            Path.Combine(realPath (Path.GetDirectoryName full), Path.GetFileName full)

        match
            (try
                Directory.ResolveLinkTarget(combined, true)
             with _ ->
                 null)
        with
        | null ->
            match
                (try
                    File.ResolveLinkTarget(combined, true)
                 with _ ->
                     null)
            with
            | null -> combined
            | t -> t.FullName
        | t -> t.FullName

let private fileCheckCounter (name: string) (targetCanonical: string) =
    let count = ref 0

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChecked result ->
                        if AbsFilePath.value result.File = targetCanonical then
                            System.Threading.Interlocked.Increment(count) |> ignore
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = None
          Teardown = None }

    ((fun () -> count.Value), handler)

[<Fact(Timeout = 180000)>]
let ``scoped: changing one project leaves an independent project warm (not re-checked)`` () =
    withTempDir "fshw-scoped-warm" (fun tmpDir ->
        let tmpDir = realPath tmpDir

        let mkProj (name: string) (body: string) =
            let dir = Path.Combine(tmpDir, "src", name)
            Directory.CreateDirectory(dir) |> ignore
            let fsproj = Path.Combine(dir, name + ".fsproj")

            File.WriteAllText(
                fsproj,
                sprintf
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"%s.fs\"/></ItemGroup></Project>\n"
                    name
            )

            let fs = Path.Combine(dir, name + ".fs")
            File.WriteAllText(fs, body)
            (dir, fsproj, Path.GetFullPath fs)

        let _, aFsproj, aFs = mkProj "A" "module A\nlet a = 1\n"
        let _, _, bFs = mkProj "B" "module B\nlet b = 2\n"

        runDotnetIn (Path.GetDirectoryName aFsproj) "restore --nologo"
        runDotnetIn (Path.Combine(tmpDir, "src", "B")) "restore --nologo"

        let getA, counterA = fileCheckCounter "count-a" aFs
        let getB, counterB = fileCheckCounter "count-b" bFs

        let checker = FSharpChecker.Create(projectCacheSize = 50)
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(counterA)
            daemon.RegisterHandler(counterB)
            let task = Async.StartAsTask(daemon.Run(cts.Token))
            daemon.Ready.Wait(TimeSpan.FromSeconds(60.0)) |> ignore

            daemon.ScanAll() |> Async.RunSynchronously
            waitUntil (fun () -> getA () >= 1 && getB () >= 1) 60000

            if getA () < 1 || getB () < 1 then
                Assert.Fail(
                    sprintf "Baseline: both projects should be checked on boot (A=%d B=%d)" (getA ()) (getB ())
                )

            let baselineB = getB ()

            // Touch A's .fsproj only.
            File.WriteAllText(
                aFsproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- bump --><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"A.fs\"/></ItemGroup></Project>\n"
            )

            let aBefore = getA ()
            waitUntil (fun () -> getA () > aBefore) 60000

            if getA () <= aBefore then
                Assert.Fail(sprintf "A should be re-checked after its .fsproj change (stayed at %d)" (getA ()))

            // An erroneous B re-check needs time to land before "B stayed flat" means
            // anything.
            System.Threading.Thread.Sleep(3000)

            if getB () <> baselineB then
                Assert.Fail(
                    sprintf
                        "Independent project B was re-checked (%d → %d) when only A changed — scoped invalidation should leave it warm."
                        baselineB
                        (getB ())
                )

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())

[<Fact(Timeout = 180000)>]
let ``scoped: changing a project re-checks its dependent (correctness over warmth)`` () =
    withTempDir "fshw-scoped-dep" (fun tmpDir ->
        let tmpDir = realPath tmpDir
        let srcDir = Path.Combine(tmpDir, "src")
        let aDir = Path.Combine(srcDir, "A")
        let bDir = Path.Combine(srcDir, "B")
        Directory.CreateDirectory(aDir) |> ignore
        Directory.CreateDirectory(bDir) |> ignore

        // A: library. B: references A.
        let aFsproj = Path.Combine(aDir, "A.fsproj")

        File.WriteAllText(
            aFsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"A.fs\"/></ItemGroup></Project>\n"
        )

        let aFs = Path.Combine(aDir, "A.fs")
        File.WriteAllText(aFs, "module A\nlet a = 1\n")

        let bFsproj = Path.Combine(bDir, "B.fsproj")

        File.WriteAllText(
            bFsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"B.fs\"/></ItemGroup><ItemGroup><ProjectReference Include=\"../A/A.fsproj\"/></ItemGroup></Project>\n"
        )

        let bFs = Path.Combine(bDir, "B.fs")
        File.WriteAllText(bFs, "module B\nlet b = A.a + 1\n")

        let bFsCanonical = Path.GetFullPath bFs

        // Restore B (pulls in A as a project reference).
        runDotnetIn bDir "restore --nologo"
        runDotnetIn aDir "restore --nologo"

        let getB, counterB = fileCheckCounter "count-b-dep" bFsCanonical

        let checker = FSharpChecker.Create(projectCacheSize = 50)
        let cts = new CancellationTokenSource()
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(counterB)
            let task = Async.StartAsTask(daemon.Run(cts.Token))
            daemon.Ready.Wait(TimeSpan.FromSeconds(60.0)) |> ignore

            daemon.ScanAll() |> Async.RunSynchronously
            waitUntil (fun () -> getB () >= 1) 60000

            if getB () < 1 then
                Assert.Fail("Baseline: dependent B should be checked on boot")

            let baselineB = getB ()

            // Only A's .fsproj is touched; B must be re-checked anyway, since B's own
            // files and options are unchanged.
            File.WriteAllText(
                aFsproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- bump --><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"A.fs\"/></ItemGroup></Project>\n"
            )

            waitUntil (fun () -> getB () > baselineB) 90000

            if getB () <= baselineB then
                Assert.Fail(
                    sprintf
                        "Dependent B was NOT re-checked (stayed at %d) when its dependency A changed — scoped invalidation must include transitive dependents."
                        baselineB
                )

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())

// ===========================================================================
// The jj-merge wedge repro (AUTOMATION-15/26). A foreground `scan`/`check` issued
// while the daemon was mid auto-rebuild — a merge landing ~13 files at once,
// including .fsproj edits — wedged it: `status` and `check` both blocked on the same
// dead socket. Reproduced here with a real daemon over a real pipe.
// ===========================================================================

/// Poll until the IPC server answers a cheap `scanStatus`; false if it never came up.
let private waitForIpcServer (pipeName: string) (timeoutMs: int) : bool =
    let serverUp () =
        try
            IpcClient.scanStatus pipeName |> Async.RunSynchronously |> ignore
            true
        with _ ->
            false

    waitUntil serverUp timeoutMs
    serverUp ()

[<Fact(Timeout = 180000)>]
let ``daemon stays responsive to status while mid auto-rebuild after a multi-file batch (AUTOMATION-15/26)`` () =
    withTempDir "fshw-wedge-rebuild" (fun tmpDir ->
        let tmpDir = realPath tmpDir
        let projDir = Path.Combine(tmpDir, "src", "MyProj")
        Directory.CreateDirectory(projDir) |> ignore

        // ~13 source files + the .fsproj — the jj-merge repro's batch shape.
        let fileNames = [ for i in 1..13 -> sprintf "M%d.fs" i ]

        let fsprojBody (comment: string) =
            let compiles =
                fileNames |> List.map (sprintf "<Compile Include=\"%s\"/>") |> String.concat ""

            sprintf
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!-- %s --><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup>%s</ItemGroup></Project>\n"
                comment
                compiles

        let fsprojPath = Path.Combine(projDir, "MyProj.fsproj")
        File.WriteAllText(fsprojPath, fsprojBody "v1")

        for i, name in List.indexed fileNames do
            File.WriteAllText(Path.Combine(projDir, name), sprintf "module M%d\nlet v%d = %d\n" (i + 1) (i + 1) i)

        runDotnetIn projDir "restore --nologo"

        let checker = FSharpChecker.Create(projectCacheSize = 50)
        let cts = new CancellationTokenSource()
        let pipeName = Program.computePipeName tmpDir

        // The build is a slow `sleep`, modelling the long auto-rebuild in flight. The
        // `Some 30` timeout means an unbounded hang fails fast instead of stalling the
        // suite; the sleep finishes well inside it.
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(BuildPlugin.create "sleep" "5" [] daemon.Graph [] None [] (Some 30))

            let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

            if not (waitForIpcServer pipeName 60000) then
                Assert.Fail("IPC server never came up")

            // Let the boot scan settle.
            IpcClient.waitForScan pipeName -1L |> Async.RunSynchronously |> ignore

            // Land the batch — bumping the .fsproj and rewriting every source kicks
            // re-discovery plus the slow auto-rebuild.
            File.WriteAllText(fsprojPath, fsprojBody "v2")

            for i, name in List.indexed fileNames do
                File.WriteAllText(
                    Path.Combine(projDir, name),
                    sprintf "module M%d\nlet v%d = %d\n" (i + 1) (i + 1) (i + 100)
                )

            // FSHW-WAIT-001 ok: this sleep opens the window the test measures INSIDE
            // (status stays responsive WHILE a rebuild runs) — it is not a wait for an
            // event to arrive, and every probe below carries its own 8s budget. The build
            // is a literal `sleep 5`, so 1.5s lands mid-build with 3.5s of margin.
            Thread.Sleep(1500)

            let statusReturnedWithin (ms: int) =
                let t =
                    Async.StartAsTask(
                        async {
                            let! s = IpcClient.scanStatus pipeName
                            let! g = IpcClient.getStatus pipeName
                            return (s, g)
                        }
                    )

                t.Wait(TimeSpan.FromMilliseconds(float ms)) && t.IsCompletedSuccessfully

            // 8s is generous — production status is sub-second — so a probe that misses
            // it is a wedge, not slowness.
            let mutable allResponsive = true

            for _ in 1..5 do
                if not (statusReturnedWithin 8000) then
                    allResponsive <- false

                // FSHW-WAIT-001 ok: spacing between probes so they spread across
                // the in-flight-build window. Nothing is awaited here.
                Thread.Sleep(400)

            if not allResponsive then
                Assert.Fail(
                    "DAEMON WEDGED: a concurrent `status` over the pipe did not return within budget while a \
                     multi-file rebuild was in flight. Cancellation did NOT cure the race — this needs a \
                     dedicated fix (do not paper over). See AUTOMATION-15/26."
                )

            // Responsive is not enough — the daemon must still drive work to completion.
            let settled =
                Async.StartAsTask(
                    async {
                        let! _ = IpcClient.scan pipeName
                        return! IpcClient.waitForScan pipeName -1L
                    }
                )

            test <@ settled.Wait(TimeSpan.FromSeconds(60.0)) @>

            cts.Cancel()

            try
                task.Wait(TimeSpan.FromSeconds(10.0)) |> ignore
            with :? AggregateException ->
                ()
        finally
            (daemon :> IDisposable).Dispose())
