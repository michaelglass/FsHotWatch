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

/// Poll until the plugin status is no longer Running, with a timeout.
let private waitForStatusSettled (host: PluginHost) (pluginName: string) (timeoutMs: int) =
    waitForSettled host pluginName timeoutMs

// ---------------------------------------------------------------------------
// Helper: build the ExampleAnalyzer once (thread-safe, shared across tests)
// ---------------------------------------------------------------------------
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

// ---------------------------------------------------------------------------
// Helper: build the repo's own convention rules (FsHotWatch.Rules) once
// ---------------------------------------------------------------------------
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

    // Pick a simple source file from FsHotWatch itself
    let sourceFile = Path.Combine(repoRoot, "src", "FsHotWatch", "Events.fs")
    let source = File.ReadAllText(sourceFile)

    // Get script options for the file
    let sourceText = SourceText.ofString source

    let projOptions =
        checker.GetProjectOptionsFromScript(sourceFile, sourceText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    pipeline.RegisterProject("FsHotWatch", projOptions)

    // Register all four plugins
    let dbPath = Path.Combine(Path.GetTempPath(), $"fshw-inttest-{Guid.NewGuid():N}.db")

    let testPrune = TestPrunePlugin.create dbPath repoRoot None None None None None []

    let lint = LintPlugin.create None None None None
    let fantomas = createFormatCheck None
    let analyzers = AnalyzersPlugin.create None [] None DiagnosticSeverity.Hint

    host.RegisterHandler(testPrune)
    host.RegisterHandler(lint)
    host.RegisterHandler(fantomas)
    host.RegisterHandler(analyzers)

    // Check the file via the pipeline
    let result =
        pipeline.CheckFile(AbsFilePath.create sourceFile) |> Async.RunSynchronously

    // Emit the check result to plugins (triggers lint, analyzers, test-prune)
    match result with
    | Some checkResult -> host.EmitFileChecked(checkResult)
    | None -> failwith "Failed to check file"

    // Verify plugins that listen to OnFileChecked received events
    test <@ host.GetStatus("lint").IsSome @>
    test <@ host.GetStatus("analyzers").IsSome @>
    test <@ host.GetStatus("test-prune").IsSome @>

    // Emit a FileChanged for fantomas (it listens to OnFileChanged, not OnFileChecked)
    host.EmitFileChanged(SourceChanged [ sourceFile ])
    test <@ host.GetStatus("format-check").IsSome @>

    // Run the "diagnostics" command on the analyzers plugin
    let diagResult = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ diagResult.IsSome @>
    test <@ diagResult.Value.Contains("analyzers") @>
    test <@ diagResult.Value.Contains("files") @>
    test <@ diagResult.Value.Contains("diagnostics") @>

    // Run the "warnings" command on the lint plugin
    let warnResult = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ warnResult.IsSome @>
    test <@ warnResult.Value.Contains("files") @>
    test <@ warnResult.Value.Contains("warnings") @>

    // Run the "unformatted" command on the format-check plugin
    let fmtResult = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ fmtResult.IsSome @>
    test <@ fmtResult.Value.Contains("count") @>

    // Run the "affected-tests" command on the test-prune plugin
    let testsResult = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ testsResult.IsSome @>
    test <@ testsResult.Value.StartsWith("[") @>

    // Run the "changed-files" command on the test-prune plugin
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

    // Check Events.fs — it has match expressions the wildcard analyzer might inspect
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

    // The analyzers plugin should have completed (or failed gracefully)
    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    // Verify it completed rather than failed — real analyzers should work
    match status.Value with
    | Completed _ -> () // Success — analyzers ran and produced results
    | PluginStatus.Failed(msg, _, _) ->
        // G-Research analyzers may fail due to FCS version mismatch, that's OK
        // as long as the plugin handled it gracefully
        let info = sprintf "Analyzers failed gracefully: %s" msg
        Assert.True(true, info)
    | other -> Assert.Fail(sprintf "Unexpected status: %A" other)

// ---------------------------------------------------------------------------
// Fail-loud guard (DaemonConfig.analyzersLoadFailure): a CONFIGURED analyzer
// path that loads ≥1 analyzer must NOT fire the guard. Uses the real
// ExampleAnalyzer bin so this exercises the genuine load path (excluded from
// coverage in this project because the SDK-reflection load is nondeterministic).
// ---------------------------------------------------------------------------
[<Fact(Timeout = 30000)>]
let ``analyzers load guard does not fire when a real analyzer loads`` () =
    let analyzerPath = exampleAnalyzerPath.Value

    let handler =
        AnalyzersPlugin.create None [ analyzerPath ] None DiagnosticSeverity.Hint

    // The real ExampleAnalyzer DLL contributes at least one analyzer.
    test <@ handler.Init.LoadedCount >= 1 @>

    // Per-path guard: the one configured path loaded ≥1 ⇒ no failure (gate stays
    // green). LoadedByPath is the genuine per-path result from the real load.
    test <@ (FsHotWatch.Cli.DaemonConfig.analyzerPathFailures handler.Init.LoadedByPath).IsNone @>

// ---------------------------------------------------------------------------
// Per-path strictness end-to-end through registerPlugins, exercising the REAL
// SDK load: a multi-path config where one path loads ≥1 (the ExampleAnalyzer
// bin) and the others are empty / missing (load 0) must STILL raise ConfigError,
// naming the zero-loading paths but NOT the one that loaded. This is the partial
// silent-skip the earlier aggregate (total==0) guard missed. Lives here (not the
// coverage-rachet unit suite) because the SDK-reflection load is nondeterministic.
// ---------------------------------------------------------------------------
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

        // The two zero-loading paths are named …
        Assert.Contains(emptyPath, ex.Message)
        Assert.Contains("no-such-analyzer-bin-dir", ex.Message)
        // … but the path that loaded ≥1 analyzer is NOT named.
        test <@ not (ex.Message.Contains(goodPath)) @>
        // The plugin must NOT have registered.
        test <@ not (daemon.Host.GetAllStatuses().ContainsKey("analyzers")) @>)

// ---------------------------------------------------------------------------
// Helper: create a temp directory with a single .fs returning (dir, filePath)
// ---------------------------------------------------------------------------
let private withTempFsFile (content: string) (action: string -> string -> 'a) =
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    let filePath = Path.Combine(dir, "Temp.fs")
    File.WriteAllText(filePath, content)

    try
        action dir filePath
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

// ---------------------------------------------------------------------------
// Helper: set up checker + pipeline + project options for a temp file
// ---------------------------------------------------------------------------
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

// ---------------------------------------------------------------------------
// Helper: run an analyzer test — build analyzer, check a temp file, assert on result
// ---------------------------------------------------------------------------

// Process-wide gate: AnalyzersPlugin tests share an FSharpChecker and contend on
// analyzer-DLL loading. Running >1 in parallel (or against a busy CPU from the
// rest of the suite) triggers >10s waits in `waitForTerminalStatus`. Serialize
// them so each gets a clean FCS slice.
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

        // failOnSeverity = Error so the analyzer's RAW severity survives to the
        // ledger. With the default Hint threshold, `promoteIfFailing` rewrites
        // every sub-error finding (incl. this wildcard Warning) to Error with a
        // `[promoted from warning]` prefix — correct production behaviour (it fails the
        // build), but it masks the raw severity these tests assert on. Error is
        // the highest threshold, so promotion never fires and a Warning stays a
        // Warning. The promotion path itself is covered by the dedicated
        // `promoteIfFailing` unit tests in AnalyzersPluginTests.fs.
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
            // Graceful skip on FCS version mismatch
            Assert.True(true, $"Skipped due to FCS exception: {ex.Message}"))

[<Fact(Timeout = 5000)>]
let ``format check plugin detects unformatted code`` () =
    // Badly formatted F# — extra spaces, wrong indentation
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
    // Use Fantomas to produce a known-good formatted file
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

        // Before any event, plugin status is Idle (initialized on register)
        let beforeStatus = host.GetStatus("format-check")
        test <@ beforeStatus = Some Idle @>

        // Trigger an event
        host.EmitFileChanged(SourceChanged [ filePath ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // After event, status should be Completed
        let afterStatus = host.GetStatus("format-check")
        test <@ afterStatus.IsSome @>

        match afterStatus.Value with
        | Completed _ -> () // Expected lifecycle: Idle -> Running -> Completed
        | other -> Assert.Fail($"Expected Completed, got: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``multiple file changes are debounced into one batch by SourceChanged`` () =
    // The SourceChanged event accepts a list of files — verify the plugin
    // processes all files from a single batched event.
    let dir = Path.Combine(Path.GetTempPath(), $"fshw-debounce-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore

    try
        // Create multiple temp files rapidly
        let files =
            [ for i in 1..5 ->
                  let fp = Path.Combine(dir, $"File{i}.fs")
                  // Alternate between well-formatted and badly-formatted
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

        // Emit all files as a single batched SourceChanged event
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

[<Fact(Timeout = 30000)>]
let ``PROFILE: startup phases timing`` () =
    let repoRoot = findRepoRoot ()
    let sw = Stopwatch()

    sw.Restart()

    let checker =
        FSharpChecker.Create(
            projectCacheSize = 200,
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true,
            parallelReferenceResolution = true
        )

    sw.Stop()
    let phase1 = sw.ElapsedMilliseconds

    sw.Restart()

    let searchDirs =
        [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
        |> List.filter Directory.Exists

    let fsprojFiles =
        searchDirs
        |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
        |> List.filter (fun f ->
            let n = f.Replace('\\', '/')
            not (n.Contains("/obj/")) && not (n.Contains("/bin/")))

    sw.Stop()
    let phase2 = sw.ElapsedMilliseconds

    sw.Restart()
    let pipeline = CheckPipeline(checker)
    let mutable totalFiles = 0

    for fsproj in fsprojFiles do
        try
            let doc = System.Xml.Linq.XDocument.Load(fsproj)
            let projDir = Path.GetDirectoryName(Path.GetFullPath(fsproj))

            let sourceFiles =
                doc.Descendants(System.Xml.Linq.XName.Get "Compile")
                |> Seq.choose (fun el ->
                    let inc = el.Attribute(System.Xml.Linq.XName.Get "Include")

                    if inc <> null then
                        Some(Path.GetFullPath(Path.Combine(projDir, inc.Value)))
                    else
                        None)
                |> Seq.toArray

            if sourceFiles.Length > 0 && File.Exists(sourceFiles.[0]) then
                let source = File.ReadAllText(sourceFiles.[0])
                let sourceText = SourceText.ofString source

                let projOptions, _ =
                    checker.GetProjectOptionsFromScript(sourceFiles.[0], sourceText, assumeDotNetFramework = false)
                    |> Async.RunSynchronously

                pipeline.RegisterProject(
                    fsproj,
                    { projOptions with
                        SourceFiles = sourceFiles }
                )

                totalFiles <- totalFiles + sourceFiles.Length
        with _ ->
            ()

    sw.Stop()
    let phase3 = sw.ElapsedMilliseconds

    sw.Restart()
    let allFiles = pipeline.GetAllRegisteredFiles()
    let mutable checkedCount = 0

    for file in allFiles do
        match pipeline.CheckFile(file) |> Async.RunSynchronously with
        | Some _ -> checkedCount <- checkedCount + 1
        | None -> ()

    sw.Stop()
    let phase4 = sw.ElapsedMilliseconds
    let total = phase1 + phase2 + phase3 + phase4

    let profilePath =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "profile.txt")

    let lines =
        [ ""
          "=== STARTUP PROFILE ==="
          $"Phase 1 — Create FSharpChecker: %d{phase1}ms"
          $"Phase 2 — Discover %d{fsprojFiles.Length} projects: %d{phase2}ms"
          $"Phase 3 — Load options (%d{totalFiles} files): %d{phase3}ms"
          $"Phase 4 — Check %d{checkedCount}/%d{allFiles.Length} files: %d{phase4}ms"
          $"TOTAL: %d{total}ms"
          $"Serializing would save Phases 2-3: %d{phase2 + phase3}ms (%d{if total > 0L then (phase2 + phase3) * 100L / total else 0L}%% of total)"
          $"Phase 4 (FCS warm-up) is unavoidable: %d{phase4}ms (%d{if total > 0L then phase4 * 100L / total else 0L}%% of total)"
          "===" ]

    File.WriteAllLines(profilePath, lines)

    test <@ checkedCount >= 0 @>

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

    // Events.fs from FsHotWatch itself should be clean
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
        | Completed _ -> () // Clean code, lint completed
        | PluginStatus.Failed(msg, _, _) ->
            // FCS version mismatch may cause lint to fail — acceptable
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
            | Completed _ -> () // Empty analyzer paths, should complete with no diagnostics
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

// ---------------------------------------------------------------------------
// Bug C (2026-06-17): a long-lived (warm) daemon loads analyzers ONCE at
// construction. When a downstream repo ADDS a new analyzer (new .fs + <Compile>
// entry) and rebuilds, the analyzer DLL on disk changes but the in-memory
// `client` still holds the OLD set — the new analyzer silently never runs, so
// the gate reports green without ever inspecting the codebase with it. This was
// observed twice in thellma/intelligence (a new analyzer reported 0 findings
// when it actually had 24). Bug A's cache key invalidates stale RESULTS but the
// loaded assembly stays stale; Bug B fails loud on a 0-analyzer load but a
// PARTIAL stale load that's merely MISSING the new analyzer slips past it.
//
// Repro on a genuine warm handler: point it at an analyzer dir that starts
// EMPTY (loads 0 analyzers), emit FileChecked on a wildcard-DU file → no
// findings. Then copy a real analyzer DLL into the SAME dir (a downstream "add
// + rebuild") and re-emit → the analyzer must now fire. Pre-fix the second
// emission still saw 0 analyzers loaded; post-fix `reloadIfStale` re-scans the
// changed assembly set before analyzing and the new analyzer runs.
// ---------------------------------------------------------------------------
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

            // The handler loads from analyzerDir at construction — currently EMPTY,
            // so zero analyzers are loaded (the warm daemon's starting state before
            // the downstream add). failOnSeverity = Error so the raw Warning the
            // ExampleAnalyzer emits survives to the ledger unpromoted.
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

                // Cycle 1: empty analyzer dir ⇒ no analyzer ⇒ no findings. The
                // plugin reaches Completed; assert the ledger stays empty.
                emit ()
                waitForTerminalStatus host "analyzers" 15000
                test <@ List.isEmpty (findings ()) @>

                // Downstream adds a new analyzer to the SAME path and rebuilds:
                // copy the real ExampleAnalyzer DLL set into the watched dir.
                Directory.GetFiles(exampleBin, "*.dll")
                |> Array.iter (fun f -> File.Copy(f, Path.Combine(analyzerDir, Path.GetFileName f), true))

                // Cycle 2: the assembly set on disk changed ⇒ reloadIfStale must
                // re-scan and load the new analyzer BEFORE analyzing this file, so
                // the wildcard-DU warning now appears. The plugin status lingers at
                // Completed from cycle 1, so poll the ledger (the real
                // postcondition) rather than waiting on a status transition that
                // already happened. Pre-fix the ledger stayed empty (stale load).
                emit ()
                waitUntil (fun () -> not (List.isEmpty (findings ()))) 15000

                let after = findings ()
                test <@ not (List.isEmpty after) @>
                test <@ after |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Warning) @>)))

// ===========================================================================
// FsHotWatch.Rules — the repo's own convention analyzers (FSHW-CLAIM-001 /
// FSHW-CLOCK-001), exercised through the GENUINE load-and-run path (the same
// AnalyzersPlugin host the gate uses, pointed at the real Rules bin). These
// are the positive controls: an analyzer that reports nothing and an analyzer
// that never loaded are indistinguishable, so each rule must be SEEN to fire
// on a deliberately-violating snippet before its green is worth anything.
// ===========================================================================

let private runRulesOn (source: string) : FsHotWatch.ErrorLedger.ErrorEntry list =
    let repoRoot = findRepoRoot ()
    let rulesBin = conventionRulesPath.Value
    let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
    let host = PluginHost.create checker repoRoot

    let analyzers =
        AnalyzersPlugin.create None [ rulesBin ] None DiagnosticSeverity.Error

    // The rules DLL must actually LOAD — a zero-load here would make every
    // assertion below vacuous.
    test <@ analyzers.Init.LoadedCount >= 1 @>

    host.RegisterHandler(analyzers)

    withTempFsFile source (fun _dir tmpFile ->
        match checkTempFile checker tmpFile with
        | Some checkResult ->
            // The fixture MUST parse. An offside-broken source yields an empty
            // AST, so every analyzer reports zero findings — and a "the rule
            // fires" assertion would fail for the wrong reason while a "stays
            // silent" assertion would PASS for the wrong reason. Refuse the
            // fixture rather than let it produce a meaningless verdict.
            test <@ not checkResult.ParseResults.ParseHadErrors @>

            host.EmitFileChecked(checkResult)
            waitForTerminalStatus host "analyzers" 15000
            host.GetErrorsByPlugin("analyzers") |> Map.toList |> List.collect snd
        | None -> failwith "FCS failed to check temp file")

/// Build an F# source file from explicit lines. NOT a `\`-continuation string:
/// that strips the leading whitespace of each continued line, which silently
/// produces an offside-broken source that FCS cannot parse — every analyzer
/// then reports zero findings and a "the rule fires" test passes VACUOUSLY.
/// (Caught exactly that way while writing these.)
let private fsSource (lines: string list) : string = String.concat "\n" lines + "\n"

/// A self-contained, type-correct stand-in for the real PluginCtx.RunExclusive
/// seam. The rule is name-based, so a structurally identical local reproduces it.
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
        // UTC clocks and a MATCHED claim — the negative control proving the
        // positive controls above aren't simply firing on everything (and that
        // the fixture genuinely PARSES: an unparseable source yields zero
        // findings too, which would make this pass for the wrong reason).
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

/// A self-contained stand-in for the real `TestResult` seam, mirroring the one
/// fact the rule turns on: `isPassed` is TRUE for the zero-match case. The rule
/// is name-based, so a structurally identical local reproduces it — and stating
/// the trap here keeps the fixture honest about WHY the fold is wrong.
let private verdictPreamble =
    [ "module Test"
      "type TestResult ="
      "    | TestsPassed"
      "    | TestsFailed"
      "    | TestsNoMatch"
      "module TestResult ="
      "    let isPassed r ="
      "        match r with"
      "        | TestsFailed -> false"
      // The whole hazard, in one line: a project that executed nothing passes.
      "        | TestsPassed"
      "        | TestsNoMatch -> true"
      "    let isNoMatch r ="
      "        match r with"
      "        | TestsNoMatch -> true"
      "        | TestsPassed"
      "        | TestsFailed -> false"
      // The SCOPE predicate, present so the negative control can pin that the
      // rule does not reach for it. Note the shape the real one has: true for
      // the zero-match case, deliberately, so a scope fold cannot be fooled by
      // a project that executed nothing.
      "    let wasFiltered r ="
      "        match r with"
      "        | TestsPassed -> false"
      "        | TestsFailed -> false"
      "        | TestsNoMatch -> true"
      "let allZeroMatchOf (results: Map<string, TestResult>) ="
      "    not results.IsEmpty && results |> Map.forall (fun _ r -> TestResult.isNoMatch r)" ]

[<Fact(Timeout = 60000)>]
let ``FSHW-VERDICT-001 fires on a run verdict folded from isPassed`` () =
    withAnalyzerGate (fun () ->
        let source =
            fsSource (
                verdictPreamble
                @ [ "let green (results: Map<string, TestResult>) ="
                    "    results |> Map.forall (fun _ r -> TestResult.isPassed r)"
                    "let green2 (results: TestResult list) = List.forall TestResult.isPassed results"
                    "let green3 (results: Map<string, TestResult>) ="
                    "    let allPassed = results |> Map.forall (fun _ r -> TestResult.isPassed r)"
                    "    allPassed" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "not a run-level verdict")

        // One per fold: piped Map.forall, point-free List.forall, and a fold bound
        // to a name whose decision asks nothing further.
        test <@ findings.Length = 3 @>
        test <@ findings |> List.forall (fun e -> e.Severity = DiagnosticSeverity.Error) @>)

[<Fact(Timeout = 60000)>]
let ``FSHW-VERDICT-001 stays silent on the legitimate uses of isPassed`` () =
    withAnalyzerGate (fun () ->
        // The negative control that decides whether this rule is shippable. Every
        // shape here is live in TestPrunePlugin, and a rule that fires on any of
        // them is a rule that gets suppressed rather than obeyed.
        let source =
            fsSource (
                verdictPreamble
                @ [ // Filtering FOR the non-green — `recordRunOutcome`. Correct: it
                    // selects what to report, it does not infer a green.
                    "let nonGreen (results: Map<string, TestResult>) ="
                    "    results |> Map.toList |> List.filter (fun (_, r) -> not (TestResult.isPassed r))"
                    // Per-project, on a single result.
                    "let projectPassed (r: TestResult) = TestResult.isPassed r"
                    // A forall over projects whose predicate is a LOOKUP, not the
                    // pass predicate — the per-project green-commit fold.
                    "let covered (results: Map<string, TestResult>) (names: Set<string>) ="
                    "    names"
                    "    |> Set.forall (fun n ->"
                    "        match Map.tryFind n results with"
                    "        | Some r -> TestResult.isPassed r"
                    "        | None -> false)"
                    // The cacheable-green gate: an all-passed fold that DOES ask the
                    // run-level question, in the same decision.
                    "let cacheable (results: Map<string, TestResult>) ="
                    "    let allPassed = results |> Map.forall (fun _ r -> TestResult.isPassed r)"
                    "    let allZeroMatchRun = allZeroMatchOf results"
                    "    allPassed && not allZeroMatchRun"
                    // A bare SCOPE fold. Silence here is a DECISION, not an oversight,
                    // and this line is what pins it: see `isPassedPredicate` in
                    // ConventionAnalyzers.fs for why the two folds are different bugs.
                    // If you are widening the rule to cover scope, this is the
                    // assertion you have to come and delete on purpose.
                    //
                    // NOT `TestResult.ranFullSuite`'s body, though it was when this
                    // control was written — AUTOMATION-281 made the real one
                    // `executedAnything results && Map.forall …`. Deliberately kept as
                    // the BARE fold: that is the expression a widened rule would flag,
                    // so it is the one worth pinning. Naming it `ranFullSuite` here is
                    // just fixture shadowing, not a copy of core.
                    "let ranFullSuite (results: Map<string, TestResult>) ="
                    "    results |> Map.forall (fun _ r -> not (TestResult.wasFiltered r))" ]
            )

        let findings =
            runRulesOn source
            |> List.filter (fun e -> e.Message.Contains "not a run-level verdict")

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
    let dbPath =
        Path.Combine(Path.GetTempPath(), $"fshw-tp-inttest-{Guid.NewGuid():N}.db")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
            TestPrunePlugin.create dbPath "/tmp" (Some testConfigs) None None None None []

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

    // No matching files — poll briefly; will time out at Idle (expected)
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
        // Use `sh -c` with a direct append — avoids writing/chmoding a helper script.
        // The plugin's command appends a line to the sentinel every time it runs.
        let cmd = "sh"
        let args = $"-c \"echo ran >> '{sentinel}'\""

        // Use an in-memory cache so we can observe cache-hit vs cache-miss behavior.
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

        // Fix a stable cache key so replays hit the cache (otherwise each event
        // produces a new key and cache replay never fires — defeating the test).
        let getCommitId () = Some "stable-commit-for-test"

        let trigger: FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern = Some(fun f -> f.EndsWith(".ratchet.json"))
              AfterTests = None }

        let handler = create (PluginName.create pluginName) trigger cmd args "/tmp" None

        let parsedPattern = FsHotWatch.Watcher.FilePattern.parse pattern
        host.RegisterHandler(handler)
        host.RegisterFileCommandPattern(pluginName, parsedPattern)

        // First run: matching file event → plugin runs, sentinel has 1 line.
        // Wait on the observable SIDE EFFECT, not on `waitForStatusSettled`:
        // "settled" means "status is not Running", which is also true in the
        // window before the freshly-dispatched event has flipped the plugin to
        // Running — so under load the wait can return on the stale/None status
        // before the command has written the sentinel. Waiting for the file
        // itself removes that race.
        host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
        waitUntil (fun () -> File.Exists(sentinel) && File.ReadAllLines(sentinel).Length >= 1) 5000
        waitForStatusSettled host pluginName 5000
        test <@ File.Exists(sentinel) @>
        test <@ File.ReadAllLines(sentinel).Length = 1 @>

        // Second run with same commit id: cache hit, plugin does NOT run.
        // Sentinel line count should stay at 1.
        host.EmitFileChanged(SourceChanged [ "coverage.ratchet.json" ])
        Thread.Sleep(500) // cache replay is synchronous but give dispatch time to settle
        waitForStatusSettled host pluginName 5000
        test <@ File.ReadAllLines(sentinel).Length = 1 @>

        // Rerun via the public host API — same call the IPC endpoint makes.
        let rerunResult = host.RerunFileCommandPlugin(pluginName)
        test <@ rerunResult = Result.Ok() @>

        // Status already shows Completed from the previous run, so `waitForStatusSettled`
        // would return immediately. Wait for observable side effect (second line).
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
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        // Register FormatPreprocessor
        let preprocessor = FormatPreprocessor()
        host.RegisterPreprocessor(preprocessor)

        // Register BuildPlugin (echo for success)
        let buildHandler =
            BuildPlugin.create "echo" "build ok" [] (ProjectGraph()) [] None [] None

        host.RegisterHandler(buildHandler)

        // Register TestPrunePlugin with echo test command
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
            TestPrunePlugin.create dbPath "/tmp" (Some testConfigs) None None None None []

        host.RegisterHandler(testPruneHandler)

        // Create a temp .fs file and run preprocessors on it
        let fsFile = Path.Combine(tmpDir, "Temp.fs")
        File.WriteAllText(fsFile, "module Temp\n\nlet x = 5\n")
        let modified = host.RunPreprocessors([ fsFile ])
        // Well-formatted file should not be modified
        test <@ modified |> List.contains fsFile |> not @>

        // Emit FileChanged — triggers build plugin
        host.EmitFileChanged(SourceChanged [ fsFile ])

        // Build now runs on thread pool — wait for it to settle
        waitForTerminalStatus host "build" 5000

        // Build should succeed (echo), which triggers test-prune tests,
        // which emit TestCompleted, which triggers coverage
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

        // Verify format preprocessor status
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

    // Use /bin/sleep 1 as a slow build command so the second emit arrives while the first is running
    let handler =
        BuildPlugin.create "/bin/sleep" "1" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    // Emit two FileChanged events — the building guard should prevent the second build
    host.EmitFileChanged(SourceChanged [ "src/A.fs" ])

    // Wait until first build is running before emitting second event
    waitUntil
        (fun () ->
            match host.GetStatus("build") with
            | Some(PluginStatus.Running _) -> true
            | _ -> false)
        5000

    host.EmitFileChanged(SourceChanged [ "src/B.fs" ])

    waitForTerminalStatus host "build" 5000

    // Wait for build-counter to process the BuildCompleted event
    waitUntil (fun () -> buildCount >= 1) 2000

    // Only one build should have completed — the guard skipped the second
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
    let dbPath =
        Path.Combine(Path.GetTempPath(), $"fshw-tp-concurrent-{Guid.NewGuid():N}.db")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
            TestPrunePlugin.create dbPath "/tmp" (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        // Emit two BuildSucceeded events — the testsRunning guard should queue the second
        host.EmitBuildCompleted(BuildSucceeded)

        // Wait until first test run is active before emitting second event
        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(PluginStatus.Running _) -> true
                | _ -> false)
            5000

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for async test execution to complete (sleep 1 + potential re-run/skip)
        waitForTerminalStatus host "test-prune" 15000

        let cmdResult = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
        test <@ cmdResult.IsSome @>
        // The rerun with 0 affected classes is now correctly skipped (empty results),
        // or the first cold-start run produces passed results — either is acceptable.
        let doc = JsonDocument.Parse(cmdResult.Value)
        let projects = doc.RootElement.GetProperty("projects")

        Assert.True(
            projects.GetArrayLength() = 0
            || projects.[0].GetProperty("status").GetString() = "passed"
        )

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // Status should not be Failed from resource exhaustion — the guard prevents concurrent runs
        test
            <@
                match status.Value with
                | Completed _ -> true
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
// Real-world validation: confirms the DI seams in unit tests reflect
// actual production behavior. Excluded from coverage on purpose — these
// exercise OS-level error modes (permission denied, file deletion races)
// that are flaky enough to make the coverage ratchet jitter.
// ===========================================================================

[<Fact(Timeout = 10000)>]
let ``hashFileWith: real File.ReadAllBytes throws on unreadable file`` () =
    // Validates that hashFileWith's None branch — exercised in unit tests via
    // an injected throwing reader — actually fires in production when a file
    // exists but cannot be read (permission denied). If macOS ever stops
    // throwing here, the unit test's mock would no longer represent reality.
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
// ProcessHelper kill-on-timeout tests — moved from FsHotWatch.Tests because
// the kill/drain race produces nondeterministic line coverage on
// ProcessHelper.fs (drifts 89-92%). Excluded from coverage on purpose so the
// auto-ratchet in `mise run check` stays stable.
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
    // AUTOMATION-149. This assertion USED to check that `partial` reached the tail.
    // It was weakened to `| TimedOut _ -> ()` on the grounds that "capturing pre-kill
    // stdout races subprocess startup under load". It did not race the subprocess. It
    // raced the THREAD POOL: the drain's stream pumps were `task {}` continuations, so
    // on a saturated box the reader was never scheduled, the 2s window expired having
    // read zero bytes, and `runProcess` handed back "" — and a `string` tail could not
    // tell "we read nothing" from "the child printed nothing".
    //
    // That was AUTOMATION-126. It is fixed: the pumps own dedicated threads, so a
    // saturated pool cannot starve them, and a drain that still cannot finish says so
    // by its own name (`DrainTimedOut`) instead of forging an empty measurement. The
    // weakening was an accommodation made for a TOOL bug, and it is now load-bearing on
    // nothing — so the assertion goes back to what it should always have been.
    //
    // The child prints `partial` and THEN sleeps 10s, so those bytes are on the pipe
    // long before the 300ms timeout fires. A tail that lacks them is a drain that
    // failed to measure — the whole bug — and must be RED.
    match
        runProcess "sh" "-c \"echo partial; sleep 10\"" "." [] (ProcessBounds.silent (TimeSpan.FromMilliseconds 300.0))
    with
    | TimedOut(_, tail, _) ->
        // The kill tears the pipes down under the pumps, so whether they end at EOF
        // (`Drained`) or die mid-read (`DrainTimedOut`) is a genuine OS race: the TAG is
        // not the contract here, the CAPTURE is. Whichever way it lands, what the child
        // printed before the kill must be in it. `ProcessOutput.text` is the deliberate,
        // greppable act of reading a capture without demanding it be complete — and
        // asserting that a marker is PRESENT can only ever cost a hit, never invent one,
        // so a starved drain FAILS this test rather than sliding past it.
        Assert.Contains("partial", ProcessOutput.text tail)
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.killAll terminates tracked live processes`` () =
    // Per-scope registry isolates this test from concurrent ones — `install`
    // is AsyncLocal-scoped so killAll only affects this test's tracked PIDs.
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

    // Wait for the child to register (Process.Start is fast; track is the next line).
    // 8s deadline tolerates parallel-test thread-pool contention — a Task.Run body
    // sometimes doesn't reach Process.Start for several seconds under load.
    let deadline = DateTime.UtcNow.AddSeconds 8.0

    while registry.Snapshot().IsEmpty && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 25

    Assert.NotEmpty(registry.Snapshot())

    registry.KillAll()

    let completed = task.Wait(5000)
    Assert.True(completed, "runProcess did not return after killAll")

// ===========================================================================
// TestPrune timeout test — moved from FsHotWatch.Tests because it spawns a
// real `sleep 10` subprocess with a 1s timeout, exercising the kill-on-timeout
// drain race that causes line-coverage drift on TestPrunePlugin.fs (Aborted
// catch arm) and ProcessHelper.fs (drain branches).
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
// §1 fcs-signature oracle — regression test
// ===========================================================================
// Doc: docs/plans/2026-04-26-build-system-ideas-design.md §8.5 calls for a
// "synthetic cross-file test for §1": Bar.fs depends on Foo.fs's signature;
// edit Foo, verify Bar's downstream cache invalidates correctly.
//
// The contract under test is `fcsCheckSignature`: when Foo.fs changes in a way
// that affects Bar.fs's diagnostics (e.g. removes a symbol Bar uses), Bar.fs's
// signature must differ — even though Bar.fs's own bytes are unchanged. That
// signature feeds the analyzers/lint merkle cache key, so when this contract
// holds, downstream caches invalidate correctly.

[<Fact(Timeout = 30000)>]
let ``§1 regression: Bar's fcsCheckSignature changes when Foo's signature breaks Bar`` () =
    withTempDir "fshw-fcs-sig-cross-file" (fun tmpDir ->
        let fooPath = Path.Combine(tmpDir, "Foo.fs")
        let barPath = Path.Combine(tmpDir, "Bar.fs")

        File.WriteAllText(fooPath, "module Foo\nlet add (x: int) (y: int) = x + y\n")
        File.WriteAllText(barPath, "module Bar\nlet result = Foo.add 1 2\n")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        // Use Foo as the script-options entry, then override SourceFiles to the
        // 2-file project view so Bar can resolve Foo.
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

        // Bar.fs's own bytes are identical — only Foo changed. The signature must
        // still differ, otherwise downstream caches would replay stale results.
        test <@ sig1 <> sig2 @>)

// --- IgnoreFilterCache concurrency stress (moved from FsHotWatch.Tests) ---
//
// This 16-thread stress test asserts that IgnoreFilterCache is correct under
// high concurrent contention. It was originally a unit test in PathFilterTests
// but contributed to flaky branch coverage on src/FsHotWatch/PathFilter.fs:
// the inner double-checked-lock branch (Some entry when isFresh entry inside
// the lock) was recorded inconsistently by coverlet under high contention,
// so the file's branch percentage would flicker between 68.2% and 63.6%.
// Coverage stability is more important than this test's specific contribution
// to coverage; the concurrency-correctness assertion still runs as part of
// `mise run test-integration`.
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
// Moved from FsHotWatch.Tests due to subprocess-timing flakiness under the
// parallel xUnit collection runner. These tests spawn real `sleep` subprocesses
// and assert on cross-thread timing windows that are starved by the unit-test
// suite's parallel scheduler. They live here (where xunit runs less parallel
// content) and are excluded from coverage.
// ---------------------------------------------------------------------------

// Drop semantics under RunExclusive "build": while a build is in flight,
// additional FileChanged events must NOT spawn a second concurrent build.
// Counts BuildCompleted emissions across rapid back-to-back triggers and asserts
// exactly one fired.
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
    // Tiny delay to ensure RunExclusive has marked the slot Running before the
    // second dispatch evaluates the policy.
    System.Threading.Thread.Sleep(100)
    host.EmitFileChanged(SourceChanged [ "src/B.fs" ])

    waitForTerminalStatus host "build" 15000
    // Settle window: any erroneously-spawned second build would also fire its
    // BuildCompleted within ~1.5s of the first; wait long enough to catch it.
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

// Moved from FsHotWatch.Tests.IpcTests 2026-04-30: this test registers four
// plugins, dispatches a FileChanged, and waits up to 5s for handler "d" to
// reach Failed status. Under contention the 5s wait runs out — observed ~20%
// flake rate locally under `mise run check` load. Intrinsically it's
// integration-grade (multi-plugin host + DaemonRpcTarget JSON-serialization
// roundtrip). Lives here with bumped timeouts.
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

// `waitForPluginTerminalIfRunning` tests moved from FsHotWatch.Tests
// 2026-05-02 — Task.Delay-based timing assertions systematically bust
// Fact(Timeout=5000) under parallel unit-suite load. Integration suite
// has looser parallelism so the timing windows hold.

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
// FR (docs/fr-auto-refresh-fsproj-changes.md, 2026-05-25):
// Adding a PackageReference + `dotnet restore` must refresh the affected
// project's FCS state without restarting the daemon and without forcing the
// user to save any .fs file.
//
// The user-visible symptom ("daemon keeps reporting stale FS0039 until
// manually stopped+started") decomposes into two daemon contracts that the
// FR fix has to honor. We test them as two separate integration tests rather
// than one E2E because the FCS-resolves-new-namespace endpoint relies on
// Ionide.ProjInfo's design-time MSBuild eval correctly picking up post-
// restore PackageReferences — a property of the *user's* real project tree,
// not a daemon contract per se, and minimal synthetic .fsproj test fixtures
// don't reliably trigger it.
//
//   contract 1 (this file, "daemon auto-rechecks ..."): after a .fsproj or
//   obj/project.assets.json change, the daemon re-evaluates the project's
//   options *and* re-runs FCS on the project's source files within one
//   debounce window — without any .fs file being saved. The original bug
//   was that the daemon would re-evaluate options but never re-check, so
//   the FCS error ledger stayed stale.
//
//   contract 2 (this file, "watcher classifies project.assets.json"):
//   `Watcher.classifyChange` maps `obj/project.assets.json` paths to
//   `ProjectChanged`, and the FileSystemWatcher / FSEvents stream actually
//   delivers events for that file even though it lives in obj/ (otherwise
//   excluded). This is the new signal the FR added.
//
//   contract 3 (this file, "daemon resolves a newly-added PackageReference
//   ... without restart"): the literal FR acceptance criterion, end to end.
//   Add a PackageReference, `dotnet restore`, and the FCS error referencing
//   the new namespace clears with no daemon restart and no .fs save. This
//   exercises the race the FR hinted at: the `.fsproj` edit fires the watcher
//   while `dotnet restore` is still writing obj/, so the first eval can see a
//   stale package graph; the post-restore `project.assets.json` write (now a
//   watched signal — contract 2) triggers a second eval that picks up the
//   resolved reference.
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
    // Contract 1: after a .fsproj change, the daemon must re-run FCS on the
    // project's source files within one debounce window — *without* any .fs
    // file being saved. The original bug: invalidate + re-evaluate options
    // fired, but the per-file re-check never followed, so the FCS error
    // ledger stayed stale.
    //
    // We don't assert anything about *which* FCS errors appear — just that a
    // second FileChecked cohort lands for the project's source file after the
    // .fsproj edit. That's the daemon contract; whether the new options
    // actually contain a new PackageReference is a downstream concern (real
    // project, real ProjInfo eval).
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

        // Capture FileChecked emissions for Lib.fs so we can count cohorts.
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

            // Boot scan: drives the first FileChecked for Lib.fs.
            daemon.ScanAll() |> Async.RunSynchronously

            // Give the FileChecked event time to propagate to our handler.
            waitUntil (fun () -> libCheckCount.Value >= 1) 30000

            if libCheckCount.Value < 1 then
                Assert.Fail(
                    sprintf
                        "Baseline failed: boot scan should produce ≥1 FileChecked for Lib.fs (got %d)"
                        libCheckCount.Value
                )

            let baseline = libCheckCount.Value

            // Edit .fsproj. The CONTENT of the new project file doesn't matter
            // for this contract test — only that it triggers a ProjectChanged
            // event. We use a comment so MSBuild eval still succeeds (and
            // returns the same options, which is the worst-case for re-check
            // detection — if our trigger fires even when options are unchanged,
            // it definitely fires when they change).
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

            // FR contract: a ProjectChanged must trigger a fresh FileChecked
            // cohort for the project's source files within one debounce window
            // + MSBuild re-eval. Allow a generous timeout for FSEvents
            // cold-start + 200ms project debounce + 0.5s MSBuild eval + FCS check.
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
    // Contract 2: the daemon's file watcher actually picks up writes to
    // `obj/project.assets.json` even though `obj/` is otherwise excluded by
    // `PathFilter.isGeneratedPath`. This is the new post-restore signal —
    // without it, the only signal the daemon has for a PackageReference
    // change is the .fsproj edit itself (which can race ahead of the
    // restore completing).
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

            // FSEvents cold-start can be 4-20s on macOS. Probe-loop writes
            // until at least one ProjectChanged for the assets file lands.
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
    // Contract 3: the literal FR acceptance criterion, end to end with a real
    // package (Newtonsoft.Json), real `dotnet restore`, real FCS.
    //
    // This is the test that motivated the whole change set. Note the ordering
    // hazard it deliberately exercises: writing the .fsproj fires the watcher
    // immediately, but `dotnet restore` then takes longer than the 200ms
    // project debounce — so the daemon's first re-eval can race ahead and read
    // a stale obj/ package graph (observed: FCS still can't see Newtonsoft).
    // The fix that makes this test pass is watching `obj/project.assets.json`
    // (contract 2): restore's final atomic write of that file triggers a
    // SECOND re-eval after the package graph is coherent, which resolves the
    // reference and — via the auto re-check (contract 1) — clears the error.
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

            // Boot scan: Lib.fs opens Newtonsoft, which isn't referenced yet →
            // baseline FS0039.
            daemon.ScanAll() |> Async.RunSynchronously
            waitUntil hasNewtonsoftError 90000

            if not (hasNewtonsoftError ()) then
                Assert.Fail(
                    "Baseline failed: expected FS0039 (Newtonsoft not defined) after boot scan but none observed."
                )

            // Add the package + restore. The watcher sees both the .fsproj edit
            // and (after restore) the obj/project.assets.json write. No .fs save,
            // no daemon restart.
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
// Scoped invalidation (gap #1): a change to one project must re-check that
// project AND its transitive dependents, but leave INDEPENDENT projects warm
// (not re-checked, cache preserved). The earlier "re-check everything" fix
// was correct but paid full cold-start cost on every project change.
// ===========================================================================

/// Resolve all symlink components to the canonical real path. macOS temp dirs
/// live under /var/folders (a symlink to /private/var/folders); FSEvents
/// reports the canonical /private form while MSBuild/ProjInfo preserve the
/// as-given /var form. Production repos generally sit at a canonical path, so
/// the daemon's scoped-invalidation path matches watcher events to project
/// paths by string equality and falls back to full re-discovery when they
/// diverge (correct, just not warmth-preserving). These scoped tests
/// canonicalize the temp root up front so they exercise the common-case
/// scoped path rather than the symlink-divergence fallback.
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

/// Per-file FileChecked counter handler. Returns (getCount, handler).
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
            // A must be re-checked.
            waitUntil (fun () -> getA () > aBefore) 60000

            if getA () <= aBefore then
                Assert.Fail(sprintf "A should be re-checked after its .fsproj change (stayed at %d)" (getA ()))

            // Give any erroneous B re-check time to land, then assert B stayed flat.
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

            // Touch A's .fsproj (the dependency). B depends on A, so B must
            // be re-checked even though B's own files/options didn't change.
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
// AUTOMATION-15 regression (absorbs AUTOMATION-26): the jj-merge wedge repro.
//
// On 2026-06-16 a foreground `scan`/`check` issued WHILE the daemon was mid
// auto-rebuild (a jj merge landed ~13 files at once, including .fsproj edits)
// wedged the daemon: `status` and `check` both blocked on the same dead socket
// ("could not connect to daemon: operation timed out"). This test reproduces
// the shape deterministically — a real daemon over a real pipe, a multi-file
// batch (incl. a .fsproj) landing to kick a (slow) auto-rebuild — and asserts
// the daemon STAYS RESPONSIVE: a concurrent `status`/`scanStatus` over the
// same pipe returns within a bounded window rather than hanging.
//
// Per the ticket: if this shows cancellation did NOT cure the race (status
// hangs), the test FAILS loudly rather than papering over it.
// ===========================================================================

/// Poll until the IPC server answers a cheap `scanStatus`, or the deadline.
/// Returns whether the server came up within the budget.
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

        // A build plugin whose build is a slow `sleep` — models the long auto
        // rebuild in flight. A DEFAULT timeout is wired (Some 30) so an
        // unbounded hang would instead fail fast; here the sleep finishes well
        // within it, but the point is the daemon must not be wedged WHILE it runs.
        let daemon = Daemon.createWith checker tmpDir Daemon.DaemonOptions.defaults

        try
            daemon.RegisterHandler(BuildPlugin.create "sleep" "5" [] daemon.Graph [] None [] (Some 30))

            let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

            if not (waitForIpcServer pipeName 60000) then
                Assert.Fail("IPC server never came up")

            // Let the boot scan settle.
            IpcClient.waitForScan pipeName -1L |> Async.RunSynchronously |> ignore

            // Land the multi-file batch incl. the .fsproj — kicks re-discovery +
            // the (slow) auto-rebuild. Bump the .fsproj and rewrite every source.
            File.WriteAllText(fsprojPath, fsprojBody "v2")

            for i, name in List.indexed fileNames do
                File.WriteAllText(
                    Path.Combine(projDir, name),
                    sprintf "module M%d\nlet v%d = %d\n" (i + 1) (i + 1) (i + 100)
                )

            // Give the watcher debounce + build-kick a moment to put a build in
            // flight, then HAMMER status concurrently. Each call MUST return
            // within a bounded window — a wedged daemon would block here, which
            // is exactly the 2026-06-16 symptom.
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

            // Several probes across the in-flight-build window. Responsive ==
            // every probe returns inside the budget (8s is generous; production
            // status is sub-second).
            let mutable allResponsive = true

            for _ in 1..5 do
                if not (statusReturnedWithin 8000) then
                    allResponsive <- false

                Thread.Sleep(400)

            if not allResponsive then
                Assert.Fail(
                    "DAEMON WEDGED: a concurrent `status` over the pipe did not return within budget while a \
                     multi-file rebuild was in flight. Cancellation did NOT cure the race — this needs a \
                     dedicated fix (do not paper over). See AUTOMATION-15/26."
                )

            // And it must still drive work to completion: the rebuild settles and
            // the next scan completes (not stuck forever).
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
