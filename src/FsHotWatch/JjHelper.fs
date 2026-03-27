module FsHotWatch.JjHelper

open System
open FsHotWatch.ProcessHelper
open FsHotWatch.Logging

/// Get the jj working copy commit_id (content-addressed hash of entire tree)
let getWorkingCopyCommitId () : string option =
    try
        let (success, output) = ProcessHelper.runProcess "jj" "log -r @ --no-graph -T commit_id" "" []

        if success then
            let id = output.Trim()
            if id.Length > 0 then Some id else None
        else
            Logging.debug "jj-helper" "Failed to get jj commit_id"
            None
    with ex ->
        Logging.debug "jj-helper" $"jj commit_id error: %s{ex.Message}"
        None

/// Get files changed between two jj snapshots
let getChangedFiles (fromCommitId: string) : Set<string> option =
    try
        let args = $"diff --name-only --from %s{fromCommitId} --to @"
        let (success, output) =
            ProcessHelper.runProcess "jj" args "" []

        if success then
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun f -> f.Trim())
            |> Array.filter (fun f -> f.Length > 0)
            |> Set.ofArray
            |> Some
        else
            None
    with ex ->
        Logging.debug "jj-helper" $"jj diff error: %s{ex.Message}"
        None

/// Memoize the current working copy commit_id for this process
let private mutableCachedCommitId : string option ref = ref None

/// Get memoized jj commit_id (avoids repeated jj calls during a single daemon session)
let currentCommitId () : string option =
    match !mutableCachedCommitId with
    | Some id -> Some id
    | None ->
        let id = getWorkingCopyCommitId ()
        mutableCachedCommitId := id
        id
