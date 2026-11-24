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
module FsHotWatch.TestPrune.BaselineRebuild

open FsHotWatch
open TestPrune.AstAnalyzer

/// Why an analysable manifest entry did not resolve. Each one means "no baseline".
[<RequireQualifiedAccess>]
type Miss =
    /// The reading checkout's project model places the file in no project: the tree no
    /// longer has it, or no longer compiles it.
    | NoProject of path: string
    /// No analysis record under the file's key for any project that compiles it: none was
    /// filed, or it was filed by another analyser build (whose key differs), or it does not
    /// read under this root.
    | NoRecord of path: string
    /// The file compiles in several projects (a linked file) and their records under the
    /// manifest's exact identity hold DIFFERENT analyses: which one the index held cannot
    /// be told, so there is no baseline.
    | Ambiguous of path: string * projects: string list

/// One analysable file of T, with the analysis that holds for it there.
type Resolved =
    { Path: string
      Project: string
      Analysis: AnalysisResult }

/// Every analysable file of `record`'s manifest (each one with an analysis identity) with
/// its analysis read from `store` under this checkout's `root`, in manifest order; or every
/// entry that did not resolve. `projectsOf` is the reading checkout's project model: every
/// project a repo-relative path compiles in (more than one for a linked file). The record
/// is looked up under the manifest's EXACT identity for each; one hit, or several holding
/// the same analysis, resolves it. READ-ONLY.
let resolve
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (projectsOf: string -> string list)
    (record: TestBaseline.CheckoutRecord)
    : Result<Resolved list, Miss list> =
    let attempts =
        record.Entries
        |> List.choose (fun (path, _) -> record.Identities |> Map.tryFind path |> Option.map (fun id -> path, id))
        |> List.map (fun (path, identity) ->
            match projectsOf path with
            | [] -> Error(Miss.NoProject path)
            | projects ->
                let hits =
                    projects
                    |> List.choose (fun project ->
                        AnalysisRecords.lookup store root (AnalysisRecords.keyOf identity path project)
                        |> Option.map (fun analysis -> project, analysis))

                match hits with
                | [] -> Error(Miss.NoRecord path)
                | (project, analysis) :: others when others |> List.forall (fun (_, other) -> other = analysis) ->
                    Ok
                        { Path = path
                          Project = project
                          Analysis = analysis }
                | _ -> Error(Miss.Ambiguous(path, hits |> List.map fst)))

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
