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
/// record a lookup once served is evidence too. Except for an INDEX kind (`Judged.AnswerOnly`),
/// keyed by the request rather than by content: its older records describe older trees, so
/// only its answer is judged (AUTOMATION-1841).
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

/// A record kind the audit can be asked for: the closed set of kinds FsHotWatch files.
/// `fshw audit --kind` parses into it at the boundary, so a misspelled or comma-joined name
/// is refused by name instead of being audited as a kind no store holds (a vacuous answer).
[<RequireQualifiedAccess>]
type RecordKind =
    | Check
    | Options
    | OptionsInputs
    | OptionsPortable
    | OptionsPortableInputs
    | Format
    | TestPruneAnalysis
    | Analyze
    | Test
    | TestEvidence

module RecordKind =
    /// Every kind, in the order help and refusals list them.
    let all: RecordKind list =
        [ RecordKind.Check
          RecordKind.Options
          RecordKind.OptionsInputs
          RecordKind.OptionsPortable
          RecordKind.OptionsPortableInputs
          RecordKind.Format
          RecordKind.TestPruneAnalysis
          RecordKind.Analyze
          RecordKind.Test
          RecordKind.TestEvidence ]

    /// The name the kind's producer files records under. Producers compiled after this
    /// module (OptionsPortable, the plugins) are pinned to these names by a test.
    let name (kind: RecordKind) : string =
        match kind with
        | RecordKind.Check -> CheckRecords.CheckKind
        | RecordKind.Options -> CheckRecords.OptionsKind
        | RecordKind.OptionsInputs -> OptionsRecords.InputsKind
        | RecordKind.OptionsPortable -> "options-portable"
        | RecordKind.OptionsPortableInputs -> "options-portable-inputs"
        | RecordKind.Format -> "format"
        | RecordKind.TestPruneAnalysis -> "testprune-analysis"
        | RecordKind.Analyze -> "analyze"
        | RecordKind.Test -> "test"
        | RecordKind.TestEvidence -> "test-evidence"

    let tryParse (text: string) : RecordKind option =
        all |> List.tryFind (fun k -> name k = text)

/// Which of a key's stored records the audit judges (AUTOMATION-1841).
[<RequireQualifiedAccess>]
type Judged =
    /// Every record under the key: a CONTENT-keyed kind, whose key claims each record is what
    /// these inputs produce, so an older record that differs is a key that missed an input.
    | EveryRecord
    /// Only the key's answer, its newest record: an INDEX kind, keyed by the request rather
    /// than by content (the options evaluation's `options-inputs` and
    /// `options-portable-inputs`). Each evaluation appends what that request read on the
    /// tree of the day, so an older record describes an older tree and is superseded, never
    /// a mismatch; only the answer is ever served.
    | AnswerOnly

/// How one record kind is audited.
[<NoComparison; NoEquality>]
type AuditKind =
    {
        Kind: string
        /// The fields in which `clean` differs from `stored`; empty when they agree.
        Differences: Side -> Side -> string list
        Judged: Judged
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
    /// `node` with `root` tokenized inside every string value, recursively. A string that holds
    /// JSON (test-prune files its spans so) is filed with its quotes escaped as `\u0022`, which
    /// hides the root's left boundary from a tokenizer run over the raw text; on the decoded
    /// string the quote is a quote.
    let rec private tokenizeStrings (root: string) (node: JsonNode) : JsonNode =
        match node with
        | :? JsonObject as o ->
            let copy = JsonObject()

            for p in o do
                copy[p.Key] <-
                    if isNull p.Value then
                        null
                    else
                        tokenizeStrings root p.Value

            copy
        | :? JsonArray as a -> JsonArray([| for item in a -> if isNull item then null else tokenizeStrings root item |])
        | _ ->
            match node.GetValueKind() with
            | JsonValueKind.String -> JsonValue.Create(PathToken.tokenize root (node.GetValue<string>()))
            | _ -> node.DeepClone()

    /// A record's outputs with its producer's checkout root as `PathToken.Token` in every
    /// string, at a whole path prefix (`PathToken.tokenize`, AUTOMATION-1602 (b)): a record
    /// filed in one checkout and recomputed in another names its files under two roots, and
    /// that is not a difference in what the action computed. Re-serialized, so both sides
    /// compare in one spelling; outputs that are not JSON are tokenized as text.
    let normalizedOutputs (side: Side) : string option =
        let root = side.Record.Producer.Worktree

        side.Outputs
        |> Option.map (fun text ->
            try
                match JsonNode.Parse text with
                | null -> text
                | node -> (tokenizeStrings root node).ToJsonString()
            with :? JsonException ->
                PathToken.tokenize root text)

    /// Any kind: the outcome, and the outputs byte for byte once the producer's checkout
    /// root is a token (a record's outputs are serialized deterministically, so equal
    /// outputs file equal bytes).
    let generic (kind: string) : AuditKind =
        { Kind = kind
          Judged = Judged.EveryRecord
          Differences =
            fun stored clean ->
                outcomeDiffers stored clean
                @ (if
                       outputsDigest (normalizedOutputs stored)
                       <> outputsDigest (normalizedOutputs clean)
                   then
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
          Judged = Judged.EveryRecord
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

    /// The top-level fields of a JSON object, each as comparable text: an array as the
    /// sorted multiset of its elements (`upper` folds case, for a set collected
    /// case-insensitively), anything else as its raw text. None for text that is no object.
    let private fieldsOf (upper: Set<string>) (text: string) : Map<string, string list> option =
        try
            use doc = JsonDocument.Parse text

            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                Some(
                    doc.RootElement.EnumerateObject()
                    |> Seq.map (fun p ->
                        let fold (s: string) =
                            if upper.Contains p.Name then s.ToUpperInvariant() else s

                        match p.Value.ValueKind with
                        | JsonValueKind.Array ->
                            p.Name,
                            p.Value.EnumerateArray()
                            |> Seq.map (fun e -> fold (e.GetRawText()))
                            |> Seq.sort
                            |> List.ofSeq
                        | _ -> p.Name, [ p.Value.GetRawText() ])
                    |> Map.ofSeq
                )
        with :? JsonException ->
            None

    /// The fields in which two field maps differ, by name.
    let private fieldDifferences (stored: Map<string, string list>) (clean: Map<string, string list>) =
        Set.union (Set.ofSeq stored.Keys) (Set.ofSeq clean.Keys)
        |> Set.toList
        |> List.filter (fun name -> Map.tryFind name stored <> Map.tryFind name clean)

    /// The options evaluation's declared inputs (AUTOMATION-1633). `reads`, `probes`,
    /// `listings` and `envNames` are SETS, collected concurrently and written in hash
    /// order, so two evaluations of one project file them in different orders: each is
    /// compared as a set (`envNames` case-insensitively, as it is collected), every other
    /// field exactly, all with the producer's checkout root as a token. An index: only the
    /// answer is judged (`Judged.AnswerOnly`).
    let optionsInputs: AuditKind =
        { Kind = OptionsRecords.InputsKind
          Judged = Judged.AnswerOnly
          Differences =
            fun stored clean ->
                let parse side =
                    normalizedOutputs side |> Option.bind (fieldsOf (set [ "envNames" ]))

                outcomeDiffers stored clean
                @ (match parse stored, parse clean with
                   | Some s, Some c -> fieldDifferences s c
                   | _ -> [ "outputs-unreadable" ]) }

    /// A test project's run (`test`): the outcome and the counts. Its duration is not an
    /// outcome; it differs run to run.
    let test: AuditKind =
        { Kind = "test"
          Judged = Judged.EveryRecord
          Differences =
            fun stored clean ->
                outcomeDiffers stored clean
                @ (if stored.Record.Counts <> clean.Record.Counts then
                       [ "counts" ]
                   else
                       []) }

    /// The kind name of an entry of the test-evidence store (`TestEvidenceStore`), which
    /// lives beside the outcome store rather than in it.
    [<Literal>]
    let TestEvidenceKind = "test-evidence"

    /// The deterministic part of a CTRF report: each test's name and status, sorted. Its
    /// report id, timestamps and durations are a run's, not the code's.
    let internal ctrfResults (report: string) : string list option =
        try
            use doc = JsonDocument.Parse report

            match
                Json.tryProp doc.RootElement "results"
                |> Option.bind (fun r -> Json.tryProp r "tests")
            with
            | Some tests when tests.ValueKind = JsonValueKind.Array ->
                Some(
                    tests.EnumerateArray()
                    |> Seq.map (fun t ->
                        let field name =
                            Json.tryString t name |> Option.defaultValue ""

                        $"""%s{field "name"}|%s{field "status"}""")
                    |> Seq.sort
                    |> List.ofSeq
                )
            | _ -> None
        with :? JsonException ->
            None

    /// A test-evidence entry (`evidenceSide`): the project, the counts and each test's name
    /// and status. NOT compared, because a rerun of the same inputs moves them: the producer
    /// (checkout, run id, tree hash, time), the duration, the report's id and timestamps, and
    /// the coverage report (its path names the run; its hit counts follow scheduling).
    let testEvidence: AuditKind =
        { Kind = TestEvidenceKind
          Judged = Judged.EveryRecord
          Differences =
            fun stored clean ->
                [ if stored.Record.Subject <> clean.Record.Subject then
                      "project"
                  if stored.Record.Counts <> clean.Record.Counts then
                      "counts"
                  match stored.Outputs |> Option.bind ctrfResults, clean.Outputs |> Option.bind ctrfResults with
                  | Some s, Some c when s <> c -> "tests"
                  | Some _, Some _ -> ()
                  | _ -> "report-unreadable" ] }

    /// How `kind` is audited: four kinds have a comparison of their own, the rest `generic`;
    /// the two options index kinds judge only their answer.
    let find (kind: RecordKind) : AuditKind =
        match kind with
        | RecordKind.Check -> check
        | RecordKind.OptionsInputs -> optionsInputs
        | RecordKind.Test -> test
        | RecordKind.TestEvidence -> testEvidence
        | RecordKind.OptionsPortableInputs ->
            { generic (RecordKind.name kind) with
                Judged = Judged.AnswerOnly }
        | RecordKind.Options
        | RecordKind.OptionsPortable
        | RecordKind.Format
        | RecordKind.TestPruneAnalysis
        | RecordKind.Analyze -> generic (RecordKind.name kind)

    /// How a record named by its stored key (`--key`) is audited: the store is a boundary,
    /// and a kind this build does not know is compared generically.
    let ofStoredName (kind: string) : AuditKind =
        match RecordKind.tryParse kind with
        | Some known -> find known
        | None -> generic kind

    /// The kinds audited when none is named. Kinds join as their comparison is shown not to
    /// red on a clean tree (AUTOMATION-1633 starts with check records).
    let defaults: RecordKind list = [ RecordKind.Check ]

    /// Every kind a clean recompute files, for `--kind all`. The portable options kinds
    /// are filed only in shadow mode (AUTOMATION-1684), so `all` does not ask for them.
    let all: RecordKind list =
        [ RecordKind.Check
          RecordKind.Options
          RecordKind.OptionsInputs
          RecordKind.Format
          RecordKind.TestPruneAnalysis
          RecordKind.Analyze
          RecordKind.Test
          RecordKind.TestEvidence ]

    /// The keyword `--kind` takes for `all`.
    [<Literal>]
    let AllKeyword = "all"

    /// `--kind` values (each repeatable and comma-separated; `all` for `all`) as kinds, in
    /// first-named order; Error names every unknown name and lists the valid ones.
    let parse (values: string list) : Result<RecordKind list, string> =
        let names =
            values
            |> List.collect (fun v -> v.Split(',') |> List.ofArray)
            |> List.map _.Trim()

        let lookup name =
            if name = AllKeyword then
                Some all
            else
                RecordKind.tryParse name |> Option.map List.singleton

        let folded =
            names
            |> List.fold
                (fun acc name ->
                    match acc, lookup name with
                    | Ok kinds, Some more -> Ok(kinds @ more)
                    | Ok _, None -> Error [ name ]
                    | Error unknown, None -> Error(unknown @ [ name ])
                    | Error unknown, Some _ -> Error unknown)
                (Ok [])

        match folded with
        | Ok kinds -> Ok(List.distinct kinds)
        | Error unknown ->
            let valid = (RecordKind.all |> List.map RecordKind.name) @ [ AllKeyword ]
            let quoted = unknown |> List.map (fun n -> $"'%s{n}'") |> String.concat ", "

            Error
                $"""unknown record kind %s{quoted}; --kind takes one of: %s{String.concat ", " valid} (repeat the flag or separate kinds with commas)"""

/// What to audit.
type Options =
    {
        /// The kinds to sample. Ignored when `Keys` names records.
        Kinds: RecordKind list
        /// Keys audited per kind; None audits every shared key.
        Sample: int option
        /// The sampling seed: the same seed over the same stores audits the same keys.
        Seed: string
        /// Audit exactly these keys (`fshw audit --key`), whatever their kind.
        Keys: ActionKey.KeyValue list
        /// Sample keys with an input of this label first (e.g. a new key shape that most
        /// needs auditing), then the rest.
        PreferInput: string option
        /// Shadow logs to read lookup agreement from (ADR-074 (b)); none asks nothing.
        ShadowLogs: string list
    }

module Options =
    let defaults =
        { Kinds = AuditKind.defaults
          Sample = Some DefaultSample
          Seed = "fshw-audit"
          Keys = []
          PreferInput = None
          ShadowLogs = [] }

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
        /// Stored records compared (a key's whole history; an index kind's answer only).
        Records: int
        /// Audited keys with at least one differing record.
        Mismatched: int
    }

/// What one kind's audit established beside its counts (AUTOMATION-1684).
[<RequireQualifiedAccess>]
type KindFinding =
    /// At least one key was compared.
    | Compared
    /// No key was compared, so the kind answers nothing: never agreement.
    | Vacuous of why: string

module KindTally =
    let finding (k: KindTally) : KindFinding =
        if k.Audited > 0 then
            KindFinding.Compared
        elif k.CleanKeys = 0 then
            KindFinding.Vacuous "the clean store holds no key of this kind"
        elif k.Shared = 0 then
            KindFinding.Vacuous "the audited store holds none of its clean keys"
        else
            KindFinding.Vacuous "no shared key holds a readable record"

/// A kind the audit had no store to read for, and why (AUTOMATION-1767). It has no counts: it
/// never looked. Distinct from vacuous, which looked and compared nothing; like vacuous, it is
/// no answer, never agreement.
type NotAuditedKind = { Name: string; Why: string }

/// What the shadow logs say about the lookup on the trees today's daemon graded: ADR-074's
/// night count (b), LOOKUP AGREEMENT (AUTOMATION-1577). Publications only (`check`,
/// `confirm`); a `fshw verdict` read and a line the shadow could not compare are not counted.
type AgreementTally =
    {
        /// The logs read.
        Logs: string list
        /// Publications compared.
        Publications: int
        /// Of those, the ones today's daemon graded green.
        DaemonGreen: int
        /// Of the daemon greens, the ones the lookup also answered green.
        LookupGreen: int
        /// Publications the daemon graded RED that the lookup answered green: a false green.
        LookupGreenOnRed: int
        /// Lines that are not a shadow line.
        Unreadable: int
    }

/// What an agreement tally establishes.
[<RequireQualifiedAccess>]
type AgreementFinding =
    /// Some daemon greens were answered green by the lookup, and no red was.
    | Agreeing
    /// No publication was graded green, so nothing was compared: no answer.
    | NoDaemonGreen
    /// Daemon greens exist and the lookup answered none of them green: 0% agreement.
    | NoAgreement
    /// The lookup answered green on trees the daemon graded red.
    | GreenOnRed of count: int

module AgreementTally =
    /// The tally of shadow-log `lines` read from `logs`.
    let ofLines (logs: string list) (lines: string seq) : AgreementTally =
        let read (line: string) =
            try
                use doc = JsonDocument.Parse line
                let root = doc.RootElement

                let text (el: JsonElement) (name: string) =
                    match el.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None

                let child (name: string) =
                    match root.TryGetProperty name with
                    | true, v when v.ValueKind = JsonValueKind.Object -> Some v
                    | _ -> None

                Some(
                    text root "command",
                    child "today" |> Option.bind (fun t -> text t "class"),
                    child "lookup" |> Option.bind (fun l -> text l "outcome")
                )
            with :? JsonException ->
                None

        let empty =
            { Logs = logs
              Publications = 0
              DaemonGreen = 0
              LookupGreen = 0
              LookupGreenOnRed = 0
              Unreadable = 0 }

        lines
        |> Seq.filter (fun l -> not (String.IsNullOrWhiteSpace l))
        |> Seq.fold
            (fun t line ->
                match read line with
                | None -> { t with Unreadable = t.Unreadable + 1 }
                | Some(Some("check" | "confirm"), Some today, Some(("green" | "red" | "missing") as lookup)) ->
                    { t with
                        Publications = t.Publications + 1
                        DaemonGreen = t.DaemonGreen + (if today = "green" then 1 else 0)
                        LookupGreen = t.LookupGreen + (if today = "green" && lookup = "green" then 1 else 0)
                        LookupGreenOnRed = t.LookupGreenOnRed + (if today = "red" && lookup = "green" then 1 else 0) }
                | Some _ -> t)
            empty

    let finding (t: AgreementTally) : AgreementFinding =
        if t.LookupGreenOnRed > 0 then
            AgreementFinding.GreenOnRed t.LookupGreenOnRed
        elif t.DaemonGreen = 0 then
            AgreementFinding.NoDaemonGreen
        elif t.LookupGreen = 0 then
            AgreementFinding.NoAgreement
        else
            AgreementFinding.Agreeing

    /// The share of daemon greens the lookup answered green, in percent.
    let rate (t: AgreementTally) : float =
        if t.DaemonGreen = 0 then
            0.0
        else
            100.0 * float t.LookupGreen / float t.DaemonGreen

/// Whether an audit was asked about lookup agreement, and what the logs said.
[<RequireQualifiedAccess>]
type LookupAgreement =
    /// No shadow log was named: the report says nothing about agreement.
    | NotAsked
    | Tallied of AgreementTally

module LookupAgreement =
    /// The agreement of the shadow logs at `paths`; `NotAsked` for none. A log that is absent or
    /// cannot be read contributes no line, and is still named in `Logs`.
    let read (paths: string list) : LookupAgreement =
        match paths with
        | [] -> LookupAgreement.NotAsked
        | _ ->
            let lines =
                paths
                |> Seq.collect (fun p ->
                    try
                        if File.Exists p then
                            File.ReadAllLines p :> string seq
                        else
                            Seq.empty
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> Seq.empty)

            LookupAgreement.Tallied(AgreementTally.ofLines paths lines)

[<RequireQualifiedAccess>]
type Verdict =
    /// Something was compared and nothing differed.
    | Agree
    /// A stored record differs from the clean recompute.
    | Mismatch
    /// A kind (or a named key) compared nothing: no answer, never agreement.
    | Vacuous

/// What lookup agreement adds to an audit: nothing when it was not asked or agrees; otherwise the
/// verdict it imposes and the line that says why. The one place both the verdict and the text read.
let agreementAlarm (agreement: LookupAgreement) : (Verdict * string) option =
    match agreement with
    | LookupAgreement.NotAsked -> None
    | LookupAgreement.Tallied t ->
        match AgreementTally.finding t with
        | AgreementFinding.Agreeing -> None
        | AgreementFinding.NoDaemonGreen ->
            Some(Verdict.Vacuous, "LOOKUP AGREEMENT VACUOUS: no publication was graded green, so nothing was compared")
        | AgreementFinding.NoAgreement ->
            Some(
                Verdict.Vacuous,
                $"LOOKUP AGREEMENT 0%%: the lookup answered none of %d{t.DaemonGreen} daemon-green publication(s) green"
            )
        | AgreementFinding.GreenOnRed n ->
            Some(
                Verdict.Mismatch,
                $"LOOKUP GREEN ON RED: %d{n} publication(s) the daemon graded red were answered green by the lookup"
            )

type Report =
    {
        Seed: string
        Sample: int option
        AuditedStore: string
        CleanStore: string
        Kinds: KindTally list
        /// Kinds asked for that had no store to read (AUTOMATION-1767): beside `Kinds`, not in it.
        NotAudited: NotAuditedKind list
        Mismatches: Mismatch list
        /// The audited keys, by kind, in sampling order: what `--key` re-audits.
        AuditedKeys: (string * string list) list
        /// Named keys (`--key`) one of the stores does not hold.
        Unmatched: string list
        /// Files in the clean store, and in the audited keys' directories, that are not
        /// records.
        UnreadableClean: int
        UnreadableAudited: int
        /// What the shadow logs say about lookup agreement (ADR-074 (b)), when the audit was asked.
        Agreement: LookupAgreement
        /// The audited store's quarantine (AUTOMATION-1701, ADR-072): every quarantined file by
        /// kind, key and when, so none is out of sight. Reported, never a verdict.
        Quarantine: OutcomeStore.QuarantineCensus
    }

    /// The records' verdict, unless lookup agreement raises an alarm (`agreementAlarm`); a record
    /// mismatch outranks it.
    member this.Verdict =
        match this.RecordsVerdict, agreementAlarm this.Agreement with
        | Verdict.Mismatch, _ -> Verdict.Mismatch
        | records, None -> records
        | _, Some(alarm, _) -> alarm

    /// The verdict of the record comparison alone, whatever lookup agreement says: ADR-074's night
    /// count (a), RECORD CORRECTNESS, which counts on this and never on the overall `Verdict`.
    member this.RecordsVerdict =
        if not this.Mismatches.IsEmpty then
            Verdict.Mismatch
        elif
            this.Kinds.IsEmpty
            || this.Kinds |> List.exists (fun k -> KindTally.finding k <> KindFinding.Compared)
        then
            Verdict.Vacuous
        // A kind with no store to read answers nothing, as a vacuous one does (exit 3).
        elif not this.NotAudited.IsEmpty then
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

/// The records of a key's history (oldest first, the answer last) that `kind` judges, each
/// with whether it is the answer.
let private judgedRecords (kind: AuditKind) (stored: Side list) : (bool * Side) list =
    let last = List.length stored - 1
    let all = stored |> List.mapi (fun i side -> i = last, side)

    match kind.Judged with
    | Judged.EveryRecord -> all
    | Judged.AnswerOnly -> all |> List.filter fst

/// Compare one audited key: the stored records under it that `kind` judges against the clean
/// answer.
let private compareKey (kind: AuditKind) (clean: Side) (stored: Side list) : Mismatch list =
    judgedRecords kind stored
    |> List.map (fun (isAnswer, side) ->
        match kind.Differences side clean with
        | [] -> None
        | fields ->
            Some
                { Kind = kind.Kind
                  Key = clean.Record.Key.Value.Text
                  Subject = clean.Record.Subject
                  Fields = fields
                  IsAnswer = isAnswer
                  Stored = RecordSummary.ofSide side
                  Clean = RecordSummary.ofSide clean })
    |> List.choose id

/// Which store a cache home files under `cache/<area>`.
[<RequireQualifiedAccess>]
type StoreArea =
    | Outcomes
    | Evidence

/// A store directory a cache home files: `<home>/cache/<area>/<namespace>/v1` (AUTOMATION-1767).
type CacheHomeStore =
    { Home: string
      Area: StoreArea
      Namespace: string }

module CacheHomeStore =
    let private areaName (area: StoreArea) =
        match area with
        | StoreArea.Outcomes -> "outcomes"
        | StoreArea.Evidence -> "evidence"

    /// The store's directory.
    let path (store: CacheHomeStore) : string =
        Path.Combine(store.Home, "cache", areaName store.Area, store.Namespace, "v1")

    /// `directory` read as a store a cache home files; None for a directory of any other shape.
    let tryParse (directory: string) : CacheHomeStore option =
        let rec upward (d: DirectoryInfo) =
            match Option.ofObj d.Parent with
            | Some parent -> d :: upward parent
            | None -> [ d ]

        let area (name: string) =
            [ StoreArea.Outcomes; StoreArea.Evidence ]
            |> List.tryFind (areaName >> (=) name)

        match upward (DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath directory))) with
        | v1 :: ns :: areaDir :: cache :: home :: _ when v1.Name = "v1" && cache.Name = "cache" ->
            area areaDir.Name
            |> Option.map (fun a ->
                { Home = home.FullName
                  Area = a
                  Namespace = ns.Name })
        | _ -> None

    /// The evidence store the same cache home files beside the outcome store `directory`; None
    /// when `directory` is not a cache home's outcome store.
    let evidenceBesideOutcomes (directory: string) : string option =
        tryParse directory
        |> Option.filter (_.Area >> (=) StoreArea.Outcomes)
        |> Option.map (fun store -> path { store with Area = StoreArea.Evidence })

/// The test-evidence store one side of an audit reads, or why that side names none
/// (AUTOMATION-1767). An unnamed side makes test-evidence NOT AUDITED, never vacuous.
[<RequireQualifiedAccess>]
type EvidenceStore =
    | At of directory: string
    | Unnamed of why: string

/// The stores one audit compares: the outcome stores, and the test-evidence stores beside them.
type Stores =
    { Audited: string
      Clean: string
      AuditedEvidence: EvidenceStore
      CleanEvidence: EvidenceStore }

/// One asked-for kind: compared (its tally, mismatches and audited keys), or not audited.
[<RequireQualifiedAccess>]
type private KindRun =
    | Ran of KindTally * Mismatch list * (string * string list)
    | NotRun of NotAuditedKind

/// A test-evidence entry as the audit compares it: kind `test-evidence`, keyed by the
/// entry's key, its project as the subject, its counts, and its CTRF report as outputs.
let internal evidenceSide (entry: TestEvidenceStore.Entry) : Side option =
    ActionKey.KeyValue.tryParse (ActionKey.Prefix + entry.Key)
    |> Option.map (fun value ->
        { Record =
            { Key =
                { Kind = AuditKind.TestEvidenceKind
                  Value = value
                  Inputs = entry.KeyInputs }
              Subject = entry.Project
              Outcome = OutcomeStore.Outcome.Pass
              Counts =
                Some
                    { Total = entry.Summary.Tests
                      Passed = entry.Summary.Passed
                      Failed = entry.Summary.Failed
                      Skipped = entry.Summary.Skipped }
              DurationMs = Some entry.Summary.DurationMs
              Producer =
                { Binary = entry.Producer.ProducerHash
                  Worktree = entry.Producer.Worktree
                  RunId = Some entry.Producer.RunId }
              RecordedAt = entry.Producer.EarnedAt }
          Outputs = Some entry.Report })

/// One evidence file, read without changing the store: its entry, or None for a file that
/// is not one.
let private readEvidenceFile (path: string) : Side option =
    try
        match TestEvidenceStore.decode (File.ReadAllText path) with
        | TestEvidenceStore.Decoded.Entry entry -> evidenceSide entry
        | _ -> None
    with :? IOException ->
        None

/// Where an evidence store files the entry of key `hex` (`TestEvidenceStore`).
let private evidencePath (root: string) (hex: string) =
    Path.Combine(root, hex.Substring(0, 2), hex + ".json")

/// Every entry of an evidence store, and the count of files that are not one. Only the
/// two-hex shard directories hold entries; its project index and quarantine are skipped.
let internal readEvidenceStore (root: string) : Side list * int =
    if not (Directory.Exists root) then
        [], 0
    else
        let read =
            [ for shard in Directory.GetDirectories root do
                  if (Path.GetFileName shard).Length = 2 then
                      for file in Directory.GetFiles(shard, "*.json") -> readEvidenceFile file ]

        List.choose id read, read |> List.filter Option.isNone |> List.length

/// Audit the records `auditedRoot` holds against the clean recompute filed in `cleanRoot`.
let auditStores (stores: Stores) (options: Options) : Report =
    let auditedRoot = stores.Audited
    let cleanRoot = stores.Clean
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
    let mutable unreadableClean = cleanScan.Unreadable

    let storedOf (key: ActionKey.KeyValue) =
        let records, unreadable = OutcomeStore.readKeyAt auditedRoot key
        unreadableAudited <- unreadableAudited + unreadable

        records
        |> List.map (fun (record, outputs) -> { Record = record; Outputs = outputs })

    let holds (key: ActionKey.KeyValue) =
        let hex = key.Text.Substring ActionKey.Prefix.Length
        Directory.Exists(Path.Combine(auditedRoot, "records", hex.Substring(0, 2), hex))

    /// Audit `candidates` (clean answers of one kind) in sampling order, up to the sample,
    /// against the store `holds` and `storedOf` read.
    let auditKind
        (holds: ActionKey.KeyValue -> bool)
        (storedOf: ActionKey.KeyValue -> Side list)
        (kind: AuditKind)
        (candidates: Side list)
        (limit: int option)
        =

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

        { Kind = kind.Kind
          CleanKeys = candidates.Length
          Shared = shared
          Audited = audited.Length
          Records = audited |> List.sumBy (fun (_, stored) -> (judgedRecords kind stored).Length)
          Mismatched = mismatches |> List.map _.Key |> List.distinct |> List.length },
        mismatches,
        (kind.Kind, audited |> List.map (fun (clean, _) -> clean.Record.Key.Value.Text))

    let runs, unmatched =
        match options.Keys with
        | [] ->
            options.Kinds
            |> List.distinct
            |> List.map (fun kind ->
                match kind with
                | RecordKind.TestEvidence ->
                    // Test evidence lives in its own store beside the outcome store.
                    match stores.CleanEvidence, stores.AuditedEvidence with
                    | EvidenceStore.At cleanEvidence, EvidenceStore.At auditedEvidence ->
                        let candidates, unreadable = readEvidenceStore cleanEvidence
                        unreadableClean <- unreadableClean + unreadable

                        let storedEvidence (key: ActionKey.KeyValue) =
                            let path = evidencePath auditedEvidence (key.Text.Substring ActionKey.Prefix.Length)

                            if File.Exists path then
                                match readEvidenceFile path with
                                | Some side -> [ side ]
                                | None ->
                                    unreadableAudited <- unreadableAudited + 1
                                    []
                            else
                                []

                        let holdsEvidence (key: ActionKey.KeyValue) =
                            File.Exists(evidencePath auditedEvidence (key.Text.Substring ActionKey.Prefix.Length))

                        KindRun.Ran(
                            auditKind holdsEvidence storedEvidence (AuditKind.find kind) candidates options.Sample
                        )
                    | EvidenceStore.Unnamed cleanWhy, EvidenceStore.Unnamed auditedWhy ->
                        KindRun.NotRun
                            { Name = AuditKind.TestEvidenceKind
                              Why = $"clean side: %s{cleanWhy}; audited side: %s{auditedWhy}" }
                    | EvidenceStore.Unnamed why, EvidenceStore.At _ ->
                        KindRun.NotRun
                            { Name = AuditKind.TestEvidenceKind
                              Why = $"clean side: %s{why}" }
                    | EvidenceStore.At _, EvidenceStore.Unnamed why ->
                        KindRun.NotRun
                            { Name = AuditKind.TestEvidenceKind
                              Why = $"audited side: %s{why}" }
                | _ ->
                    let candidates =
                        cleanAnswers
                        |> Map.toList
                        |> List.map snd
                        |> List.filter (fun s -> s.Record.Key.Kind = RecordKind.name kind)

                    KindRun.Ran(auditKind holds storedOf (AuditKind.find kind) candidates options.Sample)),
            []
        | keys ->
            let found, missing =
                keys
                |> List.distinct
                |> List.partition (fun key -> cleanAnswers.ContainsKey key && holds key)

            found
            |> List.map (fun key -> cleanAnswers[key])
            |> List.groupBy _.Record.Key.Kind
            |> List.map (fun (kind, candidates) ->
                KindRun.Ran(auditKind holds storedOf (AuditKind.ofStoredName kind) candidates None)),
            missing |> List.map _.Text

    let results =
        runs
        |> List.choose (function
            | KindRun.Ran(tally, mismatches, keys) -> Some(tally, mismatches, keys)
            | KindRun.NotRun _ -> None)

    { Seed = options.Seed
      Sample = if options.Keys.IsEmpty then options.Sample else None
      AuditedStore = auditedRoot
      CleanStore = cleanRoot
      Kinds = results |> List.map (fun (tally, _, _) -> tally)
      NotAudited =
        runs
        |> List.choose (function
            | KindRun.NotRun notAudited -> Some notAudited
            | KindRun.Ran _ -> None)
      Mismatches = results |> List.collect (fun (_, mismatches, _) -> mismatches)
      AuditedKeys = results |> List.map (fun (_, _, keys) -> keys)
      Unmatched = unmatched
      UnreadableClean = unreadableClean
      UnreadableAudited = unreadableAudited
      Agreement = LookupAgreement.read options.ShadowLogs
      Quarantine = OutcomeStore.readQuarantine auditedRoot }

/// Audit the records `auditedRoot` holds against the clean recompute filed in `cleanRoot`,
/// outcome stores only: test evidence, if asked for, is not audited.
let audit (auditedRoot: string) (cleanRoot: string) (options: Options) : Report =
    let outcomeStoresOnly =
        EvidenceStore.Unnamed "this audit was given outcome stores only"

    auditStores
        { Audited = auditedRoot
          Clean = cleanRoot
          AuditedEvidence = outcomeStoresOnly
          CleanEvidence = outcomeStoresOnly }
        options

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
    o["recordsVerdict"] <- verdictToken report.RecordsVerdict
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

        match KindTally.finding k with
        | KindFinding.Compared -> e["vacuous"] <- false
        | KindFinding.Vacuous why ->
            e["vacuous"] <- true
            e["vacuousReason"] <- why

        kinds.Add e

    o["kinds"] <- kinds

    // The kinds that compared nothing, by name: a finding a reader of `kinds` cannot miss.
    o["vacuousKinds"] <-
        JsonArray(
            report.Kinds
            |> List.filter (fun k -> KindTally.finding k <> KindFinding.Compared)
            |> List.map (fun k -> JsonValue.Create k.Kind :> JsonNode)
            |> Array.ofList
        )

    // The kinds that had no store to read (AUTOMATION-1767): not vacuous (they never looked), so
    // neither in `kinds` (they have no counts) nor in `vacuousKinds`. The verdict is vacuous (exit 3).
    o["notAuditedKinds"] <-
        JsonArray(
            report.NotAudited
            |> List.map (fun n ->
                let e = JsonObject()
                e["kind"] <- n.Name
                e["reason"] <- n.Why
                e :> JsonNode)
            |> Array.ofList
        )

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

    match report.Agreement with
    | LookupAgreement.NotAsked -> ()
    | LookupAgreement.Tallied t ->
        let a = JsonObject()
        a["logs"] <- JsonArray(t.Logs |> List.map (fun l -> JsonValue.Create l :> JsonNode) |> Array.ofList)
        a["publications"] <- t.Publications
        a["daemonGreen"] <- t.DaemonGreen
        a["lookupGreen"] <- t.LookupGreen
        a["agreementPercent"] <- AgreementTally.rate t
        a["lookupGreenOnRed"] <- t.LookupGreenOnRed
        a["unreadable"] <- t.Unreadable

        a["alarm"] <-
            match AgreementTally.finding t with
            | AgreementFinding.Agreeing -> null
            | AgreementFinding.NoDaemonGreen -> JsonValue.Create "no-daemon-green"
            | AgreementFinding.NoAgreement -> JsonValue.Create "no-agreement"
            | AgreementFinding.GreenOnRed _ -> JsonValue.Create "lookup-green-on-red"

        o["lookupAgreement"] <- a

    let quarantined = JsonArray()

    for note in report.Quarantine.Noted do
        let e = JsonObject()
        e["key"] <- note.Key
        e["file"] <- note.File

        match note.Mover with
        | OutcomeStore.Mover.Read -> e["by"] <- "read"
        | OutcomeStore.Mover.Writer kind ->
            e["by"] <- "writer"
            e["kind"] <- kind

        e["why"] <- note.Why
        e["at"] <- note.At.ToString("o")
        quarantined.Add e

    o["quarantined"] <- quarantined
    o["quarantinedUnnoted"] <- report.Quarantine.Unnoted
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
          match KindTally.finding k with
          | KindFinding.Compared ->
              $"  %-20s{k.Kind} clean keys %d{k.CleanKeys}, shared %d{k.Shared}, audited %d{k.Audited} (%d{k.Records} record(s)), mismatched %d{k.Mismatched}"
          | KindFinding.Vacuous why ->
              $"  %-20s{k.Kind} VACUOUS: clean keys %d{k.CleanKeys}, shared %d{k.Shared}, nothing compared (%s{why})"

      for n in report.NotAudited do
          $"  %-20s{n.Name} NOT AUDITED: %s{n.Why}"

      for m in report.Mismatches do
          let answer = if m.IsAnswer then "answer" else "history"
          let fields = String.concat "," m.Fields
          let storedAt = m.Stored.RecordedAt.ToString("o")

          $"  MISMATCH %s{m.Kind} %s{m.Subject} %s{m.Key} (%s{answer}): %s{fields}; stored by %s{m.Stored.Worktree} at %s{storedAt}"

      for key in report.Unmatched do
          $"  not in both stores: %s{key}"

      if report.UnreadableClean + report.UnreadableAudited > 0 then
          $"  unreadable files: clean %d{report.UnreadableClean}, audited %d{report.UnreadableAudited}"

      // AUTOMATION-1701 (ADR-072): quarantined files stay visible, by kind, key and when.
      if not report.Quarantine.Noted.IsEmpty || report.Quarantine.Unnoted > 0 then
          $"  quarantined files in the audited store: %d{report.Quarantine.Noted.Length + report.Quarantine.Unnoted} (%d{report.Quarantine.Unnoted} without a note)"

      for note in report.Quarantine.Noted do
          let by =
              match note.Mover with
              | OutcomeStore.Mover.Read -> "read"
              | OutcomeStore.Mover.Writer kind -> $"writer of %s{kind}"

          let at = note.At.ToString("o")
          $"  QUARANTINED %s{note.Key} %s{note.File} by %s{by} at %s{at}: %s{note.Why}"

      match report.Agreement with
      | LookupAgreement.NotAsked -> ()
      | LookupAgreement.Tallied t ->
          let rate =
              (AgreementTally.rate t).ToString("0.0", Globalization.CultureInfo.InvariantCulture)

          $"  lookup agreement: %d{t.LookupGreen} of %d{t.DaemonGreen} daemon-green publication(s) answered green by the lookup (%s{rate}%%), lookup green on daemon red %d{t.LookupGreenOnRed}, %d{t.Publications} publication(s) in %d{t.Logs.Length} log(s)"

      match agreementAlarm report.Agreement with
      | Some(_, alarm) -> "  " + alarm
      | None -> ()

      // The records' own verdict, ADR-074's night count (a), whatever lookup agreement says.
      let records =
          match report.RecordsVerdict with
          | Verdict.Agree -> "AGREE: every audited record equals its clean recompute"
          | Verdict.Mismatch ->
              // A kind that compared nothing is a finding of its own (AUTOMATION-1841): a mismatch
              // elsewhere outranks it in the verdict, never hides it.
              let alsoVacuous =
                  [ for k in report.Kinds do
                        match KindTally.finding k with
                        | KindFinding.Vacuous why -> $"%s{k.Kind} (%s{why})"
                        | KindFinding.Compared -> () ]
                  |> function
                      | [] -> ""
                      | kinds -> "; also VACUOUS: " + String.concat ", " kinds

              $"MISMATCH: %d{report.Mismatches.Length} stored record(s) differ from a clean recompute under the same key: a key missed an input%s{alsoVacuous}"
          | Verdict.Vacuous -> "VACUOUS: a kind or a named key compared nothing, or a kind was not audited; no answer"

      $"records: %s{records}"

      // The overall line names what decided `Verdict`: a record mismatch, else the agreement alarm,
      // else the records' verdict.
      let verdict =
          match report.RecordsVerdict, agreementAlarm report.Agreement with
          | Verdict.Mismatch, _
          | _, None -> records
          | _, Some(_, alarm) -> alarm

      $"audit: %s{verdict}" ]

/// The store directory of `repoRoot`'s repository under the cache home `cacheHome`: where a
/// recompute run with `FSHW_CACHE_HOME=<cacheHome>` files its records.
let storeUnderCacheHome (cacheHome: string) (repoRoot: string) : string =
    CacheHomeStore.path
        { Home = cacheHome
          Area = StoreArea.Outcomes
          Namespace = RepoIdentity.namespaceOf repoRoot }

/// The test-evidence store of `repoRoot`'s repository under the cache home `cacheHome`.
let evidenceUnderCacheHome (cacheHome: string) (repoRoot: string) : string =
    CacheHomeStore.path
        { Home = cacheHome
          Area = StoreArea.Evidence
          Namespace = RepoIdentity.namespaceOf repoRoot }

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
        /// The audited test-evidence store (`--evidence`); None for the one beside the audited
        /// outcome store.
        Evidence: string option
        /// `--kind` values as given: parsed by `resolve` (`AuditKind.parse`).
        Kinds: string list
        /// Keys per kind; None for the default, 0 for every shared key.
        Sample: int option
        Seed: string option
        Keys: string list
        PreferInput: string option
        /// Shadow logs to read lookup agreement from (`--shadow-log`, repeatable).
        ShadowLogs: string list
    }

module Request =
    let empty =
        { Clean = None
          CleanHome = None
          Store = None
          Evidence = None
          Kinds = []
          Sample = None
          Seed = None
          Keys = []
          PreferInput = None
          ShadowLogs = [] }

/// An audit resolved against a repository: the two stores and what to compare.
type Setup =
    {
        AuditedStore: string
        CleanStore: string
        /// The test-evidence stores beside them (AUTOMATION-1767): each named by its cache home,
        /// by `--evidence`, or as the box-wide one; otherwise why that side names none.
        AuditedEvidence: EvidenceStore
        CleanEvidence: EvidenceStore
        Options: Options
    }

/// The evidence store beside the outcome store `directory`, named by `flag`; when `directory` is
/// not a cache home's outcome store, why not and what to pass instead.
let private evidenceBeside (flag: string) (instead: string) (directory: string) : EvidenceStore =
    match CacheHomeStore.evidenceBesideOutcomes directory with
    | Some evidence -> EvidenceStore.At evidence
    | None ->
        EvidenceStore.Unnamed
            $"%s{flag} %s{directory} is not a cache home's outcome store (<home>/cache/outcomes/<namespace>/v1), so it names no evidence store; pass %s{instead}"

/// Resolve `request` against `repoRoot`; Error names what is wrong with it.
let resolve (repoRoot: string) (request: Request) : Result<Setup, string> =
    let clean =
        match request.Clean, request.CleanHome with
        | Some store, None ->
            let store = Path.GetFullPath store
            Ok(store, evidenceBeside "--clean" "--clean-home <its cache home>" store)
        | None, Some home ->
            let home = Path.GetFullPath home
            Ok(storeUnderCacheHome home repoRoot, EvidenceStore.At(evidenceUnderCacheHome home repoRoot))
        | Some _, Some _ -> Error "pass --clean or --clean-home, not both"
        | None, None ->
            Error
                "name the clean recompute's store: --clean-home <the FSHW_CACHE_HOME a from-scratch check ran under>, or --clean <store dir>"

    let audited =
        match request.Store, request.Evidence with
        | store, Some evidence ->
            store
            |> Option.map Path.GetFullPath
            |> Option.defaultWith (fun () -> OutcomeStore.directoryFor repoRoot),
            EvidenceStore.At(Path.GetFullPath evidence)
        | Some store, None ->
            let store = Path.GetFullPath store
            store, evidenceBeside "--store" "--evidence <its evidence store>" store
        | None, None -> OutcomeStore.directoryFor repoRoot, EvidenceStore.At(TestEvidenceStore.directoryFor repoRoot)

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

    let kinds =
        match request.Kinds with
        | [] -> Ok AuditKind.defaults
        | given -> AuditKind.parse given

    match clean, sample, badKey, kinds with
    | Error e, _, _, _
    | _, Error e, _, _
    | _, _, Some e, _
    | _, _, _, Error e -> Error e
    | Ok(cleanStore, cleanEvidence), Ok sample, None, Ok kinds ->
        let auditedStore, auditedEvidence = audited

        Ok
            { AuditedStore = auditedStore
              CleanStore = cleanStore
              AuditedEvidence = auditedEvidence
              CleanEvidence = cleanEvidence
              Options =
                { Kinds = kinds
                  Sample = sample
                  Seed = request.Seed |> Option.defaultValue Options.defaults.Seed
                  Keys = keys |> List.choose Result.toOption
                  PreferInput = request.PreferInput
                  ShadowLogs = request.ShadowLogs |> List.map Path.GetFullPath } }

/// Run a resolved audit: refuse a store audited against itself or a missing clean store,
/// then compare. Error is a usage error (exit 2).
let execute (setup: Setup) : Result<Report, string> =
    validateStores setup.AuditedStore setup.CleanStore
    |> Result.map (fun () ->
        auditStores
            { Audited = setup.AuditedStore
              Clean = setup.CleanStore
              AuditedEvidence = setup.AuditedEvidence
              CleanEvidence = setup.CleanEvidence }
            setup.Options)

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
