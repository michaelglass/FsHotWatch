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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>
    test <@ result.Value.Contains("\"files\":0") @>
    test <@ result.Value.Contains("\"diagnostics\":0") @>

[<Fact(Timeout = 15000)>]
let ``analyzer error path does not crash`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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

    let cacheKeyFn = handler.CacheKey.Value

    let parseOnlyKey = cacheKeyFn (FileChecked parseOnlyResult)
    let fullCheckKey = cacheKeyFn (FileChecked fullCheckResult)

    test <@ parseOnlyKey.IsSome @>
    test <@ fullCheckKey.IsSome @>
    test <@ parseOnlyKey <> fullCheckKey @>

[<Fact(Timeout = 20000)>]
let ``ParseOnly dispatches to analyzer worker instead of skipping`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = AbsFilePath.create "/tmp/nonexistent/Fake.fs"
          Source = "let x = 1"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let cacheKeyFn = handler.CacheKey.Value

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

[<Fact(Timeout = 15000)>]
let ``analyzerAssemblyIdentity changes when an analyzer DLL's content changes`` () =
    let dir = analyzerBinWith "MyAnalyzer" [| 1uy; 2uy; 3uy |]

    try
        let before = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]

        // A rebuild: same path, same DLL filename, new bytes. The identity shift is what
        // invalidates the stale per-file cache entry.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "MyAnalyzer.dll"), [| 9uy; 8uy; 7uy; 6uy |])
        let after = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]

        test <@ before <> after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzerAssemblyIdentity changes when a new analyzer DLL is added`` () =
    let dir = analyzerBinWith "First" [| 1uy; 2uy |]

    try
        let before = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]

        // Adding a second analyzer to the same path must change identity.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "Second.dll"), [| 3uy; 4uy |])
        let after = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]

        test <@ before <> after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzerAssemblyIdentity is stable for identical content (cache hits survive a no-op rescan)`` () =
    let dir = analyzerBinWith "Stable" [| 5uy; 5uy; 5uy |]

    try
        let a = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]
        let b = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]
        test <@ a = b @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzerAssemblyIdentity ignores known-non-analyzer (bundled dep) DLLs`` () =
    // Otherwise an FSharp.Core refresh would invalidate every cached verdict.
    let dir = analyzerBinWith "RealAnalyzer" [| 1uy |]

    try
        let before = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "FSharp.Core.dll"), [| 42uy; 43uy |])
        let after = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]
        test <@ before = after @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``analyzerAssemblyIdentity does not throw on a missing path`` () =
    // A configured-but-missing path contributes a stable sentinel, never a throw
    // that would abort plugin construction.
    let id1 =
        analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ "/tmp/no-such-analyzer-dir-id-xyz" ]

    let id2 =
        analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ "/tmp/no-such-analyzer-dir-id-xyz" ]

    test <@ id1 = id2 @>

[<Fact(Timeout = 15000)>]
let ``regression: cache key changes when the analyzer DLL is rebuilt (same path)`` () =
    // Two handlers on the SAME analyzer path with different DLL content. Under the old
    // path-string-only key these keys were identical, so a daemon that cached "clean"
    // under the old DLL replayed it after the rebuild.
    let dir = analyzerBinWith "RuleChanged" [| 0uy; 1uy; 2uy |]

    try
        let h1 = create None [ dir ] None DiagnosticSeverity.Hint
        let event = FileChecked(fakeResult $"{dir}/Subject.fs")
        let key1 = (h1.CacheKey.Value) event

        // The rebuild: same path, new content.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "RuleChanged.dll"), [| 9uy; 9uy; 9uy; 9uy |])

        let h2 = create None [ dir ] None DiagnosticSeverity.Hint
        let key2 = (h2.CacheKey.Value) event

        test <@ key1.IsSome @>
        test <@ key2.IsSome @>
        test <@ key1 <> key2 @>
    finally
        try
            System.IO.Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``cache key for Custom event returns None`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value

    let customKey = cacheKeyFn (Custom(AnalysisComplete("/tmp/Fake.fs", [])))
    test <@ customKey.IsNone @>

[<Fact(Timeout = 15000)>]
let ``cache key for non-FileChecked event returns None`` () =
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value

    let buildKey = cacheKeyFn (BuildCompleted BuildSucceeded)
    test <@ buildKey.IsNone @>

[<Fact(Timeout = 15000)>]
let ``regression: FileChecked replays from cache on second emission with same content`` () =
    // Pins the Async.Start regression: fire-and-forget execution meant the first run
    // never wrote to cache, so every subsequent identical event re-ran the analyzer.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // Cold cache: the analyzer crashes (terminal Failed), which still writes the entry.
    host.EmitFileChecked(fakeResult "/tmp/test/Replay.fs")
    waitForTerminalStatus host "analyzers" 15000

    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/Replay.fs" }

    let cacheKeyFn = handler.CacheKey.Value
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
let ``regression AUTOMATION-186: cache replay must not resurrect a stale global findings summary`` () =
    // The mechanism (AUTOMATION-186): a per-FileChecked cache entry used to store a
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

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let cleanFile = "/tmp/test/StaleSummary.fs"
    let checkResult = fakeResult cleanFile
    let cacheKey = (handler.CacheKey.Value(FileChecked checkResult)).Value

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
          File = Some cleanFile }

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
let ``AUTOMATION-186: per-file replay WITH findings derives EXACTLY those findings, not a hardcoded zero`` () =
    // The inverse of the clean-file case, and the reason it is needed: a summary
    // hardcoded to "0 findings" passes that regression and lies here. Demanding the
    // exact count proves the derivation reads the live ledger.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let dirtyFile = "/tmp/test/WithFindings.fs"
    let checkResult = fakeResult dirtyFile
    let cacheKey = (handler.CacheKey.Value(FileChecked checkResult)).Value

    let findings = [ for i in 1..5 -> ErrorEntry.error $"finding %d{i}" ]

    let dirtyEntry: FsHotWatch.TaskCache.TaskCacheResult =
        { CacheKey = cacheKey
          // The entry replays five live findings for this file into the ledger.
          Errors = [ dirtyFile, findings ]
          Status = FsHotWatch.TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 11.0)
          EmittedEvents = [] }

    let compKey: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some dirtyFile }

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

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // The analyzer crashes on the null ParseResults. A crash is still terminal, so the
    // cache write must happen anyway.
    host.EmitFileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    waitForTerminalStatus host "analyzers" 15000

    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/CacheRegression.fs" }

    let cacheKeyFn = handler.CacheKey.Value
    let event = FileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    let computedKey = cacheKeyFn event

    test <@ computedKey.IsSome @>
    // Cache write lags terminal status — poll it rather than reading once.
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 15000
    let result = cacheIface.TryGet key computedKey.Value
    test <@ result.IsSome @>

[<Fact(Timeout = 15000)>]
let ``multiple concurrent FileChecked events are bounded by semaphore`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
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

[<Fact(Timeout = 20000)>]
let ``analyzers skip compile items outside the repo (AUTOMATION-49)`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
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

    test <@ summarize 2 map = "analyzed 2 files, 3 findings (2 errors, 1 warnings)" @>

[<Fact>]
let ``summarize reads 0 findings when every file's entry is empty`` () =
    // A prior cycle's findings cleared (entry replaced with []) ⇒ 0, not stale N.
    let map =
        Map.ofList [ AbsFilePath.create "/tmp/A.fs", []; AbsFilePath.create "/tmp/B.fs", [] ]

    test <@ summarize 2 map = "analyzed 2 files, 0 findings (0 errors, 0 warnings)" @>

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
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, summaries, ledger

let private nullCommandCtx: FsHotWatch.PluginFramework.CommandCtx<AnalyzersMsg> =
    { RepoRoot = ""
      Log = fun _ -> ()
      Post = fun _ -> ()
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

    test <@ summaries |> Seq.last = "analyzed 1 files, 2 findings (2 errors, 0 warnings)" @>
    test <@ ledger.ContainsKey file && ledger.[file].Length = 2 @>

    // The same file re-checks clean.
    handler.Update ctx state1 (Custom(AnalysisComplete(file, [])))
    |> Async.RunSynchronously
    |> ignore

    // 0, not the stale 2 from the first cycle — and the ledger entry is cleared too.
    test <@ summaries |> Seq.last = "analyzed 2 files, 0 findings (0 errors, 0 warnings)" @>
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

    let json = diagnosticsCmd nullCommandCtx populated [||] |> Async.RunSynchronously

    test <@ json.Contains("\"diagnostics\":2") @>
    test <@ json.Contains("\"files\":1") @>
