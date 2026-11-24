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
/// and T's manifest: per file, its content hash and, for a file the index analysed, the
/// analysis identity it held (`file_keys`) with the project and key of the exact analysis
/// record that holds that analysis (`AnalysisRecords`): what another checkout rebuilds its
/// index at T from (`BaselineRebuild`). A tree some analysable file of which resolves to no
/// single record files no baseline (AUTOMATION-1793 step 1, amendment 2).
///
/// One INDEX record per repository store names the newest `IndexCapacity` baselines. It is
/// rewritten in place (`OutcomeStore.Store.Supersede`), never appended: appended, each
/// pointer would supersede the last and GC would trim exactly the older candidates. GC keeps
/// every baseline the live index names (`OutcomeStore.keepsOf`), and the analysis records the
/// newest `RetainedTrees` of them pinned. Measured on the box 2026-11-24: with the store over
/// its budget, GC evicted 44-52 of each tree's analyses, and an unchanged file's analysis is
/// never re-filed (`AnalysisRecords.fileIfAbsent`), so without the pins no baseline resolved.
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

/// How many of the index's newest baselines keep their analysis records alive (AUTOMATION-1793
/// step 1, amendment 2). The index record keeps the exact analysis keys these baselines pinned
/// (`appendIndex`); an older baseline keeps only its own record, may lose its analyses to GC,
/// and then simply does not resolve (`BaselineRebuild`). Never each baseline's own keeps: that
/// would pin the analyses of every baseline the store still holds.
[<Literal>]
let RetainedTrees = 3

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
    /// Analysable files with no single analysis record in the store under any project that
    /// compiles them: none filed (or evicted), or a linked file whose projects' records hold
    /// different analyses. A baseline must name the exact record every analysable file's
    /// analysis is read from, or a rebuild at its tree could not be whole.
    | AnalysesUnresolved of paths: string list
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
        /// Manifest rows pinned to an analysis record (source files the index holds).
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

/// The analysis an analysable manifest row names (AUTOMATION-1793 step 1, amendment 2).
[<RequireQualifiedAccess>]
type RowAnalysis =
    /// The exact analysis record filing found the file's analysis under: the analysis
    /// identity the index held for it, the project whose record it is, and that record's key
    /// (`AnalysisRecords.keyOf identity path project`). Its record is kept alive while the
    /// baseline is among the index's newest `RetainedTrees`.
    | Pinned of identity: string * project: string * key: string
    /// A row from a record filed before keys were pinned: only the identity. Its analyses
    /// were never kept alive for it, so nothing may be rebuilt from it.
    | Unpinned of identity: string

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
        /// The analysis each analysable file of T names (a project file or other unanalysed
        /// entry has none): what another checkout rebuilds its index at T from
        /// (`BaselineRebuild`, AUTOMATION-1793).
        Analyses: Map<string, RowAnalysis>
        /// Each runnable project at T and the full-run K_P key its pass rests on: what a box
        /// baseline re-checks per project at restart (AUTOMATION-1793 step 2b).
        FullKeys: Map<string, string>
        /// When the record was filed.
        FiledAt: DateTimeOffset
    }

/// One cell of a manifest row, as JSON holds it.
type private Cell =
    | Text of string
    | Null
    | Other

let private cellOf (e: JsonElement) : Cell =
    match e.ValueKind with
    | JsonValueKind.String -> Text(e.GetString())
    | JsonValueKind.Null -> Null
    | _ -> Other

/// A manifest row: `[path, hash]` and the analysis cells. An unanalysed row carries none, or
/// nulls (3 cells before keys were pinned, 5 since); an analysed one its identity (3 cells,
/// `Unpinned`) or its identity, project and key (5 cells, `Pinned`). Anything else is
/// malformed.
let private rowOf (r: JsonElement) : (string * string * RowAnalysis option) option =
    if r.ValueKind = JsonValueKind.Array then
        match [ for c in r.EnumerateArray() -> cellOf c ] with
        | [ Text path; Text hash ]
        | [ Text path; Text hash; Null ]
        | [ Text path; Text hash; Null; Null; Null ] -> Some(path, hash, None)
        | [ Text path; Text hash; Text identity ] -> Some(path, hash, Some(RowAnalysis.Unpinned identity))
        | [ Text path; Text hash; Text identity; Text project; Text key ] ->
            Some(path, hash, Some(RowAnalysis.Pinned(identity, project, key)))
        | _ -> None
    else
        None

/// The record's `projects` object: each project and the K_P key text its pass rests on.
/// A value that is not a string is no entry.
let private fullKeysOf (o: JsonElement) : Map<string, string> =
    match Json.tryProp o "projects" with
    | Some projects when projects.ValueKind = JsonValueKind.Object ->
        projects.EnumerateObject()
        |> Seq.filter (fun p -> p.Value.ValueKind = JsonValueKind.String)
        |> Seq.map (fun p -> p.Name, p.Value.GetString())
        |> Map.ofSeq
    | _ -> Map.empty

/// A filed record's outputs: always JSON, since the store files nothing else.
let private parseRecord
    (tree: string)
    (key: string)
    (filedAt: DateTimeOffset)
    (outputs: string)
    : Result<CheckoutRecord, string> =
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
            let rows = [ for r in manifest.EnumerateArray() -> rowOf r ]

            if rows |> List.exists Option.isNone then
                Error "the record's manifest has a malformed row"
            else
                let rows = List.choose id rows

                Ok
                    { Tree = tree
                      Key = key
                      RunId = runId
                      Entries = rows |> List.map (fun (path, hash, _) -> path, hash)
                      Analyses =
                        rows
                        |> List.choose (fun (path, _, analysis) -> analysis |> Option.map (fun a -> path, a))
                        |> Map.ofList
                      FullKeys = fullKeysOf o
                      FiledAt = filedAt }
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
    | Some(record, Some outputs) -> parseRecord tree key.Value.Text record.RecordedAt outputs

/// The analysis keys a baseline pinned; none for a baseline that cannot be read or was filed
/// before keys were pinned.
let private pinnedKeysOf (store: OutcomeRecords.IOutcomeStore) (entry: IndexEntry) : string list =
    match checkoutRecordFor store entry.Tree with
    | Ok record ->
        record.Analyses
        |> Map.toList
        |> List.choose (fun (_, analysis) ->
            match analysis with
            | RowAnalysis.Pinned(_, _, key) -> Some key
            | RowAnalysis.Unpinned _ -> None)
    | Error _ -> []

/// The index's outputs: its entries, and as `keeps` every baseline it names plus `pinned`, the
/// analysis keys of the newest `RetainedTrees` of them.
let private indexOutputs (entries: IndexEntry list) (pinned: string list) : string =
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

    for key in pinned do
        keeps.Add(JsonValue.Create key)

    o[OutcomeStore.KeepsField] <- keeps
    o["entries"] <- rows
    o.ToJsonString()

/// Put `entry` at the front of the index, drop any older entry for the same tree, keep the
/// newest `IndexCapacity`, and rewrite the index as its key's one record, keeping alive every
/// baseline it names and the analysis records the newest `RetainedTrees` of them pinned.
/// Returns the entry count and the record's bytes, or `None` when it could not be filed.
let appendIndex (store: OutcomeRecords.IOutcomeStore) (producer: Producer) (entry: IndexEntry) : (int * int64) option =
    let entries =
        entry :: (readIndex store |> List.filter (fun e -> e.Tree <> entry.Tree))
        |> List.truncate IndexCapacity

    let pinned =
        entries
        |> List.truncate RetainedTrees
        |> List.collect (pinnedKeysOf store)
        |> List.distinct

    OutcomeStore.Through.supersede store (recordOf indexKey "index" None producer) (Some(indexOutputs entries pinned))
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

/// A manifest row as filing writes it.
type private FiledRow =
    /// A file the index holds no analysis of (a project file, a non-source input).
    | Unanalysed of path: string * hash: string
    /// An analysable file, with the exact analysis record its analysis is read from.
    | Pinned of path: string * hash: string * identity: string * project: string * key: string

/// Each manifest entry as a row, every analysable one (a path with an identity in `keys`)
/// pinned to the exact analysis record that holds it in `store`, read under `root`: its key
/// under each project that compiles the file (`projectsOf`, more than one for a linked
/// file). One hit, or several holding equal analyses, pins the first; none, or hits holding
/// different analyses, leave the file unresolved, and any unresolved file refuses the
/// baseline, naming every one.
let private pinRows
    (store: OutcomeRecords.IOutcomeStore)
    (root: string)
    (projectsOf: string -> string list)
    (entries: (string * string) list)
    (keys: Map<string, string>)
    : Result<FiledRow list, Refusal list> =
    let rows =
        entries
        |> List.map (fun (path, hash) ->
            match Map.tryFind path keys with
            | None -> Ok(FiledRow.Unanalysed(path, hash))
            | Some identity ->
                let hits =
                    projectsOf path
                    |> List.choose (fun project ->
                        let key = AnalysisRecords.keyOf identity path project

                        AnalysisRecords.lookup store root key
                        |> Option.map (fun analysis -> project, key, analysis))

                match hits with
                | (project, key, analysis) :: others when others |> List.forall (fun (_, _, other) -> other = analysis) ->
                    Ok(FiledRow.Pinned(path, hash, identity, project, key.Value.Text))
                | _ -> Error path)

    match
        rows
        |> List.choose (fun row ->
            match row with
            | Error path -> Some path
            | Ok _ -> None)
    with
    | [] -> Ok(rows |> List.choose Result.toOption)
    | unresolved -> Error [ Refusal.AnalysesUnresolved unresolved ]

let private baselineOutputs
    (facts: Facts)
    (tree: string)
    (rows: FiledRow list)
    (projects: Map<string, FullPass>)
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

    let text (value: string) = JsonValue.Create value :> JsonNode

    for row in rows do
        match row with
        | FiledRow.Unanalysed(path, hash) -> manifest.Add(JsonArray(text path, text hash, null, null, null))
        | FiledRow.Pinned(path, hash, identity, project, key) ->
            manifest.Add(JsonArray(text path, text hash, text identity, text project, text key))

    o["manifest"] <- manifest
    o.ToJsonString()

/// File T's baseline and name it in the index, or say which conditions refused it. The
/// cheap conditions are judged first and all of them reported; the projects' K_P keys
/// (`fullKeys`, which hashes the tree) and the index's file keys are read only once those
/// hold. Every analysable file is pinned to its analysis record in `store`, read under
/// `root` through each project `projectsOf` says compiles it (`pinRows`), or nothing is filed.
/// Never throws for a store that cannot be written: that is `NotFiled`.
let file
    (store: OutcomeRecords.IOutcomeStore option)
    (root: string)
    (projectsOf: string -> string list)
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
            pinRows store root projectsOf entries (fileKeys ())
            |> Result.map (fun rows -> projects, rows))
        |> Result.bind (fun (projects, rows) ->
            let key = keyOf tree
            let outputs = baselineOutputs facts tree rows projects

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
                      Identified =
                        rows
                        |> List.filter (fun row ->
                            match row with
                            | FiledRow.Pinned _ -> true
                            | FiledRow.Unanalysed _ -> false)
                        |> List.length
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
    | Refusal.AnalysesUnresolved _ -> "analyses-unresolved"

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
    | Refusal.AnalysesUnresolved paths ->
        let named = String.concat ", " paths

        $"%d{paths.Length} analysable file(s) resolve to no single analysis record under any project that compiles them: %s{named}"

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
