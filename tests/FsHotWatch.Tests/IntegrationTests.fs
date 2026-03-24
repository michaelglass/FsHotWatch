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
