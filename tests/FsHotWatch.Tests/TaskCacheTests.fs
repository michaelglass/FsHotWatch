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
      Status = cachedFileDone
      EmittedEvents = [] }

[<Fact(Timeout = 15000)>]
let ``TryGet returns None for unknown key`` () =
    let cache = InMemoryTaskCache()
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash1")
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``Set then TryGet roundtrip`` () =
    let cache = InMemoryTaskCache()
    let expected = makeResult "hash1"
    cache.Set(ck "build" "Foo.fs", hash "hash1", expected)
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash1")
    test <@ result = Some expected @>

[<Fact(Timeout = 15000)>]
let ``TryGet returns None when cacheKey does not match`` () =
    let cache = InMemoryTaskCache()
    let entry = makeResult "hash1"
    cache.Set(ck "build" "Foo.fs", hash "hash1", entry)
    let result = cache.TryGet(ck "build" "Foo.fs", hash "hash2")
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``Clear removes all entries`` () =
    let cache = InMemoryTaskCache()
    cache.Set(ck "build" "Foo.fs", hash "h1", makeResult "h1")
    cache.Set(ck "lint" "Bar.fs", hash "h2", makeResult "h2")
    cache.Clear()
    test <@ cache.TryGet(ck "build" "Foo.fs", hash "h1") = None @>
    test <@ cache.TryGet(ck "lint" "Bar.fs", hash "h2") = None @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``defaultCacheKey returns commit_id for FileChecked`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 15000)>]
let ``defaultCacheKey returns commit_id for FileChanged`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 15000)>]
let ``defaultCacheKey returns commit_id for BuildCompleted`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    let result = defaultCacheKey getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 15000)>]
let ``defaultCacheKey returns None when jj unavailable`` () =
    let getCommitId () = None
    let event: PluginEvent<unit> = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``defaultCacheKey returns None for Custom events`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<string> = Custom "hello"
    let result = defaultCacheKey getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``saltedCacheKey appends non-empty salt to commit`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = BuildCompleted BuildSucceeded
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = Some(hash "abc123:salty") @>

[<Fact(Timeout = 15000)>]
let ``saltedCacheKey omits separator for empty salt`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = saltedCacheKey (fun _ -> "") getCommitId event
    test <@ result = Some(hash "abc123") @>

[<Fact(Timeout = 15000)>]
let ``saltedCacheKey returns None for Custom events regardless of salt`` () =
    let getCommitId () = Some "abc123"
    let event: PluginEvent<string> = Custom "hello"
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``saltedCacheKey returns None when commit unavailable`` () =
    let getCommitId () = None
    let event: PluginEvent<unit> = FileChecked(Unchecked.defaultof<FileCheckResult>)
    let result = saltedCacheKey (fun _ -> "salty") getCommitId event
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``optionalSaltedCacheKey returns None when getCommitId is None`` () =
    let result: (PluginEvent<unit> -> ContentHash option) option =
        optionalSaltedCacheKey (fun _ -> "x") None

    test <@ Option.isNone result @>

// --- §2a: merkle cache key tests ---

[<Fact(Timeout = 15000)>]
let ``merkleCacheKey is stable for identical inputs`` () =
    let a = merkleCacheKey [ "tool", "FSharpLint-1.0"; "src", "let x = 1" ]
    let b = merkleCacheKey [ "tool", "FSharpLint-1.0"; "src", "let x = 1" ]
    test <@ a = b @>

[<Fact(Timeout = 15000)>]
let ``merkleCacheKey changes when any input value changes`` () =
    let baseline = merkleCacheKey [ "tool", "v1"; "src", "let x = 1" ]
    let editedSrc = merkleCacheKey [ "tool", "v1"; "src", "let x = 2" ]
    let editedTool = merkleCacheKey [ "tool", "v2"; "src", "let x = 1" ]
    test <@ baseline <> editedSrc @>
    test <@ baseline <> editedTool @>

[<Fact(Timeout = 15000)>]
let ``merkleCacheKey is order-independent on labels`` () =
    let a = merkleCacheKey [ "tool", "v1"; "src", "x" ]
    let b = merkleCacheKey [ "src", "x"; "tool", "v1" ]
    test <@ a = b @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``LintPlugin cache key is stable across runs for same file content`` () =
    // §2a hypothesis: editing Foo.fs and reverting it should hit the cache.
    // The cache key for a FileChecked event should depend on file content,
    // not on jj commit_id (which would change on every save).
    let handler = FsHotWatch.Lint.LintPlugin.create None None None None

    let mkResult (file: string) (source: string) : FileCheckResult =
        { File = AbsFilePath.create file
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

[<Fact(Timeout = 15000)>]
let ``LintPlugin cache key is None for non-FileChecked events`` () =
    let handler = FsHotWatch.Lint.LintPlugin.create None None None None

    match handler.CacheKey with
    | None -> failwith "expected LintPlugin to provide a CacheKey"
    | Some keyFn ->
        let result = keyFn (FileChanged(SourceChanged [ "/src/Foo.fs" ]))
        test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``LintPlugin cache key reflects config file content`` () =
    // §2a: editing the lint config should invalidate cached lint results.
    withTempDir "lint-config" (fun tmpDir ->
        let configPath = System.IO.Path.Combine(tmpDir, "fsharplint.json")
        System.IO.File.WriteAllText(configPath, "{\"rules\":\"v1\"}")
        let handler1 = FsHotWatch.Lint.LintPlugin.create None (Some configPath) None None

        let mkResult source : FileCheckResult =
            { File = AbsFilePath.create "/src/Foo.fs"
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
            let handler2 = FsHotWatch.Lint.LintPlugin.create None (Some configPath) None None

            match handler2.CacheKey with
            | None -> failwith "expected CacheKey"
            | Some k2 ->
                let key2 = k2 (FileChecked(mkResult "let x = 1"))
                test <@ key1 <> key2 @>)

[<Fact(Timeout = 15000)>]
let ``§1: LintPlugin cache key reflects FCS check signature for ParseOnly vs FullCheck`` () =
    // §1 oracle: the cache key must distinguish ParseOnly from FullCheck even
    // when source bytes are identical — they may produce different lint
    // results because Lint inspects type info from check results when available.
    let handler = FsHotWatch.Lint.LintPlugin.create None None None None

    let mkResult (file: string) (source: string) (state: FileCheckState) : FileCheckResult =
        { File = AbsFilePath.create file
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

[<Fact(Timeout = 15000)>]
let ``LintPlugin cache key uses missing-config marker when config path doesn't exist`` () =
    // Covers the `Some path` branch where the file is not on disk — should
    // produce a stable key (no exception) distinct from the `None` case.
    let h1 =
        FsHotWatch.Lint.LintPlugin.create None (Some "/nonexistent/fsharplint.json") None None

    let h2 = FsHotWatch.Lint.LintPlugin.create None None None None

    let mkResult () : FileCheckResult =
        { File = AbsFilePath.create "/src/Foo.fs"
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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
          Status = cachedFileDone
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
                    ctx.ReportStatus(completedAt DateTime.UtcNow)
                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          CacheKey = Some(fun _ -> Some(hash "commit-abc"))
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChecked(dummyFileCheckResult "/src/A.fs")

    // Wait for the agent to process the event
    waitUntil (fun () -> host.GetStatus("test-plugin") <> Some Idle) 12000

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

[<Fact(Timeout = 15000)>]
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
                    ctx.ReportStatus(completedAt DateTime.UtcNow)
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

[<Fact(Timeout = 15000)>]
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
                    ctx.ReportStatus(completedAt DateTime.UtcNow)
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
    waitUntil (fun () -> updateCallCount = 2) 12000
    test <@ updateCallCount = 2 @>

// --- FileTaskCache tests ---

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 5 — the task cache grew without bound.
// ---------------------------------------------------------------------------
//
// Entries are named `{plugin--file}@{contentHash}.json` "so multiple versions
// coexist", but `tryGet` reconstructs the exact path from the key — so only the
// entry matching the CURRENT content is reachable, and every prior one is dead
// weight that nothing ever removed. Each edit to a file added one, forever:
// 3,126 files / 13 MB in a ~1.5-day-old workspace, across ~6 live workspaces,
// while `Stats`/`clearFile`/`clearPlugin` full-scan the directory each call.
//
// RED-BEFORE-GREEN: remove the `pruneSiblingsOf` call from `set` and the file
// count is 3, not 1.

[<Fact(Timeout = 15000)>]
let ``FileTaskCache keeps only the newest entry per plugin+file`` () =
    withTempDir "ftc-prune" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir) :> ITaskCache
        let key = ck "lint" "/src/A.fs"

        let resultFor (h: string) =
            { CacheKey = hash h
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        // Three successive edits to the same file — three content hashes.
        for h in [ "v1"; "v2"; "v3" ] do
            cache.Set key (hash h) (resultFor h)

        let files = System.IO.Directory.GetFiles(tmpDir, "*.json")
        test <@ files.Length = 1 @>

        // The survivor is the NEWEST, and it is readable.
        test <@ (cache.TryGet key (hash "v3")).IsSome @>
        // The superseded ones are gone — which costs nothing, because they were
        // already unreachable: a lookup keyed by old content could never be issued
        // for a file whose content has moved on.
        test <@ (cache.TryGet key (hash "v1")).IsNone @>
        test <@ (cache.TryGet key (hash "v2")).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache pruning never touches a DIFFERENT plugin or file`` () =
    withTempDir "ftc-prune-scope" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir) :> ITaskCache

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        // Same file, different plugin; same plugin, different file; and a
        // plugin-only (file-less) key — none may be collected by a sibling prune.
        cache.Set (ck "lint" "/src/A.fs") (hash "k") { result with CacheKey = hash "k" }
        cache.Set (ck "analyzers" "/src/A.fs") (hash "k") { result with CacheKey = hash "k" }
        cache.Set (ck "lint" "/src/B.fs") (hash "k") { result with CacheKey = hash "k" }
        cache.Set (ckPlugin "build") (hash "k") { result with CacheKey = hash "k" }

        // Now supersede ONE of them.
        cache.Set (ck "lint" "/src/A.fs") (hash "k2") { result with CacheKey = hash "k2" }

        test <@ (cache.TryGet (ck "lint" "/src/A.fs") (hash "k2")).IsSome @>
        test <@ (cache.TryGet (ck "analyzers" "/src/A.fs") (hash "k")).IsSome @>
        test <@ (cache.TryGet (ck "lint" "/src/B.fs") (hash "k")).IsSome @>
        test <@ (cache.TryGet (ckPlugin "build") (hash "k")).IsSome @>
        test <@ System.IO.Directory.GetFiles(tmpDir, "*.json").Length = 4 @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache prune failure never propagates (cache hygiene must not fail a task)`` () =
    // The prune runs immediately AFTER a successful write, so a failure here would
    // otherwise throw away a result the task had already earned. It must not — and
    // this cannot be staged through `Set`, because a directory that refuses a delete
    // also refuses the write that precedes it. So drive the prune directly.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "ftc-prune-fail" (fun tmpDir ->
            let sibling = System.IO.Path.Combine(tmpDir, "lint---src-A.fs@deadbeefcafe.json")
            System.IO.File.WriteAllText(sibling, "{}")

            // Deleting a directory ENTRY needs write permission on the DIRECTORY.
            System.IO.File.SetUnixFileMode(
                tmpDir,
                System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserExecute
            )

            try
                // Must not throw.
                pruneSupersededSiblings [ sibling ] "/some/other/keep.json"
            finally
                System.IO.File.SetUnixFileMode(
                    tmpDir,
                    System.IO.UnixFileMode.UserRead
                    ||| System.IO.UnixFileMode.UserWrite
                    ||| System.IO.UnixFileMode.UserExecute
                )

            // The sibling survived — the delete really did fail, so we proved the
            // swallow, not merely that nothing was there to delete. The next write
            // to this key collects it.
            test <@ System.IO.File.Exists sibling @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache prune keeps collecting after one delete fails`` () =
    // Each delete is independently guarded, so one undeletable sibling cannot shield
    // the rest. (The old shape wrapped the whole loop in a single `try`: the first
    // throw abandoned every sibling after it.) The undeletable one is a DIRECTORY at
    // an entry's path — `File.Delete` refuses it — which needs no permission games.
    withTempDir "ftc-prune-partial" (fun tmpDir ->
        let blocker = System.IO.Path.Combine(tmpDir, "lint---src-A.fs@aaaaaaaaaaaa.json")

        let collectable =
            System.IO.Path.Combine(tmpDir, "lint---src-A.fs@bbbbbbbbbbbb.json")

        System.IO.Directory.CreateDirectory(blocker) |> ignore
        System.IO.File.WriteAllText(collectable, "{}")

        pruneSupersededSiblings [ blocker; collectable ] "/some/other/keep.json"

        test <@ System.IO.Directory.Exists blocker @>
        test <@ not (System.IO.File.Exists collectable) @>)

// ---------------------------------------------------------------------------
// The prune must not SCAN. Its cost cannot be a function of the cache's size.
// ---------------------------------------------------------------------------
//
// `Directory.EnumerateFiles(dir, pattern)` is not a prefix-optimised syscall: it
// readdirs the WHOLE directory and pattern-matches in managed code. A cold scan
// writes ~3 entries per source file (Lint, Analyzers, FormatCheck each carry a
// per-`FileChecked` cache key) into a directory that grows to ~3 entries per file —
// so a prune that scans makes the cold scan QUADRATIC. Measured at ~2.2 ms per scan
// against a 4,500-entry directory: ~10 seconds of pure directory scanning added to a
// cold scan of a 1500-file repo, on exactly the paths (cold scan, `--run-once`, the
// merge gate) that were already timing out.
//
// RED-BEFORE-GREEN: restore the `EnumerateFiles(cacheDir, prefix + "*.json")` prune
// and this reads 200, not 0.

[<Fact(Timeout = 15000)>]
let ``FileTaskCache write path never scans the directory (prune cost is independent of cache size)`` () =
    withTempDir "ftc-prune-cost" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        // The constructor's one-time sweeps are the only scans this cache is allowed.
        let afterConstruction = cache.DirectoryScanCount
        let itc = cache :> ITaskCache

        let resultFor (h: string) =
            { CacheKey = hash h
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        // 50 files × 4 successive edits — a cold scan in miniature.
        for i in 1..50 do
            for h in [ "v1"; "v2"; "v3"; "v4" ] do
                itc.Set (ck "lint" $"/src/F%d{i}.fs") (hash h) (resultFor h)

        // NOT ONE directory scan across 200 writes.
        test <@ cache.DirectoryScanCount = afterConstruction @>

        // …and the prune still did its job: one surviving entry per key, the newest.
        test <@ System.IO.Directory.GetFiles(tmpDir, "*.json").Length = 50 @>
        test <@ (itc.TryGet (ck "lint" "/src/F7.fs") (hash "v4")).IsSome @>
        test <@ (itc.TryGet (ck "lint" "/src/F7.fs") (hash "v1")).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache collects siblings left behind by a PREVIOUS process`` () =
    // The write path knows only the paths IT wrote, so the guarantee ("only the newest
    // hash per key survives") would end at the process boundary — a cache directory
    // carried over from an earlier daemon would keep its dead siblings forever. The
    // constructor's one-time sweep is what closes that: it seeds the memo from disk, so
    // the first write to a key still collects what someone else left under it.
    withTempDir "ftc-prune-prior-process" (fun tmpDir ->
        // Entries a previous process left behind, for two different keys.
        let priorA1 = System.IO.Path.Combine(tmpDir, "lint---src-A.fs@aaaaaaaaaaaa.json")
        let priorA2 = System.IO.Path.Combine(tmpDir, "lint---src-A.fs@bbbbbbbbbbbb.json")
        let priorB = System.IO.Path.Combine(tmpDir, "lint---src-B.fs@cccccccccccc.json")

        for f in [ priorA1; priorA2; priorB ] do
            System.IO.File.WriteAllText(f, "{}")

        // A FRESH cache over that directory — a new process, as after a daemon restart.
        let cache = FileTaskCache(tmpDir) :> ITaskCache

        cache.Set
            (ck "lint" "/src/A.fs")
            (hash "v-new")
            { CacheKey = hash "v-new"
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        // Both of A's inherited siblings are collected by A's first write…
        test <@ not (System.IO.File.Exists priorA1) @>
        test <@ not (System.IO.File.Exists priorA2) @>
        // …and B's, belonging to a key nobody wrote, is untouched: it is still the
        // newest entry for ITS key, and is still reachable.
        test <@ System.IO.File.Exists priorB @>
        test <@ (cache.TryGet (ck "lint" "/src/A.fs") (hash "v-new")).IsSome @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache ignores a stray .json that is not one of its entries`` () =
    // The construction sweep reads every `*.json` in the cache dir and works out which
    // key each one belongs to — from the `@{hash}` suffix in its name. A file WITHOUT
    // that suffix belongs to no key: it is unreachable through `TryGet`, so it is not
    // ours to remember — and, just as importantly, not ours to DELETE. Nothing in the
    // cache dir may be collected except as the superseded sibling of a key we wrote.
    withTempDir "ftc-stray" (fun tmpDir ->
        let stray = System.IO.Path.Combine(tmpDir, "notes.json")
        let leadingAt = System.IO.Path.Combine(tmpDir, "@nokey.json")
        System.IO.File.WriteAllText(stray, "{}")
        System.IO.File.WriteAllText(leadingAt, "{}")

        // Construction must survive them…
        let cache = FileTaskCache(tmpDir) :> ITaskCache

        // …and a write that prunes its own siblings must leave them alone.
        cache.Set
            (ck "lint" "/src/A.fs")
            (hash "v1")
            { CacheKey = hash "v1"
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        cache.Set
            (ck "lint" "/src/A.fs")
            (hash "v2")
            { CacheKey = hash "v2"
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        test <@ System.IO.File.Exists stray @>
        test <@ System.IO.File.Exists leadingAt @>
        test <@ (cache.TryGet (ck "lint" "/src/A.fs") (hash "v2")).IsSome @>
        // The key's own predecessor WAS collected: 2 strays + 1 live entry.
        test <@ System.IO.Directory.GetFiles(tmpDir, "*.json").Length = 3 @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache persists and retrieves across instances`` () =
    withTempDir "ftc-persist" (fun tmpDir ->
        let cache1 = FileTaskCache(tmpDir)

        let result =
            { CacheKey = hash "abc"
              Errors = [ "/src/A.fs", [ errorEntry "warn" DiagnosticSeverity.Warning ] ]
              Status = cachedFileDone
              EmittedEvents = [] }

        (cache1 :> ITaskCache).Set (ck "lint" "/src/A.fs") (hash "abc") result

        // New instance, same directory
        let cache2 = FileTaskCache(tmpDir)
        let retrieved = (cache2 :> ITaskCache).TryGet (ck "lint" "/src/A.fs") (hash "abc")
        test <@ retrieved.IsSome @>
        test <@ retrieved.Value.Errors.Length = 1 @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache clear removes all files`` () =
    withTempDir "ftc-clear" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)

        let result =
            { CacheKey = hash "abc"
              Errors = []
              Status = cachedFileDone
              EmittedEvents = [] }

        (cache :> ITaskCache).Set (ckPlugin "build") (hash "abc") result
        (cache :> ITaskCache).Clear()
        test <@ (cache :> ITaskCache).TryGet (ckPlugin "build") (hash "abc") |> Option.isNone @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips all CachedStatus variants`` () =
    withTempDir "ftc-status" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let statuses =
            [ CachedFileCompleted(TimeSpan.FromMilliseconds 250.0)
              CachedFileFailed("boom", TimeSpan.FromMilliseconds 125.0)
              CachedRunCompleted(RunVerdict.create "6 passed, 0 failed" (TimeSpan.FromSeconds 2.0))
              CachedRunFailed("kaput", RunVerdict.create "1 failed" (TimeSpan.FromSeconds 3.0)) ]

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

        for i, status in statuses |> List.indexed do
            test <@ (c2.TryGet (ck "plugin" $"%d{i}") (hash "k")).Value.Status = status @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips cached events`` () =
    withTempDir "ftc-events" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
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

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips wasFiltered=true and RanFullSuite=false`` () =
    withTempDir "ftc-filtered" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let runId = System.Guid.NewGuid()

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
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

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips the TestsDeferred case (never-ran, non-green)`` () =
    // Issue 1: the new TestsDeferred case must survive serialization, and on
    // the way back it must stay NON-passing (so a cached deferred result can
    // never replay as a silent false-green).
    withTempDir "ftc-deferred" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
              EmittedEvents =
                [ CachedTestRunCompleted
                      { RunId = System.Guid.NewGuid()
                        TotalElapsed = System.TimeSpan.Zero
                        Outcome = Normal
                        Results = Map.ofList [ "p1", TestsDeferred "apphost not produced; tests did not run" ]
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
        let p1 = evt.Value.Results.["p1"]
        test <@ TestResult.isDeferred p1 @>
        test <@ not (TestResult.isPassed p1) @>
        test <@ (TestResult.output p1).Contains("apphost not produced") @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips the TestsErrored case (aborted, non-green)`` () =
    // The TestsErrored case must survive serialization and stay NON-passing on
    // the way back. In practice an errored result is never written (it is
    // non-passing, so the cacheKey gate skips the write), but the serializer
    // must still handle it for exhaustiveness/robustness.
    withTempDir "ftc-errored" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
              EmittedEvents =
                [ CachedTestRunCompleted
                      { RunId = System.Guid.NewGuid()
                        TotalElapsed = System.TimeSpan.Zero
                        Outcome = Normal
                        Results =
                          Map.ofList [ "p1", TestsErrored "test host exited non-zero but wrote no parseable report" ]
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
        let p1 = evt.Value.Results.["p1"]
        test <@ TestResult.isErrored p1 @>
        test <@ not (TestResult.isPassed p1) @>
        test <@ (TestResult.output p1).Contains("no parseable report") @>)

[<Fact(Timeout = 15000)>]
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
              Status = cachedFileDone
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

[<Fact(Timeout = 15000)>]
let ``FileTaskCache ClearPlugin removes only matching files`` () =
    withTempDir "ftc-clearplugin" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        c.Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        c.Set (ck "lint" "Foo.fs") (hash "h2") (makeResult "h2")
        c.ClearPlugin "build"
        test <@ c.TryGet (ck "build" "Foo.fs") (hash "h1") |> Option.isNone @>
        test <@ c.TryGet (ck "lint" "Foo.fs") (hash "h2") |> Option.isSome @>)

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``FileTaskCache.Set leaves no .tmp files behind`` () =
    withTempDir "ftc-atomic-clean" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        (cache :> ITaskCache).Set (ck "build" "Foo.fs") (hash "h1") (makeResult "h1")
        let tmps = System.IO.Directory.EnumerateFiles(tmpDir, "*.tmp") |> Seq.toList
        test <@ List.isEmpty tmps @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache constructor sweeps orphan .tmp files`` () =
    withTempDir "ftc-atomic-sweep" (fun tmpDir ->
        // Simulate a prior crash mid-write by dropping an orphan .tmp file.
        let orphan = System.IO.Path.Combine(tmpDir, "build--Foo.fs@deadbeef.json.tmp")
        System.IO.File.WriteAllText(orphan, "{ partial JSON")
        // Constructor should sweep it.
        let _cache = FileTaskCache(tmpDir)
        test <@ not (System.IO.File.Exists orphan) @>)

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``FileTaskCache.Stats on empty dir reports zero`` () =
    withTempDir "ftc-stats-empty" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let entryCount = cache.Stats.EntryCount
        let sizeBytes = cache.Stats.SizeBytes
        test <@ entryCount = 0 @>
        test <@ sizeBytes = 0L @>)

[<Fact(Timeout = 15000)>]
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

// --- Coverage-floor tests for FileTaskCache JSON variants -----------
// These exercise serialise/deserialise paths for the rarer EmittedEvents
// shapes (TestsTimedOut, CachedTestProgress, CachedCommandCompleted,
// Aborted outcome, CompositeKey-without-file). Without them
// FileTaskCache.fs sits at ~77.7 % line coverage and the ratchet's 78 %
// threshold drifts in/out of fail across runs (deterministic shortfall,
// not flake — see coverage_ratchet_lucky_ceiling memory).

[<Fact(Timeout = 15000)>]
let ``FileTaskCache tolerates explicit null wasFiltered/elapsedSeconds (old cache back-compat)`` () =
    // Old caches written before these fields existed (and any that serialized
    // them as JSON null) must still deserialize, defaulting to false / Zero.
    // Exercises the `isNull node` branches in deserializeTestResult.
    withTempDir "ftc-null-fields" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        let key = ck "test-prune" "X.fs"
        let cacheKey = hash "k"

        let result =
            { CacheKey = cacheKey
              Errors = []
              Status = cachedFileDone
              EmittedEvents =
                [ CachedTestRunCompleted
                      { RunId = System.Guid.NewGuid()
                        TotalElapsed = System.TimeSpan.Zero
                        Outcome = Normal
                        Results = Map.ofList [ "p1", TestsPassed("ok", true, TimeSpan.FromSeconds 2.0) ]
                        RanFullSuite = false } ] }

        c.Set key cacheKey result

        // Rewrite the on-disk JSON, replacing the stored wasFiltered/elapsedSeconds
        // values with explicit nulls (the "old/partial cache" shape).
        let path = System.IO.Directory.EnumerateFiles(tmpDir, "*.json") |> Seq.head
        let raw = System.IO.File.ReadAllText(path)

        let patched =
            raw
                .Replace("\"wasFiltered\":true", "\"wasFiltered\":null")
                .Replace("\"wasFiltered\": true", "\"wasFiltered\": null")
                .Replace("\"elapsedSeconds\":2", "\"elapsedSeconds\":null")
                .Replace("\"elapsedSeconds\": 2", "\"elapsedSeconds\": null")

        System.IO.File.WriteAllText(path, patched)

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet key cacheKey
        test <@ r.IsSome @>

        let evt =
            r.Value.EmittedEvents
            |> List.tryPick (function
                | CachedTestRunCompleted e -> Some e
                | _ -> None)

        test <@ evt.IsSome @>
        let p1 = evt.Value.Results.["p1"]
        // Null fields → safe defaults.
        test <@ not (TestResult.wasFiltered p1) @>
        test <@ TestResult.elapsed p1 = TimeSpan.Zero @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips TestsTimedOut variant`` () =
    withTempDir "ftc-timed-out" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let runId = System.Guid.NewGuid()

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
              EmittedEvents =
                [ CachedTestRunCompleted
                      { RunId = runId
                        TotalElapsed = TimeSpan.FromSeconds(7.5)
                        Outcome = Aborted "user-cancel"
                        Results =
                          Map.ofList
                              [ "p1",
                                TestsTimedOut("output", TimeSpan.FromSeconds(30.0), false, TimeSpan.FromSeconds(30.0)) ]
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

        match evt.Value.Outcome with
        | Aborted reason -> test <@ reason = "user-cancel" @>
        | _ -> failwith "expected Aborted outcome"

        match evt.Value.Results.["p1"] with
        | TestsTimedOut(_, after, _, elapsed) ->
            test <@ after = TimeSpan.FromSeconds(30.0) @>
            test <@ elapsed = TimeSpan.FromSeconds(30.0) @>
        | _ -> failwith "expected TestsTimedOut")

[<Fact(Timeout = 15000)>]
let ``FileTaskCache roundtrips CachedTestProgress and CachedCommandCompleted`` () =
    withTempDir "ftc-progress-cmd" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache

        let runId = System.Guid.NewGuid()
        let startedAt = fixedTime

        let progress: TestProgress =
            { RunId = runId
              NewResults =
                Map.ofList
                    [ "proj-a", TestsPassed("ok", false, TimeSpan.FromSeconds 1.0)
                      "proj-b", TestsFailed("nope", true, TimeSpan.FromSeconds 0.5) ] }

        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }

        let cmdOk: CommandCompletedResult =
            { Name = "echo"
              Outcome = CommandSucceeded "hi" }

        let cmdBad: CommandCompletedResult =
            { Name = "false"
              Outcome = CommandFailed "boom" }

        let result =
            { CacheKey = hash "k"
              Errors = []
              Status = cachedFileDone
              EmittedEvents =
                [ CachedTestRunStarted started
                  CachedTestProgress progress
                  CachedCommandCompleted cmdOk
                  CachedCommandCompleted cmdBad ] }

        // Use a CompositeKey *without* a file to also cover that branch
        // of compositeKeyToString.
        c.Set (ckPlugin "filecmd") (hash "k") result

        let cache2 = FileTaskCache(tmpDir)
        let r = (cache2 :> ITaskCache).TryGet (ckPlugin "filecmd") (hash "k")
        test <@ r.IsSome @>
        test <@ r.Value.EmittedEvents.Length = 4 @>

        let progressEvt =
            r.Value.EmittedEvents
            |> List.tryPick (function
                | CachedTestProgress p -> Some p
                | _ -> None)

        test <@ progressEvt.IsSome @>
        test <@ progressEvt.Value.RunId = runId @>
        test <@ progressEvt.Value.NewResults.Count = 2 @>

        let startedEvt =
            r.Value.EmittedEvents
            |> List.tryPick (function
                | CachedTestRunStarted s -> Some s
                | _ -> None)

        test <@ startedEvt.IsSome @>
        test <@ startedEvt.Value.RunId = runId @>

        let cmds =
            r.Value.EmittedEvents
            |> List.choose (function
                | CachedCommandCompleted c -> Some c
                | _ -> None)

        test <@ cmds.Length = 2 @>

        let cmdOutcomes = cmds |> List.map (fun c -> c.Outcome)

        test
            <@
                cmdOutcomes
                |> List.exists (function
                    | CommandSucceeded "hi" -> true
                    | _ -> false)
            @>

        test
            <@
                cmdOutcomes
                |> List.exists (function
                    | CommandFailed "boom" -> true
                    | _ -> false)
            @>)

[<Fact(Timeout = 15000)>]
let ``FileTaskCache TryGet on missing file returns None`` () =
    withTempDir "ftc-miss" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        let result = c.TryGet (ck "build" "Nonexistent.fs") (hash "k")
        test <@ result = None @>)

// ---------------------------------------------------------------------------
// A cache replay must NEVER claim the plugin is at rest while an exclusive run
// is in flight (AUTOMATION-95/99 — "a verdict nobody earned").
// ---------------------------------------------------------------------------
//
// On a warm scan EVERY FileChecked is a cache hit, and each hit re-reported its
// cached `Completed` — stomping the `Running` that the in-flight test run had
// just set. `allPluginsAtRest` then saw no plugin Running and `WaitForComplete`
// resolved WHILE the tests were still executing (field evidence: run launched
// 11:30:17, still running at 11:30:34, daemon logged "all plugins already
// terminal", `check` exited 0). The suppression that fixes it shipped without a
// test; this is that test.
//
// The replay is proven to have actually HAPPENED — via the errors it replays —
// so this cannot pass by accident on a replay that never ran.
//
// RED-BEFORE-GREEN: drop the `anyRunSlotBusy ()` guard in PluginFramework's
// replay path and the status is Completed while the run is still in flight.

[<Fact(Timeout = 20000)>]
let ``cache replay does not stomp a Running status while an exclusive run is in flight`` () =
    let cache = InMemoryTaskCache()

    // The cached entry for B: terminal, and carrying an error so the replay is
    // observable.
    cache.Set(
        ck "test-plugin" "/src/B.fs",
        hash "k-B",
        { CacheKey = hash "k-B"
          Errors =
            [ "/src/B.fs",
              [ { Message = "replayed"
                  Severity = DiagnosticSeverity.Warning
                  Line = 1
                  Column = 0
                  Detail = None } ] ]
          Status = cachedFileDone
          EmittedEvents = [] }
    )

    // Held for the duration of the "test run", so the exclusive slot is busy on
    // our schedule rather than on a timer.
    use runGate = new SemaphoreSlim(0, 1)
    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "test-plugin"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChecked _ ->
                        // Launch a long "test run", exactly as TestPrune does —
                        // the framework reports Running at the claim.
                        let claim =
                            ctx.RunExclusive
                                "tests"
                                (async {
                                    do! runGate.WaitAsync() |> Async.AwaitTask
                                    return ()
                                })

                        test <@ claim = Claimed @>
                    | Custom() ->
                        // The run finished and reported its own real verdict.
                        ctx.ReportStatus(completedAt DateTime.UtcNow)
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          // A is a cache MISS (drives the run); B is a HIT (drives the replay).
          CacheKey =
            Some(fun event ->
                match event with
                | FileChecked r when (AbsFilePath.value r.File).EndsWith("B.fs") -> Some(hash "k-B")
                | _ -> None)
          Teardown = None }

    host.RegisterHandler(handler)

    // 1. A: cache miss → Update runs → Running + an exclusive run that is now stuck
    //    on the gate.
    host.EmitFileChecked(dummyFileCheckResult "/src/A.fs")

    waitUntil
        (fun () ->
            match host.GetStatus("test-plugin") with
            | Some(Running _) -> true
            | _ -> false)
        12000

    // 2. B: cache HIT → the replay path runs. Wait for the replayed ERROR, which
    //    proves the replay really happened (and isn't merely slow).
    host.EmitFileChecked(dummyFileCheckResult "/src/B.fs")
    waitUntil (fun () -> host.HasFailingReasons(warningsAreFailures = true)) 12000

    // 3. THE ASSERTION. The run is still in flight — nobody has earned a verdict —
    //    so the cached terminal status must not have been reported over it.
    test
        <@
            match host.GetStatus("test-plugin") with
            | Some(Running _) -> true
            | _ -> false
        @>

    // 4. Let the run finish; the REAL verdict (from the run itself) lands.
    runGate.Release() |> ignore
    waitForTerminalStatus host "test-plugin" 12000

    // NOT the cached `fixedTime` — the verdict came from the run, not the replay.
    test <@ host.GetStatus("test-plugin") <> Some(completedAt fixedTime) @>

    test
        <@
            match host.GetStatus("test-plugin") with
            | Some(Completed _) -> true
            | _ -> false
        @>

// --- RunVerdict on the wire (AUTOMATION-99) --------------------------------
// A Completed status now CARRIES its verdict. Two consequences pinned here:
// an on-disk entry from before the verdict existed has no evidence to replay,
// so it must be a cache MISS; and a replayed verdict must identify itself as
// served from cache rather than passing as a fresh run.

[<Fact(Timeout = 15000)>]
let ``FileTaskCache rejects a pre-verdict completed entry as a cache miss`` () =
    withTempDir "ftc-preverdict" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        let key = ck "test-prune" "X.fs"
        let cacheKey = hash "k"
        c.Set key cacheKey (makeResult "k")

        // Strip the status's required evidence fields (elapsedMs; summary is
        // already absent on a per-file entry) — an entry whose terminal has no
        // evidence must read as a MISS, never as a verdict-free terminal.
        let path = System.IO.Directory.EnumerateFiles(tmpDir, "*.json") |> Seq.head

        let root =
            System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)).AsObject()

        let status = root.["status"].AsObject()
        status.Remove("summary") |> ignore
        status.Remove("elapsedMs") |> ignore
        System.IO.File.WriteAllText(path, root.ToJsonString())

        let before = cache.ParseFailureCount
        let result = c.TryGet key cacheKey
        test <@ result = None @>
        test <@ cache.ParseFailureCount = before + 1 @>)

[<Fact(Timeout = 20000)>]
let ``cache replay of a whole-run entry reports the original verdict marked as cached`` () =
    // AUTOMATION-186 scope rule, the sound half: a `File = None` entry is keyed
    // on the run's FULL input, so its stored verdict is a pure function of the
    // key and replays VERBATIM (plus the cached marker) — never laundered into
    // a ledger-derived summary that would lose run evidence like pass counts.
    let cache = InMemoryTaskCache()

    cache.Set(
        ckPlugin "verdict-plugin",
        hash "k-V",
        { CacheKey = hash "k-V"
          Errors = []
          Status = CachedRunCompleted(RunVerdict.create "6 passed, 0 failed in 6 projects" (TimeSpan.FromSeconds 12.5))
          EmittedEvents = [] }
    )

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "verdict-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = Some(fun _ -> Some(hash "k-V"))
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitBuildCompleted(BuildSucceeded)

    waitUntil
        (fun () ->
            match host.GetStatus("verdict-plugin") with
            | Some(Completed _) -> true
            | _ -> false)
        12000

    match host.GetStatus("verdict-plugin") with
    | Some(Completed(_, v)) ->
        // The evidence is the ORIGINAL run's (summary + true duration), and the
        // rendering can never pass the replay off as a fresh run.
        test <@ v.Summary = "6 passed, 0 failed in 6 projects (cached)" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 12.5 @>
    | other -> failwith $"expected Completed with verdict, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``cache replay does not stack the cached marker on an already-marked verdict`` () =
    // Idempotence pin for the replay marker: a cached run verdict whose summary
    // already carries " (cached)" (however it got there) replays unchanged —
    // never "(cached) (cached)".
    let cache = InMemoryTaskCache()

    cache.Set(
        ckPlugin "marked-plugin",
        hash "k-M",
        { CacheKey = hash "k-M"
          Errors = []
          Status = CachedRunCompleted(RunVerdict.create "ok (cached)" (TimeSpan.FromSeconds 1.0))
          EmittedEvents = [] }
    )

    let host = PluginHost(nullChecker, "/tmp/test", taskCache = (cache :> ITaskCache))

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "marked-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
          CacheKey = Some(fun _ -> Some(hash "k-M"))
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitBuildCompleted(BuildSucceeded)

    waitUntil
        (fun () ->
            match host.GetStatus("marked-plugin") with
            | Some(Completed _) -> true
            | _ -> false)
        12000

    match host.GetStatus("marked-plugin") with
    | Some(Completed(_, v)) -> test <@ v.Summary = "ok (cached)" @>
    | other -> failwith $"expected Completed, got %A{other}"

// NOTE (AUTOMATION-186): the former "cache replay of a non-terminal status
// replays it verbatim" pin is gone WITH its hazard — `CachedStatus` has no
// non-terminal variants, so a cache entry that asserts nothing terminal is
// unrepresentable and there is no laundering branch left to test.

[<Fact(Timeout = 15000)>]
let ``FileTaskCache reads an old-format entry as a miss, counted as a parse failure`` () =
    // AUTOMATION-186: format-1 entries (no "format" field) stored a status
    // summary a per-file key cannot back. They must deterministically read as
    // a MISS (invalidating the whole pre-fix cache) — never half-parse into a
    // result carrying the stale claim.
    withTempDir "ftc-old-format" (fun tmpDir ->
        let cache = FileTaskCache(tmpDir)
        let c = cache :> ITaskCache
        let key = ck "analyzers" "/src/X.fs"
        let cacheKey = hash "k1"
        c.Set key cacheKey (makeResult "k1")

        // Rewrite the entry as format 1 wrote it: no "format" field, and the
        // old PluginStatus shape carrying the whole-session summary snapshot.
        let path = System.IO.Directory.EnumerateFiles(tmpDir, "*.json") |> Seq.head

        let root =
            System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)).AsObject()

        root.Remove("format") |> ignore
        let status = System.Text.Json.Nodes.JsonObject()
        status["type"] <- "completed"
        status["at"] <- fixedTime.ToString("o")
        status["summary"] <- "analyzed 1044 files, 5 findings (5 errors, 0 warnings)"
        status["elapsedMs"] <- 11.0
        root["status"] <- status
        System.IO.File.WriteAllText(path, root.ToJsonString())

        let before = cache.ParseFailureCount
        let result = c.TryGet key cacheKey
        test <@ result = None @>
        test <@ cache.ParseFailureCount = before + 1 @>)
