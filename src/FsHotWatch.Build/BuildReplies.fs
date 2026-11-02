/// The replies of the build plugin's commands, as types: each command's reply schema id,
/// the reply it carries, the JSON it is written as, and the reading of that JSON back.
module FsHotWatch.Build.BuildReplies

open System.Text.Json

/// The reply schema of `build-status`.
[<Literal>]
let BuildStatusSchema = "build/build-status-v1"

/// The reply schema of `force-rebuild`.
[<Literal>]
let ForceRebuildSchema = "build/force-rebuild-v1"

/// What `build-status` answers: the last build's outcome, with its (truncated) output.
[<RequireQualifiedAccess>]
type BuildStatus =
    | Passed of output: string
    | Failed of output: string
    | NotRun

module BuildStatus =
    let serialize (status: BuildStatus) : string =
        match status with
        | BuildStatus.Passed output -> JsonSerializer.Serialize({| status = "passed"; output = output |})
        | BuildStatus.Failed output -> JsonSerializer.Serialize({| status = "failed"; output = output |})
        | BuildStatus.NotRun -> JsonSerializer.Serialize({| status = "not run" |})

    /// The status a `build-status` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : BuildStatus option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryString root "status", FsHotWatch.Json.tryString root "output" with
            | Some "passed", Some output -> Some(BuildStatus.Passed output)
            | Some "failed", Some output -> Some(BuildStatus.Failed output)
            | Some "not run", None -> Some BuildStatus.NotRun
            | _ -> None)

/// What `force-rebuild` answers once the request is committed: the next build is forced.
[<RequireQualifiedAccess>]
type ForceRebuild = | Forced

module ForceRebuild =
    let serialize (ForceRebuild.Forced: ForceRebuild) : string =
        JsonSerializer.Serialize({| status = "ok"; forced = true |})

    /// The acknowledgement a `force-rebuild` payload carries; `None` for one this schema does
    /// not describe.
    let tryParse (payload: string) : ForceRebuild option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryString root "status", FsHotWatch.Json.tryBool root "forced" with
            | Some "ok", Some true -> Some ForceRebuild.Forced
            | _ -> None)
