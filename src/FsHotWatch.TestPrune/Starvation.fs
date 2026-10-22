/// A test project's declared behaviour under CPU starvation, the load measured while it
/// ran, and what a timeout under that load is allowed to become.
///
/// A bounded unit that runs out of wall clock on a saturated box is not evidence about
/// the code. On 2026-10-20/21 every land on one host at load 30–70 went red on a suite
/// overrunning its 600 s budget, with 60 s per-test cancellations as the knock-on, and
/// every class passed when re-run alone. Nothing told those reds apart from real ones.
///
/// The rule this module encodes, and nothing looser:
///
///   * a project DECLARES that a timeout under measured starvation means "re-run alone"
///     (`.fshw.json` `tests.projects[].starvation`), optionally for named classes only;
///   * fshw SAMPLES the load average while the project runs;
///   * a red qualifies only when every failure is a timeout — the project's own
///     `timeoutSec`, or per-test timeouts in a report whose every failed row is one —
///     and most of the samples were over the threshold. An assertion failure is never
///     re-run;
///   * a qualifying project is re-run ONCE, after every other project of the run has
///     finished, and its second result is the one reported. The first attempt is kept
///     beside it and the verdict records "starved, then passed alone" (or "still red")
///     apart from a clean pass.
module FsHotWatch.TestPrune.Starvation

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch
open FsHotWatch.Events

/// What a project declared. Its presence is the declaration: there is one behaviour,
/// "re-run alone", and a project that does not declare it is never re-run.
type Policy =
    {
        /// The classes the declaration covers; empty covers the whole project.
        Classes: string list
        /// A sample is starved when the 1-minute load average per core is at least this.
        LoadPerCore: float
        /// A run is starved when at least this share of its samples is.
        StarvedShare: float
    }

module Policy =
    /// Twice the cores: a box running two runnable threads per core gives each about
    /// half the CPU it was budgeted for. The nights this exists for ran at 2.5–8 per core.
    [<Literal>]
    let DefaultLoadPerCore = 2.0

    /// Most of the wall, not a spike.
    [<Literal>]
    let DefaultStarvedShare = 0.5

    /// The declaration with the default thresholds.
    let rerunAlone (classes: string list) : Policy =
        { Classes = classes
          LoadPerCore = DefaultLoadPerCore
          StarvedShare = DefaultStarvedShare }

    /// Is `testClass` (a CTRF row's `extra.type`, or a filter's class) covered? A nested
    /// type (`Outer+Inner`) is covered by its declared outer class.
    let covers (policy: Policy) (testClass: string) : bool =
        List.isEmpty policy.Classes
        || policy.Classes
           |> List.exists (fun declared -> testClass = declared || testClass.StartsWith(declared + "+"))

    /// Parse `tests.projects[].starvation`. Refuses anything it cannot honour, so a typo
    /// is a config error rather than a declaration that silently never applies.
    let parse (key: string) (element: JsonElement) : Result<Policy, string> =
        let number (name: string) (fallback: float) (valid: float -> bool) (expected: string) =
            match element.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number && valid (v.GetDouble()) -> Ok(v.GetDouble())
            | true, v -> Error $"%s{key}.%s{name}: %s{v.GetRawText()} — expected %s{expected}"
            | false, _ -> Ok fallback

        if element.ValueKind <> JsonValueKind.Object then
            Error $"%s{key}: expected an object such as {{ \"onTimeout\": \"rerun-alone\" }}"
        else
            let unknown =
                element.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.filter (fun name -> not (List.contains name [ "onTimeout"; "classes"; "loadPerCore"; "share" ]))
                |> Seq.toList

            let onTimeout =
                match element.TryGetProperty "onTimeout" with
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "rerun-alone" -> Ok()
                | true, v -> Error $"%s{key}.onTimeout: %s{v.GetRawText()} — the only behaviour is \"rerun-alone\""
                | false, _ -> Error $"%s{key}: \"onTimeout\" is required (\"rerun-alone\")"

            let classes =
                match element.TryGetProperty "classes" with
                | false, _ -> Ok []
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    let items = v.EnumerateArray() |> Seq.toList

                    if
                        items
                        |> List.forall (fun i ->
                            i.ValueKind = JsonValueKind.String
                            && not (String.IsNullOrWhiteSpace(i.GetString())))
                    then
                        Ok(items |> List.map _.GetString() |> List.distinct)
                    else
                        Error $"%s{key}.classes: expected an array of non-blank class names"
                | true, _ -> Error $"%s{key}.classes: expected an array of class names"

            match
                unknown,
                onTimeout,
                classes,
                number "loadPerCore" DefaultLoadPerCore (fun v -> v >= 0.0) "a number ≥ 0",
                number "share" DefaultStarvedShare (fun v -> v > 0.0 && v <= 1.0) "a number in (0, 1]"
            with
            | (_ :: _ as names), _, _, _, _ ->
                let listed = names |> String.concat ", "
                Error $"%s{key}: unknown key(s) %s{listed} — expected onTimeout, classes, loadPerCore, share"
            | [], Ok(), Ok classes, Ok loadPerCore, Ok share ->
                Ok
                    { Classes = classes
                      LoadPerCore = loadPerCore
                      StarvedShare = share }
            | [], Error e, _, _, _
            | [], _, Error e, _, _
            | [], _, _, Error e, _
            | [], _, _, _, Error e -> Error e

/// One reading of the 1-minute load average.
type Sample = { At: DateTime; Load: float }

/// The samples taken while one attempt ran, and the cores they are measured against.
type Reading = { Cores: int; Samples: Sample list }

module Reading =
    let private perCore (reading: Reading) (sample: Sample) =
        sample.Load / float (max 1 reading.Cores)

    /// How many of the samples were starved under `policy`.
    let starvedCount (policy: Policy) (reading: Reading) : int =
        reading.Samples
        |> List.filter (fun s -> perCore reading s >= policy.LoadPerCore)
        |> List.length

    /// Did the attempt run starved for most of its wall? No samples is never starvation:
    /// on a platform with no load average nothing qualifies.
    let isStarved (policy: Policy) (reading: Reading) : bool =
        match reading.Samples with
        | [] -> false
        | samples ->
            float (starvedCount policy reading)
            >= policy.StarvedShare * float samples.Length

    /// The load as one sentence: range, cores, and how many samples were over the threshold.
    let describe (policy: Policy) (reading: Reading) : string =
        match reading.Samples with
        | [] -> $"no load samples (no load average on this platform) on %d{reading.Cores} cores"
        | samples ->
            let loads = samples |> List.map _.Load

            $"load %.1f{List.min loads}–%.1f{List.max loads} on %d{reading.Cores} cores; \
              %d{starvedCount policy reading} of %d{samples.Length} samples at ≥ %.1f{policy.LoadPerCore}/core"

/// Samples the load average on a timer while one attempt runs.
type Sampler =
    private
        { Read: unit -> float option
          Taken: Collections.Concurrent.ConcurrentQueue<Sample>
          Timer: Threading.Timer }

module Sampler =
    /// The default interval: a 600 s suite yields ~120 samples, a 10 s one yields 4.
    let DefaultInterval = TimeSpan.FromSeconds 5.0

    let private take (read: unit -> float option) (queue: Collections.Concurrent.ConcurrentQueue<Sample>) =
        match read () with
        | Some load -> queue.Enqueue { At = DateTime.UtcNow; Load = load }
        | None -> ()

    /// Start sampling: once now, then every `interval`. `read` is the load source
    /// (`OperationWatchdog.loadAverage` in the daemon).
    let start (interval: TimeSpan) (read: unit -> float option) : Sampler =
        let queue = Collections.Concurrent.ConcurrentQueue<Sample>()
        take read queue

        let timer =
            new Threading.Timer((fun _ -> take read queue), null, interval, interval)

        { Read = read
          Taken = queue
          Timer = timer }

    /// Stop sampling, take one closing sample, and return every sample in order.
    let stop (sampler: Sampler) : Sample list =
        sampler.Timer.Dispose()
        take sampler.Read sampler.Taken
        sampler.Taken.ToArray() |> Array.sortBy _.At |> Array.toList

/// What timed out in the first attempt.
type TimedOut =
    /// The project overran its own `timeoutSec` and its host was killed.
    | ProjectTimedOut of after: TimeSpan
    /// The host finished; every failed row of its report was a per-test timeout.
    | TestTimeouts of tests: string list

module TimedOut =
    let describe (timedOut: TimedOut) : string =
        match timedOut with
        | ProjectTimedOut after -> $"the project timed out after %d{int after.TotalSeconds}s"
        | TestTimeouts tests ->
            let names = tests |> String.concat ", "
            $"%d{tests.Length} test(s) timed out: %s{names}"

/// What a red result becomes.
type Decision =
    /// Re-run the project alone once, after the rest of the run.
    | RerunAlone of TimedOut * Reading
    /// Report it as it is. `Some` says why a project that DECLARED a policy does not
    /// qualify (so the log can say so); `None` when no question arose.
    | Report of why: string option

let isRerun (decision: Decision) : bool =
    match decision with
    | RerunAlone _ -> true
    | Report _ -> false

/// A string field of a CTRF node; empty when the node or the field is absent or not a string.
let private field (node: JsonNode) (name: string) : string =
    match node with
    | null -> ""
    | n ->
        match n[name] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> s
            | _ -> ""
        | _ -> ""

let private isTimeoutRow (row: JsonNode) =
    field (row["extra"]) "exception" = "Xunit.Sdk.TestTimeoutException"
    || (field row "message").StartsWith("Test execution timed out after")

/// A row's class: `extra.type` when the runner wrote one, otherwise its name up to the
/// last dot.
let private classOf (row: JsonNode) =
    match field (row["extra"]) "type" with
    | "" ->
        let name = field row "name"
        let dot = name.LastIndexOf '.'
        if dot > 0 then name.Substring(0, dot) else name
    | t -> t

/// The timed-out tests of a CTRF report, when EVERY failed or unrecognised row is a
/// timeout in a class `policy` covers; otherwise why not. A report with no failed row
/// names no timeout either.
let timeoutsOfReport (policy: Policy) (json: string) : Result<string list, string> =
    let rows =
        try
            match JsonNode.Parse json with
            | null -> None
            | root ->
                let tests =
                    match root["results"] with
                    | null -> root["tests"]
                    | results -> results["tests"]

                match tests with
                | :? JsonArray as arr -> Some(arr |> Seq.filter (isNull >> not) |> Seq.toList)
                | _ -> None
        with :? JsonException ->
            None

    match rows with
    | None -> Error "its report is unreadable, so it cannot show every failure was a timeout"
    | Some rows ->
        let red =
            rows
            |> List.filter (fun r -> not (List.contains (field r "status") [ "passed"; "skipped"; "pending" ]))

        let notTimeouts = red |> List.filter (isTimeoutRow >> not)

        let undeclared =
            red
            |> List.filter (fun r -> isTimeoutRow r && not (Policy.covers policy (classOf r)))

        match red, notTimeouts, undeclared with
        | [], _, _ -> Error "its report names no failed test"
        | _, (_ :: _), _ ->
            let names = notTimeouts |> List.map (fun r -> field r "name") |> String.concat ", "
            Error $"not every failure was a timeout (%s{names}); an assertion failure is never re-run"
        | _, [], (_ :: _) ->
            let names = undeclared |> List.map classOf |> List.distinct |> String.concat ", "
            Error $"timed-out class(es) %s{names} are not covered by the starvation declaration"
        | _, [], [] -> Ok(red |> List.map (fun r -> field r "name"))

/// Decide what a project's result becomes. `selection` is the classes the run filtered
/// the project to (`None` when it ran in full); `reportJson` is its CTRF report.
let decide
    (policy: Policy option)
    (selection: string list option)
    (result: TestResult)
    (reportJson: string option)
    (reading: Reading)
    : Decision =
    let starvedOr (p: Policy) (timedOut: TimedOut) =
        if Reading.isStarved p reading then
            RerunAlone(timedOut, reading)
        else
            Report(
                Some
                    $"%s{TimedOut.describe timedOut}, but the box was not starved (%s{Reading.describe p reading}) — reported as it is"
            )

    match policy, result with
    | None, _ -> Report None
    | Some p, TestsTimedOut(_, after, _, _) ->
        let pinned =
            match selection with
            | None -> List.isEmpty p.Classes
            | Some classes -> not (List.isEmpty classes) && classes |> List.forall (Policy.covers p)

        if pinned then
            starvedOr p (ProjectTimedOut after)
        else
            Report(
                Some
                    "the project timed out, but its run included classes the starvation declaration does not cover — reported as it is"
            )
    | Some p, TestsFailed _ ->
        match reportJson |> Option.map (timeoutsOfReport p) with
        | Some(Ok tests) -> starvedOr p (TestTimeouts tests)
        | Some(Error why) -> Report(Some $"not re-run: %s{why}")
        | None -> Report(Some "not re-run: no report, so nothing shows every failure was a timeout")
    | Some _, TestsPassed _
    | Some _, TestsErrored _
    | Some _, TestsDeferred _
    | Some _, TestsNoMatch _ -> Report None

/// Where the first attempt's artifacts are kept, inside the run directory. A
/// subdirectory, so no reader of the run's `*.ctrf.json` reports counts them.
[<Literal>]
let AttemptDirName = "starved-attempt"

/// The record's suffix, beside `<Project>.ctrf.json`.
[<Literal>]
let Suffix = ".starved.json"

let pathIn (runDir: string) (project: string) : string = Path.Combine(runDir, project + Suffix)

/// Move `project`'s first-attempt artifacts (`<project><suffix>` for each suffix) into
/// the attempt directory, so the re-run writes the run's report and log afresh. Returns
/// the paths they now have. A file that is absent is skipped; a move that fails is
/// logged and left where it is.
let setAside (runDir: string) (project: string) (suffixes: string list) : string list =
    let into = Path.Combine(runDir, AttemptDirName)

    suffixes
    |> List.choose (fun suffix ->
        let source = Path.Combine(runDir, project + suffix)

        if File.Exists source then
            try
                Directory.CreateDirectory into |> ignore
                let target = Path.Combine(into, project + suffix)
                File.Move(source, target, true)
                Some target
            with
            | :? IOException
            | :? UnauthorizedAccessException as ex ->
                Logging.warn
                    "test-prune"
                    $"%s{project}: could not set the starved attempt's %s{source} aside: %s{ex.Message}"

                None
        else
            None)

/// What the run records about a project it re-ran.
type Record =
    {
        Project: string
        TimedOut: TimedOut
        /// The load while the first attempt ran.
        FirstAttempt: Reading
        /// The load while the re-run ran.
        Rerun: Reading
        /// How the re-run ended (`passed`, `failed`, `timed out`, …).
        RerunOutcome: string
        PassedAlone: bool
        /// Where the first attempt's report and log were set aside.
        AttemptDir: string
    }

/// The one line a reader sees: "starved, then passed alone" or "… still red".
let summary (record: Record) : string =
    let first = TimedOut.describe record.TimedOut

    if record.PassedAlone then
        $"%s{record.Project}: starved, then passed alone — %s{first} under CPU starvation; re-run alone it passed"
    else
        $"%s{record.Project}: starved, re-run alone, still red — %s{first} under CPU starvation; re-run alone it %s{record.RerunOutcome}"

/// Write the record beside the project's report. A filesystem refusal is logged: losing
/// it loses the annotation, never the result.
let write (policy: Policy) (runDir: string) (record: Record) : unit =
    let samples (reading: Reading) =
        reading.Samples
        |> List.map (fun s ->
            {| at = s.At.ToString("O")
               load = s.Load |})

    let json =
        JsonSerializer.Serialize(
            {| project = record.Project
               summary = summary record
               passedAlone = record.PassedAlone
               firstAttempt = TimedOut.describe record.TimedOut
               load = Reading.describe policy record.FirstAttempt
               rerunLoad = Reading.describe policy record.Rerun
               rerunOutcome = record.RerunOutcome
               attemptDir = record.AttemptDir
               cores = record.FirstAttempt.Cores
               loadPerCore = policy.LoadPerCore
               share = policy.StarvedShare
               firstAttemptSamples = samples record.FirstAttempt
               rerunSamples = samples record.Rerun |},
            JsonSerializerOptions(WriteIndented = true)
        )

    let path = pathIn runDir record.Project

    try
        FsHwPaths.atomicWriteAllText path json
    with ex when RunLog.isExpectedFileFailure ex ->
        Logging.warn "test-prune" $"%s{record.Project}: could not write the starvation record %s{path}: %s{ex.Message}"

/// The fields of a record a verdict carries.
type Stored =
    { PassedAlone: bool
      Summary: string
      FirstAttempt: string
      Load: string }

/// Read a record back; `None` when there is none or it cannot be read.
let tryRead (path: string) : Stored option =
    try
        if not (File.Exists path) then
            None
        else
            use doc = JsonDocument.Parse(File.ReadAllText path)
            let root = doc.RootElement

            let text (name: string) =
                match root.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> ""

            match root.TryGetProperty "passedAlone" with
            | true, v when v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False ->
                Some
                    { PassedAlone = v.GetBoolean()
                      Summary = text "summary"
                      FirstAttempt = text "firstAttempt"
                      Load = text "load" }
            | _ -> None
    with
    | :? IOException
    | :? UnauthorizedAccessException
    | :? JsonException -> None
