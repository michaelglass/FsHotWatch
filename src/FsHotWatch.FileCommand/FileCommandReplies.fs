/// The reply of a file command's `<name>-status` command, as a type: its schema id, the status
/// it carries, the JSON it is written as, and the reading of that JSON back.
module FsHotWatch.FileCommand.FileCommandReplies

open System.Text.Json

/// The reply schema of every file command's `<name>-status`.
[<Literal>]
let StatusSchema = "file-command/status-v1"

/// What `<name>-status` answers: how the command's last run ended, if it has run.
[<RequireQualifiedAccess>]
type CommandStatus =
    | Passed
    | Failed
    | NotRun

module CommandStatus =
    let serialize (status: CommandStatus) : string =
        match status with
        | CommandStatus.Passed -> JsonSerializer.Serialize({| passed = true |})
        | CommandStatus.Failed -> JsonSerializer.Serialize({| passed = false |})
        | CommandStatus.NotRun -> JsonSerializer.Serialize({| status = "not run" |})

    /// The status a `<name>-status` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : CommandStatus option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryBool root "passed", FsHotWatch.Json.tryString root "status" with
            | Some true, None -> Some CommandStatus.Passed
            | Some false, None -> Some CommandStatus.Failed
            | None, Some "not run" -> Some CommandStatus.NotRun
            | _ -> None)
