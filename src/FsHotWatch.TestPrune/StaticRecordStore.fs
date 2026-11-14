/// Filing `StaticRecord`s (AUTOMATION-1686, ADR-070): the box-wide store, the JSON line a
/// record is written as, the info line it logs, and the capture at launch that files a
/// completed run's record. Nothing here throws into the run.
module FsHotWatch.TestPrune.StaticRecordStore

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.Events
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.StaticRecord

/// Where a repository's records live: box-wide, under the shared cache, because the
/// workspaces that file them are short-lived and a bar of 150 trees outlives every one.
let defaultStorePath (repoRoot: string) : string =
    Path.Combine(
        FsHotWatch.FsHwPaths.sharedCacheHome (),
        "cache",
        "selection-record",
        FsHotWatch.RepoIdentity.namespaceOf repoRoot,
        "records.jsonl"
    )

/// Rendering a record as one JSON line, version 1.
module Json =
    let private options =
        JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private num (value: int) : JsonNode = JsonValue.Create value

    let private obj (properties: (string * JsonNode) seq) : JsonNode =
        JsonObject(properties |> Seq.map Collections.Generic.KeyValuePair.Create)

    let private arr (nodes: JsonNode seq) : JsonNode = JsonArray(Seq.toArray nodes)

    let private sorted (values: string seq) =
        values |> Seq.sort |> Seq.map str |> arr

    let private selection (byProject: Map<string, ProjectSelection>) =
        byProject
        |> Map.toSeq
        |> Seq.map (fun (project, s) ->
            project,
            match s with
            | ProjectInFull -> obj [ "scope", str "whole" ]
            | ProjectClasses classes -> obj [ "scope", str "classes"; "classes", sorted classes ]
            | ProjectExcept classes -> obj [ "scope", str "all-but"; "classes", sorted classes ])
        |> obj

    let private test (t: TestId) =
        obj [ "project", str t.Project; "class", str t.Class; "name", str t.Name ]

    let private outcomeName =
        function
        | Passed -> "passed"
        | Failed -> "failed"
        | Skipped -> "skipped"

    let private outcomes (byProject: Map<string, ProjectOutcome>) =
        byProject
        |> Map.toSeq
        |> Seq.map (fun (project, outcome) ->
            project,
            match outcome with
            | Ran rows ->
                obj
                    [ "kind", str "ran"
                      "tests",
                      rows
                      |> Map.toSeq
                      |> Seq.map (fun (t, o) ->
                          obj [ "class", str t.Class; "name", str t.Name; "outcome", str (outcomeName o) ])
                      |> arr ]
            | RedUnnamed -> obj [ "kind", str "red-unnamed" ]
            | TimedOut -> obj [ "kind", str "timed-out" ]
            | ProvedNothing reason -> obj [ "kind", str "proved-nothing"; "reason", str reason ])
        |> obj

    let private failure =
        function
        | TestFailed t -> obj [ "kind", str "test"; "test", test t ]
        | ProjectTimedOut project -> obj [ "kind", str "timed-out"; "project", str project ]
        | ProjectRedUnnamed project -> obj [ "kind", str "red-unnamed"; "project", str project ]

    let private failures (set: Set<Failure>) = set |> Seq.map failure |> arr

    /// The judgement's name, as the log line and the store spell it.
    let judgementName =
        function
        | StaticRanEverything -> "static-ran-everything"
        | OutcomesUnknown _ -> "outcomes-unknown"
        | Green -> "green"
        | CaughtByStatic _ -> "caught-by-static"
        | MissedByStatic _ -> "missed-by-static"

    let judgement (record: StaticRecord) (j: StaticJudgement) : JsonNode =
        match j with
        | StaticRanEverything
        | Green -> obj [ "kind", str (judgementName j) ]
        | OutcomesUnknown projects ->
            obj
                [ "kind", str (judgementName j)
                  "projects", sorted (NonEmptySet.toSet projects) ]
        | CaughtByStatic(inside, outside) ->
            obj
                [ "kind", str (judgementName j)
                  "insideFailed", failures (NonEmptySet.toSet inside)
                  "outsideFailed", failures outside ]
        | MissedByStatic outside ->
            obj
                [ "kind", str (judgementName j)
                  "outsideFailed", failures (NonEmptySet.toSet outside)
                  "flakyOnly", JsonValue.Create(isFlakyOnly record outside) ]

    let private treeNode =
        function
        | Tree identity -> str identity
        | Unidentified -> null

    /// The record and its judgement as one line of the store.
    let render (record: StaticRecord) (j: StaticJudgement) : string =
        (obj
            [ "v", num 1
              "tree", treeNode record.Tree
              "runId", str (record.RunId.ToString("N"))
              "static", selection record.Static
              "ran", selection record.Ran
              "outcomes", outcomes record.Outcomes
              "knownFlaky", record.KnownFlaky |> Seq.map test |> arr
              "judgement", judgement record j ])
            .ToJsonString
            options

/// The info line every filed record logs, so a missing store still leaves a trace.
[<Literal>]
let LogPrefix = "selection-record: "

/// The tests a record holds outcomes for, and how many of them are inside the static set.
let private counts (record: StaticRecord) =
    let tests = record.Outcomes |> Map.values |> Seq.collect testsOf |> Seq.toList

    let inside =
        tests
        |> List.filter (fun t -> isInside record.Static (TestFailed t))
        |> List.length

    inside, List.length tests

let logLine (record: StaticRecord) (j: StaticJudgement) : string =
    let staticTests, ranTests = counts record

    let tree =
        match record.Tree with
        | Tree identity -> identity
        | Unidentified -> "unidentified"

    $"%s{LogPrefix}{{tree: %s{tree}, staticTests: %d{staticTests}, ranTests: %d{ranTests}, judgement: %s{Json.judgementName j}}}"

/// Append `record` and its judgement to the store at `storePath`, one line, and log it.
/// Never throws: a store that cannot be written is one warning and no record.
let file (info: string -> unit) (warn: string -> unit) (storePath: string) (record: StaticRecord) : unit =
    let j = judge record
    info (logLine record j)

    try
        Directory.CreateDirectory(Path.GetDirectoryName storePath) |> ignore
        File.AppendAllText(storePath, Json.render record j + "\n")
    with ex ->
        let runId = record.RunId.ToString "N"

        warn
            $"selection-record: could not file the record for run %s{runId} at %s{storePath} (%s{ex.Message}); the run, its verdict and its evidence are unchanged"

/// Whether a plugin files records: for the projects whose `runtimeSelection` is
/// `"runtime/record-static"`, into `store`.
[<RequireQualifiedAccess>]
type Recording =
    | Off
    | For of projects: Set<string> * store: string

module Recording =
    /// Recording for `projects`, into the repository's box-wide store; off when none.
    let ofProjects (repoRoot: string) (projects: Set<string>) : Recording =
        if Set.isEmpty projects then
            Recording.Off
        else
            Recording.For(projects, defaultStorePath repoRoot)

/// What a launch selected, as the record needs it: the selection it launched, the projects
/// only runtime coverage took whole, and the classes static causes named.
type Launched =
    { Ran: Map<string, ProjectSelection>
      RuntimeOnly: Set<string>
      Outstanding: Map<string, string list>
      Always: Map<string, string list> }

/// Capture, at launch and before any test runs, what filing this run's record needs, and
/// return what files it once the run is over: the completion and the tree the run was bound
/// to. Under `Recording.Off` that does nothing. The capture is pure (the flaky history reads
/// as empty when it cannot be read), and the filing catches everything it does, so neither
/// half throws into the run.
let prepare
    (info: string -> unit)
    (warn: string -> unit)
    (repoRoot: string)
    (recording: Recording)
    (launched: Launched)
    : TestRunCompleted -> string option -> unit =
    match recording with
    | Recording.Off -> fun _ _ -> ()
    | Recording.For(projects, store) ->
        let atLaunch =
            { Static =
                staticOf launched.Ran (Set.intersect launched.RuntimeOnly projects) launched.Outstanding launched.Always
              Ran = launched.Ran
              FlakyNames = flakyNamesOf (Flakiness.loadHistory (Flakiness.historyPath repoRoot)) }

        fun completion tree ->
            match CompletedRun.ofCompletion completion with
            | Error reason ->
                let runId = completion.RunId.ToString "N"
                info $"selection-record: run %s{runId} did not complete (%s{reason}); no record"
            | Ok run ->
                // Reading the run's reports is I/O; whatever it throws costs only the record.
                try
                    let runId = CompletedRun.runId run

                    // `executeTests` reports a project it did not launch as a pass that
                    // proves nothing; only what launched has outcomes.
                    let launchedResults =
                        CompletedRun.results run
                        |> Map.filter (fun project _ -> Map.containsKey project launched.Ran)

                    let recordedTree =
                        match tree with
                        | Some identity -> Tree identity
                        | None -> Unidentified

                    outcomesOf (reportIn repoRoot runId) launchedResults
                    |> recordOf atLaunch recordedTree run
                    |> file info warn store
                with ex ->
                    warn
                        $"selection-record: building the record failed (%s{ex.Message}); no record, the run and its verdict are unchanged"
