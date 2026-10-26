/// The durable record of which test projects' runtime coverage did not ingest, and why.
///
/// A run ingests each project's runtime coverage after the project ran; when that fails
/// (a malformed receipt, `SQLite Error 5: 'database is locked'`), what the project's tests
/// reach is not known for the tree it ran on, so its runtime selection cannot be trusted.
/// That is ONE project's debt: until a run of that project ingests, it runs in full. It
/// used to be recorded as unknown debt for the whole suite (every run widened to the full
/// suite until one passed every project), and the marker it left made the next daemon
/// start the same way.
///
/// The run writes the record BEFORE it tells the owner (`arm`), so a daemon that stops in
/// between still owes the project on restart. The owner rewrites it as the debt changes,
/// and removes it once nothing is owed.
///
/// On-disk shape: `{"<project>": "<reason>"}` under `.fshw/test-prune/`, written
/// atomically. A file that exists and cannot be read is `Unreadable`: which projects are
/// owed is then unknown, which only a full suite can discharge.
module FsHotWatch.TestPrune.CoverageNotIngested

open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// Each owed project, with why its runtime coverage did not ingest.
type Record = Map<string, string>

[<RequireQualifiedAccess>]
type LoadedRecord =
    /// The file was read in full; empty when there is none.
    | Loaded of Record
    /// The file EXISTS and could not be read.
    | Unreadable of reason: string

let sidecarPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-prune", "runtime-coverage-not-ingested.json")

let private reasonOf (node: JsonNode) =
    match node with
    | :? JsonValue as value ->
        match value.TryGetValue<string>() with
        | true, reason -> Some reason
        | _ -> None
    | _ -> None

/// Read the record at `path`. Never throws; a missing file is an empty record.
let loadFrom (path: string) : LoadedRecord =
    if not (File.Exists path) then
        LoadedRecord.Loaded Map.empty
    else
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as root ->
                let entries =
                    root |> Seq.map (fun pair -> pair.Key, reasonOf pair.Value) |> List.ofSeq

                match entries |> List.tryFind (snd >> Option.isNone) with
                | Some(project, _) -> LoadedRecord.Unreadable $"the entry for %s{project} is not a reason string"
                | None ->
                    entries
                    |> List.map (fun (project, reason) -> project, Option.get reason)
                    |> Map.ofList
                    |> LoadedRecord.Loaded
            | _ -> LoadedRecord.Unreadable "the file does not hold a JSON object"
        with ex ->
            LoadedRecord.Unreadable $"%s{ex.GetType().Name}: %s{ex.Message}"

let load (repoRoot: string) : LoadedRecord = loadFrom (sidecarPath repoRoot)

/// Write `record` to `path` atomically; an empty record removes the file.
let saveTo (path: string) (record: Record) : unit =
    if Map.isEmpty record then
        if File.Exists path then
            File.Delete path
    else
        let root = JsonObject()

        for KeyValue(project, reason) in record do
            root.[project] <- JsonValue.Create reason

        FsHwPaths.atomicWriteAllText path (root.ToJsonString())

let save (repoRoot: string) (record: Record) : unit = saveTo (sidecarPath repoRoot) record

/// Record `project`'s failure at `path`, keeping every project already recorded there,
/// then `notify` the owner. A record that cannot be read is left as it is: it already
/// stands for unknown debt, and rewriting it with one entry would forget the others.
let arm (notify: unit -> unit) (path: string) (project: string) (reason: string) : unit =
    match loadFrom path with
    | LoadedRecord.Loaded record -> saveTo path (Map.add project reason record)
    | LoadedRecord.Unreadable _ -> ()

    notify ()
