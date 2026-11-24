/// AUTOMATION-1793 (intelligence ADR 0651, revised): the analyses a checkout with no full
/// pass of its own needs to rebuild its impact index at a tree T another checkout passed in
/// full, found in the box-wide analysis records (`AnalysisRecords`, AUTOMATION-1482) under
/// the identities T's `test-baseline` manifest names.
///
/// A land checks out a new merge every time, so its own full suite has passed no tree and a
/// restart has nothing to diff from (`PluginSteps.restartBaselineIn`, ADR-071). Every file's
/// analysis at T is already filed box-wide, named relative to the root it was analysed
/// under, so any checkout reads its own: rebuilding the rows from them gives the index T's
/// passing checkout had, with no copied database and no foreign paths.
///
/// The soundness of the whole thing is that EVERY analysable entry resolves, or there is no
/// baseline. A partial index would diff T to T' against missing rows and select too little,
/// the one failure a test-impact tool must not have. So `resolve` returns either every
/// analysis or every miss, never some of the analyses.
///
/// Each analysable row names the exact record its analysis is read from (`RowAnalysis.Pinned`:
/// the project and key filing found it under, AUTOMATION-1793 step 1 amendment 2), and the
/// index keeps the records of its newest `TestBaseline.RetainedTrees` baselines alive against
/// GC. Resolving is an exact lookup, never a search: a linked file's choice of project was
/// made, or refused, when the baseline was filed.
module FsHotWatch.TestPrune.BaselineRebuild

open FsHotWatch
open TestPrune.AstAnalyzer

/// Why an analysable manifest entry did not resolve. Each one means "no baseline".
[<RequireQualifiedAccess>]
type Miss =
    /// The reading checkout's project model no longer compiles the file in the project whose
    /// record the row pinned: the tree no longer has it, or that project no longer compiles it.
    | NoProject of path: string
    /// No readable analysis record under the row's pinned key: evicted (the baseline fell out
    /// of the retained trees), a key this analyser build would not compute for the row, a
    /// record that does not read under this root, or a row filed before keys were pinned
    /// (`RowAnalysis.Unpinned`), whose analyses were never kept.
    | NoRecord of path: string

/// One analysable file of T, with the analysis that holds for it there.
type Resolved =
    { Path: string
      Project: string
      Analysis: AnalysisResult }

/// One pinned row's analysis, read from `store` under `root`.
let private resolvePinned
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (projectsOf: string -> string list)
    (path: string)
    (identity: string)
    (project: string)
    (pinnedKey: string)
    : Result<Resolved, Miss> =
    if not (List.contains project (projectsOf path)) then
        Error(Miss.NoProject path)
    else
        let key = AnalysisRecords.keyOf identity path project

        if key.Value.Text <> pinnedKey then
            Error(Miss.NoRecord path)
        else
            match AnalysisRecords.lookup store root key with
            | None -> Error(Miss.NoRecord path)
            | Some analysis ->
                Ok
                    { Path = path
                      Project = project
                      Analysis = analysis }

/// Every analysable file of `record`'s manifest with its analysis read from `store` under
/// this checkout's `root`, in manifest order; or every entry that did not resolve.
/// `projectsOf` is the reading checkout's project model: every project a repo-relative path
/// compiles in. A pinned row resolves only while its project still compiles the file, this
/// analyser computes the key it pinned, and the record under that key reads. READ-ONLY.
let resolve
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (projectsOf: string -> string list)
    (record: TestBaseline.CheckoutRecord)
    : Result<Resolved list, Miss list> =
    let attempts =
        record.Entries
        |> List.choose (fun (path, _) -> record.Analyses |> Map.tryFind path |> Option.map (fun a -> path, a))
        |> List.map (fun (path, analysis) ->
            match analysis with
            | TestBaseline.RowAnalysis.Pinned(identity, project, key) ->
                resolvePinned store root projectsOf path identity project key
            | TestBaseline.RowAnalysis.Unpinned _ -> Error(Miss.NoRecord path))

    let resolved, misses =
        List.foldBack
            (fun attempt (resolved, misses) ->
                match attempt with
                | Ok r -> r :: resolved, misses
                | Error miss -> resolved, miss :: misses)
            attempts
            ([], [])

    match misses with
    | [] -> Ok resolved
    | _ -> Error misses
