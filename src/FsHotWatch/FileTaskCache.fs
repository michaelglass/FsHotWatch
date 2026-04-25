/// On-disk JSON file-backed task cache for cross-restart persistence.
module FsHotWatch.FileTaskCache

open System
open System.IO
open System.Text.Json.Nodes
open FsHotWatch.TaskCache
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

let private sanitizeKey = FsHotWatch.StringHelpers.sanitizeFileName

let private severityToString = DiagnosticSeverity.toString
let private stringToSeverity = DiagnosticSeverity.fromString

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
    | TestsPassed(output, wasFiltered) ->
        obj["result"] <- "passed"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
    | TestsFailed(output, wasFiltered) ->
        obj["result"] <- "failed"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
    | TestsTimedOut(output, after, wasFiltered) ->
        obj["result"] <- "timed-out"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
        obj["timeoutSeconds"] <- after.TotalSeconds

    obj

let private deserializeTestResult (obj: JsonObject) : string * TestResult =
    let project = obj["project"].GetValue<string>()

    let output = obj["output"].GetValue<string>()

    // wasFiltered is optional for backward compatibility with caches written
    // before the field existed; default to false (full run).
    let wasFiltered =
        if obj.ContainsKey("wasFiltered") then
            let node = obj["wasFiltered"]

            if isNull node then false else node.GetValue<bool>()
        else
            false

    let result =
        match obj["result"].GetValue<string>() with
        | "passed" -> TestsPassed(output, wasFiltered)
        | "failed" -> TestsFailed(output, wasFiltered)
        | "timed-out" ->
            let secs =
                if obj.ContainsKey("timeoutSeconds") then
                    obj["timeoutSeconds"].GetValue<float>()
                else
                    0.0

            TestsTimedOut(output, TimeSpan.FromSeconds secs, wasFiltered)
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
    | CachedTestRunStarted started ->
        obj["type"] <- "testRunStarted"
        obj["runId"] <- string<System.Guid> started.RunId
        obj["startedAt"] <- started.StartedAt.ToString("o")
    | CachedTestProgress progress ->
        obj["type"] <- "testProgress"
        obj["runId"] <- string<System.Guid> progress.RunId
        let resultsArr = JsonArray()

        for kvp in progress.NewResults do
            resultsArr.Add(serializeTestResult kvp.Key kvp.Value)

        obj["newResults"] <- resultsArr
    | CachedTestRunCompleted completed ->
        obj["type"] <- "testRunCompleted"
        obj["runId"] <- string<System.Guid> completed.RunId
        obj["elapsedMs"] <- completed.TotalElapsed.TotalMilliseconds

        match completed.Outcome with
        | Normal -> obj["outcome"] <- "normal"
        | Aborted reason ->
            obj["outcome"] <- "aborted"
            obj["abortReason"] <- reason

        let resultsArr = JsonArray()

        for kvp in completed.Results do
            resultsArr.Add(serializeTestResult kvp.Key kvp.Value)

        obj["results"] <- resultsArr
        obj["ranFullSuite"] <- completed.RanFullSuite
    | CachedCommandCompleted result ->
        obj["type"] <- "command"
        obj["name"] <- result.Name

        match result.Outcome with
        | CommandSucceeded output ->
            obj["succeeded"] <- true
            obj["output"] <- output
        | CommandFailed output ->
            obj["succeeded"] <- false
            obj["output"] <- output

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
    | "testRunStarted" ->
        let runId = System.Guid.Parse(obj["runId"].GetValue<string>())
        let startedAt = System.DateTime.Parse(obj["startedAt"].GetValue<string>())
        CachedTestRunStarted { RunId = runId; StartedAt = startedAt }
    | "testProgress" ->
        let runId = System.Guid.Parse(obj["runId"].GetValue<string>())

        let newResults =
            obj["newResults"].AsArray()
            |> Seq.map (fun n -> deserializeTestResult (n.AsObject()))
            |> Map.ofSeq

        CachedTestProgress
            { RunId = runId
              NewResults = newResults }
    | "testRunCompleted" ->
        let runId = System.Guid.Parse(obj["runId"].GetValue<string>())
        let elapsed = TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>())

        let outcome =
            match obj["outcome"].GetValue<string>() with
            | "normal" -> Normal
            | "aborted" -> Aborted(obj["abortReason"].GetValue<string>())
            | o -> failwith $"Unknown TestRunOutcome: %s{o}"

        let results =
            obj["results"].AsArray()
            |> Seq.map (fun n -> deserializeTestResult (n.AsObject()))
            |> Map.ofSeq

        let ranFullSuite =
            match obj["ranFullSuite"] with
            | null -> true
            | node -> node.GetValue<bool>()

        CachedTestRunCompleted
            { RunId = runId
              TotalElapsed = elapsed
              Outcome = outcome
              Results = results
              RanFullSuite = ranFullSuite }
    | "command" ->
        let name = obj["name"].GetValue<string>()
        let output = obj["output"].GetValue<string>()

        let outcome =
            if obj["succeeded"].GetValue<bool>() then
                CommandSucceeded output
            else
                CommandFailed output

        CachedCommandCompleted { Name = name; Outcome = outcome }
    | t -> failwith $"Unknown cached event type: %s{t}"

let private serializeResult (result: TaskCacheResult) =
    let root = JsonObject()
    root["cacheKey"] <- ContentHash.value result.CacheKey
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

    { CacheKey = ContentHash.create (root["cacheKey"].GetValue<string>())
      Errors = errors
      Status = deserializeStatus (root["status"].AsObject())
      EmittedEvents = emittedEvents }

let private hashCacheKey (cacheKey: ContentHash) =
    (FsHotWatch.CheckCache.sha256Hex (ContentHash.value cacheKey)).Substring(0, 12)

/// Serialize a CompositeKey to a file-safe string.
let private compositeKeyToString (key: CompositeKey) =
    match key.File with
    | Some file -> $"%s{key.Plugin}--%s{file}"
    | None -> key.Plugin

/// On-disk task cache. Each entry is a JSON file in the cache directory.
/// Files are named `{compositeKey}@{cacheKeyHash}.json` so multiple versions coexist.
type FileTaskCache(cacheDir: string) =
    do Directory.CreateDirectory(cacheDir) |> ignore

    let filePath (compositeKey: CompositeKey) cacheKey =
        let keyHash = hashCacheKey cacheKey
        Path.Combine(cacheDir, $"%s{sanitizeKey (compositeKeyToString compositeKey)}@%s{keyHash}.json")

    let jsonWriteOptions = System.Text.Json.JsonSerializerOptions(WriteIndented = true)

    let tryGet (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        let path = filePath compositeKey cacheKey

        try
            let json = File.ReadAllText(path)
            let result = deserializeResult json

            if result.CacheKey = cacheKey then Some result else None
        with _ ->
            None

    let set (compositeKey: CompositeKey) (cacheKey: ContentHash) (result: TaskCacheResult) =
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
        let suffix = sanitizeKey ("--" + file)

        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            let name = Path.GetFileName(f)
            let atIdx = name.IndexOf('@')

            if atIdx > 0 && name.Substring(0, atIdx).EndsWith(suffix) then
                File.Delete(f)

    let clearPluginFile (plugin: string) (file: string) =
        let prefix = sanitizeKey (plugin + "--" + file) + "@"

        for f in Directory.EnumerateFiles(cacheDir, "*.json") do
            let name = Path.GetFileName(f)

            if name.StartsWith(prefix) then
                File.Delete(f)

    /// Try to retrieve a cached result.
    member _.TryGet(compositeKey: CompositeKey, cacheKey: ContentHash) = tryGet compositeKey cacheKey

    /// Store a result under the given compositeKey.
    member _.Set(compositeKey: CompositeKey, cacheKey: ContentHash, result: TaskCacheResult) =
        set compositeKey cacheKey result

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
