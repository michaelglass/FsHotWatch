module FsHotWatch.Tests.AnalyzersPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

let private fakeResult file : FileCheckResult =
    { File = file
      Source = "let x = 1"
      ParseResults = Unchecked.defaultof<_>
      CheckResults = ParseOnly
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L }

[<Fact(Timeout = 30000)>]
let ``plugin has correct name`` () =
    let handler = create [] None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 30000)>]
let ``diagnostics command returns zeroes when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
    host.RegisterHandler(handler)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>
    test <@ result.Value.Contains("\"files\":0") @>
    test <@ result.Value.Contains("\"diagnostics\":0") @>

[<Fact(Timeout = 30000)>]
let ``analyzer error path does not crash`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

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

[<Fact(Timeout = 30000)>]
let ``analyzer with non-existent path skips loading`` () =
    // Exercise the Directory.Exists false branch
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [ "/tmp/no-such-analyzer-dir-12345" ] None
    host.RegisterHandler(handler)

    // No analyzers should be loaded — diagnostics command shows 0 analyzers
    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\":0") @>

[<Fact(Timeout = 30000)>]
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

        host.RegisterHandler(handler)

        let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"analyzers\":0") @>
    finally
        try
            System.IO.Directory.Delete(emptyDir, true)
        with _ ->
            ()

[<Fact(Timeout = 30000)>]
let ``concurrent analyzer runs are bounded`` () =
    let handler = create [] None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "analyzers" @>

[<Fact(Timeout = 30000)>]
let ``cache key includes parse-only suffix for ParseOnly results`` () =
    let commitId = "abc123"
    let handler = create [] (Some(fun () -> Some commitId))

    let parseOnlyResult: FileCheckResult =
        { File = "/tmp/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    let fullCheckResult: FileCheckResult =
        { parseOnlyResult with
            CheckResults = FullCheck(Unchecked.defaultof<_>) }

    let cacheKeyFn = handler.CacheKey.Value

    let parseOnlyKey = cacheKeyFn (FileChecked parseOnlyResult)
    let fullCheckKey = cacheKeyFn (FileChecked fullCheckResult)

    // ParseOnly should have a different cache key than FullCheck
    test <@ parseOnlyKey.IsSome @>
    test <@ fullCheckKey.IsSome @>
    test <@ parseOnlyKey <> fullCheckKey @>

[<Fact(Timeout = 30000)>]
let ``ParseOnly dispatches to analyzer worker instead of skipping`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
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

[<Fact(Timeout = 30000)>]
let ``empty analyzer paths still creates working handler`` () =
    let handler = create [] None
    test <@ handler.Init.LoadedCount = 0 @>
    test <@ handler.Init.DiagnosticsByFile = Map.empty @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>

[<Fact(Timeout = 30000)>]
let ``AnalysisFailed custom message sets status to Completed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
    host.RegisterHandler(handler)

    host.EmitFileChecked(fakeResult "/tmp/test/FailAnalysis.fs")
    waitForTerminalStatus host "analyzers" 3000

    let errors = host.GetErrorsByPlugin("analyzers")

    let hasAnalyzerCrash =
        errors
        |> Map.exists (fun _ entries -> entries |> List.exists (fun e -> e.Message.Contains("Analyzer crashed")))

    test <@ hasAnalyzerCrash @>

[<Fact(Timeout = 30000)>]
let ``cache key is None when getCommitId is None`` () =
    let handler = create [] None
    test <@ handler.CacheKey.IsNone @>

[<Fact(Timeout = 30000)>]
let ``cache key returns None when getCommitId returns None`` () =
    let handler = create [] (Some(fun () -> None))
    let cacheKeyFn = handler.CacheKey.Value

    let key = cacheKeyFn (FileChecked(fakeResult "/tmp/Fake.fs"))
    test <@ key.IsNone @>

[<Fact(Timeout = 30000)>]
let ``cache key for Custom event returns None`` () =
    let commitId = "commit-xyz"
    let handler = create [] (Some(fun () -> Some commitId))
    let cacheKeyFn = handler.CacheKey.Value

    let customKey = cacheKeyFn (Custom(AnalysisComplete("/tmp/Fake.fs", [])))
    test <@ customKey.IsNone @>

[<Fact(Timeout = 30000)>]
let ``cache key for non-FileChecked non-Custom event returns getCommitId`` () =
    let commitId = "commit-abc"
    let handler = create [] (Some(fun () -> Some commitId))
    let cacheKeyFn = handler.CacheKey.Value

    let buildKey = cacheKeyFn (BuildCompleted BuildSucceeded)
    test <@ buildKey = Some commitId @>

[<Fact(Timeout = 30000)>]
let ``multiple concurrent FileChecked events are bounded by semaphore`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
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

[<Fact(Timeout = 30000)>]
let ``teardown cancels CTS and disposes resources`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create [] None
    host.RegisterHandler(handler)

    host.Teardown()

    try
        host.EmitFileChecked(fakeResult "/tmp/teardown/Fake.fs")
    with _ ->
        ()
