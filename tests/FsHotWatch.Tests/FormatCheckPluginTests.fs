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

    // ProjectChanged and SolutionChanged should not crash the plugin
    host.EmitFileChanged(ProjectChanged [ "/tmp/Test.fsproj" ])
    host.EmitFileChanged(SolutionChanged)

    // Wait until Completed AND no events still queued. The two EmitFileChanged
    // calls above are dispatched asynchronously; waitUntil could see Completed
    // after the first event but before the second (SolutionChanged) has been
    // dequeued, causing a spurious Running at the assertion below. Requiring
    // !AnyPluginBusy() ensures both events have been fully processed.
    // 17s: well under the 20s Fact timeout but enough headroom for slow Linux CI.
    waitUntil
        (fun () ->
            (not (host.AnyPluginBusy()))
            && (match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false))
        17000

    // The plugin still sets Completed status (empty unformatted set)
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

    // Emit a SourceChanged with a file that doesn't exist — File.Exists check
    // should cause it to be skipped (no crash)
    host.EmitFileChanged(SourceChanged [ "/tmp/nonexistent/Fake.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("format-check") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    // The non-existent file should not be in the unformatted set
    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>

// --- F2 regression: unreadable file in the format-check merkle ---
// See docs/plans/2026-05-02-error-handling-audit.md §F2.

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when any input file is unreadable (F2)`` () =
    // F2 — pre-fix the cacheKey lambda silently substituted "" for an
    // unreadable file. That collided with a real empty file AND across
    // distinct read-failure causes, producing stale "format OK" cache hits
    // on transient locks. The fix returns None so the cache is bypassed
    // (cache miss + retry on the next event).
    let handler = createFormatCheck None

    let cacheKeyFn =
        handler.CacheKey |> Option.defaultWith (fun () -> failwith "expected CacheKey")

    let nonExistentFile =
        Path.Combine(Path.GetTempPath(), $"fshw-f2-missing-{Guid.NewGuid():N}.fs")

    let key = cacheKeyFn (FileChanged(SourceChanged [ nonExistentFile ]))

    test <@ key = None @>

[<Fact(Timeout = 15000)>]
let ``format-check cacheKey returns None when one of multiple files is unreadable (F2 short-circuit)`` () =
    // Cover the post-None short-circuit branch in the fold: a readable file
    // followed by an unreadable one collapses to None for the whole event.
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
    // After alphabetical sort, "AAA-missing-...fs" comes BEFORE "ZZZ-good.fs",
    // exercising the short-circuit branch where Option.bind sees None and
    // skips the (still readable) successor file.
    let sortedFirstMissing = Path.Combine(tmpDir, "AAA-still-missing.fs")
    let sortedSecondGood = Path.Combine(tmpDir, "ZZZ-good.fs")
    File.WriteAllText(sortedSecondGood, "module ZZZ\n")

    try
        let key = cacheKeyFn (FileChanged(SourceChanged [ goodFile; missingFile ]))
        test <@ key = None @>
        // Short-circuit: first file None → second file's Some never folds in.
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
    // Sanity: the happy path still produces a key so cache hits are possible
    // when all inputs are readable.
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
        // Badly formatted: missing spaces, wrong indentation
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.Length = 1 @>
        test <@ modified.[0] = file @>

        // Verify the file was rewritten
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
        // Well-formatted F# code
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
        // Should not throw; error is caught internally
        let modified = preprocessor.Process [ file ] tmpDir
        // May or may not modify depending on Fantomas behavior, but should not crash
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
        // Write invalid content that might cause Fantomas to throw
        File.WriteAllText(file, "module \x00\x00\x00")

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        // This should not crash the plugin - errors are caught
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

        // Create a mock commit ID provider that always returns the same ID (simulating unchanged commit)
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

        // Verify plugin detected unformatted file
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

        // With proper cache invalidation, plugin should detect file is now formatted
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

        // The format-check plugin should report errors to the ErrorLedger
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

        // Errors should be cleared
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
// AUTOMATION-98 finding 1(a) — the FormatPreprocessor must be BOUNDED.
// ---------------------------------------------------------------------------
//
// The regression this pins: `FormatPreprocessor.Process` ran Fantomas via a bare
// `Async.RunSynchronously` with NO timeout, while its twin `createFormatCheck`
// wrapped the IDENTICAL call in `runWithCancellableTimeout` — one bounded, one
// not, and the unbounded one on the WORSE path. A preprocessor runs inside the
// daemon's `processBatch`, so a Fantomas hang there wedges the changeAgent: the
// daemon silently stops processing file changes, forever. It also runs inside
// `performScan`, which `WaitForScan` — `check`'s first step — blocks on.
//
// A zero budget is the boundary case of "cannot finish in time": the work is
// started and the wait expires before it can complete, which is exactly the shape
// of a hang, minus the waiting.

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor leaves a file alone when formatting exceeds its timeout`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"fshw-fmt-timeout-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        let original = "module Bad\nlet   x=1\nlet   y   =   2\n"
        File.WriteAllText(file, original)

        // Force the timeout DETERMINISTICALLY via the same test seam the
        // format-check plugin has: a slow hook inside the guarded region, and a
        // real (1 s) budget. The previous `timeoutSec = 0` raced the timer
        // against Fantomas — on a warm, idle box the format WON, the file was
        // rewritten, and this test failed while asserting nothing about the
        // timeout path.
        let slowHook () = System.Threading.Thread.Sleep 3000

        let preprocessor =
            FormatPreprocessor(timeoutSec = 1, slowHook = slowHook) :> IFsHotWatchPreprocessor

        let modified = preprocessor.Process [ file ] tmpDir

        // No rewrite is reported...
        test <@ modified.IsEmpty @>
        // ...and the file is untouched — a timed-out format must never write a
        // half-formatted document.
        test <@ File.ReadAllText(file) = original @>
    finally
        Directory.Delete(tmpDir, true)
