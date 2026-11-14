/// The lookup verdict of a tree, read from the outcome stores alone (ADR-059 §7; AUTOMATION-1695,
/// Phase 1 step C). ONE definition, shared by the daemon's shadow of a `fshw verdict` read
/// (`ShadowVerdict.observeReadWith`) and by a pure lookup with no daemon and no FCS
/// (`ofRepo`, behind `fshw-lookup`), so experiment E5 times the same answer the shadow logs.
///
/// A lookup reads; it files nothing, moves nothing and evicts nothing.
module FsHotWatch.Lookup

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// The shadow's directory under `.fshw/`.
let directory (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "shadow")

/// The shadow outcome store's root: where publications file their plugin, diagnostics and test
/// records.
let storeRoot (repoRoot: string) =
    Path.Combine(directory repoRoot, "outcomes")

/// The shadow store of `repoRoot`, with the default TTL and byte budget.
let storeAt (repoRoot: string) (log: string -> unit) =
    OutcomeStore.Store(
        { Root = storeRoot repoRoot
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = log }
    )

/// The field of a `test-outcome-key` record's outputs that carries the K_P key it names.
[<Literal>]
let OutcomeKeyField = "outcomeKey"

/// The key under which a publication names `project`'s K_P over `subject`'s tree and binary
/// (AUTOMATION-1443): a read has no daemon to name it, so it reads it back from here.
let outcomeKeyKey (subject: Requirements.Subject) (project: string) =
    ActionKey.create "test-outcome-key" [ "tree", subject.Tree; "producer", subject.Producer; "project", project ]

/// The K_P keys publications over `subject`'s tree and binary named, by project; a project none
/// named, or whose record does not read back as a key, is absent.
let recordedOutcomeKeys
    (store: OutcomeRecords.IOutcomeStore)
    (testProjects: string list)
    (subject: Requirements.Subject)
    : Map<string, ActionKey.KeyValue> =
    let keyIn (outputs: string) =
        try
            match JsonNode.Parse outputs with
            | :? JsonObject as obj ->
                match obj[OutcomeKeyField] with
                | :? JsonValue as v ->
                    match v.TryGetValue<string>() with
                    | true, text -> ActionKey.KeyValue.tryParse text
                    | _ -> None
                | _ -> None
            | _ -> None
        with :? JsonException ->
            None

    testProjects
    |> List.choose (fun project ->
        OutcomeStore.Through.lookupWithOutputs store (outcomeKeyKey subject project).Value
        |> Option.bind snd
        |> Option.bind keyIn
        |> Option.map (fun key -> project, key))
    |> Map.ofList

/// `subject` with the K_P keys earlier publications named, when it names none itself: a read.
let withRecordedOutcomeKeys
    (store: OutcomeRecords.IOutcomeStore)
    (testProjects: string list)
    (subject: Requirements.Subject)
    : Requirements.Subject =
    if Map.isEmpty subject.OutcomeKeys then
        { subject with
            OutcomeKeys = recordedOutcomeKeys store testProjects subject }
    else
        subject

/// The answer for a key under `command`: the shadow store's, else the K_P store's, where the
/// test runner files. Under `check`, a K_P full fail whose every named failure a later run
/// passed at the same K_P reads as a pass, as today's outstanding-failure ledger clears it
/// (`TestFailureClearance`, AUTOMATION-1444); `confirm` grades the full suite itself.
let lookupIn
    (command: Requirements.Command)
    (store: OutcomeRecords.IOutcomeStore)
    (kpStore: OutcomeRecords.IOutcomeStore option)
    (key: ActionKey.KeyValue)
    : LookupVerdict.Outcome option =
    let read (s: OutcomeRecords.IOutcomeStore) =
        match command with
        | Requirements.Command.Check -> TestFailureClearance.lookupForCheck s key
        | Requirements.Command.Confirm -> OutcomeStore.Through.lookup s key |> Option.map _.Outcome

    match read store with
    | Some outcome -> Some outcome
    | None -> kpStore |> Option.bind read

/// The lookup verdict of R(`command`, `subject`'s tree) for `configured`, from `store` and, for
/// the keys only the test runner files, `kpStore`.
let answer
    (command: Requirements.Command)
    (store: OutcomeRecords.IOutcomeStore)
    (kpStore: OutcomeRecords.IOutcomeStore option)
    (configured: Requirements.Configured)
    (subject: Requirements.Subject)
    : LookupVerdict.Answer =
    LookupVerdict.decide (Requirements.requirements command subject configured) (lookupIn command store kpStore)

/// What a pure lookup of a checkout concluded.
[<RequireQualifiedAccess>]
type Read =
    /// The stores' answer for the tree as it is now, graded as the named binary grades it.
    | Answered of tree: string * producer: string * answer: LookupVerdict.Answer
    /// No daemon has recorded which fshw binary grades this checkout, so no key can be named:
    /// every record is keyed by its producer.
    | NoProducer of tree: string
    /// `.fshw.json` is not JSON, so what a green requires is unknown.
    | Unconfigured of reason: string

/// What `repoRoot`'s `.fshw.json` requires; the defaults when there is none.
let private requiredOf (repoRoot: string) : Result<RequiredConfig.Required, string> =
    let path = FsHwPaths.configFile repoRoot

    if File.Exists path then
        try
            Ok(RequiredConfig.ofJson (File.ReadAllText path))
        with
        | :? JsonException as ex -> Error $"{FsHwPaths.ConfigFileName}: {ex.Message}"
        | :? IOException as ex -> Error $"{FsHwPaths.ConfigFileName}: {ex.Message}"
    else
        Ok RequiredConfig.defaults

/// The lookup verdict of `repoRoot` as it is now, for `command`: the configuration's
/// requirements, the tree's hash, the producer the daemon recorded, then the stores. The
/// shadow store is the one a `fshw verdict` read consults; the box-wide K_P store is opened only
/// when a publication named a K_P key to look up there, as the read does (`ShadowVerdict.observeRead`).
///
/// Unlike the daemon, it does not VALIDATE the configuration's plugin settings: a configuration
/// the daemon refuses files nothing, so its keys are missing here.
let ofRepo (repoRoot: string) (command: Requirements.Command) (log: string -> unit) : Read =
    match requiredOf repoRoot with
    | Error reason -> Read.Unconfigured reason
    | Ok required ->
        let tree = (TreeHash.compute repoRoot required.Exclude).Hash

        match DaemonIdentity.readRecorded repoRoot with
        | None -> Read.NoProducer tree
        | Some identity ->
            let producer = DaemonIdentity.BinaryIdentity.render identity

            let configured: Requirements.Configured =
                { RequiredPlugins = required.Plugins
                  TestProjects = List.sort required.TestProjects }

            let store = storeAt repoRoot log

            let subject =
                withRecordedOutcomeKeys
                    store
                    configured.TestProjects
                    { Tree = tree
                      Producer = producer
                      OutcomeKeys = Map.empty }

            let kpStore =
                if Map.isEmpty subject.OutcomeKeys then
                    None
                else
                    Some(OutcomeStore.openFor repoRoot log :> OutcomeRecords.IOutcomeStore)

            Read.Answered(tree, producer, answer command store kpStore configured subject)
