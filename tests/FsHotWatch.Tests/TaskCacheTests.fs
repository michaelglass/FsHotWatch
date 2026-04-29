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

// --- §2a: merkle cache key tests ---

[<Fact(Timeout = 5000)>]
let ``merkleCacheKey is stable for identical inputs`` () =
    let a = merkleCacheKey [ "tool", "FSharpLint-1.0"; "src", "let x = 1" ]
    let b = merkleCacheKey [ "tool", "FSharpLint-1.0"; "src", "let x = 1" ]
    test <@ a = b @>

[<Fact(Timeout = 5000)>]
let ``merkleCacheKey changes when any input value changes`` () =
    let baseline = merkleCacheKey [ "tool", "v1"; "src", "let x = 1" ]
    let editedSrc = merkleCacheKey [ "tool", "v1"; "src", "let x = 2" ]
    let editedTool = merkleCacheKey [ "tool", "v2"; "src", "let x = 1" ]
    test <@ baseline <> editedSrc @>
    test <@ baseline <> editedTool @>

[<Fact(Timeout = 5000)>]
let ``merkleCacheKey is order-independent on labels`` () =
    let a = merkleCacheKey [ "tool", "v1"; "src", "x" ]
    let b = merkleCacheKey [ "src", "x"; "tool", "v1" ]
    test <@ a = b @>

[<Fact(Timeout = 5000)>]
let ``merkleCacheKey distinguishes "ab","" from "a","b"`` () =
    // Guard against naive concatenation collision.
    let a = merkleCacheKey [ "x", "ab"; "y", "" ]
    let b = merkleCacheKey [ "x", "a"; "y", "b" ]
    test <@ a <> b @>

[<Fact(Timeout = 30000); Trait("Category", "Benchmark")>]
let ``BENCH merkleCacheKey on representative .fs file`` () =
    // §2a measurement B: per-FileChecked hashing cost. Repo avg .fs size ~12KB.
    // Use the longest .fs file we can find as a worst-case proxy.
    let testSrc =
        let typical =
            String.replicate 240 "let aReasonablyLongIdentifier = someValue + otherValue\n"

        typical // ~12KB

    let inputs =
        [ "plugin-version", "lint-merkle-v1"
          "tool", "1.2.3.4"
          "config", "abc123def456"
          "file", "/Users/me/repo/src/SomeModule/SomeFile.fs"
          "source", testSrc ]

    let warmup = 100
    let iterations = 1000

    for _ in 1..warmup do
        merkleCacheKey inputs |> ignore

    let sw = System.Diagnostics.Stopwatch.StartNew()

    for _ in 1..iterations do
        merkleCacheKey inputs |> ignore

    sw.Stop()
    let perCallUs = sw.Elapsed.TotalMicroseconds / float iterations

    // Print to stdout via xunit's facility — using printfn since Trait gives
    // us a way to filter this test out of normal runs if needed.
    printfn
        "merkleCacheKey on %d-byte source: %.1f µs/call (%d iters in %d ms)"
        testSrc.Length
        perCallUs
        iterations
        sw.ElapsedMilliseconds

    // Soft assertion: < 1 ms per call. If this fires, predicted downside #1
    // (hashing cost per tick) is real.
    test <@ perCallUs < 1000.0 @>

[<Fact(Timeout = 5000)>]
let ``LintPlugin cache key is stable across runs for same file content`` () =
    // §2a hypothesis: editing Foo.fs and reverting it should hit the cache.
    // The cache key for a FileChecked event should depend on file content,
    // not on jj commit_id (which would change on every save).
    let handler = FsHotWatch.Lint.LintPlugin.create None None None

    let mkResult (file: string) (source: string) : FileCheckResult =
        { File = file
          Source = source
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    match handler.CacheKey with
    | None -> failwith "expected LintPlugin to provide a CacheKey"
    | Some keyFn ->
        let a = keyFn (FileChecked(mkResult "/src/Foo.fs" "let x = 1"))
        let b = keyFn (FileChecked(mkResult "/src/Foo.fs" "let x = 1"))
        let edited = keyFn (FileChecked(mkResult "/src/Foo.fs" "let x = 2"))
        test <@ a = b @>
        test <@ a <> edited @>

[<Fact(Timeout = 5000)>]
let ``LintPlugin cache key is None for non-FileChecked events`` () =
    let handler = FsHotWatch.Lint.LintPlugin.create None None None

    match handler.CacheKey with
    | None -> failwith "expected LintPlugin to provide a CacheKey"
    | Some keyFn ->
        let result = keyFn (FileChanged(SourceChanged [ "/src/Foo.fs" ]))
        test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``LintPlugin cache key reflects config file content`` () =
    // §2a: editing the lint config should invalidate cached lint results.
    withTempDir "lint-config" (fun tmpDir ->
        let configPath = System.IO.Path.Combine(tmpDir, "fsharplint.json")
        System.IO.File.WriteAllText(configPath, "{\"rules\":\"v1\"}")
        let handler1 = FsHotWatch.Lint.LintPlugin.create (Some configPath) None None

        let mkResult source : FileCheckResult =
            { File = "/src/Foo.fs"
              Source = source
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        match handler1.CacheKey with
        | None -> failwith "expected CacheKey"
        | Some k1 ->
            let key1 = k1 (FileChecked(mkResult "let x = 1"))
            // Edit config, rebuild handler.
            System.IO.File.WriteAllText(configPath, "{\"rules\":\"v2\"}")
            let handler2 = FsHotWatch.Lint.LintPlugin.create (Some configPath) None None

            match handler2.CacheKey with
            | None -> failwith "expected CacheKey"
            | Some k2 ->
                let key2 = k2 (FileChecked(mkResult "let x = 1"))
                test <@ key1 <> key2 @>)

[<Fact(Timeout = 5000)>]
let ``§1: LintPlugin cache key reflects FCS check signature for ParseOnly vs FullCheck`` () =
    // §1 oracle: the cache key must distinguish ParseOnly from FullCheck even
    // when source bytes are identical — they may produce different lint
    // results because Lint inspects type info from check results when available.
    let handler = FsHotWatch.Lint.LintPlugin.create None None None

    let mkResult (file: string) (source: string) (state: FileCheckState) : FileCheckResult =
        { File = file
          Source = source
          ParseResults = Unchecked.defaultof<_>
          CheckResults = state
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    match handler.CacheKey with
    | None -> failwith "expected CacheKey"
    | Some keyFn ->
        let parseOnly = keyFn (FileChecked(mkResult "/src/X.fs" "let x = 1" ParseOnly))

        let fullCheckNull =
            keyFn (FileChecked(mkResult "/src/X.fs" "let x = 1" (FullCheck(Unchecked.defaultof<_>))))

        test <@ parseOnly.IsSome @>
        test <@ fullCheckNull.IsSome @>
        test <@ parseOnly <> fullCheckNull @>

[<Fact(Timeout = 5000)>]
let ``LintPlugin cache key uses missing-config marker when config path doesn't exist`` () =
    // Covers the `Some path` branch where the file is not on disk — should
    // produce a stable key (no exception) distinct from the `None` case.
    let h1 =
        FsHotWatch.Lint.LintPlugin.create (Some "/nonexistent/fsharplint.json") None None

    let h2 = FsHotWatch.Lint.LintPlugin.create None None None

    let mkResult () : FileCheckResult =
        { File = "/src/Foo.fs"
          Source = "let x = 1"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    let evt = FileChecked(mkResult ())

    let k1 = h1.CacheKey |> Option.bind (fun f -> f evt)
    let k2 = h2.CacheKey |> Option.bind (fun f -> f evt)
    test <@ k1.IsSome @>
    test <@ k1 <> k2 @>

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

// Cache tests use Source = "" and null ParseResults — the cache intercept
// doesn't read either, so override the shared helper rather than build the
// record by hand.
let private dummyFileCheckResult file =
    { fakeFileCheckResult file with
        Source = ""
        ParseResults = Unchecked.defaultof<_> }

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
                        Results =
                          Map.ofList
                              [ "proj1", TestsPassed("ok", false, TimeSpan.Zero)
                                "proj2", TestsFailed("fail", false, TimeSpan.Zero) ]
                        RanFullSuite = true } ] }

        c.Set (ck "build" "X.fs") (hash "k") result

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet (ck "build" "X.fs") (hash "k")
        test <@ r.IsSome @>
        test <@ r.Value.EmittedEvents.Length = 3 @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache roundtrips wasFiltered=true and RanFullSuite=false`` () =
    withTempDir "ftc-filtered" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let runId = System.Guid.NewGuid()

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = Completed(at = fixedTime)
              EmittedEvents =
                [ CachedTestRunCompleted
                      { RunId = runId
                        TotalElapsed = System.TimeSpan.FromSeconds(1.0)
                        Outcome = Normal
                        Results =
                          Map.ofList
                              [ "p1", TestsPassed("ok", true, TimeSpan.Zero)
                                "p2", TestsFailed("bad", true, TimeSpan.Zero) ]
                        RanFullSuite = false } ] }

        c.Set (ck "test-prune" "X.fs") (hash "k") result

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet (ck "test-prune" "X.fs") (hash "k")
        test <@ r.IsSome @>

        let evt =
            r.Value.EmittedEvents
            |> List.tryPick (function
                | CachedTestRunCompleted e -> Some e
                | _ -> None)

        test <@ evt.IsSome @>
        test <@ not evt.Value.RanFullSuite @>
        let p1 = evt.Value.Results.["p1"]
        test <@ TestResult.wasFiltered p1 @>
        test <@ TestResult.isPassed p1 @>)

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

// --- §2b: atomic write tests ---

[<Fact(Timeout = 5000)>]
let ``FileTaskCache.Set leaves no .tmp files behind`` () =
    withTempDir "ftc-atomic-clean" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        (cache :> ITaskCache).Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        let tmps = System.IO.Directory.EnumerateFiles(tmpDir, "*.tmp") |> Seq.toList
        test <@ List.isEmpty tmps @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache constructor sweeps orphan .tmp files`` () =
    withTempDir "ftc-atomic-sweep" (fun tmpDir ->
        // Simulate a prior crash mid-write by dropping an orphan .tmp file.
        let orphan = System.IO.Path.Combine(tmpDir, "build--Foo.fs@deadbeef.json.tmp")
        System.IO.File.WriteAllText(orphan, "{ partial JSON")
        // Constructor should sweep it.
        let _cache = FileTaskCache(tmpDir)
        test <@ not (System.IO.File.Exists orphan) @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache.Stats reports entry count and total bytes`` () =
    withTempDir "ftc-stats" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        c.Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        c.Set (ck "lint" "Bar.fs") (hash "h2") (makeResult "h2")

        let entryCount = cache.Stats.EntryCount
        let sizeBytes = cache.Stats.SizeBytes
        test <@ entryCount = 2 @>
        test <@ sizeBytes > 0L @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache.Stats on empty dir reports zero`` () =
    withTempDir "ftc-stats-empty" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let entryCount = cache.Stats.EntryCount
        let sizeBytes = cache.Stats.SizeBytes
        test <@ entryCount = 0 @>
        test <@ sizeBytes = 0L @>)

[<Fact(Timeout = 5000)>]
let ``FileTaskCache.ParseFailureCount increments on malformed cache file`` () =
    withTempDir "ftc-parse-counter" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let key = ck "lint" "X.fs"
        let cacheKey = hash "k1"
        (cache :> ITaskCache).Set key cacheKey (makeResult "k1")
        let path = System.IO.Directory.EnumerateFiles(tmpDir, "*.json") |> Seq.head
        System.IO.File.WriteAllText(path, "{ not valid json")
        let before = cache.ParseFailureCount
        let result = (cache :> ITaskCache).TryGet key cacheKey
        test <@ result = None @>
        test <@ cache.ParseFailureCount = before + 1 @>)
