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
          DirectSpawns = 0L
          HelperSpawns = 0L
          Scope = FsHotWatch.DaemonHosting.ResourceScope.Process
          SampledAt = System.DateTime.UtcNow }

    test <@ List.isEmpty (Scenario.scanProblems (Scenario.Owner.Process 87070) (Some(sample 207 0)) window) @>
    test <@ List.length (Scenario.scanProblems (Scenario.Owner.Process 87070) (Some(sample 200 7)) window) = 2 @>
    test <@ List.length (Scenario.scanProblems (Scenario.Owner.Process 1) (Some(sample 207 0)) window) = 1 @>
    test <@ List.length (Scenario.scanProblems (Scenario.Owner.Process 87070) None window) = 1 @>

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

// Host mode: each session's own daemon.log, as the repository host writes it.
let private hostedLog pid session =
    [ "  [config] 2026-09-23T08:00:00.000Z verdictInputs: 5 declared, 6 file(s) folded into the tree hash, 0 absent"
      $"  [host] 2026-09-23T08:00:00.100Z Attached to repository host pid=%d{pid} session=%s{session}"
      "  [scan] 2026-09-23T08:00:01.000Z 13 projects, 207 files registered"
      "  [scan] 2026-09-23T08:01:00.000Z Checked 207 files (5 tiers), skipped 32, unchecked 0"
      "  [check] 2026-09-23T08:02:00.000Z settled epoch=3 after=2392ms files=1"
      "  [test-prune] 2026-09-23T08:02:01.000Z   |   [check] 08:02:01.000 settled epoch=9 after=1ms files=1"
      "  [check] 2026-09-23T08:03:00.000Z settled epoch=4 after=2515ms files=2" ]

[<Fact>]
let ``a hosted session's log names its host pid and session`` () =
    test <@ DaemonLog.attachedHost (hostedLog 4242 "r1.w2.7") = Some(4242, "r1.w2.7") @>
    test <@ DaemonLog.attachedHost (DaemonLog.sinceLastStart log) = None @>

[<Fact>]
let ``settle lines parse epoch, latency and files, and relayed ones do not count`` () =
    let got =
        DaemonLog.settled (hostedLog 1 "s")
        |> List.map (fun (x: DaemonLog.Settle) -> x.Epoch, x.AfterMs, x.Files)

    test <@ got = [ 3L, 2392.0, 1; 4L, 2515.0, 2 ] @>

let private scanOf checkedFiles : FsHotWatch.ScanMetrics.ScanSample =
    { Generation = 1L
      Kind = "cold"
      DurationMs = 1.0
      FilesRegistered = 207
      FilesChecked = checkedFiles
      FilesUnchecked = 0
      FilesSkipped = 0
      FilesDepsGated = 0
      FilesUncovered = 0
      RetryRounds = 0
      RssBytes = 0L
      ManagedBytes = 0L
      ForcedGc = false
      Gen2Collections = 0
      DirectSpawns = 0L
      HelperSpawns = 0L
      Scope = FsHotWatch.DaemonHosting.ResourceScope.Process
      SampledAt = System.DateTime.UtcNow }

[<Fact>]
let ``a hosted session is owned when its log names the measured host, not its own pid`` () =
    let window = DaemonLog.sinceLastStart (hostedLog 4242 "r1.w2.7")
    test <@ List.isEmpty (Scenario.scanProblems (Scenario.Owner.Host 4242) (Some(scanOf 207)) window) @>

    test
        <@
            Scenario.scanProblems (Scenario.Owner.Host 5000) (Some(scanOf 207)) window = [ "session attached to host pid 4242, not the measured host 5000" ]
        @>
    // A legacy-owned check of a hosted log fails: there is no per-process announcement.
    test <@ List.length (Scenario.scanProblems (Scenario.Owner.Process 4242) (Some(scanOf 207)) window) = 1 @>

[<Fact>]
let ``host sessions must be distinct, and every session must have attached`` () =
    test <@ List.isEmpty (Scenario.hostSessionProblems [ 1, Some "a"; 2, Some "b" ]) @>

    test
        <@ Scenario.hostSessionProblems [ 1, Some "a"; 2, Some "a" ] = [ "sessions share a host session id: s1=a s2=a" ] @>

    test <@ Scenario.hostSessionProblems [ 1, Some "a"; 2, None ] = [ "s2 never attached to the repository host" ] @>

[<Fact>]
let ``an edit appends a marker line and restoring returns the original bytes`` () =
    let original = "module M\n\nlet x = 1\n"
    let edited = Scenario.editedContent original 3
    test <@ edited.StartsWith original && edited.Contains "fshw-bench edit 3" @>
    test <@ Scenario.editedContent original 3 <> Scenario.editedContent original 4 @>

let private st epoch after : DaemonLog.Settle =
    { Epoch = epoch
      AfterMs = after
      Files = 1 }

[<Fact>]
let ``a high-fan-out edit's settle is the LAST line of the first new epoch`` () =
    // One edit, three cohorts checked under epoch 7, then an unrelated epoch 8.
    let fresh = [ st 7L 900.0; st 7L 4100.0; st 7L 9800.0; st 8L 300.0 ]
    test <@ Scenario.editSettle fresh |> Option.map _.AfterMs = Some 9800.0 @>
    test <@ Scenario.editSettle [] = None @>

[<Fact>]
let ``an edit's epoch is done when a later epoch appears or its lines go quiet`` () =
    let quiet = System.TimeSpan.FromSeconds 3.0
    test <@ not (Scenario.editEpochDone [] (System.TimeSpan.FromSeconds 60.0) quiet) @>
    test <@ not (Scenario.editEpochDone [ st 7L 900.0 ] (System.TimeSpan.FromSeconds 1.0) quiet) @>
    test <@ Scenario.editEpochDone [ st 7L 900.0 ] (System.TimeSpan.FromSeconds 3.0) quiet @>
    test <@ Scenario.editEpochDone [ st 7L 900.0; st 8L 10.0 ] System.TimeSpan.Zero quiet @>

[<Fact>]
let ``a host session attaches with start, as a legacy daemon starts, never with scan`` () =
    // `scan` on a fresh attach runs the attach's cold scan AND a forced one (~21 s of
    // extra churn right before the settled sample and the first edit), which legacy
    // `start` does not: the modes would not be doing the same work.
    test <@ Scenario.hostAttachArgs false = "--no-cache start" @>
    test <@ Scenario.hostAttachArgs true = "start" @>

[<Fact>]
let ``the virtualRoot echo is read from a session's log, and relayed lines do not count`` () =
    let window =
        [ "  [config] 2026-09-24T01:00:00.000Z verdictInputs: 1 declared, 1 file(s) folded into the tree hash, 0 absent"
          "  [config] 2026-09-24T01:00:00.100Z virtualRoot=off"
          "  [test-prune] 2026-09-24T01:00:01.000Z   |   [config] 01:00:01.000 virtualRoot=on" ]

    test <@ DaemonLog.virtualRootEcho window = Some "off" @>
    test <@ DaemonLog.virtualRootEcho [] = None @>

[<Fact>]
let ``a session must echo the virtual-root setting its host was launched with`` () =
    let off = [ "FSHW_VIRTUAL_ROOT", "0" ]
    test <@ List.isEmpty (Scenario.virtualRootProblems off (Some "off")) @>
    test <@ List.isEmpty (Scenario.virtualRootProblems [] (Some "on")) @>
    // An older binary echoes nothing: fine when nothing was asked of it.
    test <@ List.isEmpty (Scenario.virtualRootProblems [] None) @>

    test
        <@
            Scenario.virtualRootProblems off (Some "on") = [ "FSHW_VIRTUAL_ROOT=0 was set but the session echoed virtualRoot=on" ]
        @>

    test
        <@
            Scenario.virtualRootProblems off None = [ "FSHW_VIRTUAL_ROOT=0 was set but the session echoed no virtualRoot" ]
        @>

[<Fact>]
let ``--env parses KEY=VALUE and refuses anything else`` () =
    test <@ Scenario.parseEnv "FSHW_VIRTUAL_ROOT=0" = Ok("FSHW_VIRTUAL_ROOT", "0") @>
    test <@ Scenario.parseEnv "A=b=c" = Ok("A", "b=c") @>
    test <@ Result.isError (Scenario.parseEnv "NOVALUE") @>
    test <@ Result.isError (Scenario.parseEnv "=x") @>

// A hosted session in a repository that declares no verdictInputs (CommandTree has no
// .fshw.json at all): no `verdictInputs:` line is written, so the window must start at
// the `Loaded .fshw.json` line every start writes.
let private hostedNoVerdictInputs =
    [ "  [scan] 2026-09-24T01:00:00.000Z Checked 99 files (1 tiers), skipped 0, unchecked 0"
      "Daemon stopped."
      "  [config] 2026-09-24T01:18:40.569Z Loaded .fshw.json"
      "  [config] 2026-09-24T01:18:40.573Z virtualRoot=on"
      "  [config] 2026-09-24T01:18:40.574Z checker: cacheSizeFactor=100"
      "  [task-cache] 2026-09-24T01:18:40.601Z Workspace cache: 0 entries, 0.0 MB"
      "  [host] 2026-09-24T01:18:40.749Z Attached to repository host pid=48568 session=a.b.c"
      "  [scan] 2026-09-24T01:18:41.666Z 4 projects, 22 files registered"
      "  [scan] 2026-09-24T01:18:49.852Z Checked 22 files (2 tiers), skipped 12, unchecked 0" ]

[<Fact>]
let ``with no verdictInputs the window starts at the Loaded config line every start writes`` () =
    let window = DaemonLog.sinceLastStart hostedNoVerdictInputs
    test <@ List.length window = 7 @>
    test <@ DaemonLog.attachedHost window = Some(48568, "a.b.c") @>
    test <@ DaemonLog.virtualRootEcho window = Some "on" @>

    test
        <@
            DaemonLog.scanCycles window
            |> List.choose DaemonLog.scanCounts
            |> List.map _.Checked = [ 22 ]
        @>

[<Fact>]
let ``with verdictInputs the window still begins at the verdictInputs line before Loaded`` () =
    let window =
        DaemonLog.sinceLastStart (
            hostedNoVerdictInputs
            @ [ "Daemon stopped."
                "  [config] 2026-09-24T02:00:00.000Z verdictInputs: 1 declared, 1 file(s) folded into the tree hash, 0 absent"
                "  [config] 2026-09-24T02:00:00.010Z NOT RUN — tests/X: reason"
                "  [config] 2026-09-24T02:00:00.020Z Loaded .fshw.json"
                "  [scan] 2026-09-24T02:00:01.000Z 1 projects, 1 files registered" ]
        )

    test <@ window.Head.Contains "verdictInputs:" && List.length window = 4 @>
