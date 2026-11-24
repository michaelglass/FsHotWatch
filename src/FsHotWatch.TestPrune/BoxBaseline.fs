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

open System.IO
open FsHotWatch
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

/// Rebuild this checkout's index at the retained tree closest to `current` and seal it there,
/// returning the baseline the restart diffs from; or why there is none. Every refusal leaves
/// the index not sealed at T: the seal it had before, or none.
let rebuild (env: RebuildEnv) (current: ReceiptTree) : Result<BoxRebuilt, string> =
    match choose (retained env.Store) current with
    | None -> Error "no retained test-baseline reads as a pass of this analyser build"
    | Some(record, _) when record.Tree = current.Identity ->
        Error $"the tree has not moved from the retained baseline %s{record.Tree} (the skip on records answers it)"
    | Some(record, chosenDistance) ->
        match BaselineRebuild.resolve env.Store env.Root env.ProjectsOf record with
        | Error misses ->
            let line = describeMisses record.Tree misses
            env.Log line
            Error line
        | Ok resolved ->
            writeAndSeal env record resolved
            |> Result.map (fun () ->
                { Baseline =
                    { Tree = record.Tree
                      Key = record.Key
                      RunId = record.RunId
                      Manifest =
                        { Identity = record.Tree
                          Entries = record.Entries } }
                  Distance = chosenDistance })
