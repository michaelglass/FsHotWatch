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

/// What a record concluded: the lookup's own type (`LookupVerdict.Outcome`, in FsHotWatch.Core).
type Outcome = LookupVerdict.Outcome

/// Test counts, for an action that ran tests.
type Counts = OutcomeRecords.Counts

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

/// `r` (with `outputs`) as FsHotWatch.Core's codec holds it (AUTOMATION-1695, L1). Counts and a
/// duration belong to a test run (`OutcomeRecords.Ran.TestRun`); a run id without counts is what
/// a run earned (`InRun`); neither is an action that ran no tests. No writer files a duration
/// without counts, and it is not carried.
let toStored (r: Record) (outputs: string option) : OutcomeRecords.StoredRecord =
    { Key = r.Key
      Subject = r.Subject
      Outcome = r.Outcome
      Ran =
        match r.Counts, r.Producer.RunId with
        | Some counts, runId ->
            OutcomeRecords.Ran.TestRun
                { RunId = runId
                  Counts = counts
                  DurationMs = r.DurationMs }
        | None, Some runId -> OutcomeRecords.Ran.InRun runId
        | None, None -> OutcomeRecords.Ran.NonTest
      Producer =
        { Binary = r.Producer.Binary
          Worktree = r.Producer.Worktree }
      RecordedAt = r.RecordedAt
      Outputs = outputs }

/// A stored record as this module's `Record`, with its outputs.
let ofStored (s: OutcomeRecords.StoredRecord) : Record * string option =
    let counts, durationMs, runId =
        match s.Ran with
        | OutcomeRecords.Ran.TestRun run -> Some run.Counts, run.DurationMs, run.RunId
        | OutcomeRecords.Ran.InRun runId -> None, None, Some runId
        | OutcomeRecords.Ran.NonTest -> None, None, None

    { Key = s.Key
      Subject = s.Subject
      Outcome = s.Outcome
      Counts = counts
      DurationMs = durationMs
      Producer =
        { Binary = s.Producer.Binary
          Worktree = s.Producer.Worktree
          RunId = runId }
      RecordedAt = s.RecordedAt },
    s.Outputs

/// The payload text of `r`: FsHotWatch.Core's codec (`OutcomeRecords.RecordCodec`).
let private serializePayload (r: Record) (outputs: string option) : string =
    OutcomeRecords.RecordCodec.encodePayload (toStored r outputs)

let private envelope (payload: string) : string =
    OutcomeRecords.RecordCodec.envelope payload

/// Read an envelope back: the record, or why it is not one.
let private decode (text: string) : Result<Record * string option, string> =
    OutcomeRecords.RecordCodec.decodeAny text
    |> Result.map ofStored
    |> Result.mapError OutcomeRecords.Rejected.describe

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

/// Every record in a store directory with its outputs, read without changing it.
type ScanWithOutputs =
    {
        /// Every readable record of every key with its outputs, oldest first.
        Records: (Record * string option) list
        /// Files that are not a record of the key they are filed under.
        Unreadable: int
    }

/// The record files of one key's directory, decoded: each readable record of that key with
/// its outputs, and `None` for each file that is not one.
let private readKeyDirectory (keyValue: ActionKey.KeyValue option) (dir: string) =
    [ for file in Directory.GetFiles(dir, "*.json") ->
          try
              match decode (File.ReadAllText file) with
              | Ok(record, outputs) when Some record.Key.Value = keyValue -> Some(record, outputs)
              | _ -> None
          with :? IOException ->
              None ]

/// `readAll` with each record's outputs (AUTOMATION-1633): a pure read for a reader that
/// compares what records produced, such as the clean-recompute audit.
let readAllWithOutputs (root: string) : ScanWithOutputs =
    let recordsDir = Path.Combine(root, "records")

    let read =
        if Directory.Exists recordsDir then
            [ for shard in Directory.GetDirectories recordsDir do
                  for dir in Directory.GetDirectories shard do
                      yield!
                          readKeyDirectory (ActionKey.KeyValue.tryParse (ActionKey.Prefix + Path.GetFileName dir)) dir ]
        else
            []

    { Records = read |> List.choose id |> chronological
      Unreadable = read |> List.filter Option.isNone |> List.length }

/// Read every record under `root` for analysis (AUTOMATION-1368). A pure read: unlike a
/// lookup, it quarantines nothing, so a report never changes the store it reports on. A
/// file that a lookup would quarantine is counted in `Unreadable` and left where it is.
let readAll (root: string) : Scan =
    let scan = readAllWithOutputs root

    { Records = scan.Records |> List.map fst
      Unreadable = scan.Unreadable }

/// One key's records under `root` with their outputs, oldest first, read without changing
/// the store (AUTOMATION-1633): where a lookup quarantines a file it cannot read, this
/// leaves it and counts it. Empty when the store holds no directory for the key.
let readKeyAt (root: string) (keyValue: ActionKey.KeyValue) : (Record * string option) list * int =
    let hex = keyValue.Text.Substring ActionKey.Prefix.Length
    let dir = Path.Combine(root, "records", hex.Substring(0, 2), hex)

    if Directory.Exists dir then
        let read = readKeyDirectory (Some keyValue) dir
        read |> List.choose id |> chronological, read |> List.filter Option.isNone |> List.length
    else
        [], 0

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

/// The file in a store's root that records when its last GC completed (AUTOMATION-1665).
[<Literal>]
let GcStampFile = "gc-stamp"

/// How often a test run asks for a GC (AUTOMATION-1665). A GC reads every record in the store,
/// so it costs O(store), not O(run): 110 s of a 125 s post-run tail on a laptop store. The TTL is
/// 14 days and the byte budget generous, so eviction is never urgent at run granularity, and
/// evicting up to this much later changes nothing a run can observe except its wall time.
let defaultGcInterval = TimeSpan.FromHours 6.0

/// What `Store.GcIfDue` did: ran (the keys it evicted, oldest first), or did not, because a GC
/// completed within the interval (when it did).
[<RequireQualifiedAccess>]
type GcRun =
    | Ran of evicted: ActionKey.KeyValue list
    | NotDue of lastRanAt: DateTimeOffset

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

    /// ONE read of `keyValue` (`OutcomeRecords.KeyRead.ofFiles`, in FsHotWatch.Core): a file that
    /// is not a record of the key is quarantined; one that could not be read moves nothing.
    let readOnce (keyValue: ActionKey.KeyValue) : OutcomeRecords.KeyRead =
        let dir = directoryOf keyValue

        let files =
            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.json")
            else
                [||]

        let read =
            files
            |> Array.toList
            |> List.map (fun file ->
                file,
                (try
                    Ok(File.ReadAllText file)
                 with ex ->
                     Error ex.Message))

        let keyRead = OutcomeRecords.KeyRead.ofFiles keyValue read

        match keyRead with
        | OutcomeRecords.KeyRead.Unreadable(file, why) ->
            settings.Log
                $"outcome store: %s{Path.GetFileName file} could not be read, so %s{keyValue.Text} is a miss: %s{why}"
        | OutcomeRecords.KeyRead.NothingFiled
        | OutcomeRecords.KeyRead.OnlyRejected _
        | OutcomeRecords.KeyRead.Answered _ ->
            for file, why in OutcomeRecords.KeyRead.rejected keyRead do
                quarantine file (OutcomeRecords.Rejected.describe why)

        keyRead

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
        match readOnce keyValue with
        | OutcomeRecords.KeyRead.Unreadable(file, why) -> Error $"%s{Path.GetFileName file} could not be read: %s{why}"
        | OutcomeRecords.KeyRead.Answered(newest, older, _) ->
            [ for filed in older @ [ newest ] ->
                  let record, outputs = ofStored filed.Record
                  record, (filed.File, outputs) ]
            |> Ok
        | OutcomeRecords.KeyRead.NothingFiled
        | OutcomeRecords.KeyRead.OnlyRejected _ -> Ok []

    let readKeyFiles (keyValue: ActionKey.KeyValue) : (Record * (string * string option)) list =
        tryReadKeyFiles keyValue |> Result.defaultValue []

    /// Every readable record filed under `keyValue` with its outputs, oldest first.
    let readKeyWithOutputs (keyValue: ActionKey.KeyValue) : (Record * string option) list =
        readKeyFiles keyValue
        |> List.map (fun (record, (_, outputs)) -> record, outputs)

    let readKey (keyValue: ActionKey.KeyValue) : Record list =
        readKeyWithOutputs keyValue |> List.map fst

    /// Whether `path` holds exactly `text`. A file that cannot be read does not.
    let holds (path: string) (text: string) : bool =
        try
            String.Equals(File.ReadAllText path, text, StringComparison.Ordinal)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> false

    /// File `text` at `path`, whose name is the digest of the payload `text` carries: true when
    /// this call put it there, false when the file already held exactly these bytes.
    ///
    /// A file already at `path` that is unreadable or holds other bytes (torn) is REPLACED by the
    /// same atomic rename (AUTOMATION-1701): the caller has just computed the bytes the name is
    /// the digest of, so they are what belongs there. Skipping them left the key a miss for as
    /// long as the bad file stayed. A reader still moves nothing (AUTOMATION-1568); only a writer
    /// holding byte-identical content repairs. Never delete-then-write: there is no moment with
    /// no file at the name. A rename that fails throws, for the caller's `NotFiled`.
    let writeOnce (path: string) (text: string) =
        Directory.CreateDirectory tmpDir |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = Path.Combine(tmpDir, Guid.NewGuid().ToString "N")

        try
            File.WriteAllText(temporary, text)

            try
                File.Move(temporary, path, false)
                true
            with :? IOException when File.Exists path ->
                if holds path text then
                    false
                else
                    File.Move(temporary, path, true)
                    true
        finally
            if File.Exists temporary then
                File.Delete temporary

    /// File `record`'s bytes once: the path it is filed at and whether this call filed it, or why
    /// it could not be filed. Never throws.
    let fileOnce (record: OutcomeRecords.StoredRecord) : Result<string * OutcomeRecords.Filing, string> =
        try
            let payload = OutcomeRecords.RecordCodec.encodePayload record

            let path =
                Path.Combine(directoryOf record.Key.Value, OutcomeRecords.RecordCodec.fileName payload)

            if writeOnce path (OutcomeRecords.RecordCodec.envelope payload) then
                Ok(path, OutcomeRecords.Filing.Filed)
            else
                Ok(path, OutcomeRecords.Filing.AlreadyFiled)
        with ex ->
            settings.Log $"outcome store: could not record %s{record.Key.Kind} %s{record.Subject}: %s{ex.Message}"
            Error ex.Message

    /// Delete every record file beside `kept`, under the same key.
    let deleteOthers (kept: string) =
        for other in Directory.GetFiles(Path.GetDirectoryName kept, "*.json") do
            if not (String.Equals(other, kept, StringComparison.Ordinal)) then
                try
                    File.Delete other
                with ex ->
                    settings.Log $"outcome store: could not delete the superseded %s{other}: %s{ex.Message}"

    let holdsKeyValue (keyValue: ActionKey.KeyValue) : bool =
        let dir = directoryOf keyValue

        try
            Directory.Exists dir
            && not (Seq.isEmpty (Directory.EnumerateFiles(dir, "*.json")))
        with :? DirectoryNotFoundException ->
            // GC removed the key between the two calls: nothing is filed.
            false

    member _.Root = root

    /// Whether any record file is filed under `key`, by name alone: no payload is read,
    /// checksummed or verified (AUTOMATION-1609). A corrupt record answers yes here; it is a
    /// miss, and quarantined, where it is read (`History`, `LookupWithOutputs`).
    member _.HoldsKey(key: ActionKey.ActionKey) : bool = holdsKeyValue key.Value

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

            writeOnce path (envelope payload) |> ignore
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
            writeOnce path (envelope payload) |> ignore

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

    /// `Gc`, unless one completed within `interval` of `now` (AUTOMATION-1665). When a GC
    /// last completed is the time written to `GcStampFile` after it; a missing or unreadable
    /// stamp means none has. Two daemons racing on the stamp may both run once, which is
    /// harmless: a GC can only turn a hit into a miss, and it spares what `keep` names.
    member this.GcIfDue(now: DateTimeOffset, keep: Set<ActionKey.KeyValue>, interval: TimeSpan) : GcRun =
        let stamp = Path.Combine(root, GcStampFile)

        let lastRanAt =
            if File.Exists stamp then
                match
                    DateTimeOffset.TryParse(
                        File.ReadAllText stamp,
                        Globalization.CultureInfo.InvariantCulture,
                        Globalization.DateTimeStyles.RoundtripKind
                    )
                with
                | true, at -> Some at
                | false, _ -> None
            else
                None

        match lastRanAt with
        | Some at when now - at < interval -> GcRun.NotDue at
        | _ ->
            let evicted = this.Gc(now, keep)

            try
                Directory.CreateDirectory root |> ignore
                File.WriteAllText(stamp, now.ToString("o", Globalization.CultureInfo.InvariantCulture))
            with
            | :? IOException
            | :? UnauthorizedAccessException as ex ->
                settings.Log $"outcome store: could not record the gc time in %s{stamp}: %s{ex.Message}"

            GcRun.Ran evicted

    /// L1 (AUTOMATION-1695): this store as FsHotWatch.Core's `IOutcomeStore`.
    interface OutcomeRecords.IOutcomeStore with
        member _.Read keyValue = readOnce keyValue
        member _.HoldsKey keyValue = holdsKeyValue keyValue

        member _.File record =
            match fileOnce record with
            | Ok(_, filing) -> filing
            | Error why -> OutcomeRecords.Filing.NotFiled why

        member _.Supersede record =
            match fileOnce record with
            | Ok(path, filing) ->
                deleteOthers path
                filing
            | Error why -> OutcomeRecords.Filing.NotFiled why


/// This module's `Record`, filed and read through any `IOutcomeStore` (AUTOMATION-1695, L1): the
/// disk store above, or FsHotWatch.Core's in-memory one in the simulation.
module Through =
    /// File `record` with `outputs`; whether it is filed (now or already). Never throws.
    let file (store: OutcomeRecords.IOutcomeStore) (record: Record) (outputs: string option) : bool =
        match store.File(toStored record outputs) with
        | OutcomeRecords.Filing.Filed
        | OutcomeRecords.Filing.AlreadyFiled -> true
        | OutcomeRecords.Filing.NotFiled _ -> false

    /// The newest record of `keyValue` with its outputs; `None` for a miss of any kind (the
    /// lookup's view, `KeyRead.outcome`).
    let lookupWithOutputs
        (store: OutcomeRecords.IOutcomeStore)
        (keyValue: ActionKey.KeyValue)
        : (Record * string option) option =
        match store.Read keyValue with
        | OutcomeRecords.KeyRead.Answered(newest, _, _) -> Some(ofStored newest.Record)
        | OutcomeRecords.KeyRead.NothingFiled
        | OutcomeRecords.KeyRead.OnlyRejected _
        | OutcomeRecords.KeyRead.Unreadable _ -> None

    /// The newest record of `keyValue`; `None` for a miss of any kind.
    let lookup (store: OutcomeRecords.IOutcomeStore) (keyValue: ActionKey.KeyValue) : Record option =
        lookupWithOutputs store keyValue |> Option.map fst

/// The store of `repoRoot`'s repository with the default TTL and byte budget.
let openFor (repoRoot: string) (log: string -> unit) =
    Store(
        { Root = directoryFor repoRoot
          Ttl = defaultTtl
          MaxBytes = defaultMaxBytes
          Log = log }
    )

/// Work a plugin files here, box-wide, and also caches per workspace (AUTOMATION-1609).
///
/// A per-workspace task-cache hit skips the work, and with it the filing: a workspace whose
/// own cache answered every file filed nothing, and every fresh workspace re-did the work
/// live. A cache entry records THAT the work was done, not what it produced, so it cannot
/// file the record itself. The rule is therefore: the per-workspace cache answers only
/// once the store holds the work's record; until then the work runs again, and is filed
/// only when its key is absent (AUTOMATION-1596: filing without a lookup re-filed one
/// analysis per checkout). `holds` says whether the store holds a USABLE record under a
/// key, as the plugin's reader would take it.
module FiledWork =
    /// Whether a per-workspace cache entry may stand in for work filed under `key`: always
    /// when nothing would be filed (no store, or work with no key), otherwise only once
    /// `store` holds it.
    let mayAnswerLocally
        (holds: 'Store -> ActionKey.ActionKey -> bool)
        (store: 'Store option)
        (key: ActionKey.ActionKey option)
        : bool =
        match store, key with
        | Some store, Some key -> holds store key
        | _ -> true

    /// File through `file` only when `store` holds nothing under `key`. Returns whether
    /// it filed.
    let fileIfAbsent
        (holds: 'Store -> ActionKey.ActionKey -> bool)
        (store: 'Store)
        (key: ActionKey.ActionKey)
        (file: unit -> bool)
        : bool =
        not (holds store key) && file ()
