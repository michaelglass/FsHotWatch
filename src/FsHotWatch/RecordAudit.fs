/// The clean-recompute audit (ADR-059 §35 Change 1a, AUTOMATION-1633): `nix build --check`
/// for the outcome store.
///
/// A record says "the action with key K came out like this". A key is built from DECLARED
/// inputs, and every false green §14–§34 patched was an input nobody declared: a record
/// filed under K by one tree, served to another tree whose undeclared input differed. The
/// audit finds that class mechanically. A CLEAN store is filed by recomputing a tree from
/// scratch (an empty cache home, so nothing is replayed). For every key the clean store and
/// the AUDITED store (the box-wide one daemons serve from) both hold, the two records must
/// say the same thing. Equal keys with different outcomes are a key that failed to capture
/// an input: a ticket, a fix and a schema bump.
///
/// What is compared is per kind (`AuditKind`): a check record field by field, any other
/// kind by outcome and the digest of its outputs. The kind list is open: a kind is audited
/// by name, and one that needs more than the generic comparison registers its own.
///
/// SAMPLING is seeded and deterministic: the keys the two stores share are ranked by
/// `sha256(seed LF key)`, so the same seed over the same stores audits the same records and
/// a red reproduces. Each audited key's whole history is compared, not only its answer: a
/// record a lookup once served is evidence too.
///
/// The audit only READS both stores (`OutcomeStore.readKeyAt`): it quarantines nothing, so
/// it never changes the store it judges.
module FsHotWatch.RecordAudit

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.Events

/// The report format.
[<Literal>]
let ReportSchema = "fshw-audit-v1"

/// The default number of keys audited per kind: ADR-059 §35 Change 2's nightly floor.
[<Literal>]
let DefaultSample = 200

/// One record as the audit compares it.
type Side =
    { Record: OutcomeStore.Record
      Outputs: string option }

/// How one record kind is audited.
[<NoComparison; NoEquality>]
type AuditKind =
    {
        Kind: string
        /// The fields in which `clean` differs from `stored`; empty when they agree.
        Differences: Side -> Side -> string list
    }

let private outcomeDiffers (stored: Side) (clean: Side) =
    if stored.Record.Outcome <> clean.Record.Outcome then
        [ "outcome" ]
    else
        []

/// The digest of a record's outputs, as filed; `none` for a record without outputs.
let outputsDigest (outputs: string option) : string =
    match outputs with
    | Some text -> ContentHash.ofText text
    | None -> "none"

module AuditKind =
    /// Any kind: the outcome, and the outputs byte for byte (a record's outputs are
    /// serialized deterministically, so equal outputs file equal bytes).
    let generic (kind: string) : AuditKind =
        { Kind = kind
          Differences =
            fun stored clean ->
                outcomeDiffers stored clean
                @ (if outputsDigest stored.Outputs <> outputsDigest clean.Outputs then
                       [ "outputs" ]
                   else
                       []) }

    let private diagnosticText (d: StoredDiagnostic) =
        String.concat
            "|"
            [ d.FileName
              string d.StartLine
              string d.StartColumn
              string d.EndLine
              string d.EndColumn
              string d.ErrorNumber
              StoredDiagnostic.severityToken d.Severity
              d.Subcategory
              d.Message ]

    /// A check record, field by field, so a mismatch names what moved: the diagnostics
    /// (compared as a multiset; their order is not an input), the FCS signature, the used
    /// signatures, the file's own API signature. Outputs a check record cannot have parse
    /// as `outputs-unreadable`.
    let check: AuditKind =
        { Kind = CheckRecords.CheckKind
          Differences =
            fun stored clean ->
                let parse (side: Side) =
                    side.Outputs |> Option.bind (CheckRecords.CheckOutputs.tryOfJson None)

                outcomeDiffers stored clean
                @ (match parse stored, parse clean with
                   | Some s, Some c ->
                       [ if
                             List.sort (List.map diagnosticText s.Diagnostics)
                             <> List.sort (List.map diagnosticText c.Diagnostics)
                         then
                             "diagnostics"
                         if s.FcsSignature <> c.FcsSignature then
                             "fcsSignature"
                         if s.UsedSignatures <> c.UsedSignatures then
                             "usedSignatures"
                         if s.ApiSignature <> c.ApiSignature then
                             "apiSignature" ]
                   | _ -> [ "outputs-unreadable" ]) }

    /// The kinds with a comparison of their own. Every other kind is audited by `generic`.
    let registered: AuditKind list = [ check ]

    /// How `kind` is audited.
    let find (kind: string) : AuditKind =
        registered
        |> List.tryFind (fun k -> k.Kind = kind)
        |> Option.defaultWith (fun () -> generic kind)

    /// The kinds audited when none is named. Kinds join as their comparison is shown not to
    /// red on a clean tree (AUTOMATION-1633 starts with check records).
    let defaults: string list = [ CheckRecords.CheckKind ]

/// What to audit.
type Options =
    {
        /// The kinds to sample. Ignored when `Keys` names records.
        Kinds: string list
        /// Keys audited per kind; None audits every shared key.
        Sample: int option
        /// The sampling seed: the same seed over the same stores audits the same keys.
        Seed: string
        /// Audit exactly these keys (`fshw audit --key`), whatever their kind.
        Keys: ActionKey.KeyValue list
        /// Sample keys with an input of this label first (e.g. a new key shape that most
        /// needs auditing), then the rest.
        PreferInput: string option
    }

module Options =
    let defaults =
        { Kinds = AuditKind.defaults
          Sample = Some DefaultSample
          Seed = "fshw-audit"
          Keys = []
          PreferInput = None }

/// A record as a report names it.
type RecordSummary =
    { Outcome: string
      OutputsDigest: string
      RecordedAt: DateTimeOffset
      Binary: string
      Worktree: string }

module RecordSummary =
    let ofSide (side: Side) =
        { Outcome =
            match side.Record.Outcome with
            | OutcomeStore.Outcome.Pass -> "pass"
            | OutcomeStore.Outcome.Fail -> "fail"
          OutputsDigest = outputsDigest side.Outputs
          RecordedAt = side.Record.RecordedAt
          Binary = side.Record.Producer.Binary
          Worktree = side.Record.Producer.Worktree }

/// A stored record that differs from the clean recompute under the same key: a key bug.
type Mismatch =
    {
        Kind: string
        Key: string
        Subject: string
        /// The fields that differ (`AuditKind.Differences`).
        Fields: string list
        /// Whether the differing record is its key's answer (what a lookup serves now),
        /// rather than an older record in its history.
        IsAnswer: bool
        Stored: RecordSummary
        Clean: RecordSummary
    }

/// One kind's counts.
type KindTally =
    {
        Kind: string
        /// Keys of this kind in the clean store.
        CleanKeys: int
        /// Of those, keys the audited store holds a readable record for.
        Shared: int
        /// Keys audited (at most the sample).
        Audited: int
        /// Stored records compared (a key's whole history).
        Records: int
        /// Audited keys with at least one differing record.
        Mismatched: int
    }

[<RequireQualifiedAccess>]
type Verdict =
    /// Something was compared and nothing differed.
    | Agree
    /// A stored record differs from the clean recompute.
    | Mismatch
    /// A kind (or a named key) compared nothing: no answer, never agreement.
    | Vacuous

type Report =
    {
        Seed: string
        Sample: int option
        AuditedStore: string
        CleanStore: string
        Kinds: KindTally list
        Mismatches: Mismatch list
        /// The audited keys, by kind, in sampling order: what `--key` re-audits.
        AuditedKeys: (string * string list) list
        /// Named keys (`--key`) one of the stores does not hold.
        Unmatched: string list
        /// Files in the clean store, and in the audited keys' directories, that are not
        /// records.
        UnreadableClean: int
        UnreadableAudited: int
    }

    member this.Verdict =
        if not this.Mismatches.IsEmpty then
            Verdict.Mismatch
        elif this.Kinds.IsEmpty || this.Kinds |> List.exists (fun k -> k.Audited = 0) then
            Verdict.Vacuous
        elif not this.Unmatched.IsEmpty then
            Verdict.Vacuous
        else
            Verdict.Agree

/// The process exit code of a verdict: 0 agree, 1 mismatch, 3 vacuous (as `replay-gate`).
let exitCode (verdict: Verdict) : int =
    match verdict with
    | Verdict.Agree -> 0
    | Verdict.Mismatch -> 1
    | Verdict.Vacuous -> 3

/// A key's sampling rank under `seed`.
let rank (seed: string) (key: ActionKey.KeyValue) : string =
    ContentHash.ofText $"%s{seed}\n%s{key.Text}"

let private hasInput (label: string) (record: OutcomeStore.Record) =
    record.Key.Inputs |> List.exists (fun (l, _) -> l = label)

/// Compare one audited key: every stored record under it against the clean answer.
let private compareKey (kind: AuditKind) (clean: Side) (stored: Side list) : Mismatch list =
    let last = List.length stored - 1

    stored
    |> List.mapi (fun i side ->
        match kind.Differences side clean with
        | [] -> None
        | fields ->
            Some
                { Kind = kind.Kind
                  Key = clean.Record.Key.Value.Text
                  Subject = clean.Record.Subject
                  Fields = fields
                  IsAnswer = (i = last)
                  Stored = RecordSummary.ofSide side
                  Clean = RecordSummary.ofSide clean })
    |> List.choose id

/// Audit the records `auditedRoot` holds against the clean recompute filed in `cleanRoot`.
let audit (auditedRoot: string) (cleanRoot: string) (options: Options) : Report =
    let cleanScan = OutcomeStore.readAllWithOutputs cleanRoot

    // The clean store's answer for each key: its newest record (the scan is oldest first).
    let cleanAnswers =
        cleanScan.Records
        |> List.groupBy (fun (r, _) -> r.Key.Value)
        |> List.map (fun (key, records) ->
            let record, outputs = List.last records
            key, { Record = record; Outputs = outputs })
        |> Map.ofList

    let mutable unreadableAudited = 0

    let storedOf (key: ActionKey.KeyValue) =
        let records, unreadable = OutcomeStore.readKeyAt auditedRoot key
        unreadableAudited <- unreadableAudited + unreadable

        records
        |> List.map (fun (record, outputs) -> { Record = record; Outputs = outputs })

    let holds (key: ActionKey.KeyValue) =
        let hex = key.Text.Substring ActionKey.Prefix.Length
        Directory.Exists(Path.Combine(auditedRoot, "records", hex.Substring(0, 2), hex))

    /// Audit `candidates` (clean answers of one kind) in sampling order, up to the sample.
    let auditKind (kindName: string) (candidates: Side list) (limit: int option) =
        let kind = AuditKind.find kindName

        let ordered =
            candidates
            |> List.filter (fun c -> holds c.Record.Key.Value)
            |> List.sortWith (fun a b ->
                let preferred (s: Side) =
                    match options.PreferInput with
                    | Some label when hasInput label s.Record -> 0
                    | _ -> 1

                match compare (preferred a) (preferred b) with
                | 0 ->
                    String.CompareOrdinal(rank options.Seed a.Record.Key.Value, rank options.Seed b.Record.Key.Value)
                | c -> c)

        // Read lazily: a key whose directory holds no readable record is not shared, and
        // the sample is filled from the next one.
        let audited =
            ordered
            |> Seq.map (fun clean -> clean, storedOf clean.Record.Key.Value)
            |> Seq.filter (fun (_, stored) -> not stored.IsEmpty)
            |> fun s ->
                match limit with
                | Some n -> Seq.truncate n s
                | None -> s
            |> List.ofSeq

        let mismatches =
            audited |> List.collect (fun (clean, stored) -> compareKey kind clean stored)

        let shared = ordered.Length

        { Kind = kindName
          CleanKeys = candidates.Length
          Shared = shared
          Audited = audited.Length
          Records = audited |> List.sumBy (fun (_, stored) -> stored.Length)
          Mismatched = mismatches |> List.map _.Key |> List.distinct |> List.length },
        mismatches,
        (kindName, audited |> List.map (fun (clean, _) -> clean.Record.Key.Value.Text))

    let results, unmatched =
        match options.Keys with
        | [] ->
            options.Kinds
            |> List.distinct
            |> List.map (fun kind ->
                let candidates =
                    cleanAnswers
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun s -> s.Record.Key.Kind = kind)

                auditKind kind candidates options.Sample),
            []
        | keys ->
            let found, missing =
                keys
                |> List.distinct
                |> List.partition (fun key -> cleanAnswers.ContainsKey key && holds key)

            found
            |> List.map (fun key -> cleanAnswers[key])
            |> List.groupBy _.Record.Key.Kind
            |> List.map (fun (kind, candidates) -> auditKind kind candidates None),
            missing |> List.map _.Text

    { Seed = options.Seed
      Sample = if options.Keys.IsEmpty then options.Sample else None
      AuditedStore = auditedRoot
      CleanStore = cleanRoot
      Kinds = results |> List.map (fun (tally, _, _) -> tally)
      Mismatches = results |> List.collect (fun (_, mismatches, _) -> mismatches)
      AuditedKeys = results |> List.map (fun (_, _, keys) -> keys)
      Unmatched = unmatched
      UnreadableClean = cleanScan.Unreadable
      UnreadableAudited = unreadableAudited }

let private verdictToken (verdict: Verdict) =
    match verdict with
    | Verdict.Agree -> "agree"
    | Verdict.Mismatch -> "mismatch"
    | Verdict.Vacuous -> "vacuous"

let private summaryJson (s: RecordSummary) =
    let o = JsonObject()
    o["outcome"] <- s.Outcome
    o["outputsDigest"] <- s.OutputsDigest
    o["recordedAt"] <- s.RecordedAt.ToString("o")
    o["binary"] <- s.Binary
    o["worktree"] <- s.Worktree
    o

/// The report as one `fshw-audit-v1` JSON object.
let toJson (report: Report) : string =
    let o = JsonObject()
    o["schema"] <- ReportSchema
    o["verdict"] <- verdictToken report.Verdict
    o["exitCode"] <- exitCode report.Verdict
    o["seed"] <- report.Seed

    match report.Sample with
    | Some n -> o["sample"] <- n
    | None -> o["sample"] <- null

    o["auditedStore"] <- report.AuditedStore
    o["cleanStore"] <- report.CleanStore
    let kinds = JsonArray()

    for k in report.Kinds do
        let e = JsonObject()
        e["kind"] <- k.Kind
        e["cleanKeys"] <- k.CleanKeys
        e["shared"] <- k.Shared
        e["audited"] <- k.Audited
        e["records"] <- k.Records
        e["mismatched"] <- k.Mismatched
        kinds.Add e

    o["kinds"] <- kinds
    let mismatches = JsonArray()

    for m in report.Mismatches do
        let e = JsonObject()
        e["kind"] <- m.Kind
        e["key"] <- m.Key
        e["subject"] <- m.Subject
        e["fields"] <- JsonArray(m.Fields |> List.map (fun f -> JsonValue.Create f :> JsonNode) |> Array.ofList)
        e["isAnswer"] <- m.IsAnswer
        e["stored"] <- summaryJson m.Stored
        e["clean"] <- summaryJson m.Clean
        mismatches.Add e

    o["mismatches"] <- mismatches
    let audited = JsonObject()

    for kind, keys in report.AuditedKeys do
        audited[kind] <- JsonArray(keys |> List.map (fun k -> JsonValue.Create k :> JsonNode) |> Array.ofList)

    o["auditedKeys"] <- audited

    o["unmatched"] <-
        JsonArray(
            report.Unmatched
            |> List.map (fun k -> JsonValue.Create k :> JsonNode)
            |> Array.ofList
        )

    o["unreadableClean"] <- report.UnreadableClean
    o["unreadableAudited"] <- report.UnreadableAudited
    o.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// The report for a person.
let render (report: Report) : string list =
    [ $"audited store: %s{report.AuditedStore}"
      $"clean store:   %s{report.CleanStore}"
      let sample =
          match report.Sample with
          | Some n -> string n
          | None -> "every shared key"

      $"seed %s{report.Seed}, sample %s{sample} per kind"

      for k in report.Kinds do
          $"  %-20s{k.Kind} clean keys %d{k.CleanKeys}, shared %d{k.Shared}, audited %d{k.Audited} (%d{k.Records} record(s)), mismatched %d{k.Mismatched}"

      for m in report.Mismatches do
          let answer = if m.IsAnswer then "answer" else "history"
          let fields = String.concat "," m.Fields
          let storedAt = m.Stored.RecordedAt.ToString("o")

          $"  MISMATCH %s{m.Kind} %s{m.Subject} %s{m.Key} (%s{answer}): %s{fields}; stored by %s{m.Stored.Worktree} at %s{storedAt}"

      for key in report.Unmatched do
          $"  not in both stores: %s{key}"

      if report.UnreadableClean + report.UnreadableAudited > 0 then
          $"  unreadable files: clean %d{report.UnreadableClean}, audited %d{report.UnreadableAudited}"

      let verdict =
          match report.Verdict with
          | Verdict.Agree -> "AGREE: every audited record equals its clean recompute"
          | Verdict.Mismatch ->
              $"MISMATCH: %d{report.Mismatches.Length} stored record(s) differ from a clean recompute under the same key: a key missed an input"
          | Verdict.Vacuous -> "VACUOUS: a kind or a named key compared nothing; no answer"

      $"audit: %s{verdict}" ]

/// The store directory of `repoRoot`'s repository under the cache home `cacheHome`: where a
/// recompute run with `FSHW_CACHE_HOME=<cacheHome>` files its records.
let storeUnderCacheHome (cacheHome: string) (repoRoot: string) : string =
    Path.Combine(cacheHome, "cache", "outcomes", RepoIdentity.namespaceOf repoRoot, "v1")

/// Refuse to audit a store against itself: every key would agree with its own record.
let validateStores (auditedRoot: string) (cleanRoot: string) : Result<unit, string> =
    let full (p: string) =
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)

    if full auditedRoot = full cleanRoot then
        Error "the clean store is the audited store: a store always agrees with itself"
    elif not (Directory.Exists cleanRoot) then
        Error $"no clean store at %s{cleanRoot}: run a recompute with an empty FSHW_CACHE_HOME first"
    else
        Ok()

/// An audit as a front end (`fshw audit`, `fshw-bench audit`) asks for it, before it is
/// resolved against a repository.
type Request =
    {
        /// The clean store's directory.
        Clean: string option
        /// The cache home a recompute ran under; the clean store is the repository's in it.
        CleanHome: string option
        /// The audited store; None for the repository's box-wide one.
        Store: string option
        Kinds: string list
        /// Keys per kind; None for the default, 0 for every shared key.
        Sample: int option
        Seed: string option
        Keys: string list
        PreferInput: string option
    }

module Request =
    let empty =
        { Clean = None
          CleanHome = None
          Store = None
          Kinds = []
          Sample = None
          Seed = None
          Keys = []
          PreferInput = None }

/// An audit resolved against a repository: the two stores and what to compare.
type Setup =
    { AuditedStore: string
      CleanStore: string
      Options: Options }

/// Resolve `request` against `repoRoot`; Error names what is wrong with it.
let resolve (repoRoot: string) (request: Request) : Result<Setup, string> =
    let clean =
        match request.Clean, request.CleanHome with
        | Some store, None -> Ok(Path.GetFullPath store)
        | None, Some home -> Ok(storeUnderCacheHome (Path.GetFullPath home) repoRoot)
        | Some _, Some _ -> Error "pass --clean or --clean-home, not both"
        | None, None ->
            Error
                "name the clean recompute's store: --clean-home <the FSHW_CACHE_HOME a from-scratch check ran under>, or --clean <store dir>"

    let sample =
        match request.Sample with
        | None -> Ok(Some DefaultSample)
        | Some 0 -> Ok None
        | Some n when n > 0 -> Ok(Some n)
        | Some n -> Error $"--sample must be 0 (every key) or positive, not %d{n}"

    let keys =
        request.Keys
        |> List.map (fun text ->
            match ActionKey.KeyValue.tryParse text with
            | Some key -> Ok key
            | None -> Error $"not a record key: %s{text}")

    let badKey =
        keys
        |> List.tryPick (function
            | Error e -> Some e
            | Ok _ -> None)

    match clean, sample, badKey with
    | Error e, _, _
    | _, Error e, _
    | _, _, Some e -> Error e
    | Ok cleanStore, Ok sample, None ->
        Ok
            { AuditedStore =
                request.Store
                |> Option.map Path.GetFullPath
                |> Option.defaultWith (fun () -> OutcomeStore.directoryFor repoRoot)
              CleanStore = cleanStore
              Options =
                { Kinds =
                    if request.Kinds.IsEmpty then
                        AuditKind.defaults
                    else
                        request.Kinds
                  Sample = sample
                  Seed = request.Seed |> Option.defaultValue Options.defaults.Seed
                  Keys = keys |> List.choose Result.toOption
                  PreferInput = request.PreferInput } }

/// Run a resolved audit: refuse a store audited against itself or a missing clean store,
/// then compare. Error is a usage error (exit 2).
let execute (setup: Setup) : Result<Report, string> =
    validateStores setup.AuditedStore setup.CleanStore
    |> Result.map (fun () -> audit setup.AuditedStore setup.CleanStore setup.Options)

/// A front end's whole run: resolve, execute, write `out` when given, print the report
/// (`json` or human) through `print`, and return the exit code (2 for a usage error, which
/// is printed through `printError`).
let runRequest
    (repoRoot: string)
    (request: Request)
    (json: bool)
    (out: string option)
    (print: string -> unit)
    (printError: string -> unit)
    : int =
    match resolve repoRoot request |> Result.bind execute with
    | Error reason ->
        printError $"audit: %s{reason}"
        2
    | Ok report ->
        let text = toJson report

        out
        |> Option.iter (fun path ->
            let path = Path.GetFullPath path
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, text))

        if json then
            print text
        else
            render report |> List.iter print

        exitCode report.Verdict
