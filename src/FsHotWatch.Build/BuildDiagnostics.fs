module FsHotWatch.Build.BuildDiagnostics

open System.Text.RegularExpressions
open FsHotWatch.ErrorLedger

let private diagnosticPattern =
    Regex(@"^(.+?)\((\d+),(\d+)\):\s+(warning|error)\s+(.+)$", RegexOptions.Multiline)

let parseMSBuildDiagnostics (output: string) : ErrorEntry list =
    [ for m in diagnosticPattern.Matches(output) do
          let severity =
              match m.Groups.[4].Value with
              | "error" -> DiagnosticSeverity.Error
              | _ -> DiagnosticSeverity.Warning

          { Message = m.Groups.[5].Value
            Severity = severity
            Line = int m.Groups.[2].Value
            Column = int m.Groups.[3].Value
            Detail = None } ]
