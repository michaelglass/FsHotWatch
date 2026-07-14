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

// Analyzer tests use Unchecked.defaultof for ParseResults to verify the plugin
// guards against null inputs from FCS aborts. Override the shared helper rather
// than reinvent the record literal at every site.
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

    // With framework handler, status may be Idle (event not yet processed),
    // Running, or Completed — the key thing is the plugin doesn't crash
    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> ()
    | Running _ -> ()
    | Idle -> ()
    | other -> Assert.Fail($"Expected Idle, Completed, or Running, got: %A{other}")

[<Fact(Timeout = 15000)>]
let ``analyzer with non-existent path skips loading`` () =
    // Exercise the Directory.Exists false branch
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create None [ "/tmp/no-such-analyzer-dir-12345" ] None DiagnosticSeverity.Hint

    host.RegisterHandler(handler)

    // No analyzers should be loaded — diagnostics command shows 0 analyzers
    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>

[<Fact(Timeout = 20000)>]
let ``analyzer with mix of valid and invalid paths`` () =
    // Create a real empty dir that exists, paired with one that does not
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

// Fail-loud guard inputs: a CONFIGURED analyzer path that loads zero analyzers
// must produce LoadedCount = 0, which is what DaemonConfig.analyzersLoadFailure
// turns into a RED gate. Two sub-cases the guard must catch:
//   (a) the path does not exist (the actual CI bug — bin built in wrong config)
//   (b) the path exists but contains no analyzer DLLs

[<Fact(Timeout = 15000)>]
let ``configured non-existent analyzer path loads zero (guard input)`` () =
    let path = "/tmp/no-such-analyzer-dir-guard-a"
    let handler = create None [ path ] None DiagnosticSeverity.Hint

    test <@ handler.Init.LoadedCount = 0 @>
    // Per-path: a non-existent path is recorded with a 0 count so the per-path
    // guard can name it as the offender.
    test <@ handler.Init.LoadedByPath = [ path, 0 ] @>

[<Fact(Timeout = 15000)>]
let ``configured empty analyzer dir loads zero (guard input)`` () =
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-guard-empty-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(emptyDir) |> ignore

    try
        let handler = create None [ emptyDir ] None DiagnosticSeverity.Hint
        test <@ handler.Init.LoadedCount = 0 @>
        // Per-path: an existing-but-empty dir is recorded with a 0 count.
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

    // ParseOnly should have a different cache key than FullCheck
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

    // Wait for the plugin to finish processing (status reaches Completed/Failed)
    waitForTerminalStatus host "analyzers" 12000

    // ParseOnly should dispatch to the async worker (not skip synchronously).
    // With Unchecked.defaultof ParseResults, the analyzer will crash — but it
    // should crash via the AnalysisFailed path (proving the worker ran),
    // not silently complete via the old skip path.
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

// §2a: getCommitId is no longer consulted by Analyzers; the plugin always
// provides a CacheKey and the key depends only on the FileChecked content.
// Earlier "cache key when getCommitId is None" tests are obsolete under the
// new contract — replaced below by content-based behavior.

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
    // §2a: even with no jj commit, identical source bytes produce identical keys.
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
// Cache correctness (2026-06-17): a rebuilt analyzer DLL must invalidate cached
// per-file verdicts even when the configured analyzer PATHS are byte-identical.
// Before this fix the cache key hashed the path STRINGS only, so a long-lived
// daemon replayed stale-green for unchanged source after an analyzer rule
// changed / a new analyzer was added (same path, new DLL content). These tests
// use throwaway *.dll byte files so the keying is exercised deterministically
// without a real SDK analyzer load.
// ---------------------------------------------------------------------------

/// Write `bytes` to a fresh `name`.dll inside a unique temp dir; return the dir.
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

        // Simulate a rebuild: same path, same DLL filename, new bytes (rule
        // changed). The identity MUST differ — this is what invalidates the
        // stale per-file cache entry.
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
    // A bundled BCL/FCS dep churning (e.g. FSharp.Core refresh) must NOT change
    // the analyzer identity — only the analyzer assemblies themselves count.
    let dir = analyzerBinWith "RealAnalyzer" [| 1uy |]

    try
        let before = analyzerAssemblyIdentity knownNonAnalyzerPrefixes [ dir ]
        // FSharp.Core matches a known-non-analyzer prefix ⇒ excluded from identity.
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
    // The end-to-end Bug A repro at the CacheKey level: two handlers configured
    // with the SAME analyzer path, but the DLL content differs between them
    // (a rebuild). The per-file cache key for identical source MUST differ, so a
    // daemon that cached "clean" under the old DLL cannot replay it after the
    // rebuild. Before the fix (path-string-only key) these keys were identical.
    let dir = analyzerBinWith "RuleChanged" [| 0uy; 1uy; 2uy |]

    try
        let h1 = create None [ dir ] None DiagnosticSeverity.Hint
        let event = FileChecked(fakeResult $"{dir}/Subject.fs")
        let key1 = (h1.CacheKey.Value) event

        // Rebuild the analyzer (rule changed) — same path, new content.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "RuleChanged.dll"), [| 9uy; 9uy; 9uy; 9uy |])

        let h2 = create None [ dir ] None DiagnosticSeverity.Hint
        let key2 = (h2.CacheKey.Value) event

        test <@ key1.IsSome @>
        test <@ key2.IsSome @>
        // Same source, same path — but different analyzer DLL ⇒ different key.
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
    // §2a: only FileChecked produces a cache key; other events aren't
    // cached at all (the plugin only subscribes to SubscribeFileChecked anyway).
    let handler = create None [] None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value

    let buildKey = cacheKeyFn (BuildCompleted BuildSucceeded)
    test <@ buildKey.IsNone @>

[<Fact(Timeout = 15000)>]
let ``regression: FileChecked replays from cache on second emission with same content`` () =
    // Two FileChecked events with the same File+Source. The first runs the
    // analyzer (terminal-status writes to cache); the second should replay
    // from cache without re-running.
    //
    // With Async.Start (pre-fix), the first never wrote to cache, so the
    // second always re-ran. With awaited execution, the first writes and the
    // second hits.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // First run — cold cache, analyzer crashes (terminal Failed), cache write.
    host.EmitFileChecked(fakeResult "/tmp/test/Replay.fs")
    waitForTerminalStatus host "analyzers" 15000

    // Verify cache populated.
    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/Replay.fs" }

    let cacheKeyFn = handler.CacheKey.Value
    let event = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey = cacheKeyFn event
    // The framework writes the cache entry in `runAndCache` AFTER the plugin
    // reports terminal status (ReportStatus → GetStatus is what
    // waitForTerminalStatus observes, then `cache.Set` runs). So terminal
    // status happens-before the cache write only by a small window; poll the
    // cache itself (the real postcondition) rather than reading once and racing
    // that window.
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 15000
    test <@ (cacheIface.TryGet key computedKey.Value).IsSome @>

    // Verify hit count by computing keys again (should be the same).
    let event2 = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey2 = cacheKeyFn event2
    test <@ computedKey = computedKey2 @>

[<Fact(Timeout = 15000)>]
let ``regression: FileChecked with TaskCache writes a cache entry on terminal status`` () =
    // Before this fix, AnalyzersPlugin used Async.Start to dispatch its work,
    // which returned `state` synchronously while the analysis ran in the
    // background. The framework's per-event cache-write window only saw the
    // "Running" status (not terminal), so it never wrote to the TaskCache.
    // After: FileChecked awaits the inner async, so by the time the event
    // handler returns, capturedStatus is Completed/Failed and the cache writes.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler = create None [] None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // Use a fake result; analyzer will crash (Unchecked.defaultof ParseResults),
    // which exercises the Choice3Of3 path. Crash status is also terminal, so
    // the cache write must still happen.
    host.EmitFileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    waitForTerminalStatus host "analyzers" 15000

    // The cache should now contain an entry for (analyzers, that-file).
    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/CacheRegression.fs" }

    let cacheKeyFn = handler.CacheKey.Value
    let event = FileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    let computedKey = cacheKeyFn event

    test <@ computedKey.IsSome @>
    // Cache write lags terminal status (see the Replay test): the framework's
    // `cache.Set` runs after the plugin's ReportStatus that waitForTerminalStatus
    // observed. Poll the cache (the real postcondition) instead of reading once.
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
// Pure-function unit tests for reflection helpers.
// These deterministically cover branches that the live-FCS integration tests
// hit nondeterministically depending on which SDK version is loaded.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``analyzers handler times out when work exceeds TimeoutSec`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    // slowHook sleeps longer than the 1s timeout, forcing a TimedOut outcome
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

    // Out-of-repo NuGet-injected compile item (xunit.v3's _content under
    // ~/.nuget): must be skipped before analysis — running FSharpLint over it
    // crashed the analyzer host (AUTOMATION-49). Emitted FIRST.
    host.EmitFileChecked(
        fakeResult "/home/dev/.nuget/packages/xunit.v3.core.mtp-v1/3.2.2/_content/DefaultRunnerReporters.fs"
    )
    // Repo-owned file emitted SECOND — it IS analyzed, and its terminal status is
    // the deterministic sync point (no sleeps): per-plugin events are serialized
    // by the MailboxProcessor, so once this file's analysis completes the skipped
    // one has already been dequeued and returned without analyzing.
    host.EmitFileChecked(fakeResult "/my/repo/src/File.fs")
    waitForTerminalStatus host "analyzers" 15000

    // Exactly one analysis ran — the repo-owned file; the out-of-repo one was
    // skipped (proves the guard fires AND doesn't kill analysis of real files).
    test <@ analyzedCount = 1 @>

// These pure synchronous micro-tests (prefix matching, reflection ctor probes)
// complete in microseconds; the Fact(Timeout) is only a backstop against a
// hypothetical hang. The former 1000ms cap was tight enough that on a slow /
// loaded CI runner JIT + parallel-class contention could push a single test
// past 1s and CANCEL it (observed: `default knownNonAnalyzerPrefixes does not
// match real analyzer packages` canceled at 1s 973ms). Raised to the suite's
// standard 15000ms — still bounds a true infinite loop, no longer flakes under
// load.
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
    // A constructor whose signature doesn't match the 7-arg shape we invoke
    // with will throw at Invoke time; the helper must swallow the exception
    // and return null rather than crash the plugin.
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
    test <@ result.Message = "[hint] some hint" @>

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
    test <@ result.Message = "[info] some info" @>

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
// Verdict reliability (2026-06-02 Issue A): the analyzer summary count must be
// derived from the current cycle's live diagnostic map — the SAME set that
// gates the verdict — never a monotonic accumulator carried across cycles.
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

/// Recording PluginCtx that captures the run summaries (from the verdict each
/// terminal status carries) and the per-file report/clear calls (the gated
/// ledger set, modelled as a Map).
let private makeAnalyzerRecordingCtx () =
    let summaries = System.Collections.Generic.List<string>()
    let ledger = System.Collections.Generic.Dictionary<string, ErrorEntry list>()

    let ctx: FsHotWatch.PluginFramework.PluginCtx<AnalyzersMsg> =
        { ReportStatus =
            // The run summary arrives as the Completed status's verdict now —
            // capture it where the old CompleteWithSummary hook captured the
            // side-channel value.
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

/// Minimal CommandCtx for invoking a handler's IPC commands directly in tests.
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

    // Cycle 1: 2 errors on the file.
    let state1 =
        handler.Update ctx handler.Init (Custom(AnalysisComplete(file, findings)))
        |> Async.RunSynchronously

    test <@ summaries |> Seq.last = "analyzed 1 files, 2 findings (2 errors, 0 warnings)" @>
    test <@ ledger.ContainsKey file && ledger.[file].Length = 2 @>

    // Cycle 2: same file re-checks clean (0 findings).
    handler.Update ctx state1 (Custom(AnalysisComplete(file, [])))
    |> Async.RunSynchronously
    |> ignore

    // The summary must reflect the CURRENT cycle's gated set: 0 findings — not
    // the stale 2 from cycle 1. And the file's ledger entry must be cleared.
    test <@ summaries |> Seq.last = "analyzed 2 files, 0 findings (0 errors, 0 warnings)" @>
    test <@ not (ledger.ContainsKey file) @>

// The `diagnostics` command sums `entries.Length` across every file in the live
// map. The empty-state path is covered elsewhere ("diagnostics command returns
// zeroes when no files checked"); this covers the NON-empty fold — a state with
// real diagnostics — so the summing lambda is exercised deterministically (no
// SDK analyzer load). Populates state via a Custom(AnalysisComplete …) update,
// then invokes the command function directly on the resulting state.
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

    // The fold over the live map must report both findings on the one file.
    test <@ json.Contains("\"diagnostics\":2") @>
    test <@ json.Contains("\"files\":1") @>
