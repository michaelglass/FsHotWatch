module FsHotWatch.JjHelper

open System.Threading
open FsHotWatch.ProcessHelper
open FsHotWatch.Logging

/// Get the jj working copy commit_id (content-addressed hash of entire tree)
let getWorkingCopyCommitId (repoRoot: string) : string option =
    try
        let (success, output) =
            ProcessHelper.runProcess "jj" "log -r @ --no-graph -T commit_id" repoRoot []

        if success then
            let id = output.Trim()
            if id.Length > 0 then Some id else None
        else
            Logging.debug "jj-helper" "Failed to get jj commit_id"
            None
    with ex ->
        Logging.debug "jj-helper" $"jj commit_id error: %s{ex.Message}"
        None

/// Memoize the current working copy commit_id for this process
let mutable private cachedCommitId: string option = None

/// Get memoized jj commit_id (avoids repeated jj calls during a single daemon session)
let currentCommitId (repoRoot: string) : string option =
    match Volatile.Read(&cachedCommitId) with
    | Some id -> Some id
    | None ->
        let id = getWorkingCopyCommitId repoRoot
        Volatile.Write(&cachedCommitId, id)
        id
