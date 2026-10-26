/// The durable record of which test projects last ran WHOLE, and over which compile inputs.
///
/// A daemon's earned evidence (`EarnedEvidence`) lives in memory, so a new daemon started
/// with none: every test project ran in full on its first run, however little changed
/// ("Evidence gap: 7 project(s) have no whole-project run under the current project
/// model", once per cold daemon). What makes a whole-project run still stand for a later
/// tree is not the daemon or its model generation but the project's compile-input identity
/// (`ProjectModel.ProjectInputs`: its own compiler options folded with those of everything
/// it references, every in-repository path spelled relatively). This file records that
/// identity per project, so a later daemon can recognise a run whose inputs it still holds.
///
/// What changed in the SOURCES since that run is not this file's business: the
/// pending-verification queue and the symbol diff own it, exactly as they do for the
/// full-suite baseline beside this file. This record only answers "has this project run
/// whole over these compile inputs?".
///
/// On-disk shape: `{"projects": {"<project>": {"fsproj": "...", "identity": "..."}}}`
/// under `.fshw/test-prune/`, written atomically. A file that exists and cannot be read is
/// `Unreadable` and is treated as no record: the cost is a whole-project run, never a
/// project vouched for by a record nobody could read.
module FsHotWatch.TestPrune.WholeProjectEvidence

open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// Each project's `.fsproj` and the compile-input identity it ran whole over.
type Record = Map<string, string * string>

[<RequireQualifiedAccess>]
type LoadedRecord =
    /// The file was read in full; empty when there is none.
    | Loaded of Record
    /// The file EXISTS and could not be read.
    | Unreadable of reason: string

let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "whole-project-evidence.json")

let private entryOf (project: string) (node: JsonNode) : Result<string * (string * string), string> =
    let field (name: string) =
        match node with
        | :? JsonObject as entry ->
            match entry.[name] with
            | :? JsonValue as value ->
                match value.TryGetValue<string>() with
                | true, text when not (System.String.IsNullOrWhiteSpace text) -> Some text
                | _ -> None
            | _ -> None
        | _ -> None

    match field "fsproj", field "identity" with
    | Some fsproj, Some identity -> Ok(project, (fsproj, identity))
    | _ -> Error $"the entry for %s{project} has no `fsproj` and `identity` strings"

let load (repoRoot: string) : LoadedRecord =
    let path = sidecarPath repoRoot

    if not (File.Exists path) then
        LoadedRecord.Loaded Map.empty
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as root ->
                match root.["projects"] with
                | :? JsonObject as projects ->
                    let entries =
                        projects |> Seq.map (fun pair -> entryOf pair.Key pair.Value) |> List.ofSeq

                    match
                        entries
                        |> List.tryPick (function
                            | Error reason -> Some reason
                            | Ok _ -> None)
                    with
                    | Some reason -> LoadedRecord.Unreadable reason
                    | None ->
                        entries
                        |> List.choose (function
                            | Ok entry -> Some entry
                            | Error _ -> None)
                        |> Map.ofList
                        |> LoadedRecord.Loaded
                | _ -> LoadedRecord.Unreadable "the file has no `projects` object"
            | _ -> LoadedRecord.Unreadable "the file does not hold a JSON object"
        with ex ->
            LoadedRecord.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

let save (repoRoot: string) (record: Record) : unit =
    let projects = JsonObject()

    for KeyValue(project, (fsproj, identity)) in record do
        let entry = JsonObject()
        entry.["fsproj"] <- JsonValue.Create fsproj
        entry.["identity"] <- JsonValue.Create identity
        projects.[project] <- entry

    let root = JsonObject()
    root.["projects"] <- projects
    FsHwPaths.atomicWriteAllText (sidecarPath repoRoot) (root.ToJsonString())
