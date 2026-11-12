/// One test run's dealings with the box-wide test-evidence store: which projects it may
/// leave unlaunched because another checkout earned their green under identical inputs,
/// whether that still holds when the run completes, and which of its own greens it may
/// publish for others.
///
/// Every rule here can only WIDEN what runs. A project is reused only when its key,
/// computed once `beforeRun` has settled the tree and before any test ran, matches an
/// entry a real full-project green wrote; it stays reused only when the key still matches
/// at completion. A green is published only when the project ran in full, every test
/// passed on its first attempt, every artifact the gate grades exists, and the key held
/// still for the whole run.
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

/// The outcome-store action key of a test project's run over K_P `key`, in `scope`
/// (`full` or `impact`). K_P's own labels travel in as `kp:<label>`, so a miss names the K_P
/// input that moved (ADR-059 §1).
let testActionKey (key: TestEvidenceKey.Key) (scope: string) : ActionKey.ActionKey =
    ActionKey.create
        "test"
        ((key.Labels |> List.map (fun (label, digest) -> "kp:" + label, digest))
         @ [ "kp", key.Value; "run-scope", scope ])

/// Why the outcome store will not let a full green earned at `earnedAt` under K_P `key`
/// stand in for a run of the project (ADR-059 §34, AUTOMATION-1615), or `None` when it lets
/// it. Every reuse asks this one question: `plan`'s ordinary reuse, and anything else that
/// answers a project from the test-evidence store.
///
/// The test-evidence store holds greens only. A fail filed later at the same K_P (a flake,
/// or another checkout's run) lives in the outcome store, whose answer for a key is its
/// newest record (§2). So the reuse is refused when:
///   * the newest record at the FULL key is a fail no §25 clearance names (a later full pass,
///     or a clearance filed after the fail, lets the green stand: fail-then-pass);
///   * an IMPACT-scoped fail at the same K_P is filed at or after the full green that stands
///     (the newest full pass, the clearance, or `earnedAt` when the full key holds nothing):
///     it failed one of the very tests that green passed, over the same bytes. Only a later
///     full green clears it; a later impact pass may have run other tests;
///   * a file at either key, or at the clearance, could not be READ: it may be that fail, so
///     it is never read as "no fail" (the AUTOMATION-1568 rule).
/// With no outcome store wired there is nothing to consult, and the evidence store alone
/// decides, as it did before.
let reuseRefusal
    (outcomes: OutcomeStore.Store option)
    (key: TestEvidenceKey.Key)
    (earnedAt: DateTimeOffset)
    : string option =
    match outcomes with
    | None -> None
    | Some outcomes ->
        let full = testActionKey key "full"

        let unreadable (what: string) (why: string) =
            $"its %s{what} at its K_P could not be read, so a newer fail cannot be ruled out: %s{why}"

        let filedBy (r: OutcomeStore.Record) =
            let run = r.Producer.RunId |> Option.defaultValue "an unnamed run"
            $"""run %s{run} in %s{r.Producer.Worktree} at %s{r.RecordedAt.ToString("u")}"""

        // When the full green that stands was filed, or why none stands.
        let standing =
            match outcomes.TryHistory full.Value with
            | Error why -> Error(unreadable "full-run record" why)
            | Ok history ->
                match List.tryLast history with
                | None -> Ok earnedAt
                | Some pass when pass.Outcome = OutcomeStore.Outcome.Pass -> Ok pass.RecordedAt
                | Some fail ->
                    let clearances =
                        match fail.Producer.RunId with
                        | Some run -> outcomes.TryHistory (TestFailureClearance.keyOf full.Value run).Value
                        | None -> Ok []

                    match clearances with
                    | Error why -> Error(unreadable "clearance record" why)
                    | Ok records ->
                        match
                            records
                            |> List.filter (fun c ->
                                c.Outcome = OutcomeStore.Outcome.Pass && c.RecordedAt > fail.RecordedAt)
                            |> List.tryLast
                        with
                        | Some clearance -> Ok clearance.RecordedAt
                        | None ->
                            Error $"a full fail at its K_P, filed by %s{filedBy fail}, is newer than any green there"

        match standing with
        | Error why -> Some why
        | Ok since ->
            match outcomes.TryHistory (testActionKey key "impact").Value with
            | Error why -> Some(unreadable "impact record" why)
            | Ok history ->
                history
                |> List.filter (fun r -> r.Outcome = OutcomeStore.Outcome.Fail && r.RecordedAt >= since)
                |> List.tryLast
                |> Option.map (fun r ->
                    $"an impact fail at its K_P, filed by %s{filedBy r}, is newer than the full green there")

/// The launch-time half of a run: every key it computed, and the projects it reuses.
[<NoComparison; NoEquality>]
type Plan =
    {
        Wiring: Wiring
        /// The relativised options identity of a project, as the model published it at
        /// launch. The completion re-key uses the same lookup.
        OptionsHash: string -> string option
        Candidates: Candidate list
        /// Each candidate's key over the settled tree, before any test ran.
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
/// launch, with no outstanding failure, in a run that may consult evidence at all. A hit
/// is reused only when `reuseRefusal` finds no newer fail at its K_P in the outcome store:
/// this checkout's outstanding failures say nothing of a fail another checkout filed.
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

            c.Project,
            keys[c.Project]
            |> Result.map (fun key -> key, wiring.Store.Lookup(relative, key)))

    // Each eligible candidate's decision: the entry it reuses, or why it launches. A hit
    // stands only while the outcome store holds no newer fail at its K_P (ADR-059 §34).
    let decisions =
        lookups
        |> List.map (fun (project, lookup) ->
            project,
            match lookup with
            | Ok(key, TestEvidenceStore.Lookup.Hit entry) ->
                match reuseRefusal wiring.Outcomes key entry.Producer.EarnedAt with
                | None -> Ok entry
                | Some why -> Error why
            | Ok(_, TestEvidenceStore.Lookup.Miss miss) -> Error(TestEvidenceStore.describeMiss miss)
            | Error reason -> Error $"no key: %s{reason}")

    { Wiring = wiring
      OptionsHash = optionsHash
      Candidates = candidates
      LaunchKeys = keys
      Reused =
        decisions
        |> List.choose (fun (project, decision) ->
            decision |> Result.toOption |> Option.map (fun entry -> project, entry))
        |> Map.ofList
      Misses =
        decisions
        |> List.choose (fun (project, decision) ->
            match decision with
            | Ok _ -> None
            | Error why -> Some(project, why))
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

/// Each candidate's FULL-run outcome key over the tree as it is NOW, or why it has none
/// (AUTOMATION-1587): what a `test-baseline` record names its refusal by, where
/// `currentOutcomeKeys` leaves an unkeyable candidate out.
let currentOutcomeKeyResults
    (wiring: Wiring)
    (repoRoot: string)
    (optionsHash: string -> string option)
    (candidates: Candidate list)
    : Map<string, Result<string, string>> =
    keysOf wiring repoRoot optionsHash candidates
    |> fst
    |> Map.map (fun _ key -> key |> Result.map (fun key -> (testActionKey key "full").Value.Text))

/// The outcome-store key of each candidate's FULL run over the tree as it is NOW (ADR-059
/// step 7b): what a lookup verdict asks the store for. A candidate with no key is absent, so
/// nothing can stand in for it.
let currentOutcomeKeys
    (wiring: Wiring)
    (repoRoot: string)
    (optionsHash: string -> string option)
    (candidates: Candidate list)
    : Map<string, string> =
    keysOf wiring repoRoot optionsHash candidates
    |> fst
    |> Map.toList
    |> List.choose (fun (project, key) ->
        match key with
        | Ok key -> Some(project, (testActionKey key "full").Value.Text)
        | Error why ->
            // Named, never silently dropped (AUTOMATION-1617): no lookup can stand in for it.
            Logging.info "test-prune" $"%s{project}: no K_P key — %s{why}; no outcome record can stand in for it"
            None)
    |> Map.ofList

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

/// File `record`, the outcome of `run` (ADR-059 step 7), in `outcomes`. `fullKey` is the
/// project's full-run key under the same K_P. Returns the keys it filed under.
///
/// A failed run whose report names every failure it counts is filed with those names. A
/// passing run that passed each failure the newest full fail at `fullKey` names also files
/// that fail's clearance (`TestFailureClearance`, AUTOMATION-1444). A timed-out run names
/// nothing: what it did not finish is not among its failures.
let fileOutcome
    (outcomes: OutcomeStore.Store)
    (fullKey: ActionKey.ActionKey)
    (run: ProducedRun)
    (record: OutcomeStore.Record)
    : ActionKey.KeyValue list =
    let names = run.Report |> Option.bind Ctrf.tryTestNames

    match run.Result with
    | TestsFailed _ ->
        // Named only when the summary counts no failure but the named ones: no `other` row
        // (an errored test, a crashed host) and no failed row without a name.
        let outputs =
            match names, run.Report |> Option.bind Ctrf.trySummary with
            | Some n, Some summary when not n.Failed.IsEmpty && n.Failed.Length = summary.Failed && summary.Other = 0 ->
                Some(TestFailureClearance.outputsOf n.Failed)
            | _ -> None

        outcomes.RecordWithOutputs(record, outputs) |> ignore
        [ record.Key.Value ]
    | _ ->
        outcomes.Record record |> ignore

        let clearance =
            names
            |> Option.bind (fun n ->
                TestFailureClearance.clearanceOf fullKey.Value (outcomes.LookupWithOutputs fullKey) n.Passed record)

        match clearance with
        | Some c ->
            outcomes.Record c |> ignore
            [ record.Key.Value; c.Key.Value ]
        | None -> [ record.Key.Value ]

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

/// The run `publish` judges for a project: the result it reported, built by `produce`, unless
/// it has no result in this run or earned its green on a re-run under starvation, which the
/// stored evidence could not say.
let producedRunOf
    (result: TestResult option)
    (rerun: bool)
    (produce: TestResult -> ProducedRun)
    : Result<ProducedRun, string> =
    match result with
    | None -> Error "it has no result in this run"
    | Some _ when rerun -> Error "its green was earned on a re-run under starvation"
    | Some result -> Ok(produce result)

/// What `publish` did with each project the run launched: how many greens it filed, and
/// every launched project it filed nothing for, with the reason.
type Published =
    { Written: int
      Withheld: (string * string) list }

/// Publish every green `runOf` reports that `qualifies`, against the completion keys of
/// `reconciled`. Runs AFTER the run's own results are final — coverage that failed to
/// ingest has already made its project non-green — and never for a reused project.
/// `runOf` is `Error` naming why a project has no run to judge.
let publish
    (plan: Plan)
    (repoRoot: string)
    (reconciled: Reconciled)
    (reused: Map<string, TestEvidenceStore.Entry>)
    (runOf: Candidate -> Result<ProducedRun, string>)
    (producer: TestEvidenceStore.Producer)
    : Published =
    let judged =
        plan.Candidates
        |> List.filter (fun c -> not (reused.ContainsKey c.Project))
        |> List.map (fun c -> c, runOf c)

    let launched =
        judged
        |> List.choose (fun (c, run) ->
            match run with
            | Ok run -> Some(c, run)
            | Error _ -> None)

    match plan.Wiring.Outcomes with
    | Some outcomes ->
        let filed =
            launched
            |> List.collect (fun (candidate, run) ->
                match Map.tryFind candidate.Project plan.LaunchKeys with
                | Some(Ok atLaunch) ->
                    outcomeRecordOf
                        candidate
                        (Some(Ok atLaunch))
                        (Map.tryFind candidate.Project reconciled.Keys)
                        run
                        producer
                    |> Option.map (fileOutcome outcomes (testActionKey atLaunch "full") run)
                    |> Option.defaultValue []
                | _ -> [])

        // The box-wide store is bounded by TTL and bytes (ADR-059 §2). GC can only turn a hit
        // into a miss, and it spares what this run just filed.
        outcomes.Gc(producer.EarnedAt, Set.ofList filed) |> ignore
    | None -> ()

    let outcomes =
        judged
        |> List.map (fun (candidate, run) ->
            candidate.Project,
            run
            |> Result.bind (fun run ->
                qualifies
                    (Map.tryFind candidate.Project plan.LaunchKeys)
                    (Map.tryFind candidate.Project reconciled.Keys)
                    run
                |> Result.bind (fun key ->
                    if plan.Wiring.Store.Write(entryOf repoRoot candidate key producer run) then
                        Ok()
                    else
                        Error "the evidence store did not file it")))

    { Written = outcomes |> List.filter (snd >> Result.isOk) |> List.length
      Withheld =
        outcomes
        |> List.choose (fun (project, outcome) ->
            match outcome with
            | Ok() -> None
            | Error why -> Some(project, why)) }

/// One log line per project a run filed no evidence for, naming why (AUTOMATION-1511).
let withheldLines (published: Published) : string list =
    published.Withheld
    |> List.map (fun (project, why) -> $"%s{project}: no test evidence written — %s{why}")

/// The tally a run logs: what it reused, what it launched for want of evidence, and what
/// it published.
let tally (plan: Plan) (reconciled: Reconciled) (published: Published) =
    $"test evidence: reused %d{Map.count reconciled.Held}, moved %d{reconciled.Withdrawn.Length}, missed %d{Map.count plan.Misses}, written %d{published.Written}, withheld %d{published.Withheld.Length} (%d{plan.Lookups} lookup(s), %d{plan.BytesHashed} byte(s) hashed at launch)"
