/// L1, the outcome store's records (re-architecture Phase 1, AUTOMATION-1695): what a record
/// holds, its file format, what ONE read of a key found, and the store interface. No I/O: the
/// disk store (`FsHotWatch.OutcomeStore`) and the simulation's in-memory store both implement
/// `IOutcomeStore` over this one codec, so the format is defined once.
///
/// FORMAT (ADR-059 §2; unchanged from the disk store's, byte for byte, RecordBytesGoldenTests):
/// an envelope `{"schema","checksum","payload"}` whose checksum is the SHA-256 of the payload
/// text; the payload carries the key, its kind and labelled inputs (which must fold back to the
/// key), the subject, the outcome, the counts / duration / run id of a test run, the producer,
/// the instant and the outputs. A record's file name is the SHA-256 of its payload.
module FsHotWatch.OutcomeRecords

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// The record format. A file stating any other schema is not a record (`Rejected.UnknownSchema`).
[<Literal>]
let Schema = "fshw-outcome-v1"

/// Test counts.
type Counts =
    { Total: int
      Passed: int
      Failed: int
      Skipped: int }

/// A test project's run, as its record keeps it.
type TestRun =
    {
        /// The run it was; `None` when the verdict did not attribute the suite to a run.
        RunId: string option
        Counts: Counts
        /// How long it took; `None` when the run was not timed.
        DurationMs: int64 option
    }

/// What the action that filed a record ran. Only a test run has counts, so code about any
/// other record cannot name them.
[<RequireQualifiedAccess>]
type Ran =
    /// A test project's run.
    | TestRun of TestRun
    /// Something a test run earned without counts of its own, such as a baseline: the run.
    | InRun of runId: string
    /// An action that ran no tests: a check, an analysis, a plugin, an options evaluation.
    | NonTest

/// Who produced a record.
type Producer =
    {
        /// The fshw build that produced it.
        Binary: string
        /// The checkout it was produced in.
        Worktree: string
    }

/// One record, as the store files it.
type StoredRecord =
    {
        Key: ActionKey.ActionKey
        /// What the action was about, for a reader: a project, a plugin name.
        Subject: string
        Outcome: LookupVerdict.Outcome
        Ran: Ran
        Producer: Producer
        RecordedAt: DateTimeOffset
        /// What the action produced, as JSON (ADR-059 §14), for a record that carries outputs.
        /// Kind-erased here: which kinds always, sometimes or never carry outputs is the typed
        /// record layer's to state (`Outcome<'Kind>`), not the store's.
        /// OWED (AUTOMATION-1695, decided 2026-11-14): when callers move to `Outcome<'Kind>`, each
        /// kind's outputs get their own type there and this option goes. Not an Outputs union here:
        /// that would be this option under another name.
        Outputs: string option
    }

/// A record as one read found it: the file it is filed as (a name the store understands) and the record.
type Filed = { File: string; Record: StoredRecord }

/// A file filed under a key that is not a record of that key.
[<RequireQualifiedAccess>]
type Rejected =
    /// Not an envelope, a bad checksum, a payload that does not parse or whose inputs do not
    /// fold to its key: a torn or corrupt write.
    | Torn of why: string
    /// An envelope of a schema this build does not read.
    | UnknownSchema of schema: string
    /// A whole record, but of another key.
    | OtherKey of filedUnder: ActionKey.KeyValue

module Rejected =
    /// Why, for a log line.
    let describe (rejected: Rejected) : string =
        match rejected with
        | Rejected.Torn why -> why
        | Rejected.UnknownSchema schema -> $"unknown schema %s{schema}"
        | Rejected.OtherKey _ -> "the record is filed under another key"

/// What ONE read of one key found.
[<RequireQualifiedAccess>]
type KeyRead =
    /// No file is filed under the key: never filed, or collected. A miss.
    | NothingFiled
    /// Files are filed under the key and none is a record of it. A miss.
    | OnlyRejected of FsHotWatch.Core.NonEmpty<string * Rejected>
    /// Its records: the newest (last by instant, a fail outranking a pass at the same instant),
    /// the older ones oldest first, and any files under the key that are not its records.
    | Answered of newest: Filed * older: Filed list * rejected: (string * Rejected) list
    /// A file under the key could not be READ, so it says nothing about what it holds, and it
    /// may be the newest fail. The whole key is a miss and nothing is moved (AUTOMATION-1568);
    /// a reader asking "is a newer fail filed?" must not take this for `NothingFiled`
    /// (AUTOMATION-1615).
    | Unreadable of file: string * why: string

/// What filing a record did.
[<RequireQualifiedAccess>]
type Filing =
    | Filed
    /// The same bytes were already filed: a record's name is the digest of its payload.
    | AlreadyFiled
    /// It could not be filed. That costs a future lookup, never the run that produced it.
    | NotFiled of why: string

/// L1: an outcome store. Time comes in as an argument; nothing here reads a clock.
type IOutcomeStore =
    /// Everything filed under a key, read once.
    abstract Read: ActionKey.KeyValue -> KeyRead
    /// Whether any file is filed under the key, by name alone (AUTOMATION-1609): nothing is
    /// read or verified, so a corrupt record answers yes.
    abstract HoldsKey: ActionKey.KeyValue -> bool
    /// File a record.
    abstract File: StoredRecord -> Filing
    /// File a record as its key's only one: file it, then drop the key's others (AUTOMATION-1587).
    abstract Supersede: StoredRecord -> Filing

/// The file format.
module RecordCodec =
    let private sha256 (text: string) =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

    let private outcomeToken (outcome: LookupVerdict.Outcome) =
        match outcome with
        | LookupVerdict.Outcome.Pass -> "pass"
        | LookupVerdict.Outcome.Fail -> "fail"

    /// The payload text of `r`.
    let encodePayload (r: StoredRecord) : string =
        let o = JsonObject()
        o["key"] <- r.Key.Value.Text
        o["kind"] <- r.Key.Kind
        let inputs = JsonArray()

        for (label, digest) in r.Key.Inputs do
            inputs.Add(JsonArray(JsonValue.Create label, JsonValue.Create digest))

        o["inputs"] <- inputs
        o["subject"] <- r.Subject
        o["outcome"] <- outcomeToken r.Outcome

        match r.Ran with
        | Ran.TestRun run ->
            let counts = JsonObject()
            counts["total"] <- run.Counts.Total
            counts["passed"] <- run.Counts.Passed
            counts["failed"] <- run.Counts.Failed
            counts["skipped"] <- run.Counts.Skipped
            o["counts"] <- counts

            match run.DurationMs with
            | Some ms -> o["durationMs"] <- ms
            | None -> ()
        | Ran.InRun _
        | Ran.NonTest -> ()

        let producer = JsonObject()
        producer["binary"] <- r.Producer.Binary
        producer["worktree"] <- r.Producer.Worktree

        match r.Ran with
        | Ran.TestRun { RunId = Some run }
        | Ran.InRun run -> producer["runId"] <- run
        | Ran.TestRun { RunId = None }
        | Ran.NonTest -> ()

        o["producer"] <- producer
        o["recordedAt"] <- r.RecordedAt.ToString("o")

        match r.Outputs with
        | Some text -> o["outputs"] <- JsonNode.Parse text
        | None -> ()

        o.ToJsonString()

    /// The file text a payload is filed as.
    let envelope (payload: string) : string =
        let o = JsonObject()
        o["schema"] <- Schema
        o["checksum"] <- sha256 payload
        o["payload"] <- payload
        o.ToJsonString()

    /// The name a payload is filed under: its digest.
    let fileName (payload: string) : string = sha256 payload + ".json"

    let private property (el: JsonElement) (name: string) : JsonElement option =
        match el.ValueKind with
        | JsonValueKind.Object ->
            match el.TryGetProperty name with
            | true, value -> Some value
            | false, _ -> None
        | _ -> None

    let private required (el: JsonElement) (name: string) : JsonElement =
        match property el name with
        | Some v -> v
        | None -> raise (FormatException $"no %s{name}")

    let private stringOf (el: JsonElement) (name: string) : string option =
        property el name
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.String -> Some(v.GetString())
            | _ -> None)

    let private parsePayload (payload: string) : StoredRecord =
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
            | "pass" -> LookupVerdict.Outcome.Pass
            | "fail" -> LookupVerdict.Outcome.Fail
            | other -> raise (FormatException $"unknown outcome %s{other}")

        let producer = required o "producer"
        let runId = stringOf producer "runId"
        let durationMs = property o "durationMs" |> Option.map _.GetInt64()

        let ran =
            match property o "counts", runId, durationMs with
            | Some c, _, _ ->
                Ran.TestRun
                    { RunId = runId
                      Counts =
                        { Total = (required c "total").GetInt32()
                          Passed = (required c "passed").GetInt32()
                          Failed = (required c "failed").GetInt32()
                          Skipped = (required c "skipped").GetInt32() }
                      DurationMs = durationMs }
            | None, _, Some _ -> raise (FormatException "a duration without counts")
            | None, Some run, None -> Ran.InRun run
            | None, None, None -> Ran.NonTest

        { Key = key
          Subject = str o "subject"
          Outcome = outcome
          Ran = ran
          Producer =
            { Binary = str producer "binary"
              Worktree = str producer "worktree" }
          RecordedAt = DateTimeOffset.Parse(str o "recordedAt", CultureInfo.InvariantCulture)
          Outputs = property o "outputs" |> Option.map _.GetRawText() }

    /// A file's text as a record of whatever key it states, or why it is not a record.
    let decodeAny (text: string) : Result<StoredRecord, Rejected> =
        try
            use doc = JsonDocument.Parse text
            let o = doc.RootElement

            match stringOf o "schema" with
            | None -> Error(Rejected.Torn "no schema")
            | Some schema when schema <> Schema -> Error(Rejected.UnknownSchema schema)
            | Some _ ->
                match stringOf o "checksum", stringOf o "payload" with
                | Some checksum, Some payload when sha256 payload = checksum -> Ok(parsePayload payload)
                | Some _, Some _ -> Error(Rejected.Torn "checksum mismatch")
                | _ -> Error(Rejected.Torn "no payload")
        with
        | :? JsonException as ex -> Error(Rejected.Torn ex.Message)
        | :? FormatException as ex -> Error(Rejected.Torn ex.Message)
        | :? InvalidOperationException as ex -> Error(Rejected.Torn ex.Message)
        | :? ArgumentException as ex -> Error(Rejected.Torn ex.Message)
        | :? IndexOutOfRangeException as ex -> Error(Rejected.Torn ex.Message)

    /// A file's text as a record of `expected`, or why it is not one.
    let decode (expected: ActionKey.KeyValue) (text: string) : Result<StoredRecord, Rejected> =
        decodeAny text
        |> Result.bind (fun record ->
            if record.Key.Value = expected then
                Ok record
            else
                Error(Rejected.OtherKey record.Key.Value))

/// Reads a key's files into a `KeyRead`, and the views of one.
module KeyRead =
    /// Newest last; at the same instant a pass sorts before a fail, so the last is the fail.
    /// Stable, so records of one instant and one outcome keep the order they were given in.
    let chronological (records: Filed list) : Filed list =
        records
        |> List.sortBy (fun f ->
            f.Record.RecordedAt,
            (match f.Record.Outcome with
             | LookupVerdict.Outcome.Fail -> 1
             | LookupVerdict.Outcome.Pass -> 0))

    /// What `files` (each a name and its text, or why it could not be read), all filed under
    /// `key`, amount to. One unreadable file makes the whole key `Unreadable`.
    let ofFiles (key: ActionKey.KeyValue) (files: (string * Result<string, string>) list) : KeyRead =
        let unreadable =
            files
            |> List.tryPick (fun (name, text) ->
                match text with
                | Error why -> Some(name, why)
                | Ok _ -> None)

        match unreadable with
        | Some(name, why) -> KeyRead.Unreadable(name, why)
        | None ->
            let decoded =
                files
                |> List.choose (fun (name, text) ->
                    match text with
                    | Ok text -> Some(name, RecordCodec.decode key text)
                    | Error _ -> None)

            let records =
                decoded
                |> List.choose (fun (name, r) ->
                    match r with
                    | Ok record -> Some { File = name; Record = record }
                    | Error _ -> None)
                |> chronological

            let rejected =
                decoded
                |> List.choose (fun (name, r) ->
                    match r with
                    | Error why -> Some(name, why)
                    | Ok _ -> None)

            match List.rev records, FsHotWatch.Core.NonEmpty.ofList rejected with
            | [], None -> KeyRead.NothingFiled
            | [], Some only -> KeyRead.OnlyRejected only
            | newest :: olderNewestFirst, _ -> KeyRead.Answered(newest, List.rev olderNewestFirst, rejected)

    /// The files under the key that are not its records, for a store to quarantine.
    let rejected (read: KeyRead) : (string * Rejected) list =
        match read with
        | KeyRead.OnlyRejected only -> FsHotWatch.Core.NonEmpty.toList only
        | KeyRead.Answered(_, _, rejected) -> rejected
        | KeyRead.NothingFiled
        | KeyRead.Unreadable _ -> []

    /// The LOOKUP's view: the newest record's outcome, or `None` for a miss of any kind.
    ///
    /// Only for a caller that does not need to know WHY a key missed. One that does (the
    /// clean-recompute audit, AUTOMATION-1615's "is a newer fail filed?", analytics) matches
    /// on `KeyRead` itself: `None` here cannot tell `Unreadable` from `NothingFiled`.
    let outcome (read: KeyRead) : LookupVerdict.Outcome option =
        match read with
        | KeyRead.Answered(newest, _, _) -> Some newest.Record.Outcome
        | KeyRead.NothingFiled
        | KeyRead.OnlyRejected _
        | KeyRead.Unreadable _ -> None

    /// The key's records, oldest first; empty for a miss of any kind. The same caution as
    /// `outcome`: a caller that must tell the misses apart matches on `KeyRead`.
    let history (read: KeyRead) : StoredRecord list =
        match read with
        | KeyRead.Answered(newest, older, _) -> (older @ [ newest ]) |> List.map _.Record
        | KeyRead.NothingFiled
        | KeyRead.OnlyRejected _
        | KeyRead.Unreadable _ -> []

/// An `IOutcomeStore` in memory, over the same codec: for the simulation (plan §7.4), where a
/// torn file, an unreadable one or a lagging peer is injected at the interface. A rejected file
/// is moved to `Quarantined` on read, as the disk store moves it; an unreadable one is not moved.
type InMemoryOutcomeStore() =
    let files =
        Collections.Generic.Dictionary<ActionKey.KeyValue, ResizeArray<string * Result<string, string>>>()

    let quarantined = ResizeArray<string * Rejected>()

    let filesOf (key: ActionKey.KeyValue) =
        match files.TryGetValue key with
        | true, list -> list
        | false, _ ->
            let list = ResizeArray()
            files[key] <- list
            list

    /// As the disk store files (AUTOMATION-1701): a file of the record's name that holds exactly
    /// its bytes is left alone; one that is unreadable or holds other bytes is replaced in place.
    let file (record: StoredRecord) =
        let payload = RecordCodec.encodePayload record
        let name = RecordCodec.fileName payload
        let text = RecordCodec.envelope payload
        let list = filesOf record.Key.Value

        match Seq.tryFindIndex (fun (n, _) -> n = name) list with
        | Some i when list[i] = (name, Ok text) -> name, Filing.AlreadyFiled
        | Some i ->
            list[i] <- (name, Ok text)
            name, Filing.Filed
        | None ->
            list.Add(name, Ok text)
            name, Filing.Filed

    /// Put `text` under `key` as file `name`, as a torn or foreign write would.
    member _.Plant(key: ActionKey.KeyValue, name: string, text: string) = (filesOf key).Add(name, Ok text)

    /// Put a file under `key` that cannot be read.
    member _.PlantUnreadable(key: ActionKey.KeyValue, name: string, why: string) = (filesOf key).Add(name, Error why)

    /// The names filed under `key`, in filing order.
    member _.FileNames(key: ActionKey.KeyValue) : string list =
        match files.TryGetValue key with
        | true, list -> list |> Seq.map fst |> List.ofSeq
        | false, _ -> []

    /// Every key anything is filed under.
    member _.Keys: ActionKey.KeyValue list = files.Keys |> List.ofSeq

    /// The text of file `name` under `key`, when it is there and readable.
    member _.TextOf(key: ActionKey.KeyValue, name: string) : string option =
        match files.TryGetValue key with
        | true, list ->
            list
            |> Seq.tryPick (fun (n, text) ->
                match text with
                | Ok text when n = name -> Some text
                | Ok _
                | Error _ -> None)
        | false, _ -> None

    /// Replace file `name` under `key` with `text` (a torn rewrite), or make it unreadable.
    member _.Replace(key: ActionKey.KeyValue, name: string, text: Result<string, string>) =
        let list = filesOf key

        match Seq.tryFindIndex (fun (n, _) -> n = name) list with
        | Some i -> list[i] <- (name, text)
        | None -> list.Add(name, text)

    /// The files a read moved out, with why.
    member _.Quarantined: (string * Rejected) list = List.ofSeq quarantined

    interface IOutcomeStore with
        member _.Read key =
            let read =
                match files.TryGetValue key with
                | true, list -> KeyRead.ofFiles key (List.ofSeq list)
                | false, _ -> KeyRead.NothingFiled

            for name, why in KeyRead.rejected read do
                (filesOf key).RemoveAll(fun (n, _) -> n = name) |> ignore
                quarantined.Add(name, why)

            read

        member _.HoldsKey key =
            match files.TryGetValue key with
            | true, list -> list.Count > 0
            | false, _ -> false

        member _.File record = snd (file record)

        member _.Supersede record =
            let name, filing = file record

            (filesOf record.Key.Value).RemoveAll(fun (n, _) -> n <> name) |> ignore

            filing
