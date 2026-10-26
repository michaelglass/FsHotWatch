/// Where a repository host's session checks each of its projects: under the repository's
/// virtual root, shared with every session whose project is the same, or at its own
/// paths.
module FsHotWatch.SessionFrames

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json.Nodes
open FsHotWatch

/// Why a project is checked at its own paths, not shared with other worktrees.
type AtOwnPaths =
    /// It reads its own location: the construct, and the worktree-relative file (or
    /// path) it is in (`FrameExclusions`).
    | ReadsItsLocation of construct: string * where: string
    /// It references these projects (worktree-relative), each checked at its own paths.
    | ReferencesOwnPaths of upstreams: string list
    /// Its content is not the repository's canonical content for it, which these
    /// worktrees check under the virtual root (`CanonicalProjects`).
    | NotCanonical of heldBy: string list

module AtOwnPaths =
    /// For the log and `status --repository`: why, in a clause.
    let describe (reason: AtOwnPaths) : string =
        match reason with
        | ReadsItsLocation(construct, where) -> $"it uses %s{construct} in %s{where}"
        | ReferencesOwnPaths upstreams ->
            let listed = String.Join(", ", upstreams)
            $"references %s{listed}, checked at their own paths"
        | NotCanonical heldBy ->
            let holders =
                match heldBy with
                | [] -> "another worktree"
                | roots -> String.Join(", ", roots)

            $"its content differs from what %s{holders} checks under the virtual root"

    let private strings (values: string list) =
        JsonArray(values |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> Array.ofList)

    /// One `status --repository` entry: `{project, reason, ...what it names}`.
    let toJson (project: string) (reason: AtOwnPaths) : JsonObject =
        let node = JsonObject()
        node["project"] <- JsonValue.Create project

        match reason with
        | ReadsItsLocation(construct, where) ->
            node["reason"] <- JsonValue.Create "reads-its-location"
            node["construct"] <- JsonValue.Create construct
            node["where"] <- JsonValue.Create where
        | ReferencesOwnPaths upstreams ->
            node["reason"] <- JsonValue.Create "references-own-paths"
            node["upstreams"] <- strings upstreams
        | NotCanonical heldBy ->
            node["reason"] <- JsonValue.Create "not-canonical"
            node["heldBy"] <- strings heldBy

        node

    /// `toJson`'s entry read back: `None` for one it does not describe.
    let ofJson (entry: JsonNode) : (string * AtOwnPaths) option =
        let text (name: string) =
            match entry[name] with
            | null -> ""
            | value -> value.GetValue<string>()

        let texts (name: string) =
            match entry[name] with
            | null -> []
            | values -> values.AsArray() |> Seq.map (fun v -> v.GetValue<string>()) |> List.ofSeq

        match text "reason" with
        | "reads-its-location" -> Some(text "project", ReadsItsLocation(text "construct", text "where"))
        | "references-own-paths" -> Some(text "project", ReferencesOwnPaths(texts "upstreams"))
        | "not-canonical" -> Some(text "project", NotCanonical(texts "heldBy"))
        | _ -> None

/// One session's projects checked at their own paths, and why, as last decided: a
/// project that comes to be shared leaves it.
type Report() =
    let projects = ConcurrentDictionary<string, AtOwnPaths>(StringComparer.Ordinal)

    /// The latest decision for `project`: `None` when it is shared.
    member _.Record(project: string, decision: AtOwnPaths option) : unit =
        match decision with
        | Some reason -> projects[project] <- reason
        | None -> projects.TryRemove project |> ignore

    /// Every project at its own paths, by project.
    member _.Current: (string * AtOwnPaths) list =
        projects |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.sortBy fst |> List.ofSeq

    member this.ToJson() : JsonArray =
        JsonArray(
            this.Current
            |> List.map (fun (project, reason) -> AtOwnPaths.toJson project reason :> JsonNode)
            |> Array.ofList
        )

/// A repository host's reports, one per attached worktree root.
type Reports() =
    let reports = ConcurrentDictionary<string, Report>(StringComparer.Ordinal)

    /// A new, empty report for a session starting at `root`, replacing any before it.
    member _.Start(root: string) : Report =
        let report = Report()
        reports[root] <- report
        report

    /// The report of the session at `root`, made on first use.
    member _.For(root: string) : Report =
        reports.GetOrAdd(root, fun _ -> Report())

    /// The session at `root` whose report is `report` has ended. A later session's
    /// report at the same root stays.
    member _.End(root: string, report: Report) : unit =
        reports.TryRemove(Collections.Generic.KeyValuePair(root, report)) |> ignore

    /// `{root: [entry, ...]}`, by root: `status --repository`'s `ownPaths`.
    member _.ToJson() : JsonObject =
        let node = JsonObject()

        for KeyValue(root, report) in reports.ToArray() |> Array.sortBy (fun kv -> kv.Key) do
            node[root] <- report.ToJson()

        node

/// The frame choice for the session of the worktree at `realRoot`.
///
/// A project is checked under `virtualRoot` when nothing in it reads its own location
/// (`FrameExclusions`), every project it references is shared too, and its content is
/// the repository's canonical content for it (`CanonicalProjects`). Otherwise it is
/// checked where it is: `report` holds why, and the log says so once per project and
/// reason.
let choice
    (registry: CanonicalProjects.Registry)
    (report: Report)
    (realRoot: string)
    (virtualRoot: string)
    (log: string -> unit)
    : PathFrame.FrameChoice =
    let frame = PathFrame.create realRoot virtualRoot

    let exclusions =
        ConcurrentDictionary<string * string, FrameExclusions.Exclusion option>()

    let reported = ConcurrentDictionary<string, unit>()

    // Worktree-relative, whichever worktree: a path outside it reads `../`.
    let relative (path: string) =
        Path.GetRelativePath(realRoot, path).Replace('\\', '/')

    fun options closure upstreamsAtOwnPaths ->
        let project = relative options.ProjectFileName

        let atOwnPaths () =
            match upstreamsAtOwnPaths with
            | _ :: _ -> Some(ReferencesOwnPaths(upstreamsAtOwnPaths |> List.map relative))
            | [] ->
                // Scanned again only when the project's content changes: the closure hashes it.
                match
                    exclusions.GetOrAdd(
                        (project, closure),
                        fun _ -> FrameExclusions.exclusionFor File.ReadAllText options
                    )
                with
                | Some excluded -> Some(ReadsItsLocation(excluded.Construct, relative excluded.Where))
                | None when registry.Claim(project, closure, realRoot) -> None
                | None -> Some(NotCanonical(registry.HoldersOf project))

        let decision = atOwnPaths ()
        report.Record(project, decision)

        match decision with
        | Some reason ->
            let said = AtOwnPaths.describe reason

            if reported.TryAdd($"%s{project}|%s{said}", ()) then
                log $"%s{project} is checked at its own paths, not shared with other worktrees: %s{said}"

            None
        | None -> Some frame
