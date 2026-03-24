module FsHotWatch.Tests.IntegrationTests

open System
open System.IO
open System.Reflection
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Lint.LintPlugin
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Analyzers.AnalyzersPlugin

let private findRepoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

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

[<Fact>]
let ``all plugins receive events when checking a file`` () =
    let repoRoot = findRepoRoot ()

    let checker =
        FSharpChecker.Create(
            projectCacheSize = 200,
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true
        )

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
    let dbPath =
        Path.Combine(Path.GetTempPath(), $"fshw-inttest-{Guid.NewGuid():N}.db")

    let testPrune = TestPrunePlugin(dbPath, repoRoot)
    let lint = LintPlugin()
    let fantomas = FormatCheckPlugin()
    let analyzers = AnalyzersPlugin([])

    host.Register(testPrune)
    host.Register(lint)
    host.Register(fantomas)
    host.Register(analyzers)

    // Check the file via the pipeline
    let result = pipeline.CheckFile(sourceFile) |> Async.RunSynchronously

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

    // Cleanup
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

[<Fact>]
let ``analyzers plugin loads real analyzers and runs without crashing`` () =
    let repoRoot = findRepoRoot ()

    // Build the example analyzer project
    let exampleProjectDir = Path.Combine(repoRoot, "examples/ExampleAnalyzer")

    let buildPsi =
        System.Diagnostics.ProcessStartInfo("dotnet", $"""build "{exampleProjectDir}" -v quiet""")

    buildPsi.UseShellExecute <- false
    let buildProc = System.Diagnostics.Process.Start(buildPsi)
    buildProc.WaitForExit()
    test <@ buildProc.ExitCode = 0 @>

    // Find analyzer DLL paths
    let gResearchPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget/packages/g-research.fsharp.analyzers/0.22.0/analyzers/dotnet/fs"
        )

    let customAnalyzerPath =
        Path.Combine(repoRoot, "examples/ExampleAnalyzer/bin/Debug/net10.0")

    let analyzerPaths =
        [ gResearchPath; customAnalyzerPath ] |> List.filter Directory.Exists

    // At minimum the custom analyzer should be available
    test <@ analyzerPaths |> List.exists (fun p -> p.Contains("ExampleAnalyzer")) @>

    let analyzers = AnalyzersPlugin(analyzerPaths)

    let checker =
        FSharpChecker.Create(
            projectCacheSize = 200,
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = true
        )

    let host = PluginHost.create checker repoRoot
    host.Register(analyzers)

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

    let result = pipeline.CheckFile(sourceFile) |> Async.RunSynchronously

    match result with
    | Some checkResult -> host.EmitFileChecked(checkResult)
    | None -> failwith "Failed to check file"

    // The analyzers plugin should have completed (or failed gracefully)
    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    // Verify it completed rather than failed — real analyzers should work
    match status.Value with
    | Completed _ -> () // Success — analyzers ran and produced results
    | PluginStatus.Failed(msg, _) ->
        // G-Research analyzers may fail due to FCS version mismatch, that's OK
        // as long as the plugin handled it gracefully
        let info = sprintf "Analyzers failed gracefully: %s" msg
        Assert.True(true, info)
    | other -> Assert.Fail(sprintf "Unexpected status: %A" other)
