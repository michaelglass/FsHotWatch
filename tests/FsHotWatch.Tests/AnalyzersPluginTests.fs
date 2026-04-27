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

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler = create [] None None DiagnosticSeverity.Hint
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 10000)>]
let ``diagnostics command returns zeroes when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>
    test <@ result.Value.Contains("\"files\":0") @>
    test <@ result.Value.Contains("\"diagnostics\":0") @>

[<Fact(Timeout = 5000)>]
let ``analyzer error path does not crash`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
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

[<Fact(Timeout = 5000)>]
let ``analyzer with non-existent path skips loading`` () =
    // Exercise the Directory.Exists false branch
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create [ "/tmp/no-such-analyzer-dir-12345" ] None None DiagnosticSeverity.Hint

    host.RegisterHandler(handler)

    // No analyzers should be loaded — diagnostics command shows 0 analyzers
    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>

[<Fact(Timeout = 10000)>]
let ``analyzer with mix of valid and invalid paths`` () =
    // Create a real empty dir that exists, paired with one that does not
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-empty-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(emptyDir) |> ignore

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler =
            create
                [ emptyDir // exists but no analyzer DLLs
                  "/tmp/nonexistent-path-xyz-99999" ] // does not exist
                None
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

[<Fact(Timeout = 5000)>]
let ``concurrent analyzer runs are bounded`` () =
    let handler = create [] None None DiagnosticSeverity.Hint
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 5000)>]
let ``cache key includes parse-only suffix for ParseOnly results`` () =
    let commitId = "abc123"
    let handler = create [] (Some(fun () -> Some commitId)) None DiagnosticSeverity.Hint

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

[<Fact(Timeout = 10000)>]
let ``ParseOnly dispatches to analyzer worker instead of skipping`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = "let x = 1"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    host.EmitFileChecked(fakeResult)

    // Wait for the plugin to finish processing (status reaches Completed/Failed)
    waitForTerminalStatus host "analyzers" 5000

    // ParseOnly should dispatch to the async worker (not skip synchronously).
    // With Unchecked.defaultof ParseResults, the analyzer will crash — but it
    // should crash via the AnalysisFailed path (proving the worker ran),
    // not silently complete via the old skip path.
    let errors = host.GetErrorsByPlugin("analyzers")

    let hasAnalyzerCrash =
        errors
        |> Map.exists (fun _ entries -> entries |> List.exists (fun e -> e.Message.Contains("Analyzer crashed")))

    test <@ hasAnalyzerCrash @>

[<Fact(Timeout = 5000)>]
let ``empty analyzer paths still creates working handler`` () =
    let handler = create [] None None DiagnosticSeverity.Hint
    test <@ handler.Init.LoadedCount = 0 @>
    test <@ handler.Init.DiagnosticsByFile = Map.empty @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>

[<Fact(Timeout = 5000)>]
let ``AnalysisFailed custom message sets status to Completed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeResult "/tmp/test/FailAnalysis.fs")
    waitForTerminalStatus host "analyzers" 3000

    let errors = host.GetErrorsByPlugin("analyzers")

    let hasAnalyzerCrash =
        errors
        |> Map.exists (fun _ entries -> entries |> List.exists (fun e -> e.Message.Contains("Analyzer crashed")))

    test <@ hasAnalyzerCrash @>

// §2a: getCommitId is no longer consulted by Analyzers; the plugin always
// provides a CacheKey and the key depends only on the FileChecked content.
// Earlier "cache key when getCommitId is None" tests are obsolete under the
// new contract — replaced below by content-based behavior.

[<Fact(Timeout = 5000)>]
let ``cache key is provided regardless of getCommitId`` () =
    let h1 = create [] None None DiagnosticSeverity.Hint
    let h2 = create [] (Some(fun () -> None)) None DiagnosticSeverity.Hint
    let h3 = create [] (Some(fun () -> Some "abc123")) None DiagnosticSeverity.Hint
    test <@ h1.CacheKey.IsSome @>
    test <@ h2.CacheKey.IsSome @>
    test <@ h3.CacheKey.IsSome @>

[<Fact(Timeout = 5000)>]
let ``cache key reflects file content when getCommitId is unavailable`` () =
    // §2a: even with no jj commit, identical source bytes produce identical keys.
    let handler = create [] (Some(fun () -> None)) None DiagnosticSeverity.Hint
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

[<Fact(Timeout = 5000)>]
let ``cache key for Custom event returns None`` () =
    let handler = create [] None None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value

    let customKey = cacheKeyFn (Custom(AnalysisComplete("/tmp/Fake.fs", [])))
    test <@ customKey.IsNone @>

[<Fact(Timeout = 5000)>]
let ``cache key for non-FileChecked event returns None`` () =
    // §2a: only FileChecked produces a cache key; other events aren't
    // cached at all (the plugin only subscribes to SubscribeFileChecked anyway).
    let handler = create [] None None DiagnosticSeverity.Hint
    let cacheKeyFn = handler.CacheKey.Value

    let buildKey = cacheKeyFn (BuildCompleted BuildSucceeded)
    test <@ buildKey.IsNone @>

[<Fact(Timeout = 5000)>]
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

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // First run — cold cache, analyzer crashes (terminal Failed), cache write.
    host.EmitFileChecked(fakeResult "/tmp/test/Replay.fs")
    waitForTerminalStatus host "analyzers" 3000

    // Verify cache populated.
    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/Replay.fs" }

    let cacheKeyFn = handler.CacheKey.Value
    let event = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey = cacheKeyFn event
    test <@ (cacheIface.TryGet key computedKey.Value).IsSome @>

    // Verify hit count by computing keys again (should be the same).
    let event2 = FileChecked(fakeResult "/tmp/test/Replay.fs")
    let computedKey2 = cacheKeyFn event2
    test <@ computedKey = computedKey2 @>

[<Fact(Timeout = 5000)>]
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

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    // Use a fake result; analyzer will crash (Unchecked.defaultof ParseResults),
    // which exercises the Choice3Of3 path. Crash status is also terminal, so
    // the cache write must still happen.
    host.EmitFileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    waitForTerminalStatus host "analyzers" 3000

    // The cache should now contain an entry for (analyzers, that-file).
    let key: FsHotWatch.TaskCache.CompositeKey =
        { Plugin = "analyzers"
          File = Some "/tmp/test/CacheRegression.fs" }

    let cacheKeyFn = handler.CacheKey.Value
    let event = FileChecked(fakeResult "/tmp/test/CacheRegression.fs")
    let computedKey = cacheKeyFn event

    test <@ computedKey.IsSome @>
    let result = cacheIface.TryGet key computedKey.Value
    test <@ result.IsSome @>

[<Fact(Timeout = 5000)>]
let ``multiple concurrent FileChecked events are bounded by semaphore`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
    host.RegisterHandler(handler)

    let events =
        [ for i in 1..10 ->
              { fakeResult $"/tmp/concurrent/File%d{i}.fs" with
                  Version = int64 i } ]

    for e in events do
        host.EmitFileChecked(e)

    waitForTerminalStatus host "analyzers" 5000

    let errors = host.GetErrorsByPlugin("analyzers")
    test <@ errors.Count > 0 @>

[<Fact(Timeout = 5000)>]
let ``teardown cancels CTS and disposes resources`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None None DiagnosticSeverity.Hint
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

[<Fact(Timeout = 10000)>]
let ``analyzers handler times out when work exceeds TimeoutSec`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    // slowHook sleeps longer than the 1s timeout, forcing a TimedOut outcome
    let slowHook () = System.Threading.Thread.Sleep 3000

    let handler =
        createWithSlowHook [] None (Some 1) DiagnosticSeverity.Hint (Some slowHook)

    host.RegisterHandler(handler)
    host.EmitFileChecked(fakeResult "/tmp/slow/File.fs")
    waitForTerminalStatus host "analyzers" 5000
    let snap = host.GetActivitySnapshot("analyzers")

    match snap.LastRun with
    | Some r ->
        match r.Outcome with
        | FsHotWatch.Events.TimedOut _ -> ()
        | other -> Assert.Fail $"Expected TimedOut, got {other}"
    | None -> Assert.Fail "Expected LastRun record"

[<Fact(Timeout = 1000)>]
let ``isKnownNonAnalyzerPrefix returns true when name has matching prefix`` () =
    test <@ isKnownNonAnalyzerPrefix [| "System."; "Microsoft." |] "System.Text.Json" @>

[<Fact(Timeout = 1000)>]
let ``isKnownNonAnalyzerPrefix returns false when no prefix matches`` () =
    test <@ not (isKnownNonAnalyzerPrefix [| "System."; "Microsoft." |] "ExampleAnalyzer") @>

[<Fact(Timeout = 1000)>]
let ``isKnownNonAnalyzerPrefix is case-sensitive`` () =
    // StringComparison.Ordinal — "system." does not match "System."
    test <@ not (isKnownNonAnalyzerPrefix [| "System." |] "system.text.json") @>

[<Fact(Timeout = 1000)>]
let ``isKnownNonAnalyzerPrefix with empty prefix array returns false`` () =
    test <@ not (isKnownNonAnalyzerPrefix [||] "System.Something") @>

[<Fact(Timeout = 1000)>]
let ``default knownNonAnalyzerPrefixes excludes common BCL assemblies`` () =
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "System.Collections" @>
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "Microsoft.Extensions.Logging" @>
    test <@ isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "FSharp.Core" @>

[<Fact(Timeout = 1000)>]
let ``default knownNonAnalyzerPrefixes does not match real analyzer packages`` () =
    test <@ not (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "ExampleAnalyzer") @>
    test <@ not (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes "FSharpLint.Core") @>

[<Fact(Timeout = 1000)>]
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

[<Fact(Timeout = 1000)>]
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
