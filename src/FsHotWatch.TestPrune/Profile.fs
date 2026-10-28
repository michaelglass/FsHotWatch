/// The per-project profile of one test run: `.fshw/test-runs/<runId>/<Project>.profile.jsonl`
/// (AUTOMATION-1300).
///
/// Answers "which classes eat the budget, and were they slow or starved?" from the run
/// directory alone. On 2026-10-21 that question took hours: the projects that mattered
/// had been killed at their timeout, which leaves no CTRF report, and nothing else had
/// recorded the load they ran under.
///
/// One JSON object per line, written as it happens (`AutoFlush`), by fshw rather than by
/// the test host — so a host SIGKILLed at its timeout cannot truncate it, and a daemon
/// that dies mid-run leaves everything up to its last sample:
///
///   * `start`  — cores, the project's `timeoutSec`, the 1-minute load at launch;
///   * `spawn`  — the host's pid (once per launch; a relaunch adds another);
///   * `sample` — every `DefaultInterval`: the load, and the CPU time of the host's process
///                tree so far (`treeCpu`);
///   * `end`    — wall time, the tree's last-seen CPU time, the load, the outcome;
///   * `class`  — per class of the CTRF report: tests, summed duration, the span from its
///                first start to its last stop, and the load per core over THAT window,
///                so a slow class says on its own line whether the box was contended.
///                A host killed before it wrote a report still gets them, from the
///                per-test result lines it printed as each test finished (`observe`, fed
///                the output stream the run log already tees); `source` says which;
///   * `classes-unavailable` — instead of class lines, why there are none;
///   * `running` — with no report, each test xUnit reported long-running (`--long-running`)
///                that never finished: the test the host was running when it ended, and
///                when it started.
///
/// Not a verdict input: a profile describes how a run went, never whether it passed.
module FsHotWatch.TestPrune.Profile

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch

/// The suffix the profile is named with, beside `<Project>.ctrf.json`.
[<Literal>]
let Suffix = ".profile.jsonl"

/// Where a project's profile lives inside a run's directory.
let pathIn (runDir: string) (project: string) : string = Path.Combine(runDir, project + Suffix)

/// One class's share of a report.
type ClassTiming =
    {
        /// The row's `extra.type` (the runner's class, `Outer+Inner` for a nested one).
        Class: string
        /// Rows of that class.
        Tests: int
        /// The rows' durations, summed: the class's own cost, whatever ran beside it.
        DurationMs: int64
        /// The earliest row start (UTC).
        Start: DateTime
        /// The latest row stop (UTC). `Stop - Start` is the class's wall span.
        Stop: DateTime
    }

/// One reading taken while the host ran.
type Sample =
    {
        At: DateTime
        /// The 1-minute load average; `None` where the platform has none.
        Load1: float option
        /// The CPU time of the host's process tree so far (`treeCpu`); `None` before the spawn,
        /// or when the OS will not say.
        CpuMs: float option
    }

/// The load a class ran under, read from the run's own samples.
type Contention =
    {
        /// The samples the judgement rests on.
        Samples: int
        LoadPerCoreMean: float option
        LoadPerCoreMax: float option
        /// Mean load per core ≥ `ContendedLoadPerCore`; `None` when no sample read a load
        /// — unknown is never reported as quiet.
        Contended: bool option
    }

/// At one runnable thread per core the class could no longer count on a whole core.
/// The raw load figures are on the line too, so a reader can apply its own threshold.
[<Literal>]
let ContendedLoadPerCore = 1.0

/// A 600 s suite yields ~120 samples; each costs one `getloadavg`, one process-info call
/// and one short line.
let DefaultInterval = TimeSpan.FromSeconds 5.0

/// A result's name on an `end` line — the same strings `run-tests` reports a project's
/// status by.
let outcomeOf (result: FsHotWatch.Events.TestResult) : string =
    match result with
    | FsHotWatch.Events.TestsNoMatch _ -> "no-tests-matched"
    | FsHotWatch.Events.TestsPassed _ -> "passed"
    | FsHotWatch.Events.TestsFailed _ -> "failed"
    | FsHotWatch.Events.TestsTimedOut _ -> "timed-out"
    | FsHotWatch.Events.TestsDeferred _ -> "deferred"
    | FsHotWatch.Events.TestsErrored _ -> "errored"

/// Where a class line's timings came from.
[<RequireQualifiedAccess>]
type ClassSource =
    /// The CTRF report's rows.
    | Report
    /// The per-test result lines the host printed (a host killed before it wrote a report).
    | Console

/// The processes of one command seen in a host's tree.
type ChildCommand =
    { Command: string
      Processes: int
      CpuMs: float }

/// One line of a profile.
type Line =
    | Start of project: string * at: DateTime * cores: int * timeoutSec: int option * load1: float option
    | Spawn of at: DateTime * pid: int
    | Sampled of Sample
    | End of at: DateTime * wallMs: int64 * cpuMs: float option * load1: float option * outcome: string
    | Class of ClassTiming * Contention * ClassSource
    | ClassesUnavailable of reason: string
    /// A test still running when the host ended: reported long-running by xUnit, never
    /// finished. `since` is when it started.
    | Running of test: string * since: DateTime
    /// The processes of one command seen in the host's tree, and the CPU time they used.
    | Children of ChildCommand

// ---------------------------------------------------------------------------------------
// The report's classes
// ---------------------------------------------------------------------------------------

let private stringOf (node: JsonNode) (name: string) : string option =
    match node with
    | null -> None
    | n ->
        match n[name] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s when not (String.IsNullOrEmpty s) -> Some s
            | _ -> None
        | _ -> None

let private int64Of (node: JsonNode) (name: string) : int64 option =
    match node[name] with
    | :? JsonValue as v ->
        match v.TryGetValue<int64>() with
        | true, n -> Some n
        | _ ->
            match v.TryGetValue<float>() with
            | true, f -> Some(int64 f)
            | _ -> None
    | _ -> None

let private ofEpochMs (ms: int64) =
    DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime

/// A row's class: `extra.type` when the runner wrote one, otherwise its name up to the
/// last dot (the same rule `Starvation` reads a row by).
let private classOf (row: JsonNode) : string option =
    match stringOf row["extra"] "type" with
    | Some t -> Some t
    | None ->
        stringOf row "name"
        |> Option.map (fun name ->
            let dot = name.LastIndexOf '.'
            if dot > 0 then name.Substring(0, dot) else name)

/// Rows of (class, start, stop, duration ms) as one timing per class, slowest span first.
let private timingsOf (rows: (string * DateTime * DateTime * int64) list) : ClassTiming list =
    rows
    |> List.groupBy (fun (cls, _, _, _) -> cls)
    |> List.map (fun (cls, rows) ->
        { Class = cls
          Tests = rows.Length
          DurationMs = rows |> List.sumBy (fun (_, _, _, d) -> d)
          Start = rows |> List.map (fun (_, s, _, _) -> s) |> List.min
          Stop = rows |> List.map (fun (_, _, s, _) -> s) |> List.max })
    |> List.sortByDescending (fun t -> t.Stop - t.Start, t.Class)

/// The classes of a CTRF report, slowest span first. Rows without a class, a start or a
/// stop are skipped; an unreadable report has no classes.
let classTimings (ctrfJson: string) : ClassTiming list =
    let rows =
        try
            match JsonNode.Parse ctrfJson with
            | null -> []
            | root ->
                let tests =
                    match root["results"] with
                    | null -> root["tests"]
                    | results -> results["tests"]

                match tests with
                | :? JsonArray as arr -> arr |> Seq.filter (isNull >> not) |> Seq.toList
                | _ -> []
        with :? JsonException ->
            []

    rows
    |> List.choose (fun row ->
        match classOf row, int64Of row "start", int64Of row "stop" with
        | Some cls, Some start, Some stop ->
            let duration = int64Of row "duration" |> Option.defaultValue (stop - start)
            Some(cls, ofEpochMs start, ofEpochMs stop, duration)
        | _ -> None)
    |> timingsOf

// ---------------------------------------------------------------------------------------
// The console's per-test results
// ---------------------------------------------------------------------------------------

/// A Microsoft.Testing.Platform host asked for `--output Detailed` prints one line as each
/// test finishes: `passed <name> (4ms)`, `failed (canceled) <name> (2m 3s 4ms)`,
/// `skipped <name> (0ms)`. At column 0: the test output it prints under a result is
/// indented, so an indented line is never a result, whatever it says.
let private completionShape =
    Text.RegularExpressions.Regex(
        @"^(passed|failed|skipped) (?:\([^)]*\) )?(\S.*) \(((?:\d+(?:\.\d+)? ?(?:ms|s|m|h|d) ?)+)\)$",
        Text.RegularExpressions.RegexOptions.Compiled
    )

let private durationPart =
    Text.RegularExpressions.Regex(@"(\d+(?:\.\d+)?) ?(ms|s|m|h|d)", Text.RegularExpressions.RegexOptions.Compiled)

/// The same CSI sequences `TestPrunePlugin.ConsoleText` removes: MTP colours the verb.
let private ansiSequence =
    Text.RegularExpressions.Regex(@"\u001b\[[0-9;?]*[ -/]*[@-~]", Text.RegularExpressions.RegexOptions.Compiled)

/// A console line as words: colour removed, trailing space and `\r` dropped. A host in CI
/// (`CI`, `GITHUB_ACTIONS`, `TF_BUILD` set) colours its output even when it is redirected,
/// so every line a parser reads goes through this first.
let private plainLine (line: string) : string =
    (if line.Contains '\u001b' then
         ansiSequence.Replace(line, "")
     else
         line)
        .TrimEnd()

/// A runner's per-test result line as (outcome, test name, duration ms); `None` for any
/// other line.
let parseCompletion (line: string) : (string * string * int64) option =
    let plain = plainLine line

    let m = completionShape.Match plain

    if m.Success then
        let ms =
            durationPart.Matches m.Groups[3].Value
            |> Seq.sumBy (fun part ->
                let value =
                    Double.Parse(part.Groups[1].Value, Globalization.CultureInfo.InvariantCulture)

                let unit =
                    match part.Groups[2].Value with
                    | "ms" -> 1.0
                    | "s" -> 1_000.0
                    | "m" -> 60_000.0
                    | "h" -> 3_600_000.0
                    | _ -> 86_400_000.0

                value * unit)

        Some(m.Groups[1].Value, m.Groups[2].Value, int64 (Math.Round ms))
    else
        None

/// The class of a console test name: the longest prefix ending before a `.` that holds no
/// space and no `(`. A class name has neither, while a backticked method (`reads
/// .fshw.json`) or a theory's arguments (`m(x: 1.5)`) may hold dots after one; the report's
/// `extra.type`, which a killed host never wrote, is not there to say. A name with no
/// such prefix is its own class.
let classOfTestName (name: string) : string =
    let stop = name.IndexOfAny [| ' '; '(' |]
    let searchable = if stop < 0 then name.Length - 1 else stop

    match
        if searchable > 0 then
            name.LastIndexOf('.', searchable - 1)
        else
            -1
    with
    | dot when dot > 0 -> name.Substring(0, dot)
    | _ -> name

/// xUnit's report of a test still running past its long-running threshold (`--long-running`,
/// printed when diagnostic messages are on): `[Long Running Test] '<name>', Elapsed: hh:mm:ss`,
/// at column 0 like a result line.
let private longRunningShape =
    Text.RegularExpressions.Regex(
        @"^\[Long Running Test\] '(.+)', Elapsed: (\d+):(\d\d):(\d\d)$",
        Text.RegularExpressions.RegexOptions.Compiled
    )

/// A long-running report as (test name, elapsed ms); `None` for any other line.
let parseLongRunning (line: string) : (string * int64) option =
    let m = longRunningShape.Match(plainLine line)

    if m.Success then
        let part (i: int) = int64 m.Groups[i].Value
        Some(m.Groups[1].Value, ((part 2 * 60L + part 3) * 60L + part 4) * 1000L)
    else
        None

/// A partial line longer than this is not a result line (a name is never this long); it
/// is dropped up to its newline rather than held.
[<Literal>]
let MaxPartialLine = 64 * 1024

/// The load a window ran under: every sample inside it, plus the nearest sample on each
/// side — a class shorter than the sampling interval still has the two readings that
/// bracket it.
let contentionOver (cores: int) (samples: Sample list) (windowStart: DateTime, windowStop: DateTime) : Contention =
    let ordered = samples |> List.sortBy _.At

    let before = ordered |> List.filter (fun s -> s.At < windowStart) |> List.tryLast

    let inside =
        ordered |> List.filter (fun s -> s.At >= windowStart && s.At <= windowStop)

    let after = ordered |> List.tryFind (fun s -> s.At > windowStop)

    let used = Option.toList before @ inside @ Option.toList after

    let perCore =
        used |> List.choose _.Load1 |> List.map (fun l -> l / float (max 1 cores))

    match perCore with
    | [] ->
        { Samples = used.Length
          LoadPerCoreMean = None
          LoadPerCoreMax = None
          Contended = None }
    | loads ->
        let mean = List.average loads

        { Samples = used.Length
          LoadPerCoreMean = Some mean
          LoadPerCoreMax = Some(List.max loads)
          Contended = Some(mean >= ContendedLoadPerCore) }

// ---------------------------------------------------------------------------------------
// Lines
// ---------------------------------------------------------------------------------------

let private iso (at: DateTime) = at.ToUniversalTime().ToString("O")

let private optNumber (value: float option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create v
    | None -> null

let private optInt (value: int option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create v
    | None -> null

let private optBool (value: bool option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create v
    | None -> null

let private objectOf (fields: (string * JsonNode) list) : JsonObject =
    let o = JsonObject()

    for name, value in fields do
        o[name] <- value

    o

/// One line of JSON, no newline.
let render (line: Line) : string =
    let str (s: string) : JsonNode = JsonValue.Create s
    let num (f: float) : JsonNode = JsonValue.Create f
    let i64 (n: int64) : JsonNode = JsonValue.Create n
    let i32 (n: int) : JsonNode = JsonValue.Create n

    let fields =
        match line with
        | Start(project, at, cores, timeoutSec, load1) ->
            [ "event", str "start"
              "project", str project
              "at", str (iso at)
              "cores", i32 cores
              "timeoutSec", optInt timeoutSec
              "load1", optNumber load1 ]
        | Spawn(at, pid) -> [ "event", str "spawn"; "at", str (iso at); "pid", i32 pid ]
        | Sampled s ->
            [ "event", str "sample"
              "at", str (iso s.At)
              "load1", optNumber s.Load1
              "cpuMs", optNumber s.CpuMs ]
        | End(at, wallMs, cpuMs, load1, outcome) ->
            [ "event", str "end"
              "at", str (iso at)
              "wallMs", i64 wallMs
              "cpuMs", optNumber cpuMs
              "load1", optNumber load1
              "outcome", str outcome ]
        | Class(t, c, source) ->
            [ "event", str "class"
              "source",
              str (
                  match source with
                  | ClassSource.Report -> "report"
                  | ClassSource.Console -> "console"
              )
              "class", str t.Class
              "tests", i32 t.Tests
              "durationMs", i64 t.DurationMs
              "start", str (iso t.Start)
              "stop", str (iso t.Stop)
              "spanMs", num (t.Stop - t.Start).TotalMilliseconds
              "samples", i32 c.Samples
              "loadPerCoreMean", optNumber c.LoadPerCoreMean
              "loadPerCoreMax", optNumber c.LoadPerCoreMax
              "contended", optBool c.Contended ]
        | ClassesUnavailable reason -> [ "event", str "classes-unavailable"; "reason", str reason ]
        | Running(test, since) -> [ "event", str "running"; "test", str test; "since", str (iso since) ]
        | Children c ->
            [ "event", str "children"
              "command", str c.Command
              "processes", i32 c.Processes
              "cpuMs", num c.CpuMs ]

    (objectOf fields).ToJsonString()

/// A field of the type asked for; `None` when absent or of another type.
let private typed<'T> (o: JsonObject) (name: string) : 'T option =
    match o[name] with
    | :? JsonValue as v ->
        match v.TryGetValue<'T>() with
        | true, x -> Some x
        | _ -> None
    | _ -> None

/// A present-but-null optional field is `Some None`; an absent or mistyped one is `None`.
let private optional<'T> (o: JsonObject) (name: string) : 'T option option =
    let mutable node: JsonNode = null

    match o.TryGetPropertyValue(name, &node), node with
    | true, null -> Some None
    | true, (:? JsonValue as v) ->
        match v.TryGetValue<'T>() with
        | true, x -> Some(Some x)
        | _ -> None
    | _ -> None

/// Parse one line; `None` for anything that is not a whole, known line.
let tryParse (text: string) : Line option =
    let date (o: JsonObject) (name: string) : DateTime option =
        typed<string> o name
        |> Option.bind (fun s ->
            match
                DateTime.TryParse(
                    s,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.RoundtripKind
                )
            with
            | true, d -> Some(d.ToUniversalTime())
            | _ -> None)

    try
        match JsonNode.Parse text with
        | :? JsonObject as o ->
            match typed<string> o "event" with
            | Some "start" ->
                match
                    typed<string> o "project",
                    date o "at",
                    typed<int> o "cores",
                    optional<int> o "timeoutSec",
                    optional<float> o "load1"
                with
                | Some p, Some at, Some cores, Some t, Some l -> Some(Start(p, at, cores, t, l))
                | _ -> None
            | Some "spawn" ->
                match date o "at", typed<int> o "pid" with
                | Some at, Some pid -> Some(Spawn(at, pid))
                | _ -> None
            | Some "sample" ->
                match date o "at", optional<float> o "load1", optional<float> o "cpuMs" with
                | Some at, Some l, Some c -> Some(Sampled { At = at; Load1 = l; CpuMs = c })
                | _ -> None
            | Some "end" ->
                match
                    date o "at",
                    typed<int64> o "wallMs",
                    optional<float> o "cpuMs",
                    optional<float> o "load1",
                    typed<string> o "outcome"
                with
                | Some at, Some w, Some c, Some l, Some outcome -> Some(End(at, w, c, l, outcome))
                | _ -> None
            | Some "class" ->
                let timing =
                    match
                        typed<string> o "class",
                        typed<int> o "tests",
                        typed<int64> o "durationMs",
                        date o "start",
                        date o "stop"
                    with
                    | Some cls, Some tests, Some d, Some s, Some e ->
                        Some
                            { Class = cls
                              Tests = tests
                              DurationMs = d
                              Start = s
                              Stop = e }
                    | _ -> None

                let contention =
                    match
                        typed<int> o "samples",
                        optional<float> o "loadPerCoreMean",
                        optional<float> o "loadPerCoreMax",
                        optional<bool> o "contended"
                    with
                    | Some n, Some mean, Some mx, Some c ->
                        Some
                            { Samples = n
                              LoadPerCoreMean = mean
                              LoadPerCoreMax = mx
                              Contended = c }
                    | _ -> None

                let source =
                    match o.ContainsKey "source", typed<string> o "source" with
                    // Written before class lines had a source (0.13.0-alpha.93): from a report.
                    | false, _ -> Some ClassSource.Report
                    | true, Some "report" -> Some ClassSource.Report
                    | true, Some "console" -> Some ClassSource.Console
                    | true, _ -> None

                match timing, contention, source with
                | Some t, Some c, Some s -> Some(Class(t, c, s))
                | _ -> None
            | Some "classes-unavailable" -> typed<string> o "reason" |> Option.map ClassesUnavailable
            | Some "children" ->
                match typed<string> o "command", typed<int> o "processes", typed<float> o "cpuMs" with
                | Some command, Some processes, Some cpu ->
                    Some(
                        Children
                            { Command = command
                              Processes = processes
                              CpuMs = cpu }
                    )
                | _ -> None
            | Some "running" ->
                match typed<string> o "test", date o "since" with
                | Some test, Some since -> Some(Running(test, since))
                | _ -> None
            | _ -> None
        | _ -> None
    with :? JsonException ->
        None

/// Every whole line of a profile, in order. A missing file reads as empty, and a line a
/// crash cut short is skipped — the lines before it are the record.
let read (path: string) : Line list =
    try
        if File.Exists path then
            // FileShare.ReadWrite: a profile still being written is readable.
            use stream =
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

            use reader = new StreamReader(stream)

            reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun l -> tryParse (l.Trim()))
            |> Array.toList
        else
            []
    with
    | :? IOException
    | :? UnauthorizedAccessException -> []

// ---------------------------------------------------------------------------------------
// The recorder
// ---------------------------------------------------------------------------------------

/// One process's CPU time, while it is alive; `None` once it has exited or when the OS
/// refuses. That process only; `treeCpu` sums a tree.
let processCpuMs (pid: int) : float option =
    try
        use p = Process.GetProcessById pid
        Some p.TotalProcessorTime.TotalMilliseconds
    with
    | :? ArgumentException
    | :? InvalidOperationException
    | :? ComponentModel.Win32Exception
    | :? NotSupportedException -> None

/// The program a process-table command line runs: its first word's file name, or for
/// `dotnet <x>.dll` the assembly's name (`testhost`), so the host is told apart from the
/// `dotnet` CLI that launched it. `?` for an empty line.
let commandName (commandLine: string) : string =
    let words =
        commandLine.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

    let fileName (path: string) =
        match path.LastIndexOfAny [| '/'; '\\' |] with
        | -1 -> path
        | i -> path.Substring(i + 1)

    match words |> Array.toList with
    | [] -> "?"
    | first :: second :: _ when
        fileName first = "dotnet"
        && second.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ->
        Path.GetFileNameWithoutExtension(fileName second)
    | first :: _ -> fileName first

/// A host's process tree, as one launch reads it: its CPU time, and its members by command.
[<NoComparison; NoEquality>]
type ProcessTree =
    {
        /// The CPU time of the tree rooted at the pid, for `Recorder`'s `readCpu`.
        Cpu: int -> float option
        /// Every member seen so far, by command, most CPU first. A process that lived and
        /// died between two table reads is never seen, so the counts are a lower bound.
        Children: unit -> ChildCommand list
    }

/// The CPU time of the host's whole process tree, for `Recorder`'s `readCpu`.
///
/// The pid fshw spawns is rarely the test host: under `dotnet run` it is the CLI, which
/// starts a coverage controller, which starts the host. Every call re-reads the CPU time
/// of each member known so far and sums every member ever seen at its last reading — so a
/// member that has exited still counts what it used up to the sample before it went.
///
/// Membership comes from the process table, read on the first call and then every
/// `refreshEvery` calls: `ps -A` measured 67 ms on a loaded box, over 1% of a 5 s
/// interval, while a per-pid CPU read costs microseconds. A table that cannot be read
/// keeps the members already known.
///
/// Stateful (the readings so far): one per project launch.
let internal processTreeWith
    (refreshEvery: int)
    (readTable: unit -> Result<ProcessHelper.ProcessRow list, string>)
    (readPid: int -> float option)
    : ProcessTree =
    let lastSeen = Collections.Generic.Dictionary<int, float>()
    let members = Collections.Generic.HashSet<int>()
    // Each member's command, as the table named it when it was first seen.
    let commands = Collections.Generic.Dictionary<int, string>()
    let calls = ref 0
    let gate = obj ()

    { Cpu =
        fun root ->
            lock gate (fun () ->
                if calls.Value % max 1 refreshEvery = 0 then
                    match readTable () with
                    | Ok rows ->
                        ProcessHelper.treeOf root rows
                        |> List.iter (fun r ->
                            if members.Add r.Pid then
                                commands[r.Pid] <- commandName r.Command)
                    | Error _ -> ()

                calls.Value <- calls.Value + 1
                members.Add root |> ignore

                for pid in members do
                    readPid pid |> Option.iter (fun cpu -> lastSeen[pid] <- cpu)

                if lastSeen.Count = 0 then
                    None
                else
                    Some(Seq.sum lastSeen.Values))
      Children =
        fun () ->
            lock gate (fun () ->
                members
                |> Seq.toList
                |> List.groupBy (fun pid ->
                    match commands.TryGetValue pid with
                    | true, c -> c
                    | _ -> "?")
                |> List.map (fun (command, pids) ->
                    { Command = command
                      Processes = pids.Length
                      CpuMs =
                        pids
                        |> List.sumBy (fun pid ->
                            match lastSeen.TryGetValue pid with
                            | true, cpu -> cpu
                            | _ -> 0.0) })
                |> List.sortBy (fun c -> -c.CpuMs, c.Command)) }

/// The tree's CPU time alone — `processTreeWith`'s `Cpu`.
let internal treeCpuWith
    (refreshEvery: int)
    (readTable: unit -> Result<ProcessHelper.ProcessRow list, string>)
    (readPid: int -> float option)
    : int -> float option =
    (processTreeWith refreshEvery readTable readPid).Cpu

/// `processTreeWith` over the live process table, read every 6th sample.
let processTree () : ProcessTree =
    processTreeWith 6 (fun () -> ProcessHelper.readProcessTableWithin (TimeSpan.FromSeconds 2.0)) processCpuMs

/// `processTree`'s CPU reading alone.
let treeCpu () : int -> float option = (processTree ()).Cpu

/// A profile being written. Opaque: `start` opens it, `finish` closes it.
[<NoComparison; NoEquality>]
type Recorder =
    private
        {
            Write: (string -> unit) option
            Close: unit -> unit
            Gate: obj
            Clock: Stopwatch
            Cores: int
            ReadLoad: unit -> float option
            ReadCpu: int -> float option
            mutable Pid: int option
            mutable Samples: Sample list
            mutable Finished: bool
            mutable Timer: Threading.Timer option
            /// Guards the console state below, apart from `Gate`: a sample holds `Gate` through a
            /// process-table read, and the output pump must never wait on one.
            ConsoleGate: obj
            /// The output since the last newline.
            Partial: Text.StringBuilder
            /// Inside a line past `MaxPartialLine`: dropped up to its newline.
            mutable Discarding: bool
            /// The results the host printed, newest first: (arrived, name, duration ms).
            mutable Completions: (DateTime * string * int64) list
            /// The tests reported long-running, by name: when each started, by its newest report.
            mutable LongRunning: Map<string, DateTime>
            /// The host tree's children by command, read at `finish` (`trackChildren`).
            mutable ReadChildren: (unit -> ChildCommand list) option
            mutable ConsoleClosed: bool
        }

let private emit (r: Recorder) (line: Line) =
    match r.Write with
    | Some write ->
        try
            write (render line + "\n")
        with ex when RunLog.isExpectedFileFailure ex ->
            ()
    | None -> ()

/// Take one sample now. A no-op after `finish`.
let sampleNow (r: Recorder) : unit =
    lock r.Gate (fun () ->
        if not r.Finished then
            let sample =
                { At = DateTime.UtcNow
                  Load1 = r.ReadLoad()
                  CpuMs = r.Pid |> Option.bind r.ReadCpu }

            r.Samples <- sample :: r.Samples
            emit r (Sampled sample))

/// Open `<project>.profile.jsonl` in `runDir`, write its `start` line, and sample every
/// `interval` (`Timeout.InfiniteTimeSpan`: only when `sampleNow` is called).
///
/// Never throws: a profile that cannot be written loses the profile, never the run.
let start
    (runDir: string)
    (project: string)
    (timeoutSec: int option)
    (cores: int)
    (readLoad: unit -> float option)
    (readCpu: int -> float option)
    (interval: TimeSpan)
    : Recorder =
    let path = pathIn runDir project

    let write, close =
        try
            Directory.CreateDirectory runDir |> ignore

            let stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)

            let writer = new StreamWriter(stream)
            writer.AutoFlush <- true

            Some(writer.Write: string -> unit),
            fun () ->
                try
                    writer.Dispose()
                with ex when RunLog.isExpectedFileFailure ex ->
                    ()
        with ex when RunLog.isExpectedFileFailure ex ->
            Logging.warn "test-prune" $"%s{project}: not profiling this run — could not open %s{path}: %s{ex.Message}"
            None, ignore

    let recorder =
        { Write = write
          Close = close
          Gate = obj ()
          Clock = Stopwatch.StartNew()
          Cores = cores
          ReadLoad = readLoad
          ReadCpu = readCpu
          Pid = None
          Samples = []
          Finished = false
          Timer = None
          ConsoleGate = obj ()
          Partial = Text.StringBuilder()
          Discarding = false
          Completions = []
          LongRunning = Map.empty
          ReadChildren = None
          ConsoleClosed = false }

    emit recorder (Start(project, DateTime.UtcNow, cores, timeoutSec, readLoad ()))

    if write.IsSome && interval <> Threading.Timeout.InfiniteTimeSpan then
        recorder.Timer <- Some(new Threading.Timer((fun _ -> sampleNow recorder), null, interval, interval))

    recorder

/// The host is running as `pid`: its CPU time is sampled from now on.
let spawned (r: Recorder) (pid: int) : unit =
    lock r.Gate (fun () ->
        if not r.Finished then
            r.Pid <- Some pid
            emit r (Spawn(DateTime.UtcNow, pid)))

let private takeLine (r: Recorder) (at: DateTime) (line: string) =
    match parseCompletion line with
    | Some(_, name, ms) -> r.Completions <- (at, name, ms) :: r.Completions
    | None ->
        match parseLongRunning line with
        | Some(name, elapsed) -> r.LongRunning <- Map.add name (at.AddMilliseconds(float -elapsed)) r.LongRunning
        | None -> ()

/// A chunk of the host's output, as it arrived at `at`: every whole line in it that is a
/// per-test result (`parseCompletion`) is kept as a test that finished at `at`, and the
/// rest of a line waits for its newline. Feed it every chunk, in order (the run log's sink
/// does). A no-op after `finish`; never throws.
///
/// Held in memory, not written per test: the class lines are written by `finish`, which
/// every path out of a launch reaches, a killed host's included. A daemon that dies first
/// loses them; the run log still holds the lines.
let observeAt (r: Recorder) (at: DateTime) (chunk: string) : unit =
    if not (isNull chunk) then
        lock r.ConsoleGate (fun () ->
            if not r.ConsoleClosed then
                let mutable from = 0

                while from < chunk.Length do
                    match chunk.IndexOf('\n', from) with
                    | -1 ->
                        if not r.Discarding then
                            r.Partial.Append(chunk, from, chunk.Length - from) |> ignore

                            if r.Partial.Length > MaxPartialLine then
                                r.Partial.Clear() |> ignore
                                r.Discarding <- true

                        from <- chunk.Length
                    | newline ->
                        if r.Discarding then
                            r.Discarding <- false
                        else
                            r.Partial.Append(chunk, from, newline - from) |> ignore
                            takeLine r at (r.Partial.ToString())

                        r.Partial.Clear() |> ignore
                        from <- newline + 1)

/// `observeAt` now.
let observe (r: Recorder) (chunk: string) : unit = observeAt r DateTime.UtcNow chunk

/// The classes of the results the host printed, slowest span first (each test ran for its
/// printed duration up to the moment its line arrived), and the tests reported long-running
/// that never finished, with when each started, earliest first. Closes the console: nothing
/// is observed after.
let private consoleState (r: Recorder) : ClassTiming list * (string * DateTime) list =
    lock r.ConsoleGate (fun () ->
        r.ConsoleClosed <- true
        r.Partial.Clear() |> ignore

        let finished = r.Completions |> List.map (fun (_, name, _) -> name) |> Set.ofList

        r.Completions
        |> List.map (fun (at, name, ms) -> classOfTestName name, at.AddMilliseconds(float -ms), at, ms)
        |> timingsOf,
        r.LongRunning
        |> Map.toList
        |> List.filter (fun (name, _) -> not (finished.Contains name))
        |> List.sortBy (fun (name, since) -> since, name))

/// At `finish`, also write the host tree's children by command, as `read` returns them
/// (`ProcessTree.Children`).
let trackChildren (r: Recorder) (read: unit -> ChildCommand list) : unit =
    lock r.Gate (fun () -> r.ReadChildren <- Some read)

/// Write the `end` line and the class lines (or why there are none), and close. Only the
/// first call writes.
let finish (r: Recorder) (outcome: string) (classes: Result<ClassTiming list, string>) : unit =
    let first =
        lock r.Gate (fun () ->
            if r.Finished then
                false
            else
                r.Finished <- true
                true)

    if first then
        r.Timer |> Option.iter _.Dispose()
        r.Clock.Stop()

        lock r.Gate (fun () ->
            let samples = List.rev r.Samples
            let lastCpu = samples |> List.choose _.CpuMs |> List.tryLast

            emit r (End(DateTime.UtcNow, int64 r.Clock.Elapsed.TotalMilliseconds, lastCpu, r.ReadLoad(), outcome))

            let classLines source timings =
                for t in timings do
                    emit r (Class(t, contentionOver r.Cores samples (t.Start, t.Stop), source))

            // The report, when there is one; else what the host printed before it ended.
            // With no report, also the tests the host was still running when it ended.
            match classes, consoleState r with
            | Ok timings, _ -> classLines ClassSource.Report timings
            | Error reason, (printed, running) ->
                match printed with
                | [] ->
                    emit
                        r
                        (ClassesUnavailable $"%s{reason}; and the host printed no per-test result line before it ended")
                | _ -> classLines ClassSource.Console printed

                for name, since in running do
                    emit r (Running(name, since))

            // The host tree's children, by command. A reader that fails loses them, never the run.
            match r.ReadChildren with
            | Some read ->
                try
                    for c in read () do
                        emit r (Children c)
                with ex ->
                    Logging.warn "test-prune" $"could not read the host's child processes: %s{ex.Message}"
            | None -> ()

            r.Close())
