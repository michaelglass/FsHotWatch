/// The weave manifest: `manifest.tsv` (one row per probe id) and `documents.tsv` (each
/// source document's PDB-recorded hash), tab-separated with no header.
module TestPrune.Trace.Manifest

open System
open System.IO
open TestPrune.Trace.Model

let private kindCode =
    function
    | UserMethod -> "user"
    | GeneratedMethod -> "gen"
    | StaticCtor -> "cctor"
    | UnionCase -> "case"
    | TypeUse -> "type"

let private parseKind =
    function
    | "user" -> Ok UserMethod
    | "gen" -> Ok GeneratedMethod
    | "cctor" -> Ok StaticCtor
    | "case" -> Ok UnionCase
    | "type" -> Ok TypeUse
    | other -> Error $"unknown probe kind '%s{other}'"

let private field (s: string) =
    if s.IndexOfAny [| '\t'; '\n'; '\r' |] >= 0 then
        invalidArg "manifest" $"manifest field contains a tab or newline: %s{s}"

    s

/// Write `manifest.tsv` and `documents.tsv` into `dir`, creating it. Raises
/// ArgumentException when a field contains a tab or a newline.
let write (dir: string) (m: Manifest) =
    Directory.CreateDirectory dir |> ignore

    // Seq.map rather than Array.map, which FSharp.Core inlines with a null check on the
    // array; both are materialized before either file is touched, so a refused field
    // leaves no partial manifest behind.
    let rows =
        m.Rows
        |> Seq.map (fun r ->
            String.Join(
                "\t",
                string r.Id,
                kindCode r.Kind,
                field r.Assembly,
                field r.TypeName,
                field r.Member,
                field (defaultArg r.Document ""),
                string r.FirstLine,
                string r.LastLine
            ))
        |> Seq.toArray

    let docs =
        m.Documents
        |> Seq.map (fun (KeyValue(p, h)) -> field p + "\t" + field h)
        |> Seq.toArray

    File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), rows)
    File.WriteAllLines(Path.Combine(dir, "documents.tsv"), docs)

/// Read a manifest written by `write`.
let read (dir: string) : Result<Manifest, string> =
    try
        let rows =
            File.ReadAllLines(Path.Combine(dir, "manifest.tsv"))
            |> Seq.map (fun line ->
                let c = line.Split '\t'

                match parseKind c.[1] with
                | Error e -> failwith e
                | Ok kind ->
                    { Id = int c.[0]
                      Kind = kind
                      Assembly = c.[2]
                      TypeName = c.[3]
                      Member = c.[4]
                      Document = if c.[5] = "" then None else Some c.[5]
                      FirstLine = int c.[6]
                      LastLine = int c.[7] })
            |> Seq.toArray

        let docs =
            File.ReadAllLines(Path.Combine(dir, "documents.tsv"))
            |> Seq.map (fun l ->
                let c = l.Split '\t'
                c.[0], c.[1])
            |> Map.ofSeq

        Ok
            { Rows = rows
              Documents = docs
              IdCount = rows.Length }
    with ex ->
        Error ex.Message
