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

module Record =
    /// The record of an action that ran no tests, untimed and outside any test run: a check,
    /// an options evaluation, a format pass. `binary` names what produced it, `worktree` where.
    let ofAction
        (key: ActionKey.ActionKey)
        (subject: string)
        (outcome: Outcome)
        (binary: string)
        (worktree: string)
        (recordedAt: DateTimeOffset)
        : Record =
        { Key = key
          Subject = subject
          Outcome = outcome
          Counts = None
          DurationMs = None
          Producer =
            { Binary = binary
              Worktree = worktree
              RunId = None }
          RecordedAt = recordedAt }

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

/// AUTOMATION-1506: 2 GiB. One intelligence tree (2731 files) files about 300 MB, most of
/// it TestPrune analyses (about 120 KB a file); at 256 MB a cold daemon's own GC evicted its first
/// analyses minutes after filing them. Workspaces at different trees share the store.
let defaultMaxBytes = 2048L * 1024L * 1024L

/// The store directory of `repoRoot`'s repository.
let directoryFor (repoRoot: string) =
    Path.Combine(FsHwPaths.sharedCacheHome (), "cache", "outcomes", RepoIdentity.namespaceOf repoRoot, "v1")

let private outcomeToken =
    function
    | Outcome.Pass -> "pass"
    | Outcome.Fail -> "fail"

let private serializePayload (r: Record) (outputs: string option) : string =
    let o = JsonObject()
    o["key"] <- r.Key.Value.Text
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

    if key.Value.Text <> str o "key" then
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
                          yield ActionKey.KeyValue.tryParse (ActionKey.Prefix + Path.GetFileName dir), file ]
        else
            []

    let read =
        files
        |> List.map (fun (keyValue, file) ->
            try
                match decode (File.ReadAllText file) with
                | Ok(record, outputs) when Some record.Key.Value = keyValue -> Some(record, outputs)
                | _ -> None
            with :? IOException ->
                None)

    { Records = read |> List.choose id |> chronological |> List.map fst
      Unreadable = read |> List.filter Option.isNone |> List.length }

/// A superseded record: one of a key's records that is not its answer. Deleting it never
/// changes a lookup; it only shortens the key's history (AUTOMATION-1583).
type private SupersededRecord =
    { File: string
      Size: int64
      RecordedAt: DateTimeOffset }

/// A key as GC sees it.
[<CustomEquality; NoComparison>]
type private GcEntry =
    {
        Key: ActionKey.KeyValue
        Dir: string
        Newest: DateTimeOffset
        Size: int64
        Kind: string
        Superseded: SupersededRecord list
        /// The keys this key's answer keeps alive (`keepsOf`).
        Keeps: ActionKey.KeyValue list
    }

    override this.Equals(other) =
        match other with
        | :? GcEntry as e -> e.Key = this.Key
        | _ -> false

    override this.GetHashCode() = this.Key.GetHashCode()

/// Each kind's share of `budget`, given the kinds' sizes (AUTOMATION-1583): the largest
/// `cap` with `sum (min size cap) <= budget`, so a kind smaller than an equal split keeps
/// all it holds and leaves the rest to the larger kinds. No kind table: a kind is any
/// record kind, and a store that fits needs no share.
let fairShare (budget: int64) (sizes: int64 list) : int64 =
    let rec fill remaining kinds sorted =
        match sorted with
        | size :: rest when size * int64 kinds <= remaining -> fill (remaining - size) (kinds - 1) rest
        | _ -> remaining / int64 (max kinds 1)

    fill budget (List.length sizes) (List.sort sizes)

/// The outputs field naming the keys a record keeps alive (AUTOMATION-1587).
[<Literal>]
let KeepsField = "keeps"

/// The keys a record's outputs name under `keeps`: while that record is its key's answer,
/// GC keeps each of them as if the caller had passed it in `keep`. An index record names
/// what it indexes this way, so the store cannot evict an entry out from under the index
/// that points at it. Anything that is not a key value is ignored.
let keepsOf (outputs: string option) : ActionKey.KeyValue list =
    match outputs with
    | Some text when text.Contains("\"" + KeepsField + "\"", StringComparison.Ordinal) ->
        try
            use doc = JsonDocument.Parse text

            match Json.tryProp doc.RootElement KeepsField with
            | Some keeps when keeps.ValueKind = JsonValueKind.Array ->
                [ for k in keeps.EnumerateArray() do
                      if k.ValueKind = JsonValueKind.String then
                          match ActionKey.KeyValue.tryParse (k.GetString()) with
                          | Some key -> yield key
                          | None -> () ]
            | _ -> []
        with :? JsonException ->
            []
    | _ -> []

type Store(settings: Settings) =
    let root = settings.Root
    let recordsDir = Path.Combine(root, "records")
    let quarantineDir = Path.Combine(root, "quarantine")
    let tmpDir = Path.Combine(root, "tmp")

    let hexOf (keyValue: ActionKey.KeyValue) =
        keyValue.Text.Substring ActionKey.Prefix.Length

    let directoryOf (keyValue: ActionKey.KeyValue) =
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

    /// Every record filed under `keyValue` with its file and outputs, oldest first. A file
    /// that was read and is not a record of THAT key is quarantined and left out.
    ///
    /// A file that could not be READ is a different case (AUTOMATION-1568): it says nothing
    /// about what it holds, and it may be the newest fail. Answering the key from the rest
    /// would return the older pass, and quarantining it would make that answer permanent.
    /// So the whole key is a miss and nothing is moved, as in `TestEvidenceStore.Lookup`.
    /// `Gc` then sees no readable record and evicts the key as expired unless it is kept: a miss
    /// either way. `Error` names the file and why, for a reader that must not take it for an
    /// empty key (AUTOMATION-1615).
    let tryReadKeyFiles (keyValue: ActionKey.KeyValue) : Result<(Record * (string * string option)) list, string> =
        let dir = directoryOf keyValue

        let files =
            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.json")
            else
                [||]

        let read =
            files
            |> Array.map (fun file ->
                file,
                (try
                    Ok(File.ReadAllText file)
                 with ex ->
                     Error ex.Message))

        let unreadable =
            read
            |> Array.tryPick (fun (file, text) ->
                match text with
                | Error why -> Some(file, why)
                | Ok _ -> None)

        match unreadable with
        | Some(file, why) ->
            settings.Log
                $"outcome store: %s{Path.GetFileName file} could not be read, so %s{keyValue.Text} is a miss: %s{why}"

            Error $"%s{Path.GetFileName file} could not be read: %s{why}"
        | None ->
            [ for file, text in read do
                  match text |> Result.bind decode with
                  | Ok(record, outputs) when record.Key.Value = keyValue -> yield record, (file, outputs)
                  | Ok _ -> quarantine file "the record is filed under another key"
                  | Error why -> quarantine file why ]
            |> chronological
            |> Ok

    let readKeyFiles (keyValue: ActionKey.KeyValue) : (Record * (string * string option)) list =
        tryReadKeyFiles keyValue |> Result.defaultValue []

    /// Every readable record filed under `keyValue` with its outputs, oldest first.
    let readKeyWithOutputs (keyValue: ActionKey.KeyValue) : (Record * string option) list =
        readKeyFiles keyValue
        |> List.map (fun (record, (_, outputs)) -> record, outputs)

    let readKey (keyValue: ActionKey.KeyValue) : Record list =
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
    member _.DirectoryOf(keyValue: ActionKey.KeyValue) = directoryOf keyValue

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

    /// File `record` with `outputs` as its key's ONLY record (AUTOMATION-1587): file it as
    /// `RecordWithOutputs` does, then delete every other record under the key. The new
    /// record is the newest, so the lookup answer is the same as with the history kept; only
    /// the history is gone. For a record that is REWRITTEN rather than accumulated, such as
    /// an index: kept as history, each rewrite would supersede the last and GC would trim
    /// them oldest first. Returns the filed record's size in bytes, or `None` when it could
    /// not be filed (and then nothing is deleted). Never throws.
    member _.Supersede(record: Record, outputs: string option) : int64 option =
        try
            let payload = serializePayload record outputs
            let dir = directoryOf record.Key.Value
            let path = Path.Combine(dir, ContentHash.ofText payload + ".json")
            writeOnce path (envelope payload)

            for other in Directory.GetFiles(dir, "*.json") do
                if not (String.Equals(other, path, StringComparison.Ordinal)) then
                    try
                        File.Delete other
                    with ex ->
                        settings.Log $"outcome store: could not delete the superseded %s{other}: %s{ex.Message}"

            Some(FileInfo(path).Length)
        with ex ->
            settings.Log $"outcome store: could not record %s{record.Key.Kind} %s{record.Subject}: %s{ex.Message}"
            None

    /// Every record of `keyValue`, oldest first.
    member _.History(keyValue: ActionKey.KeyValue) : Record list = readKey keyValue

    /// Every record of `keyValue`, oldest first, or `Error` naming a file at the key that could
    /// not be READ (AUTOMATION-1615). `History` reads that key as empty, which is right for a
    /// miss and wrong for a reader asking whether a newer fail is filed: the unreadable file may
    /// be that fail.
    member _.TryHistory(keyValue: ActionKey.KeyValue) : Result<Record list, string> =
        tryReadKeyFiles keyValue |> Result.map (List.map fst)

    /// `Lookup` with the record's outputs: `None` is a miss.
    member _.LookupWithOutputs(keyValue: ActionKey.KeyValue) : (Record * string option) option =
        readKeyWithOutputs keyValue |> List.tryLast

    /// The answer for `keyValue`: its newest record, a fail outranking a pass at the same
    /// instant. `None` is a miss.
    member this.Lookup(keyValue: ActionKey.KeyValue) : Record option = this.History keyValue |> List.tryLast

    /// `Lookup` by the key itself.
    member this.Lookup(key: ActionKey.ActionKey) : Record option = this.Lookup key.Value

    /// `LookupWithOutputs` by the key itself.
    member this.LookupWithOutputs(key: ActionKey.ActionKey) : (Record * string option) option =
        this.LookupWithOutputs key.Value

    /// `History` by the key itself.
    member this.History(key: ActionKey.ActionKey) : Record list = this.History key.Value

    /// `DirectoryOf` by the key itself.
    member _.DirectoryOf(key: ActionKey.ActionKey) = directoryOf key.Value

    /// Evict every key whose newest record is older than the TTL at `now`. Then, while the
    /// store is over `MaxBytes`, reclaim only from the kinds over their fair share
    /// (`fairShare`), each down to its share: first its superseded records, oldest first,
    /// then its keys with the oldest newest-record. A kind within its share loses nothing
    /// younger than the TTL (AUTOMATION-1583). Never a key in `keep`; when kept keys hold a
    /// kind over its share, the oldest other keys go until the store fits. Returns the
    /// evicted key values, oldest first, and says in the log what it evicted, why and of
    /// which kinds (AUTOMATION-1506: an eviction is the one way a filed record turns into a
    /// miss, and both callers discard the list).
    member _.Gc(now: DateTimeOffset, keep: Set<ActionKey.KeyValue>) : ActionKey.KeyValue list =
        let entries =
            if Directory.Exists recordsDir then
                [ for shard in Directory.GetDirectories recordsDir do
                      for dir in Directory.GetDirectories shard do
                          match ActionKey.KeyValue.tryParse (ActionKey.Prefix + Path.GetFileName dir) with
                          | None -> ()
                          | Some keyValue ->
                              let records = readKeyFiles keyValue
                              let size = Directory.GetFiles dir |> Array.sumBy (fun f -> FileInfo(f).Length)

                              match List.tryLast records with
                              | Some(newest, (_, newestOutputs)) ->
                                  let superseded =
                                      [ for (r, (file, _)) in List.take (records.Length - 1) records ->
                                            { File = file
                                              Size = FileInfo(file).Length
                                              RecordedAt = r.RecordedAt } ]

                                  yield
                                      { Key = keyValue
                                        Dir = dir
                                        Newest = newest.RecordedAt
                                        Size = size
                                        Kind = newest.Key.Kind
                                        Superseded = superseded
                                        Keeps = keepsOf newestOutputs }
                              | None ->
                                  yield
                                      { Key = keyValue
                                        Dir = dir
                                        Newest = DateTimeOffset.MinValue
                                        Size = size
                                        Kind = "unreadable"
                                        Superseded = []
                                        Keeps = [] } ]
                |> List.sortBy _.Newest
            else
                []

        // A key named in the answer of any key here is kept as if the caller had asked.
        let keep =
            entries
            |> List.fold (fun kept e -> List.fold (fun k key -> Set.add key k) kept e.Keeps) keep

        let expired, live =
            entries
            |> List.filter (fun e -> not (Set.contains e.Key keep))
            |> List.partition (fun e -> now - e.Newest > settings.Ttl)

        let expiredKeys = expired |> List.map _.Key |> Set.ofList

        let remaining =
            entries |> List.filter (fun e -> not (Set.contains e.Key expiredKeys))

        let total = ref (remaining |> List.sumBy _.Size)
        let trimmed = Collections.Generic.HashSet<string>()
        let overBudget = Collections.Generic.List<GcEntry>()
        let mutable share = None

        let sizeLeft (e: GcEntry) =
            e.Size
            - (e.Superseded
               |> List.sumBy (fun s -> if trimmed.Contains s.File then s.Size else 0L))

        let evict (e: GcEntry) =
            total.Value <- total.Value - sizeLeft e
            overBudget.Add e

        if total.Value > settings.MaxBytes then
            let kinds =
                remaining
                |> List.groupBy _.Kind
                |> List.map (fun (kind, es) -> kind, es |> List.sumBy _.Size)

            let cap = fairShare settings.MaxBytes (kinds |> List.map snd)
            share <- Some cap

            for (kind, size) in kinds |> List.filter (fun (_, size) -> size > cap) do
                let mutable kindTotal = size
                let own = live |> List.filter (fun e -> e.Kind = kind)

                for s in own |> List.collect _.Superseded |> List.sortBy _.RecordedAt do
                    if kindTotal > cap then
                        kindTotal <- kindTotal - s.Size
                        total.Value <- total.Value - s.Size
                        trimmed.Add s.File |> ignore

                for e in own do
                    if kindTotal > cap then
                        kindTotal <- kindTotal - sizeLeft e
                        evict e

            for e in live do
                if total.Value > settings.MaxBytes && not (overBudget.Contains e) then
                    evict e

        let trimmedRecords =
            [ for e in remaining do
                  for s in e.Superseded do
                      if trimmed.Contains s.File then
                          try
                              File.Delete s.File
                              yield e.Kind, s.Size
                          with ex ->
                              settings.Log $"outcome store: could not trim %s{s.File}: %s{ex.Message}" ]

        let evicted =
            [ for e in expired @ List.ofSeq overBudget |> List.sortBy _.Newest do
                  try
                      Directory.Delete(e.Dir, true)

                      yield
                          e,
                          (if Set.contains e.Key expiredKeys then
                               e.Size
                           else
                               sizeLeft e)
                  with ex ->
                      settings.Log $"outcome store: could not evict %s{e.Key.Text}: %s{ex.Message}" ]

        if not (evicted.IsEmpty && trimmedRecords.IsEmpty) then
            // "; check 3, format 1", or nothing when no record of any kind went that way.
            let byKind (before: string) (after: string) (kinds: string list) =
                if kinds.IsEmpty then
                    ""
                else
                    kinds
                    |> List.countBy id
                    |> List.sortBy fst
                    |> List.map (fun (kind, n) -> $"%s{kind} %d{n}")
                    |> String.concat ", "
                    |> fun text -> before + text + after

            let bytes = (evicted |> List.sumBy snd) + (trimmedRecords |> List.sumBy snd)

            let shareText =
                share
                |> Option.map (fun cap -> $"; a kind's share is %d{cap} byte(s)")
                |> Option.defaultValue ""

            let keyKinds = evicted |> List.map (fun (e, _) -> e.Kind) |> byKind "; " ""
            let trimmedKinds = trimmedRecords |> List.map fst |> byKind " (" ")"

            settings.Log(
                $"outcome store: gc evicted %d{evicted.Length} key(s) (%d{expired.Length} past the ttl, "
                + $"%d{overBudget.Count} over the byte budget of %d{settings.MaxBytes}%s{shareText}"
                + $"%s{keyKinds}) and "
                + $"%d{trimmedRecords.Length} superseded record(s)%s{trimmedKinds}, "
                + $"%d{bytes} byte(s); %d{total.Value} byte(s) remain"
            )

        evicted |> List.map (fun (e, _) -> e.Key)

/// The store of `repoRoot`'s repository with the default TTL and byte budget.
let openFor (repoRoot: string) (log: string -> unit) =
    Store(
        { Root = directoryFor repoRoot
          Ttl = defaultTtl
          MaxBytes = defaultMaxBytes
          Log = log }
    )
