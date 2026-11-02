/// The reply of the analyzers plugin's `diagnostics` command, as a type: its schema id, the
/// counts it carries, the JSON it is written as, and the reading of that JSON back. The
/// `analyzer-findings` reply has its own module, `AnalyzerFindings`.
module FsHotWatch.Analyzers.AnalyzerReplies

open System.Text.Json

/// The reply schema of `diagnostics`.
[<Literal>]
let DiagnosticsSchema = "analyzers/diagnostics-v1"

/// What `diagnostics` answers: how many analyzers are loaded, how many files hold findings, and
/// how many findings there are.
type Diagnostics =
    { Analyzers: int
      Files: int
      Diagnostics: int }

module Diagnostics =
    let serialize (diagnostics: Diagnostics) : string =
        JsonSerializer.Serialize(
            {| analyzers = diagnostics.Analyzers
               files = diagnostics.Files
               diagnostics = diagnostics.Diagnostics |}
        )

    /// The counts a `diagnostics` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : Diagnostics option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match
                FsHotWatch.Json.tryInt root "analyzers",
                FsHotWatch.Json.tryInt root "files",
                FsHotWatch.Json.tryInt root "diagnostics"
            with
            | Some analyzers, Some files, Some diagnostics ->
                Some
                    { Analyzers = analyzers
                      Files = files
                      Diagnostics = diagnostics }
            | _ -> None)
