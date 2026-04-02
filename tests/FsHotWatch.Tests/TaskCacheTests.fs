module FsHotWatch.Tests.TaskCacheTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TaskCache
open FsHotWatch.Tests.TestHelpers

let private fixedTime = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let private makeResult cacheKey =
    { CacheKey = cacheKey
      Errors = []
      Status = Completed(at = fixedTime)
      EmittedEvents = [] }

[<Fact>]
let ``TryGet returns None for unknown key`` () =
    let cache = InMemoryTaskCache()
    let result = cache.TryGet("build--Foo.fs", "hash1")
    test <@ result = None @>

[<Fact>]
let ``Set then TryGet roundtrip`` () =
    let cache = InMemoryTaskCache()
    let expected = makeResult "hash1"
    cache.Set("build--Foo.fs", "hash1", expected)
    let result = cache.TryGet("build--Foo.fs", "hash1")
    test <@ result = Some expected @>

[<Fact>]
let ``TryGet returns None when cacheKey does not match`` () =
    let cache = InMemoryTaskCache()
    let entry = makeResult "hash1"
    cache.Set("build--Foo.fs", "hash1", entry)
    let result = cache.TryGet("build--Foo.fs", "hash2")
    test <@ result = None @>

[<Fact>]
let ``Clear removes all entries`` () =
    let cache = InMemoryTaskCache()
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("lint--Bar.fs", "h2", makeResult "h2")
    cache.Clear()
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("lint--Bar.fs", "h2") = None @>

[<Fact>]
let ``ClearPlugin removes only that plugin's entries`` () =
    let cache = InMemoryTaskCache()
    let lintResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("build--Bar.fs", "h2", makeResult "h2")
    cache.Set("lint--Foo.fs", "h3", lintResult)
    cache.Set("build", "h4", makeResult "h4")
    cache.ClearPlugin("build")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h2") = None @>
    test <@ cache.TryGet("build", "h4") = None @>
    test <@ cache.TryGet("lint--Foo.fs", "h3") = Some lintResult @>

[<Fact>]
let ``ClearFile removes entries matching the file`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("lint--Foo.fs", "h2", makeResult "h2")
    cache.Set("build--Bar.fs", "h3", barResult)
    cache.ClearFile("Foo.fs")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("lint--Foo.fs", "h2") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h3") = Some barResult @>

[<Fact>]
let ``ClearPluginFile removes specific entry`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h2"
    let lintResult = makeResult "h3"
    cache.Set("build--Foo.fs", "h1", makeResult "h1")
    cache.Set("build--Bar.fs", "h2", barResult)
    cache.Set("lint--Foo.fs", "h3", lintResult)
    cache.ClearPluginFile("build", "Foo.fs")
    test <@ cache.TryGet("build--Foo.fs", "h1") = None @>
    test <@ cache.TryGet("build--Bar.fs", "h2") = Some barResult @>
    test <@ cache.TryGet("lint--Foo.fs", "h3") = Some lintResult @>

[<Fact>]
let ``defaultCacheKey returns commit_id for FileChecked`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = defaultCacheKey getCommitId event
    test <@ result = Some "abc123" @>

[<Fact>]
let ``defaultCacheKey returns commit_id for FileChanged`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = Some "abc123" @>

[<Fact>]
let ``defaultCacheKey returns commit_id for BuildCompleted`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    let result = defaultCacheKey getCommitId event
    test <@ result = Some "abc123" @>

[<Fact>]
let ``defaultCacheKey returns None when jj unavailable`` () =
    let getCommitId () = None
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

[<Fact>]
let ``defaultCacheKey returns None for Custom events`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<string> = Custom "hello"
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

// --- Integration tests: cache intercept in registerHandler ---

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private dummyFileCheckResult file : FileCheckResult =
    { File = file
      Source = ""
      ParseResults = Unchecked.defaultof<_>
      CheckResults = None
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

[<Fact>]
let ``plugin skips Update on cache hit and replays errors`` () =
    let cache = InMemoryTaskCache()

    // Pre-populate cache with a result
    let cachedErrors =
        [ ("/src/A.fs",
           [ { Message = "cached warning"
               Severity = DiagnosticSeverity.Warning
               Line = 1
               Column = 0
               Detail = None } ]) ]

    let cachedResult: TaskCacheResult =
        { CacheKey = "commit-abc"
          Errors = cachedErrors
          Status = Completed(at = fixedTime)
          EmittedEvents = [] }

    cache.Set("test-plugin--/src/A.fs", "commit-abc", cachedResult)

    let mutable updateCallCount = 0

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = "test-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                FileChecked = true }
          CacheKey = Some(fun _ -> Some "commit-abc") }

    host.RegisterHandler(handler)
    host.EmitFileChecked(dummyFileCheckResult "/src/A.fs")

    // Wait for the agent to process the event
    waitUntil (fun () -> host.GetStatus("test-plugin") <> Some Idle) 5000

    // Update should NOT have been called — cache hit
    test <@ updateCallCount = 0 @>

    // Errors should be replayed into the ledger
    test <@ host.HasErrors() @>
    test <@ host.ErrorCount() = 1 @>

[<Fact>]
let ``plugin stores result on cache miss then hits on second event`` () =
    let cache = InMemoryTaskCache()
    let mutable updateCallCount = 0

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = "counter-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                FileChecked = true }
          CacheKey = Some(fun _ -> Some "commit-xyz") }

    host.RegisterHandler(handler)

    // First event: cache miss, runs Update
    host.EmitFileChecked(dummyFileCheckResult "/src/B.fs")
    waitForTerminalStatus host "counter-plugin" 5000
    test <@ updateCallCount = 1 @>

    // Second event with same cache key: cache hit, skips Update
    host.EmitFileChecked(dummyFileCheckResult "/src/B.fs")
    // Wait for the agent to process — status will be set by replay
    Thread.Sleep(200)
    test <@ updateCallCount = 1 @>

[<Fact>]
let ``plugin runs Update when cache key changes`` () =
    let cache = InMemoryTaskCache()
    let mutable updateCallCount = 0
    let mutable currentCommit = "commit-1"

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = "key-change-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                FileChecked = true }
          CacheKey = Some(fun _ -> Some currentCommit) }

    host.RegisterHandler(handler)

    // First event: cache miss
    host.EmitFileChecked(dummyFileCheckResult "/src/C.fs")
    waitForTerminalStatus host "key-change-plugin" 5000
    test <@ updateCallCount = 1 @>

    // Change the commit — second event should miss cache
    currentCommit <- "commit-2"
    host.EmitFileChecked(dummyFileCheckResult "/src/C.fs")
    waitUntil (fun () -> updateCallCount = 2) 5000
    test <@ updateCallCount = 2 @>
