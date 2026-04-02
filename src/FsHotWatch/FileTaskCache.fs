/// On-disk JSON file-backed task cache for cross-restart persistence.
module FsHotWatch.FileTaskCache

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch.TaskCache
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

let private sanitizeKey = FsHotWatch.StringHelpers.sanitizeFileName

let private severityToString (sev: DiagnosticSeverity) =
    match sev with
    | Error -> "error"
    | Warning -> "warning"
    | Info -> "info"
    | Hint -> "hint"

let private stringToSeverity (s: string) =
    match s with
    | "error" -> Error
    | "warning" -> Warning
    | "info" -> Info
    | "hint" -> Hint
    | _ -> Error

let private serializeErrorEntry (e: ErrorEntry) =
    let obj = JsonObject()
    obj["message"] <- e.Message
    obj["severity"] <- severityToString e.Severity
    obj["line"] <- e.Line
    obj["column"] <- e.Column

    match e.Detail with
    | Some d -> obj["detail"] <- d
    | None -> ()

    obj

let private deserializeErrorEntry (obj: JsonObject) : ErrorEntry =
    { Message = obj["message"].GetValue<string>()
      Severity = obj["severity"].GetValue<string>() |> stringToSeverity
      Line = obj["line"].GetValue<int>()
      Column = obj["column"].GetValue<int>()
      Detail =
        match obj.ContainsKey("detail") with
        | true -> Some(obj["detail"].GetValue<string>())
        | false -> None }

let private serializeStatus (status: PluginStatus) =
    let obj = JsonObject()

    match status with
    | Idle -> obj["type"] <- "idle"
    | Running since ->
        obj["type"] <- "running"
        obj["at"] <- since.ToString("o")
    | Completed at ->
        obj["type"] <- "completed"
        obj["at"] <- at.ToString("o")
    | Failed(msg, at) ->
        obj["type"] <- "failed"
        obj["message"] <- msg
        obj["at"] <- at.ToString("o")

    obj

let private deserializeStatus (obj: JsonObject) : PluginStatus =
    match obj["type"].GetValue<string>() with
    | "idle" -> Idle
    | "running" -> Running(since = DateTime.Parse(obj["at"].GetValue<string>()).ToUniversalTime())
    | "completed" -> Completed(at = DateTime.Parse(obj["at"].GetValue<string>()).ToUniversalTime())
    | "failed" ->
        Failed(
            error = obj["message"].GetValue<string>(),
            at = DateTime.Parse(obj["at"].GetValue<string>()).ToUniversalTime()
        )
    | t -> failwith $"Unknown status type: %s{t}"

let private serializeTestResult (key: string) (result: TestResult) =
    let obj = JsonObject()
    obj["project"] <- key

    match result with
    | TestsPassed output ->
        obj["result"] <- "passed"
        obj["output"] <- output
    | TestsFailed output ->
        obj["result"] <- "failed"
        obj["output"] <- output

    obj

let private deserializeTestResult (obj: JsonObject) : string * TestResult =
    let project = obj["project"].GetValue<string>()

    let result =
        match obj["result"].GetValue<string>() with
        | "passed" -> TestsPassed(obj["output"].GetValue<string>())
        | "failed" -> TestsFailed(obj["output"].GetValue<string>())
        | r -> failwith $"Unknown test result: %s{r}"

    project, result

let private serializeCachedEvent (evt: CachedEvent) =
    let obj = JsonObject()

    match evt with
    | CachedBuildCompleted BuildSucceeded ->
        obj["type"] <- "build"
        obj["result"] <- "succeeded"
    | CachedBuildCompleted(BuildFailed errors) ->
        obj["type"] <- "build"
        obj["result"] <- "failed"
        let arr = JsonArray()

        for e in errors do
            arr.Add(e)

        obj["errors"] <- arr
    | CachedTestCompleted results ->
        obj["type"] <- "test"
        let resultsArr = JsonArray()

        for kvp in results.Results do
            resultsArr.Add(serializeTestResult kvp.Key kvp.Value)

        obj["results"] <- resultsArr
        obj["elapsedMs"] <- results.Elapsed.TotalMilliseconds

    obj

let private deserializeCachedEvent (obj: JsonObject) : CachedEvent =
    match obj["type"].GetValue<string>() with
    | "build" ->
        match obj["result"].GetValue<string>() with
        | "succeeded" -> CachedBuildCompleted BuildSucceeded
        | "failed" ->
            let errors =
                obj["errors"].AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> Seq.toList

            CachedBuildCompleted(BuildFailed errors)
        | r -> failwith $"Unknown build result: %s{r}"
    | "test" ->
        let results =
            obj["results"].AsArray()
            |> Seq.map (fun n -> deserializeTestResult (n.AsObject()))
            |> Map.ofSeq

        let elapsed = TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>())
        CachedTestCompleted { Results = results; Elapsed = elapsed }
    | t -> failwith $"Unknown cached event type: %s{t}"

let private serializeResult (result: TaskCacheResult) =
    let root = JsonObject()
    root["cacheKey"] <- result.CacheKey
    root["status"] <- serializeStatus result.Status

    let errorsArr = JsonArray()

    for file, entries in result.Errors do
        let fileObj = JsonObject()
        fileObj["file"] <- file
        let entriesArr = JsonArray()

        for e in entries do
            entriesArr.Add(serializeErrorEntry e)

        fileObj["entries"] <- entriesArr
        errorsArr.Add(fileObj)

    root["errors"] <- errorsArr

    let eventsArr = JsonArray()

    for evt in result.EmittedEvents do
        eventsArr.Add(serializeCachedEvent evt)

    root["emittedEvents"] <- eventsArr
    root

let private deserializeResult (json: string) : TaskCacheResult =
    let root = JsonNode.Parse(json).AsObject()

    let errors =
        root["errors"].AsArray()
        |> Seq.map (fun n ->
            let obj = n.AsObject()
            let file = obj["file"].GetValue<string>()

            let entries =
                obj["entries"].AsArray()
                |> Seq.map (fun e -> deserializeErrorEntry (e.AsObject()))
                |> Seq.toList

            file, entries)
        |> Seq.toList

    let emittedEvents =
        root["emittedEvents"].AsArray()
        |> Seq.map (fun n -> deserializeCachedEvent (n.AsObject()))
        |> Seq.toList

    { CacheKey = root["cacheKey"].GetValue<string>()
      Errors = errors
      Status = deserializeStatus (root["status"].AsObject())
      EmittedEvents = emittedEvents }

let private hashCacheKey (cacheKey: string) =
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = System.Text.Encoding.UTF8.GetBytes(cacheKey)
    let hash = sha.ComputeHash(bytes)

    hash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
    |> fun s -> s.Substring(0, 12)

/// On-disk task cache. Each entry is a JSON file in the cache directory.
/// Files are named `{compositeKey}@{cacheKeyHash}.json` so multiple versions coexist.
type FileTaskCache(cacheDir: string) =
    do Directory.CreateDirectory(cacheDir) |> ignore

    let filePath compositeKey cacheKey =
        let keyHash = hashCacheKey cacheKey
        Path.Combine(cacheDir, $"%s{sanitizeKey compositeKey}@%s{keyHash}.json")

    let jsonWriteOptions = System.Text.Json.JsonSerializerOptions(WriteIndented = true)

    let tryGet (compositeKey: string) (cacheKey: string) =
        let path = filePath compositeKey cacheKey

        try
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                let result = deserializeResult json
                Some result
            else
                None
        with _ ->
            None

    let set (compositeKey: string) (cacheKey: string) (result: TaskCacheResult) =
        let path = filePath compositeKey cacheKey
        let json = serializeResult result
        File.WriteAllText(path, json.ToJsonString(jsonWriteOptions))

    let clear () =
        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            File.Delete(f)

    let clearPlugin (plugin: string) =
        let prefix = sanitizeKey (plugin + "--")
        let exact = sanitizeKey plugin + "@"

        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            let name = Path.GetFileName(f)

            if name.StartsWith(prefix) || name.StartsWith(exact) then
                File.Delete(f)

    let clearFile (file: string) =
        let suffix = sanitizeKey ("--" + file) + "@"

        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            let name = Path.GetFileName(f)

            if name.Contains(suffix) then
                File.Delete(f)

    let clearPluginFile (plugin: string) (file: string) =
        let prefix = sanitizeKey (plugin + "--" + file) + "@"

        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            let name = Path.GetFileName(f)

            if name.StartsWith(prefix) then
                File.Delete(f)

    /// Try to retrieve a cached result. Returns Some only when the compositeKey
    /// matches AND the stored result's CacheKey matches the provided cacheKey.
    member _.TryGet(compositeKey: string, cacheKey: string) = tryGet compositeKey cacheKey

    /// Store a result under the given compositeKey.
    member _.Set(compositeKey: string, cacheKey: string, result: TaskCacheResult) = set compositeKey cacheKey result

    /// Remove all cached entries.
    member _.Clear() = clear ()

    /// Remove entries for a specific plugin.
    member _.ClearPlugin(plugin: string) = clearPlugin plugin

    /// Remove entries for a specific file.
    member _.ClearFile(file: string) = clearFile file

    /// Remove the specific plugin+file entry.
    member _.ClearPluginFile(plugin: string, file: string) = clearPluginFile plugin file

    interface ITaskCache with
        member _.TryGet compositeKey cacheKey = tryGet compositeKey cacheKey
        member _.Set compositeKey cacheKey result = set compositeKey cacheKey result
        member _.Clear() = clear ()
        member _.ClearPlugin plugin = clearPlugin plugin
        member _.ClearFile file = clearFile file
        member _.ClearPluginFile plugin file = clearPluginFile plugin file
