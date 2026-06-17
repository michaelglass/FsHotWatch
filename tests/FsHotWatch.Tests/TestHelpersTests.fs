module FsHotWatch.Tests.TestHelpersTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

// Regression guard (2026-06-17): `withTempDir`'s `finally` recursively deletes
// the scratch dir on the test thread. A daemon test cancels its daemon but only
// waits a bounded time for shutdown; the daemon's background pipeline
// (watcher / FCS / FileErrorReporter) can keep (re)creating files under
// `.fshw/errors/` after the wait returns. That background writer races the
// recursive delete and made `Directory.Delete(..., recursive=true)` throw
// `IOException "Directory not empty"` / `DirectoryNotFoundException` ON THE TEST
// THREAD, which the test host recorded as an unattributed "failed: 1" with
// tests unreported (the observed CI flake). `withTempDir` must now survive a
// concurrent writer in its cleanup window.

/// Hammer the tree under `dir` the way a not-yet-drained daemon does (recreate a
/// nested subdir and write files into it) for a short burst, then stop on its
/// own. The burst is timed to still be in flight when `withTempDir` starts its
/// recursive delete (proving resilience) but to stop quickly so the delete's
/// retry succeeds within a few attempts — keeping the test fast and leak-free.
let private hammerTreeBriefly (dir: string) (burst: TimeSpan) : Thread =
    let run () =
        let nested = Path.Combine(dir, ".fshw", "errors")
        let deadline = DateTime.UtcNow + burst
        let mutable n = 0

        while DateTime.UtcNow < deadline do
            try
                Directory.CreateDirectory(nested) |> ignore
                File.WriteAllText(Path.Combine(nested, $"fcs-{n}.json"), "{}")
                n <- n + 1
            with _ ->
                // The delete may yank the dir mid-write — mirror the daemon's
                // tolerated FileErrorReporter behaviour and keep hammering.
                ()

    let t = Thread(ThreadStart(run))
    t.IsBackground <- true
    t.Start()
    t

[<Fact(Timeout = 20000)>]
let ``withTempDir cleanup tolerates a daemon still writing into the dir`` () =
    // Drive the race a handful of times so the cleanup window reliably overlaps a
    // live writer (the bug threw on ~80% of single attempts in a direct micro-
    // repro, so a few iterations make a pre-fix regression essentially certain).
    for _ in 1..8 do
        let mutable hammer = Unchecked.defaultof<Thread>

        // withTempDir must NOT throw out of its finally even though a background
        // thread is still writing into the tree when the recursive delete starts.
        withTempDir "helpers-cleanup-race" (fun tmpDir ->
            // Burst outlasts the few ms before `finally` runs, so the writer is
            // mid-flight when Directory.Delete begins, then drains so cleanup's
            // retry converges fast.
            hammer <- hammerTreeBriefly tmpDir (TimeSpan.FromMilliseconds(75.0))
            Thread.Sleep(10))

        hammer.Join(TimeSpan.FromSeconds(2.0)) |> ignore

    // Reaching here means every cleanup race returned without throwing.
    test <@ true @>
