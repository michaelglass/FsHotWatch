module FsHotWatch.Tests.AnalyzersPluginTests

open System
open System.Reflection
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

/// The spelling the plugin framework keys a per-file cache entry by: REPO-RELATIVE,
/// so an entry survives being read in another checkout. A test that
/// stores under the absolute path stores under a key the framework will never look up.
let private compositeFileKey (repoRoot: string) (file: string) =
    FsHotWatch.CachePathIdentity.keyOf (Some repoRoot) file


// `Unchecked.defaultof` for ParseResults is deliberate: it stands in for the null FCS
// hands back on an abort, which the plugin must guard against.
let private fakeResult file =
    { fakeFileCheckResult file with
        Source = "let x = 1"
        ParseResults = Unchecked.defaultof<_> }

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 20000)>]
let ``diagnostics command returns zeroes when no files checked`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>
    test <@ result.Value.Contains("\"files\":0") @>
    test <@ result.Value.Contains("\"diagnostics\":0") @>

[<Fact(Timeout = 15000)>]
let ``analyzer error path does not crash`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let fakeResult =
        { fakeFileCheckResult "/tmp/nonexistent/Fake.fs" with
            Source = ""
            ParseResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    // Any of Idle / Running / Completed is acceptable — the event may not have been
    // picked up yet. Only a crash is a failure.
    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> ()
    | Running _ -> ()
    | Idle -> ()
    | other -> Assert.Fail($"Expected Idle, Completed, or Running, got: %A{other}")

[<Fact(Timeout = 15000)>]
let ``analyzer with non-existent path skips loading`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create None [ "/tmp/no-such-analyzer-dir-12345" ] None DiagnosticSeverity.Hint

    host.RegisterHandler(handler)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>

[<Fact(Timeout = 20000)>]
let ``analyzer with mix of valid and invalid paths`` () =
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-empty-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(emptyDir) |> ignore

    try
        let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

        let handler =
            create
                None
                [ emptyDir // exists but no analyzer DLLs
                  "/tmp/nonexistent-path-xyz-99999" ] // does not exist
                None
                DiagnosticSeverity.Hint

        host.RegisterHandler(handler)

        let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"analyzers\":0") @>
    finally
        try
            System.IO.Directory.Delete(emptyDir, true)
        with _ ->
            ()

// Guard inputs: a configured analyzer path that loads zero must produce LoadedCount = 0,
// which DaemonConfig.analyzersLoadFailure turns into a RED gate. The two shapes that hit
// it are a missing path (the actual CI bug — bin built in the wrong config) and an
// existing path with no analyzer DLLs.

[<Fact(Timeout = 15000)>]
let ``configured non-existent analyzer path loads zero (guard input)`` () =
    let path = "/tmp/no-such-analyzer-dir-guard-a"
    let handler = create None [ path ] None DiagnosticSeverity.Hint

    test <@ handler.Init.LoadedCount = 0 @>
    // Recorded per-path, so the guard can name the offender rather than just the total.
    test <@ handler.Init.LoadedByPath = [ path, 0 ] @>

[<Fact(Timeout = 15000)>]
let ``configured empty analyzer dir loads zero (guard input)`` () =
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-guard-empty-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(emptyDir) |> ignore

    try
        let handler = create None [ emptyDir ] None DiagnosticSeverity.Hint
        test <@ handler.Init.LoadedCount = 0 @>
        test <@ handler.Init.LoadedByPath = [ emptyDir, 0 ] @>
    finally
        try
            System.IO.Directory.Delete(emptyDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``concurrent analyzer runs are bounded`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 15000)>]
let ``cache key includes parse-only suffix for ParseOnly results`` () =
    let commitId = "abc123"
    let handler = create None [] None DiagnosticSeverity.Hint

    let parseOnlyResult =
        { fakeFileCheckResult "/tmp/Fake.fs" with
            Source = ""
            ParseResults = Unchecked.defaultof<_> }

    let fullCheckResult =
        { parseOnlyResult with
            CheckResults = FullCheck(Unchecked.defaultof<_>) }

    let cacheKeyFn = handler.CacheKey.Value handler.Init

    let parseOnlyKey = cacheKeyFn (FileChecked parseOnlyResult)
    let fullCheckKey = cacheKeyFn (FileChecked fullCheckResult)

    test <@ parseOnlyKey.IsSome @>
    test <@ fullCheckKey.IsSome @>
    test <@ parseOnlyKey <> fullCheckKey @>

[<Fact(Timeout = 20000)>]
let ``ParseOnly dispatches to analyzer worker instead of skipping`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = AbsFilePath.create "/tmp/nonexistent/Fake.fs"
          Source = "let x = 1"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L
          ModelGeneration = Some fixtureModelGeneration
          Frame = None }

    host.EmitFileChecked(fakeResult)

    waitForTerminalStatus host "analyzers" 12000

    // The crash IS the evidence: with a null ParseResults the analyzer must fail via
    // AnalysisFailed, proving the worker ran rather than skipping synchronously.
    let errors = host.GetErrorsByPlugin("analyzers")

    let hasAnalyzerCrash =
        errors
        |> Map.exists (fun _ entries -> entries |> List.exists (fun e -> e.Message.Contains("Analyzer crashed")))

    test <@ hasAnalyzerCrash @>

[<Fact(Timeout = 15000)>]
let ``empty analyzer paths still creates working handler`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    test <@ handler.Init.LoadedCount = 0 @>
    // No configured paths ⇒ empty per-path list ⇒ the per-path guard stays silent.
    test <@ List.isEmpty handler.Init.LoadedByPath @>
    test <@ handler.Init.DiagnosticsByFile = Map.empty @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>

[<Fact(Timeout = 15000)>]
let ``AnalysisFailed custom message sets status to Completed`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeResult "/tmp/test/FailAnalysis.fs")
    waitForTerminalStatus host "analyzers" 15000

    let errors = host.GetErrorsByPlugin("analyzers")

    let hasAnalyzerCrash =
        errors
        |> Map.exists (fun _ entries -> entries |> List.exists (fun e -> e.Message.Contains("Analyzer crashed")))

    test <@ hasAnalyzerCrash @>

// getCommitId is not consulted here: the plugin always provides a CacheKey and the key
// depends only on the FileChecked content.

[<Fact(Timeout = 15000)>]
let ``cache key is provided regardless of getCommitId`` () =
    let h1 = create None [] None DiagnosticSeverity.Hint
    let h2 = create None [] None DiagnosticSeverity.Hint
    let h3 = create None [] None DiagnosticSeverity.Hint
    test <@ h1.CacheKey.IsSome @>
    test <@ h2.CacheKey.IsSome @>
    test <@ h3.CacheKey.IsSome @>

[<Fact(Timeout = 15000)>]
let ``cache key reflects file content when getCommitId is unavailable`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value handler.Init

    let r1 =
        { fakeResult "/tmp/X.fs" with
            Source = "let x = 1" }

    let r2 =
        { fakeResult "/tmp/X.fs" with
            Source = "let x = 1" }

    let r3 =
        { fakeResult "/tmp/X.fs" with
            Source = "let x = 2" }

    let k1 = cacheKeyFn (FileChecked r1)
    let k2 = cacheKeyFn (FileChecked r2)
    let k3 = cacheKeyFn (FileChecked r3)
    test <@ k1.IsSome @>
    test <@ k1 = k2 @>
    test <@ k1 <> k3 @>

// ---------------------------------------------------------------------------
// A rebuilt analyzer DLL must invalidate cached per-file verdicts even when the
// configured PATHS are byte-identical. The key used to hash the path strings only, so a
// long-lived daemon replayed stale-green for unchanged source after a rule changed. The
// throwaway *.dll byte files below exercise the keying deterministically, with no real
// SDK analyzer load.
// ---------------------------------------------------------------------------

let private analyzerBinWith (name: string) (bytes: byte array) =
    let dir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-id-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(dir) |> ignore
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{name}.dll"), bytes)
    dir

/// The `analyzer-inputs` slot for a set of throwaway analyzer bins. No PDB, no
/// repository: every DLL is identified by its bytes, which is what these tests need.
let private inputsOf (dirs: string list) =
    (snapshotAnalyzerSet None knownNonAnalyzerPrefixes dirs).Inputs

[<Fact(Timeout = 15000)>]
let ``analyzer-inputs changes when an analyzer DLL's content changes`` () =
    let dir = analyzerBinWith "MyAnalyzer" [| 1uy; 2uy; 3uy |]

    try
        let before = inputsOf [ dir ]

        // A rebuild: same path, same DLL filename, new bytes. The identity shift is what
        // invalidates the stale per-file cache entry.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "MyAnalyzer.dll"), [| 9uy; 8uy; 7uy; 6uy |])
        let after = inputsOf [ dir ]

        test <@ before <> after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzer-inputs changes when a new analyzer DLL is added`` () =
    let dir = analyzerBinWith "First" [| 1uy; 2uy |]

    try
        let before = inputsOf [ dir ]

        // Adding a second analyzer to the same path must change identity.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "Second.dll"), [| 3uy; 4uy |])
        let after = inputsOf [ dir ]

        test <@ before <> after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzer-inputs is stable for identical content (cache hits survive a no-op rescan)`` () =
    let dir = analyzerBinWith "Stable" [| 5uy; 5uy; 5uy |]

    try
        let a = inputsOf [ dir ]
        let b = inputsOf [ dir ]
        test <@ a = b @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzer-inputs ignores known-non-analyzer (bundled dep) DLLs`` () =
    // Otherwise an FSharp.Core refresh would invalidate every cached verdict.
    let dir = analyzerBinWith "RealAnalyzer" [| 1uy |]

    try
        let before = inputsOf [ dir ]
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "FSharp.Core.dll"), [| 42uy; 43uy |])
        let after = inputsOf [ dir ]
        test <@ before = after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzer set snapshot does not throw on a missing path`` () =
    // A configured-but-missing path contributes nothing, never a throw that would
    // abort plugin construction; the 0-analyzer guard is what reports it.
    let id1 = inputsOf [ "/tmp/no-such-analyzer-dir-id-xyz" ]
    let id2 = inputsOf [ "/tmp/no-such-analyzer-dir-id-xyz" ]

    test <@ id1 = id2 @>
    test <@ Result.isOk id1 @>

[<Fact(Timeout = 30000)>]
let ``a refused analyzer identity means no cache key, no cache entry, and the analyzers still run`` () =
    // The house-rules DLL copied WITHOUT its PDB: its CodeView entry places the PDB
    // under this repository, so the receipt is expected and missing (`MissingPdb`).
    withTempDir "az-refused" (fun dir ->
        withTempDir "az-refused-store" (fun store ->
            let dll =
                System.IO.Path.Combine(dir, System.IO.Path.GetFileName AnalyzerFixtures.rulesDll)

            System.IO.File.Copy(AnalyzerFixtures.rulesDll, dll)

            let repoRoot = AnalyzerFixtures.repoRoot

            let cache =
                FsHotWatch.FileTaskCache.FileTaskCache(store, repoRoot = repoRoot) :> FsHotWatch.TaskCache.ITaskCache

            let host = PluginHost(Unchecked.defaultof<_>, repoRoot, taskCache = cache)
            host.WorkStore.PublishProjectModel fixtureModel
            let handler = create (Some repoRoot) [ dir ] None DiagnosticSeverity.Hint
            // The analyzers loaded: the refusal is about the CACHE, not the run.
            test <@ handler.Init.LoadedCount >= 1 @>
            host.RegisterHandler(handler)

            let file = System.IO.Path.Combine(repoRoot, "src", "Probe.fs")
            test <@ (handler.CacheKey.Value handler.Init) (FileChecked(fakeResult file)) = None @>

            host.EmitFileChecked(fakeResult file)
            waitForTerminalStatus host "analyzers" 20000

            // Nothing was written: a verdict that cannot be named cannot be replayed.
            test <@ Array.isEmpty (System.IO.Directory.GetFiles(store, "*", System.IO.SearchOption.AllDirectories)) @>))

[<Fact(Timeout = 15000)>]
let ``regression: cache key changes when the analyzer DLL is rebuilt (same path)`` () =
    // Two handlers on the SAME analyzer path with different DLL content. Under the old
    // path-string-only key these keys were identical, so a daemon that cached "clean"
    // under the old DLL replayed it after the rebuild.
    let dir = analyzerBinWith "RuleChanged" [| 0uy; 1uy; 2uy |]

    try
        let h1 = create None [ dir ] None DiagnosticSeverity.Hint
        let event = FileChecked(fakeResult $"{dir}/Subject.fs")
        let key1 = (h1.CacheKey.Value h1.Init) event

        // The rebuild: same path, new content.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "RuleChanged.dll"), [| 9uy; 9uy; 9uy; 9uy |])

        let h2 = create None [ dir ] None DiagnosticSeverity.Hint
        let key2 = (h2.CacheKey.Value h2.Init) event

        test <@ key1.IsSome @>
        test <@ key2.IsSome @>
        test <@ key1 <> key2 @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``describeRefusals names the file and the cause for every refusal, and never a hash`` () =
    let g = System.Guid "8829d00f-11b8-4213-878b-770e8597ac16"

    let described =
        describeRefusals
            [ FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.Unreadable("/a.dll", "locked")
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.MissingPdb "/b.dll"
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.PdbMismatch("/c.dll", "/c.pdb")
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.UnverifiableChecksum("/d.fs", g)
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.DocumentMissing "/e.fs"
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.DocumentDrift("/f.fs", "aaaa", "bbbb")
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.ProducerNotFound "/g.dll"
              FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.OutputOlderThanProject("/h.dll", "/h.fsproj") ]

    for named in
        [ "/a.dll"
          "locked"
          "/b.dll"
          "/c.pdb"
          "/d.fs"
          "/e.fs"
          "/f.fs"
          "/g.dll"
          "/h.fsproj" ] do
        test <@ described.Contains named @>

    test <@ not (described.Contains "aaaa") @>
    test <@ not (described.Contains "bbbb") @>

[<Fact(Timeout = 15000)>]
let ``an unreadable analyzer DLL is a refusal, never a throw, and its digest still changes`` () =
    let dir = analyzerBinWith "Locked" [| 1uy; 2uy |]
    let dll = System.IO.Path.Combine(dir, "Locked.dll")

    try
        let readable = snapshotAnalyzerSet None knownNonAnalyzerPrefixes [ dir ]
        System.IO.File.SetUnixFileMode(dll, System.IO.UnixFileMode.None)
        let locked = snapshotAnalyzerSet None knownNonAnalyzerPrefixes [ dir ]

        test <@ Result.isOk readable.Inputs @>
        test <@ Result.isError locked.Inputs @>
        test <@ readable.Materialization <> locked.Materialization @>
    finally
        try
            System.IO.File.SetUnixFileMode(dll, System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserWrite)
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``one live handler follows a rebuilt DLL: the set is reloaded and the key moves with it`` () =
    // The warm-daemon case: the handler outlives the build that refreshes the DLL. The
    // key must describe the set now on disk, not the one loaded at construction.
    let dir = analyzerBinWith "Live" [| 0uy; 1uy; 2uy |]

    try
        let handler = create None [ dir ] None DiagnosticSeverity.Hint
        let event = FileChecked(fakeResult $"{dir}/Subject.fs")
        let before = (handler.CacheKey.Value handler.Init) event

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "Live.dll"), [| 9uy; 9uy; 9uy |])
        let after = (handler.CacheKey.Value handler.Init) event
        let again = (handler.CacheKey.Value handler.Init) event

        test <@ before.IsSome && after.IsSome @>
        test <@ before <> after @>
        test <@ after = again @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``a refusal that changes cause is re-noticed; the key stays absent throughout`` () =
    withTempDir "az-refusal-change" (fun dir ->
        let repoRoot = AnalyzerFixtures.repoRoot

        let dll =
            System.IO.Path.Combine(dir, System.IO.Path.GetFileName AnalyzerFixtures.rulesDll)

        System.IO.File.Copy(AnalyzerFixtures.rulesDll, dll)
        let handler = create (Some repoRoot) [ dir ] None DiagnosticSeverity.Hint

        let event =
            FileChecked(fakeResult (System.IO.Path.Combine(repoRoot, "src", "Probe.fs")))

        // MissingPdb: no receipt beside a first-party build.
        test <@ (handler.CacheKey.Value handler.Init) event = None @>

        // PdbMismatch: a sidecar that is not this build's. The snapshot is retaken
        // every event while refused, so the new cause is seen without a byte moving.
        System.IO.File.Copy(
            System.IO.Path.Combine(AppContext.BaseDirectory, "FsHotWatch.Tests.pdb"),
            System.IO.Path.ChangeExtension(dll, ".pdb")
        )

        test <@ (handler.CacheKey.Value handler.Init) event = None @>

        // The right PDB: the refusal clears, again without the DLL changing.
        System.IO.File.Copy(AnalyzerFixtures.rulesPdb, System.IO.Path.ChangeExtension(dll, ".pdb"), true)
        // ...but a DLL in a temp dir has no producer project above it in the repo.
        test <@ (handler.CacheKey.Value handler.Init) event = None @>)

[<Fact(Timeout = 15000)>]
let ``cache key for Custom event returns None`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value handler.Init

    let customKey = cacheKeyFn (Custom(AnalysisComplete("/tmp/Fake.fs", [])))
    test <@ customKey.IsNone @>

[<Fact(Timeout = 15000)>]
let ``cache key for non-FileChecked event returns None`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value handler.Init

    let buildKey = cacheKeyFn (BuildCompleted BuildSucceeded)
    test <@ buildKey.IsNone @>

[<Fact(Timeout = 15000)>]
let ``regression: FileChecked replays from cache on second emission with same content`` () =
    // Pins the Async.Start regression: fire-and-forget execution meant the first run
    // never wrote to cache, so every subsequent identical event re-ran the analyzer.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // Cold cache: the analyzer crashes (terminal Failed), which still writes the entry.
    host.EmitFileChecked(fakeResult "/tmp/test/Replay.fs")
    waitForTerminalStatus host "analyzers" 15000

    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some(compositeFileKey "/tmp" "/tmp/test/Replay.fs") }

    let cacheKeyFn = handler.CacheKey.Value handler.Init
    let event = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey = cacheKeyFn event
    // `runAndCache` writes the entry AFTER the plugin reports terminal status, so what
    // waitForTerminalStatus observes precedes the cache write by a small window. Poll the
    // cache itself rather than reading once and racing it.
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 15000
    test <@ (cacheIface.TryGet key computedKey.Value).IsSome @>

    let event2 = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey2 = cacheKeyFn event2
    test <@ computedKey = computedKey2 @>

[<Fact(Timeout = 15000)>]
let ``regression: cache replay must not resurrect a stale global findings summary`` () =
    // The mechanism: a per-FileChecked cache entry used to store a
    // terminal status whose summary snapshotted GLOBAL state (all of DiagnosticsByFile +
    // RunAnalyzed) while being keyed on per-file content. Fixing findings in file A
    // rewrote A's entry, but every OTHER file's entry still carried the old global count,
    // and the framework replayed that string verbatim — so `confirm` rendered "5
    // findings" over a ledger that held none. The fix makes the shape unrepresentable:
    // a per-file entry is a `CachedFileCompleted` with only its duration, and the summary
    // is derived from the live ledger at report time.
    //
    // The cache is pre-populated by hand because the divergence only appears when an
    // entry minted while OTHER files had findings is replayed after those were fixed —
    // a sequence a single-file test cannot produce.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let cleanFile = "/tmp/test/StaleSummary.fs"
    let checkResult = fakeResult cleanFile

    let cacheKey =
        ((handler.CacheKey.Value handler.Init) (FileChecked checkResult)).Value

    let cleanEntry: FsHotWatch.TaskCache.TaskCacheResult =
        { CacheKey = cacheKey
          // An empty entry is a clear, which is what keeps the ledger green. Under the
          // old shape this entry's summary would also have carried the global findings
          // count live at mint time, replayed verbatim over that clear.
          Errors = [ cleanFile, [] ]
          Status = FsHotWatch.TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 11.0)
          EmittedEvents = [] }

    let compKey: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some(compositeFileKey "/tmp" cleanFile) }

    cacheIface.Set compKey cacheKey cleanEntry

    // Re-check the unchanged clean file: cache HIT → framework replay path.
    host.EmitFileChecked(checkResult)
    waitForTerminalStatus host "analyzers" 15000

    // Premise of the incident: after replay the currently-valid findings set is
    // EMPTY — this is the set `fshw status` lists and the verdict validates.
    let liveFindings =
        host.GetErrorsByPlugin("analyzers")
        |> Map.toList
        |> List.sumBy (fun (_, entries) -> List.length entries)

    test <@ liveFindings = 0 @>

    let summary =
        match host.GetStatus("analyzers") with
        | Some(Completed(_, v)) -> v.Summary
        | Some(Failed(_, _, v)) -> v.Summary
        | other -> failwith $"expected a terminal analyzers status, got %A{other}"

    // THE INVARIANT: the rendered summary must derive from the CURRENT findings
    // set. Any findings count it asserts has to match the live diagnostics —
    // a summary claiming findings the ledger doesn't have is the false
    // "5 findings (cached)" over a green verdict.
    let claimedFindings =
        let m = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) findings")
        if m.Success then int m.Groups.[1].Value else 0

    Assert.True(
        (claimedFindings = liveFindings),
        $"cache replay re-asserted a stale findings count: summary=\"%s{summary}\" claims %d{claimedFindings} findings, but the currently-valid diagnostics set has %d{liveFindings}"
    )

[<Fact(Timeout = 15000)>]
let ``per-file replay WITH findings derives EXACTLY those findings, not a hardcoded zero`` () =
    // The inverse of the clean-file case, and the reason it is needed: a summary
    // hardcoded to "0 findings" passes that regression and lies here. Demanding the
    // exact count proves the derivation reads the live ledger.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let dirtyFile = "/tmp/test/WithFindings.fs"
    let checkResult = fakeResult dirtyFile

    let cacheKey =
        ((handler.CacheKey.Value handler.Init) (FileChecked checkResult)).Value

    let findings = [ for i in 1..5 -> ErrorEntry.error $"finding %d{i}" ]

    let dirtyEntry: FsHotWatch.TaskCache.TaskCacheResult =
        { CacheKey = cacheKey
          // The entry replays five live findings for this file into the ledger.
          Errors = [ dirtyFile, findings ]
          Status = FsHotWatch.TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 11.0)
          EmittedEvents = [] }

    let compKey: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some(compositeFileKey "/tmp" dirtyFile) }

    cacheIface.Set compKey cacheKey dirtyEntry

    // Re-check the file: cache HIT → framework replay path lands the findings.
    host.EmitFileChecked(checkResult)
    waitForTerminalStatus host "analyzers" 15000

    let liveFindings =
        host.GetErrorsByPlugin("analyzers")
        |> Map.toList
        |> List.sumBy (fun (_, entries) -> List.length entries)

    test <@ liveFindings = 5 @>

    let summary =
        match host.GetStatus("analyzers") with
        | Some(Completed(_, v)) -> v.Summary
        | Some(Failed(_, _, v)) -> v.Summary
        | other -> failwith $"expected a terminal analyzers status, got %A{other}"

    let claimedFindings =
        let m = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) findings")
        if m.Success then int m.Groups.[1].Value else 0

    Assert.True(
        (claimedFindings = liveFindings),
        $"derived summary must report the live findings exactly: summary=\"%s{summary}\" claims %d{claimedFindings}, live diagnostics has %d{liveFindings}"
    )

[<Fact(Timeout = 15000)>]
let ``regression: FileChecked with TaskCache writes a cache entry on terminal status`` () =
    // Pins the Async.Start regression: dispatching the work fire-and-forget returned
    // `state` while the analysis still ran, so the framework's cache-write window only
    // ever saw "Running" and nothing was written. FileChecked must await the inner async.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // The analyzer crashes on the null ParseResults. A crash is still terminal, so the
    // cache write must happen anyway.
    host.EmitFileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    waitForTerminalStatus host "analyzers" 15000

    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some(compositeFileKey "/tmp" "/tmp/test/CacheRegression.fs") }

    let cacheKeyFn = handler.CacheKey.Value handler.Init
    let event = FileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    let computedKey = cacheKeyFn event

    test <@ computedKey.IsSome @>
    // Cache write lags terminal status — poll it rather than reading once.
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 15000
    let result = cacheIface.TryGet key computedKey.Value
    test <@ result.IsSome @>

[<Fact(Timeout = 15000)>]
let ``multiple concurrent FileChecked events are bounded by semaphore`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let events =
        [ for i in 1..10 ->
              { fakeResult $"/tmp/concurrent/File%d{i}.fs" with
                  Version = int64 i } ]

    for e in events do
        host.EmitFileChecked(e)

    waitForTerminalStatus host "analyzers" 12000

    let errors = host.GetErrorsByPlugin("analyzers")
    test <@ errors.Count > 0 @>

[<Fact(Timeout = 15000)>]
let ``teardown cancels CTS and disposes resources`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    host.Teardown()

    try
        host.EmitFileChecked(fakeResult "/tmp/teardown/Fake.fs")
    with _ ->
        ()

// ---------------------------------------------------------------------------
// Pure-function tests for the reflection helpers. They cover deterministically what the
// live-FCS integration tests hit only nondeterministically, depending on the loaded SDK.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``analyzers handler times out when work exceeds TimeoutSec`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"
    // Sleeps well past the 1s timeout, forcing a TimedOut outcome.
    let slowHook () = System.Threading.Thread.Sleep 3000

    let handler =
        createWithSlowHook None [] (Some 1) DiagnosticSeverity.Hint (Some slowHook)

    host.RegisterHandler(handler)
    host.EmitFileChecked(fakeResult "/tmp/slow/File.fs")
    waitForTerminalStatus host "analyzers" 12000
    let snap = host.GetActivitySnapshot("analyzers")

    match snap.LastRun with
    | Some r ->
        match r.Outcome with
        | FsHotWatch.Events.TimedOut _ -> ()
        | other -> Assert.Fail $"Expected TimedOut, got {other}"
    | None -> Assert.Fail "Expected LastRun record"

[<Fact(Timeout = 15000)>]
let ``timed-out synchronous analyzer cannot overlap the next file`` () =
    // Production change that makes this pass: retain the analyzer concurrency
    // permit until non-cooperative timed-out work has actually exited. Merely
    // cancelling its token is insufficient for a synchronous analyzer callback.
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"
    use release = new System.Threading.ManualResetEventSlim(false)
    let mutable started = 0
    let mutable active = 0
    let mutable maximumActive = 0

    let slowHook () =
        System.Threading.Interlocked.Increment(&started) |> ignore
        let nowActive = System.Threading.Interlocked.Increment(&active)

        let rec recordMaximum () =
            let observed = System.Threading.Volatile.Read(&maximumActive)

            if nowActive > observed then
                let prior =
                    System.Threading.Interlocked.CompareExchange(&maximumActive, nowActive, observed)

                if prior <> observed then
                    recordMaximum ()

        recordMaximum ()

        try
            release.Wait()
        finally
            System.Threading.Interlocked.Decrement(&active) |> ignore

        // The retained permit must be released even when timed-out work later
        // faults while unwinding.
        failwith "late analyzer failure"

    let handler =
        createWithSlowHook None [] (Some 1) DiagnosticSeverity.Hint (Some slowHook)

    host.RegisterHandler(handler)

    try
        host.EmitFileChecked(fakeResult "/tmp/slow/First.fs")
        host.EmitFileChecked(fakeResult "/tmp/slow/Second.fs")
        waitUntil (fun () -> System.Threading.Volatile.Read(&started) >= 1) 3000

        // The first timeout has fired by now. A token-ignoring synchronous
        // callback is still alive, so the second file must not start beside it.
        System.Threading.Thread.Sleep 1500
        Assert.Equal(1, System.Threading.Volatile.Read(&started))
        Assert.Equal(1, System.Threading.Volatile.Read(&maximumActive))

        // A queued next file is waiting behind the retained execution fence. It
        // must not overwrite the terminal timeout with a fresh Running status;
        // otherwise `check` appears silently busy forever while the timed-out
        // synchronous callback continues to consume CPU.
        match host.GetStatus("analyzers") with
        | Some(Failed(_, _, value)) -> Assert.Contains("timed out", value.Summary)
        | other -> Assert.Fail $"timeout must remain visible while the execution fence is held, got %A{other}"

        release.Set()
        waitUntil (fun () -> System.Threading.Volatile.Read(&started) >= 2) 5000
        Assert.Equal(1, System.Threading.Volatile.Read(&maximumActive))
    finally
        release.Set()
        waitUntil (fun () -> System.Threading.Volatile.Read(&active) = 0) 5000
        host.Teardown()

[<Fact(Timeout = 20000)>]
let ``analyzers skip compile items outside the repo`` () =
    let host = createModelHost (Unchecked.defaultof<_>) "/tmp"
    // The hook fires at the START of analysis, so it counts files actually analyzed.
    let mutable analyzedCount = 0

    let hook () =
        System.Threading.Interlocked.Increment(&analyzedCount) |> ignore

    let handler =
        createWithSlowHook (Some "/my/repo") [] None DiagnosticSeverity.Hint (Some hook)

    host.RegisterHandler(handler)

    // An out-of-repo NuGet-injected compile item: running FSharpLint over one of these
    // crashed the analyzer host, so it must be skipped before analysis.
    host.EmitFileChecked(
        fakeResult "/home/dev/.nuget/packages/xunit.v3.core.mtp-v1/3.2.2/_content/DefaultRunnerReporters.fs"
    )
    // The repo-owned file is emitted second and IS analyzed, which makes its terminal
    // status a sleep-free sync point: per-plugin events are serialized by the
    // MailboxProcessor, so by the time this one completes the skipped one has already
    // been dequeued and returned.
    host.EmitFileChecked(fakeResult "/my/repo/src/File.fs")
    waitForTerminalStatus host "analyzers" 15000

    // Exactly one — proves the guard fires AND doesn't kill analysis of real files.
    test <@ analyzedCount = 1 @>

// The micro-tests below run in microseconds; their Fact(Timeout) is only a backstop
// against a hang, which is why it is the suite's standard 15s rather than something
// tight. At 1s, JIT plus parallel-class contention was enough to cancel one outright.
[<Fact(Timeout = 15000)>]
let ``isKnownNonAnalyzerPrefix returns true when name has matching prefix`` () =
    test <@ isKnownNonAnalyzerPrefix [| "System."; "Microsoft." |] "System.Text.Json" @>

[<Fact(Timeout = 15000)>]
let ``isKnownNonAnalyzerPrefix returns false when no prefix matches`` () =
    test <@ not (isKnownNonAnalyzerPrefix [| "System."; "Microsoft." |] "ExampleAnalyzer") @>

[<Fact(Timeout = 15000)>]
let ``isKnownNonAnalyzerPrefix is case-sensitive`` () =
    // StringComparison.Ordinal — "system." does not match "System."
    test <@ not (isKnownNonAnalyzerPrefix [| "System." |] "system.text.json") @>

[<Fact(Timeout = 15000)>]
let ``isKnownNonAnalyzerPrefix with empty prefix array returns false`` () =
    test <@ not (isKnownNonAnalyzerPrefix [||] "System.Something") @>

[<Fact(Timeout = 15000)>]
let ``default knownNonAnalyzerPrefixes excludes common BCL assemblies`` () =
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "System.Collections" @>
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "Microsoft.Extensions.Logging" @>
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "FSharp.Core" @>

[<Fact(Timeout = 15000)>]
let ``default knownNonAnalyzerPrefixes does not match real analyzer packages`` () =
    test <@ not (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "ExampleAnalyzer") @>
    test <@ not (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "FSharpLint.Core") @>

[<Fact(Timeout = 15000)>]
let ``buildAnalyzerProjectOptions returns null when apoCtor is None`` () =
    // Matches the code path hit when the loaded SDK's AnalyzerProjectOptions
    // parameter type exposes no public constructors.
    let result = buildAnalyzerProjectOptions None (box 42)
    test <@ isNull result @>

type private FakeProjectOptions() =
    member val SourceFiles: string array = [| "Foo.fs" |] with get, set
    member val OtherOptions: string array = [||] with get, set
    member val ProjectFileName: string = "Fake.fsproj" with get, set

type private FailingCtorTarget(_v: int) = class end

[<Fact(Timeout = 15000)>]
let ``buildAnalyzerProjectOptions returns null when ctor invocation throws`` () =
    // A ctor that doesn't match the 7-arg shape throws at Invoke time; the helper must
    // swallow it and return null rather than take the plugin down.
    let ctor = typeof<FailingCtorTarget>.GetConstructors().[0]
    let result = buildAnalyzerProjectOptions (Some ctor) (FakeProjectOptions() :> obj)
    test <@ isNull result @>

[<Fact>]
let ``promoteIfFailing promotes hint to error with prefix when threshold is hint`` () =
    let entry =
        { Message = "some hint"
          Severity = DiagnosticSeverity.Hint
          Line = 1
          Column = 0
          Detail = None }

    let result = promoteIfFailing DiagnosticSeverity.Hint entry
    test <@ result.Severity = DiagnosticSeverity.Error @>
    test <@ result.Message = "[promoted from hint] some hint" @>

[<Fact>]
let ``promoteIfFailing promotes info to error with prefix when threshold is info`` () =
    let entry =
        { Message = "some info"
          Severity = DiagnosticSeverity.Info
          Line = 1
          Column = 0
          Detail = None }

    let result = promoteIfFailing DiagnosticSeverity.Info entry
    test <@ result.Severity = DiagnosticSeverity.Error @>
    test <@ result.Message = "[promoted from info] some info" @>

[<Fact>]
let ``promoteIfFailing leaves hint untouched when threshold is info`` () =
    let entry =
        { Message = "some hint"
          Severity = DiagnosticSeverity.Hint
          Line = 1
          Column = 0
          Detail = None }

    let result = promoteIfFailing DiagnosticSeverity.Info entry
    test <@ result.Severity = DiagnosticSeverity.Hint @>
    test <@ result.Message = "some hint" @>

[<Fact>]
let ``promoteIfFailing leaves error untouched regardless of threshold`` () =
    let entry =
        { Message = "an error"
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let result = promoteIfFailing DiagnosticSeverity.Hint entry
    test <@ result.Severity = DiagnosticSeverity.Error @>
    test <@ result.Message = "an error" @>

[<Fact>]
let ``promoteIfFailing leaves warning untouched when threshold is error`` () =
    let entry =
        { Message = "a warning"
          Severity = DiagnosticSeverity.Warning
          Line = 1
          Column = 0
          Detail = None }

    let result = promoteIfFailing DiagnosticSeverity.Error entry
    test <@ result.Severity = DiagnosticSeverity.Warning @>
    test <@ result.Message = "a warning" @>

// ---------------------------------------------------------------------------
// The analyzer summary count must be derived from the current cycle's live diagnostic
// map — the SAME set that gates the verdict — never a monotonic accumulator carried
// across cycles.
// ---------------------------------------------------------------------------

[<Fact>]
let ``summarize derives findings from the live map, not an accumulator`` () =
    let mkErr msg =
        { Message = msg
          Severity = DiagnosticSeverity.Error
          Line = 1
          Column = 0
          Detail = None }

    let mkWarn msg =
        { Message = msg
          Severity = DiagnosticSeverity.Warning
          Line = 1
          Column = 0
          Detail = None }

    let map =
        Map.ofList
            [ AbsFilePath.create "/tmp/A.fs", [ mkErr "e1"; mkWarn "w1" ]
              AbsFilePath.create "/tmp/B.fs", [ mkErr "e2" ] ]

    test
        <@
            summarize 2 0 "analyzer set x" map = "analyzed 2 files, replayed 0 from cache, 3 findings (2 errors, 1 warnings) — analyzer set x"
        @>

[<Fact>]
let ``summarize reads 0 findings when every file's entry is empty`` () =
    // A prior cycle's findings cleared (entry replaced with []) ⇒ 0, not stale N.
    let map =
        Map.ofList [ AbsFilePath.create "/tmp/A.fs", []; AbsFilePath.create "/tmp/B.fs", [] ]

    test
        <@
            summarize 2 0 "analyzer set x" map = "analyzed 2 files, replayed 0 from cache, 0 findings (0 errors, 0 warnings) — analyzer set x"
        @>

/// Recording PluginCtx: captures the run summaries carried by each terminal status, and
/// the per-file report/clear calls as a stand-in for the gated ledger.
let private makeAnalyzerRecordingCtx () =
    let summaries = System.Collections.Generic.List<string>()
    let ledger = System.Collections.Generic.Dictionary<string, ErrorEntry list>()

    let ctx: FsHotWatch.PluginFramework.PluginCtx<AnalyzersMsg> =
        { ReportStatus =
            fun status ->
                match status with
                | Completed(_, verdict) -> summaries.Add verdict.Summary
                | _ -> ()
          ReportErrors = fun file entries -> ledger.[file] <- entries
          ClearErrors = fun file -> ledger.Remove(file) |> ignore
          ClearAllErrors = fun () -> ledger.Clear()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = ""
          Post = fun _ -> ()
          EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          RunExclusiveShared = fun _ _ _ _ _ -> FsHotWatch.PluginFramework.SharedClaimed
          SlotHolder = fun _ -> FsHotWatch.PluginFramework.SlotHolder.Free
          DeclareBoundedWork = FsHotWatch.PluginFramework.BoundedWork.undeclared
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, summaries, ledger

let private nullCommandCtx: FsHotWatch.PluginFramework.CommandCtx<AnalyzersMsg> =
    { RepoRoot = ""
      Log = fun _ -> ()
      Post = fun _ -> ()
      EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
      IsRunning = fun _ -> false
      ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

[<Fact(Timeout = 15000)>]
let ``regression: clean cycle after a findings cycle renders 0, not the stale count`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let ctx, summaries, ledger = makeAnalyzerRecordingCtx ()

    let file = "/tmp/cycle/Phantom.fs"

    let findings =
        [ { Message = "rule X"
            Severity = DiagnosticSeverity.Error
            Line = 10
            Column = 1
            Detail = None }
          { Message = "rule Y"
            Severity = DiagnosticSeverity.Error
            Line = 20
            Column = 1
            Detail = None } ]

    let state1 =
        handler.Update ctx handler.Init (Custom(AnalysisComplete(file, findings)))
        |> Async.RunSynchronously

    test
        <@
            summaries |> Seq.last = $"analyzed 1 files, replayed 0 from cache, 2 findings (2 errors, 0 warnings) — %s{analyzerSetLabel (snapshotAnalyzerSet None knownNonAnalyzerPrefixes []).Inputs}"
        @>

    test <@ ledger.ContainsKey file && ledger.[file].Length = 2 @>

    // The same file re-checks clean.
    handler.Update ctx state1 (Custom(AnalysisComplete(file, [])))
    |> Async.RunSynchronously
    |> ignore

    // 0, not the stale 2 from the first cycle — and the ledger entry is cleared too.
    test
        <@
            summaries |> Seq.last = $"analyzed 2 files, replayed 0 from cache, 0 findings (0 errors, 0 warnings) — %s{analyzerSetLabel (snapshotAnalyzerSet None knownNonAnalyzerPrefixes []).Inputs}"
        @>

    test <@ not (ledger.ContainsKey file) @>

// Covers the NON-empty fold; the empty-state path is covered by "diagnostics command
// returns zeroes when no files checked". State is populated through a
// Custom(AnalysisComplete …) update so the summing lambda runs without an SDK load.
[<Fact(Timeout = 15000)>]
let ``diagnostics command sums findings across files in a populated state`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let ctx, _summaries, _ledger = makeAnalyzerRecordingCtx ()

    let findings =
        [ { Message = "rule X"
            Severity = DiagnosticSeverity.Error
            Line = 10
            Column = 1
            Detail = None }
          { Message = "rule Y"
            Severity = DiagnosticSeverity.Warning
            Line = 20
            Column = 1
            Detail = None } ]

    let populated =
        handler.Update ctx handler.Init (Custom(AnalysisComplete("/tmp/diag/Sum.fs", findings)))
        |> Async.RunSynchronously

    let (_, diagnosticsCmd) =
        handler.Commands |> List.find (fun (name, _) -> name = "diagnostics")

    let json =
        FsHotWatch.PluginFramework.PluginCommand.invoke diagnosticsCmd nullCommandCtx populated [||]
        |> Async.RunSynchronously

    test <@ json.Contains("\"diagnostics\":2") @>
    test <@ json.Contains("\"files\":1") @>

// --- CliContext.TypedTree supply ---

/// Type-check `source` through a checker built with the given retention, and hand
/// back the `FileCheckState` the analyzers plugin would receive for it, together
/// with the project options that produced it (the SDK's context needs both).
let private checkResultsWith (keepAssemblyContents: bool) (prefix: string) (source: string) =
    FsHotWatch.Tests.TestHelpers.withTempDir prefix (fun tmpDir ->
        let checker =
            FSharp.Compiler.CodeAnalysis.FSharpChecker.Create(keepAssemblyContents = keepAssemblyContents)

        let file = IO.Path.Combine(tmpDir, "Typed.fsx")
        IO.File.WriteAllText(file, source)
        let sourceText = FSharp.Compiler.Text.SourceText.ofString source

        let options, _ =
            checker.GetProjectOptionsFromScript(file, sourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously

        let parseResults, answer =
            checker.ParseAndCheckFileInProject(file, 0, sourceText, options)
            |> Async.RunSynchronously

        match answer with
        | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Aborted ->
            failwith "FCS aborted the check, so this fixture would prove nothing"
        | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Succeeded results ->
            FullCheck results, options, parseResults)

let private asTypedTree (boxed: obj) =
    boxed :?> FSharp.Compiler.Symbols.FSharpImplementationFileContents option

[<Fact(Timeout = 60000)>]
let ``typedTreeOf supplies the typed tree when the checker retained it`` () =
    // The capability an analyzer walks. A rule that needs typed information and
    // receives `None` reports nothing rather than failing, so this is the assertion
    // standing between a live typed-tree rule and a silently disarmed one.
    let checkResults, _, _ =
        checkResultsWith true "typedtree-retained" "module Typed\nlet answer = 42\n"

    test <@ typedTreeOf (TypedTreeLatch()) checkResults |> asTypedTree |> Option.isSome @>

[<Fact(Timeout = 15000)>]
let ``typedTreeOf yields None for a parse-only result`` () =
    // No type-check happened, so there is no typed tree to offer.
    test <@ typedTreeOf (TypedTreeLatch()) ParseOnly |> asTypedTree = None @>

[<Fact(Timeout = 60000)>]
let ``typedTreeOf yields None rather than raising when the checker kept no contents`` () =
    // FCS RAISES on `ImplementationFile` when the checker was built without
    // `keepAssemblyContents`. A host configured that way must still analyze — the
    // typed-tree rules go quiet, every other rule keeps running — so the access is
    // guarded rather than allowed to take the analyzer stage down.
    let checkResults, _, _ =
        checkResultsWith false "typedtree-not-retained" "module Typed\nlet answer = 42\n"

    test <@ typedTreeOf (TypedTreeLatch()) checkResults |> asTypedTree = None @>

[<Fact(Timeout = 60000)>]
let ``a withheld latch withholds the typed tree, and only for its own handler`` () =
    // Two sessions in one host each load their own analyzer set, so one set proving it
    // cannot take a typed tree must not quietly disarm the other's typed-tree rules.
    let checkResults, _, _ =
        checkResultsWith true "typedtree-latch" "module Typed\nlet answer = 42\n"

    let sessionA = TypedTreeLatch()
    let sessionB = TypedTreeLatch()
    test <@ sessionA.Withhold() @>
    test <@ not (sessionA.Withhold()) @>
    test <@ typedTreeOf sessionA checkResults |> asTypedTree = None @>
    test <@ typedTreeOf sessionB checkResults |> asTypedTree |> Option.isSome @>

[<Fact(Timeout = 60000)>]
let ``createCliContext carries a real typed tree through the reflection constructor`` () =
    // THE positive control for this capability. The CliContext constructor is invoked
    // by REFLECTION to work around an FCS version mismatch, so a typed tree that
    // `typedTreeOf` produces correctly could still be rejected at `ctor.Invoke` by an
    // assembly-identity mismatch. Only driving a genuine
    // `FSharpImplementationFileContents` all the way through proves analyzers actually
    // receive one; asserting on `typedTreeOf` alone would not.
    let checkResults, options, _ =
        checkResultsWith true "clicontext-typedtree" "module Typed\nlet answer = 42\n"

    let checkResultsObj =
        match checkResults with
        | FullCheck cr -> box cr
        | ParseOnly -> null

    let context =
        createCliContext
            (box "Typed.fsx")
            (box (FSharp.Compiler.Text.SourceText.ofString "module Typed\nlet answer = 42\n"))
            (box (dummyParseResults ()))
            checkResultsObj
            (typedTreeOf (TypedTreeLatch()) checkResults)
            (box options)

    test <@ context.TypedTree |> Option.isSome @>

/// The g-research analyzer set, from the package this test project references.
/// Resolved rather than assumed: if it is not there the test FAILS, because a
/// silently-empty analyzer set would make the comparison below pass for the wrong
/// reason — the exact shape of defect this whole change is about.
let private gResearchAnalyzerDir =
    let root =
        Environment.GetEnvironmentVariable "NUGET_PACKAGES"
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            IO.Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages"))

    IO.Path.Combine(root, "g-research.fsharp.analyzers", "0.23.0", "analyzers", "dotnet", "fs")

[<Fact(Timeout = 120000)>]
let ``the configured analyzer set cannot walk a typed tree from this FCS`` () =
    // WHY the host withholds the typed tree after a mismatch, measured rather than
    // assumed. g-research 0.23.0 is compiled against an older FSharp.Compiler.Service;
    // while `TypedTree` is `None` its analyzers return before touching the differing
    // types, and handed a real typed tree they raise
    // `Method not found: FSharp.Compiler.Symbols.FSharpType.get_BasicQualifiedName()`.
    //
    // If the analyzer packages are ever rebuilt against this FCS this test FAILS, which
    // is the intended signal: the degradation in `AnalyzersPlugin` is then dead weight
    // and typed-tree rules can be armed for real.
    Assert.True(IO.Directory.Exists gResearchAnalyzerDir, $"analyzer package missing at {gResearchAnalyzerDir}")

    let source = "module Probe\nlet f (s: string) = s.StartsWith(\"a\")\n"

    let checkResults, options, parseResults =
        checkResultsWith true "typedtree-differential" source

    let checkResultsObj =
        match checkResults with
        | FullCheck cr -> box cr
        | ParseOnly -> null

    // NOT `typedTreeOf`: the real tree is wanted even if a latch would withhold it.
    let realTypedTree =
        match checkResults with
        | FullCheck cr -> box cr.ImplementationFile
        | ParseOnly -> noTypedTree

    let client =
        FSharp.Analyzers.SDK.Client<FSharp.Analyzers.SDK.CliAnalyzerAttribute, FSharp.Analyzers.SDK.CliContext>()

    let loaded = client.LoadAnalyzers gResearchAnalyzerDir
    Assert.True(loaded.Analyzers > 0, "no analyzers loaded — every assertion below would be vacuous")

    let failuresFor (typedTree: obj) =
        let context =
            createCliContext
                (box "Probe.fsx")
                (box (FSharp.Compiler.Text.SourceText.ofString source))
                (box parseResults)
                checkResultsObj
                typedTree
                (box options)

        client.RunAnalyzersSafely context
        |> Async.RunSynchronously
        |> List.choose (fun r ->
            match r.Output with
            | Result.Ok _ -> None
            | Result.Error ex -> Some ex)

    // Control: withholding the typed tree is the state the host ran in before this
    // change, and nothing fails there.
    test <@ failuresFor noTypedTree |> List.isEmpty @>

    let withTypedTree = failuresFor realTypedTree
    test <@ not (List.isEmpty withTypedTree) @>
    test <@ withTypedTree |> List.forall isFcsBinaryMismatch @>

[<Fact>]
let ``isFcsBinaryMismatch names the assembly mismatch and nothing else`` () =
    // The predicate decides whether a failure is the HOST's problem (an analyzer built
    // against another FCS) or the analyzer's own bug. Misclassifying the second as the
    // first would silently withhold typed trees from a set that could use them.
    test <@ isFcsBinaryMismatch (MissingMethodException "get_BasicQualifiedName") @>
    test <@ isFcsBinaryMismatch (TypeLoadException "FSharpType") @>
    test <@ not (isFcsBinaryMismatch (InvalidOperationException "analyzer bug")) @>
    test <@ not (isFcsBinaryMismatch (exn "boom")) @>

// A rediscovery that drops a file clears that file's findings in EVERY plugin ledger
// (`rediscoverAndClearRemoved` -> `ClearFileEverywhere`), and it clears them while the
// discovery coordinator is still `Rediscovering` — before the replacement model is
// published. A `FileChecked` captured under the OLD model and still sitting in the
// analyzers mailbox is folded after that clear, and nothing checks a dropped path
// again: the finding it re-reports is about a file outside the build and stands until
// the daemon restarts. So the fold must refuse what the current model did not stamp.
[<Fact(Timeout = 20000)>]
let ``analyzers refuse a FileChecked captured against a superseded model`` () =
    let repoRoot = "/my/repo"
    let removed = "/my/repo/src/Removed.fs"
    let present = "/my/repo/src/Present.fs"

    // Publishes the fixture model (generation 1) — the model both results below were
    // captured under.
    let host = createModelHost (Unchecked.defaultof<_>) repoRoot

    // A throwing hook is the cheapest analyzer that produces a FINDING without loading
    // a real analyzer assembly: the crash path reports one entry against the file, so
    // the ledger shows exactly what a superseded fold would have re-added.
    let mutable analyzedCount = 0

    let hook () =
        Threading.Interlocked.Increment(&analyzedCount) |> ignore
        failwith "analyzer boom"

    let handler =
        createWithSlowHook (Some repoRoot) [] None DiagnosticSeverity.Hint (Some hook)

    host.RegisterHandler(handler)

    // The rediscovery lands: generation 2 no longer has Removed.fs, and the host has
    // already cleared its findings.
    host.WorkStore.PublishProjectModel(fixtureModelOf 2L)
    host.ClearFileEverywhere(removed)

    // Queued under generation 1 — the model that still had the file.
    host.EmitFileChecked(fakeResult removed)

    // Positive control, stamped with the generation now in force. Emitted second, so
    // its terminal status is a sleep-free sync point: per-plugin events are serialized
    // by the MailboxProcessor, so the stale one was dequeued before this one completes.
    host.EmitFileChecked(
        { fakeResult present with
            ModelGeneration = Some 2L }
    )

    waitForTerminalStatus host "analyzers" 15000

    // The superseded result was never analyzed...
    let analyzed = Threading.Volatile.Read(&analyzedCount)
    test <@ analyzed = 1 @>

    let errors = host.GetErrorsByPlugin("analyzers")

    // ...so no finding was re-reported for the file the new model dropped...
    test <@ errors |> Map.containsKey removed |> not @>
    // ...and the current-generation result still reports its findings.
    test <@ (errors |> Map.tryFind present |> Option.map List.length) = Some 1 @>

// A result captured while no model was observable describes no model, so it cannot
// describe the one in force either.
[<Fact(Timeout = 20000)>]
let ``analyzers refuse a FileChecked captured against no model`` () =
    let repoRoot = "/my/repo"
    let unmodelled = "/my/repo/src/Unmodelled.fs"
    let present = "/my/repo/src/Present.fs"
    let host = createModelHost (Unchecked.defaultof<_>) repoRoot
    let mutable analyzedCount = 0

    let hook () =
        Threading.Interlocked.Increment(&analyzedCount) |> ignore
        failwith "analyzer boom"

    let handler =
        createWithSlowHook (Some repoRoot) [] None DiagnosticSeverity.Hint (Some hook)

    host.RegisterHandler(handler)

    host.EmitFileChecked(
        { fakeResult unmodelled with
            ModelGeneration = None }
    )

    // Positive control, stamped with the model in force, emitted second as the sync point.
    host.EmitFileChecked(fakeResult present)
    waitForTerminalStatus host "analyzers" 15000

    let analyzed = Threading.Volatile.Read(&analyzedCount)
    test <@ analyzed = 1 @>
    let errors = host.GetErrorsByPlugin("analyzers")
    test <@ errors |> Map.containsKey unmodelled |> not @>
    test <@ (errors |> Map.tryFind present |> Option.map List.length) = Some 1 @>

// ---------------------------------------------------------------------------
// Evidence, not just a result. `0 findings (cached)` read the same whether the stage
// examined every file or replayed every file from cache, and named no analyzer set.
// The summary must say how many files were examined, how many were replayed, and —
// on the path that ran the analyzers — which analyzer set produced the findings.
// ---------------------------------------------------------------------------

/// The label the summary names the analyzer set by: the head of the `analyzer-inputs`
/// cache-key slot, so the rendered name and the key are one value.
let private setLabelOf (dirs: string list) =
    analyzerSetLabel (snapshotAnalyzerSet None knownNonAnalyzerPrefixes dirs).Inputs

[<Fact>]
let ``analyzerSetLabel names an identified set by the head of its cache-key slot`` () =
    let key = String.replicate 8 "0123456789abcdef"
    test <@ analyzerSetLabel (Result.Ok key) = "analyzer set 0123456789ab" @>

[<Fact>]
let ``analyzerSetLabel says so when the set has no identity and the cache is off`` () =
    let refused: Result<string, FsHotWatch.Analyzers.AnalyzerIdentity.Refusal list> =
        Result.Error [ FsHotWatch.Analyzers.AnalyzerIdentity.Refusal.MissingPdb "/x/Rules.dll" ]

    test <@ analyzerSetLabel refused = "analyzer set unidentified (cache off)" @>

[<Fact>]
let ``analyzerSetLabel names a key shorter than its head in full`` () =
    test <@ analyzerSetLabel (Result.Ok "0123abc") = "analyzer set 0123abc" @>

[<Fact(Timeout = 15000)>]
let ``a summary counts the files replayed from cache between the files it analyzed`` () =
    // The framework computes the key, then either replays (Update never runs) or runs
    // Update. A keyed event the handler never saw was replayed; the handler counts it.
    let handler = create None [] None DiagnosticSeverity.Hint
    let ctx, summaries, _ledger = makeAnalyzerRecordingCtx ()
    let keyOf = handler.CacheKey.Value handler.Init

    test <@ (keyOf (FileChecked(fakeResult "/tmp/evidence/Replayed1.fs"))).IsSome @>
    test <@ (keyOf (FileChecked(fakeResult "/tmp/evidence/Replayed2.fs"))).IsSome @>

    let analyzedMsg = Custom(AnalysisComplete("/tmp/evidence/Analyzed.fs", []))

    test <@ (keyOf analyzedMsg).IsNone @>

    handler.Update ctx handler.Init analyzedMsg |> Async.RunSynchronously |> ignore

    let expected =
        $"analyzed 1 files, replayed 2 from cache, 0 findings (0 errors, 0 warnings) — %s{setLabelOf []}"

    test <@ summaries |> Seq.last = expected @>

[<Fact(Timeout = 30000)>]
let ``a replay-only run says it examined nothing; one that analyzed says how many`` () =
    // The ticket's shape: every file served from cache. The summary must not read like a
    // stage that examined the tree.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let seed file =
        let result = fakeResult file
        let key = ((handler.CacheKey.Value handler.Init) (FileChecked result)).Value

        cacheIface.Set
            { Plugin = "analyzers"
              File = Some(compositeFileKey "/tmp" file) }
            key
            { CacheKey = key
              Errors = [ file, [] ]
              Status = FsHotWatch.TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 5.0)
              EmittedEvents = [] }

        result

    let a = seed "/tmp/evidence/A.fs"
    let b = seed "/tmp/evidence/B.fs"

    host.EmitFileChecked a
    host.EmitFileChecked b
    waitForQuiescent host 15000

    let summary () =
        match host.GetStatus "analyzers" with
        | Some(Completed(_, v)) -> v.Summary
        | Some(Failed(_, _, v)) -> v.Summary
        | other -> failwith $"expected a terminal analyzers status, got %A{other}"

    test <@ summary () = "0 findings (0 errors, 0 warnings); 0 files examined, 2 replayed from cache (cached)" @>

    // A file with no cache entry runs the handler (it crashes on the null parse
    // results — still an examination), then a replay reports both tallies.
    host.EmitFileChecked(fakeResult "/tmp/evidence/Fresh.fs")
    waitForQuiescent host 15000
    host.EmitFileChecked a
    waitForQuiescent host 15000

    test <@ (summary ()).EndsWith("; 1 files examined, 3 replayed from cache (cached)") @>

[<Fact(Timeout = 30000)>]
let ``a run's summary counts only that run's files, not every file since start`` () =
    // A run is the cohort of `FileChecked` events a `BatchChecked` closes. A rescan that
    // replays both files examined nothing, whatever earlier runs examined.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)
    host.WorkStore.PublishProjectModel fixtureModel

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let seed file =
        let result = fakeResult file
        let key = ((handler.CacheKey.Value handler.Init) (FileChecked result)).Value

        cacheIface.Set
            { Plugin = "analyzers"
              File = Some(compositeFileKey "/tmp" file) }
            key
            { CacheKey = key
              Errors = [ file, [] ]
              Status = FsHotWatch.TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 5.0)
              EmittedEvents = [] }

        result

    let a = seed "/tmp/evidence/A.fs"
    let b = seed "/tmp/evidence/B.fs"

    let summary () =
        match host.GetStatus "analyzers" with
        | Some(Completed(_, v)) -> v.Summary
        | Some(Failed(_, _, v)) -> v.Summary
        | other -> failwith $"expected a terminal analyzers status, got %A{other}"

    // First run: one file examined (no cache entry), then both seeded files replayed.
    host.EmitFileChecked(fakeResult "/tmp/evidence/Fresh.fs")
    host.EmitFileChecked a
    host.EmitFileChecked b
    host.EmitBatchChecked(fakeBatchChecked [ "/tmp/evidence/Fresh.fs"; "/tmp/evidence/A.fs"; "/tmp/evidence/B.fs" ])
    waitForQuiescent host 15000

    test <@ (summary ()).EndsWith("; 1 files examined, 2 replayed from cache (cached)") @>

    // Second run: a rescan that replays both files and examines none.
    host.EmitFileChecked a
    host.EmitFileChecked b
    host.EmitBatchChecked(fakeBatchChecked [ "/tmp/evidence/A.fs"; "/tmp/evidence/B.fs" ])
    waitForQuiescent host 15000

    test <@ (summary ()).EndsWith("; 0 files examined, 2 replayed from cache (cached)") @>
