/// The persisted project options (ADR-059 §18, AUTOMATION-1372): a daemon that starts again
/// over the same bytes takes its projects' evaluated options from the one outcome store,
/// instead of paying MSBuild's evaluation and design-time build (4–10 s per restart on the
/// 1361 spike's measurements).
///
/// TWO KINDS, both filed in the store `check` records live in:
///   * `options-inputs`, keyed by the REQUEST — the checkout root, the project files asked
///     for, the toolchain, the SDK MSBuild runs from and the global properties. Its record
///     names what the last evaluation of that request read (`EvaluationInputs`), so a
///     later process knows which bytes to look at before it has evaluated anything.
///   * `options`, keyed by the request AND the current state of every one of those inputs:
///     each file's bytes, each probed path's kind, each directory listing, and each
///     environment variable the evaluation can read. Its record holds the options.
/// The first is only an index: a wrong or missing one is a miss. The second is found only
/// by a key folded from the bytes on disk now, so it is replayed only when every input it
/// was evaluated from is byte-identical.
///
/// A STALE REPLAY IS FAR WORSE THAN A MISS, so the filer refuses whenever it cannot be sure
/// what it files is what those inputs produce:
///   * an evaluation in which any project failed to load is not filed;
///   * an evaluation is filed only when no input changed from shortly before it started to
///     the moment its inputs were hashed (the status-change time, which no copy can set
///     back; `FileStamp`). A path that is absent is judged by its directory.
/// What the key does NOT cover is listed in ADR-059 §18: chiefly the files the design-time
/// targets read after evaluation, other than the package assets (which the key names).
///
/// The options a record holds are Ionide's `ProjectOptions` without `AllProperties` and
/// `AllItems`, the evaluation's full property and item dumps, which fshw never reads and
/// which would make a record many times larger. A replay carries them empty.
module FsHotWatch.OptionsRecords

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ionide.ProjInfo

/// The kind of the record that names what an evaluation read.
[<Literal>]
let InputsKind = "options-inputs"

/// The format of an `options` record's outputs. Any other is read as a miss.
[<Literal>]
let OutputsSchema = "fshw-options-outputs-v1"

/// The format of an `options-inputs` record's outputs. Any other is read as a miss.
[<Literal>]
let InputsSchema = "fshw-options-inputs-v1"

/// A directory listing the evaluation asked for: the entries `Directory.Enumerate*` gives
/// for (`Directory`, `Pattern`, `Recursive`), of `Kind` "files", "directories" or "entries".
type Listing =
    { Kind: string
      Directory: string
      Pattern: string
      Recursive: bool }

/// What one evaluation of some projects read, found by evaluating them
/// (`OptionsInputs.list`).
type EvaluationInputs =
    {
        /// Files whose BYTES the evaluation used: the project files, every import, each
        /// project's package assets, and anything MSBuild opened.
        Reads: string list
        /// Paths whose existence or kind the evaluation asked about — an `Exists`
        /// condition, an import it skipped, a file it looked for above the project.
        Probes: string list
        /// Directory listings: wildcard imports and item globs.
        Listings: Listing list
        /// Variables the evaluation can read as properties (`$(Name)`), in any case.
        EnvNames: string list
    }

/// One evaluation request, before anything is read.
type Request =
    {
        /// The checkout. In the key, so options holding this checkout's paths are never
        /// replayed in another.
        Root: string
        /// The project files asked for.
        Projects: string list
        /// The fshw and Ionide builds evaluating them.
        Toolchain: string
        /// The SDK whose MSBuild evaluates them: its resolved location, which names its
        /// version and what global.json resolved to.
        Sdk: string
        /// The global properties the evaluation runs with.
        GlobalProperties: (string * string) list
    }

/// Variables read by SDK resolvers and tasks directly rather than as properties: every one
/// set whose name starts with one of these is keyed.
let envPrefixes = [ "DOTNET_"; "MSBUILD"; "NUGET_" ]

let private ordinal (a: string) (b: string) = String.CompareOrdinal(a, b)

let private digestOf (lines: string seq) =
    lines |> Seq.sortWith ordinal |> String.concat "\n" |> ContentHash.ofText

/// A path's state as a key reads it: a file's bytes, a directory, or nothing there.
let pathState (path: string) : string =
    if File.Exists path then "file:" + ContentHash.ofFile path
    elif Directory.Exists path then "directory"
    else "absent"

/// A listing's state as a key reads it: the entries' names, relative to the listed
/// directory, or why there are none to name. A RECURSIVE listing is never read here: an
/// evaluation that asked for one is not filed (`file`), because a walk that follows what
/// MSBuild's follows (hidden directories, directory symlinks) need not terminate, and one
/// that does not (`SafeWalk`) would key a different listing.
let listingState (listing: Listing) : string =
    try
        if listing.Recursive then
            "recursive"
        elif not (Directory.Exists listing.Directory) then
            "absent"
        else
            let entries =
                match listing.Kind with
                | "directories" -> Directory.EnumerateDirectories(listing.Directory, listing.Pattern)
                | "entries" -> Directory.EnumerateFileSystemEntries(listing.Directory, listing.Pattern)
                | _ -> Directory.EnumerateFiles(listing.Directory, listing.Pattern)

            "entries:"
            + digestOf (entries |> Seq.map (fun entry -> Path.GetRelativePath(listing.Directory, entry)))
    with ex ->
        "error:" + ex.GetType().Name

/// The variables of `environment` an evaluation that reads `names` depends on: those it
/// names, in any case (MSBuild properties are case-insensitive), and those `envPrefixes`
/// marks, as `env:<name>` labels over the value's digest.
let environmentInputs (names: string list) (environment: (string * string) list) : (string * string) list =
    let wanted = HashSet<string>(names, StringComparer.OrdinalIgnoreCase)

    environment
    |> List.filter (fun (name, _) ->
        wanted.Contains name
        || envPrefixes
           |> List.exists (fun prefix -> name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
    |> List.distinctBy fst
    |> List.map (fun (name, value) -> "env:" + name, ContentHash.ofText value)

/// The key of the index record for `request`.
let requestKey (request: Request) : ActionKey.ActionKey =
    ActionKey.create
        InputsKind
        [ "root", request.Root
          "projects", digestOf request.Projects
          "toolchain", request.Toolchain
          "sdk", request.Sdk
          "global-properties", digestOf [ for name, value in request.GlobalProperties -> $"%s{name}=%s{value}" ] ]

let private listingLabel (listing: Listing) =
    let depth = if listing.Recursive then "**" else "*"
    $"listing:%s{listing.Kind}:%s{depth}:%s{Path.Combine(listing.Directory, listing.Pattern)}"

/// The key options evaluated from `inputs` for `request` are filed under, read from the
/// disk and `environment` NOW: equal only while every input is byte-identical.
let optionsKey
    (request: Request)
    (inputs: EvaluationInputs)
    (environment: (string * string) list)
    : ActionKey.ActionKey =
    let labelled =
        [ "request", (requestKey request).Value
          yield! environmentInputs inputs.EnvNames environment
          for path in inputs.Reads do
              "read:" + path, pathState path
          for path in inputs.Probes do
              "probe:" + path, pathState path
          for listing in inputs.Listings do
              listingLabel listing, listingState listing ]
        |> List.distinctBy fst

    ActionKey.create CheckRecords.OptionsKind labelled

let private stringArray (values: string seq) =
    JsonArray([| for v in values -> JsonValue.Create v :> JsonNode |])

let private readStrings (el: JsonElement) (name: string) =
    [ for v in el.GetProperty(name).EnumerateArray() -> v.GetString() ]

let private optionalString (el: JsonElement) (name: string) =
    match Json.tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private optionalNode (value: string option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create v
    | None -> null

/// Ionide's evaluated `ProjectOptions` as an `options` record files them.
module ProjectOptionsJson =
    let private properties (props: Types.Property list) =
        JsonArray(
            [| for p in props ->
                   let o = JsonObject()
                   o["name"] <- p.Name
                   o["value"] <- p.Value
                   o :> JsonNode |]
        )

    let private readProperties (el: JsonElement) (name: string) : Types.Property list =
        [ for p in el.GetProperty(name).EnumerateArray() ->
              { Name = p.GetProperty("name").GetString()
                Value = p.GetProperty("value").GetString() } ]

    let private sdkNode (sdk: Types.ProjectSdkInfo) =
        let o = JsonObject()
        o["isTestProject"] <- sdk.IsTestProject
        o["configuration"] <- sdk.Configuration
        o["isPackable"] <- sdk.IsPackable
        o["targetFramework"] <- sdk.TargetFramework
        o["targetFrameworkIdentifier"] <- sdk.TargetFrameworkIdentifier
        o["targetFrameworkVersion"] <- sdk.TargetFrameworkVersion
        o["msbuildAllProjects"] <- stringArray sdk.MSBuildAllProjects
        o["msbuildToolsVersion"] <- sdk.MSBuildToolsVersion
        o["projectAssetsFile"] <- sdk.ProjectAssetsFile
        o["restoreSuccess"] <- sdk.RestoreSuccess
        o["configurations"] <- stringArray sdk.Configurations
        o["targetFrameworks"] <- stringArray sdk.TargetFrameworks
        o["runArguments"] <- optionalNode sdk.RunArguments
        o["runCommand"] <- optionalNode sdk.RunCommand

        o["isPublishable"] <-
            match sdk.IsPublishable with
            | Some value -> JsonValue.Create value
            | None -> null

        o

    let private readSdk (el: JsonElement) : Types.ProjectSdkInfo =
        { IsTestProject = el.GetProperty("isTestProject").GetBoolean()
          Configuration = el.GetProperty("configuration").GetString()
          IsPackable = el.GetProperty("isPackable").GetBoolean()
          TargetFramework = el.GetProperty("targetFramework").GetString()
          TargetFrameworkIdentifier = el.GetProperty("targetFrameworkIdentifier").GetString()
          TargetFrameworkVersion = el.GetProperty("targetFrameworkVersion").GetString()
          MSBuildAllProjects = readStrings el "msbuildAllProjects"
          MSBuildToolsVersion = el.GetProperty("msbuildToolsVersion").GetString()
          ProjectAssetsFile = el.GetProperty("projectAssetsFile").GetString()
          RestoreSuccess = el.GetProperty("restoreSuccess").GetBoolean()
          Configurations = readStrings el "configurations"
          TargetFrameworks = readStrings el "targetFrameworks"
          RunArguments = optionalString el "runArguments"
          RunCommand = optionalString el "runCommand"
          IsPublishable =
            match Json.tryProp el "isPublishable" with
            | Some v when v.ValueKind = JsonValueKind.True -> Some true
            | Some v when v.ValueKind = JsonValueKind.False -> Some false
            | _ -> None }

    let private projectNode (p: Types.ProjectOptions) =
        let o = JsonObject()
        o["projectId"] <- optionalNode p.ProjectId
        o["projectFileName"] <- p.ProjectFileName
        o["targetFramework"] <- p.TargetFramework
        o["sourceFiles"] <- stringArray p.SourceFiles
        o["otherOptions"] <- stringArray p.OtherOptions

        o["referencedProjects"] <-
            JsonArray(
                [| for r in p.ReferencedProjects ->
                       let e = JsonObject()
                       e["relativePath"] <- r.RelativePath
                       e["projectFileName"] <- r.ProjectFileName
                       e["targetFramework"] <- r.TargetFramework
                       e :> JsonNode |]
            )

        o["packageReferences"] <-
            JsonArray(
                [| for r in p.PackageReferences ->
                       let e = JsonObject()
                       e["name"] <- r.Name
                       e["version"] <- r.Version
                       e["fullPath"] <- r.FullPath
                       e :> JsonNode |]
            )

        o["targetPath"] <- p.TargetPath
        o["targetRefPath"] <- optionalNode p.TargetRefPath

        o["outputType"] <-
            match p.ProjectOutputType with
            | Types.ProjectOutputType.Library -> "library"
            | Types.ProjectOutputType.Exe -> "exe"
            | Types.ProjectOutputType.Custom custom -> "custom:" + custom

        o["sdk"] <- sdkNode p.ProjectSdkInfo

        o["items"] <-
            JsonArray(
                [| for Types.ProjectItem.Compile(name, fullPath, metadata) in p.Items ->
                       let e = JsonObject()
                       e["name"] <- name
                       e["fullPath"] <- fullPath

                       match metadata with
                       | Some values ->
                           let m = JsonObject()

                           for KeyValue(key, value) in values do
                               m[key] <- value

                           e["metadata"] <- m
                       | None -> ()

                       e :> JsonNode |]
            )

        o["properties"] <- properties p.Properties
        o["customProperties"] <- properties p.CustomProperties

        o["analyzers"] <-
            JsonArray(
                [| for a in p.Analyzers ->
                       let e = JsonObject()
                       e["propertyName"] <- a.PropertyName
                       e["nugetPackageRoot"] <- a.NugetPackageRoot
                       e["dllRootPath"] <- a.DllRootPath
                       e :> JsonNode |]
            )

        o

    let private readProject (loadTime: DateTime) (el: JsonElement) : Types.ProjectOptions =
        let str (name: string) = el.GetProperty(name).GetString()

        { ProjectId = optionalString el "projectId"
          ProjectFileName = str "projectFileName"
          TargetFramework = str "targetFramework"
          SourceFiles = readStrings el "sourceFiles"
          OtherOptions = readStrings el "otherOptions"
          ReferencedProjects =
            [ for r in el.GetProperty("referencedProjects").EnumerateArray() ->
                  { RelativePath = r.GetProperty("relativePath").GetString()
                    ProjectFileName = r.GetProperty("projectFileName").GetString()
                    TargetFramework = r.GetProperty("targetFramework").GetString() } ]
          PackageReferences =
            [ for r in el.GetProperty("packageReferences").EnumerateArray() ->
                  { Name = r.GetProperty("name").GetString()
                    Version = r.GetProperty("version").GetString()
                    FullPath = r.GetProperty("fullPath").GetString() } ]
          LoadTime = loadTime
          TargetPath = str "targetPath"
          TargetRefPath = optionalString el "targetRefPath"
          ProjectOutputType =
            match str "outputType" with
            | "library" -> Types.ProjectOutputType.Library
            | "exe" -> Types.ProjectOutputType.Exe
            | custom when custom.StartsWith("custom:", StringComparison.Ordinal) ->
                Types.ProjectOutputType.Custom(custom.Substring "custom:".Length)
            | other -> raise (FormatException $"unknown output type %s{other}")
          ProjectSdkInfo = readSdk (el.GetProperty "sdk")
          Items =
            [ for i in el.GetProperty("items").EnumerateArray() ->
                  let metadata =
                      Json.tryProp i "metadata"
                      |> Option.map (fun m ->
                          m.EnumerateObject()
                          |> Seq.map (fun p -> p.Name, p.Value.GetString())
                          |> Map.ofSeq)

                  Types.ProjectItem.Compile(
                      i.GetProperty("name").GetString(),
                      i.GetProperty("fullPath").GetString(),
                      metadata
                  ) ]
          Properties = readProperties el "properties"
          CustomProperties = readProperties el "customProperties"
          AllProperties = Map.empty
          AllItems = Map.empty
          Analyzers =
            [ for a in el.GetProperty("analyzers").EnumerateArray() ->
                  { PropertyName = a.GetProperty("propertyName").GetString()
                    NugetPackageRoot = a.GetProperty("nugetPackageRoot").GetString()
                    DllRootPath = a.GetProperty("dllRootPath").GetString() } ] }

    /// The outputs object an `options` record files for `projects`.
    let toJson (projects: Types.ProjectOptions list) : string =
        let o = JsonObject()
        o["schema"] <- OutputsSchema
        o["projects"] <- JsonArray([| for p in projects -> projectNode p :> JsonNode |])
        o.ToJsonString()

    /// The options a record filed, loaded at `loadTime`; None for anything that is not
    /// exactly an outputs object of this schema.
    let tryOfJson (loadTime: DateTime) (text: string) : Types.ProjectOptions list option =
        try
            use doc = JsonDocument.Parse text
            let o = doc.RootElement

            if Json.tryString o "schema" <> Some OutputsSchema then
                None
            else
                Some [ for p in o.GetProperty("projects").EnumerateArray() -> readProject loadTime p ]
        with
        | :? JsonException
        | :? KeyNotFoundException
        | :? InvalidOperationException
        | :? FormatException -> None

/// The index record's outputs: what the evaluation read, and the `options` key it filed.
module private InputsJson =
    let toJson (inputs: EvaluationInputs) (key: ActionKey.ActionKey) : string =
        let o = JsonObject()
        o["schema"] <- InputsSchema
        o["reads"] <- stringArray inputs.Reads
        o["probes"] <- stringArray inputs.Probes

        o["listings"] <-
            JsonArray(
                [| for l in inputs.Listings ->
                       let e = JsonObject()
                       e["kind"] <- l.Kind
                       e["directory"] <- l.Directory
                       e["pattern"] <- l.Pattern
                       e["recursive"] <- l.Recursive
                       e :> JsonNode |]
            )

        o["envNames"] <- stringArray inputs.EnvNames

        o["optionsKey"] <- key.Value
        o.ToJsonString()

    let tryOfJson (text: string) : (EvaluationInputs * string) option =
        try
            use doc = JsonDocument.Parse text
            let o = doc.RootElement

            if Json.tryString o "schema" <> Some InputsSchema then
                None
            else
                let inputs =
                    { Reads = readStrings o "reads"
                      Probes = readStrings o "probes"
                      Listings =
                        [ for l in o.GetProperty("listings").EnumerateArray() ->
                              { Kind = l.GetProperty("kind").GetString()
                                Directory = l.GetProperty("directory").GetString()
                                Pattern = l.GetProperty("pattern").GetString()
                                Recursive = l.GetProperty("recursive").GetBoolean() } ]
                      EnvNames = readStrings o "envNames" }

                Some(inputs, o.GetProperty("optionsKey").GetString())
        with
        | :? JsonException
        | :? KeyNotFoundException
        | :? InvalidOperationException -> None

/// What a lookup found.
type Replay =
    | Replayed of Types.ProjectOptions list
    /// Why there is nothing to replay, naming the inputs that moved where it can.
    | Missed of why: string

/// The options filed for `request` whose every input is byte-identical now, loaded at
/// `loadTime`; otherwise why not.
let tryReplay
    (store: OutcomeStore.Store)
    (request: Request)
    (environment: (string * string) list)
    (loadTime: DateTime)
    : Replay =
    match store.LookupWithOutputs (requestKey request).Value with
    | None -> Missed "no evaluation of this request is filed"
    | Some(_, outputs) ->
        match outputs |> Option.bind InputsJson.tryOfJson with
        | None -> Missed "the filed inputs are unreadable"
        | Some(inputs, filedKey) ->
            let key = optionsKey request inputs environment

            match store.LookupWithOutputs key.Value with
            | Some(_, Some text) ->
                match ProjectOptionsJson.tryOfJson loadTime text with
                | Some projects -> Replayed projects
                | None -> Missed "the filed options are unreadable"
            | Some(_, None)
            | None ->
                match
                    store.Lookup filedKey
                    |> Option.map (fun filed -> ActionKey.moved filed.Key.Inputs key.Inputs)
                with
                | None
                | Some [] -> Missed "the filed options are gone"
                | Some moved ->
                    let shown = moved |> List.truncate 5 |> String.concat ", "

                    let more =
                        if moved.Length > 5 then
                            $" and %d{moved.Length - 5} more"
                        else
                            ""

                    Missed $"inputs moved: %s{shown}%s{more}"

/// Whether an evaluation was filed.
type Filing =
    | Filed
    | Refused of why: string

/// Where to look for whether `path` changed: the path itself, or for a path that is not
/// there, the nearest directory above it that is (a removal changes its directory).
let rec private witnessOf (path: string) : string option =
    if File.Exists path || Directory.Exists path then
        Some path
    else
        match Path.GetDirectoryName path with
        | null
        | "" -> None
        | parent when parent = path -> None
        | parent -> witnessOf parent

/// File the options `projects` evaluated from `inputs` for `request`, unless an input may
/// have changed since `since` (the evaluation's start, less the racy window), judged by
/// `stamp`.
let file
    (store: OutcomeStore.Store)
    (request: Request)
    (inputs: EvaluationInputs)
    (projects: Types.ProjectOptions list)
    (environment: (string * string) list)
    (since: DateTime)
    (stamp: string -> FileStamp.Stamp voption)
    : Filing =
    let changedSince (path: string) =
        match witnessOf path |> Option.map stamp with
        | Some(ValueSome s) -> s.ChangedAt >= since
        | _ -> false

    let watched =
        inputs.Reads
        @ inputs.Probes
        @ [ for listing in inputs.Listings -> listing.Directory ]

    match inputs.Listings |> List.tryFind _.Recursive, watched |> List.tryFind changedSince with
    | Some listing, _ -> Refused $"MSBuild listed the tree under %s{listing.Directory}, which is not keyed"
    | None, Some path -> Refused $"%s{path} changed while the projects were evaluated"
    | None, None ->
        let key = optionsKey request inputs environment

        let filedRecord (recordKey: ActionKey.ActionKey) (subject: string) (outputs: string) =
            store.RecordWithOutputs(
                { Key = recordKey
                  Subject = subject
                  Outcome = OutcomeStore.Outcome.Pass
                  Counts = None
                  DurationMs = None
                  Producer =
                    { Binary = request.Toolchain
                      Worktree = request.Root
                      RunId = None }
                  RecordedAt = DateTimeOffset.UtcNow },
                Some outputs
            )

        // The options first: an index naming a key that holds nothing is a miss, never a
        // replay of something else.
        if
            filedRecord key "options" (ProjectOptionsJson.toJson projects)
            && filedRecord (requestKey request) "options-inputs" (InputsJson.toJson inputs key)
        then
            Filed
        else
            Refused "the outcome store could not be written"

/// The process environment, as `optionsKey` reads it.
let processEnvironment () : (string * string) list =
    [ for entry in
          Environment.GetEnvironmentVariables()
          |> Seq.cast<System.Collections.DictionaryEntry> -> string entry.Key, string entry.Value ]

/// A workspace loader that answers from the outcome store when every input of the last
/// evaluation of the same request is byte-identical, and otherwise evaluates through
/// `inner`, lists what that read with `listInputs`, and files it.
///
/// A load with custom properties or a binary log always evaluates and files nothing: the
/// one is not the request the key names, and the other is asked for to watch MSBuild work.
type CachingLoader
    (
        inner: IWorkspaceLoader,
        listInputs: Types.ProjectOptions list -> EvaluationInputs,
        store: OutcomeStore.Store,
        request: string list -> Request,
        environment: unit -> (string * string) list,
        log: string -> unit,
        ?racyWindow: TimeSpan,
        ?stamp: string -> FileStamp.Stamp voption
    ) =
    let racyWindow = defaultArg racyWindow CheckCache.FileContentHasher.RacyWindow
    let stamp = defaultArg stamp FileStamp.read

    let evaluateAndFile (req: Request) (env: (string * string) list) (projects: string list) =
        let startedAt = DateTime.UtcNow
        let failed = List<string>()

        use _ =
            inner.Notifications.Subscribe(fun state ->
                match state with
                | Types.WorkspaceProjectState.Failed(project, _) -> lock failed (fun () -> failed.Add project)
                | _ -> ())

        let loaded = inner.LoadProjects projects |> Seq.toList

        let filing =
            if failed.Count > 0 then
                Refused $"%d{failed.Count} project(s) failed to load"
            elif List.isEmpty loaded then
                Refused "no project loaded"
            else
                try
                    file store req (listInputs loaded) loaded env (startedAt - racyWindow) stamp
                with ex ->
                    Refused $"its inputs could not be listed: %s{ex.Message}"

        match filing with
        | Filed -> log $"options: filed the evaluation of %d{loaded.Length} project(s)"
        | Refused why -> log $"options: not filed: %s{why}"

        loaded

    let load (projects: string list) : seq<Types.ProjectOptions> =
        let req = request projects
        let env = environment ()

        match tryReplay store req env DateTime.UtcNow with
        | Replayed options ->
            log $"options: replayed %d{options.Length} project(s) with no evaluation"
            options
        | Missed why ->
            log $"options: evaluating: %s{why}"
            evaluateAndFile req env projects
        |> Seq.ofList

    interface IWorkspaceLoader with
        member _.LoadProjects(projects: string list) = load projects

        member _.LoadProjects(projects: string list, customProperties: string list, binaryLog: BinaryLogGeneration) =
            match customProperties, binaryLog with
            | [], BinaryLogGeneration.Off -> load projects
            | _ -> inner.LoadProjects(projects, customProperties, binaryLog)

        member _.LoadSln(solution: string) = inner.LoadSln solution

        member _.LoadSln(solution: string, customProperties: string list, binaryLog: BinaryLogGeneration) =
            inner.LoadSln(solution, customProperties, binaryLog)

        [<CLIEvent>]
        member _.Notifications = inner.Notifications
