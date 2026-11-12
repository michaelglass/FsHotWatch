/// Options records that replay across checkouts of one repository on one machine
/// (AUTOMATION-1602 (b)), IN SHADOW: the root-keyed records (`OptionsRecords`) are filed and
/// replayed exactly as before, and beside them each filed evaluation is filed again with the
/// checkout root as a token (`PathToken`), under two kinds of its own:
///   * `options-portable-inputs`, keyed by the request with no root (`requestKey`), names
///     what the evaluation read, as root-relative paths where they are under the root;
///   * `options-portable`, keyed by that request and the state of every input read through
///     the token: a file under the root by its bytes with the root tokenized, so a restore's
///     `project.assets.json` (which names the root) is the same in every checkout, and a
///     generated compile item (`gen:`) by the bytes filed with it, so one a fresh checkout
///     has not written yet still matches.
///
/// Before an evaluation the root-keyed lookup missed, the portable lookup runs; the
/// evaluation always runs anyway, and one line of `shadow/options-portable.jsonl` beside the
/// store says what the portable record WOULD have replayed and whether it equals what was
/// evaluated, field by field (`compare`). Nothing here answers a load. The switch to serving
/// portable replays is a release decision taken on that log (the proposal's §6), never an
/// automatic one.
module FsHotWatch.OptionsPortable

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ionide.ProjInfo
open FsHotWatch.OptionsRecords

/// `optionsRecordsPortable`: `off`, or `shadow`. `on` is not built until the shadow's
/// criterion is met.
[<RequireQualifiedAccess>]
type Mode =
    | Off
    | Shadow

module Mode =
    let tryParse =
        function
        | "off" -> Some Mode.Off
        | "shadow" -> Some Mode.Shadow
        | _ -> None

/// The kind of the record that names what an evaluation read, with no root in its key.
[<Literal>]
let RequestKind = "options-portable-inputs"

/// The kind of the record that holds the tokenized options.
[<Literal>]
let OptionsKind = "options-portable"

[<Literal>]
let InputsSchema = "fshw-options-portable-inputs-v1"

[<Literal>]
let OutputsSchema = "fshw-options-portable-v1"

/// The format of one shadow log line.
[<Literal>]
let ShadowSchema = "fshw-options-portable-shadow-v1"

let private ordinal (a: string) (b: string) = String.CompareOrdinal(a, b)

let private digestOf (lines: string seq) =
    lines |> Seq.sortWith ordinal |> String.concat "\n" |> ContentHash.ofText

let private strictUtf8 = UTF8Encoding(false, true)

/// A file's text with `root` tokenized; None when it is not UTF-8, or names the root in a
/// way the token cannot rebind.
let private tokenizedText (root: string) (path: string) : string option =
    try
        let text = PathToken.tokenize root (strictUtf8.GetString(File.ReadAllBytes path))
        if PathToken.survives root text then None else Some text
    with :? DecoderFallbackException ->
        None

/// A file's state through the token: its text with the root tokenized, or, where that cannot
/// be read, its raw bytes, which name this checkout and so match no other.
let private tokenizedState (root: string) (path: string) : string =
    match tokenizedText root path with
    | Some text -> "text:" + ContentHash.ofText text
    | None -> "file:" + ContentHash.ofFile path

let private under (root: string) (path: string) =
    path = root || path.StartsWith(root + "/", StringComparison.Ordinal)

/// A path's state as the portable key reads it: through the token under `root`, exactly as
/// the root-keyed key reads it elsewhere.
let pathState (root: string) (path: string) : string =
    if under root path && File.Exists path then
        tokenizedState root path
    else
        OptionsRecords.pathState path

/// The key of the index record for `request`: the root-keyed request key's terms through
/// the token, no `root`, and `PathToken.Version`.
let requestKey (request: Request) : ActionKey.ActionKey =
    let t = PathToken.tokenize request.Root

    ActionKey.create
        RequestKind
        [ "projects", digestOf (request.Projects |> List.map t)
          "toolchain", request.Toolchain
          "sdk", t request.Sdk
          "global-properties", digestOf [ for name, value in request.GlobalProperties -> $"%s{name}=%s{t value}" ]
          "path-token", PathToken.Version ]

/// What an evaluation read, as an index record names it: every path through the token.
type Index =
    {
        Reads: string list
        Probes: string list
        Listings: Listing list
        EnvNames: string list
        /// Each generated compile item, and the state of the bytes the options record files.
        Generated: (string * string) list
    }

let private listingLabel (listing: Listing) =
    let depth = if listing.Recursive then "**" else "*"
    $"listing:%s{ListingKind.token listing.Kind}:%s{depth}:%s{Path.Combine(listing.Directory, listing.Pattern)}"

/// The key the options of `index` are filed under in `root`, read from the disk and
/// `environment` NOW. `generated` is each generated item's state: its bytes on disk where
/// they are there, and where they are not, the state filed (a fresh checkout's design-time
/// build writes them only once it runs).
let private keyOf
    (root: string)
    (request: ActionKey.ActionKey)
    (index: Index)
    (generated: (string * string) list)
    (environment: (string * string) list)
    : ActionKey.ActionKey =
    let at = PathToken.detokenize root

    let labelled =
        [ "request", request.Value.Text
          // Verbatim: a variable whose value names the root keys a miss, which is safe.
          yield! environmentInputs index.EnvNames environment
          for path in index.Reads do
              "read:" + path, pathState root (at path)
          for path in index.Probes do
              "probe:" + path, pathState root (at path)
          for listing in index.Listings do
              listingLabel listing,
              listingState
                  { listing with
                      Directory = at listing.Directory }
          for path, state in generated do
              "gen:" + path, state ]
        |> List.distinctBy fst

    ActionKey.create OptionsKind labelled

/// The generated compile items of `projects` (under a project's obj/ or bin/), whose bytes
/// the options record files.
let private generatedOf (projects: Types.ProjectOptions list) : string list =
    [ for p in projects do
          for source in p.SourceFiles do
              if PathFilter.isGeneratedFor p.ProjectFileName source then
                  Path.GetFullPath source ]
    |> List.distinct

let private stringArray (values: string seq) =
    JsonArray([| for v in values -> JsonValue.Create v :> JsonNode |])

let private readStrings (el: JsonElement) (name: string) =
    [ for v in el.GetProperty(name).EnumerateArray() -> v.GetString() ]

module private IndexJson =
    let toJson (index: Index) (key: ActionKey.ActionKey) : string =
        let o = JsonObject()
        o["schema"] <- InputsSchema
        o["reads"] <- stringArray index.Reads
        o["probes"] <- stringArray index.Probes

        o["listings"] <-
            JsonArray(
                [| for l in index.Listings ->
                       let e = JsonObject()
                       e["kind"] <- ListingKind.token l.Kind
                       e["directory"] <- l.Directory
                       e["pattern"] <- l.Pattern
                       e["recursive"] <- l.Recursive
                       e :> JsonNode |]
            )

        o["envNames"] <- stringArray index.EnvNames

        o["generated"] <-
            JsonArray(
                [| for path, state in index.Generated ->
                       let e = JsonObject()
                       e["path"] <- path
                       e["state"] <- state
                       e :> JsonNode |]
            )

        o["optionsKey"] <- key.Value.Text
        o.ToJsonString()

    let tryOfJson (text: string) : (Index * ActionKey.KeyValue) option =
        try
            use doc = JsonDocument.Parse text
            let o = doc.RootElement

            if Json.tryString o "schema" <> Some InputsSchema then
                None
            else
                let index =
                    { Reads = readStrings o "reads"
                      Probes = readStrings o "probes"
                      Listings =
                        [ for l in o.GetProperty("listings").EnumerateArray() ->
                              { Kind =
                                  match ListingKind.tryParse (l.GetProperty("kind").GetString()) with
                                  | Some kind -> kind
                                  | None -> raise (FormatException "a listing names a kind no listing has")
                                Directory = l.GetProperty("directory").GetString()
                                Pattern = l.GetProperty("pattern").GetString()
                                Recursive = l.GetProperty("recursive").GetBoolean() } ]
                      EnvNames = readStrings o "envNames"
                      Generated =
                        [ for g in o.GetProperty("generated").EnumerateArray() ->
                              g.GetProperty("path").GetString(), g.GetProperty("state").GetString() ] }

                ActionKey.KeyValue.tryParse (o.GetProperty("optionsKey").GetString())
                |> Option.map (fun key -> index, key)
        with
        | :? JsonException
        | :? Collections.Generic.KeyNotFoundException
        | :? InvalidOperationException
        | :? FormatException -> None

/// Every string value in `node`, rewritten by `f`, in place.
let rec private rewriteStrings (f: string -> string) (node: JsonNode) : unit =
    match node with
    | :? JsonObject as o ->
        for name in [ for KeyValue(name, _) in o -> name ] do
            match o[name] with
            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                o[name] <- JsonValue.Create(f (v.GetValue<string>()))
            | child when not (isNull child) -> rewriteStrings f child
            | _ -> ()
    | :? JsonArray as a ->
        for i in 0 .. a.Count - 1 do
            match a[i] with
            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String ->
                a[i] <- JsonValue.Create(f (v.GetValue<string>()))
            | child when not (isNull child) -> rewriteStrings f child
            | _ -> ()
    | _ -> ()

/// Every string value in `node`.
let rec private stringsIn (node: JsonNode) : string seq =
    seq {
        match node with
        | :? JsonObject as o ->
            for KeyValue(_, child) in o do
                if not (isNull child) then
                    yield! stringsIn child
        | :? JsonArray as a ->
            for child in a do
                if not (isNull child) then
                    yield! stringsIn child
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
        | _ -> ()
    }

/// `projects` as `ProjectOptionsJson` files them, with `root` tokenized in every string.
let private tokenizedProjects (root: string) (projects: Types.ProjectOptions list) : JsonArray =
    let o = JsonNode.Parse(ProjectOptionsJson.toJson projects).AsObject()
    let projectsNode = o["projects"].AsArray()
    o.Remove "projects" |> ignore
    rewriteStrings (PathToken.tokenize root) projectsNode
    projectsNode

/// Why a string `root` was tokenized in may not be filed portably, if it may not.
let private refusalOf (root: string) (nested: string -> string option) (original: string) (tokenized: string) =
    if PathToken.holdsToken original then
        Some $"%s{original} already holds the root token"
    elif PathToken.survives root tokenized then
        Some $"the root survives tokenizing %s{original}"
    else
        nested tokenized
        |> Option.map (fun checkout -> $"%s{original} reaches into the nested checkout %s{checkout}")

/// The portable index and outputs of an evaluation `root` filed, or why it is not portable.
let private portableOf
    (root: string)
    (inputs: EvaluationInputs)
    (projects: Types.ProjectOptions list)
    : Result<Index * string, string> =
    let t = PathToken.tokenize root
    let nested = PathToken.nestedCheckouts root

    let firstRefusal (strings: string seq) =
        strings |> Seq.tryPick (fun s -> refusalOf root nested s (t s))

    let optionStrings =
        stringsIn (JsonNode.Parse(ProjectOptionsJson.toJson projects)) |> List.ofSeq

    let generated = generatedOf projects
    let generatedSet = Set.ofList generated
    let reads = inputs.Reads |> List.filter (generatedSet.Contains >> not)

    let labelStrings =
        reads
        @ inputs.Probes
        @ [ for l in inputs.Listings do
                l.Directory
                l.Pattern ]

    match firstRefusal (optionStrings @ labelStrings @ generated) with
    | Some why -> Error why
    | None ->
        match optionStrings |> List.tryPick (fun s -> PathToken.temporaryIn (t s)) with
        | Some temporary -> Error $"an option names the temporary directory %s{temporary}"
        | None ->
            let texts =
                generated
                |> List.map (fun path ->
                    if not (File.Exists path) then
                        Error $"the generated compile item %s{path} is not there"
                    else
                        match tokenizedText root path with
                        | Some text -> Ok(t path, text)
                        | None -> Error $"the generated compile item %s{path} is not text the root token can rebind")

            match
                texts
                |> List.tryPick (function
                    | Error why -> Some why
                    | Ok _ -> None)
            with
            | Some why -> Error why
            | None ->
                let texts =
                    texts
                    |> List.choose (function
                        | Ok text -> Some text
                        | Error _ -> None)

                let index =
                    { Reads = reads |> List.map t
                      Probes = inputs.Probes |> List.map t
                      Listings =
                        [ for l in inputs.Listings ->
                              { l with
                                  Directory = t l.Directory
                                  Pattern = t l.Pattern } ]
                      EnvNames = inputs.EnvNames
                      Generated = [ for path, text in texts -> path, "text:" + ContentHash.ofText text ] }

                let outputs = JsonObject()
                outputs["schema"] <- OutputsSchema
                outputs["pathToken"] <- PathToken.Version
                outputs["projects"] <- tokenizedProjects root projects

                outputs["generated"] <-
                    JsonArray(
                        [| for path, text in texts ->
                               let e = JsonObject()
                               e["path"] <- path
                               e["text"] <- text
                               e :> JsonNode |]
                    )

                Ok(index, outputs.ToJsonString())

/// File, through the token, an evaluation the root-keyed filer has just filed for `request`
/// (so under the same guard: no input moved while it was evaluated). The key is read twice,
/// and nothing is filed unless both readings agree. Otherwise why not.
let file
    (store: OutcomeStore.Store)
    (request: Request)
    (inputs: EvaluationInputs)
    (projects: Types.ProjectOptions list)
    (environment: (string * string) list)
    : Result<unit, string> =
    let root = request.Root

    match portableOf root inputs projects with
    | Error why -> Error why
    | Ok(index, outputs) ->
        let request' = requestKey request
        let key = keyOf root request' index index.Generated environment

        if
            (keyOf root request' index index.Generated environment).Value.Text
            <> key.Value.Text
        then
            Error "an input moved while it was read"
        else
            let filed (recordKey: ActionKey.ActionKey) (subject: string) (text: string) =
                store.RecordWithOutputs(
                    OutcomeStore.Record.ofAction
                        recordKey
                        subject
                        OutcomeStore.Outcome.Pass
                        request.Toolchain
                        root
                        DateTimeOffset.UtcNow,
                    Some text
                )

            // The options first, and only where no checkout filed them already: the record a
            // later lookup compares against stays the first checkout's.
            if
                (store.HoldsKey key || filed key OptionsKind outputs)
                && filed request' RequestKind (IndexJson.toJson index key)
            then
                Ok()
            else
                Error "the outcome store could not be written"

/// What a portable record filed in another checkout holds.
[<NoComparison; NoEquality>]
type Filed =
    {
        /// The checkout that filed it.
        Root: string
        /// The options, tokenized, as `ProjectOptionsJson` files them.
        Projects: JsonArray
        /// Each generated compile item, tokenized, and its text with the root tokenized.
        Generated: (string * string) list
    }

/// What the portable lookup found, before any evaluation.
[<NoComparison; NoEquality>]
type Lookup =
    {
        Request: string
        /// Whether the key read now is the key the index names.
        KeyMatch: bool
        /// The labels whose state differs from the filed record's.
        Moved: string list
        /// Why there is nothing to compare, where there is not.
        Missed: string option
        Filed: Filed option
        GeneratedPresent: int
        GeneratedAbsent: int
    }

let private filedOf (record: OutcomeStore.Record) (text: string) : Filed option =
    try
        let o = JsonNode.Parse(text).AsObject()

        if
            o["schema"].GetValue<string>() <> OutputsSchema
            || o["pathToken"].GetValue<string>() <> PathToken.Version
        then
            None
        else
            Some
                { Root = record.Producer.Worktree
                  Projects = o["projects"].AsArray()
                  Generated =
                    [ for g in o["generated"].AsArray() -> g["path"].GetValue<string>(), g["text"].GetValue<string>() ] }
    with
    | :? JsonException
    | :? InvalidOperationException
    | :? NullReferenceException
    | :? FormatException -> None

/// What a portable record of `request` would replay in `root` now.
let lookup (store: OutcomeStore.Store) (request: Request) (environment: (string * string) list) : Lookup =
    let root = request.Root
    let request' = requestKey request

    let missed why =
        { Request = request'.Value.Text
          KeyMatch = false
          Moved = []
          Missed = Some why
          Filed = None
          GeneratedPresent = 0
          GeneratedAbsent = 0 }

    match store.LookupWithOutputs request' with
    | None -> missed "no portable evaluation of this request is filed"
    | Some(_, outputs) ->
        match outputs |> Option.bind IndexJson.tryOfJson with
        | None -> missed "the filed portable inputs are unreadable"
        | Some(index, filedKey) ->
            let present, absent =
                index.Generated
                |> List.map (fun (path, filedState) ->
                    let onDisk = PathToken.detokenize root path

                    if File.Exists onDisk then
                        true, (path, tokenizedState root onDisk)
                    else
                        false, (path, filedState))
                |> List.partition fst

            let key = keyOf root request' index (List.map snd (present @ absent)) environment
            let record = store.LookupWithOutputs filedKey

            let filed =
                match record with
                | Some(r, Some text) -> filedOf r text
                | _ -> None

            { Request = request'.Value.Text
              KeyMatch = key.Value.Text = filedKey.Text
              Moved =
                match record with
                | Some(r, _) -> ActionKey.moved r.Key.Inputs key.Inputs
                | None -> []
              Missed =
                match filed with
                | Some _ -> None
                | None -> Some "the filed portable options are gone or unreadable"
              Filed = filed
              GeneratedPresent = present.Length
              GeneratedAbsent = absent.Length }

/// One place the filed and the evaluated options differ: the project (tokenized), the JSON
/// pointer within it, and each side's value as JSON (`null` where a side has none).
type Difference =
    { Project: string
      Pointer: string
      Filed: string
      Evaluated: string }

let private escapePointer (name: string) =
    name.Replace("~", "~0").Replace("/", "~1")

let private jsonText (node: JsonNode) =
    if isNull node then "null" else node.ToJsonString()

/// Where `filed` and `evaluated` differ, below `pointer`: objects by key, arrays in order,
/// values exactly.
let rec private differ (pointer: string) (filed: JsonNode) (evaluated: JsonNode) : (string * string * string) list =
    match filed, evaluated with
    | null, null -> []
    | (:? JsonObject as f), (:? JsonObject as e) ->
        [ for name in
              Seq.append [ for KeyValue(k, _) in f -> k ] [ for KeyValue(k, _) in e -> k ]
              |> Seq.distinct
              |> Seq.sortWith ordinal do
              yield! differ $"%s{pointer}/%s{escapePointer name}" f[name] e[name] ]
    | (:? JsonArray as f), (:? JsonArray as e) ->
        [ for i in 0 .. max f.Count e.Count - 1 do
              yield!
                  differ $"%s{pointer}/%d{i}" (if i < f.Count then f[i] else null) (if i < e.Count then e[i] else null) ]
    | _ when JsonNode.DeepEquals(filed, evaluated) -> []
    | _ -> [ pointer, jsonText filed, jsonText evaluated ]

let private projectName (node: JsonNode) =
    match node with
    | :? JsonObject as o when not (isNull o["projectFileName"]) -> o["projectFileName"].GetValue<string>()
    | _ -> "?"

/// Where the options `filed` holds differ from `evaluated` in `root`, through the token:
/// the projects matched by file name (the SETS must be equal; the list's order is not
/// compared), then every field of each, in order.
let compareProjects (root: string) (filed: JsonArray) (evaluated: Types.ProjectOptions list) : Difference list =
    let byName (projects: JsonArray) =
        [ for p in projects -> projectName p, p ] |> Map.ofList

    let filedByName = byName filed
    let evaluatedByName = byName (tokenizedProjects root evaluated)

    [ for name in
          Seq.append filedByName.Keys evaluatedByName.Keys
          |> Seq.distinct
          |> Seq.sortWith ordinal do
          let side (projects: Map<string, JsonNode>) =
              Map.tryFind name projects |> Option.defaultValue null

          for pointer, f, e in differ "" (side filedByName) (side evaluatedByName) do
              { Project = name
                Pointer = pointer
                Filed = f
                Evaluated = e } ]

/// Where the generated compile items' bytes now in `root` differ from the bytes `filed` holds.
let compareGenerated (root: string) (filed: (string * string) list) : Difference list =
    [ for path, text in filed do
          let onDisk = PathToken.detokenize root path

          let now =
              if File.Exists onDisk then
                  tokenizedText root onDisk
                  |> Option.defaultValue "(not text the root token can rebind)"
              else
                  "(absent)"

          if now <> text then
              { Project = path
                Pointer = "generated:" + path
                Filed = text
                Evaluated = now } ]

let private clip (text: string) =
    if text.Length > 400 then
        text.Substring(0, 400) + "…"
    else
        text

/// One shadow log line.
let shadowLine
    (at: DateTimeOffset)
    (fshw: string)
    (root: string)
    (commit: string option)
    (lookup: Lookup option)
    (evaluatedCount: int)
    (differences: Difference list option)
    (generatedAgree: bool option)
    (error: string option)
    : string =
    let o = JsonObject()
    let filedRoot = lookup |> Option.bind _.Filed |> Option.map _.Root
    let compared = differences.IsSome
    o["schema"] <- ShadowSchema
    o["at"] <- at.ToString("o")
    o["fshw"] <- fshw
    o["pathToken"] <- PathToken.Version
    o["root"] <- root
    o["rootLength"] <- root.Length

    o["filedRoot"] <-
        (filedRoot
         |> Option.map (fun r -> JsonValue.Create r :> JsonNode)
         |> Option.toObj)

    o["filedRootLength"] <-
        (filedRoot
         |> Option.map (fun r -> JsonValue.Create r.Length :> JsonNode)
         |> Option.toObj)

    o["crossRoot"] <- (filedRoot |> Option.exists (fun r -> r <> root))
    o["commit"] <- (commit |> Option.map (fun c -> JsonValue.Create c :> JsonNode) |> Option.toObj)

    o["request"] <-
        (lookup
         |> Option.map (fun l -> JsonValue.Create l.Request :> JsonNode)
         |> Option.toObj)

    o["keyMatch"] <- (lookup |> Option.exists _.KeyMatch)
    let moved = lookup |> Option.map _.Moved |> Option.defaultValue []
    o["moved"] <- stringArray (List.truncate 20 moved)
    o["movedCount"] <- moved.Length

    o["missed"] <-
        (lookup
         |> Option.bind _.Missed
         |> Option.map (fun m -> JsonValue.Create m :> JsonNode)
         |> Option.toObj)

    o["compared"] <- compared

    o["agree"] <-
        if compared then
            JsonValue.Create(error.IsNone && differences = Some []) :> JsonNode
        else
            null

    o["projects"] <- evaluatedCount
    let differences = differences |> Option.defaultValue []

    o["differences"] <-
        JsonArray(
            [| for d in List.truncate 5 differences ->
                   let e = JsonObject()
                   e["project"] <- d.Project
                   e["pointer"] <- d.Pointer
                   e["filed"] <- clip d.Filed
                   e["evaluated"] <- clip d.Evaluated
                   e :> JsonNode |]
        )

    o["differenceCount"] <- differences.Length
    let generated = JsonObject()
    generated["present"] <- (lookup |> Option.map _.GeneratedPresent |> Option.defaultValue 0)
    generated["absent"] <- (lookup |> Option.map _.GeneratedAbsent |> Option.defaultValue 0)

    generated["bytesAgree"] <-
        (generatedAgree
         |> Option.map (fun agree -> JsonValue.Create agree :> JsonNode)
         |> Option.toObj)

    o["generated"] <- generated
    o["error"] <- (error |> Option.map (fun e -> JsonValue.Create e :> JsonNode) |> Option.toObj)
    o.ToJsonString()

/// Append `line` to the log at `path`, in one write.
let private append (path: string) (line: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

    use stream =
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)

    let bytes = Encoding.UTF8.GetBytes(line + "\n")
    stream.Write(bytes, 0, bytes.Length)

/// The shadow log of the store at `storeRoot` (`<store>/../shadow/options-portable.jsonl`):
/// box-wide, beside the store, because the trust criterion spans checkouts.
let logPathFor (storeRoot: string) =
    Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator storeRoot), "shadow", "options-portable.jsonl")

/// The shadow of the loader for the checkout `root`, filing in `store` and logging each
/// comparison to `logPath`; None, said through `log`, where the root is not portable
/// (`PathToken.portableRoot`). `fshw` names this build in each line, and `commit` the
/// checkout's commit, read when a line is written.
let shadow
    (store: OutcomeStore.Store)
    (root: string)
    (logPath: string)
    (fshw: string)
    (commit: unit -> string option)
    (log: string -> unit)
    : OptionsShadow option =
    match PathToken.portableRoot root with
    | Error why ->
        log $"options: this checkout is not portable, so its options are filed by root only: %s{why}"
        None
    | Ok root ->
        let errorText (ex: exn) =
            $"%s{ex.GetType().Name}: %s{ex.Message}"

        Some
            { BeforeEvaluation =
                fun request environment ->
                    let looked =
                        try
                            Ok(lookup store { request with Root = root } environment)
                        with ex ->
                            Error(errorText ex)

                    fun evaluated ->
                        let line =
                            try
                                let lookup =
                                    match looked with
                                    | Ok l -> l
                                    | Error why -> raise (InvalidOperationException why)

                                let differences, generatedAgree =
                                    match lookup.Filed with
                                    | Some filed ->
                                        let generated = compareGenerated root filed.Generated

                                        Some(compareProjects root filed.Projects evaluated @ generated),
                                        Some generated.IsEmpty
                                    | None -> None, None

                                shadowLine
                                    DateTimeOffset.UtcNow
                                    fshw
                                    root
                                    (commit ())
                                    (Some lookup)
                                    evaluated.Length
                                    differences
                                    generatedAgree
                                    None
                            with ex ->
                                shadowLine
                                    DateTimeOffset.UtcNow
                                    fshw
                                    root
                                    None
                                    (match looked with
                                     | Ok l -> Some l
                                     | Error _ -> None)
                                    evaluated.Length
                                    (match looked with
                                     | Ok l when l.Filed.IsSome -> Some []
                                     | _ -> None)
                                    None
                                    (Some(errorText ex))

                        append logPath line
              AfterFiled =
                fun request inputs projects environment ->
                    match file store { request with Root = root } inputs projects environment with
                    | Ok() -> log "options: filed the evaluation portably"
                    | Error why -> log $"options: not filed portably: %s{why}" }
