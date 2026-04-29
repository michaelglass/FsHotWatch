module FsHotWatch.JjHelper

open System
open System.IO
open FsHotWatch.ProcessHelper
open FsHotWatch.Logging

/// Get the jj working copy commit_id (content-addressed hash of entire tree).
/// This call triggers jj's auto-snapshot, so the returned ID reflects current disk state.
let internal getWorkingCopyCommitId (repoRoot: string) : string option =
    try
        match ProcessHelper.runProcess "jj" "log -r @ --no-graph -T commit_id" repoRoot [] with
        | ProcessOutcome.Succeeded output ->
            let id = output.Trim()
            if id.Length > 0 then Some id else None
        | _ ->
            Logging.debug "jj-helper" "Failed to get jj commit_id"
            None
    with ex ->
        Logging.debug "jj-helper" $"jj commit_id error: %s{ex.Message}"
        None

/// Get files changed between two jj commits. Returns absolute paths.
let internal getChangedFiles (repoRoot: string) (fromCommitId: string) : Set<string> =
    try
        match ProcessHelper.runProcess "jj" $"diff --name-only --from %s{fromCommitId} --to @" repoRoot [] with
        | ProcessOutcome.Succeeded output ->
            output.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun relPath -> Path.GetFullPath(Path.Combine(repoRoot, relPath.Trim())))
            |> Set.ofArray
        | _ ->
            Logging.debug "jj-helper" "jj diff failed, treating all files as changed"
            Set.empty
    with ex ->
        Logging.debug "jj-helper" $"jj diff error: %s{ex.Message}"
        Set.empty

/// Result of the jj scan guard decision.
type JjScanDecision =
    /// commit_id unchanged — serve all results from cache, skip FCS checks entirely.
    | SkipAll
    /// commit_id changed — only these files (absolute paths) need rechecking.
    | CheckSubset of changedFiles: Set<string>
    /// No stored commit_id or jj unavailable — check everything.
    | CheckAll

/// Manages jj-based scan optimization.
/// Reads/writes last-commit.id from .fshw/ directory.
/// Call BeginScan() at start of each scan cycle, CommitScanSuccess() at end.
type JjScanGuard(repoRoot: string, ?getCommitId: unit -> string option, ?getDiff: string -> Set<string>) =
    let commitIdPath =
        Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "last-commit.id")

    let getCommitId = defaultArg getCommitId (fun () -> getWorkingCopyCommitId repoRoot)
    let getDiff = defaultArg getDiff (getChangedFiles repoRoot)

    let mutable currentCommitId: string option = None

    let readStoredCommitId () : string option =
        try
            if File.Exists(commitIdPath) then
                let id = File.ReadAllText(commitIdPath).Trim()
                if id.Length > 0 then Some id else None
            else
                None
        with ex ->
            Logging.debug "jj" $"Could not read commit ID: %s{ex.Message}"
            None

    let truncId (id: string) =
        if id.Length > 8 then id.[..7] + "…" else id

    /// Call at the beginning of each scan cycle.
    member _.BeginScan() : JjScanDecision =
        let freshId = getCommitId ()
        currentCommitId <- freshId
        let storedId = readStoredCommitId ()

        match freshId, storedId with
        | Some current, Some stored when current = stored ->
            Logging.info "jj-guard" $"commit_id unchanged (%s{truncId current}) — all caches valid"
            SkipAll
        | Some current, Some stored ->
            Logging.info "jj-guard" $"commit_id changed: %s{truncId stored} → %s{truncId current}"
            let changed = getDiff stored
            Logging.info "jj-guard" $"%d{changed.Count} files changed according to jj diff"
            CheckSubset changed
        | Some current, None ->
            Logging.info "jj-guard" $"No stored commit_id — first scan (%s{truncId current})"
            CheckAll
        | None, _ ->
            Logging.debug "jj-guard" "Could not get jj commit_id — falling back to full scan"
            CheckAll

    /// Call after a successful scan to persist the current commit_id.
    member _.CommitScanSuccess() =
        match currentCommitId with
        | Some id ->
            try
                let dir = Path.GetDirectoryName(commitIdPath)

                if not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore

                File.WriteAllText(commitIdPath, id)
                Logging.debug "jj-guard" $"Stored commit_id: %s{truncId id}"
            with ex ->
                Logging.error "jj-guard" $"Failed to write commit_id: %s{ex.Message}"
        | None -> ()
