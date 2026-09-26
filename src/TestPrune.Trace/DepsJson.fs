/// Recorder injection into a test app's `.deps.json`. The woven assemblies reference the
/// recorder; the default load context resolves only what deps.json lists, so without this
/// every woven test fails with FileNotFoundException.
module TestPrune.Trace.DepsJson

open System.Text.Json
open System.Text.Json.Nodes

/// The recorder's assembly (and library) name.
[<Literal>]
let RecorderName = "TestPrune.Trace.Recorder"

/// Prefix of the Error an app that lists another recorder version gets; the found version follows.
[<Literal>]
let SkewPrefix = "recorder-version-skew:"

let private libraryEntry () =
    let o = JsonObject()
    o.["type"] <- JsonValue.Create "project"
    o.["serviceable"] <- JsonValue.Create false
    o.["sha512"] <- JsonValue.Create ""
    o

let private targetEntry () =
    let runtime = JsonObject()
    runtime.[RecorderName + ".dll"] <- JsonObject()
    let o = JsonObject()
    o.["runtime"] <- runtime
    o

/// The `type` of the recorder's library entry (`project` for an app that builds the
/// recorder itself, `package` for one that references it from a feed); `None` when the
/// app does not list the recorder or the JSON is unreadable (`injectRecorder` reports that).
let recorderLibraryType (depsJson: string) : string option =
    try
        JsonNode.Parse(depsJson).["libraries"].AsObject()
        |> Seq.tryFind (fun kv -> kv.Key.StartsWith(RecorderName + "/"))
        |> Option.map (fun kv -> kv.Value.["type"].GetValue<string>())
    with _ ->
        None

/// Add the recorder as a `project` library the app depends on. Returns (json, changed):
/// unchanged when it is already listed at `recorderVersion`. An app listing the recorder
/// at a DIFFERENT version is refused (`SkewPrefix` + that version): the woven IL
/// references this weaver's recorder.
let injectRecorder (depsJson: string) (appName: string) (recorderVersion: string) : Result<string * bool, string> =
    try
        let root = JsonNode.Parse(depsJson).AsObject()
        let libraries = root.["libraries"].AsObject()
        let key = RecorderName + "/" + recorderVersion

        match libraries |> Seq.tryFind (fun kv -> kv.Key.StartsWith(RecorderName + "/")) with
        | Some kv when kv.Key = key -> Ok(depsJson, false)
        | Some kv -> Error(SkewPrefix + kv.Key.Substring(RecorderName.Length + 1))
        | None ->
            let targetName = root.["runtimeTarget"].["name"].GetValue<string>()
            let target = root.["targets"].[targetName].AsObject()

            let app =
                (target |> Seq.find (fun kv -> kv.Key.StartsWith(appName + "/"))).Value.AsObject()

            if not (app.ContainsKey "dependencies") then
                app.["dependencies"] <- JsonObject()

            app.["dependencies"].AsObject().[RecorderName] <- JsonValue.Create recorderVersion
            target.[key] <- targetEntry ()
            libraries.[key] <- libraryEntry ()
            Ok(root.ToJsonString(JsonSerializerOptions(WriteIndented = true)), true)
    with ex ->
        Error ex.Message
