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

let private stringToSeverity s =
    DiagnosticSeverity.fromString s |> Option.defaultValue DiagnosticSeverity.Error

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

let private serializeStatus (status: CachedStatus) =
    let obj = JsonObject()

    match status with
    | CachedFileCompleted elapsed ->
        obj["type"] <- "fileCompleted"
        obj["elapsedMs"] <- elapsed.TotalMilliseconds
    | CachedFileFailed(msg, elapsed) ->
        obj["type"] <- "fileFailed"
        obj["message"] <- msg
        obj["elapsedMs"] <- elapsed.TotalMilliseconds
    | CachedRunCompleted verdict ->
        obj["type"] <- "runCompleted"
        obj["summary"] <- verdict.Summary
        obj["elapsedMs"] <- verdict.Elapsed.TotalMilliseconds
    | CachedRunFailed(msg, verdict) ->
        obj["type"] <- "runFailed"
        obj["message"] <- msg
        obj["summary"] <- verdict.Summary
        obj["elapsedMs"] <- verdict.Elapsed.TotalMilliseconds

    obj

/// Verdict fields are REQUIRED on both run-entry shapes: an entry with an
/// empty summary (`RunVerdict.create` throws) has no evidence to replay, so it
/// must read as a cache MISS (the throw is caught by `tryGet` and counted as a
/// parse failure), never as a verdict-free terminal.
let private deserializeVerdict (obj: JsonObject) : RunVerdict =
    RunVerdict.create
        (obj["summary"].GetValue<string>())
        (TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>()))

let private deserializeStatus (obj: JsonObject) : CachedStatus =
    match obj["type"].GetValue<string>() with
    | "fileCompleted" -> CachedFileCompleted(TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>()))
    | "fileFailed" ->
        CachedFileFailed(
            obj["message"].GetValue<string>(),
            TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>())
        )
    | "runCompleted" -> CachedRunCompleted(deserializeVerdict obj)
    | "runFailed" -> CachedRunFailed(obj["message"].GetValue<string>(), deserializeVerdict obj)
    | t -> failwith $"Unknown status type: %s{t}"

let private serializeTestResult (key: string) (result: TestResult) =
    let obj = JsonObject()
    obj["project"] <- key

    match result with
    | TestsPassed(output, wasFiltered, elapsed) ->
        obj["result"] <- "passed"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
        obj["elapsedSeconds"] <- elapsed.TotalSeconds
    | TestsFailed(output, wasFiltered, elapsed) ->
        obj["result"] <- "failed"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
        obj["elapsedSeconds"] <- elapsed.TotalSeconds
    | TestsTimedOut(output, after, wasFiltered, elapsed) ->
        obj["result"] <- "timed-out"
        obj["output"] <- output
        obj["wasFiltered"] <- wasFiltered
        obj["timeoutSeconds"] <- after.TotalSeconds
        obj["elapsedSeconds"] <- elapsed.TotalSeconds
    | TestsDeferred reason ->
        obj["result"] <- "deferred"
        // Stored under `output` so the back-compat deserializer's `output`
        // read finds it without a special case.
        obj["output"] <- reason
    | TestsErrored reason ->
        // In practice an errored result is never written: it is non-passing, so
        // the cacheKey gate (`allPassed`) returns None and `runAndCache` skips
        // the write — an "I don't know" verdict must never be replayed as a
        // verdict. Serialized here only for match exhaustiveness / robustness.
        obj["result"] <- "errored"
        obj["output"] <- reason

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

    // elapsedSeconds is optional for back-compat with caches written before
    // the field existed; default to TimeSpan.Zero (no recorded duration).
    let elapsed =
        if obj.ContainsKey("elapsedSeconds") then
            let node = obj["elapsedSeconds"]

            if isNull node then
                TimeSpan.Zero
            else
                TimeSpan.FromSeconds(node.GetValue<float>())
        else
            TimeSpan.Zero

    let result =
        match obj["result"].GetValue<string>() with
        | "passed" -> TestsPassed(output, wasFiltered, elapsed)
        | "failed" -> TestsFailed(output, wasFiltered, elapsed)
        | "timed-out" ->
            let secs =
                if obj.ContainsKey("timeoutSeconds") then
                    obj["timeoutSeconds"].GetValue<float>()
                else
                    0.0

            TestsTimedOut(output, TimeSpan.FromSeconds secs, wasFiltered, elapsed)
        | "deferred" -> TestsDeferred output
        | "errored" -> TestsErrored output
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

/// On-disk entry format version. Bumped to 2 by AUTOMATION-186: per-file
/// entries no longer store a status summary or timestamp (`CachedStatus`
/// replaced the cached `PluginStatus`). The version is REQUIRED on read —
/// entries written by any other format (including pre-versioned ones, where
/// the field is absent) deterministically read as a cache MISS (the throw is
/// caught by `tryGet` and counted as a parse failure), never as a half-parsed
/// result carrying a claim the new scope rule forbids.
[<Literal>]
let private EntryFormatVersion = 2

let private serializeResult (result: TaskCacheResult) =
    let root = JsonObject()
    root["format"] <- EntryFormatVersion
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

    let formatVersion =
        match root["format"] with
        | null -> 0 // pre-versioned entry (format 1 had no field)
        | node -> node.GetValue<int>()

    if formatVersion <> EntryFormatVersion then
        failwith
            $"task-cache entry format %d{formatVersion} (expected %d{EntryFormatVersion}) — stale entry, read as miss"

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

/// Snapshot of on-disk cache size for §2c telemetry.
[<Struct>]
type CacheStats = { EntryCount: int; SizeBytes: int64 }

/// The on-disk name of the key an entry file belongs to — everything before the
/// `@{contentHash}` suffix. `LastIndexOf`, because `sanitizeKey` does not strip
/// `@` and a source path may legitimately contain one (`/src/@types/x.fs`); the
/// hash is always the LAST `@`-delimited segment.
let private entryKeyOfFileName (fileName: string) =
    match fileName.LastIndexOf('@') with
    | i when i > 0 -> Some(fileName.Substring(0, i))
    | _ -> None

/// Delete `superseded` — the entry paths this key was known at before the write
/// that just landed.
///
/// AUTOMATION-98: entries are named `{plugin--file}@{contentHash}.json` so that
/// "multiple versions coexist" — but nothing ever removed the predecessors, and only
/// the entry whose hash matches the CURRENT content can ever be READ (`tryGet`
/// reconstructs the exact path from the key). So every edit to a file permanently
/// added a dead, unreachable sibling: 3,126 files / 13 MB measured in a ~1.5-day-old
/// workspace, across ~6 live workspaces — while `Stats`/`clearFile`/`clearPlugin`
/// each full-scan the directory and degrade right along with it.
///
/// No LRU is needed, and an LRU would be the wrong tool: it would retain entries
/// that are not merely cold but UNREACHABLE. Only the newest content-hash entry for
/// a plugin+file is ever useful, so on write we collect the rest.
///
/// The paths are HANDED to it. The first cut of this found them by re-listing the
/// cache directory on every `set`, and `EnumerateFiles(dir, pattern)` is not a
/// prefix-optimised syscall — it readdirs the WHOLE directory and pattern-matches in
/// managed code. A cold scan writes ~3 entries per file (Lint, Analyzers,
/// FormatCheck each carry a per-`FileChecked` cache key) into a directory that grows
/// to ~3 entries per file, each write scanning the lot: quadratic, and measured at
/// ~2.2 ms per scan against a 4,500-entry directory — roughly TEN SECONDS of pure
/// directory scanning added to a cold scan of a 1500-file repo, on exactly the paths
/// (cold scan, `--run-once`, `confirm`) that were already timing out. The
/// writer already knows the path it just wrote and the cache remembers the one it
/// wrote last, so no scan is needed to name the superseded siblings.
///
/// Never propagates, and each delete is independently guarded: a cache-hygiene
/// failure must not fail the task whose result we just successfully wrote, nor stop
/// us collecting the siblings it did not trip over. Whatever this call fails to
/// collect, the next write to the same key will.
let internal pruneSupersededSiblings (superseded: string list) (keepPath: string) : unit =
    for f in superseded do
        if not (String.Equals(f, keepPath, StringComparison.Ordinal)) then
            try
                File.Delete f
            with _ ->
                ()

/// On-disk task cache. Each entry is a JSON file in the cache directory, named
/// `{compositeKey}@{cacheKeyHash}.json`. Only the newest hash per key survives a
/// write (see `pruneSupersededSiblings`).
type FileTaskCache(cacheDir: string) =
    do Directory.CreateDirectory(cacheDir) |> ignore

    // Counts FULL-DIRECTORY enumerations performed by this instance. The write path
    // must perform ZERO of them (see `pruneSupersededSiblings`); the constructor's
    // two sweeps and the explicit `Clear*`/`Stats` operations are the only legitimate
    // ones. Asserted on directly by a test — a `set` that scans is the quadratic bug.
    let mutable directoryScanCount = 0

    let enumerateEntries (pattern: string) =
        System.Threading.Interlocked.Increment(&directoryScanCount) |> ignore
        Directory.EnumerateFiles(cacheDir, pattern)

    // Sweep orphan *.tmp files left from prior process crashes mid-write.
    do
        for f in enumerateEntries "*.tmp" do
            try
                File.Delete(f)
            with _ ->
                ()

    /// The entry path each key was last written to, so a write can name its own
    /// superseded siblings without asking the filesystem.
    ///
    /// Seeded — ONCE, at construction, in the same shape as the `*.tmp` sweep above —
    /// from whatever previous processes left on disk, so their leftovers are still
    /// collected by the first write to the key that owns them. A remembered path that
    /// something else has already deleted costs nothing: `File.Delete` on a missing
    /// file is a no-op, so the memo is allowed to be stale, never wrong.
    let livePathsLock = obj ()
    let livePaths = System.Collections.Generic.Dictionary<string, string list>()

    do
        enumerateEntries "*.json"
        |> Seq.iter (fun f ->
            match entryKeyOfFileName (Path.GetFileName f) with
            // Not an entry of ours — a stray `.json` with no `@{hash}` suffix. It is
            // not reachable through any key, so it is not ours to remember, and not
            // ours to collect either.
            | None -> ()
            | Some key ->
                livePaths[key] <-
                    match livePaths.TryGetValue key with
                    | true, existing -> f :: existing
                    | false, _ -> [ f ])

    let entryKey (compositeKey: CompositeKey) =
        sanitizeKey (compositeKeyToString compositeKey)

    /// Make `path` the key's only live entry and return what it displaces. Atomic
    /// against a concurrent write to the SAME key: exactly one of the two writers
    /// sees the other's path as superseded, so a live entry can never be collected
    /// by the writer that did not displace it.
    let claimLatest (key: string) (path: string) : string list =
        lock livePathsLock (fun () ->
            let superseded =
                match livePaths.TryGetValue key with
                | true, existing -> existing
                | false, _ -> []

            livePaths[key] <- [ path ]
            superseded)

    let forgetAll () =
        lock livePathsLock (fun () -> livePaths.Clear())

    let filePath (compositeKey: CompositeKey) cacheKey =
        let keyHash = hashCacheKey cacheKey
        Path.Combine(cacheDir, $"%s{entryKey compositeKey}@%s{keyHash}.json")

    let jsonWriteOptions = System.Text.Json.JsonSerializerOptions(WriteIndented = true)

    // Counts read attempts that found a file but failed to parse it (corrupt or stale-format).
    // Telemetry for §2b measurement A: confirms the rate of corruption-style failures.
    let mutable parseFailureCount = 0

    let tryGet (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        let path = filePath compositeKey cacheKey

        if not (File.Exists path) then
            None
        else
            try
                let json = File.ReadAllText(path)
                let result = deserializeResult json

                if result.CacheKey = cacheKey then Some result else None
            with _ ->
                System.Threading.Interlocked.Increment(&parseFailureCount) |> ignore
                None

    let set (compositeKey: CompositeKey) (cacheKey: ContentHash) (result: TaskCacheResult) =
        let path = filePath compositeKey cacheKey
        let json = serializeResult result
        FsHwPaths.atomicWriteAllText path (json.ToJsonString(jsonWriteOptions))
        // AFTER the write, so a crash mid-set can never leave the key with NO entry.
        // The claim comes after it too: nothing may be named superseded until its
        // successor is durable.
        pruneSupersededSiblings (claimLatest (entryKey compositeKey) path) path

    let clear () =
        for f in enumerateEntries "*.json" do
            File.Delete(f)

        forgetAll ()

    let clearPlugin (plugin: string) =
        let prefix = sanitizeKey (plugin + "--")
        let exact = sanitizeKey plugin + "@"

        for f in enumerateEntries "*.json" do
            let name = Path.GetFileName(f)

            if name.StartsWith(prefix) || name.StartsWith(exact) then
                File.Delete(f)

    let clearFile (file: string) =
        let suffix = sanitizeKey ("--" + file)

        for f in enumerateEntries "*.json" do
            let name = Path.GetFileName(f)
            let atIdx = name.IndexOf('@')

            if atIdx > 0 && name.Substring(0, atIdx).EndsWith(suffix) then
                File.Delete(f)

    let clearPluginFile (plugin: string) (file: string) =
        let prefix = sanitizeKey (plugin + "--" + file) + "@"

        for f in enumerateEntries "*.json" do
            let name = Path.GetFileName(f)

            if name.StartsWith(prefix) then
                File.Delete(f)

    /// Total entry count and byte size of cache files in `cacheDir`. §2c
    /// telemetry: log on daemon startup to set future LRU thresholds from data.
    member _.Stats =
        let mutable count = 0
        let mutable bytes = 0L

        for f in enumerateEntries "*.json" do
            count <- count + 1
            bytes <- bytes + FileInfo(f).Length

        { EntryCount = count
          SizeBytes = bytes }

    /// Number of read attempts that found a file but couldn't deserialise it.
    /// §2b measurement A: a non-zero value indicates corruption (e.g. crash mid-write
    /// before the atomic-rename change shipped) or a stale on-disk format.
    member _.ParseFailureCount = parseFailureCount

    /// How many times this cache has enumerated its whole directory. The write path
    /// must never do it: a `set` that scans the directory turns a cold scan into a
    /// quadratic one (see `pruneSupersededSiblings`). Internal — this is a probe for
    /// the test that holds that line, not part of the cache's contract.
    member internal _.DirectoryScanCount = directoryScanCount

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
