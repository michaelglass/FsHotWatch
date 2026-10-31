/// `Profile`: the per-project, per-run record of how a test host spent its wall clock —
/// streamed, so a host killed at its timeout still leaves one (AUTOMATION-1300).
module FsHotWatch.Tests.ProfileTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

let private at (seconds: float) =
    DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc).AddSeconds seconds

let private epochMs (t: DateTime) =
    DateTimeOffset(t).ToUnixTimeMilliseconds()

/// A CTRF report whose rows are (class, method, startSecond, durationMs).
let private ctrfOf (rows: (string * string * float * int) list) =
    let tests =
        rows
        |> List.map (fun (cls, meth, startSec, durationMs) ->
            let start = at startSec

            {| name = $"%s{cls}.%s{meth}"
               status = "passed"
               duration = durationMs
               start = epochMs start
               stop = epochMs (start.AddMilliseconds(float durationMs))
               extra = {| ``type`` = cls; ``method`` = meth |} |})

    JsonSerializer.Serialize
        {| results =
            {| summary =
                {| tests = rows.Length
                   passed = rows.Length
                   failed = 0
                   skipped = 0
                   pending = 0
                   other = 0 |}
               tests = tests |} |}

let private tempRunDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-profile-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

[<Fact>]
let ``classTimings groups a report's rows by class, with test count, summed duration and span`` () =
    let json =
        ctrfOf
            [ "A.Slow", "one", 0.0, 4000
              "A.Slow", "two", 4.0, 6000
              "A.Fast", "one", 1.0, 20 ]

    let timings = Profile.classTimings json

    // Slowest (by span) first.
    test <@ timings |> List.map _.Class = [ "A.Slow"; "A.Fast" ] @>

    let slow = timings.Head
    test <@ slow.Tests = 2 @>
    test <@ slow.DurationMs = 10_000L @>
    test <@ slow.Start = at 0.0 @>
    test <@ slow.Stop = at 10.0 @>

[<Fact>]
let ``classTimings reads a nested class by extra.type, and falls back to the name's last dot`` () =
    let json =
        """{"results":{"summary":{"tests":2},"tests":[
             {"name":"N.Outer+Inner.case one","status":"passed","duration":5,"start":1000,"stop":1005,
              "extra":{"type":"N.Outer+Inner"}},
             {"name":"N.Plain.case","status":"passed","duration":7,"start":1000,"stop":1007}]}}"""

    let classes = Profile.classTimings json |> List.map _.Class |> List.sort
    test <@ classes = [ "N.Outer+Inner"; "N.Plain" ] @>

[<Fact>]
let ``classTimings of an unreadable report is empty, never a throw`` () =
    test <@ List.isEmpty (Profile.classTimings "{ not json") @>
    test <@ List.isEmpty (Profile.classTimings "{}") @>

[<Fact>]
let ``a class is contended when the load per core over its own window reaches the threshold`` () =
    let samples: Profile.Sample list =
        [ { At = at 0.0
            Load1 = Some 4.0
            CpuMs = None
            Processes = None }
          { At = at 5.0
            Load1 = Some 40.0
            CpuMs = None
            Processes = None }
          { At = at 10.0
            Load1 = Some 48.0
            CpuMs = None
            Processes = None }
          { At = at 15.0
            Load1 = Some 44.0
            CpuMs = None
            Processes = None }
          { At = at 20.0
            Load1 = Some 2.0
            CpuMs = None
            Processes = None } ]

    // 12 cores: the window 6–14 s is bracketed by the samples at 5 and 15 and holds 10.
    let busy = Profile.contentionOver 12 samples (at 6.0, at 14.0)
    test <@ busy.Samples = 3 @>
    test <@ busy.LoadPerCoreMax = Some 4.0 @>
    test <@ busy.Contended = Some true @>

    // A short class in the quiet start: bracketed by the samples at 0 and 5 only.
    let early = Profile.contentionOver 12 samples (at 0.5, at 1.0)
    test <@ early.Samples = 2 @>
    test <@ early.Contended = Some true @> // the 5 s sample already reads 40/12

    let quiet = Profile.contentionOver 12 [ samples.Head; samples.[4] ] (at 1.0, at 2.0)
    test <@ quiet.Contended = Some false @>

[<Fact>]
let ``with no load samples a class's contention is unknown, not quiet`` () =
    let none = Profile.contentionOver 12 [] (at 0.0, at 1.0)
    test <@ none.Samples = 0 @>
    test <@ none.Contended = None @>

    let noLoad =
        Profile.contentionOver
            12
            [ { At = at 0.0
                Load1 = None
                CpuMs = Some 5.0
                Processes = None } ]
            (at 0.0, at 1.0)

    test <@ noLoad.Contended = None @>

[<Fact>]
let ``every line round-trips through render and tryParse`` () =
    let timing: Profile.ClassTiming =
        { Class = "A.Slow"
          Tests = 2
          DurationMs = 10_000L
          Start = at 0.0
          Stop = at 10.0 }

    let lines: Profile.Line list =
        [ Profile.Start("P.Tests", at 0.0, 12, Some 600, Some 3.5)
          Profile.Start("P.Tests", at 0.0, 12, None, None)
          Profile.Spawn(at 0.1, 4242)
          Profile.Sampled
              { At = at 5.0
                Load1 = Some 7.25
                CpuMs = Some 1234.5
                Processes = None }
          Profile.Sampled
              { At = at 5.0
                Load1 = None
                CpuMs = None
                Processes = None }
          Profile.End(at 10.0, 10_000L, Some 9000.0, Some 2.0, "timed-out")
          Profile.Class(timing, Profile.contentionOver 12 [] (timing.Start, timing.Stop), Profile.ClassSource.Report)
          Profile.ClassesUnavailable "no report: the host was killed at its timeout" ]

    for line in lines do
        let text = Profile.render line
        test <@ not (text.Contains "\n") @>
        test <@ Profile.tryParse text = Some line @>

[<Fact>]
let ``read skips a truncated final line — what a crash mid-write leaves`` () =
    let dir = tempRunDir ()
    let path = Path.Combine(dir, "P.Tests" + Profile.Suffix)

    let whole = Profile.render (Profile.Spawn(at 0.1, 7))
    File.WriteAllText(path, whole + "\n" + """{"event":"sample","at":"2026-""")

    test <@ Profile.read path = [ Profile.Spawn(at 0.1, 7) ] @>
    test <@ List.isEmpty (Profile.read (Path.Combine(dir, "absent.jsonl"))) @>

[<Fact>]
let ``a recorder that is never finished still leaves its start, spawn and samples on disk`` () =
    // The daemon dying mid-run (or the box rebooting) reaches no `finish`. The record
    // written so far is the evidence, so it must already be on disk.
    let dir = tempRunDir ()
    let loads = Collections.Generic.Queue<float>([ 3.0; 30.0; 31.0 ])

    let recorder =
        Profile.start
            dir
            "Killed.Tests"
            (Some 300)
            12
            (fun () -> Some(loads.Dequeue()))
            (fun pid -> if pid = 99 then Some 250.0 else None)
            Threading.Timeout.InfiniteTimeSpan

    Profile.spawned recorder 99
    Profile.sampleNow recorder
    Profile.sampleNow recorder

    // Read while the recorder still holds the file open: no finish, no close.
    let lines = Profile.read (Profile.pathIn dir "Killed.Tests")

    match lines with
    | [ Profile.Start("Killed.Tests", _, 12, Some 300, Some 3.0)
        Profile.Spawn(_, 99)
        Profile.Sampled { Load1 = Some 30.0
                          CpuMs = Some 250.0 }
        Profile.Sampled { Load1 = Some 31.0
                          CpuMs = Some 250.0 } ] -> ()
    | other -> failwith $"unexpected record: %A{other}"

[<Fact>]
let ``finish without a report records the end and why there are no class lines`` () =
    let dir = tempRunDir ()

    let recorder =
        Profile.start
            dir
            "Killed.Tests"
            (Some 2)
            12
            (fun () -> Some 1.0)
            (fun _ -> Some 80.0)
            Threading.Timeout.InfiniteTimeSpan

    Profile.spawned recorder 5
    Profile.sampleNow recorder
    Profile.finish recorder "timed-out" (Error "no report: the host was killed at its timeout")
    // Idempotent: a second finish writes nothing more.
    Profile.finish recorder "passed" (Ok [])

    let lines = Profile.read (Profile.pathIn dir "Killed.Tests")

    let ends =
        lines
        |> List.choose (function
            | Profile.End(_, wall, cpu, _, outcome) -> Some(wall, cpu, outcome)
            | _ -> None)

    match ends with
    | [ (wall, Some 80.0, "timed-out") ] -> test <@ wall >= 0L @>
    | other -> failwith $"unexpected end lines: %A{other}"

    match List.last lines with
    | Profile.ClassesUnavailable reason -> test <@ reason.StartsWith "no report: the host was killed at its timeout" @>
    | other -> failwith $"unexpected last line: %A{other}"

[<Fact>]
let ``finish with class timings writes one class line each, judged against the run's own samples`` () =
    let dir = tempRunDir ()

    let recorder =
        Profile.start dir "P.Tests" None 1 (fun () -> Some 8.0) (fun _ -> None) Threading.Timeout.InfiniteTimeSpan

    Profile.sampleNow recorder

    let timing: Profile.ClassTiming =
        { Class = "A.Slow"
          Tests = 1
          DurationMs = 5L
          Start = DateTime.UtcNow
          Stop = DateTime.UtcNow.AddMilliseconds 5.0 }

    Profile.finish recorder "passed" (Ok [ timing ])

    let classes =
        Profile.read (Profile.pathIn dir "P.Tests")
        |> List.choose (function
            | Profile.Class(t, c, _) -> Some(t.Class, c.Contended)
            | _ -> None)

    // Load 8 on one core: contended, from the class's own line.
    test <@ classes = [ "A.Slow", Some true ] @>

[<Fact>]
let ``a recorder whose file cannot be opened is a no-op, never a failed run`` () =
    let dir = tempRunDir ()
    // A FILE where the run directory should be: the open fails.
    let blocked = Path.Combine(dir, "blocked")
    File.WriteAllText(blocked, "")

    let recorder =
        Profile.start blocked "P.Tests" None 4 (fun () -> Some 1.0) (fun _ -> None) Threading.Timeout.InfiniteTimeSpan

    Profile.spawned recorder 1
    Profile.sampleNow recorder
    Profile.finish recorder "passed" (Ok [])
    test <@ File.ReadAllText blocked = "" @>

[<Fact>]
let ``a host's CPU is its whole tree's — dotnet run, the controller and the test host — and an exited member keeps what it used``
    ()
    =
    let row pid parent : FsHotWatch.ProcessHelper.ProcessRow =
        { Pid = pid
          ParentPid = parent
          Zombie = false
          Command = $"proc %d{pid}" }

    let tables =
        Collections.Generic.Queue<Result<FsHotWatch.ProcessHelper.ProcessRow list, string>>(
            [ Ok [ row 10 1; row 11 10; row 12 11; row 20 1 ]
              // The test host (12) has exited; the controller and `dotnet run` remain.
              Ok [ row 10 1; row 11 10; row 20 1 ]
              Error "`ps` did not answer" ]
        )

    let cpu =
        Collections.Generic.Queue<Map<int, float>>(
            [ Map [ 10, 100.0; 11, 50.0; 12, 1000.0; 20, 9999.0 ]
              Map [ 10, 110.0; 11, 60.0; 20, 9999.0 ]
              Map [ 10, 120.0 ] ]
        )

    let current = ref Map.empty

    let readCpu =
        Profile.treeCpuWith
            1
            (fun () ->
                current.Value <- cpu.Dequeue()
                tables.Dequeue())
            (fun pid -> Map.tryFind pid current.Value)

    // The unrelated process (20) is never counted.
    test <@ readCpu 10 = Some 1150.0 @>
    // 12 is gone: its last reading (1000) still counts.
    test <@ readCpu 10 = Some 1170.0 @>
    // No table: the members already known are re-read; each keeps its last reading.
    test <@ readCpu 10 = Some 1180.0 @>

[<Fact>]
let ``the process table is read only every Nth sample; the known members are re-read in between`` () =
    // `ps -A` measured 67 ms on a busy box: once per 5 s sample per project is over 1%
    // of the run, once per 30 s is not.
    let row pid parent : FsHotWatch.ProcessHelper.ProcessRow =
        { Pid = pid
          ParentPid = parent
          Zombie = false
          Command = $"proc %d{pid}" }

    let tableReads = ref 0
    let cpuOf = ref (Map [ 10, 1.0; 11, 2.0 ])

    let readCpu =
        Profile.treeCpuWith
            3
            (fun () ->
                tableReads.Value <- tableReads.Value + 1
                Ok [ row 10 1; row 11 10 ])
            (fun pid -> Map.tryFind pid cpuOf.Value)

    test <@ readCpu 10 = Some 3.0 @>
    cpuOf.Value <- Map [ 10, 5.0; 11, 7.0 ]
    // Not a table read, yet the child is re-read: it is a known member.
    test <@ readCpu 10 = Some 12.0 @>
    test <@ readCpu 10 = Some 12.0 @>
    test <@ tableReads.Value = 1 @>
    test <@ readCpu 10 = Some 12.0 @>
    test <@ tableReads.Value = 2 @>

[<Fact>]
let ``tryParse refuses every line that is not a whole, known line`` () =
    let refused =
        [ "{ not json"
          "[1,2]"
          "42"
          """{"no":"event"}"""
          """{"event":"unknown"}"""
          """{"event":7}"""
          """{"event":"start","at":"2026-10-21T03:00:00Z","cores":1,"timeoutSec":null,"load1":null}"""
          """{"event":"start","project":"P","at":"not a date","cores":1,"timeoutSec":null,"load1":null}"""
          """{"event":"start","project":"P","at":"2026-10-21T03:00:00Z","cores":"one","timeoutSec":null,"load1":null}"""
          """{"event":"start","project":"P","at":"2026-10-21T03:00:00Z","cores":1,"load1":null}"""
          """{"event":"start","project":"P","at":"2026-10-21T03:00:00Z","cores":1,"timeoutSec":"x","load1":null}"""
          """{"event":"start","project":"P","at":"2026-10-21T03:00:00Z","cores":1,"timeoutSec":null,"load1":[1]}"""
          """{"event":"spawn","at":"2026-10-21T03:00:00Z"}"""
          """{"event":"sample","at":"2026-10-21T03:00:00Z","load1":null}"""
          """{"event":"end","at":"2026-10-21T03:00:00Z","wallMs":1,"cpuMs":null,"load1":null}"""
          """{"event":"class","class":"C","tests":1,"durationMs":1,"start":"2026-10-21T03:00:00Z","stop":"2026-10-21T03:00:00Z","samples":0,"loadPerCoreMean":null,"loadPerCoreMax":null}"""
          """{"event":"classes-unavailable"}""" ]

    for line in refused do
        test <@ Profile.tryParse line = None @>

[<Fact>]
let ``classTimings skips rows it cannot place, and reads a top-level tests array`` () =
    let json =
        """{"tests":[
             null,
             {"name":"NoStart.case","status":"passed","duration":1,"stop":2000},
             {"name":"Bare","status":"passed","start":1000,"stop":1500},
             {"name":"N.Float.case","status":"passed","duration":2.5,"start":1000.0,"stop":1002.0},
             {"status":"passed","start":1,"stop":2}]}"""

    let timings = Profile.classTimings json

    test <@ timings |> List.map _.Class = [ "Bare"; "N.Float" ] @>
    // No duration: the row's own stop - start.
    test <@ timings.Head.DurationMs = 500L @>
    test <@ timings.[1].DurationMs = 2L @>

[<Fact>]
let ``read of a path that is a directory is empty, never a throw`` () =
    let dir = tempRunDir ()
    let asFile = Path.Combine(dir, "P.Tests" + Profile.Suffix)
    Directory.CreateDirectory asFile |> ignore
    test <@ List.isEmpty (Profile.read asFile) @>

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``the recorder samples on its own timer between start and finish`` () =
    let dir = tempRunDir ()
    use sampled = new Threading.ManualResetEventSlim(false)
    let reads = ref 0

    let readLoad () =
        // The first read is `start`'s own; any later one before `finish` is the timer's.
        if Threading.Interlocked.Increment(&reads.contents) >= 2 then
            sampled.Set()

        Some 1.0

    let recorder =
        Profile.start dir "Timed.Tests" None 1 readLoad (fun _ -> None) (TimeSpan.FromMilliseconds 20.0)

    test <@ sampled.Wait(FsHotWatch.Tests.TestHelpers.HangWait) @>
    Profile.finish recorder "passed" (Ok [])

    let samples =
        Profile.read (Profile.pathIn dir "Timed.Tests")
        |> List.filter (function
            | Profile.Sampled _ -> true
            | _ -> false)

    test <@ not samples.IsEmpty @>

[<Fact>]
let ``processCpuMs reads a live process and is None for one that is gone`` () =
    let self = Diagnostics.Process.GetCurrentProcess().Id
    test <@ (Profile.processCpuMs self |> Option.defaultValue -1.0) >= 0.0 @>
    test <@ Profile.processCpuMs Int32.MaxValue = None @>

[<Fact>]
let ``outcomeOf names every result as run-tests reports it`` () =
    let span = TimeSpan.FromSeconds 1.0
    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsNoMatch("", span)) = "no-tests-matched" @>
    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsDeferred "x") = "deferred" @>
    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsErrored "x") = "errored" @>

    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsTimedOut("", span, false, span)) = "timed-out" @>
    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsPassed("", false, span)) = "passed" @>
    test <@ Profile.outcomeOf (FsHotWatch.Events.TestsFailed("", false, span)) = "failed" @>

[<Fact>]
let ``a class line missing any field of its timing is refused`` () =
    let whole =
        [ "class", "\"C\""
          "tests", "1"
          "durationMs", "1"
          "start", "\"2026-10-21T03:00:00Z\""
          "stop", "\"2026-10-21T03:00:01Z\""
          "samples", "0"
          "loadPerCoreMean", "null"
          "loadPerCoreMax", "null"
          "contended", "null"
          "source", "\"console\"" ]

    let lineWithout (dropped: string) =
        let fields =
            whole
            |> List.filter (fun (name, _) -> name <> dropped)
            |> List.map (fun (name, value) -> $"\"%s{name}\":%s{value}")
            |> String.concat ","

        $"{{\"event\":\"class\",%s{fields}}}"

    test <@ (Profile.tryParse (lineWithout "none")).IsSome @>

    for name, _ in whole |> List.filter (fun (n, _) -> n <> "source") do
        test <@ Profile.tryParse (lineWithout name) = None @>

    // A line written before class lines had a source (0.13.0-alpha.93) is a report's.
    match Profile.tryParse (lineWithout "source") with
    | Some(Profile.Class(_, _, source)) -> test <@ source = Profile.ClassSource.Report @>
    | other -> failwith $"a sourceless class line did not parse: %A{other}"

    // A source that is neither of the two is no line either.
    test <@ Profile.tryParse ((lineWithout "source").Replace("}", ",\"source\":\"tea leaves\"}")) = None @>

[<Fact>]
let ``classTimings reads a blank extra.type as absent, and takes the class from the name`` () =
    let json =
        """{"results":{"tests":[{"name":"N.Named.case","status":"passed","duration":1,"start":1,"stop":2,
             "extra":{"type":""}}]}}"""

    test <@ Profile.classTimings json |> List.map _.Class = [ "N.Named" ] @>

[<Fact>]
let ``a finished recorder ignores later spawns and samples`` () =
    let dir = tempRunDir ()

    let recorder =
        Profile.start
            dir
            "Late.Tests"
            None
            1
            (fun () -> Some 1.0)
            (fun _ -> Some 1.0)
            Threading.Timeout.InfiniteTimeSpan

    Profile.finish recorder "passed" (Ok [])
    let before = Profile.read (Profile.pathIn dir "Late.Tests")
    Profile.spawned recorder 3
    Profile.sampleNow recorder
    test <@ Profile.read (Profile.pathIn dir "Late.Tests") = before @>

[<Fact>]
let ``a tree none of whose members will report CPU has no CPU reading`` () =
    let readCpu = Profile.treeCpuWith 1 (fun () -> Error "no table") (fun _ -> None)
    test <@ readCpu 10 = None @>
