/// The content key under which a test project's green may be reused in another checkout.
///
/// A test outcome is a function of the binaries it runs, the files it reads at run time,
/// its environment and the world outside. `K_P` names every in-repository input to those
/// and leaves the rest to a TTL and to `confirm`, which never reads evidence. Each input
/// is a separately labelled digest, so a miss can say WHICH input moved.
///
/// Output DLL bytes are deliberately not an input: without a `PathMap` they embed the
/// checkout path, so a key over them would differ in every workspace and never hit. The
/// sources, project options, resolved packages and toolchain that produce those bytes are
/// named instead.
///
/// Every input that cannot be named makes NO key (`Error`), never a key with a hole in
/// it: two checkouts that each failed to read the same input must not agree.
module FsHotWatch.TestEvidenceKey

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json

/// Bumped whenever WHAT is hashed or HOW changes, so an old entry can never match.
[<Literal>]
let Version = "test-evidence-v1"

/// How far a test project's runtime reads are assumed to reach.
[<RequireQualifiedAccess>]
type Reads =
    /// The default: the project may read any file of the verdict tree, so every one of
    /// them is in its key.
    | VerdictTree
    /// Opt-in narrowing: the project reads nothing under the directory of a project
    /// outside its closure. Files under no project directory stay in the key.
    | Closure

/// A test project's `evidence` block in `.fshw.json`.
type Config =
    {
        Reads: Reads
        /// Extra runtime reads (repo-relative paths or globs), each hashed into the key.
        Extra: string list
        /// Files the tests read for their environment (`.env.test`). Only their content
        /// HASH enters the key or the store, never their content.
        EnvFiles: string list
    }

let defaultConfig =
    { Reads = Reads.VerdictTree
      Extra = []
      EnvFiles = [] }

let private readsToken =
    function
    | Reads.VerdictTree -> "verdict-tree"
    | Reads.Closure -> "closure"

/// Parse a tests entry's optional `evidence` element. An absent element is the default;
/// a present one that cannot be honoured as written is an error, never a silent default.
let parseConfig (element: JsonElement option) : Result<Config, string> =
    let stringList (el: JsonElement) (name: string) =
        match Json.tryProp el name with
        | None -> Ok []
        | Some arr when arr.ValueKind = JsonValueKind.Array ->
            let items = arr.EnumerateArray() |> Seq.toList

            if
                items
                |> List.forall (fun i ->
                    i.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(i.GetString())))
            then
                Ok(items |> List.map (fun i -> i.GetString().Replace('\\', '/')))
            else
                Error $"evidence.%s{name} must be an array of non-empty strings"
        | Some _ -> Error $"evidence.%s{name} must be an array of non-empty strings"

    let escapes (path: string) =
        Path.IsPathRooted path || path.Split('/') |> Array.contains ".."

    match element with
    | None -> Ok defaultConfig
    | Some el when el.ValueKind <> JsonValueKind.Object -> Error "evidence must be an object"
    | Some el ->
        let reads =
            match Json.tryProp el "reads" with
            | None -> Ok Reads.VerdictTree
            | Some r when r.ValueKind = JsonValueKind.String && r.GetString() = "verdict-tree" -> Ok Reads.VerdictTree
            | Some r when r.ValueKind = JsonValueKind.String && r.GetString() = "closure" -> Ok Reads.Closure
            | Some _ -> Error "evidence.reads must be \"verdict-tree\" or \"closure\""

        match reads, stringList el "extra", stringList el "envFiles" with
        | Ok reads, Ok extra, Ok envFiles ->
            match (extra @ envFiles) |> List.filter escapes with
            | [] ->
                Ok
                    { Reads = reads
                      Extra = extra
                      EnvFiles = envFiles }
            | bad -> Error $"""evidence paths must stay inside the repository: %s{String.concat ", " bad}"""
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e

/// Every `tests.projects[]` entry's `evidence` block in `repoRoot`'s `.fshw.json`, by
/// project name. A project without one takes `defaultConfig`; a block that cannot be
/// honoured is an `Error` naming the project, so a config load can refuse it loudly.
let configsOf (repoRoot: string) : Result<Map<string, Config>, string> =
    let path = FsHwPaths.configFile repoRoot

    if not (File.Exists path) then
        Ok Map.empty
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText path)

            let projects =
                Json.tryProp doc.RootElement "tests"
                |> Option.bind (fun t -> Json.tryProp t "projects")
                |> Option.filter (fun p -> p.ValueKind = JsonValueKind.Array)
                |> Option.map (fun p -> p.EnumerateArray() |> Seq.toList)
                |> Option.defaultValue []

            projects
            |> List.choose (fun p ->
                Json.tryString p "project"
                |> Option.map (fun name ->
                    name,
                    parseConfig (Json.tryProp p "evidence" |> Option.map (fun e -> e.Clone()))
                    |> Result.mapError (fun e -> $"tests.projects[%s{name}].%s{e}")))
            |> List.fold
                (fun acc (name, parsed) ->
                    match acc, parsed with
                    | Ok map, Ok config -> Ok(Map.add name config map)
                    | Error e, _
                    | _, Error e -> Error e)
                (Ok Map.empty)
        with
        | :? JsonException
        | :? IOException
        | :? UnauthorizedAccessException as ex -> Error $"%s{path}: %s{ex.Message}"

/// Every input of one test project's key, as named entries. Paths are repo-relative with
/// `/` separators, so two checkouts holding the same bytes produce the same inputs.
type Inputs =
    {
        /// The SDK, the checker build and the platform (see `toolchainOf`).
        Toolchain: string
        /// The canonical JSON of the project's `tests` entry in `.fshw.json`.
        TestConfig: string
        /// (project, relativised options hash) for each project in the closure.
        Options: (string * string) list
        /// (project, digest of its resolved package list) for each closure project.
        Packages: (string * string) list
        /// (path, content hash) of every file under a closure project's directory.
        Closure: (string * string) list
        /// (path, content hash) of every verdict-tree file under NO project directory.
        Shared: (string * string) list
        /// (path, content hash) of every verdict-tree file under a project directory
        /// outside the closure. Empty under `Reads.Closure`.
        OtherProjects: (string * string) list
        /// (path, content hash) of the declared extra runtime reads.
        Declared: (string * string) list
        /// (path, content hash) of the declared env files.
        EnvFiles: (string * string) list
        /// What a hit asserts was run, and under which reads assumption.
        Scope: string
    }

/// A key: the value an entry is stored under, and the labelled digests it was folded
/// from, so a miss can name the input that moved.
type Key =
    { Value: string
      Labels: (string * string) list }

let private digestEntries (entries: (string * string) list) =
    entries
    |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
    |> TreeHash.hashEntries

/// The labelled digests, in a fixed order.
let labels (inputs: Inputs) : (string * string) list =
    [ "toolchain", "sha256:" + ContentHash.ofText inputs.Toolchain
      "testConfig", "sha256:" + ContentHash.ofText inputs.TestConfig
      "options", digestEntries inputs.Options
      "packages", digestEntries inputs.Packages
      "closure", digestEntries inputs.Closure
      "shared", digestEntries inputs.Shared
      "otherProjects", digestEntries inputs.OtherProjects
      "declared", digestEntries inputs.Declared
      "envFiles", digestEntries inputs.EnvFiles
      "scope", "sha256:" + ContentHash.ofText inputs.Scope ]

/// Fold labelled digests into a key value.
let ofLabels (labels: (string * string) list) : Key =
    let body =
        labels
        |> List.map (fun (label, digest) -> label + "\000" + digest)
        |> String.concat "\n"

    { Value = ContentHash.ofText (Version + "\n" + body)
      Labels = labels }

let compute (inputs: Inputs) : Key = ofLabels (labels inputs)

/// The labels whose digest differs between a stored key and the current one, in the
/// current key's order. A label present on one side only counts as moved.
let moved (stored: (string * string) list) (current: (string * string) list) : string list =
    let storedMap = Map.ofList stored
    let currentMap = Map.ofList current

    let fromCurrent =
        current
        |> List.filter (fun (label, digest) -> Map.tryFind label storedMap <> Some digest)
        |> List.map fst

    let onlyStored =
        stored
        |> List.map fst
        |> List.filter (fun label -> not (Map.containsKey label currentMap))

    fromCurrent @ onlyStored

/// The toolchain half of every key: the SDK `dotnet` resolves here, the checker build
/// (FCS, FSharp.Core and fshw module version ids), the platform, and the values of the
/// environment variables that can change what MSBuild produces.
let toolchainOf (repoRoot: string) : string =
    let msbuildEnvironment =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<System.Collections.DictionaryEntry>
        |> Seq.map (fun e -> string e.Key, string e.Value)
        |> Seq.filter (fun (name, _) ->
            SessionScope.isMsbuildRelevant name
            && not (
                SessionScope.processOwned
                |> List.exists (fun o -> String.Equals(o, name, StringComparison.OrdinalIgnoreCase))
            ))
        |> Seq.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> Seq.map (fun (name, value) -> $"%s{name}=%s{value}")
        |> Seq.toList

    [ DotnetToolchain.identity repoRoot
      CheckCache.toolchainIdentity
      $"os=%s{RuntimeInformation.OSDescription}"
      $"arch=%O{RuntimeInformation.OSArchitecture}"
      $"runtime=%s{RuntimeInformation.FrameworkDescription}"
      yield! msbuildEnvironment ]
    |> String.concat "\n"

/// The resolved package list of a restored project: each library's `id/version` and its
/// `sha512`, from `obj/project.assets.json`. Floating versions resolve per restore, so the
/// `.fsproj` alone does not name what was compiled against. The file itself is not hashed:
/// it embeds absolute paths. `None` when the file is absent or unreadable.
let resolvedPackages (projectPath: string) : string option =
    let assets =
        Path.Combine(Path.GetDirectoryName projectPath, "obj", "project.assets.json")

    try
        if not (File.Exists assets) then
            None
        else
            use doc = JsonDocument.Parse(File.ReadAllText assets)

            match Json.tryProp doc.RootElement "libraries" with
            | Some libraries when libraries.ValueKind = JsonValueKind.Object ->
                libraries.EnumerateObject()
                |> Seq.map (fun lib ->
                    let sha = Json.tryString lib.Value "sha512" |> Option.defaultValue "-"
                    let kind = Json.tryString lib.Value "type" |> Option.defaultValue "-"
                    $"%s{lib.Name}\000%s{kind}\000%s{sha}")
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> String.concat "\n"
                |> ContentHash.ofText
                |> Some
            | _ -> None
    with
    | :? IOException
    | :? UnauthorizedAccessException
    | :? JsonException -> None

/// The content-hashed verdict tree, walked and hashed ONCE per check and partitioned per
/// test project by `inputsFor`.
type Snapshot =
    {
        RepoRoot: string
        /// (repo-relative path, content hash) of every verdict-tree entry, including the
        /// sentinel entries for holes and absent declarations.
        Files: (string * string) list
        /// Repo-relative directories holding a project file in the verdict tree.
        ProjectDirs: string list
        /// Bytes read to hash `Files`.
        BytesHashed: int64
    }

let private relative (repoRoot: string) (abs: string) =
    Path.GetRelativePath(repoRoot, abs).Replace('\\', '/')

let private isProjectFile (rel: string) =
    rel.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
    || rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
    || rel.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)

let private directoryOf (rel: string) =
    match rel.LastIndexOf '/' with
    | -1 -> "."
    | i -> rel.Substring(0, i)

/// Is `rel` under the repo-relative directory `dir`? `dir` is a project directory the
/// verdict walk found, so never the repository root itself.
let private isUnder (dir: string) (rel: string) =
    rel.StartsWith(dir + "/", StringComparison.Ordinal)

let private fileSize (abs: string) =
    try
        FileInfo(abs).Length
    with _ ->
        0L

/// Repository files a test run can depend on that the verdict tree does not hold: the
/// local tool manifest pins tools a test may invoke. Each is a shared input, and an
/// absent one is an entry of its own.
let EvidenceKnownInputs = [ ".config/dotnet-tools.json" ]

/// Walk and hash the verdict tree of `repoRoot` (the same set `TreeHash` addresses),
/// plus `EvidenceKnownInputs`.
let snapshot (repoRoot: string) (excludePatterns: string list) : Snapshot =
    let root = Path.GetFullPath repoRoot
    let walked = TreeHash.files root excludePatterns
    let inTree = walked.Files |> List.map fst |> Set.ofList

    let known =
        EvidenceKnownInputs
        |> List.filter (inTree.Contains >> not)
        |> List.map (fun rel ->
            let abs = Path.Combine(root, rel)

            rel,
            (if File.Exists abs then
                 ContentHash.ofFile abs
             else
                 VerdictInputs.AbsentDeclaration))

    let files =
        (walked.Files |> List.map (fun (rel, abs) -> rel, ContentHash.ofFile abs))
        @ (walked.Skipped |> List.map (fun rel -> rel + "/", ContentHash.UnhashableContent))
        @ (walked.AbsentDeclarations
           |> List.map (fun rel -> VerdictInputs.SentinelPrefix + rel, VerdictInputs.AbsentDeclaration))
        @ known

    { RepoRoot = root
      Files = files
      ProjectDirs =
        walked.Files
        |> List.map fst
        |> List.filter isProjectFile
        |> List.map directoryOf
        |> List.distinct
      BytesHashed = walked.Files |> List.sumBy (snd >> fileSize) }

/// One test project's request against a snapshot.
[<NoComparison; NoEquality>]
type Request =
    {
        /// The test project's file, absolute.
        Project: string
        /// The test project and every project it references transitively, absolute.
        Closure: string list
        Config: Config
        /// The canonical JSON of the project's `tests` entry.
        TestConfig: string
        Toolchain: string
        /// The relativised options hash of a project, when the model knows it.
        OptionsHash: string -> string option
    }

/// The items a project file names by a literal path outside its own directory —
/// `<Compile Include="../Shared/Util.fs" />` and the like — absolute, and whether it names
/// any it cannot resolve (a glob or an MSBuild property). `Error` when the file cannot be
/// read as XML.
let linkedItems (projectFile: string) : Result<string list * bool, string> =
    let dir = Path.GetDirectoryName projectFile

    try
        let doc = System.Xml.Linq.XDocument.Load projectFile

        let includes =
            doc.Descendants()
            |> Seq.filter (fun e ->
                match e.Name.LocalName with
                | "Compile"
                | "Content"
                | "None"
                | "EmbeddedResource" -> true
                | _ -> false)
            |> Seq.collect (fun e ->
                [ e.Attribute(System.Xml.Linq.XName.Get "Include")
                  e.Attribute(System.Xml.Linq.XName.Get "Update") ]
                |> List.choose Option.ofObj
                |> List.collect (fun a -> a.Value.Split(';') |> Array.toList))
            |> Seq.map (fun p -> p.Trim().Replace('\\', '/'))
            |> Seq.filter (fun p -> p <> "")
            |> Seq.toList

        let unresolvable (p: string) =
            p.Contains "$(" || p.Contains "*" || p.Contains "?"

        let outside (p: string) =
            Path.IsPathRooted p || p.StartsWith("../", StringComparison.Ordinal)

        Ok(
            includes
            |> List.filter (fun p -> not (unresolvable p) && outside p)
            |> List.map (fun p -> Path.GetFullPath(Path.Combine(dir, p)))
            |> List.distinct,
            includes |> List.exists unresolvable
        )
    with
    | :? IOException
    | :? UnauthorizedAccessException
    | :? System.Xml.XmlException as ex -> Error $"%s{projectFile}: %s{ex.Message}"

/// The key inputs of one test project, and the bytes hashed beyond the snapshot. `Error`
/// names the input that could not be named: such a project gets no key at all.
///
/// That includes an input that could not be READ: a file the hasher could not open, or a
/// directory a walk could not enter, would otherwise contribute one shared constant, and
/// two checkouts holding different unreadable bytes would agree on a key.
let inputsFor (snap: Snapshot) (request: Request) : Result<Inputs * int64, string> =
    let root = snap.RepoRoot
    let rel = relative root

    let closure =
        request.Project :: request.Closure |> List.map Path.GetFullPath |> List.distinct

    let closureDirs = closure |> List.map (rel >> directoryOf) |> List.distinct

    let escaping =
        closureDirs
        |> List.filter (fun d -> d = ".." || d.StartsWith("../", StringComparison.Ordinal))

    let options = closure |> List.map (fun p -> rel p, request.OptionsHash p)

    match escaping, options |> List.filter (snd >> Option.isNone) with
    | _ :: _, _ -> Error $"""closure project outside the repository: %s{String.concat ", " escaping}"""
    | [], (unknown, _) :: _ -> Error $"project options unknown for %s{unknown}"
    | [], [] ->
        // Every file under a closure project's directory, walked directly rather than read
        // off the verdict tree: a project outside `src/`+`tests/` is still fully keyed.
        let mutable extraBytes = 0L

        let closureFiles =
            closureDirs
            |> List.collect (fun dir ->
                let abs = if dir = "." then root else Path.Combine(root, dir)
                let walk = SafeWalk.walk SafeWalk.SourceExcludedDirs "*" abs

                (walk.Files
                 |> List.map (fun f ->
                     extraBytes <- extraBytes + f.Length
                     rel f.FullName, ContentHash.ofFile f.FullName))
                @ (walk.Skipped
                   |> List.map (fun s -> rel s.Path + "/", ContentHash.UnhashableContent)))
            |> List.distinctBy fst

        // Items a closure project names outside the closure's directories (a linked
        // source) are inputs too. Under `reads: closure` a glob or property the key cannot
        // resolve makes no key: it might name a file outside every hashed directory.
        let linked =
            closure
            |> List.map (fun p -> linkedItems p)
            |> List.fold
                (fun acc next ->
                    match acc, next with
                    | Ok(files, unresolved), Ok(more, alsoUnresolved) -> Ok(files @ more, unresolved || alsoUnresolved)
                    | (Error _ as e), _
                    | _, (Error _ as e) -> e)
                (Ok([], false))

        let linkedFiles =
            match linked with
            | Ok(files, _) ->
                files
                |> List.map (fun abs -> rel abs, abs)
                |> List.filter (fun (r, _) ->
                    not (
                        closureDirs
                        |> List.exists (fun d ->
                            if d = "." then
                                not (r.StartsWith("../", StringComparison.Ordinal))
                            else
                                r.StartsWith(d + "/", StringComparison.Ordinal))
                    ))
                |> List.map (fun (r, abs) ->
                    extraBytes <- extraBytes + fileSize abs

                    r,
                    (if File.Exists abs then
                         ContentHash.ofFile abs
                     else
                         VerdictInputs.AbsentDeclaration))
            | Error _ -> []

        let closureFiles = closureFiles @ linkedFiles |> List.distinctBy fst
        let inClosure = closureFiles |> List.map fst |> Set.ofList

        let underAnyProject (path: string) =
            snap.ProjectDirs |> List.exists (fun d -> isUnder d path)

        let notClosure =
            snap.Files |> List.filter (fun (p, _) -> not (inClosure.Contains p))

        let shared, others =
            notClosure
            |> List.partition (fun (p, _) ->
                p.StartsWith(VerdictInputs.SentinelPrefix, StringComparison.Ordinal)
                || not (underAnyProject p))

        let hashListed (paths: string list) =
            paths
            |> List.map (fun p ->
                let abs = Path.Combine(root, p)
                extraBytes <- extraBytes + fileSize abs

                p,
                (if File.Exists abs then
                     ContentHash.ofFile abs
                 else
                     VerdictInputs.AbsentDeclaration))

        let declared =
            if List.isEmpty request.Config.Extra then
                []
            else
                let resolution =
                    VerdictInputs.resolve
                        root
                        { VerdictInputs.empty with
                            Hashed =
                                request.Config.Extra
                                |> List.map (fun p ->
                                    { VerdictInputs.Declared.Path = p
                                      VerdictInputs.Declared.Why = "evidence.extra" }) }

                (resolution.Files |> List.map rel |> hashListed)
                @ (resolution.Absent
                   |> List.map (fun p -> VerdictInputs.SentinelPrefix + p, VerdictInputs.AbsentDeclaration))

        let inputs =
            { Toolchain = request.Toolchain
              TestConfig = request.TestConfig
              Options = options |> List.map (fun (p, h) -> p, Option.get h)
              Packages =
                closure
                |> List.map (fun p -> rel p, resolvedPackages p |> Option.defaultValue "absent")
              Closure = closureFiles
              Shared = shared
              OtherProjects =
                match request.Config.Reads with
                | Reads.VerdictTree -> others
                | Reads.Closure -> []
              Declared = declared
              EnvFiles = hashListed request.Config.EnvFiles
              Scope = $"full-project;reads=%s{readsToken request.Config.Reads}" }

        let unreadable =
            [ inputs.Closure
              inputs.Shared
              inputs.OtherProjects
              inputs.Declared
              inputs.EnvFiles ]
            |> List.concat
            |> List.filter (snd >> ContentHash.isReadable >> not)
            |> List.map fst

        match linked, unreadable with
        | Error reason, _ -> Error $"a closure project file cannot be read: %s{reason}"
        | Ok(_, true), _ when request.Config.Reads = Reads.Closure ->
            Error
                "a closure project names an item by a glob or MSBuild property, which reads: closure cannot resolve — declare what it reads in evidence.extra, or use the default reads"
        | Ok _, path :: _ -> Error $"an input could not be read: %s{path}"
        | Ok _, [] ->
            match
                linkedFiles
                |> List.filter (fun (r, _) -> r.StartsWith("../", StringComparison.Ordinal))
            with
            | (outside, _) :: _ -> Error $"a closure project names a file outside the repository: %s{outside}"
            | [] -> Ok(inputs, extraBytes)
