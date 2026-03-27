module FsHotWatch.Tests.CheckPipelineTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.CheckPipeline

/// A null checker suffices for tests that only exercise state management
/// (RegisterProject / lookup) without performing actual compilation.
let private nullChecker = Unchecked.defaultof<FSharpChecker>

[<Fact>]
let ``CheckFile returns None when no project registered for the file`` () =
    let pipeline = CheckPipeline(nullChecker)
    let result = pipeline.CheckFile("/tmp/nonexistent/Lib.fs") |> Async.RunSynchronously
    test <@ result = None @>

[<Fact>]
let ``RegisterProject makes CheckFile find the project for its source files`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-pipeline-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FSharpChecker.Create(projectCacheSize = 10)

        let pipeline = CheckPipeline(checker)

        let sourceFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")

        let absSource = Path.GetFullPath(sourceFile)

        // Use GetProjectOptionsFromScript to build minimal project options
        let options, _diagnostics =
            checker.GetProjectOptionsFromScript(
                absSource,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText absSource)
            )
            |> Async.RunSynchronously

        pipeline.RegisterProject("/tmp/Test.fsproj", options)

        let result = pipeline.CheckFile(absSource) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.File = absSource @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``CheckFile returns None for unregistered file even when other projects exist`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options =
        { ProjectFileName = "/tmp/Other.fsproj"
          ProjectId = None
          SourceFiles = [| "/tmp/Other.fs" |]
          OtherOptions = [||]
          ReferencedProjects = [||]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = System.DateTime.UtcNow
          UnresolvedReferences = None
          OriginalLoadReferences = []
          Stamp = None }

    pipeline.RegisterProject("/tmp/Other.fsproj", options)

    let result = pipeline.CheckFile("/tmp/NotRegistered.fs") |> Async.RunSynchronously
    test <@ result = None @>

[<Fact>]
let ``CheckProject returns None for unregistered project`` () =
    let pipeline = CheckPipeline(nullChecker)

    let result =
        pipeline.CheckProject("/tmp/NoSuchProject.fsproj") |> Async.RunSynchronously

    test <@ result = None @>

[<Fact>]
let ``CheckProject returns results for all registered source files`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-checkproj-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FSharpChecker.Create(projectCacheSize = 10)

        let pipeline = CheckPipeline(checker)

        let sourceFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")

        let absSource = Path.GetFullPath(sourceFile)

        let options, _diagnostics =
            checker.GetProjectOptionsFromScript(
                absSource,
                FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText absSource)
            )
            |> Async.RunSynchronously

        let projectPath = "/tmp/CheckProject.fsproj"
        pipeline.RegisterProject(projectPath, options)

        let result = pipeline.CheckProject(projectPath) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Project = projectPath @>
        test <@ result.Value.FileResults.Count > 0 @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``PrepareForRediscovery clears stale file options`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options =
        { ProjectFileName = "/tmp/MyProject.fsproj"
          ProjectId = None
          SourceFiles = [| "/tmp/FileA.fs"; "/tmp/FileB.fs" |]
          OtherOptions = [||]
          ReferencedProjects = [||]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = System.DateTime.UtcNow
          UnresolvedReferences = None
          OriginalLoadReferences = []
          Stamp = None }

    pipeline.RegisterProject("/tmp/MyProject.fsproj", options)

    // Verify both files and the project are registered
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains "/tmp/FileA.fs" @>
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains "/tmp/FileB.fs" @>
    test <@ pipeline.GetRegisteredProjects() |> List.contains "/tmp/MyProject.fsproj" @>

    // Simulate re-discovery: clear, then re-register without FileB
    pipeline.PrepareForRediscovery()

    let updatedOptions =
        { options with
            SourceFiles = [| "/tmp/FileA.fs" |] }

    pipeline.RegisterProject("/tmp/MyProject.fsproj", updatedOptions)

    // FileA should still be registered, FileB should be gone
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains "/tmp/FileA.fs" @>
    test <@ pipeline.GetAllRegisteredFiles() |> List.contains "/tmp/FileB.fs" |> not @>
    test <@ pipeline.GetRegisteredProjects() |> List.contains "/tmp/MyProject.fsproj" @>

[<Fact>]
let ``CheckFile returns None when token is cancelled`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts = new CancellationTokenSource()
    cts.Cancel()

    let result =
        pipeline.CheckFile("/tmp/anything.fs", cts.Token) |> Async.RunSynchronously

    test <@ result = None @>

[<Fact>]
let ``CancelPreviousCheck cancels previous token for same file`` () =
    let pipeline = CheckPipeline(nullChecker)
    let first = pipeline.CancelPreviousCheck("/tmp/Test.fs")
    test <@ not first.IsCancellationRequested @>

    let _second = pipeline.CancelPreviousCheck("/tmp/Test.fs")
    test <@ first.IsCancellationRequested @>

[<Fact>]
let ``PrepareForRediscovery cancels all file tokens`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts1 = pipeline.CancelPreviousCheck("/tmp/A.fs")
    let cts2 = pipeline.CancelPreviousCheck("/tmp/B.fs")

    pipeline.PrepareForRediscovery()

    test <@ cts1.IsCancellationRequested @>
    test <@ cts2.IsCancellationRequested @>

[<Fact>]
let ``CancelPreviousCheck links to caller token`` () =
    let pipeline = CheckPipeline(nullChecker)
    let callerCts = new CancellationTokenSource()
    let fileCts = pipeline.CancelPreviousCheck("/tmp/Linked.fs", callerCts.Token)

    callerCts.Cancel()
    test <@ fileCts.IsCancellationRequested @>
