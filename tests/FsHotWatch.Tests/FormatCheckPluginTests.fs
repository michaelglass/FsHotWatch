module FsHotWatch.Tests.FormatCheckPluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = createFormatCheck None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "format-check" @>

[<Fact(Timeout = 20000)>]
let ``format-check handler times out when formatter exceeds TimeoutSec`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-to-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let tmpFile = Path.Combine(tmpDir, "Slow.fs")
    File.WriteAllText(tmpFile, "module Slow")

    try
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let slowHook () = System.Threading.Thread.Sleep 3000
        let handler = createFormatCheckWithSlowHook (Some 1) (Some slowHook)
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ tmpFile ])
        waitForTerminalStatus host "format-check" 5000

        let snap = host.GetActivitySnapshot("format-check")

        match snap.LastRun with
        | Some r ->
            match r.Outcome with
            | TimedOut _ -> ()
            | other -> Assert.Fail($"Expected TimedOut, got {other}")
        | None -> Assert.Fail "Expected LastRun record"
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``unformatted command returns zero count when no files processed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = createFormatCheck None
    host.RegisterHandler(handler)

    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>

[<Fact(Timeout = 20000)>]
let ``format check handles non-source change events without crashing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = createFormatCheck None
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "/tmp/Test.fsproj" ])
    host.EmitFileChanged(SolutionChanged)

    // Wait until Completed AND no events still queued: the two EmitFileChanged
    // calls are dispatched asynchronously, so waitUntil could otherwise see
    // Completed after the first event but before SolutionChanged is dequeued.
    // 17s is under the 20s Fact timeout with headroom for slow Linux CI.
    waitUntil
        (fun () ->
            (not (host.AnyPluginBusy()))
            && (match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false))
        17000

    let status = host.GetStatus("format-check")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

[<Fact(Timeout = 15000)>]
let ``format check handles non-existent source file gracefully`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = createFormatCheck None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/nonexistent/Fake.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("format-check") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>

// --- F2 regression: unreadable file in the format-check merkle ---
// See docs/plans/2026-05-02-error-handling-audit.md §F2.

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when any input file is unreadable (F2)`` () =
    // F2 — the cacheKey lambda used to substitute "" for an unreadable file, which
    // collided with a real empty file and across distinct read-failure causes,
    // producing stale "format OK" hits on transient locks. None bypasses the cache.
    let handler = createFormatCheck None

    let cacheKeyFn =
        handler.CacheKey |> Option.defaultWith (fun () -> failwith "expected CacheKey")

    let nonExistentFile =
        Path.Combine(Path.GetTempPath(), $"fshw-f2-missing-{Guid.NewGuid():N}.fs")

    let key = cacheKeyFn (FileChanged(SourceChanged [ nonExistentFile ]))

    test <@ key = None @>

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when one of multiple files is unreadable (F2 short-circuit)`` () =
    // Covers the post-None short-circuit branch in the fold.
    let handler = createFormatCheck None

    let cacheKeyFn =
        handler.CacheKey |> Option.defaultWith (fun () -> failwith "expected CacheKey")

    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-f2-mixed-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    // After alphabetical sort, "Good.fs" comes before "missing-...fs". The
    // good file is read first (Some path), then the missing one drives None.
    let goodFile = Path.Combine(tmpDir, "Good.fs")
    File.WriteAllText(goodFile, "module Good\n")
    let missingFile = Path.Combine(tmpDir, $"missing-{Guid.NewGuid():N}.fs")
    // "AAA-missing" sorts BEFORE "ZZZ-good", so Option.bind sees None first and
    // skips the still-readable successor.
    let sortedFirstMissing = Path.Combine(tmpDir, "AAA-still-missing.fs")
    let sortedSecondGood = Path.Combine(tmpDir, "ZZZ-good.fs")
    File.WriteAllText(sortedSecondGood, "module ZZZ\n")

    try
        let key = cacheKeyFn (FileChanged(SourceChanged [ goodFile; missingFile ]))
        test <@ key = None @>

        let key2 =
            cacheKeyFn (FileChanged(SourceChanged [ sortedFirstMissing; sortedSecondGood ]))

        test <@ key2 = None @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns Some for readable files`` () =
    let handler = createFormatCheck None

    let cacheKeyFn =
        handler.CacheKey |> Option.defaultWith (fun () -> failwith "expected CacheKey")

    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-f2-ok-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let file = Path.Combine(tmpDir, "F2Ok.fs")
    File.WriteAllText(file, "module F2Ok\n")

    try
        let key = cacheKeyFn (FileChanged(SourceChanged [ file ]))
        test <@ key.IsSome @>
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor formats unformatted file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.Length = 1 @>
        test <@ modified.[0] = file @>

        let contents = File.ReadAllText(file)
        test <@ contents <> "module Bad\nlet   x=1\nlet   y   =   2\n" @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 20000)>]
let ``FormatPreprocessor skips already formatted file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Good.fs")
        File.WriteAllText(file, "module Good\n\nlet x = 1\nlet y = 2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor skips non-fs files`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "readme.txt")
        File.WriteAllText(file, "hello world")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor handles non-existent file gracefully`` () =
    let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
    let modified = preprocessor.Process [ "/tmp/nonexistent-file-xyz.fs" ] "/tmp"
    test <@ modified.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor handles format error gracefully`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-err-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        // Write invalid F# that Fantomas cannot parse
        File.WriteAllText(file, "module \x00\x00\x00")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        // The result is Fantomas-dependent; the contract is only that it does not throw.
        test <@ true @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor dispose is callable`` () =
    let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
    preprocessor.Dispose()

[<Fact(Timeout = 20000)>]
let ``format check handles exception gracefully`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-err-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module \x00\x00\x00")

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _)
                | Some(PluginStatus.Failed _) -> true
                | _ -> false)
            15000

        let status = host.GetStatus("format-check")
        test <@ status.IsSome @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``format check detects formatting change even with same commit ID`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-cache-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Test.fs")

        let mockGetCommitId () = Some "fixed-commit-id"

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        // First: file is unformatted
        File.WriteAllText(file, "module Test\nlet   x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        let result1 = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
        test <@ result1.IsSome @>
        test <@ result1.Value.Contains("\"count\": 1") @>

        // Second: file is now formatted, but commit ID hasn't changed
        File.WriteAllText(file, "module Test\n\nlet x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        let result2 = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
        test <@ result2.IsSome @>
        test <@ result2.Value.Contains("\"count\": 0") @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 20000)>]
let ``format check reports unformatted files to error ledger`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-ledger-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        let errors = host.GetErrors()
        test <@ not errors.IsEmpty @>

        let fileErrors = errors |> Map.tryFind file
        test <@ fileErrors.IsSome @>

        let formatErrors =
            fileErrors.Value |> List.filter (fun (plugin, _) -> plugin = "format-check")

        test <@ not formatErrors.IsEmpty @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 20000)>]
let ``format check clears errors when file becomes formatted`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-clear-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Fix.fs")
        File.WriteAllText(file, "module Fix\nlet   x=1\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        // First: unformatted
        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        let errors1 = host.GetErrors()
        test <@ not errors1.IsEmpty @>

        // Now fix the file
        File.WriteAllText(file, "module Fix\n\nlet x = 1\n")
        host.EmitFileChanged(SourceChanged [ file ])

        // Wait for the second run to start (status leaves Completed)
        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> false
                | _ -> true)
            15000

        // Then wait for it to finish
        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        let errors2 = host.GetErrors()
        let fileErrors = errors2 |> Map.tryFind file

        test
            <@
                fileErrors.IsNone
                || fileErrors.Value
                   |> List.filter (fun (p, _) -> p = "format-check")
                   |> List.isEmpty
            @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// ---------------------------------------------------------------------------
// AUTOMATION-191 — a cached format-check verdict may only assert what its key covers.
// ---------------------------------------------------------------------------
//
// Format-check subscribes to `FileChanged`, which the framework keys as a
// WHOLE-RUN entry (`File = None`), so its stored verdict replays VERBATIM — the
// AUTOMATION-186 derive-from-ledger path only ever reached per-file entries.
// A verbatim replay is only honest if the summary is a function of the key, and
// the key is a content merkle of THIS event's files. So the invariant pinned
// here is the one AUTOMATION-245 stated for the build cache: a cache hit must be
// indistinguishable from having run.
//
// Both tests below compare a REPLAYED summary against the summary a cold-cache
// run over the same inputs produces. The pair is deliberate: the first proves a
// stale claim cannot survive the replay, the second proves the same comparison
// still SEES a genuine finding — an equality that a summary frozen to one
// constant would also satisfy is not a detector.

/// Run one format-check session over `batches`, one `SourceChanged` per batch,
/// waiting for each to land before the next is emitted (the summary observed
/// otherwise belongs to whichever batch finished last). Returns the host so the
/// caller can read the terminal summary and the ledger it must agree with.
let private runFormatCheckBatches
    (tmpDir: string)
    (taskCache: FsHotWatch.TaskCache.ITaskCache option)
    (batches: string list list)
    : PluginHost =
    let host =
        match taskCache with
        | Some cache -> PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        | None -> PluginHost(Unchecked.defaultof<_>, tmpDir)

    host.RegisterHandler(createFormatCheck None)

    for batch in batches do
        host.EmitFileChanged(SourceChanged batch)

        // `AnyPluginBusy` is raised synchronously by the emit, so this cannot
        // pass on the PREVIOUS batch's still-standing `Completed`.
        waitUntil
            (fun () ->
                (not (host.AnyPluginBusy()))
                && (match host.GetStatus("format-check") with
                    | Some(Completed _) -> true
                    | _ -> false))
            20000

    host

/// The terminal summary format-check is currently reporting.
let private formatCheckSummary (host: PluginHost) : string =
    match host.GetStatus("format-check") with
    | Some(Completed(_, verdict)) -> verdict.Summary
    | other -> failwith $"expected format-check Completed, got %A{other}"

/// How many files the plugin's LIVE ledger — the set the verdict gates on —
/// currently holds a format-check finding for.
let private formatCheckLedgerCount (host: PluginHost) : int =
    host.GetErrors()
    |> Map.toList
    |> List.sumBy (fun (_, entries) ->
        entries
        |> List.filter (fun (plugin, _) -> plugin = "format-check")
        |> List.length)

[<Fact(Timeout = 120000)>]
let ``a replayed format-check verdict cannot claim files its cache key never covered`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-stale-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let bad = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(bad, "module Bad\nlet   x=1\nlet   y   =   2\n")
        let good = Path.Combine(tmpDir, "Good.fs")
        File.WriteAllText(good, "module Good\n\nlet x = 1\n")

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        // Session 1 mints the entry for `Good` while ANOTHER file is unformatted.
        // `Good`'s key covers `Good`'s bytes and nothing else, so anything the
        // stored summary says about `Bad` is outside its scope.
        runFormatCheckBatches tmpDir (Some cache) [ [ bad ]; [ good ] ] |> ignore

        // What a run over `Good` alone actually finds, with no cache in play.
        let fresh = runFormatCheckBatches tmpDir None [ [ good ] ]
        let freshSummary = formatCheckSummary fresh
        test <@ formatCheckLedgerCount fresh = 0 @>

        // Same inputs, but served from session 1's entry.
        let replayed = runFormatCheckBatches tmpDir (Some cache) [ [ good ] ]
        let replayedSummary = formatCheckSummary replayed

        // The replay landed a green ledger; a summary claiming otherwise would be
        // a `summary:` line contradicting the verdict it sits beside.
        test <@ formatCheckLedgerCount replayed = 0 @>
        test <@ replayedSummary = freshSummary + " (cached)" @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 120000)>]
let ``a replayed format-check verdict still reports a finding that is genuinely current`` () =
    // The positive control for the test above. Same plugin, same replay path, same
    // comparison — but the cached entry's claim is TRUE at replay time, so it must
    // survive intact and still name the finding.
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-current-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let bad = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(bad, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        runFormatCheckBatches tmpDir (Some cache) [ [ bad ] ] |> ignore

        let fresh = runFormatCheckBatches tmpDir None [ [ bad ] ]
        let freshSummary = formatCheckSummary fresh
        test <@ formatCheckLedgerCount fresh = 1 @>

        let replayed = runFormatCheckBatches tmpDir (Some cache) [ [ bad ] ]
        let replayedSummary = formatCheckSummary replayed

        // Replayed from cache, and still red: the entry's error replay put the
        // finding back in the ledger.
        test <@ formatCheckLedgerCount replayed = 1 @>
        test <@ replayedSummary = freshSummary + " (cached)" @>

        // And the summary names it, rather than passing the comparison by being a
        // constant that never mentions a finding at all.
        test <@ replayedSummary.Contains "need formatting" @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 1(a) — the FormatPreprocessor must be BOUNDED.
// ---------------------------------------------------------------------------
//
// The regression this pins: `FormatPreprocessor.Process` ran Fantomas via a bare
// `Async.RunSynchronously` with NO timeout, while its twin `createFormatCheck`
// wrapped the IDENTICAL call in `runWithCancellableTimeout`. A preprocessor runs
// inside the daemon's `processBatch`, so a Fantomas hang there wedges the
// changeAgent — the daemon stops processing file changes, forever. It also runs
// inside `performScan`, which `WaitForScan` — `check`'s first step — blocks on.

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor leaves a file alone when formatting exceeds its timeout`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmt-timeout-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        let original = "module Bad\nlet   x=1\nlet   y   =   2\n"
        File.WriteAllText(file, original)

        // Force the timeout DETERMINISTICALLY via the same seam the format-check
        // plugin has: a slow hook inside the guarded region and a real (1 s)
        // budget. Do not go back to `timeoutSec = 0` — that races the timer
        // against Fantomas, and on a warm box the format wins.
        let slowHook () = System.Threading.Thread.Sleep 3000

        let preprocessor =
            FormatPreprocessor(timeoutSec = 1, slowHook = slowHook) :> IFsHotWatchPreprocessor

        let modified = preprocessor.Process [ file ] tmpDir

        test <@ modified.IsEmpty @>
        // A timed-out format must never write a half-formatted document.
        test <@ File.ReadAllText(file) = original @>
    finally
        Directory.Delete(tmpDir, true)
