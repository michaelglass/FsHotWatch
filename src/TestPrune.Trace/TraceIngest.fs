/// Ingestion: turns one traced run of one test project (its process dumps, its CTRF
/// outcomes and its weave manifest) into stored per-test traces.
///
/// Dumps from every process of the run are merged by scope key; a traced child process
/// records into the scope that started it. Probe ids are joined to symbols at their
/// current version hash, file inputs are hashed as they are now, and each test links the
/// fixture, collection and pool scopes it inherited. A trace is complete only when its
/// test passed, every executed source file still matches the PDB it was built from, every
/// executed id mapped, and every child process it started recorded too. Otherwise the
/// reasons are stored with it: nothing is silently partial.
module TestPrune.Trace.TraceIngest

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open TestPrune.Trace.Model
open TestPrune.Trace.Joiner
open TestPrune.Trace.TraceStore

/// One traced run of one test project, ready to ingest.
type IngestRequest =
    {
        RunId: string
        TestProject: string
        /// Root the joiner resolves PDB documents against.
        RepoRoot: string
        /// Root the recorder filtered file inputs against; input keys are relative to it.
        /// The same directory in production; a test fixture may index a sub-tree.
        InputRoot: string
        Kind: RunKind
        /// The input tree hash when the run was launched.
        LaunchTreeHash: string
        /// The input tree hash now; a difference means the index no longer describes the binary.
        CurrentTreeHash: string
        /// The directory the run's processes dumped into.
        DumpDir: string
        Shadow: ShadowBin.Shadow
        /// The project's CTRF outcomes.
        Outcomes: TestOutcome list
        Symbols: TestPrune.Ports.SymbolStore
        /// Repo-relative files folded into the environment fingerprint.
        FingerprintFiles: string list
        /// Environment variables folded into the environment fingerprint.
        FingerprintEnv: string list
        RecordedAt: DateTimeOffset
    }

/// What an ingestion did, for the host's log line and status.
type IngestSummary =
    {
        /// How the run was stored.
        Status: RunStatus
        /// Why the run stored no traces; empty when it did.
        Reason: string
        /// None when no trace was stored.
        EnvFingerprint: string option
        /// Distinct CTRF names that ran (every outcome except Skipped).
        Executed: int
        /// Executed names matched to a recorded test scope.
        Traced: int
        /// Stored traces with no incomplete reason.
        Complete: int
        /// Executed names with no recorded test scope, in CTRF order.
        UntracedExecuted: string list
        /// Tests per incomplete-reason kind (the reason code before any ':').
        ReasonCounts: Map<string, int>
        /// Summed over every usable dump.
        Counters: HitCounters
        /// (dump file, why) for every dump that was not used.
        RejectedDumps: (string * string) list
        CpuMs: int64
        /// Distinct executed probe ids that mapped to neither a symbol nor a file.
        UnmappedIds: int
    }

/// The `stats_json` of a recorded run. Field names are the serialized names; the type is
/// public because System.Text.Json serializes a non-public F# record as `{}`.
type RunStats =
    { executed: int
      traced: int
      complete: int
      unmappedIds: int
      cpuMs: int64
      counters: HitCounters
      rejected: string[] }

/// The scope static constructors record into. Stored with the run, never linked to a test.
[<Literal>]
let StaticInitScope = "S:static-init"

/// The scope unattributed hits record into. Stored with the run, never linked to a test.
[<Literal>]
let AmbientScope = "A:ambient"

/// The stored key of a test: "<project>|<class>|<method>".
let testKey (project: string) (cls: string) (meth: string) = $"%s{project}|%s{cls}|%s{meth}"

/// The '/'-separated path of `absPath` relative to `repoRoot`, or None when it is not under
/// it. A shadow-bin path (`bin/Traced/`) is keyed as the build output it mirrors (`bin/Debug/`).
let repoRelative (repoRoot: string) (absPath: string) : string option =
    let prefix =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath repoRoot)
        + string Path.DirectorySeparatorChar

    let full = Path.GetFullPath absPath

    if full.StartsWith(prefix, StringComparison.Ordinal) then
        let rel = full.Substring(prefix.Length).Replace('\\', '/')
        Some(("/" + rel).Replace("/bin/Traced/", "/bin/Debug/").Substring 1)
    else
        None

/// The refusal stored when a project's weave set is empty. Nothing was probed, so every
/// test would otherwise look traced with an empty, "verified" trace.
let nothingWovenReason (repoRoot: string) : string =
    "no-woven-assembly: the weave set is empty, so no probe could fire. No assembly in the build output has a "
    + $"portable PDB naming a source document under %s{repoRoot}. PDB paths mapped to /_/ by "
    + "ContinuousIntegrationBuild, DeterministicSourcePaths or a PathMap cause this: build the traced run without them."

let private hex (b: byte[]) =
    Convert.ToHexString(b).ToLowerInvariant()

let private sha256Text (s: string) =
    hex (SHA256.HashData(Encoding.UTF8.GetBytes s))

/// The version hash of every indexed symbol: its ContentHash when it has one occurrence,
/// else the hash of its sorted (file|hash) pairs (a `.fsi` and its `.fs`). Phase 2 must
/// compute "current version" with this same function.
let versionHashes (store: TestPrune.Ports.SymbolStore) : Map<string, string> =
    store.GetAllSymbols()
    |> List.groupBy (fun s -> s.FullName)
    |> List.map (fun (name, occ) ->
        match occ with
        | [ one ] -> name, one.ContentHash
        | many ->
            name,
            many
            |> List.map (fun o -> o.SourceFile + "|" + o.ContentHash)
            |> List.sort
            |> String.concat "\n"
            |> sha256Text)
    |> Map.ofList

let private zero =
    { Test = 0L
      Class = 0L
      Collection = 0L
      Assembly = 0L
      Override = 0L
      StaticInit = 0L
      Ambient = 0L
      Overflow = 0L }

let private add (a: HitCounters) (b: HitCounters) =
    { Test = a.Test + b.Test
      Class = a.Class + b.Class
      Collection = a.Collection + b.Collection
      Assembly = a.Assembly + b.Assembly
      Override = a.Override + b.Override
      StaticInit = a.StaticInit + b.StaticInit
      Ambient = a.Ambient + b.Ambient
      Overflow = a.Overflow + b.Overflow }

let private norm (s: string) = s.Replace('+', '.')

/// A CTRF name up to its argument list, nested classes dotted.
let private stemOf (name: string) =
    let n = norm name

    match n.IndexOf '(' with
    | -1 -> n
    | i -> n.Substring(0, i)

let private severity =
    function
    | Failed -> 3
    | OtherOutcome -> 2
    | Skipped -> 1
    | Passed -> 0

/// What one probe id contributes to the scope that hit it.
type private Effect =
    | Entry of name: string * hash: string
    | FileLevel of repoRelativePath: string * hash: string
    | Reason of IncompleteReason

/// One scope key merged across every process of the run.
type private Merged =
    { Key: string
      Test: TestIdentity option
      Ids: int[]
      Inputs: RecordedInput list
      Parents: string list
      Links: string list
      Children: ChildNote list }

/// A dump whose ids belong to another weave would join against the wrong manifest.
let private validate (idCount: int) (d: ProcessDump) : Result<ProcessDump, string> =
    if d.IdCount <> idCount then
        Error $"id count %d{d.IdCount}, manifest %d{idCount}"
    else
        match
            d.Scopes
            |> List.tryPick (fun s -> s.Ids |> Array.tryFind (fun id -> uint32 id >= uint32 idCount))
        with
        | Some id -> Error $"probe id %d{id} outside the manifest's %d{idCount}"
        | None -> Ok d

/// A child process records into the scope that started it; so does its static init.
let private keyOf (d: ProcessDump) (s: RecordedScope) =
    match d.ParentScope with
    | Some parent when s.Key = StaticInitScope -> parent
    | _ -> s.Key

let private merge (dumps: ProcessDump list) : Map<string, Merged> =
    dumps
    |> List.collect (fun d -> d.Scopes |> List.map (fun s -> keyOf d s, s))
    |> List.groupBy fst
    |> List.map (fun (key, group) ->
        let scopes = List.map snd group

        key,
        { Key = key
          Test = scopes |> List.tryPick (fun s -> s.Test)
          Ids = scopes |> Seq.collect (fun s -> s.Ids) |> Seq.distinct |> Array.ofSeq
          Inputs = scopes |> List.collect (fun s -> s.Inputs) |> List.distinct
          Parents = scopes |> List.collect (fun s -> s.Parents) |> List.distinct
          Links = scopes |> List.collect (fun s -> s.Links) |> List.distinct
          Children = scopes |> List.collect (fun s -> s.Children) })
    |> Map.ofList

/// The effects of every probe id: its joined symbol or file-level entry, plus the reason
/// its source document makes it incomplete.
let private effectsOf (req: IngestRequest) : Effect list[] * bool[] =
    let manifest = req.Shadow.Manifest
    let targets = joinManifest (ofStore req.Symbols req.RepoRoot) manifest
    let versions = versionHashes req.Symbols

    let docState =
        manifest.Documents
        |> Map.map (fun doc recorded ->
            repoRelative req.RepoRoot doc
            |> Option.bind (fun rel ->
                match Fingerprint.hashFile req.RepoRoot rel with
                | "missing" -> Some(NotIndexed rel)
                | now when recorded <> "" && now <> recorded -> Some(SourceDrift rel)
                | _ -> None))

    let fileHashes =
        targets
        |> Seq.choose (function
            | ToFile rel -> Some rel
            | _ -> None)
        |> Seq.distinct
        |> Seq.map (fun rel -> rel, Fingerprint.hashFile req.RepoRoot rel)
        |> Map.ofSeq

    let effects =
        Seq.init manifest.IdCount (fun id -> [ Reason(UnmappedCode $"#%d{id} (no-row)") ])
        |> Array.ofSeq

    let unmapped = Array.create manifest.IdCount true

    for row in manifest.Rows do
        let detail why =
            UnmappedCode $"%s{row.TypeName}::%s{row.Member} (%s{why})"

        let joined, isUnmapped =
            match targets.[row.Id] with
            | ToSymbol n ->
                match versions.TryFind n with
                | Some h -> [ Entry(n, h) ], false
                | None -> [ Reason(detail "no-version") ], true
            | ToFile rel -> [ FileLevel(rel, fileHashes.[rel]) ], false
            | Dropped -> [], false
            | Unmapped why -> [ Reason(detail why) ], true

        let drift =
            row.Document
            |> Option.bind (fun d -> docState.TryFind d |> Option.flatten)
            |> Option.map Reason
            |> Option.toList

        effects.[row.Id] <- drift @ joined
        unmapped.[row.Id] <- isUnmapped

    effects, unmapped

/// The (kind, key, hash) of a recorded file input as the file is now, or None when it is
/// outside the input root.
let private inputOf (root: string) (i: RecordedInput) =
    repoRelative root i.Path
    |> Option.map (fun rel ->
        let p = Path.Combine(root, rel)

        match i.Kind with
        | FileRead -> "read", rel, Fingerprint.hashFile root rel
        | ExistenceProbe -> "exists", rel, (if Path.Exists p then "present" else "absent")
        | DirectoryListing ->
            "list",
            rel,
            (if Directory.Exists p then
                 Directory.GetFileSystemEntries p
                 |> Seq.map Path.GetFileName
                 |> Seq.sort
                 |> String.concat "\n"
                 |> sha256Text
             else
                 "absent"))

let private summaryOf status reason (executed: string list) counters rejected cpu =
    { Status = status
      Reason = reason
      EnvFingerprint = None
      Executed = executed.Length
      Traced = 0
      Complete = 0
      UntracedExecuted = executed
      ReasonCounts = Map.empty
      Counters = counters
      RejectedDumps = rejected
      CpuMs = cpu
      UnmappedIds = 0 }

/// Ingest one traced run of one test project into `store`. A run with an empty weave set
/// is stored `refused`; a run with no usable main-process dump is stored `failed` with no
/// tests (those keep any older trace). Otherwise every recorded test gets a trace, complete
/// or with its reasons, and a full run drops the traces of tests it did not trace.
let ingest (store: Store) (req: IngestRequest) : IngestSummary =
    let manifest = req.Shadow.Manifest

    let results =
        DumpReader.readEach req.DumpDir
        |> List.map (fun (f, r) -> f, r |> Result.bind (validate manifest.IdCount))

    let dumps = results |> List.choose (snd >> Result.toOption)

    let rejected =
        results
        |> List.choose (fun (f, r) ->
            match r with
            | Error why -> Some(f, why)
            | Ok _ -> None)

    let counters = dumps |> List.fold (fun acc d -> add acc d.Counters) zero
    let cpu = dumps |> List.sumBy (fun d -> d.CpuMs)

    let executed =
        req.Outcomes
        |> List.filter (fun o -> o.Outcome <> Skipped)
        |> List.map (fun o -> o.Name)
        |> List.distinct

    let run fingerprint status reason statsJson : TraceRun =
        { RunId = req.RunId
          TestProject = req.TestProject
          TreeHash = req.LaunchTreeHash
          EnvFingerprint = fingerprint
          RecordedAt = req.RecordedAt
          Kind = req.Kind
          Status = status
          Reason = reason
          StatsJson = statsJson }

    let rejectedText =
        rejected |> List.map (fun (f, why) -> $"%s{Path.GetFileName f}: %s{why}")

    let mainDump = dumps |> List.tryFind (fun d -> d.ParentScope.IsNone)

    if manifest.Rows.Length = 0 then
        let why = nothingWovenReason req.RepoRoot
        store.RecordRunWithoutTraces(run "" Refused why "{}")
        summaryOf Refused why executed counters rejected cpu
    elif mainDump.IsNone then
        let why =
            if rejectedText.IsEmpty then
                "recorder-no-output"
            else
                "recorder-no-output; rejected: " + String.concat "; " rejectedText

        store.RecordRunWithoutTraces(run "" FailedToRecord why "{}")
        summaryOf FailedToRecord why executed counters rejected cpu
    else
        let fp =
            Fingerprint.compute (
                Fingerprint.gather
                    req.RepoRoot
                    req.FingerprintFiles
                    req.FingerprintEnv
                    mainDump.Value
                    req.Shadow.OriginalDepsJsonSha256
            )

        let effects, unmappedId = effectsOf req
        let merged = merge dumps

        let tracedChildren =
            dumps
            |> List.choose (fun d -> d.ParentScope |> Option.map (fun p -> p, d.Pid))
            |> Set.ofList

        let contentOf (m: Merged) : ScopeContent * IncompleteReason list =
            let hits = m.Ids |> List.ofArray |> List.collect (fun id -> effects.[id])

            let children =
                m.Children
                |> List.filter (fun c -> not (tracedChildren.Contains(m.Key, c.Pid)))
                |> List.map (fun c -> ChildProcessUntraced c.FileName)

            { Key = m.Key
              Symbols =
                hits
                |> List.choose (function
                    | Entry(n, h) -> Some(n, h)
                    | _ -> None)
              Inputs =
                (hits
                 |> List.choose (function
                     | FileLevel(rel, h) -> Some("file-level", rel, h)
                     | _ -> None))
                @ (m.Inputs |> List.choose (inputOf req.InputRoot)) },
            (hits
             |> List.choose (function
                 | Reason r -> Some r
                 | _ -> None))
            @ children
            |> List.distinct

        let contents = merged |> Map.map (fun _ m -> contentOf m)
        let runOnly = set [ StaticInitScope; AmbientScope ]

        // Fixture, collection and pool scopes a test inherits: the recorded scopes reachable
        // through parents and links. Run-only scopes are never inherited.
        let rec closure (seen: Set<string>) (keys: string list) =
            match keys with
            | [] -> seen
            | k :: rest when seen.Contains k || runOnly.Contains k || not (merged.ContainsKey k) -> closure seen rest
            | k :: rest -> closure (seen.Add k) (merged.[k].Parents @ merged.[k].Links @ rest)

        let exact = req.Outcomes |> List.groupBy (fun o -> o.Name) |> Map.ofList
        let byStem = req.Outcomes |> List.groupBy (fun o -> stemOf o.Name) |> Map.ofList

        // CTRF rows of a test scope: its display name, else its class and method.
        let rowsOf (t: TestIdentity) =
            exact.TryFind t.Display
            |> Option.orElse (byStem.TryFind(norm (t.Class + "." + t.Method)))
            |> Option.defaultValue []

        let testScopes =
            merged
            |> Map.toList
            |> List.choose (fun (_, m) -> m.Test |> Option.map (fun t -> t, m))

        let processWide =
            [ if counters.Overflow > 0L then
                  RecorderOverflow
              yield! rejectedText |> List.map DumpRejected
              if req.LaunchTreeHash <> req.CurrentTreeHash then
                  TreeMoved ]

        let tests =
            testScopes
            |> List.groupBy (fun (t, _) -> testKey req.TestProject t.Class t.Method)
            |> List.map (fun (key, scopes) ->
                let rows = scopes |> List.collect (fst >> rowsOf) |> List.distinct

                let status =
                    rows
                    |> List.map (fun o -> o.Outcome)
                    |> List.sortByDescending severity
                    |> List.tryHead

                let linked =
                    closure Set.empty (scopes |> List.map (fun (_, m) -> m.Key)) |> Set.toList

                let reasons =
                    [ match status with
                      | None -> NoOutcome
                      | Some Passed -> ()
                      | Some s -> NotPassed s
                      yield! linked |> List.collect (fun k -> snd contents.[k])
                      yield! processWide ]
                    |> List.distinct

                ({ TestKey = key
                   Status = status
                   Reasons = reasons
                   ScopeKeys = linked }
                : TestTrace),
                rows |> List.map (fun o -> o.Name))

        let tracedNames = tests |> List.collect snd |> Set.ofList
        let traces = tests |> List.map fst
        let linkedScopes = traces |> List.collect (fun t -> t.ScopeKeys) |> Set.ofList

        let scopes =
            contents
            |> Map.toList
            |> List.filter (fun (k, _) -> linkedScopes.Contains k || runOnly.Contains k)
            |> List.map (snd >> fst)

        let unmappedIds =
            merged
            |> Map.toSeq
            |> Seq.collect (fun (_, m) -> m.Ids)
            |> Seq.distinct
            |> Seq.filter (fun id -> unmappedId.[id])
            |> Seq.length

        let complete = traces |> List.filter (fun t -> t.Reasons.IsEmpty) |> List.length
        let traced = executed |> List.filter tracedNames.Contains

        let stats: RunStats =
            { executed = executed.Length
              traced = traced.Length
              complete = complete
              unmappedIds = unmappedIds
              cpuMs = cpu
              counters = counters
              rejected = Array.ofList rejectedText }

        let status =
            if req.LaunchTreeHash <> req.CurrentTreeHash then
                TreeMovedDuringRun
            else
                Recorded

        store.RecordRun(run fp status "" (JsonSerializer.Serialize stats), scopes, traces)

        store.CollectGarbage(
            req.TestProject,
            fp,
            (match req.Kind with
             | FullRun -> Some(traces |> List.map (fun t -> t.TestKey) |> Set.ofList)
             | PartialRun -> None)
        )

        { Status = status
          Reason = ""
          EnvFingerprint = Some fp
          Executed = executed.Length
          Traced = traced.Length
          Complete = complete
          UntracedExecuted = executed |> List.filter (tracedNames.Contains >> not)
          ReasonCounts =
            traces
            |> List.collect (fun t -> t.Reasons |> List.map (fun r -> (reasonCode r).Split(':').[0]) |> List.distinct)
            |> List.countBy id
            |> Map.ofList
          Counters = counters
          RejectedDumps = rejected
          CpuMs = cpu
          UnmappedIds = unmappedIds }
