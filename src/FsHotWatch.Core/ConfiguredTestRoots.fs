/// Where the test projects `.fshw.json` names live, when that is outside the discovery
/// roots (AUTOMATION-1617).
///
/// A `tests.projects` entry is something every check BUILDS AND RUNS, wherever its
/// project sits. Before this module, one under `bench/` was in no hash: an edit to it, or
/// to a project it references, left a stored green applying to a tree whose tests were
/// never run. So the tree hash takes its roots from what is tested, not from a fixed list
/// of names: the discovery roots, plus the directory of each configured test project and
/// of every project in its `ProjectReference` closure that lies outside them.
///
/// A configured project whose location cannot be established is not dropped: it is
/// `Unlocated`, with why, and the tree hash carries it as an entry of its own and names
/// it in the coverage description.
module FsHotWatch.ConfiguredTestRoots

open System
open System.IO
open System.Text.Json
open System.Xml.Linq

/// The out-of-root directories configured test projects and their closures live in, and
/// the configured projects that could not be located.
type Located =
    {
        /// Absolute directories outside the discovery roots, inside the repository, none
        /// inside another, sorted ordinally.
        Directories: string list
        /// Configured test projects, or projects their closure references, that could not
        /// be located, each with why. Sorted by name.
        Unlocated: (string * string) list
    }

/// Each `tests.projects` entry's `project` and `args`. A config that cannot be read names
/// nothing here: the daemon refuses to start on it, so no verdict is earned under it.
let configuredTestProjects (repoRoot: string) : (string * string) list =
    let path = FsHwPaths.configFile repoRoot

    let projectsIn (doc: JsonDocument) =

        let str (el: JsonElement) (name: string) =
            match el.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""

        match doc.RootElement.TryGetProperty "tests" with
        | true, tests when tests.ValueKind = JsonValueKind.Object ->
            match tests.TryGetProperty "projects" with
            | true, projects when projects.ValueKind = JsonValueKind.Array ->
                projects.EnumerateArray()
                |> Seq.filter (fun p -> p.ValueKind = JsonValueKind.Object)
                |> Seq.map (fun p -> str p "project", str p "args")
                |> Seq.filter (fst >> String.IsNullOrWhiteSpace >> not)
                |> List.ofSeq
            | _ -> []
        | _ -> []

    let text =
        if File.Exists path then
            VerdictInputs.tryRead None (fun () -> Some(File.ReadAllText path))
        else
            None

    let parsed =
        text
        |> Option.bind (fun json ->
            try
                Some(JsonDocument.Parse json)
            with :? JsonException ->
                None)

    match parsed with
    | None -> []
    | Some doc ->
        use doc = doc
        projectsIn doc

/// The path `args` passes to `--project` (`--project p` or `--project=p`), unquoted.
let projectArg (args: string) : string option =
    let tokens =
        args.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

    tokens
    |> Array.tryFindIndex (fun t -> t = "--project" || t.StartsWith("--project=", StringComparison.Ordinal))
    |> Option.bind (fun i ->
        let t = tokens[i]

        if t = "--project" then
            Array.tryItem (i + 1) tokens
        else
            Some(t.Substring "--project=".Length))
    |> Option.map (fun p -> p.Trim('"', '\''))
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

/// The `ProjectReference` includes of `fsproj`, resolved against its directory.
let private references (fsproj: string) : Result<string list, string> =
    try
        let doc = XDocument.Load fsproj
        let dir = Path.GetDirectoryName fsproj

        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "ProjectReference")
        |> Seq.choose (fun e -> e.Attribute(XName.Get "Include") |> Option.ofObj)
        |> Seq.map (fun a -> Path.GetFullPath(Path.Combine(dir, a.Value.Replace('\\', '/'))))
        |> List.ofSeq
        |> Ok
    with ex ->
        Error $"its project file could not be read: {ex.Message}"

/// Locate the configured test projects of `repoRoot` that the discovery walk does not
/// reach, with their `ProjectReference` closures. `fsprojsUnderRoots` is every `.fsproj`
/// the walk found: a configured project named only by `project` is located there.
let locate (repoRoot: string) (fsprojsUnderRoots: string list) : Located =
    let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath repoRoot)
    let sep = string Path.DirectorySeparatorChar

    let within (dir: string) (path: string) =
        path = dir || path.StartsWith(dir + sep, StringComparison.Ordinal)

    let roots =
        Discovery.discoveryRoots root
        |> List.map (Path.GetFullPath >> Path.TrimEndingDirectorySeparator)

    let inRoots (path: string) =
        roots |> List.exists (fun r -> within r path)

    let byName =
        fsprojsUnderRoots
        |> List.map (fun f -> Path.GetFileNameWithoutExtension f, f)
        |> List.distinct

    // The configured entry points: an absolute `.fsproj`, or why there is none.
    let entry (project: string, args: string) : Result<string, string> =
        match projectArg args with
        | Some given ->
            let full = Path.GetFullPath(Path.Combine(root, given))

            if File.Exists full then
                Ok full
            elif Directory.Exists full then
                let found = Directory.GetFiles(full, "*.fsproj")

                match found.Length with
                | 1 -> Ok found[0]
                | 0 -> Error $"its --project path {given} holds no .fsproj"
                | _ -> Error $"its --project path {given} holds more than one .fsproj"
            else
                Error $"its --project path {given} does not exist"
        | None ->
            match byName |> List.filter (fun (name, _) -> name = project) with
            | [ _, one ] -> Ok one
            | [] -> Error $"its args name no --project path, and no {project}.fsproj lies under the discovery roots"
            | _ -> Error $"its args name no --project path, and {project}.fsproj is ambiguous under the roots"

    let rec visit (seen: Set<string>) (unlocated: (string * string) list) (pending: (string * string) list) =
        match pending with
        | [] -> seen, unlocated
        | (_, fsproj) :: rest when seen.Contains fsproj -> visit seen unlocated rest
        | (name, fsproj) :: rest ->
            if not (within root fsproj) then
                visit seen ((name, $"{fsproj} is outside the repository and is never followed") :: unlocated) rest
            elif not (File.Exists fsproj) then
                visit seen ((name, $"the referenced project {fsproj} does not exist") :: unlocated) rest
            else
                match references fsproj with
                | Error why -> visit (seen.Add fsproj) ((name, why) :: unlocated) rest
                | Ok refs ->
                    let next = refs |> List.map (fun r -> Path.GetFileNameWithoutExtension r, r)

                    visit (seen.Add fsproj) unlocated (next @ rest)

    let configured = configuredTestProjects root

    let entries, unlocatedEntries =
        configured
        |> List.fold
            (fun (ok, bad) (project, args) ->
                match entry (project, args) with
                | Ok fsproj -> (project, fsproj) :: ok, bad
                | Error why -> ok, (project, why) :: bad)
            ([], [])

    let projects, unlocatedRefs = visit Set.empty [] (List.rev entries)

    // A project at the repository root would make the whole repository a root; it is named
    // instead, as one that could not be located.
    let atRoot, outside =
        projects
        |> Set.toList
        |> List.filter (Path.GetDirectoryName >> inRoots >> not)
        |> List.partition (fun f -> Path.GetDirectoryName f = root)

    let outside = outside |> List.map Path.GetDirectoryName |> List.distinct

    let unlocatedRefs =
        unlocatedRefs
        @ (atRoot
           |> List.map (fun f ->
               Path.GetFileNameWithoutExtension f, $"{f} lies at the repository root, which is not walked as a root"))

    // A directory inside another is covered by the outer walk.
    let directories =
        outside
        |> List.filter (fun d -> not (outside |> List.exists (fun o -> o <> d && within o d)))
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    { Directories = directories
      Unlocated =
        unlocatedEntries @ unlocatedRefs
        |> List.distinct
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b)) }
