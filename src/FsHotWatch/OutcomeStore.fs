/// The one content-addressed store of action outcomes (ADR-059 §2): "the action with key K
/// ran, and this is how it came out".
///
/// It lives in the box-wide cache, namespaced by repository, so every checkout of one
/// repository reads the same records: a key is a content address over repo-relative inputs,
/// so a record earned in one checkout speaks for any checkout holding the same bytes.
///
/// A record is IMMUTABLE and SELF-VERIFYING:
///   * it is filed at `records/<2 hex>/<key hex>/<record id>.json`, where the record id is
///     the digest of its payload, so recording an identical outcome twice is a no-op;
///   * it is written to a temp file and renamed into place, never overwritten;
///   * its envelope carries a checksum of the payload, and its payload carries the kind
///     and labelled inputs its key must fold back to.
/// Anything that cannot be read back exactly as written — torn, foreign, mis-keyed, an
/// unknown schema — is a MISS, and the file is moved aside so it is never read again.
///
/// Several records under one key are HISTORY (a flake is two outcomes for one input set),
/// never an overwrite. The answer for a key is its newest record; at the same instant a
/// fail outranks a pass. Time orders outcomes; it never decides whether one is current —
/// only the key does that. GC deletes whole keys and can only turn a hit into a miss, and
/// a miss means "run".
module FsHotWatch.OutcomeStore

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// The record format. A file stating any other schema is a miss and is quarantined.
[<Literal>]
let Schema = "fshw-outcome-v1"

[<RequireQualifiedAccess>]
type Outcome =
    | Pass
    | Fail

/// Test counts, for an action that ran tests.
type Counts =
    { Total: int
      Passed: int
      Failed: int
      Skipped: int }

/// Who produced a record.
type Producer =
    {
        /// The fshw build that produced it.
        Binary: string
        /// The checkout it was produced in.
        Worktree: string
        /// The test run, when the action was one.
        RunId: string option
    }

type Record =
    {
        Key: ActionKey.ActionKey
        /// What the action was about, for a reader: a project, a plugin name.
        Subject: string
        Outcome: Outcome
        Counts: Counts option
        DurationMs: int64 option
        Producer: Producer
        RecordedAt: DateTimeOffset
    }

[<NoComparison; NoEquality>]
type Settings =
    {
        /// The store's directory (`directoryFor`).
        Root: string
        /// GC evicts a key whose newest record is older than this.
        Ttl: TimeSpan
        /// GC evicts the oldest keys beyond this many bytes.
        MaxBytes: int64
        Log: string -> unit
    }

let defaultTtl = TimeSpan.FromDays 14.0

let defaultMaxBytes = 256L * 1024L * 1024L

/// The store directory of `repoRoot`'s repository.
let directoryFor (repoRoot: string) =
    Path.Combine(FsHwPaths.sharedCacheHome (), "cache", "outcomes", RepoIdentity.namespaceOf repoRoot, "v1")

let private outcomeToken =
    function
    | Outcome.Pass -> "pass"
    | Outcome.Fail -> "fail"

let private serializePayload (r: Record) (outputs: string option) : string =
    let o = JsonObject()
    o["key"] <- r.Key.Value
    o["kind"] <- r.Key.Kind
    let inputs = JsonArray()

    for (label, digest) in r.Key.Inputs do
        inputs.Add(JsonArray(JsonValue.Create label, JsonValue.Create digest))

    o["inputs"] <- inputs
    o["subject"] <- r.Subject
    o["outcome"] <- outcomeToken r.Outcome

    match r.Counts with
    | Some c ->
        let counts = JsonObject()
        counts["total"] <- c.Total
        counts["passed"] <- c.Passed
        counts["failed"] <- c.Failed
        counts["skipped"] <- c.Skipped
        o["counts"] <- counts
    | None -> ()

    match r.DurationMs with
    | Some ms -> o["durationMs"] <- ms
    | None -> ()

    let producer = JsonObject()
    producer["binary"] <- r.Producer.Binary
    producer["worktree"] <- r.Producer.Worktree

    match r.Producer.RunId with
    | Some run -> producer["runId"] <- run
    | None -> ()

    o["producer"] <- producer
    o["recordedAt"] <- r.RecordedAt.ToString("o")

    // ADR-059 §14: what the action produced, for a kind whose record carries outputs.
    match outputs with
    | Some text -> o["outputs"] <- JsonNode.Parse text
    | None -> ()

    o.ToJsonString()

let private required (el: JsonElement) (name: string) : JsonElement =
    match Json.tryProp el name with
    | Some v -> v
    | None -> raise (FormatException $"no %s{name}")

let private parsePayload (payload: string) : Record * string option =
    use doc = JsonDocument.Parse payload
    let o = doc.RootElement
    let str (el: JsonElement) name = (required el name).GetString()

    let inputs =
        (required o "inputs").EnumerateArray()
        |> Seq.map (fun pair -> pair[0].GetString(), pair[1].GetString())
        |> Seq.toList

    let key = ActionKey.create (str o "kind") inputs

    if key.Value <> str o "key" then
        raise (FormatException "the inputs do not fold to the stated key")

    let outcome =
        match str o "outcome" with
        | "pass" -> Outcome.Pass
        | "fail" -> Outcome.Fail
        | other -> raise (FormatException $"unknown outcome %s{other}")

    let producer = required o "producer"

    let outputs =
        Json.tryProp o "outputs"
        |> Option.map (fun (el: JsonElement) -> el.GetRawText())

    { Key = key
      Subject = str o "subject"
      Outcome = outcome
      Counts =
        Json.tryProp o "counts"
        |> Option.map (fun c ->
            { Total = (required c "total").GetInt32()
              Passed = (required c "passed").GetInt32()
              Failed = (required c "failed").GetInt32()
              Skipped = (required c "skipped").GetInt32() })
      DurationMs = Json.tryProp o "durationMs" |> Option.map _.GetInt64()
      Producer =
        { Binary = str producer "binary"
          Worktree = str producer "worktree"
          RunId = Json.tryString producer "runId" }
      RecordedAt = DateTimeOffset.Parse(str o "recordedAt", Globalization.CultureInfo.InvariantCulture) },
    outputs

let private envelope (payload: string) : string =
    let o = JsonObject()
    o["schema"] <- Schema
    o["checksum"] <- ContentHash.ofText payload
    o["payload"] <- payload
    o.ToJsonString()

/// Read an envelope back: the record, or why it is not one.
let private decode (text: string) : Result<Record * string option, string> =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        match Json.tryString o "schema" with
        | None -> Error "no schema"
        | Some schema when schema <> Schema -> Error $"unknown schema %s{schema}"
        | Some _ ->
            match Json.tryString o "checksum", Json.tryString o "payload" with
            | Some checksum, Some payload when ContentHash.ofText payload = checksum -> Ok(parsePayload payload)
            | Some _, Some _ -> Error "checksum mismatch"
            | _ -> Error "no payload"
    with
    | :? JsonException as ex -> Error ex.Message
    | :? FormatException as ex -> Error ex.Message
    | :? InvalidOperationException as ex -> Error ex.Message
    | :? ArgumentException as ex -> Error ex.Message
    | :? IndexOutOfRangeException as ex -> Error ex.Message

/// Newest last; at the same instant a pass sorts before a fail, so the last is the fail.
let private chronological (records: (Record * 'a) list) =
    records
    |> List.sortBy (fun (r, _) -> r.RecordedAt, (if r.Outcome = Outcome.Fail then 1 else 0))

/// Every record in a store directory, read without changing it.
type Scan =
    {
        /// Every readable record of every key, oldest first.
        Records: Record list
        /// Files that are not a record of the key they are filed under.
        Unreadable: int
    }

/// Read every record under `root` for analysis (AUTOMATION-1368). A pure read: unlike a
/// lookup, it quarantines nothing, so a report never changes the store it reports on. A
/// file that a lookup would quarantine is counted in `Unreadable` and left where it is.
let readAll (root: string) : Scan =
    let recordsDir = Path.Combine(root, "records")

    let files =
        if Directory.Exists recordsDir then
            [ for shard in Directory.GetDirectories recordsDir do
                  for dir in Directory.GetDirectories shard do
                      for file in Directory.GetFiles(dir, "*.json") do
                          yield ActionKey.Prefix + Path.GetFileName dir, file ]
        else
            []

    let read =
        files
        |> List.map (fun (keyValue, file) ->
            try
                match decode (File.ReadAllText file) with
                | Ok(record, outputs) when record.Key.Value = keyValue -> Some(record, outputs)
                | _ -> None
            with :? IOException ->
                None)

    { Records = read |> List.choose id |> chronological |> List.map fst
      Unreadable = read |> List.filter Option.isNone |> List.length }

type Store(settings: Settings) =
    let root = settings.Root
    let recordsDir = Path.Combine(root, "records")
    let quarantineDir = Path.Combine(root, "quarantine")
    let tmpDir = Path.Combine(root, "tmp")

    let hexOf (keyValue: string) =
        keyValue.Substring ActionKey.Prefix.Length

    let directoryOf (keyValue: string) =
        let hex = hexOf keyValue
        Path.Combine(recordsDir, hex.Substring(0, 2), hex)

    let quarantine (path: string) (why: string) =
        try
            Directory.CreateDirectory quarantineDir |> ignore

            let target =
                Path.Combine(quarantineDir, Path.GetFileName path + "." + Guid.NewGuid().ToString "N")

            File.Move(path, target)
            settings.Log $"outcome store: quarantined %s{Path.GetFileName path}: %s{why}"
        with ex ->
            settings.Log $"outcome store: could not quarantine %s{path}: %s{ex.Message}"

    /// Every readable record filed under `keyValue` with its outputs, oldest first. A file
    /// that is not a record of THAT key is quarantined and left out.
    let readKeyWithOutputs (keyValue: string) : (Record * string option) list =
        let dir = directoryOf keyValue

        if not (Directory.Exists dir) then
            []
        else
            [ for file in Directory.GetFiles(dir, "*.json") do
                  let text =
                      try
                          Ok(File.ReadAllText file)
                      with ex ->
                          Error ex.Message

                  match text |> Result.bind decode with
                  | Ok(record, outputs) when record.Key.Value = keyValue -> yield record, outputs
                  | Ok _ -> quarantine file "the record is filed under another key"
                  | Error why -> quarantine file why ]
            |> chronological

    let readKey (keyValue: string) : Record list =
        readKeyWithOutputs keyValue |> List.map fst

    let writeOnce (path: string) (text: string) =
        Directory.CreateDirectory tmpDir |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = Path.Combine(tmpDir, Guid.NewGuid().ToString "N")

        try
            File.WriteAllText(temporary, text)

            try
                File.Move(temporary, path, false)
            with :? IOException when File.Exists path ->
                // Filed already: the name is the digest of the content, so it holds these bytes.
                ()
        finally
            if File.Exists temporary then
                File.Delete temporary

    member _.Root = root

    /// The directory the records of `keyValue` are filed in.
    member _.DirectoryOf(keyValue: string) = directoryOf keyValue

    /// File `record`. Never throws: a store that cannot be written costs a future lookup,
    /// not the run that produced the record. Returns whether it is filed.
    member this.Record(record: Record) : bool = this.RecordWithOutputs(record, None)

    /// File `record` with `outputs`, a JSON object's text naming what the action produced
    /// (ADR-059 §14). Never throws, as `Record`.
    member _.RecordWithOutputs(record: Record, outputs: string option) : bool =
        try
            let payload = serializePayload record outputs

            let path =
                Path.Combine(directoryOf record.Key.Value, ContentHash.ofText payload + ".json")

            writeOnce path (envelope payload)
            true
        with ex ->
            settings.Log $"outcome store: could not record %s{record.Key.Kind} %s{record.Subject}: %s{ex.Message}"
            false

    /// Every record of `keyValue`, oldest first; empty for a value that is not a key.
    member _.History(keyValue: string) : Record list =
        if ActionKey.isValue keyValue then readKey keyValue else []

    /// `Lookup` with the record's outputs: `None` is a miss.
    member _.LookupWithOutputs(keyValue: string) : (Record * string option) option =
        if ActionKey.isValue keyValue then
            readKeyWithOutputs keyValue |> List.tryLast
        else
            None

    /// The answer for `keyValue`: its newest record, a fail outranking a pass at the same
    /// instant. `None` is a miss.
    member this.Lookup(keyValue: string) : Record option = this.History keyValue |> List.tryLast

    /// Evict every key whose newest record is older than the TTL at `now`, then the keys
    /// with the oldest newest-record until the store fits `MaxBytes`. Never a key in
    /// `keep`. Returns the evicted key values, oldest first.
    member _.Gc(now: DateTimeOffset, keep: Set<string>) : string list =
        let keys =
            if Directory.Exists recordsDir then
                [ for shard in Directory.GetDirectories recordsDir do
                      for dir in Directory.GetDirectories shard do
                          let keyValue = ActionKey.Prefix + Path.GetFileName dir

                          if ActionKey.isValue keyValue then
                              let size = Directory.GetFiles dir |> Array.sumBy (fun f -> FileInfo(f).Length)

                              match readKey keyValue |> List.tryLast with
                              | Some newest -> yield keyValue, dir, newest.RecordedAt, size
                              | None -> yield keyValue, dir, DateTimeOffset.MinValue, size ]
                |> List.sortBy (fun (_, _, newest, _) -> newest)
            else
                []

        let evictable = keys |> List.filter (fun (k, _, _, _) -> not (Set.contains k keep))

        let expired, live =
            evictable
            |> List.partition (fun (_, _, newest, _) -> now - newest > settings.Ttl)

        let sizeOf (entries: (string * string * DateTimeOffset * int64) list) =
            entries |> List.sumBy (fun (_, _, _, size) -> size)

        let mutable total = sizeOf keys - sizeOf expired

        let overBudget =
            [ for (k, dir, newest, size) in live do
                  if total > settings.MaxBytes then
                      total <- total - size
                      yield k, dir, newest, size ]

        [ for (k, dir, _, _) in expired @ overBudget do
              try
                  Directory.Delete(dir, true)
                  yield k
              with ex ->
                  settings.Log $"outcome store: could not evict %s{k}: %s{ex.Message}" ]

/// The store of `repoRoot`'s repository with the default TTL and byte budget.
let openFor (repoRoot: string) (log: string -> unit) =
    Store(
        { Root = directoryFor repoRoot
          Ttl = defaultTtl
          MaxBytes = defaultMaxBytes
          Log = log }
    )
