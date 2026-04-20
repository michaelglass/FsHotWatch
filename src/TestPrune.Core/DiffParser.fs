module TestPrune.DiffParser

open System.Text.RegularExpressions

let private diffHeaderPattern =
    Regex(@"^diff --git a/.+ b/(.+)$", RegexOptions.Multiline)

let private codeExtensions = set [ ".fs"; ".fsx"; ".fsproj" ]

let private isCodeFile (path: string) =
    codeExtensions
    |> Set.exists (fun ext -> path.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))

let isFsproj (path: string) =
    path.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)

/// Parse unified diff text (from jj diff --git or git diff) to extract changed file paths.
/// Only returns F# code files (.fs, .fsx, .fsproj).
let parseChangedFiles (diffText: string) : string list =
    diffHeaderPattern.Matches(diffText)
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Seq.filter isCodeFile
    |> Seq.distinct
    |> Seq.toList

/// Returns true if any .fsproj file changed (triggers conservative fallback).
let hasFsprojChanges (changedFiles: string list) : bool = changedFiles |> List.exists isFsproj
