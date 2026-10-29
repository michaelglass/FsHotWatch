/// One test run's dealings with the box-wide test-evidence store: which projects it may
/// leave unlaunched because another checkout earned their green under identical inputs,
/// whether that still holds when the run completes, and which of its own greens it may
/// publish for others.
///
/// Every rule here can only WIDEN what runs. A project is reused only when its key,
/// computed before the run touched anything, matches an entry a real full-project green
/// wrote; it stays reused only when the key still matches at completion. A green is
/// published only when the project ran in full, every test passed on its first attempt,
/// every artifact the gate grades exists, and the key held still for the whole run.
module FsHotWatch.TestPrune.TestEvidenceRun

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch
open FsHotWatch.Events

/// What a run is told about the store, once per plugin.
[<NoComparison; NoEquality>]
type Wiring =
    {
        Store: TestEvidenceStore.Store
        /// Each test project's `evidence` block; a project absent here takes the default.
        Configs: Map<string, TestEvidenceKey.Config>
        Toolchain: Lazy<string>
        /// The environment the daemon started with, captured once when it wired evidence.
        Loaded: TestEvidenceKey.LoadedEnvironment
        /// The fshw build producing entries, recorded as provenance.
        ProducerHash: string
        /// The daemon's `exclude`: the patterns its verdict tree hash leaves out, and so
        /// the patterns every key leaves out. Not a second list: a file the tree hash does
        /// not address cannot decide a key either, and a path-bearing generated file there
        /// would give every checkout its own key.
        ExcludePatterns: string list
        /// The box-wide outcome store (ADR-059) every completed project run is recorded in,
        /// under the K_P of the inputs it ran over. `None` records nothing.
        Outcomes: OutcomeStore.Store option
    }

/// Where a reused project's green came from.
type Provenance =
    { Key: string
      Worktree: string
      RunId: string
      TreeHash: string
      ProducerHash: string
      EarnedAt: DateTimeOffset }

module Provenance =
    let ofEntry (entry: TestEvidenceStore.Entry) : Provenance =
        { Key = entry.Key
          Worktree = entry.Producer.Worktree
          RunId = entry.Producer.RunId
          TreeHash = entry.Producer.TreeHash
          ProducerHash = entry.Producer.ProducerHash
          EarnedAt = entry.Producer.EarnedAt }

    let describe (p: Provenance) =
        $"""reused: earned by run %s{p.RunId} in %s{p.Worktree} at %s{p.EarnedAt.ToString("u")} under key %s{p.Key.Substring(0, 12)}"""

/// A test project the run can key: its configured name, its `.fsproj`, the projects it
/// references transitively, and the text its configuration is keyed by.
type Candidate =
    { Project: string
      ProjectFile: string
      Closure: string list
      TestConfig: string }

/// The transitive `ProjectReference` closure of `project` (excluding itself), sorted.
let closureOf (references: string -> string list) (project: string) : string list =
    let rec walk (seen: Set<string>) (pending: string list) =
        match pending with
        | [] -> seen
        | next :: rest when seen.Contains next -> walk seen rest
        | next :: rest -> walk (seen.Add next) (references next @ rest)

    walk Set.empty (references project) |> Set.remove project |> Set.toList

/// Every candidate's key from ONE walk of the tree, with the bytes hashed. A candidate
/// that cannot be keyed is an `Error` naming why, and so is every candidate when the walk
/// itself fails: an unkeyable project is simply launched, never a reason to stop the run.
let keysOf
    (wiring: Wiring)
    (repoRoot: string)
    (optionsHash: string -> string option)
    (candidates: Candidate list)
    : Map<string, Result<TestEvidenceKey.Key, string>> * int64 =
    if List.isEmpty candidates then
        Map.empty, 0L
    else
        try
            let snap = TestEvidenceKey.snapshot repoRoot wiring.ExcludePatterns
            let mutable bytes = snap.BytesHashed

            let keys =
                candidates
                |> List.map (fun c ->
                    let request: TestEvidenceKey.Request =
                        { Project = c.ProjectFile
                          Closure = c.Closure
                          Config =
                            wiring.Configs
                            |> Map.tryFind c.Project
                            |> Option.defaultValue TestEvidenceKey.defaultConfig
                          TestConfig = c.TestConfig
                          Toolchain = wiring.Toolchain.Value
                          OptionsHash = optionsHash
                          Loaded = wiring.Loaded }

                    c.Project,
                    (TestEvidenceKey.inputsFor snap request
                     |> Result.map (fun (inputs, extra) ->
                         bytes <- bytes + extra
                         TestEvidenceKey.compute inputs)))
                |> Map.ofList

            keys, bytes
        with ex ->
            candidates
            |> List.map (fun c -> c.Project, Error $"could not key the tree: %s{ex.Message}")
            |> Map.ofList,
            0L

/// The launch-time half of a run: every key it computed, and the projects it reuses.
[<NoComparison; NoEquality>]
type Plan =
    {
        Wiring: Wiring
        /// The relativised options identity of a project, as the model published it at
        /// launch. The completion re-key uses the same lookup.
        OptionsHash: string -> string option
        Candidates: Candidate list
        /// Each candidate's key before the run touched the tree.
        LaunchKeys: Map<string, Result<TestEvidenceKey.Key, string>>
        /// The projects this run does not launch, and the entry each reuses.
        Reused: Map<string, TestEvidenceStore.Entry>
        /// Why each eligible project that is launched found nothing to reuse.
        Misses: Map<string, string>
        /// How many lookups this plan made. `confirm` and the kill switch make none.
        Lookups: int
        BytesHashed: int64
    }

/// Key every candidate, then look up the ones `eligible` admits: a project about to
/// launch, with no outstanding failure, in a run that may consult evidence at all.
let plan
    (wiring: Wiring)
    (repoRoot: string)
    (optionsHash: string -> string option)
    (candidates: Candidate list)
    (eligible: string -> bool)
    : Plan =
    let keys, bytes = keysOf wiring repoRoot optionsHash candidates
    let root = Path.GetFullPath repoRoot

    // Each eligible candidate: `Error` when it has no key, else what the store answered.
    // `keysOf` answers for every candidate it is given.
    let lookups =
        candidates
        |> List.filter (fun c -> eligible c.Project)
        |> List.map (fun c ->
            let relative = Path.GetRelativePath(root, c.ProjectFile).Replace('\\', '/')
            c.Project, keys[c.Project] |> Result.map (fun key -> wiring.Store.Lookup(relative, key)))

    let hit (lookup: Result<TestEvidenceStore.Lookup, string>) =
        match lookup with
        | Ok(TestEvidenceStore.Lookup.Hit entry) -> Some entry
        | Ok(TestEvidenceStore.Lookup.Miss _)
        | Error _ -> None

    let missReason (lookup: Result<TestEvidenceStore.Lookup, string>) =
        match lookup with
        | Ok(TestEvidenceStore.Lookup.Hit _) -> None
        | Ok(TestEvidenceStore.Lookup.Miss miss) -> Some(TestEvidenceStore.describeMiss miss)
        | Error reason -> Some $"no key: %s{reason}"

    { Wiring = wiring
      OptionsHash = optionsHash
      Candidates = candidates
      LaunchKeys = keys
      Reused =
        lookups
        |> List.choose (fun (project, lookup) -> hit lookup |> Option.map (fun entry -> project, entry))
        |> Map.ofList
      Misses =
        lookups
        |> List.choose (fun (project, lookup) -> missReason lookup |> Option.map (fun why -> project, why))
        |> Map.ofList
      Lookups = lookups |> List.filter (snd >> Result.isOk) |> List.length
      BytesHashed = bytes }

/// The completion-time re-check: a reuse holds only while its key is unchanged. Returns
/// the reuses that held, and each one that did not with the inputs that moved (or why it
/// could no longer be keyed at all).
let recheck
    (reused: Map<string, TestEvidenceStore.Entry>)
    (completionKeys: Map<string, Result<TestEvidenceKey.Key, string>>)
    : Map<string, TestEvidenceStore.Entry> * (string * string list) list =
    let held, moved =
        reused
        |> Map.partition (fun project entry ->
            match Map.tryFind project completionKeys with
            | Some(Ok key) -> key.Value = entry.Key
            | Some(Error _)
            | None -> false)

    held,
    moved
    |> Map.toList
    |> List.map (fun (project, entry) ->
        project,
        match Map.tryFind project completionKeys with
        | Some(Ok key) -> TestEvidenceKey.moved entry.KeyInputs key.Labels
        | Some(Error why) -> [ why ]
        | None -> [ "not keyed" ])

/// Every candidate keyed again from a fresh walk, for the completion re-check and the
/// producer.
let completionKeys (plan: Plan) (repoRoot: string) =
    keysOf plan.Wiring repoRoot plan.OptionsHash plan.Candidates |> fst

/// A file's text, or `None` when it is absent or cannot be read: a report that cannot
/// be read is not evidence.
let readIfPresent (path: string) : string option =
    try
        if File.Exists path then
            Some(File.ReadAllText path)
        else
            None
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

/// Delete a file that may not exist. A file that cannot be deleted is left; a filed
/// report the run withdraws is then still on disk, which is why every withdrawal also
/// marks the project deferred: the deferral is what the verdict reads.
let deleteQuietly (path: string) : unit =
    try
        File.Delete path
    with
    | :? IOException
    | :? UnauthorizedAccessException -> ()

/// One project's real run, as the producer judges it.
type ProducedRun =
    {
        Project: string
        Result: TestResult
        /// The CTRF report the run wrote, if any.
        Report: string option
        /// Whether the gate grades a coverage report for this project.
        CoverageRequired: bool
        /// The raw coverage the run wrote, if any.
        Coverage: (string * string) option
    }

/// Whether any row of a CTRF report passed only after a retry, or is marked flaky.
let internal anyRetried (report: string) =
    try
        match JsonNode.Parse report with
        | :? JsonObject as top ->
            let body =
                match top["results"] with
                | :? JsonObject as nested -> nested
                | _ -> top

            match body["tests"] with
            | :? JsonArray as rows ->
                rows
                |> Seq.exists (fun row ->
                    match row with
                    | :? JsonObject as o ->
                        let retries =
                            match o["retries"] with
                            | :? JsonValue as v ->
                                match v.TryGetValue<int>() with
                                | true, n -> n > 0
                                | _ -> true
                            | null -> false
                            | _ -> true

                        let flaky =
                            match o["flaky"] with
                            | :? JsonValue as v ->
                                match v.TryGetValue<bool>() with
                                | true, b -> b
                                | _ -> true
                            | null -> false
                            | _ -> true

                        retries || flaky
                    | _ -> true)
            | _ -> true
        | _ -> true
    with _ ->
        true

/// Why this run's green may NOT be published, or `Ok` when it may. Every condition is a
/// reason to stay silent; publishing is the one outcome that has to be earned.
let qualifies
    (launchKey: Result<TestEvidenceKey.Key, string> option)
    (completionKey: Result<TestEvidenceKey.Key, string> option)
    (run: ProducedRun)
    : Result<TestEvidenceKey.Key, string> =
    match run.Result with
    | TestsPassed(_, true, _) -> Error "the run was filtered"
    | TestsPassed(_, false, _) ->
        match launchKey, completionKey with
        | Some(Ok atLaunch), Some(Ok atCompletion) when atLaunch.Value = atCompletion.Value ->
            match run.Report with
            | None -> Error "no CTRF report"
            | Some report ->
                match Ctrf.tryVerdictReport report with
                | Error reason -> Error $"the CTRF report is not verdict evidence: %s{reason}"
                | Ok verdict ->
                    let summary = Ctrf.VerdictReport.summary verdict

                    if summary.Failed > 0 || summary.Other > 0 then
                        Error "the CTRF report counts failures"
                    elif summary.Total = 0 then
                        Error "the CTRF report counts no tests"
                    elif anyRetried report then
                        Error "a test passed only on retry"
                    elif run.CoverageRequired && Option.isNone run.Coverage then
                        Error "its coverage report is missing"
                    else
                        Ok atLaunch
        | Some(Ok _), Some(Ok _) -> Error "the tree moved during the run"
        | Some(Error reason), _
        | _, Some(Error reason) -> Error $"no key: %s{reason}"
        | _ -> Error "no key"
    | TestsFailed _ -> Error "the run failed"
    | TestsTimedOut _ -> Error "the run timed out"
    | TestsDeferred _
    | TestsErrored _
    | TestsNoMatch _ -> Error "the run verified nothing"

/// The outcome-store action key of a test project's run over K_P `key`, in `scope`
/// (`full` or `impact`). K_P's own labels travel in as `kp:<label>`, so a miss names the K_P
/// input that moved (ADR-059 §1).
let testActionKey (key: TestEvidenceKey.Key) (scope: string) : ActionKey.ActionKey =
    ActionKey.create
        "test"
        ((key.Labels |> List.map (fun (label, digest) -> "kp:" + label, digest))
         @ [ "kp", key.Value; "run-scope", scope ])

/// The outcome record a completed project run files (ADR-059 §11 step 7): a pass or a fail,
/// under the K_P its inputs had at LAUNCH, only when that is still the key at completion. A
/// run whose inputs moved under it describes no tree and files nothing (641, 699); so does a
/// project with no key, and a run that verified nothing.
let outcomeRecordOf
    (candidate: Candidate)
    (launchKey: Result<TestEvidenceKey.Key, string> option)
    (completionKey: Result<TestEvidenceKey.Key, string> option)
    (run: ProducedRun)
    (producer: TestEvidenceStore.Producer)
    : OutcomeStore.Record option =
    let outcome =
        match run.Result with
        | TestsPassed(_, filtered, _) -> Some(OutcomeStore.Outcome.Pass, filtered)
        | TestsFailed(_, filtered, _) -> Some(OutcomeStore.Outcome.Fail, filtered)
        | TestsTimedOut(_, _, filtered, _) -> Some(OutcomeStore.Outcome.Fail, filtered)
        | TestsDeferred _
        | TestsErrored _
        | TestsNoMatch _ -> None

    match launchKey, completionKey, outcome with
    | Some(Ok atLaunch), Some(Ok atCompletion), Some(outcome, filtered) when atLaunch.Value = atCompletion.Value ->
        Some
            { Key = testActionKey atLaunch (if filtered then "impact" else "full")
              Subject = candidate.Project
              Outcome = outcome
              Counts =
                run.Report
                |> Option.bind Ctrf.trySummary
                |> Option.map (fun s ->
                    ({ Total = s.Total
                       Passed = s.Passed
                       Failed = s.Failed
                       Skipped = s.Skipped }
                    : OutcomeStore.Counts))
              DurationMs = None
              Producer =
                ({ Binary = producer.ProducerHash
                   Worktree = producer.Worktree
                   RunId = Some producer.RunId }
                : OutcomeStore.Producer)
              RecordedAt = producer.EarnedAt }
    | _ -> None

/// The entry a qualifying run publishes.
let entryOf
    (repoRoot: string)
    (project: Candidate)
    (key: TestEvidenceKey.Key)
    (producer: TestEvidenceStore.Producer)
    (run: ProducedRun)
    : TestEvidenceStore.Entry =
    let root = Path.GetFullPath repoRoot

    let report =
        run.Report |> Option.defaultValue "" |> TestEvidenceStore.relativise root

    let summary =
        run.Report
        |> Option.bind (Ctrf.tryVerdictReport >> Result.toOption)
        |> Option.map Ctrf.VerdictReport.summary

    { Key = key.Value
      Project = Path.GetRelativePath(root, project.ProjectFile).Replace('\\', '/')
      KeyInputs = key.Labels
      Producer = producer
      Summary =
        { Tests = summary |> Option.map _.Total |> Option.defaultValue 0
          Passed = summary |> Option.map _.Passed |> Option.defaultValue 0
          Failed = 0
          Skipped = summary |> Option.map _.Skipped |> Option.defaultValue 0
          DurationMs =
            match run.Result with
            | TestsPassed(_, _, elapsed) -> int64 elapsed.TotalMilliseconds
            | _ -> 0L }
      Report = report
      Coverage =
        run.Coverage
        |> Option.map (fun (path, content) ->
            { Path = Path.GetRelativePath(root, path).Replace('\\', '/')
              Content = TestEvidenceStore.relativise root content }) }

/// The name of the field a reused report carries its provenance under.
[<Literal>]
let ReusedFromField = "fshwReusedFrom"

/// The stored report rebound into this checkout, with its provenance stamped on, ready
/// to be filed in the reusing run's directory under the project's name.
let reusedReport (repoRoot: string) (entry: TestEvidenceStore.Entry) : string =
    let rebound = TestEvidenceStore.rebind repoRoot entry.Report

    match JsonNode.Parse rebound with
    | :? JsonObject as top ->
        let p = JsonObject()
        p["key"] <- entry.Key
        p["worktree"] <- entry.Producer.Worktree
        p["runId"] <- entry.Producer.RunId
        p["treeHash"] <- entry.Producer.TreeHash
        p["producerHash"] <- entry.Producer.ProducerHash
        p["earnedAt"] <- entry.Producer.EarnedAt.ToString("O")
        top[ReusedFromField] <- p
        top.ToJsonString()
    | _ -> rebound

/// What a run's completion decided about the projects it reused.
type Reconciled =
    {
        /// Every candidate keyed again from a fresh walk; the producer reads the same keys.
        Keys: Map<string, Result<TestEvidenceKey.Key, string>>
        /// The reuses whose key still held: they stand as the run's greens.
        Held: Map<string, TestEvidenceStore.Entry>
        /// Each reuse that no longer describes the tree, with the deferral it reports.
        Withdrawn: (string * string) list
    }

/// The completion re-check: key every candidate again, keep the reuses whose key held and
/// withdraw the rest. Runs BEFORE anything the run reports is folded, so a withdrawn
/// reuse is never seen as a green by anyone.
let reconcile (plan: Plan) (repoRoot: string) (reused: Map<string, TestEvidenceStore.Entry>) : Reconciled =
    let keys = completionKeys plan repoRoot
    let held, moved = recheck reused keys

    { Keys = keys
      Held = held
      Withdrawn =
        moved
        |> List.map (fun (project, labels) ->
            project,
            $"""reused evidence no longer matches this tree (key moved during the run: %s{String.concat ", " labels}) — not counted; it runs next""") }

/// Publish every green `runOf` reports that `qualifies`, against the completion keys of
/// `reconciled`. Runs AFTER the run's own results are final — coverage that failed to
/// ingest has already made its project non-green — and never for a reused project.
/// Returns how many were written.
let publish
    (plan: Plan)
    (repoRoot: string)
    (reconciled: Reconciled)
    (reused: Map<string, TestEvidenceStore.Entry>)
    (runOf: Candidate -> ProducedRun option)
    (producer: TestEvidenceStore.Producer)
    : int =
    let launched =
        plan.Candidates
        |> List.filter (fun c -> not (reused.ContainsKey c.Project))
        |> List.choose (fun c -> runOf c |> Option.map (fun run -> c, run))

    match plan.Wiring.Outcomes with
    | Some outcomes ->
        let records =
            launched
            |> List.choose (fun (candidate, run) ->
                outcomeRecordOf
                    candidate
                    (Map.tryFind candidate.Project plan.LaunchKeys)
                    (Map.tryFind candidate.Project reconciled.Keys)
                    run
                    producer)

        for record in records do
            outcomes.Record record |> ignore

        // The box-wide store is bounded by TTL and bytes (ADR-059 §2). GC can only turn a hit
        // into a miss, and it spares what this run just filed.
        outcomes.Gc(producer.EarnedAt, records |> List.map _.Key.Value |> Set.ofList)
        |> ignore
    | None -> ()

    launched
    |> List.filter (fun (candidate, run) ->
        match
            qualifies
                (Map.tryFind candidate.Project plan.LaunchKeys)
                (Map.tryFind candidate.Project reconciled.Keys)
                run
        with
        | Ok key -> plan.Wiring.Store.Write(entryOf repoRoot candidate key producer run)
        | Error why ->
            Logging.debug "test-prune" $"%s{candidate.Project}: no test evidence written: %s{why}"
            false)
    |> List.length

/// The tally a run logs: what it reused, what it launched for want of evidence, and what
/// it published.
let tally (plan: Plan) (reconciled: Reconciled) (written: int) =
    $"test evidence: reused %d{Map.count reconciled.Held}, moved %d{reconciled.Withdrawn.Length}, missed %d{Map.count plan.Misses}, written %d{written} (%d{plan.Lookups} lookup(s), %d{plan.BytesHashed} byte(s) hashed at launch)"
