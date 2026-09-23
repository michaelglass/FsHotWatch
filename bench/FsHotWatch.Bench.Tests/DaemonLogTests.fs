module FsHotWatch.Bench.Tests.DaemonLogTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

// Shaped on a real logs/daemon.log: two daemon starts, the second with two scan
// cycles' worth of markers, two test cycles, and a nested daemon's relayed lines.
let private log =
    [ "  [config] 11:49:58.753 verdictInputs: 5 declared, 6 file(s) folded into the tree hash, 0 absent"
      "Starting FsHotWatch daemon for /r — claimed the singleton lock, pid=59942 argv=x start"
      "  [scan] 11:50:05.217 13 projects, 206 files registered"
      "  [scan] 11:51:33.801 Checked 211 files (5 tiers), skipped 32, unchecked 0"
      "Daemon stopped."
      "  [config] 19:36:07.894 verdictInputs: 5 declared, 6 file(s) folded into the tree hash, 0 absent"
      "  [config] 19:36:07.907 Loaded .fshw.json"
      "Starting FsHotWatch daemon for /r — claimed the singleton lock, pid=87070 argv=x start"
      "  [scan] 19:36:12.572 13 projects, 207 files registered"
      "  [test-prune] 19:36:33.923 executeTests starting with 1 configs"
      "  [test-prune] 19:36:51.005 FsHotWatch.Tests: streaming run output to /r/.fshw/test-runs/a/FsHotWatch.Tests.output.log"
      "  [test-prune] 19:50:26.805   |   [test-prune] 19:50:26.402 Tests complete: 1 projects, 0.0s"
      "  [test-prune] 19:38:40.301 Tests complete: 1 projects, 126.4s"
      "  [scan] 19:39:33.852 Checked 207 files (5 tiers), skipped 32, unchecked 0"
      "  [test-prune] 19:38:42.787 executeTests starting with 1 configs"
      "  [test-prune] 19:38:45.943 FsHotWatch.Tests: streaming run output to /r/.fshw/test-runs/b/FsHotWatch.Tests.output.log" ]

[<Fact>]
let ``the window starts at the LAST daemon start and announces that daemon's pid`` () =
    let window = DaemonLog.sinceLastStart log
    test <@ List.length window = 11 @>
    test <@ DaemonLog.announcedPid window = Some 87070 @>

[<Fact>]
let ``a log with no start marker yields an empty window`` () =
    test
        <@
            List.isEmpty (
                DaemonLog.sinceLastStart [ "  [scan] 01:02:03.456 Checked 1 files (1 tiers), skipped 0, unchecked 0" ]
            )
        @>

[<Fact>]
let ``a start marker relayed from a nested daemon does not open a window`` () =
    let relayed =
        log
        @ [ "  [test-prune] 19:50:00.000   |   [config] 19:50:00.000 verdictInputs: 1 declared, 1 file(s) folded into the tree hash, 0 absent" ]

    test <@ DaemonLog.announcedPid (DaemonLog.sinceLastStart relayed) = Some 87070 @>

[<Fact>]
let ``scan counts come from this daemon's cycle, not an earlier run's`` () =
    let window = DaemonLog.sinceLastStart log
    let counts = DaemonLog.scanCycles window |> List.choose DaemonLog.scanCounts

    test
        <@
            counts = [ { Projects = 13
                         Registered = 207
                         Checked = 207
                         Skipped = 32
                         Unchecked = 0 } ]
        @>

[<Fact>]
let ``a relayed Tests complete line does not close a test cycle, and the last cycle is unfinished`` () =
    let cycles = DaemonLog.testCycles (DaemonLog.sinceLastStart log)
    test <@ List.length cycles = 2 @>
    test <@ cycles.[0].EndLine = Some "  [test-prune] 19:38:40.301 Tests complete: 1 projects, 126.4s" @>
    test <@ cycles.[1].EndLine = None @>
    let last = DaemonLog.lastComplete cycles
    test <@ last |> Option.map DaemonLog.outputLogs = Some [ "/r/.fshw/test-runs/a/FsHotWatch.Tests.output.log" ] @>

[<Fact>]
let ``a second start before an end closes the first cycle unfinished`` () =
    let isStart (l: string) = l.StartsWith "S"
    let isEnd (l: string) = l.StartsWith "E"

    let cycles =
        DaemonLog.cycles isStart isEnd [ "E0"; "S1"; "x"; "S2"; "y"; "E2"; "z" ]

    test <@ cycles |> List.map (fun c -> c.StartLine, c.EndLine) = [ "S1", None; "S2", Some "E2" ] @>
    test <@ cycles.[1].Lines = [ "S2"; "y"; "E2" ] @>

[<Fact>]
let ``test totals come from the LAST summary block of a run log`` () =
    let text =
        """Test run summary: Failed! - a.dll
  total: 10
  failed: 2
  succeeded: 8
  skipped: 0
retrying
Test run summary: Passed! - a.dll (net10.0|arm64)
  total: 3767
  failed: 0
  succeeded: 3767
  skipped: 0
  duration: 1m 47s 188ms
"""

    test
        <@
            DaemonLog.parseTestTotals text = Some
                { Total = 3767
                  Failed = 0
                  Succeeded = 3767
                  Skipped = 0 }
        @>

[<Fact>]
let ``a run log with no or a truncated summary is a missing result, not zero tests`` () =
    test <@ DaemonLog.parseTestTotals "building...\n" = None @>
    test <@ DaemonLog.parseTestTotals "Test run summary: Passed!\n  total: 3\n" = None @>

[<Fact>]
let ``scan validity flags a truncated scan, a foreign window and log-metrics disagreement`` () =
    let window = DaemonLog.sinceLastStart log

    let sample checkedFiles unchecked : FsHotWatch.ScanMetrics.ScanSample =
        { Generation = 1L
          Kind = "cold"
          DurationMs = 1.0
          FilesRegistered = 207
          FilesChecked = checkedFiles
          FilesUnchecked = unchecked
          FilesSkipped = 0
          FilesDepsGated = 0
          FilesUncovered = 0
          RetryRounds = 0
          RssBytes = 0L
          ManagedBytes = 0L
          ForcedGc = false
          Gen2Collections = 0
          SampledAt = System.DateTime.UtcNow }

    test <@ List.isEmpty (Scenario.scanProblems 87070 (Some(sample 207 0)) window) @>
    test <@ List.length (Scenario.scanProblems 87070 (Some(sample 200 7)) window) = 2 @>
    test <@ List.length (Scenario.scanProblems 1 (Some(sample 207 0)) window) = 1 @>
    test <@ List.length (Scenario.scanProblems 87070 None window) = 1 @>

[<Fact>]
let ``sessions that checked different file counts are a parity failure`` () =
    test <@ List.isEmpty (Scenario.parityProblems [ 1, 207; 2, 207 ]) @>
    test <@ List.isEmpty (Scenario.parityProblems []) @>
    test <@ Scenario.parityProblems [ 1, 207; 2, 103 ] = [ "sessions checked different file counts: s1=207 s2=103" ] @>

[<Fact>]
let ``test totals are summed from the last complete cycle's output logs`` () =
    let dir = Directory.CreateTempSubdirectory("fshw-bench-")

    try
        let out = Path.Combine(dir.FullName, "P.output.log")
        File.WriteAllText(out, "Test run summary: Passed!\n  total: 5\n  failed: 0\n  succeeded: 4\n  skipped: 1\n")

        let window =
            [ "  [test-prune] 10:00:00.000 executeTests starting with 1 configs"
              $"  [test-prune] 10:00:01.000 P: streaming run output to {out}"
              "  [test-prune] 10:00:02.000 Tests complete: 1 projects, 1.0s" ]

        test <@ Scenario.testTotals window |> Option.map _.Total = Some 5 @>
        File.Delete out
        test <@ Scenario.testTotals window = None @>
    finally
        dir.Delete(true)

[<Fact>]
let ``current ISO-8601 UTC timestamps window and count the same as the older clock-only ones`` () =
    let iso =
        [ "  [config] 2026-09-23T03:51:43.670Z verdictInputs: 5 declared, 6 file(s) folded into the tree hash, 0 absent"
          "Starting FsHotWatch daemon for /w — claimed the singleton lock, pid=4242 argv=x start"
          "  [scan] 2026-09-23T03:51:48.423Z 13 projects, 209 files registered"
          "  [test-prune] 2026-09-23T03:52:00.000Z   |   [scan] 03:52:00.000 Checked 1 files (1 tiers), skipped 0, unchecked 0"
          "  [scan] 2026-09-23T03:55:01.000Z Checked 209 files (5 tiers), skipped 32, unchecked 0" ]

    let window = DaemonLog.sinceLastStart iso
    test <@ DaemonLog.announcedPid window = Some 4242 @>

    test
        <@
            DaemonLog.scanCycles window
            |> List.choose DaemonLog.scanCounts
            |> List.map _.Checked = [ 209 ]
        @>
