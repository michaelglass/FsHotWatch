/// The `test-baseline` record (AUTOMATION-1587, design §4.1): "every runnable project passed
/// full-scope on tree T", filed box-wide in the ADR-059 outcome store under T's receipt
/// identity, with what a neighbour tree would need to diff against T.
///
/// Step 2 wrote these records in shadow, to be counted and measured (records per land,
/// bytes). Their one reader is `checkoutRecordFor` (steps 4-5): a restarted daemon whose
/// index is sealed at a tree T its own checkout passed in full (ADR-071) holds its first
/// launch for the cold scan's diff T to T' and selects from it, with T's manifest as the
/// evidence the tree moved from. No skip, cache or verdict consults them.
///
/// A record is filed only when all of these hold at a run's completion:
///   * the completion EARNED the full-suite baseline (`earnsBaseline`: a full run that passed
///     every runnable project — never a red or stop-on-fail run, AUTOMATION-1581);
///   * every runnable project's newest full-scope record at its CURRENT K_P is a pass, filed
///     by this run or REPLAYED by it from another run: K_P names the project's inputs as
///     they are now, so either says the same (AUTOMATION-1682, ADR-071);
///   * the impact index is sealed at the receipt tree (`SealedTree` = T): the per-file
///     analysis identities read from it describe T's bytes, not an earlier tree's.
///
/// Its outputs name the run, the K_P full-run key each project rests on (and, for a replayed
/// pass, the run and checkout that filed it), the analyser build,
/// and T's manifest: per file, its content hash and the analysis identity the index held for
/// it (`file_keys`), which is what finds the file's analysis record at T (`AnalysisRecords`).
///
/// One INDEX record per repository store names the newest `IndexCapacity` baselines. It is
/// rewritten in place (`OutcomeStore.Store.Supersede`), never appended: appended, each
/// pointer would supersede the last and GC would trim exactly the older candidates. GC keeps
/// every baseline the live index names (`OutcomeStore.keepsOf`).
module FsHotWatch.TestPrune.TestBaseline

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch

/// The kind of a baseline record.
[<Literal>]
let Kind = "test-baseline"

/// The kind of the per-repository index record.
[<Literal>]
let IndexKind = "test-baseline-index"

/// The format of a baseline record's outputs.
[<Literal>]
let Schema = "fshw-test-baseline-v1"

/// The format of the index record's outputs. Any other is read as an empty index.
[<Literal>]
let IndexSchema = "fshw-test-baseline-index-v1"

/// How many baselines the index names, newest first (design §7, Q1).
[<Literal>]
let IndexCapacity = 20

/// The key T's baseline is filed under: T's receipt identity (ADR-013 content address).
let keyOf (tree: string) : ActionKey.ActionKey = ActionKey.create Kind [ "tree", tree ]

/// The one fixed key of the index. The store is already namespaced by repository.
let indexKey: ActionKey.ActionKey =
    ActionKey.create IndexKind [ "index", IndexSchema ]

/// What a completion knows when it asks for a baseline.
type Facts =
    {
        /// The completed run, as the outcome records name it (`Guid.ToString "N"`).
        RunId: string
        EarnsBaseline: bool
        RunnableProjects: Set<string>
        /// The receipt identity of the tree the last `BatchChecked` folded over.
        SealedTree: string option
        /// The receipt tree read at completion: its identity and per-file entries.
        ReceiptTree: (string * (string * string) list) option
    }

/// Why no baseline was filed. Each names the condition that failed.
[<RequireQualifiedAccess>]
type Refusal =
    /// No outcome store is wired.
    | NoStore
    /// The completion did not earn the full-suite baseline.
    | NotEarned
    /// The tree could not be read at completion.
    | NoReceiptTree
    /// The index was sealed at another tree, or never sealed.
    | NotSealed of sealedTree: string option * receiptTree: string
    /// A runnable project's newest full-scope record at its current K_P is missing or a fail.
    | LacksFullPass of project: string * why: string
    /// The store did not file the record.
    | NotFiled of why: string

/// One entry of the index.
type IndexEntry =
    { Tree: string
      Baseline: string
      RunId: string
      At: DateTimeOffset }

/// The run and checkout a replayed full pass was earned in (AUTOMATION-1682). Every K_P
/// full-scope record a test run files names that run; one that names none is no run's pass
/// and is refused before it gets here (`fullPasses`).
type ReplayedFrom = { RunId: string; Worktree: string }

/// What a project's newest full-scope record at its current K_P is, when it is a pass.
[<RequireQualifiedAccess>]
type FullPass =
    /// Filed by the run that completed.
    | EarnedHere of key: string
    /// Filed by another run, which this one replayed: the record is keyed to the project's
    /// inputs as they are now, so it says what a pass of this run would (AUTOMATION-1682).
    | Replayed of key: string * from: ReplayedFrom

module FullPass =
    let key (pass: FullPass) =
        match pass with
        | FullPass.EarnedHere key
        | FullPass.Replayed(key, _) -> key

/// What a filed baseline amounted to, for the log.
type Written =
    {
        Key: ActionKey.KeyValue
        Tree: string
        Projects: int
        Files: int
        /// Manifest rows carrying an analysis identity (source files the index holds).
        Identified: int
        Bytes: int64
        IndexEntries: int
        IndexBytes: int64
        /// The projects whose full pass this run replayed from another run, and whose.
        Replayed: Map<string, ReplayedFrom>
    }

/// Who files, and when.
type Producer =
    { Binary: string
      Worktree: string
      Now: DateTimeOffset }

let private recordOf (key: ActionKey.ActionKey) (subject: string) (runId: string option) (producer: Producer) =
    { OutcomeStore.Record.ofAction key subject OutcomeStore.Outcome.Pass producer.Binary producer.Worktree producer.Now with
        Producer =
            { Binary = producer.Binary
              Worktree = producer.Worktree
              RunId = runId } }

let private parseIndex (outputs: string) : IndexEntry list =
    try
        use doc = JsonDocument.Parse outputs
        let o = doc.RootElement

        match Json.tryString o "schema", Json.tryProp o "entries" with
        | Some schema, Some entries when schema = IndexSchema && entries.ValueKind = JsonValueKind.Array ->
            [ for e in entries.EnumerateArray() do
                  match
                      Json.tryString e "tree",
                      Json.tryString e "baseline",
                      Json.tryString e "runId",
                      Json.tryString e "at"
                  with
                  | Some tree, Some baseline, Some runId, Some at ->
                      match DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                      | true, at ->
                          yield
                              { Tree = tree
                                Baseline = baseline
                                RunId = runId
                                At = at }
                      | _ -> ()
                  | _ -> () ]
        | _ -> []
    with :? JsonException ->
        []

/// The index's entries, newest first; empty when there is none or it cannot be read.
let readIndex (store: OutcomeRecords.IOutcomeStore) : IndexEntry list =
    match OutcomeStore.Through.lookupWithOutputs store indexKey.Value with
    | Some(_, Some outputs) -> parseIndex outputs
    | _ -> []

/// A checkout's own baseline, as its `test-baseline` record names it: what a restarted daemon
/// may diff its tree from (AUTOMATION-1587).
type CheckoutRecord =
    {
        Tree: string
        /// The record's key, as the log and the launch cite it.
        Key: string
        /// The run that filed it.
        RunId: string
        /// T's manifest: each path and its content hash.
        Entries: (string * string) list
    }

/// A filed record's outputs: always JSON, since the store files nothing else.
let private parseRecord (tree: string) (key: string) (outputs: string) : Result<CheckoutRecord, string> =
    use doc = JsonDocument.Parse outputs
    let o = doc.RootElement

    let field name =
        Json.tryString o name |> Option.defaultValue ""

    let schema = field "schema"
    let named = field "tree"

    if schema <> Schema then
        Error $"the record is of schema %s{schema}, not %s{Schema}"
    elif not (String.Equals(named, tree, StringComparison.Ordinal)) then
        Error $"the record names tree %s{named}"
    elif field "analyzer" <> AnalysisRecords.analyzerIdentity then
        Error "the record was filed by a different analyser build"
    else
        match Json.tryString o "runId", Json.tryProp o "manifest" with
        | Some runId, Some manifest when manifest.ValueKind = JsonValueKind.Array ->
            let row (r: JsonElement) =
                if r.ValueKind = JsonValueKind.Array && r.GetArrayLength() >= 2 then
                    match r[0].ValueKind, r[1].ValueKind with
                    | JsonValueKind.String, JsonValueKind.String -> Some(r[0].GetString(), r[1].GetString())
                    | _ -> None
                else
                    None

            let rows = [ for r in manifest.EnumerateArray() -> row r ]

            if rows |> List.exists Option.isNone then
                Error "the record's manifest has a malformed row"
            else
                Ok
                    { Tree = tree
                      Key = key
                      RunId = runId
                      Entries = List.choose id rows }
        | _ -> Error "the record has no run or no manifest"

/// The `test-baseline` record for exactly `tree`, when it is still the store's passing
/// answer for that tree, was filed by this analyser build, and carries a readable manifest;
/// otherwise why not. READ-ONLY. Every failure is a miss. The store's reads never throw: a
/// record it cannot read is one it does not hold.
let checkoutRecordFor (store: OutcomeRecords.IOutcomeStore) (tree: string) : Result<CheckoutRecord, string> =
    let key = keyOf tree

    match OutcomeStore.Through.lookupWithOutputs store key.Value with
    | None -> Error "no test-baseline record for that tree"
    | Some(record, _) when record.Outcome <> OutcomeStore.Outcome.Pass ->
        Error "the test-baseline record for that tree is not a pass"
    | Some(_, None) -> Error "the test-baseline record for that tree has no outputs"
    | Some(_, Some outputs) -> parseRecord tree key.Value.Text outputs

let private indexOutputs (entries: IndexEntry list) : string =
    let o = JsonObject()
    o["schema"] <- IndexSchema
    let keeps = JsonArray()
    let rows = JsonArray()

    for e in entries do
        keeps.Add(JsonValue.Create e.Baseline)
        let row = JsonObject()
        row["tree"] <- e.Tree
        row["baseline"] <- e.Baseline
        row["runId"] <- e.RunId
        row["at"] <- e.At.ToString("o", CultureInfo.InvariantCulture)
        rows.Add row

    o[OutcomeStore.KeepsField] <- keeps
    o["entries"] <- rows
    o.ToJsonString()

/// Put `entry` at the front of the index, drop any older entry for the same tree, keep the
/// newest `IndexCapacity`, and rewrite the index as its key's one record. Returns the entry
/// count and the record's bytes, or `None` when it could not be filed.
let appendIndex (store: OutcomeRecords.IOutcomeStore) (producer: Producer) (entry: IndexEntry) : (int * int64) option =
    let entries =
        entry :: (readIndex store |> List.filter (fun e -> e.Tree <> entry.Tree))
        |> List.truncate IndexCapacity

    OutcomeStore.Through.supersede store (recordOf indexKey "index" None producer) (Some(indexOutputs entries))
    |> Option.map (fun bytes -> entries.Length, bytes)

/// Each runnable project's full pass at its current K_P, or why it has none. The newest
/// full-scope record at K_P decides, whichever run filed it. K_P names the project's inputs
/// as they are now (its closure, packages, toolchain, loaded environment and scope), so a
/// pass another run filed there says what a pass of this run would, and it is the very
/// record this run's own green replayed (AUTOMATION-1682, ADR-071). A newer fail there
/// refuses; a record at another K_P is no record.
let private fullPasses
    (store: OutcomeRecords.IOutcomeStore)
    (runId: string)
    (runnable: Set<string>)
    (fullKeys: Map<string, Result<string, string>>)
    : Result<Map<string, FullPass>, Refusal list> =
    let judged =
        [ for project in runnable ->
              project,
              match Map.tryFind project fullKeys with
              | None ->
                  Error
                      "no K_P: the project model does not name it (not in the project model; a test project outside the daemon's discovery roots is never modelled)"
              | Some(Error why) -> Error $"no K_P: %s{why}"
              | Some(Ok text) ->
                  match ActionKey.KeyValue.tryParse text with
                  | None -> Error $"no K_P: %s{text} is not a key"
                  | Some key ->
                      match OutcomeStore.Through.lookup store key with
                      | None -> Error "no full-scope record at its current K_P"
                      | Some r when r.Outcome = OutcomeStore.Outcome.Fail ->
                          Error "its newest full-scope record is a fail"
                      | Some r ->
                          match r.Producer.RunId with
                          | None ->
                              Error
                                  $"its full-scope pass, filed in %s{r.Producer.Worktree}, names no run (every test run names itself)"
                          | Some earner when earner = runId -> Ok(FullPass.EarnedHere key.Text)
                          | Some earner ->
                              Ok(
                                  FullPass.Replayed(
                                      key.Text,
                                      { RunId = earner
                                        Worktree = r.Producer.Worktree }
                                  )
                              ) ]

    match
        judged
        |> List.choose (fun (project, r) ->
            match r with
            | Error why -> Some(Refusal.LacksFullPass(project, why))
            | Ok _ -> None)
    with
    | [] ->
        judged
        |> List.choose (fun (project, r) -> r |> Result.toOption |> Option.map (fun k -> project, k))
        |> Map.ofList
        |> Ok
    | refusals -> Error refusals

let private baselineOutputs
    (facts: Facts)
    (tree: string)
    (entries: (string * string) list)
    (projects: Map<string, FullPass>)
    (fileKeys: Map<string, string>)
    : string =
    let o = JsonObject()
    o["schema"] <- Schema
    o["runId"] <- facts.RunId
    o["tree"] <- tree
    o["analyzer"] <- AnalysisRecords.analyzerIdentity
    let p = JsonObject()
    let replayed = JsonObject()

    for KeyValue(project, pass) in projects do
        p[project] <- FullPass.key pass

        match pass with
        | FullPass.EarnedHere _ -> ()
        | FullPass.Replayed(_, from) ->
            let r = JsonObject()
            r["runId"] <- from.RunId
            r["worktree"] <- from.Worktree
            replayed[project] <- r

    o["projects"] <- p
    o["replayed"] <- replayed
    let manifest = JsonArray()

    for path, hash in entries do
        let identity =
            match Map.tryFind path fileKeys with
            | Some id -> JsonValue.Create id :> JsonNode
            | None -> null

        manifest.Add(JsonArray(JsonValue.Create path, JsonValue.Create hash, identity))

    o["manifest"] <- manifest
    o.ToJsonString()

/// File T's baseline and name it in the index, or say which conditions refused it. The
/// cheap conditions are judged first and all of them reported; the projects' K_P keys
/// (`fullKeys`, which hashes the tree) and the index's file keys are read only once those
/// hold. Never throws for a store that cannot be written: that is `NotFiled`.
let file
    (store: OutcomeRecords.IOutcomeStore option)
    (fullKeys: unit -> Map<string, Result<string, string>>)
    (fileKeys: unit -> Map<string, string>)
    (producer: Producer)
    (facts: Facts)
    : Result<Written, Refusal list> =
    let cheap =
        [ if store.IsNone then
              Refusal.NoStore
          if not facts.EarnsBaseline then
              Refusal.NotEarned
          match facts.ReceiptTree with
          | None -> Refusal.NoReceiptTree
          | Some(tree, _) when facts.SealedTree <> Some tree -> Refusal.NotSealed(facts.SealedTree, tree)
          | Some _ -> () ]

    match cheap, store, facts.ReceiptTree with
    | [], Some store, Some(tree, entries) ->
        fullPasses store facts.RunId facts.RunnableProjects (fullKeys ())
        |> Result.bind (fun projects ->
            let keys = fileKeys ()
            let key = keyOf tree
            let outputs = baselineOutputs facts tree entries projects keys

            match
                OutcomeStore.Through.supersede store (recordOf key tree (Some facts.RunId) producer) (Some outputs)
            with
            | None -> Error [ Refusal.NotFiled "the outcome store did not file the baseline" ]
            | Some bytes ->
                let entry =
                    { Tree = tree
                      Baseline = key.Value.Text
                      RunId = facts.RunId
                      At = producer.Now }

                let indexEntries, indexBytes =
                    appendIndex store producer entry |> Option.defaultValue (0, 0L)

                Ok
                    { Key = key.Value
                      Tree = tree
                      Projects = projects.Count
                      Files = entries.Length
                      Identified = entries |> List.filter (fun (path, _) -> keys.ContainsKey path) |> List.length
                      Bytes = bytes
                      IndexEntries = indexEntries
                      IndexBytes = indexBytes
                      Replayed =
                        projects
                        |> Map.toList
                        |> List.choose (fun (project, pass) ->
                            match pass with
                            | FullPass.EarnedHere _ -> None
                            | FullPass.Replayed(_, from) -> Some(project, from))
                        |> Map.ofList })
    | refusals, _, _ -> Error refusals

let private token (refusal: Refusal) =
    match refusal with
    | Refusal.NoStore -> "no-store"
    | Refusal.NotEarned -> "not-earned"
    | Refusal.NoReceiptTree -> "no-receipt-tree"
    | Refusal.NotSealed _ -> "not-sealed"
    | Refusal.LacksFullPass _ -> "lacks-full-pass"
    | Refusal.NotFiled _ -> "not-filed"

let private detail (refusal: Refusal) =
    match refusal with
    | Refusal.NoStore -> "no outcome store is wired"
    | Refusal.NotEarned -> "the run did not earn the full-suite baseline (not a full run that passed every project)"
    | Refusal.NoReceiptTree -> "the tree could not be read at completion"
    | Refusal.NotSealed(sealedTree, receipt) ->
        let sealedText = sealedTree |> Option.defaultValue "none"
        $"index not sealed at the receipt tree: sealed=%s{sealedText}, receipt=%s{receipt}"
    | Refusal.LacksFullPass(project, why) -> $"project %s{project} lacks a full-pass K_P record: %s{why}"
    | Refusal.NotFiled why -> why

/// The completion's one log line. Every line starts `test-baseline: written` or
/// `test-baseline: not written`, carries `run=`, and a refusal names each failed condition
/// as a `reason=` token, so a daemon log can be counted by grep.
let describe (runId: string) (elapsedMs: int64) (outcome: Result<Written, Refusal list>) : string =
    match outcome with
    | Ok w ->
        $"test-baseline: written run=%s{runId} tree=%s{w.Tree} projects=%d{w.Projects} files=%d{w.Files} "
        + $"identities=%d{w.Identified} bytes=%d{w.Bytes} index=%d{w.IndexEntries}/%d{IndexCapacity} "
        + $"index-bytes=%d{w.IndexBytes} replayed=%d{w.Replayed.Count} ms=%d{elapsedMs}"
    | Error refusals ->
        let tokens = refusals |> List.map token |> List.distinct |> String.concat ","
        let details = refusals |> List.map detail |> String.concat "; "
        $"test-baseline: not written run=%s{runId} reason=%s{tokens} ms=%d{elapsedMs} — %s{details}"
