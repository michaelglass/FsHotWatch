module FsHotWatch.Tests.TestHelpersTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers

// Regression guard: `withTempDir`'s `finally` recursively deletes the scratch dir
// on the test thread, while a daemon whose shutdown wait has timed out can keep
// (re)creating files under `.fshw/errors/`. That race threw `IOException
// "Directory not empty"` ON THE TEST THREAD, which the host reported as an
// unattributed "failed: 1" with tests unreported. Cleanup must survive a
// concurrent writer.

/// Hammer the tree under `dir` the way a not-yet-drained daemon does (recreate a
/// nested subdir and write files into it) for a short burst, then stop on its
/// own. The burst is timed to still be in flight when `withTempDir` starts its
/// recursive delete (proving resilience) but to stop quickly so the delete's
/// retry converges within a few attempts.
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
    // Repeat so the cleanup window reliably overlaps a live writer: the bug threw
    // on ~80% of single attempts, so 8 makes a pre-fix regression near-certain.
    for _ in 1..8 do
        let mutable hammer = Unchecked.defaultof<Thread>

        withTempDir "helpers-cleanup-race" (fun tmpDir ->
            hammer <- hammerTreeBriefly tmpDir (TimeSpan.FromMilliseconds(75.0))
            Thread.Sleep(10))

        hammer.Join(TimeSpan.FromSeconds(2.0)) |> ignore

    // Reaching here means every cleanup race returned without throwing.
    test <@ true @>
