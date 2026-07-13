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
open FsHotWatch.Tests.TestHelpers

/// A null checker suffices for tests that only exercise state management
/// (RegisterProject / lookup) without performing actual compilation.
let private nullChecker = Unchecked.defaultof<FSharpChecker>

let private dummyOptions projectName sourceFiles =
    makeProjectOptions projectName sourceFiles []

/// In-memory cache backend for testing
type private InMemoryCache() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string, FileCheckResult>()

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

        member this.Clear() =
            this.ClearCalls.Value <- this.ClearCalls.Value + 1
            store.Clear()

[<Fact(Timeout = 15000)>]
let ``CheckFile returns None when no project registered for the file`` () =
    let pipeline = CheckPipeline(nullChecker)

    let result =
        pipeline.CheckFile(AbsFilePath.create "/tmp/nonexistent/Lib.fs")
        |> Async.RunSynchronously

    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``RegisterProject makes CheckFile find the project for its source files`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-pipeline-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

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

        let result =
            pipeline.CheckFile(AbsFilePath.create absSource) |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ AbsFilePath.value result.Value.File = absSource @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``CheckFile returns None for unregistered file even when other projects exist`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options = dummyOptions "/tmp/Other.fsproj" [ "/tmp/Other.fs" ]
    pipeline.RegisterProject("/tmp/Other.fsproj", options)

    let result =
        pipeline.CheckFile(AbsFilePath.create "/tmp/NotRegistered.fs")
        |> Async.RunSynchronously

    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``CheckProject returns None for unregistered project`` () =
    let pipeline = CheckPipeline(nullChecker)

    let result =
        pipeline.CheckProject("/tmp/NoSuchProject.fsproj") |> Async.RunSynchronously

    test <@ result = None @>

[<Fact(Timeout = 20000)>]
let ``CheckProject returns results for all registered source files`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-checkproj-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

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

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears stale file options`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options =
        dummyOptions "/tmp/MyProject.fsproj" [ "/tmp/FileA.fs"; "/tmp/FileB.fs" ]

    pipeline.RegisterProject("/tmp/MyProject.fsproj", options)

    // Verify both files and the project are registered
    test
        <@
            pipeline.GetAllRegisteredFiles()
            |> List.contains (AbsFilePath.create "/tmp/FileA.fs")
        @>

    test
        <@
            pipeline.GetAllRegisteredFiles()
            |> List.contains (AbsFilePath.create "/tmp/FileB.fs")
        @>

    test <@ pipeline.GetRegisteredProjects() |> List.contains "/tmp/MyProject.fsproj" @>

    // Simulate re-discovery: clear, then re-register without FileB
    pipeline.PrepareForRediscovery()

    let updatedOptions =
        { options with
            SourceFiles = [| "/tmp/FileA.fs" |] }

    pipeline.RegisterProject("/tmp/MyProject.fsproj", updatedOptions)

    // FileA should still be registered, FileB should be gone
    test
        <@
            pipeline.GetAllRegisteredFiles()
            |> List.contains (AbsFilePath.create "/tmp/FileA.fs")
        @>

    test
        <@
            pipeline.GetAllRegisteredFiles()
            |> List.contains (AbsFilePath.create "/tmp/FileB.fs")
            |> not
        @>

    test <@ pipeline.GetRegisteredProjects() |> List.contains "/tmp/MyProject.fsproj" @>

[<Fact(Timeout = 15000)>]
let ``RegisterProject excludes obj and bin files from registration`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options =
        dummyOptions
            "/tmp/MyProject.fsproj"
            [ "/tmp/src/Real.fs"
              "/tmp/src/obj/Debug/net10.0/AssemblyInfo.fs"
              "/tmp/src/obj/Debug/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.fs"
              "/tmp/src/bin/Release/net10.0/SomeThing.fs"
              "/tmp/src/Another.fs" ]

    pipeline.RegisterProject("/tmp/MyProject.fsproj", options)

    let registered = pipeline.GetAllRegisteredFiles()
    test <@ registered |> List.contains (AbsFilePath.create "/tmp/src/Real.fs") @>
    test <@ registered |> List.contains (AbsFilePath.create "/tmp/src/Another.fs") @>
    test <@ registered |> List.length = 2 @>

[<Fact(Timeout = 15000)>]
let ``CheckFile returns None when token is cancelled`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts = new CancellationTokenSource()
    cts.Cancel()

    let result =
        pipeline.CheckFile(AbsFilePath.create "/tmp/anything.fs", cts.Token)
        |> Async.RunSynchronously

    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``CancelPreviousCheck cancels previous token for same file`` () =
    let pipeline = CheckPipeline(nullChecker)
    let first = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/Test.fs")
    test <@ not first.IsCancellationRequested @>

    let _second = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/Test.fs")
    test <@ first.IsCancellationRequested @>

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery cancels all file tokens`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts1 = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/A.fs")
    let cts2 = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/B.fs")

    pipeline.PrepareForRediscovery()

    test <@ cts1.IsCancellationRequested @>
    test <@ cts2.IsCancellationRequested @>

[<Fact(Timeout = 15000)>]
let ``CancelPreviousCheck links to caller token`` () =
    let pipeline = CheckPipeline(nullChecker)
    let callerCts = new CancellationTokenSource()

    let fileCts =
        pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/Linked.fs", callerCts.Token)

    callerCts.Cancel()
    test <@ fileCts.IsCancellationRequested @>

[<Fact(Timeout = 15000)>]
let ``CheckFile assigns increasing version numbers`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-version-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let sourceFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(sourceFile, "module Lib\nlet x = 42\n")
        let absSource = Path.GetFullPath(sourceFile)

        let options, _ =
            checker.GetProjectOptionsFromScript(absSource, SourceText.ofString (File.ReadAllText absSource))
            |> Async.RunSynchronously

        pipeline.RegisterProject("/tmp/Version.fsproj", options)

        let result1 =
            pipeline.CheckFile(AbsFilePath.create absSource) |> Async.RunSynchronously

        let result2 =
            pipeline.CheckFile(AbsFilePath.create absSource) |> Async.RunSynchronously

        test <@ result1.IsSome @>
        test <@ result2.IsSome @>
        test <@ result2.Value.Version > result1.Value.Version @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// --- InvalidateFile coverage (lines 44-54) ---

[<Fact(Timeout = 15000)>]
let ``InvalidateFile with cache backend calls Invalidate for registered file`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    // F7: real on-disk file so makeCacheKeyFast returns Some and Invalidate
    // is actually called.
    let tempDir =
        Path.Combine(Path.GetTempPath(), $"fshw-inv-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tempDir) |> ignore
    let invFile = Path.Combine(tempDir, "Inv.fs")
    File.WriteAllText(invFile, "module Inv")
    let projFile = Path.Combine(tempDir, "Inv.fsproj")
    let options = dummyOptions projFile [ invFile ]

    try
        pipeline.RegisterProject(projFile, options)
        pipeline.InvalidateFile(AbsFilePath.create invFile)
        test <@ cache.InvalidateCalls.Count = 1 @>
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``InvalidateFile skips Invalidate when registered file is unreadable (F7 None branch)`` () =
    // F7: when GetFileHash returns None, makeCacheKeyFast returns None and
    // InvalidateFile must skip the call entirely (no key to invalidate).
    // Exercises the None match arm introduced by the F7 fix.
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    let missing = "/nonexistent/F7-Inv.fs"
    let projFile = "/tmp/F7-Inv.fsproj"
    let options = dummyOptions projFile [ missing ]
    pipeline.RegisterProject(projFile, options)

    pipeline.InvalidateFile(AbsFilePath.create missing)
    test <@ cache.InvalidateCalls.Count = 0 @>

[<Fact(Timeout = 15000)>]
let ``InvalidateFile with cache backend does nothing for unregistered file`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)
    pipeline.InvalidateFile(AbsFilePath.create "/tmp/Unknown.fs")
    test <@ cache.InvalidateCalls.Count = 0 @>

[<Fact(Timeout = 15000)>]
let ``InvalidateFile without cache backend does not throw`` () =
    let pipeline = CheckPipeline(nullChecker)
    // No cache backend — should be a no-op without error
    pipeline.InvalidateFile(AbsFilePath.create "/tmp/NoCacheFile.fs")

// --- cancelAndDispose already-disposed CTS (lines 17-18) ---

[<Fact(Timeout = 15000)>]
let ``CancelPreviousCheck tolerates already-disposed CTS`` () =
    let pipeline = CheckPipeline(nullChecker)
    let cts = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/Disposable.fs")
    // Manually dispose the CTS before the pipeline tries to cancel it
    cts.Dispose()
    // This second call will try to cancel+dispose the first (already disposed) CTS
    let newCts = pipeline.CancelPreviousCheck(AbsFilePath.create "/tmp/Disposable.fs")
    test <@ not newCts.IsCancellationRequested @>

// --- PrepareForRediscovery clears cache backend ---

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears cache backend`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    let options = dummyOptions "/tmp/Cached.fsproj" [ "/tmp/Cached.fs" ]
    pipeline.RegisterProject("/tmp/Cached.fsproj", options)
    pipeline.PrepareForRediscovery()
    test <@ cache.ClearCalls.Value = 1 @>

// --- CheckProject with missing project (line 186) ---

[<Fact(Timeout = 15000)>]
let ``CheckProject returns None for missing project`` () =
    let pipeline = CheckPipeline(nullChecker)
    // Register a different project so the dictionary isn't empty
    let options = dummyOptions "/tmp/Exists.fsproj" [ "/tmp/Exists.fs" ]
    pipeline.RegisterProject("/tmp/Exists.fsproj", options)
    let result = pipeline.CheckProject("/tmp/Missing.fsproj") |> Async.RunSynchronously
    test <@ result = None @>

// --- Cancellation during CheckFile (lines 174-176) ---

[<Fact(Timeout = 15000)>]
let ``CheckFile returns None when cancelled before FCS call`` () =
    let pipeline = CheckPipeline(nullChecker)

    let options = dummyOptions "/tmp/Cancel.fsproj" [ "/tmp/Cancel.fs" ]
    pipeline.RegisterProject("/tmp/Cancel.fsproj", options)

    let cts = new CancellationTokenSource()
    cts.Cancel()

    let result =
        pipeline.CheckFile(AbsFilePath.create "/tmp/Cancel.fs", cts.Token)
        |> Async.RunSynchronously

    test <@ result = None @>

// --- Cancellation during FCS check (fileToken must propagate into CheckFileCore) ---

[<Fact(Timeout = 20000)>]
let ``CheckFile honors a pre-cancelled caller token and returns None`` () =
    // Deterministic version of the in-flight cancellation contract: a caller-supplied
    // cancellation token that is already cancelled when CheckFile is called must
    // produce None without invoking FCS. The previous in-flight variant of this test
    // raced against FCS speed under parallel test load — when FCS finished before
    // the cancel loop fired, the assertion failed spuriously.
    FsHotWatch.Tests.TestHelpers.withTempDir "cancel-precancelled" (fun tmpDir ->
        let checker =
            FSharpChecker.Create(projectCacheSize = 1, keepAssemblyContents = true)

        let pipeline = CheckPipeline(checker)

        let sourceFile = Path.Combine(tmpDir, "Mod.fs")
        File.WriteAllLines(sourceFile, [| "module Mod"; "let x = 1" |])
        let absSource = Path.GetFullPath(sourceFile)

        let options, _ =
            checker.GetProjectOptionsFromScript(absSource, SourceText.ofString (File.ReadAllText absSource))
            |> Async.RunSynchronously

        pipeline.RegisterProject(Path.Combine(tmpDir, "Mod.fsproj"), options)

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let result =
            pipeline.CheckFile(AbsFilePath.create absSource, cts.Token)
            |> Async.RunSynchronously

        test <@ result = None @>)

// The two integration tests that used to live here ("file cache enables fast
// cold-start check", "cached check returns None because partial FCS results are
// unusable by plugins") went away with the on-disk `FileCheckCache` in
// AUTOMATION-98 — read their names again and they were, all along, a description
// of a cache that could not hit. `tryGetCachedFullCheck` below is the surviving
// invariant they were really pinning: a `ParseOnly` entry is a MISS.

// --- tryGetCachedFullCheck pure-logic tests (no disk, no FCS) ---

let private dummyKey suffix =
    { FileHash = ContentHash.create $"file-%s{suffix}"
      ProjectOptionsHash = ContentHash.create $"opts-%s{suffix}" }

let private fullCheckResult path options =
    { File = AbsFilePath.create path
      Source = "module M"
      ParseResults = Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpParseFileResults>
      CheckResults = FullCheck Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
      ProjectOptions = options
      Version = 1L }

let private parseOnlyResult path options =
    { File = AbsFilePath.create path
      Source = "module M"
      ParseResults = Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpParseFileResults>
      CheckResults = ParseOnly
      ProjectOptions = options
      Version = 1L }

[<Fact(Timeout = 15000)>]
let ``tryGetCachedFullCheck returns None when backend is None`` () =
    let result = tryGetCachedFullCheck None (Some(dummyKey "a"))
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``tryGetCachedFullCheck returns None when key is None`` () =
    let cache = InMemoryCache() :> ICheckCacheBackend
    let result = tryGetCachedFullCheck (Some cache) None
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``tryGetCachedFullCheck returns None on cache miss`` () =
    let cache = InMemoryCache() :> ICheckCacheBackend
    let result = tryGetCachedFullCheck (Some cache) (Some(dummyKey "miss"))
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``tryGetCachedFullCheck returns Some on FullCheck hit`` () =
    let cache = InMemoryCache() :> ICheckCacheBackend
    let key = dummyKey "hit"
    let opts = dummyOptions "/tmp/Hit.fsproj" [ "/tmp/Hit.fs" ]
    cache.Set key (fullCheckResult "/tmp/Hit.fs" opts)
    let result = tryGetCachedFullCheck (Some cache) (Some key)
    test <@ result.IsSome @>
    test <@ AbsFilePath.value result.Value.File = "/tmp/Hit.fs" @>

[<Fact(Timeout = 15000)>]
let ``tryGetCachedFullCheck returns None when cached entry is ParseOnly`` () =
    // ParseOnly entries are treated as misses so the file is re-checked.
    let cache = InMemoryCache() :> ICheckCacheBackend
    let key = dummyKey "parseonly"
    let opts = dummyOptions "/tmp/PO.fsproj" [ "/tmp/PO.fs" ]
    cache.Set key (parseOnlyResult "/tmp/PO.fs" opts)
    let result = tryGetCachedFullCheck (Some cache) (Some key)
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``CheckFile short-circuits via cache hit without invoking FCS`` () =
    // Pre-populate the cache with a FullCheck entry. CheckFile should return it
    // without ever calling the (null) checker — proving cache lookup happens
    // before FCS dispatch and disk read.
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    // Use a real on-disk file: F7 makes makeCacheKey return None when the
    // file is unreadable (so the cache lookup is bypassed). To test the
    // cache-hit path we need a real file the provider can hash.
    let tempDir =
        Path.Combine(Path.GetTempPath(), $"fshw-cachehit-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tempDir) |> ignore
    let hotFile = Path.Combine(tempDir, "Hot.fs")
    File.WriteAllText(hotFile, "module Hot")
    let projFile = Path.Combine(tempDir, "Hot.fsproj")
    let opts = dummyOptions projFile [ hotFile ]
    pipeline.RegisterProject(projFile, opts)

    try
        // Compute the same key the pipeline would use, then seed the cache.
        let key =
            makeCacheKey (TimestampCacheKeyProvider() :> ICacheKeyProvider) hotFile opts
            |> Option.defaultWith (fun () -> failwith "expected Some CacheKey for real file")

        let seeded = fullCheckResult hotFile opts
        (cache :> ICheckCacheBackend).Set key seeded

        let result =
            pipeline.CheckFile(AbsFilePath.create hotFile) |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ AbsFilePath.value result.Value.File = hotFile @>
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``InvalidateFile removes cached entry so next CheckFile would re-check`` () =
    let cache = InMemoryCache()
    let pipeline = CheckPipeline(nullChecker, cacheBackend = cache)

    // F7: real file on disk so makeCacheKey returns Some.
    let tempDir =
        Path.Combine(Path.GetTempPath(), $"fshw-invalidate-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tempDir) |> ignore
    let invFile = Path.Combine(tempDir, "Inv2.fs")
    File.WriteAllText(invFile, "module Inv2")
    let projFile = Path.Combine(tempDir, "Inv2.fsproj")
    let opts = dummyOptions projFile [ invFile ]
    pipeline.RegisterProject(projFile, opts)

    try
        let key =
            makeCacheKey (TimestampCacheKeyProvider() :> ICacheKeyProvider) invFile opts
            |> Option.defaultWith (fun () -> failwith "expected Some CacheKey for real file")

        (cache :> ICheckCacheBackend).Set key (fullCheckResult invFile opts)

        test
            <@
                tryGetCachedFullCheck (Some(cache :> ICheckCacheBackend)) (Some key)
                |> Option.isSome
            @>

        pipeline.InvalidateFile(AbsFilePath.create invFile)
        test <@ tryGetCachedFullCheck (Some(cache :> ICheckCacheBackend)) (Some key) = None @>
        test <@ cache.InvalidateCalls.Count = 1 @>
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()
