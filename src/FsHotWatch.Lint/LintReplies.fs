/// The reply of the lint plugin's `warnings` command, as a type: its schema id, the counts it
/// carries, the JSON it is written as, and the reading of that JSON back.
module FsHotWatch.Lint.LintReplies

/// The reply schema of `warnings`.
[<Literal>]
let WarningsSchema = "lint/warnings-v1"

/// What `warnings` answers: how many files hold lint warnings, and how many there are.
type Warnings = { Files: int; Warnings: int }

module Warnings =
    let serialize (warnings: Warnings) : string =
        $"{{\"files\": %d{warnings.Files}, \"warnings\": %d{warnings.Warnings}}}"

    /// The counts a `warnings` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : Warnings option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryInt root "files", FsHotWatch.Json.tryInt root "warnings" with
            | Some files, Some warnings -> Some { Files = files; Warnings = warnings }
            | _ -> None)
