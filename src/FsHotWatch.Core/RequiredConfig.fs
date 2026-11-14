/// What a `.fshw.json` requires of a green: the plugins whose outcome the verdict needs, and the
/// test projects (AUTOMATION-1695, Phase 1 step C). THE one definition of "which plugins are
/// required": the daemon's `DaemonConfig.requiredPluginNames` calls it on the same raw JSON, and
/// R(c, T) (`Requirements.Configured`) is built from it, in the daemon and in a pure lookup alike.
///
/// It reads only what decides the two lists. `DaemonConfig.parseConfig` still parses and validates
/// each plugin's settings; a configuration it refuses is refused before these lists are asked for.
/// `RequiredConfigParityTests` holds the two together: the plugins the daemon CONFIGURES for a
/// configuration are exactly the ones required here.
module FsHotWatch.RequiredConfig

open System.Text.Json

/// The analyzers plugin's name, as it names itself.
[<Literal>]
let AnalyzersPluginName = "analyzers"

/// The test plugin's name, as it names itself.
[<Literal>]
let TestPrunePluginName = "test-prune"

/// What a configuration requires.
type Required =
    {
        /// The plugins whose outcome a green needs, in registration order: preprocessors, the
        /// formatter, lint, the analyzers, the build, file commands, coverage, then the tests.
        Plugins: string list
        /// The configured test projects, by the name their reports carry.
        TestProjects: string list
        /// The globs the tree hash leaves out (`exclude`): they decide which tree is being graded.
        Exclude: string list
    }

let private property (el: JsonElement) (name: string) : JsonElement option =
    match el.ValueKind with
    | JsonValueKind.Object ->
        match el.TryGetProperty name with
        | true, v -> Some v
        | false, _ -> None
    | _ -> None

let private stringOf (el: JsonElement) (name: string) : string option =
    property el name
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.String -> Some(v.GetString())
        | _ -> None)

let private array (el: JsonElement) : JsonElement list =
    match el.ValueKind with
    | JsonValueKind.Array -> el.EnumerateArray() |> List.ofSeq
    | _ -> []

/// The formatter's plugin: `format` (auto, the default), `format-check`, or none.
let private formatPlugin (root: JsonElement) : string list =
    match property root "format" with
    | Some v when v.ValueKind = JsonValueKind.String ->
        match v.GetString().ToLowerInvariant() with
        | "check" -> [ "format-check" ]
        | "off"
        | "false" -> []
        | _ -> [ "format" ]
    | Some v when v.ValueKind = JsonValueKind.False -> []
    | Some _
    | None -> [ "format" ]

/// Lint runs unless `"lint": false`.
let private lintPlugin (root: JsonElement) : string list =
    match property root "lint" with
    | Some v when v.ValueKind = JsonValueKind.False -> []
    | Some _
    | None -> [ "lint" ]

/// The analyzers run when they name at least one path.
let private analyzersPlugin (root: JsonElement) : string list =
    match property root "analyzers" |> Option.bind (fun a -> property a "paths") with
    | Some paths when not (array paths).IsEmpty -> [ AnalyzersPluginName ]
    | Some _
    | None -> []

/// A build runs unless `"build": false` or an empty list; absent, the default build runs.
let private buildPlugin (root: JsonElement) : string list =
    match property root "build" with
    | Some v when v.ValueKind = JsonValueKind.False -> []
    | Some v when v.ValueKind = JsonValueKind.Array && (array v).IsEmpty -> []
    | Some _
    | None -> [ "build" ]

/// Each file command, by its `name`, else `file-cmd-<pattern>`.
let private fileCommandPlugins (root: JsonElement) : string list =
    match property root "fileCommands" with
    | Some commands ->
        [ for fc in array commands do
              match stringOf fc "name", stringOf fc "pattern" with
              | Some name, _ -> name
              | None, Some pattern -> $"file-cmd-{pattern}"
              | None, None -> () ]
    | None -> []

/// Coverage runs when it names a floor set, or a non-empty array of them.
let private coveragePlugin (root: JsonElement) : string list =
    match property root "coverage" with
    | Some v when v.ValueKind = JsonValueKind.Object -> [ "coverage" ]
    | Some v when v.ValueKind = JsonValueKind.Array && not (array v).IsEmpty -> [ "coverage" ]
    | Some _
    | None -> []

/// The `exclude` globs, verbatim; none when absent or not an array.
let private excludeOf (root: JsonElement) : string list =
    match property root "exclude" with
    | Some v -> [ for e in array v -> e.GetString() ]
    | None -> []

/// The configured test projects, by name.
let private testProjectsOf (root: JsonElement) : string list =
    match property root "tests" |> Option.bind (fun t -> property t "projects") with
    | Some projects -> [ for p in array projects -> stringOf p "project" ] |> List.choose id
    | None -> []

/// What `json`, a `.fshw.json`'s text, requires. Raises `JsonException` for text that is not
/// JSON; the daemon's parser has refused anything else invalid before this is asked.
let ofJson (json: string) : Required =
    use doc = JsonDocument.Parse json
    let root = doc.RootElement

    let preprocessors =
        match property root "preprocessors" with
        | Some entries -> [ for p in array entries -> stringOf p "name" ] |> List.choose id
        | None -> []

    let projects = testProjectsOf root

    { Plugins =
        preprocessors
        @ formatPlugin root
        @ lintPlugin root
        @ analyzersPlugin root
        @ buildPlugin root
        @ fileCommandPlugins root
        @ coveragePlugin root
        @ (if projects.IsEmpty then [] else [ TestPrunePluginName ])
        |> List.distinct
      TestProjects = projects
      Exclude = excludeOf root }

/// What a repository without a `.fshw.json` requires: the defaults.
let defaults: Required = ofJson "{}"
