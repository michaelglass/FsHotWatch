/// A box-wide, content-addressed store of test greens: "test project P ran in full, all
/// green, in some checkout, under key K".
///
/// It lives beside the shared task store, outside any checkout, namespaced by
/// repository, so every workspace of one repository reads the same entries. An entry is
/// immutable and self-verifying: it is written to a unique temp file and renamed into
/// place, and it carries a checksum of its payload. Anything that cannot be read back
/// exactly as written — a torn write, a corrupt or foreign file, an unknown schema — is
/// a MISS, and the file is moved aside so it is never read again.
///
/// A miss always means "run the project"; nothing here can make a result green that a
/// real run did not earn.
module FsHotWatch.TestEvidenceStore

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// The entry format. A file stating any other schema is a miss and is quarantined.
///
/// v2 stores the coverage report gzip-compressed and base64-encoded (`coverage.gzip`).
[<Literal>]
let Schema = "fshw-test-evidence-v2"

/// Where an entry came from.
type Producer =
    {
        /// The checkout that earned it.
        Worktree: string
        /// The test run that earned it.
        RunId: string
        /// The verdict tree hash of the producing checkout when it ran.
        TreeHash: string
        /// The fshw build that produced it.
        ProducerHash: string
        EarnedAt: DateTimeOffset
    }

/// The producing run's counts for this project.
type Summary =
    { Tests: int
      Passed: int
      Failed: int
      Skipped: int
      DurationMs: int64 }

/// The project's coverage report, with paths inside the producing checkout written
/// relative to it (`relativise`), so a reader can `rebind` them into its own.
type CoverageBlob = { Path: string; Content: string }

type Entry =
    {
        Key: string
        /// The test project, repo-relative.
        Project: string
        /// The labelled digests the key was folded from.
        KeyInputs: (string * string) list
        Producer: Producer
        Summary: Summary
        /// The producing run's CTRF report for this project, relativised. A reusing run
        /// files it under its own run directory, so the verdict grades the same rows the
        /// producer graded.
        Report: string
        Coverage: CoverageBlob option
    }

/// Why a lookup found nothing to reuse. Every case means "run".
[<RequireQualifiedAccess>]
type Miss =
    /// Nothing has ever been stored for this project.
    | NoEntry
    /// Something was stored for this project, under a key whose named inputs differ.
    | Moved of labels: string list
    /// The entry is older than the store's TTL.
    | Expired of age: TimeSpan
    /// The entry could not be read back as written; it has been quarantined.
    | Corrupt of reason: string
    /// The entry states a schema this build does not read; it has been quarantined.
    | UnknownSchema of schema: string

let describeMiss =
    function
    | Miss.NoEntry -> "no evidence for this project"
    | Miss.Moved labels -> $"""key moved: %s{String.concat ", " labels}"""
    | Miss.Expired age -> $"evidence expired (%.0f{age.TotalHours} h old)"
    | Miss.Corrupt reason -> $"evidence unreadable, quarantined: %s{reason}"
    | Miss.UnknownSchema schema -> $"evidence has unknown schema %s{schema}, quarantined"

[<RequireQualifiedAccess>]
type Lookup =
    | Hit of Entry
    | Miss of Miss

/// The placeholder a producing checkout's root is written as inside a stored report.
[<Literal>]
let RepoRootPlaceholder = "${fshw-repo}"

let private trimmed (path: string) =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

/// `path` with every symbolic link along it resolved: the spelling a process that asked
/// the OS for its working directory reports (`/private/var/…` for `/var/…` on macOS). A
/// chain longer than the OS would follow (a cycle) stops where it is, as a lookup there
/// would fail; nothing here may throw, since a report is relativised on a run's own path.
let internal physical (path: string) =
    let mutable hops = 0

    let rec resolve (full: string) =
        match Path.GetDirectoryName full with
        | null -> full
        | parent ->
            let parent = resolve parent
            let here = Path.Combine(parent, Path.GetFileName full)

            let target =
                try
                    FileInfo(here).LinkTarget
                with
                | :? IOException
                | :? UnauthorizedAccessException -> null

            match target with
            | null -> here
            | _ when hops >= 40 -> here
            | target ->
                hops <- hops + 1
                resolve (Path.GetFullPath(target, parent))

    resolve (trimmed path)

/// Every spelling of `repoRoot` a report written there can carry, longest first.
let private rootSpellings (repoRoot: string) =
    [ trimmed repoRoot; physical repoRoot ]
    |> List.distinct
    |> List.sortByDescending String.length

/// `text` with every occurrence of `repoRoot` — under any spelling, and only where it is a
/// whole path prefix (`/w/repo` never rewrites `/w/repo2`) — replaced by the placeholder.
let relativise (repoRoot: string) (text: string) =
    rootSpellings repoRoot
    |> List.fold
        (fun (acc: string) spelling ->
            System.Text.RegularExpressions.Regex.Replace(
                acc,
                System.Text.RegularExpressions.Regex.Escape spelling + "(?=[/\\\\\"'<\\s]|$)",
                RepoRootPlaceholder.Replace("$", "$$")
            ))
        text

/// `text` with the placeholder bound to `repoRoot`.
let rebind (repoRoot: string) (text: string) =
    text.Replace(RepoRootPlaceholder, trimmed repoRoot)

/// Environment variable that turns evidence reuse off: `0` means every project runs
/// exactly as it would without a store.
[<Literal>]
let KillSwitchEnvVar = "FSHW_TEST_EVIDENCE"

/// Evidence is on unless `FSHW_TEST_EVIDENCE` is `0`.
let enabledBy (value: string) =
    not (String.Equals((if isNull value then "" else value).Trim(), "0", StringComparison.Ordinal))

let enabled () =
    enabledBy (Environment.GetEnvironmentVariable KillSwitchEnvVar)

[<NoComparison; NoEquality>]
type Settings =
    {
        /// The store's directory (`directoryFor`).
        Root: string
        /// How long an entry is reusable. Bounds what no key can name: the network, the
        /// clock, services outside the repository.
        Ttl: TimeSpan
        /// The most bytes the store keeps; the oldest entries are evicted beyond it.
        MaxBytes: int64
        Now: unit -> DateTimeOffset
        Log: string -> unit
    }

let defaultTtl = TimeSpan.FromHours 72.0

let defaultMaxBytes = 512L * 1024L * 1024L

/// The store directory of `repoRoot`'s repository.
let directoryFor (repoRoot: string) =
    Path.Combine(FsHwPaths.sharedCacheHome (), "cache", "evidence", RepoIdentity.namespaceOf repoRoot, "v1")

let private isKeyValue (key: string) =
    key.Length = 64 && key |> Seq.forall Uri.IsHexDigit

/// A coverage report as stored: gzip, then base64. A Cobertura report is tens of megabytes
/// of repetitive XML per project, and written raw as a JSON string inside the envelope's
/// JSON string it grew by half again in escapes. Measured on one repository's full suite,
/// a 32.5 MB report compressed to 1.7 MB, so the store holds many trees' evidence rather
/// than about two.
let private compress (text: string) : string =
    let buffer = new MemoryStream()
    // Disposed explicitly rather than by `use`: the gzip footer is written on dispose, and
    // `ToArray` still answers on the closed buffer.
    let gzip = new Compression.GZipStream(buffer, Compression.CompressionLevel.Optimal)

    try
        let bytes = Text.Encoding.UTF8.GetBytes text
        gzip.Write(bytes, 0, bytes.Length)
    finally
        gzip.Dispose()

    Convert.ToBase64String(buffer.ToArray())

/// The inverse of `compress`. Text that is not base64 throws `FormatException`, and bytes
/// that are not a gzip stream throw `InvalidDataException`; `decode` reads both as corrupt.
let private decompress (stored: string) : string =
    let gzip =
        new Compression.GZipStream(
            new MemoryStream(Convert.FromBase64String stored),
            Compression.CompressionMode.Decompress
        )

    try
        (new StreamReader(gzip, Text.Encoding.UTF8)).ReadToEnd()
    finally
        gzip.Dispose()

let private serializePayload (entry: Entry) : string =
    let o = JsonObject()
    o["key"] <- entry.Key
    o["project"] <- entry.Project
    let inputs = JsonObject()

    for (label, digest) in entry.KeyInputs do
        inputs[label] <- digest

    o["keyInputs"] <- inputs
    let p = JsonObject()
    p["worktree"] <- entry.Producer.Worktree
    p["runId"] <- entry.Producer.RunId
    p["treeHash"] <- entry.Producer.TreeHash
    p["producerHash"] <- entry.Producer.ProducerHash
    p["earnedAt"] <- entry.Producer.EarnedAt.ToString("O")
    o["producer"] <- p
    let s = JsonObject()
    s["tests"] <- entry.Summary.Tests
    s["passed"] <- entry.Summary.Passed
    s["failed"] <- entry.Summary.Failed
    s["skipped"] <- entry.Summary.Skipped
    s["durationMs"] <- entry.Summary.DurationMs
    o["summary"] <- s
    o["report"] <- entry.Report

    match entry.Coverage with
    | Some c ->
        let co = JsonObject()
        co["path"] <- c.Path
        co["gzip"] <- compress c.Content
        o["coverage"] <- co
    | None -> ()

    o.ToJsonString()

let private req (el: JsonElement) (name: string) =
    match Json.tryString el name with
    | Some v -> v
    | None -> raise (JsonException $"missing %s{name}")

let private reqInt (el: JsonElement) (name: string) =
    match Json.tryInt el name with
    | Some v -> v
    | None -> raise (JsonException $"missing %s{name}")

let private reqObj (el: JsonElement) (name: string) =
    match Json.tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.Object -> v
    | _ -> raise (JsonException $"missing %s{name}")

let private parsePayload (payload: string) : Entry =
    use doc = JsonDocument.Parse payload
    let o = doc.RootElement
    let p = reqObj o "producer"
    let s = reqObj o "summary"

    { Key = req o "key"
      Project = req o "project"
      KeyInputs =
        (reqObj o "keyInputs").EnumerateObject()
        |> Seq.map (fun prop -> prop.Name, prop.Value.GetString())
        |> Seq.toList
      Producer =
        { Worktree = req p "worktree"
          RunId = req p "runId"
          TreeHash = req p "treeHash"
          ProducerHash = req p "producerHash"
          EarnedAt = DateTimeOffset.Parse(req p "earnedAt", Globalization.CultureInfo.InvariantCulture) }
      Summary =
        { Tests = reqInt s "tests"
          Passed = reqInt s "passed"
          Failed = reqInt s "failed"
          Skipped = reqInt s "skipped"
          DurationMs = Json.tryInt64 s "durationMs" |> Option.defaultValue 0L }
      Report = req o "report"
      Coverage =
        match Json.tryProp o "coverage" with
        | Some c when c.ValueKind = JsonValueKind.Object ->
            Some
                { Path = req c "path"
                  Content = decompress (req c "gzip") }
        | _ -> None }

/// The envelope written to disk: schema, a checksum of the payload, and the payload as a
/// string, so the checksum is over exactly the bytes that are parsed.
let internal envelope (entry: Entry) : string =
    let payload = serializePayload entry
    let o = JsonObject()
    o["schema"] <- Schema
    o["checksum"] <- ContentHash.ofText payload
    o["payload"] <- payload
    o.ToJsonString()

[<RequireQualifiedAccess>]
type internal Decoded =
    | Entry of Entry
    | Corrupt of string
    | UnknownSchema of string

let internal decode (text: string) : Decoded =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        match Json.tryString o "schema" with
        | None -> Decoded.Corrupt "no schema"
        | Some schema when schema <> Schema -> Decoded.UnknownSchema schema
        | Some _ ->
            match Json.tryString o "checksum", Json.tryString o "payload" with
            | Some checksum, Some payload when ContentHash.ofText payload = checksum ->
                Decoded.Entry(parsePayload payload)
            | Some _, Some _ -> Decoded.Corrupt "checksum mismatch"
            | _ -> Decoded.Corrupt "no payload"
    with
    | :? JsonException as ex -> Decoded.Corrupt ex.Message
    | :? FormatException as ex -> Decoded.Corrupt ex.Message
    | :? InvalidDataException as ex -> Decoded.Corrupt ex.Message
    | :? InvalidOperationException as ex -> Decoded.Corrupt ex.Message

type Store(settings: Settings) =
    let root = settings.Root
    let quarantineDir = Path.Combine(root, "quarantine")
    let projectsDir = Path.Combine(root, "projects")
    let mutable lookups = 0

    let entryPath (key: string) =
        Path.Combine(root, key.Substring(0, 2), key + ".json")

    let projectIndexPath (project: string) =
        Path.Combine(projectsDir, ContentHash.ofText(project).Substring(0, 32) + ".json")

    let quarantine (path: string) (why: string) =
        try
            Directory.CreateDirectory quarantineDir |> ignore

            let target =
                Path.Combine(quarantineDir, (Path.GetFileName path + "." + Guid.NewGuid().ToString "N"))

            File.Move(path, target)
            settings.Log $"test evidence: quarantined %s{Path.GetFileName path}: %s{why}"
        with ex ->
            settings.Log $"test evidence: could not quarantine %s{path}: %s{ex.Message}"

    /// The labelled digests the project was last written under, for naming what moved.
    let lastKeyInputs (project: string) : (string * string) list option =
        try
            let path = projectIndexPath project

            if File.Exists path then
                use doc = JsonDocument.Parse(File.ReadAllText path)

                if Json.tryString doc.RootElement "project" = Some project then
                    Json.tryProp doc.RootElement "keyInputs"
                    |> Option.filter (fun el -> el.ValueKind = JsonValueKind.Object)
                    |> Option.map (fun el ->
                        el.EnumerateObject()
                        |> Seq.map (fun prop -> prop.Name, prop.Value.GetString())
                        |> Seq.toList)
                else
                    None
            else
                None
        with _ ->
            None

    let writeAtomically (path: string) (text: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString "N" + ".tmp"

        try
            File.WriteAllText(temporary, text)
            File.Move(temporary, path, true)
        finally
            // A delete that fails here surfaces through `Write`, which never throws.
            if File.Exists temporary then
                File.Delete temporary

    /// The entries and quarantined files retention weighs. Never a writer's `.tmp`: one
    /// in flight is not yet an entry, and evicting it would fail that write. Sizes and
    /// times come from the enumeration itself, so a file renamed or evicted meanwhile by
    /// another writer cannot fail a second look at it.
    let storedFiles () =
        if Directory.Exists root then
            DirectoryInfo(root).EnumerateDirectories()
            |> Seq.filter (fun d -> d.Name.Length = 2 || d.Name = "quarantine")
            |> Seq.collect (fun d ->
                if d.Name = "quarantine" then
                    d.EnumerateFiles()
                else
                    d.EnumerateFiles "*.json")
            |> Seq.map (fun f -> f.FullName, f.Length, f.LastWriteTimeUtc)
            |> Seq.toList
        else
            []

    member _.Root = root

    /// How many lookups this store has answered. `confirm` and the kill switch make none.
    member _.Lookups = lookups

    /// The entry for `project` under `key`, or why there is none.
    member _.Lookup(project: string, key: TestEvidenceKey.Key) : Lookup =
        System.Threading.Interlocked.Increment(&lookups) |> ignore
        let path = entryPath key.Value

        let noEntry () =
            match lastKeyInputs project with
            | Some stored ->
                match TestEvidenceKey.moved stored key.Labels with
                | [] -> Lookup.Miss Miss.NoEntry
                | labels -> Lookup.Miss(Miss.Moved labels)
            | None -> Lookup.Miss Miss.NoEntry

        if not (isKeyValue key.Value) || not (File.Exists path) then
            noEntry ()
        else
            let text =
                try
                    Some(File.ReadAllText path)
                with _ ->
                    None

            match text |> Option.map decode with
            | None -> Lookup.Miss(Miss.Corrupt "unreadable")
            | Some(Decoded.UnknownSchema schema) ->
                quarantine path $"unknown schema %s{schema}"
                Lookup.Miss(Miss.UnknownSchema schema)
            | Some(Decoded.Corrupt reason) ->
                quarantine path reason
                Lookup.Miss(Miss.Corrupt reason)
            | Some(Decoded.Entry entry) when entry.Key <> key.Value || entry.Project <> project ->
                quarantine path "entry names a different key or project"
                Lookup.Miss(Miss.Corrupt "entry names a different key or project")
            | Some(Decoded.Entry entry) ->
                let age = settings.Now() - entry.Producer.EarnedAt

                if age > settings.Ttl then
                    Lookup.Miss(Miss.Expired age)
                else
                    Lookup.Hit entry

    /// Store `entry`, then enforce retention. Never throws: a store that cannot be
    /// written costs a future reuse, not this run.
    member this.Write(entry: Entry) : bool =
        try
            if not (isKeyValue entry.Key) then
                failwith $"not a key: %s{entry.Key}"

            writeAtomically (entryPath entry.Key) (envelope entry)

            let index = JsonObject()
            index["project"] <- entry.Project
            index["key"] <- entry.Key
            let inputs = JsonObject()

            for (label, digest) in entry.KeyInputs do
                inputs[label] <- digest

            index["keyInputs"] <- inputs
            writeAtomically (projectIndexPath entry.Project) (index.ToJsonString())
            this.Prune() |> ignore
            true
        with ex ->
            settings.Log $"test evidence: could not store %s{entry.Project}: %s{ex.Message}"
            false

    /// Evict expired entries, then the oldest until the store fits `MaxBytes`. Logs and
    /// returns each eviction.
    member _.Prune() : string list =
        let now = settings.Now().UtcDateTime
        let files = storedFiles () |> List.sortBy (fun (_, _, written) -> written)

        let expired, live =
            files |> List.partition (fun (_, _, written) -> now - written > settings.Ttl)

        let mutable total = live |> List.sumBy (fun (_, size, _) -> size)

        let overBudget =
            [ for (path, size, _) in live do
                  if total > settings.MaxBytes then
                      total <- total - size
                      yield path, "over the byte budget" ]

        let evictions =
            (expired |> List.map (fun (path, _, _) -> path, "expired")) @ overBudget

        [ for (path, why) in evictions do
              try
                  File.Delete path
                  settings.Log $"test evidence: evicted %s{Path.GetFileName path} (%s{why})"
                  yield path
              with ex ->
                  settings.Log $"test evidence: could not evict %s{path}: %s{ex.Message}" ]

/// The store of `repoRoot`'s repository with the default TTL and byte budget.
let openFor (repoRoot: string) (log: string -> unit) =
    Store(
        { Root = directoryFor repoRoot
          Ttl = defaultTtl
          MaxBytes = defaultMaxBytes
          Now = fun () -> DateTimeOffset.UtcNow
          Log = log }
    )
