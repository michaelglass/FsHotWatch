module FsHotWatch.Tests.CheckPipelineTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.CheckCache
open FsHotWatch.Events

/// A null checker suffices for tests that only exercise state management
/// (RegisterProject / lookup) without performing actual compilation.
let private nullChecker = Unchecked.defaultof<FSharpChecker>

let private dummyOptions projectName sourceFiles =
    { ProjectFileName = projectName
      ProjectId = None
      SourceFiles = sourceFiles |> Array.ofList
      OtherOptions = [||]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = System.DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// In-memory cache backend for testing
type private InMemoryCache() =
    let store = System.Collections.Concurrent.ConcurrentDictionary<string, FileCheckResult>()

    member val InvalidateCalls = System.Collections.Generic.List<CacheKey>()
    member val ClearCalls = ref 0

    interface ICheckCacheBackend with
        member _.TryGet(key) =
            let hash = hashCacheKey key

            match store.TryGetValue(hash) with
            | true, v -> Some v
            | _ -> None

        member _.Set key result =
            let hash = hashCacheKey key
            store[hash] <- result

        member this.Invalidate(key) =
            let hash = hashCacheKey key
            store.TryRemove(hash) |> ignore
            this.InvalidateCalls.Add(key)

        member this.Clear() = incr this.ClearCalls; store.Clear()

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

[<Fact>]
let ``CheckFile assigns increasing version numbers`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-version-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FSharpChecker.Create(projectCacheSize = 10)
        let pipeline = CheckPipeline(checker)
        let sourceFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")
        let absSource = Path.GetFullPath(sourceFile)

        let options, _ =
            checker.GetProjectOptionsFromScript(absSource, SourceText.ofString (File.ReadAllText absSource))
            |> Async.RunSynchronously

        pipeline.RegisterProject("/tmp/Version.fsproj", options)

        let result1 = pipeline.CheckFile(absSource) |> Async.RunSynchronously
        let result2 = pipeline.CheckFile(absSource) |> Async.RunSynchronously

        test <@ result1.IsSome @>
        test <@ result2.IsSome @>
        test <@ result2.Value.Version > result1.Value.Version @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// --- InvalidateFile coverage (lines 44-54) ---

[<Fact>]
let ``InvalidateFile with cache backend calls Invalidate for registered file`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    let options = dummyOptions "/tmp/Inv.fsproj" [ "/tmp/Inv.fs" ]
    pipeline.RegisterProject("/tmp/Inv.fsproj", options)
    pipeline.InvalidateFile("/tmp/Inv.fs")
    test <@ cache.InvalidateCalls.Count = 1 @>

[<Fact>]
let ``InvalidateFile with cache backend does nothing for unregistered file`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)
    pipeline.InvalidateFile("/tmp/Unknown.fs")
    test <@ cache.InvalidateCalls.Count = 0 @>

[<Fact>]
let ``InvalidateFile without cache backend does not throw`` () =
    let pipeline = CheckPipeline(nullChecker)
    // No cache backend — should be a no-op without error
    pipeline.InvalidateFile("/tmp/NoCacheFile.fs")

// --- cancelAndDispose already-disposed CTS (lines 17-18) ---

[<Fact>]
let ``CancelPreviousCheck tolerates already-disposed CTS`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts = pipeline.CancelPreviousCheck("/tmp/Disposable.fs")
    // Manually dispose the CTS before the pipeline tries to cancel it
    cts.Dispose()
    // This second call will try to cancel+dispose the first (already disposed) CTS
    let newCts = pipeline.CancelPreviousCheck("/tmp/Disposable.fs")
    test <@ not newCts.IsCancellationRequested @>

// --- PrepareForRediscovery clears cache backend ---

[<Fact>]
let ``PrepareForRediscovery clears cache backend`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    let options = dummyOptions "/tmp/Cached.fsproj" [ "/tmp/Cached.fs" ]
    pipeline.RegisterProject("/tmp/Cached.fsproj", options)
    pipeline.PrepareForRediscovery()
    test <@ cache.ClearCalls.Value = 1 @>

// --- CheckProject with missing project (line 186) ---

[<Fact>]
let ``CheckProject returns None for missing project`` () =
    let pipeline = CheckPipeline(nullChecker)
    // Register a different project so the dictionary isn't empty
    let options = dummyOptions "/tmp/Exists.fsproj" [ "/tmp/Exists.fs" ]
    pipeline.RegisterProject("/tmp/Exists.fsproj", options)
    let result = pipeline.CheckProject("/tmp/Missing.fsproj") |> Async.RunSynchronously
    test <@ result = None @>

// --- Cancellation during CheckFile (lines 174-176) ---

[<Fact>]
let ``CheckFile returns None when cancelled before FCS call`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options = dummyOptions "/tmp/Cancel.fsproj" [ "/tmp/Cancel.fs" ]
    pipeline.RegisterProject("/tmp/Cancel.fsproj", options)

    let cts = new CancellationTokenSource()
    cts.Cancel()

    let result =
        pipeline.CheckFile("/tmp/Cancel.fs", cts.Token) |> Async.RunSynchronously

    test <@ result = None @>
