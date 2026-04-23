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
open FsHotWatch.FileTaskCache

let private fixedTime = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Helper to construct a CompositeKey with a file.
let private ck plugin file : CompositeKey = { Plugin = plugin; File = Some file }

/// Helper to construct a CompositeKey without a file.
let private ckPlugin plugin : CompositeKey = { Plugin = plugin; File = None }

let private hash (s: string) = ContentHash.create s

let private makeResult (cacheKey: string) =
    { CacheKey = hash cacheKey
      Errors = []
      Status = Completed(at = fixedTime)
      EmittedEvents = [] }

[<Fact(Timeout = 5000)>]
let ``TryGet returns None for unknown key`` () =
    let cache = InMemoryTaskCache()
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash1")
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``Set then TryGet roundtrip`` () =
    let cache = InMemoryTaskCache()
    let expected = makeResult "hash1"
    cache.Set(ck "build" "Foo.fs", hash "hash1", expected)
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash1")
    test <@ result = Some expected @>

[<Fact(Timeout = 5000)>]
let ``TryGet returns None when cacheKey does not match`` () =
    let cache = InMemoryTaskCache()
    let entry = makeResult "hash1"
    cache.Set(ck "build" "Foo.fs", hash "hash1", entry)
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash2")
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``Clear removes all entries`` () =
    let cache = InMemoryTaskCache()
    cache.Set(ck "build" "Foo.fs", hash "h1", makeResult "h1")
    cache.Set(ck "lint" "Bar.fs", hash "h2", makeResult "h2")
    cache.Clear()
    test <@ cache.TryGet(ck "build" "Foo.fs", hash "h1") = None @>
    test <@ cache.TryGet(ck "lint" "Bar.fs", hash "h2") = None @>

[<Fact(Timeout = 5000)>]
let ``ClearPlugin removes only that plugin's entries`` () =
    let cache = InMemoryTaskCache()
    let lintResult = makeResult "h3"
    cache.Set(ck "build" "Foo.fs", hash "h1", makeResult "h1")
    cache.Set(ck "build" "Bar.fs", hash "h2", makeResult "h2")
    cache.Set(ck "lint" "Foo.fs", hash "h3", lintResult)
    cache.Set(ckPlugin "build", hash "h4", makeResult "h4")
    cache.ClearPlugin("build")
    test <@ cache.TryGet(ck "build" "Foo.fs", hash "h1") = None @>
    test <@ cache.TryGet(ck "build" "Bar.fs", hash "h2") = None @>
    test <@ cache.TryGet(ckPlugin "build", hash "h4") = None @>
    test <@ cache.TryGet(ck "lint" "Foo.fs", hash "h3") = Some lintResult @>

[<Fact(Timeout = 5000)>]
let ``ClearFile removes entries matching the file`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h3"
    cache.Set(ck "build" "Foo.fs", hash "h1", makeResult "h1")
    cache.Set(ck "lint" "Foo.fs", hash "h2", makeResult "h2")
    cache.Set(ck "build" "Bar.fs", hash "h3", barResult)
    cache.ClearFile("Foo.fs")
    test <@ cache.TryGet(ck "build" "Foo.fs", hash "h1") = None @>
    test <@ cache.TryGet(ck "lint" "Foo.fs", hash "h2") = None @>
    test <@ cache.TryGet(ck "build" "Bar.fs", hash "h3") = Some barResult @>

[<Fact(Timeout = 5000)>]
let ``ClearPluginFile removes specific entry`` () =
    let cache = InMemoryTaskCache()
    let barResult = makeResult "h2"
    let lintResult = makeResult "h3"
    cache.Set(ck "build" "Foo.fs", hash "h1", makeResult "h1")
    cache.Set(ck "build" "Bar.fs", hash "h2", barResult)
    cache.Set(ck "lint" "Foo.fs", hash "h3", lintResult)
    cache.ClearPluginFile("build", "Foo.fs")
    test <@ cache.TryGet(ck "build" "Foo.fs", hash "h1") = None @>
    test <@ cache.TryGet(ck "build" "Bar.fs", hash "h2") = Some barResult @>
    test <@ cache.TryGet(ck "lint" "Foo.fs", hash "h3") = Some lintResult @>

[<Fact(Timeout = 5000)>]
let ``defaultCacheKey returns commit_id for FileChecked`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 5000)>]
let ``defaultCacheKey returns commit_id for FileChanged`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 5000)>]
let ``defaultCacheKey returns commit_id for BuildCompleted`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 5000)>]
let ``defaultCacheKey returns None when jj unavailable`` () =
    let getCommitId () = None
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``defaultCacheKey returns None for Custom events`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<string> = Custom "hello"
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``ITaskCache interface methods dispatch to implementation`` () =
    let cache = InMemoryTaskCache() :> ITaskCache
    let entry = makeResult "h1"
    cache.Set (ck "build" "Foo.fs") (hash "h1") entry
    cache.Set (ck "build" "Bar.fs") (hash "h2") (makeResult "h2")
    cache.Set (ck "lint" "Foo.fs") (hash "h3") (makeResult "h3")

    test <@ cache.TryGet (ck "build" "Foo.fs") (hash "h1") = Some entry @>

    cache.ClearPluginFile "build" "Foo.fs"
    test <@ cache.TryGet (ck "build" "Foo.fs") (hash "h1") = None @>

    cache.ClearFile "Bar.fs"
    test <@ cache.TryGet (ck "build" "Bar.fs") (hash "h2") = None @>

    cache.ClearPlugin "lint"
    test <@ cache.TryGet (ck "lint" "Foo.fs") (hash "h3") = None @>

    cache.Set (ck "a" "F.fs") (hash "k") (makeResult "k")
    cache.Clear()
    test <@ cache.TryGet (ck "a" "F.fs") (hash "k") = None @>

[<Fact(Timeout = 5000)>]
let ``saltedCacheKey appends non-empty salt to commit`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = Some(hash "abc123:salty") @>

[<Fact(Timeout = 5000)>]
let ``saltedCacheKey omits separator for empty salt`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = saltedCacheKey (fun _ -> "") getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 5000)>]
let ``saltedCacheKey returns None for Custom events regardless of salt`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<string> = Custom "hello"
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``saltedCacheKey returns None when commit unavailable`` () =
    let getCommitId () = None
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``optionalSaltedCacheKey returns None when getCommitId is None`` () =
    let result: (PluginEvent<unit> -> ContentHash option) option =
        optionalSaltedCacheKey (fun _ -> "x") None

    test <@ Option.isNone result @>

[<Fact(Timeout = 5000)>]
let ``optionalSaltedCacheKey wraps getSalt when getCommitId is Some`` () =
    let getCommitId = Some(fun () -> Some "abc123")

    let keyFn =
        optionalSaltedCacheKey (fun _ -> "salty") getCommitId
        |> Option.defaultWith (fun () -> failwith "expected Some")

    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    test <@ keyFn event = Some(hash "abc123:salty") @>

// --- Integration tests: cache intercept in registerHandler ---

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private dummyFileCheckResult file : FileCheckResult =
    { File = file
      Source = ""
      ParseResults = Unchecked.defaultof<_>
      CheckResults = ParseOnly
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

[<Fact(Timeout = 5000)>]
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
        { CacheKey = hash "commit-abc"
          Errors = cachedErrors
          Status = Completed(at = fixedTime)
          EmittedEvents = [] }

    cache.Set(ck "test-plugin" "/src/A.fs", hash "commit-abc", cachedResult)

    let mutable updateCallCount = 0

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "test-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = Some(fun _ -> Some(hash "commit-abc"))
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChecked(dummyFileCheckResult "/src/A.fs")

    // Wait for the agent to process the event
    waitUntil (fun () -> host.GetStatus("test-plugin") <> Some Idle) 5000

    // Update should NOT have been called — cache hit
    test <@ updateCallCount = 0 @>

    // Errors should be replayed into the ledger
    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

    test
        <@
            host.GetErrors()
            |> Map.toList
            |> List.sumBy (fun (_, entries) -> entries.Length) = 1
        @>

[<Fact(Timeout = 5000)>]
let ``plugin stores result on cache miss then hits on second event`` () =
    let cache = InMemoryTaskCache()
    let mutable updateCallCount = 0

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "counter-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = Some(fun _ -> Some(hash "commit-xyz"))
          Teardown = None }

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

[<Fact(Timeout = 5000)>]
let ``plugin runs Update when cache key changes`` () =
    let cache = InMemoryTaskCache()
    let mutable updateCallCount = 0
    let mutable currentCommit = "commit-1"

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "key-change-plugin"
          Init = ()
          Update =
            fun ctx state _event ->
                async {
                    updateCallCount <- updateCallCount + 1
                    ctx.ReportStatus(Completed(at = DateTime.UtcNow))
                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = Some(fun _ -> Some(hash currentCommit))
          Teardown = None }

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

// --- FileTaskCache tests ---

[<Fact(Timeout = 5000)>]
let ``FileTaskCache persists and retrieves across instances`` () =
    withTempDir "ftc-persist" (fun tmpDir ->
        let cache1 = FileTaskCache(tmpDir)

        let result =
            { CacheKey = hash "abc"
              Errors = [ "/src/A.fs", [ errorEntry "warn" DiagnosticSeverity.Warning ] ]
              Status = Completed(at = fixedTime)
              EmittedEvents = [] }

        (cache1 :> ITaskCache).Set (ck "lint" "/src/A.fs") (hash "abc") result

        // New instance, same directory
        let cache2 = FileTaskCache(tmpDir)
        let retrieved = (cache2 :> ITaskCache).TryGet (ck "lint" "/src/A.fs") (hash "abc")
        test <@ retrieved.IsSome @>
        test <@ retrieved.Value.Errors.Length = 1 @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache clear removes all files`` () =
    withTempDir "ftc-clear" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)

        let result =
            { CacheKey = hash "abc"
              Errors = []
              Status = Completed(at = fixedTime)
              EmittedEvents = [] }

        (cache :> ITaskCache).Set (ckPlugin "build") (hash "abc") result
        (cache :> ITaskCache).Clear()
        test <@ (cache :> ITaskCache).TryGet (ckPlugin "build") (hash "abc") |> Option.isNone @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache roundtrips all PluginStatus variants`` () =
    withTempDir "ftc-status" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let statuses =
            [ Idle
              Running(since = fixedTime)
              Completed(at = fixedTime)
              Failed("boom", at = fixedTime) ]

        for i, status in statuses |> List.indexed do
            let key = ck "plugin" $"%d{i}"

            let result =
                { CacheKey = hash "k"
                  Errors = []
                  Status = status
                  EmittedEvents = [] }

            c.Set key (hash "k") result

        // Read back from a new instance
        let cache2 = FileTaskCache(tmpDir)
        let c2 = cache2 :> ITaskCache
        test <@ (c2.TryGet (ck "plugin" "0") (hash "k")).Value.Status = Idle @>
        test <@ (c2.TryGet (ck "plugin" "1") (hash "k")).Value.Status = Running(since = fixedTime) @>
        test <@ (c2.TryGet (ck "plugin" "2") (hash "k")).Value.Status = Completed(at = fixedTime) @>
        test <@ (c2.TryGet (ck "plugin" "3") (hash "k")).Value.Status = Failed("boom", at = fixedTime) @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache roundtrips cached events`` () =
    withTempDir "ftc-events" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = Completed(at = fixedTime)
              EmittedEvents =
                [ CachedBuildCompleted BuildSucceeded
                  CachedBuildCompleted(BuildFailed [ "err1"; "err2" ])
                  CachedTestRunCompleted
                      { RunId = System.Guid.NewGuid()
                        TotalElapsed = System.TimeSpan.FromSeconds(3.5)
                        Outcome = Normal
                        Results = Map.ofList [ "proj1", TestsPassed "ok"; "proj2", TestsFailed "fail" ] } ] }

        c.Set (ck "build" "X.fs") (hash "k") result

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet (ck "build" "X.fs") (hash "k")
        test <@ r.IsSome @>
        test <@ r.Value.EmittedEvents.Length = 3 @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache roundtrips error entries with detail`` () =
    withTempDir "ftc-detail" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let entry: ErrorEntry =
            { Message = "test msg"
              Severity = DiagnosticSeverity.Error
              Line = 42
              Column = 7
              Detail = Some "full detail" }

        let result =
            { CacheKey = hash "k"
              Errors = [ "/src/X.fs", [ entry ] ]
              Status = Completed(at = fixedTime)
              EmittedEvents = [] }

        c.Set (ck "lint" "/src/X.fs") (hash "k") result

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet (ck "lint" "/src/X.fs") (hash "k")
        test <@ r.IsSome @>
        let e = r.Value.Errors.[0] |> snd |> List.head
        test <@ e.Message = "test msg" @>
        test <@ e.Severity = DiagnosticSeverity.Error @>
        test <@ e.Line = 42 @>
        test <@ e.Column = 7 @>
        test <@ e.Detail = Some "full detail" @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache ClearPlugin removes only matching files`` () =
    withTempDir "ftc-clearplugin" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        c.Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        c.Set (ck "lint" "Foo.fs") (hash "h2") (makeResult "h2")
        c.ClearPlugin "build"
        test <@ c.TryGet (ck "build" "Foo.fs") (hash "h1") |> Option.isNone @>
        test <@ c.TryGet (ck "lint" "Foo.fs") (hash "h2") |> Option.isSome @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache ClearFile removes entries matching the file`` () =
    withTempDir "ftc-clearfile" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        c.Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        c.Set (ck "lint" "Foo.fs") (hash "h2") (makeResult "h2")
        c.Set (ck "build" "Bar.fs") (hash "h3") (makeResult "h3")
        c.ClearFile "Foo.fs"
        test <@ c.TryGet (ck "build" "Foo.fs") (hash "h1") |> Option.isNone @>
        test <@ c.TryGet (ck "lint" "Foo.fs") (hash "h2") |> Option.isNone @>
        test <@ c.TryGet (ck "build" "Bar.fs") (hash "h3") |> Option.isSome @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache ClearPluginFile removes specific entry`` () =
    withTempDir "ftc-clearpf" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        c.Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        c.Set (ck "build" "Bar.fs") (hash "h2") (makeResult "h2")
        c.ClearPluginFile "build" "Foo.fs"
        test <@ c.TryGet (ck "build" "Foo.fs") (hash "h1") |> Option.isNone @>
        test <@ c.TryGet (ck "build" "Bar.fs") (hash "h2") |> Option.isSome @>)
