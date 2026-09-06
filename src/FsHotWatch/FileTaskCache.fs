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

let private serializeVerdict (obj: JsonObject) (verdict: RunVerdict) =
    obj["summary"] <- verdict.Summary
    obj["elapsedMs"] <- verdict.Elapsed.TotalMilliseconds

    match verdict.NothingVerified with
    | Some detail -> obj["nothingVerified"] <- detail
    | None -> ()

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
        serializeVerdict obj verdict
    | CachedRunFailed(msg, verdict) ->
        obj["type"] <- "runFailed"
        obj["message"] <- msg
        serializeVerdict obj verdict

    obj

/// Verdict fields are REQUIRED on both run-entry shapes: an entry with an empty
/// summary (`RunVerdict.create` throws) has no evidence to replay, so it must read as
/// a cache MISS, never as a verdict-free terminal.
///
/// `nothingVerified` is written only for a verdict that carries it, and its presence
/// is what rebuilds the verdict through `RunVerdict.verifiedNothing`: a replayed
/// verified-nothing run verified exactly as much as the original did, and the fact
/// must survive the round trip as a VALUE — the summary's words are display, and no
/// reader parses them (AUTOMATION-339). An entry written before the field existed is
/// refused by the format version below, never read as a verified run.
let private deserializeVerdict (obj: JsonObject) : RunVerdict =
    let elapsed = TimeSpan.FromMilliseconds(obj["elapsedMs"].GetValue<float>())

    match obj["nothingVerified"] with
    | null -> RunVerdict.create (obj["summary"].GetValue<string>()) elapsed
    | detail -> RunVerdict.verifiedNothing (detail.GetValue<string>()) elapsed

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
        // Never written in practice: it is non-passing, so the cacheKey gate
        // (`allPassed`) returns None and `runAndCache` skips the write. Serialized
        // only for exhaustiveness.
        obj["result"] <- "errored"
        obj["output"] <- reason
    | TestsNoMatch(output, elapsed) ->
        // Its own stored tag, so a replayed entry comes back as the case it was
        // written as. Entries predating this tag are reconstructed by the legacy read
        // in `deserializeTestResult`.
        //
        // Also rare: an all-zero-match run is not cacheable, so only a MIXED run — one
        // project matched nothing, another really ran — reaches the write.
        obj["result"] <- "no-match"
        obj["output"] <- output
        obj["elapsedSeconds"] <- elapsed.TotalSeconds

    obj

let private deserializeTestResult (obj: JsonObject) : string * TestResult =
    let project = obj["project"].GetValue<string>()

    let output = obj["output"].GetValue<string>()

    // `wasFiltered` is read inline below, not here: only the shapes whose WRITER emits
    // it may demand it. `TestsPassed`/`TestsFailed`/`TestsTimedOut` carry the flag and
    // always serialize it; `TestsNoMatch`/`TestsDeferred`/`TestsErrored` do not carry
    // it at all, so its absence THERE must stay readable.
    //
    // On the shapes that do carry it, absence must not default to `false`:
    // FileCommandPlugin derives the run scope from these results on the progress path,
    // so that would launder a filtered run into a full suite. `.GetValue<bool>()` on an
    // absent or null node throws, `tryGet` catches it, and the entry reads as a cache
    // MISS — the throw-to-miss contract every required field here relies on.

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
        // Back-compat, and it must come BEFORE the plain "passed" read. Entries written
        // before `TestsNoMatch` existed stored a zero match as `"passed"` with the
        // marker embedded in `output`; reading one back as a plain pass would replay a
        // run that executed no test as a genuine green. Reconstruct the case and strip
        // the marker so the surfaced output matches a fresh run.
        | "passed" when output.StartsWith(TestResult.LegacyZeroMatchMarker, StringComparison.Ordinal) ->
            TestsNoMatch(output.Substring(TestResult.LegacyZeroMatchMarker.Length), elapsed)
        | "passed" -> TestsPassed(output, obj["wasFiltered"].GetValue<bool>(), elapsed)
        | "no-match" -> TestsNoMatch(output, elapsed)
        | "failed" -> TestsFailed(output, obj["wasFiltered"].GetValue<bool>(), elapsed)
        | "timed-out" ->
            let secs =
                if obj.ContainsKey("timeoutSeconds") then
                    obj["timeoutSeconds"].GetValue<float>()
                else
                    0.0

            TestsTimedOut(output, TimeSpan.FromSeconds secs, obj["wasFiltered"].GetValue<bool>(), elapsed)
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
        // A token, not a bool. `RunVerification.token` is the only writer and `tryParse`
        // the only reader, so an entry whose token this build cannot interpret reads as
        // a MISS rather than as some default verdict.
        obj["verification"] <- RunVerification.token completed.Verification
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

        // Required on read, and parsed rather than defaulted: no value here is an honest
        // reading of "unknown". An absent field throws on `.GetValue<string>()`; a token
        // written by a NEWER build yields `None`. Both read as a cache MISS and the run
        // is redone — failing closed on a token from the future is deliberate.
        let verification =
            match RunVerification.tryParse (obj["verification"].GetValue<string>()) with
            | Some v -> v
            | None -> failwith "task-cache testRunCompleted has an uninterpretable verification token"

        CachedTestRunCompleted
            { RunId = runId
              TotalElapsed = elapsed
              Outcome = outcome
              Results = results
              Verification = verification }
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

/// On-disk entry format version. Required on read: an entry written by any other
/// format (including pre-versioned ones, where the field is absent) reads as a cache
/// MISS, never as a half-parsed result carrying a claim the current scope rule
/// forbids. Bump this whenever the entry schema changes — otherwise a stale entry is
/// rejected one layer deeper by a field read throwing, which reports every stale
/// entry as a parse FAILURE. The cost of a bump is a one-time re-run.
[<Literal>]
let private EntryFormatVersion = 5

/// The marker a run writes when it cleared its WHOLE ledger. Not a path, so it is
/// exempt from path encoding on the way out and on the way back in.
[<Literal>]
let private ClearAllMarker = "*"

/// Encode an error's file path so the entry can be read in ANOTHER checkout of the
/// same repository. Paths inside the repo become `repo:`-relative; anything outside
/// stays explicitly machine-local and therefore cannot be rebound elsewhere — which
/// is the honest answer, not a limitation to route around.
let private encodeErrorPath (repoRoot: string option) (file: string) =
    if file = ClearAllMarker then
        file
    else
        match repoRoot with
        | Some root -> CachePathIdentity.ofPath root file |> CachePathIdentity.toKey
        | None -> CachePathIdentity.toKey (CachePathIdentity.ExternalAbsolute file)

/// Resolve an encoded error path against THIS checkout. Returns None when the entry
/// cannot honestly be replayed here — an unparseable encoding, or a repo-relative
/// path with no repo root to rebind it against. The caller turns that into a cache
/// MISS: a replay that reported findings against another workspace's files would be
/// worse than recomputing.
let private decodeErrorPath (repoRoot: string option) (encoded: string) =
    if encoded = ClearAllMarker then
        Some encoded
    else
        match CachePathIdentity.tryParse encoded with
        | Some(CachePathIdentity.ExternalAbsolute absolute) -> Some absolute
        | Some(CachePathIdentity.RepoRelative _ as identity) ->
            repoRoot |> Option.bind (fun root -> CachePathIdentity.tryRebind root identity)
        | None -> None

let private serializeResult (repoRoot: string option) (result: TaskCacheResult) =
    let root = JsonObject()
    root["format"] <- EntryFormatVersion
    root["cacheKey"] <- ContentHash.value result.CacheKey
    root["status"] <- serializeStatus result.Status

    // The labelled digests behind this key, when this process minted it. Persisted so
    // a LATER lookup that misses can name the input that moved — including a lookup
    // from a different workspace, which is the case this whole store exists for.
    match FsHotWatch.TaskCache.KeyFingerprints.tryGet (ContentHash.value result.CacheKey) with
    | Some inputs ->
        let inputsObj = JsonObject()

        for (label, digest) in inputs do
            inputsObj[label] <- digest

        root["keyInputs"] <- inputsObj
    | None -> ()

    let errorsArr = JsonArray()

    for file, entries in result.Errors do
        let fileObj = JsonObject()
        fileObj["file"] <- encodeErrorPath repoRoot file
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

/// Read an entry. Returns the result AND the labelled key-input digests it carries
/// (empty when the writer could not record them), so a caller holding a MISS can
/// explain it.
///
/// Throws on anything it cannot honestly reproduce here — including an error path
/// that will not rebind into this checkout. Every caller turns a throw into a MISS.
let private deserializeEntry (repoRoot: string option) (json: string) : TaskCacheResult * (string * string) list =
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

            let file =
                let encoded = obj["file"].GetValue<string>()

                match decodeErrorPath repoRoot encoded with
                | Some path -> path
                | None ->
                    failwith $"task-cache entry names a file that cannot be resolved in this checkout: %s{encoded}"

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

    let keyInputs =
        match root["keyInputs"] with
        | null -> []
        | node ->
            node.AsObject()
            |> Seq.map (fun kvp -> kvp.Key, kvp.Value.GetValue<string>())
            |> Seq.toList

    { CacheKey = ContentHash.create (root["cacheKey"].GetValue<string>())
      Errors = errors
      Status = deserializeStatus (root["status"].AsObject())
      EmittedEvents = emittedEvents },
    keyInputs

let private hashCacheKey (cacheKey: ContentHash) =
    (FsHotWatch.CheckCache.sha256Hex (ContentHash.value cacheKey)).Substring(0, 12)

/// Serialize a CompositeKey to a file-safe string.
let private compositeKeyToString (key: CompositeKey) =
    match key.File with
    | Some file -> $"%s{key.Plugin}--%s{file}"
    | None -> key.Plugin

/// Snapshot of on-disk cache size.
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
/// Entries are named `{plugin--file}@{contentHash}.json` so that multiple versions
/// coexist — but only the entry whose hash matches the CURRENT content can ever be
/// READ (`tryGet` reconstructs the exact path from the key). Without this cleanup,
/// every edit to a file permanently adds a dead, unreachable sibling, and that bloat
/// degrades `Stats`/`clearFile`/`clearPlugin`, which each full-scan the directory.
///
/// An LRU would be the wrong tool: it would retain entries that are not merely cold
/// but UNREACHABLE. Only the newest content-hash entry per plugin+file is useful.
///
/// The paths must be HANDED to it, never rediscovered by listing the cache directory:
/// `EnumerateFiles(dir, pattern)` is not prefix-optimised, it readdirs the whole
/// directory and pattern-matches in managed code. A cold scan writes ~3 entries per
/// file into a directory that grows to ~3 entries per file, so a scan per write is
/// quadratic — measured at ~2.2 ms per scan against a 4,500-entry directory, roughly
/// ten seconds added to a cold scan of a 1,500-file repo. The writer knows the path it
/// just wrote and the cache remembers the one it wrote last, so no scan is needed.
///
/// Never propagates, and each delete is independently guarded: a cache-hygiene failure
/// must not fail the task whose result was just written, nor stop the remaining
/// siblings being collected. Whatever this call misses, the next write to the same key
/// collects.
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
/// `repoRoot` makes the entries PORTABLE: with it, every path an entry names is
/// stored `repo:`-relative and rebound against whatever checkout reads it back, so a
/// store shared between two workspaces of the same repository replays into the right
/// files. Without it (the default), paths round-trip verbatim and the store is
/// implicitly machine-local — which is what a test fixture wants.
type FileTaskCache(cacheDir: string, ?repoRoot: string) =
    do Directory.CreateDirectory(cacheDir) |> ignore

    let repoRoot = repoRoot

    // Counts FULL-DIRECTORY enumerations performed by this instance. The write path
    // must perform ZERO of them (see `pruneSupersededSiblings`); the constructor's two
    // sweeps and the explicit `Clear*`/`Stats` operations are the only legitimate ones.
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
    /// Seeded once at construction from whatever previous processes left on disk, so
    /// their leftovers are still collected by the first write to the key that owns them.
    /// `File.Delete` on a missing file is a no-op, so the memo may be stale.
    let livePathsLock = obj ()
    let livePaths = System.Collections.Generic.Dictionary<string, string list>()

    do
        enumerateEntries "*.json"
        |> Seq.iter (fun f ->
            match entryKeyOfFileName (Path.GetFileName f) with
            // A stray `.json` with no `@{hash}` suffix: not reachable through any key,
            // so not ours to remember or to collect.
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

    // Counts read attempts that found a file but failed to parse it (corrupt or
    // stale-format). Telemetry for the corruption-failure rate.
    let mutable parseFailureCount = 0

    /// The key-input digests of whatever entry this composite key was last written
    /// under, so a lookup that missed can say which input moved. Reads the SIBLING
    /// entry `livePaths` remembers — seeded from disk at construction, so a brand-new
    /// daemon reading a store another workspace filled still gets a real answer.
    ///
    /// Never throws and never widens a hit: a sibling that cannot be read yields
    /// `None`, which degrades the reason, not the decision.
    let siblingKeyInputs (compositeKey: CompositeKey) =
        let candidates =
            lock livePathsLock (fun () ->
                match livePaths.TryGetValue(entryKey compositeKey) with
                | true, paths -> paths
                | false, _ -> [])

        candidates
        |> List.tryPick (fun p ->
            try
                if File.Exists p then
                    Some(snd (deserializeEntry repoRoot (File.ReadAllText p)))
                else
                    None
            with _ ->
                None)

    let lookup (compositeKey: CompositeKey) (cacheKey: ContentHash) : CacheLookup =
        let path = filePath compositeKey cacheKey

        if not (File.Exists path) then
            match siblingKeyInputs compositeKey with
            | Some stored -> CacheMiss(missReasonForKeys (Some stored) cacheKey)
            | None ->
                // No sibling on disk at all: either nothing was ever written under this
                // composite key, or the only sibling is unreadable. Both are honestly
                // reported as a cold key — there is no recorded input to point at.
                CacheMiss CacheMissReason.NoEntryForKey
        else
            try
                let json = File.ReadAllText(path)
                let result, _ = deserializeEntry repoRoot json

                if result.CacheKey = cacheKey then
                    CacheHit result
                else
                    // The file named by this key stores a DIFFERENT key. Only reachable
                    // through a hash collision in the 12-character file-name digest, so
                    // it is a miss with no input to blame.
                    CacheMiss(CacheMissReason.UnreadableEntry "entry stores a different cache key")
            with ex ->
                System.Threading.Interlocked.Increment(&parseFailureCount) |> ignore
                CacheMiss(CacheMissReason.UnreadableEntry $"%s{ex.GetType().Name}: %s{ex.Message}")

    let tryGet (compositeKey: CompositeKey) (cacheKey: ContentHash) =
        match lookup compositeKey cacheKey with
        | CacheHit result -> Some result
        | CacheMiss _ -> None

    let set (compositeKey: CompositeKey) (cacheKey: ContentHash) (result: TaskCacheResult) =
        let path = filePath compositeKey cacheKey
        let json = serializeResult repoRoot result
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

    /// Total entry count and byte size of cache files in `cacheDir`. Logged on daemon
    /// startup so future LRU thresholds can be set from data.
    member _.Stats =
        let mutable count = 0
        let mutable bytes = 0L

        for f in enumerateEntries "*.json" do
            count <- count + 1
            bytes <- bytes + FileInfo(f).Length

        { EntryCount = count
          SizeBytes = bytes }

    /// Number of read attempts that found a file but couldn't deserialise it. A
    /// non-zero value indicates corruption or a stale on-disk format.
    member _.ParseFailureCount = parseFailureCount

    /// How many times this cache has enumerated its whole directory. A probe for the
    /// test holding the "a `set` never scans" line, not part of the cache's contract.
    member internal _.DirectoryScanCount = directoryScanCount

    /// Try to retrieve a cached result.
    member _.TryGet(compositeKey: CompositeKey, cacheKey: ContentHash) = tryGet compositeKey cacheKey

    /// Retrieve a cached result, or the reason there is none.
    member _.Lookup(compositeKey: CompositeKey, cacheKey: ContentHash) = lookup compositeKey cacheKey

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
        member _.Lookup compositeKey cacheKey = lookup compositeKey cacheKey
        member _.Set compositeKey cacheKey result = set compositeKey cacheKey result
        member _.Clear() = clear ()
        member _.ClearPlugin plugin = clearPlugin plugin
        member _.ClearFile file = clearFile file
        member _.ClearPluginFile plugin file = clearPluginFile plugin file
