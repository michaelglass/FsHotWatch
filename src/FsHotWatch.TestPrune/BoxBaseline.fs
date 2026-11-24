/// AUTOMATION-1793 step 2 (intelligence ADR 0651, revised): a checkout with no full pass of
/// its own restarts from the box's retained green baseline, behind `tests.restartFromBoxBaseline`.
///
/// A land checks out a new merge every time, so its own suite has passed no tree and
/// `PluginSteps.restartBaselineIn` refuses: the land runs every project in full. Any checkout
/// that passed a tree T in full filed T's `test-baseline` record, pinning the analysis record
/// of every analysable file (step 1), and GC keeps those of the newest `RetainedTrees`. So:
///
///   1. CHOOSE, among those retained trees, the T whose manifest differs least from the working
///      files (`distance`, `choose`). Trees are content identities: any green T is sound, and
///      the choice only decides how much the diff T to T' selects. VCS ancestry plays no part.
///   2. RESOLVE every analysable file of T (`BaselineRebuild.resolve`), or there is no baseline.
///   3. REBUILD the index at T: each resolved analysis written exactly as the cold scan writes a
///      live one (`FileCheckedHandler.indexedAnalysisOf`, `IndexMaintenance.writeAnalyses`), in
///      one transaction, with the `file_keys` identities T's manifest names; then SEAL it at T.
///
/// The restart then diffs T to T' through the existing path, as a daemon that had passed T
/// would. Every failure leaves the index not sealed at T, and is no baseline.
module FsHotWatch.TestPrune.BoxBaseline

open System
open System.IO
open FsHotWatch
open FsHotWatch.Events
open TestPrune.AstAnalyzer
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.PluginState

/// The extensions of an F# source: a working file the manifest lacks counts as a source
/// difference only with one of these.
let private sourceExtensions = set [ ".fs"; ".fsi"; ".fsx" ]

/// How far `record`'s manifest is from the working files `current` (path, content hash, as the
/// receipt tree hashes them): every manifest path whose hash differs or that the working files
/// lack, and every working file the manifest lacks. A path is a source when T analysed it, or,
/// absent from T, when it is an F# source; every other difference is non-source.
let distance (record: TestBaseline.CheckoutRecord) (current: (string * string) list) : BoxDistance =
    let working = Map.ofList current
    let manifest = Map.ofList record.Entries

    let differing =
        (record.Entries
         |> List.filter (fun (path, hash) -> Map.tryFind path working <> Some hash)
         |> List.map fst)
        @ (current
           |> List.filter (fun (path, _) -> not (manifest.ContainsKey path))
           |> List.map fst)

    let isSource (path: string) =
        record.Analyses.ContainsKey path
        || (not (manifest.ContainsKey path)
            && sourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))

    { Differing = differing.Length
      NonSource = differing |> List.filter (isSource >> not) |> List.length }

/// The baselines a restart may rebuild from: the newest `TestBaseline.RetainedTrees` index
/// entries whose record still reads as a pass from this analyser build and whose manifest
/// hashes to its tree, newest first. READ-ONLY.
let retained (store: OutcomeRecords.IOutcomeStore) : TestBaseline.CheckoutRecord list =
    TestBaseline.readIndex store
    |> List.truncate TestBaseline.RetainedTrees
    |> List.choose (fun entry ->
        match TestBaseline.checkoutRecordFor store entry.Tree with
        | Ok record when TreeHash.hashEntries record.Entries = record.Tree -> Some record
        | Ok _
        | Error _ -> None)

/// Of `records` (newest first), the one whose manifest differs least from `current`: fewest
/// non-source differences, then fewest differences, then the newest.
let choose
    (records: TestBaseline.CheckoutRecord list)
    (current: ReceiptTree)
    : (TestBaseline.CheckoutRecord * BoxDistance) option =
    records
    |> List.mapi (fun newestFirst record -> newestFirst, record, distance record current.Entries)
    |> List.sortBy (fun (newestFirst, _, d) -> d.NonSource, d.Differing, newestFirst)
    |> List.tryHead
    |> Option.map (fun (_, record, d) -> record, d)

/// The K_P labels that digest only the verdict tree's file contents: the files under the
/// closure's project directories, under no project, and under other projects. A move in any
/// of them between T and the working tree is a path of the T to T' diff, which the restart
/// selects from (symbols) or widens for (a moved non-symbol path). Every other label
/// (toolchain, test configuration, options, packages, declared reads, environment, scope) is
/// outside that diff, so a move there refuses the project.
let treeContentLabels = set [ "closure"; "shared"; "otherProjects" ]

/// The K_P a full-run outcome record was filed under, read back from its key's labelled
/// inputs (`TestEvidenceRun.testActionKey`: each K_P label as `kp:<label>`, the K_P as `kp`).
/// `None` for a record whose key carries no K_P.
let private kpOf (record: OutcomeStore.Record) : TestEvidenceKey.Key option =
    let labels =
        record.Key.Inputs
        |> List.filter (fun (label, _) -> label.StartsWith("kp:", StringComparison.Ordinal))
        |> List.map (fun (label, digest) -> label.Substring 3, digest)

    record.Key.Inputs
    |> List.tryFind (fun (label, _) -> label = "kp")
    |> Option.map (fun (_, value) -> { Value = value; Labels = labels })

/// Whether T's full pass vouches for `project` now (`BoxProject`). Every K_P refusal is
/// re-checked here, through the reuse path's own `TestEvidenceRun.reuseRefusal`: the record
/// at the K_P T's baseline names must be the newest there and a pass, no newer fail may stand
/// at that K_P or at the project's CURRENT one, and nothing but the tree's file contents may
/// have moved between the two (`treeContentLabels`).
let judgeProject
    (store: OutcomeRecords.IOutcomeStore)
    (fullKeys: Map<string, string>)
    (currentKeys: Map<string, Result<TestEvidenceKey.Key, string>>)
    (compileInputs: Map<string, string * string>)
    (project: string)
    : BoxProject =
    let atT =
        match Map.tryFind project fullKeys |> Option.bind ActionKey.KeyValue.tryParse with
        | None -> Error "T's baseline names no full-run K_P for it"
        | Some key ->
            match OutcomeStore.Through.lookup store key with
            | None -> Error "no full-scope record at the K_P T's baseline names"
            | Some record when record.Outcome <> OutcomeStore.Outcome.Pass ->
                Error "its newest full-scope record at the K_P T's baseline names is not a pass"
            | Some record ->
                match kpOf record with
                | None -> Error "the record at the K_P T's baseline names carries no K_P"
                | Some kp -> Ok(kp, record.RecordedAt)

    let vouched =
        atT
        |> Result.bind (fun (kp, passedAt) ->
            match TestEvidenceRun.reuseRefusal (Some store) kp passedAt with
            | Some why -> Error $"at the K_P T's baseline names: %s{why}"
            | None ->
                match Map.tryFind project currentKeys with
                | None -> Error "no current K_P: the project model does not name it as a configured test project"
                | Some(Error why) -> Error $"no current K_P: %s{why}"
                | Some(Ok now) ->
                    match
                        TestEvidenceKey.moved kp.Labels now.Labels
                        |> List.filter (treeContentLabels.Contains >> not)
                    with
                    | _ :: _ as moved ->
                        let names = String.concat ", " moved
                        Error $"its K_P moved beyond the tree's file contents since T (%s{names})"
                    | [] ->
                        match TestEvidenceRun.reuseRefusal (Some store) now passedAt with
                        | Some why -> Error $"at its current K_P: %s{why}"
                        | None ->
                            match Map.tryFind project compileInputs with
                            | None -> Error "the project model publishes no compile-input identity for it"
                            | Some inputs -> Ok inputs)

    match vouched with
    | Ok(fsproj, identity) -> BoxProject.Vouched(fsproj, identity)
    | Error why -> BoxProject.Refused why

/// `judgeProject` for every project T's baseline names and every one the current model
/// configures: a project T never passed is refused, and runs whole.
let judge
    (store: OutcomeRecords.IOutcomeStore)
    (currentKeys: Map<string, Result<TestEvidenceKey.Key, string>>)
    (compileInputs: Map<string, string * string>)
    (record: TestBaseline.CheckoutRecord)
    : Map<string, BoxProject> =
    Seq.append (Map.keys record.FullKeys) (Map.keys currentKeys)
    |> Seq.distinct
    |> Seq.map (fun project -> project, judgeProject store record.FullKeys currentKeys compileInputs project)
    |> Map.ofSeq

/// T's full pass as this checkout's ledger records an own one (`FullSuiteBaseline`): its run,
/// when it was filed, the projects it passed and T. Its run must be a run id, as every test
/// run's is.
let private ledgerOf (record: TestBaseline.CheckoutRecord) : Result<FullSuiteBaseline.Baseline, string> =
    match Guid.TryParse record.RunId with
    | true, runId ->
        Ok
            { RunId = runId
              EarnedAt = record.FiledAt.UtcDateTime
              Projects = record.FullKeys |> Map.keys |> Set.ofSeq
              Tree = Some record.Tree
              EarlierTrees = [] }
    | false, _ -> Error $"T's baseline names run %s{record.RunId}, which is not a run id"

/// What T's full pass vouches for per project, judged over the current model with the
/// evidence wiring and outcome store (`judge`, as `PluginSteps.boxJudgeIn` wires it).
type Judge =
    TestEvidenceRun.Wiring -> OutcomeRecords.IOutcomeStore -> TestBaseline.CheckoutRecord -> Map<string, BoxProject>

/// What a rebuild reads and writes.
type RebuildEnv =
    {
        /// The box-wide outcome store: the index, the baselines and the analysis records.
        Store: OutcomeRecords.IOutcomeStore
        /// This checkout's root, which the records are read and the analyses named under.
        Root: string
        /// This checkout's project model: every project a repo-relative path compiles in.
        ProjectsOf: string -> string list
        /// The impact index, whose seal the rebuild writes.
        DbPath: string
        /// Write analyses (one per file) and their `file_keys` identities to the index in one
        /// transaction, or throw having written none (`IndexMaintenance.writeAnalyses`).
        Write: AnalysisResult list -> (string * string) list -> unit
        /// What T's full pass vouches for per project now (`judge` over the current model).
        Judge: TestBaseline.CheckoutRecord -> Map<string, BoxProject>
        Log: string -> unit
    }

/// One line naming T and the misses that refused it.
let private describeMisses (tree: string) (misses: BaselineRebuild.Miss list) : string =
    let noProject, noRecord =
        misses
        |> List.partition (fun miss ->
            match miss with
            | BaselineRebuild.Miss.NoProject _ -> true
            | BaselineRebuild.Miss.NoRecord _ -> false)

    $"box-baseline: tree %s{tree} does not resolve: %d{misses.Length} analysable file(s) missed "
    + $"(no-project=%d{noProject.Length}, no-record=%d{noRecord.Length})"

/// Write every resolved analysis of T into the index exactly as the cold scan writes a live
/// one, then seal it at T; or say why not, the index not sealed at T. The write is one
/// transaction, so a failed one leaves the rows and seal as they were; a seal that does not
/// read back as T is no baseline.
let private writeAndSeal
    (env: RebuildEnv)
    (record: TestBaseline.CheckoutRecord)
    (resolved: BaselineRebuild.Resolved list)
    : Result<unit, string> =
    let written =
        try
            env.Write
                (resolved
                 |> List.map (fun r -> FileCheckedHandler.indexedAnalysisOf env.Root r.Project r.Analysis))
                (resolved |> List.map (fun r -> r.Path, r.Identity))

            Ok()
        with ex ->
            Error $"rebuilding the index at %s{record.Tree} failed: %s{ex.GetType().Name}: %s{ex.Message}"

    written
    |> Result.bind (fun () ->
        IndexSeal.save env.Log env.DbPath (Some record.Tree)

        if IndexSeal.load env.DbPath = Some record.Tree then
            Ok()
        else
            Error $"the index was rebuilt at %s{record.Tree} but its seal could not be written")

/// The retained tree closest to `current`, unless it IS `current`; or why none.
let private candidate
    (store: OutcomeRecords.IOutcomeStore)
    (current: ReceiptTree)
    : Result<TestBaseline.CheckoutRecord * BoxDistance, string> =
    match choose (retained store) current with
    | None -> Error "no retained test-baseline reads as a pass of this analyser build"
    | Some(record, _) when record.Tree = current.Identity ->
        Error $"the tree has not moved from the retained baseline %s{record.Tree} (the skip on records answers it)"
    | Some chosen -> Ok chosen

/// The retained tree a restart over `current` would rebuild from, and how far it is; or why
/// none. READ-ONLY: what `rebuild` chooses and resolves, without writing. A row resolves here
/// through the project its record pinned; whether the restarted daemon's model still
/// compiles the file there is the restart's own check.
let predict
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (current: ReceiptTree)
    : Result<TestBaseline.CheckoutRecord * BoxDistance, string> =
    candidate store current
    |> Result.bind (fun (record, chosenDistance) ->
        let pinned =
            record.Analyses
            |> Map.toList
            |> List.choose (fun (path, analysis) ->
                match analysis with
                | TestBaseline.RowAnalysis.Pinned(_, project, _) -> Some(path, [ project ])
                | TestBaseline.RowAnalysis.Unpinned _ -> None)
            |> Map.ofList

        match
            BaselineRebuild.resolve store root (fun path -> Map.tryFind path pinned |> Option.defaultValue []) record
        with
        | Error misses -> Error(describeMisses record.Tree misses)
        | Ok _ -> Ok(record, chosenDistance))

/// Rebuild this checkout's index at the retained tree closest to `current` and seal it there,
/// returning the baseline the restart diffs from; or why there is none. Every refusal leaves
/// the index not sealed at T: the seal it had before, or none.
let rebuild (env: RebuildEnv) (current: ReceiptTree) : Result<BoxRebuilt, string> =
    match candidate env.Store current with
    | Error why -> Error why
    | Ok(record, chosenDistance) ->
        match ledgerOf record, BaselineRebuild.resolve env.Store env.Root env.ProjectsOf record with
        | Error why, _ -> Error why
        | Ok _, Error misses ->
            let line = describeMisses record.Tree misses
            env.Log line
            Error line
        | Ok ledger, Ok resolved ->
            writeAndSeal env record resolved
            |> Result.map (fun () ->
                { Baseline =
                    { Tree = record.Tree
                      Key = record.Key
                      RunId = record.RunId
                      Manifest =
                        { Identity = record.Tree
                          Entries = record.Entries } }
                  Distance = chosenDistance
                  Ledger = ledger
                  Projects = env.Judge record })

/// `state` once it restarts from the box baseline `rebuilt` (AUTOMATION-1793 step 2b), as it
/// would after an own full pass at T: T's pass becomes the ledger's full-suite baseline (the
/// debt a ledger with none owes), and each project it vouches for counts as whole-project
/// evidence beside any this checkout recorded itself, which wins where both name a project.
/// The ledger is replaced only when T's pass vouches for at least every project the ledger
/// names (it always names every runnable project at T): a ledger that already vouches for
/// more is kept, and the restart still diffs from T.
/// A refused project has neither, and runs whole. Logs one line naming both.
let internal adopt (rebuilt: BoxRebuilt) (state: TestPruneState) : TestPruneState =
    let projects = Map.toList rebuilt.Projects

    let inputs =
        projects
        |> List.choose (fun (name, project) ->
            match project with
            | BoxProject.Vouched(fsproj, identity) -> Some(name, (fsproj, identity))
            | BoxProject.Refused _ -> None)
        |> Map.ofList

    let refused =
        projects
        |> List.choose (fun (name, project) ->
            match project with
            | BoxProject.Refused why -> Some $"%s{name} (%s{why})"
            | BoxProject.Vouched _ -> None)

    let own =
        state.RestoredEvidence
        |> Option.map EarnedEvidence.wholeProjectInputs
        |> Option.defaultValue Map.empty

    let refusedText = String.concat "; " refused

    Logging.info
        "test-prune"
        $"box-baseline: adopted tree %s{rebuilt.Baseline.Tree} run %s{rebuilt.Baseline.RunId} as the full-suite baseline; vouches for %d{inputs.Count} project(s); refused %d{refused.Length}: %s{refusedText}"

    { state with
        Debt =
            { state.Debt with
                Baseline =
                    match state.Debt.Baseline with
                    | Some own when not (Set.isSubset own.Projects rebuilt.Ledger.Projects) -> Some own
                    | Some _
                    | None -> Some(FullSuiteBaseline.succeed state.Debt.Baseline rebuilt.Ledger) }
        RestoredEvidence = EarnedEvidence.restored (Map.fold (fun acc name entry -> Map.add name entry acc) inputs own) }
