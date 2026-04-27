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

open System.IO

/// Parse "  ProjectName -> /path/to/ProjectName.dll" lines from dotnet build output.
/// Returns a map from project-name-stem to absolute DLL path.
/// Lines containing "error" are excluded (they are diagnostic lines, not output arrows).
let parseDllPaths (output: string) : Map<string, string> =
    output.Split('\n')
    |> Array.choose (fun line ->
        if line.Contains(" -> ") && not (line.Contains("error")) then
            let parts = line.Trim().Split(" -> ", 2, System.StringSplitOptions.None)

            if parts.Length = 2 then
                let stem = parts.[0].Trim()
                let path = parts.[1].Trim()

                if
                    stem.Length > 0
                    && path.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase)
                then
                    Some(stem, path)
                else
                    None
            else
                None
        else
            None)
    |> Map.ofArray
