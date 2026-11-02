/// The reply of the format-check plugin's `unformatted` command, as a type: its schema id, what
/// it carries, the JSON it is written as, and the reading of that JSON back.
module FsHotWatch.Fantomas.FormatReplies

open System.Text.Encodings.Web
open System.Text.Json

/// The reply schema of `unformatted`.
[<Literal>]
let UnformattedSchema = "format-check/unformatted-v1"

/// What `unformatted` answers: how many files are unformatted, and their paths joined with
/// ", " for display (a path may itself hold ", ", so the joined text is not split back).
type Unformatted = { Count: int; Files: string }

module Unformatted =
    /// Escapes only what JSON requires (a quote, a backslash, a control character), so every
    /// path that was written unescaped before is written byte for byte as it was.
    let private filesOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let serialize (unformatted: Unformatted) : string =
        let files = JsonSerializer.Serialize(unformatted.Files, filesOptions)
        $"{{\"count\": %d{unformatted.Count}, \"files\": %s{files}}}"

    /// What an `unformatted` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : Unformatted option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryInt root "count", FsHotWatch.Json.tryString root "files" with
            | Some count, Some files -> Some { Count = count; Files = files }
            | _ -> None)
