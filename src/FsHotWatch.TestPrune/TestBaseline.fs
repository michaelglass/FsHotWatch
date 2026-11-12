/// The `test-baseline` record (AUTOMATION-1587, design §4.1): "every runnable project passed
/// full-scope on tree T", filed box-wide in the ADR-059 outcome store under T's receipt
/// identity, with what a neighbour tree would need to diff against T.
///
/// STEP 2 IS A SHADOW. These records are WRITTEN and logged, and nothing reads them for a
/// decision: no selection, skip, cache or verdict consults them. Their purpose is to be
/// counted and measured (records per land, bytes) before anything is allowed to rest on one.
///
/// A record is filed only when all of these hold at a run's completion:
///   * the completion EARNED the full-suite baseline (`earnsBaseline`: a full run that passed
///     every runnable project — never a red or stop-on-fail run, AUTOMATION-1581);
///   * every runnable project's newest full-scope record at its CURRENT K_P is a pass filed
///     by THIS run (a reuse earned elsewhere is not this run's, so it does not count);
///   * the impact index is sealed at the receipt tree (`SealedTree` = T): the per-file
///     analysis identities read from it describe T's bytes, not an earlier tree's.
///
/// Its outputs name the run, the K_P full-run key each project rests on, the analyser build,
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
    /// A runnable project has no full-scope pass from this run at its current K_P.
    | LacksFullPass of project: string * why: string
    /// The store did not file the record.
    | NotFiled of why: string

/// One entry of the index.
type IndexEntry =
    { Tree: string
      Baseline: string
      RunId: string
      At: DateTimeOffset }

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
let readIndex (store: OutcomeStore.Store) : IndexEntry list =
    match store.LookupWithOutputs indexKey with
    | Some(_, Some outputs) -> parseIndex outputs
    | _ -> []

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
let appendIndex (store: OutcomeStore.Store) (producer: Producer) (entry: IndexEntry) : (int * int64) option =
    let entries =
        entry :: (readIndex store |> List.filter (fun e -> e.Tree <> entry.Tree))
        |> List.truncate IndexCapacity

    store.Supersede(recordOf indexKey "index" None producer, Some(indexOutputs entries))
    |> Option.map (fun bytes -> entries.Length, bytes)

/// Each runnable project's full-scope key, or why it does not rest on a pass from `runId`.
let private fullPasses
    (store: OutcomeStore.Store)
    (runId: string)
    (runnable: Set<string>)
    (fullKeys: Map<string, Result<string, string>>)
    : Result<Map<string, string>, Refusal list> =
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
                      match store.Lookup key with
                      | None -> Error "no full-scope record at its current K_P"
                      | Some r when r.Outcome = OutcomeStore.Outcome.Fail ->
                          Error "its newest full-scope record is a fail"
                      | Some r when r.Producer.RunId <> Some runId ->
                          let earner = r.Producer.RunId |> Option.defaultValue "an unnamed run"
                          Error $"its full-scope pass is from run %s{earner} in %s{r.Producer.Worktree}, not this run"
                      | Some _ -> Ok key.Text ]

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
    (projects: Map<string, string>)
    (fileKeys: Map<string, string>)
    : string =
    let o = JsonObject()
    o["schema"] <- Schema
    o["runId"] <- facts.RunId
    o["tree"] <- tree
    o["analyzer"] <- AnalysisRecords.analyzerIdentity
    let p = JsonObject()

    for KeyValue(project, key) in projects do
        p[project] <- key

    o["projects"] <- p
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
    (store: OutcomeStore.Store option)
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

            match store.Supersede(recordOf key tree (Some facts.RunId) producer, Some outputs) with
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
                      IndexBytes = indexBytes })
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
    | Refusal.LacksFullPass(project, why) -> $"project %s{project} lacks a full-pass K_P record from this run: %s{why}"
    | Refusal.NotFiled why -> why

/// The completion's one log line. Every line starts `test-baseline: written` or
/// `test-baseline: not written`, carries `run=`, and a refusal names each failed condition
/// as a `reason=` token, so a daemon log can be counted by grep.
let describe (runId: string) (elapsedMs: int64) (outcome: Result<Written, Refusal list>) : string =
    match outcome with
    | Ok w ->
        $"test-baseline: written run=%s{runId} tree=%s{w.Tree} projects=%d{w.Projects} files=%d{w.Files} "
        + $"identities=%d{w.Identified} bytes=%d{w.Bytes} index=%d{w.IndexEntries}/%d{IndexCapacity} "
        + $"index-bytes=%d{w.IndexBytes} ms=%d{elapsedMs} (shadow: nothing reads it)"
    | Error refusals ->
        let tokens = refusals |> List.map token |> List.distinct |> String.concat ","
        let details = refusals |> List.map detail |> String.concat "; "
        $"test-baseline: not written run=%s{runId} reason=%s{tokens} ms=%d{elapsedMs} — %s{details}"
