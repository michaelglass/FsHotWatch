/// AUTOMATION-1816 step 2b: the nightly's output-level shadow of the references key.
///
/// A fresh full-suite `confirm` is the cold oracle for its own tree. For every file its scan
/// found the references key would have served (step 2's shadow), this compares what reuse
/// would have handed each plugin with what the same nightly produced: analyzer and lint
/// findings file by file, and for test-prune the tests outside the selection the stale
/// analyses would have made (S′) that failed or changed outcome. Both sides describe one tree.
///
/// RECORD AND COMPARE ONLY: nothing here decides what any gate runs, selects or serves, and
/// nothing judges the go criterion; the report carries the counts it is judged from.
///
/// Files: `.fshw/references-shadow/<runId>.json` holds the detail (the last
/// `RetainedReports`), and `.fshw/references-shadow.jsonl` one summary line per report.
module FsHotWatch.ReferencesShadowReport

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.ErrorLedger

// --- servable files ----------------------------------------------------------------------

/// A check's used-signatures hash as the plugins key on it: `CacheInputs.usedSignaturesHash`'s
/// value, or none when the check's shape could not be described (and no plugin keyed it).
[<RequireQualifiedAccess>]
type UsedSignaturesKey =
    | Described of hash: string
    | Undescribed

/// A file the references key would have served (step 2b): what reuse would key each plugin's
/// result by is the stored check's FCS and used signatures, read from its record.
type ServableFile =
    {
        /// Repository-relative, `/`-separated.
        File: string
        Project: string
        FcsSignature: string
        UsedSignatures: UsedSignaturesKey
    }

module ServableFile =
    /// The servable `file` of `project` whose stored check had `verdict`.
    let internal ofVerdict (file: string) (project: string) (verdict: AlternativeKeys.CheckVerdict) : ServableFile =
        { File = file
          Project = project
          FcsSignature = verdict.FcsSignature
          UsedSignatures =
            if verdict.UsedSignatures.StartsWith("some:", StringComparison.Ordinal) then
                UsedSignaturesKey.Described(verdict.UsedSignatures.Substring 5)
            else
                UsedSignaturesKey.Undescribed }


// --- analyzers and lint ------------------------------------------------------------------

/// One finding as the comparison sees it: rule, position, severity and message. The detail
/// (full output) is not compared.
type Finding =
    { Code: string option
      Line: int
      Column: int
      Severity: string
      Message: string }

module Finding =
    let ofEntry (e: ErrorEntry) : Finding =
        { Code = e.Code
          Line = e.Line
          Column = e.Column
          Severity = DiagnosticSeverity.toString e.Severity
          Message = e.Message }

    /// The findings normalised for comparison: sorted, so order never makes a mismatch.
    let normalise (findings: Finding list) : Finding list = List.sort findings

/// Why reuse's answer for a file cannot be told.
[<RequireQualifiedAccess>]
type UnverifiableReason =
    /// The plugin's store holds nothing under the reused key (evicted, or never filed).
    | NoReusedEntry
    /// The plugin's configuration moved since the reused entry was filed.
    | ConfigChanged
    /// Reuse would have read no key for the file: its record names no used signatures, or this
    /// run's plugin keyed no result for it (a file outside its scope).
    | NoReusedKey
    /// The entry this run's write displaced was filed under another key than the reused one, so
    /// it is not what reuse would have read (lint: its task cache keeps one entry per file).
    | DisplacedKeyDiffers

module UnverifiableReason =
    let token =
        function
        | UnverifiableReason.NoReusedEntry -> "no-reused-entry"
        | UnverifiableReason.ConfigChanged -> "config-changed"
        | UnverifiableReason.NoReusedKey -> "no-reused-key"
        | UnverifiableReason.DisplacedKeyDiffers -> "displaced-key-differs"

    let tryParse =
        function
        | "no-reused-entry" -> Some UnverifiableReason.NoReusedEntry
        | "config-changed" -> Some UnverifiableReason.ConfigChanged
        | "no-reused-key" -> Some UnverifiableReason.NoReusedKey
        | "displaced-key-differs" -> Some UnverifiableReason.DisplacedKeyDiffers
        | _ -> None

/// What reuse would have handed a plugin for one file.
[<RequireQualifiedAccess>]
type Reused =
    | Found of Finding list
    | Unverifiable of UnverifiableReason
    /// This run keyed the file's result by the very key reuse would have read. Neither `confirm
    /// --fresh` nor anything else bypasses a plugin's result cache, so this run's result may BE
    /// the cached entry: comparing the two would compare the cache with itself.
    | SameKey

/// One shadow-servable file, for one plugin.
[<RequireQualifiedAccess>]
type FileOutcome =
    | Equal
    /// Both lists, normalised: what reuse would have served, then what the nightly found.
    | Mismatch of reused: Finding list * fresh: Finding list
    | Unverifiable of UnverifiableReason
    /// This run keyed the file by the reused key too (`Reused.SameKey`): reported apart, and
    /// counted as neither equal nor unverifiable.
    | SameKey

/// Compare what reuse would have served with the nightly's own findings for the file.
let compareFile (reused: Reused) (fresh: ErrorEntry list) : FileOutcome =
    match reused with
    | Reused.Unverifiable reason -> FileOutcome.Unverifiable reason
    | Reused.SameKey -> FileOutcome.SameKey
    | Reused.Found reusedFindings ->
        let reusedNormal = Finding.normalise reusedFindings
        let freshNormal = fresh |> List.map Finding.ofEntry |> Finding.normalise

        if reusedNormal = freshNormal then
            FileOutcome.Equal
        else
            FileOutcome.Mismatch(reusedNormal, freshNormal)

type Counts =
    {
        Equal: int
        Mismatch: int
        Unverifiable: int
        /// Files this run keyed by the reused key (`FileOutcome.SameKey`).
        SameKey: int
    }

/// A mismatch, named for whoever must fix it.
type Mismatch =
    { Plugin: string
      File: string
      Reused: Finding list
      Fresh: Finding list }

/// Every shadow-servable file's outcome for one plugin (`analyzers` or `lint`), by
/// repository-relative path.
type PluginComparison =
    { Plugin: string
      Files: (string * FileOutcome) list }

module PluginComparison =
    let ofFiles (plugin: string) (files: (string * FileOutcome) list) : PluginComparison =
        { Plugin = plugin
          Files = files |> List.sortBy fst }

    let counts (comparison: PluginComparison) : Counts =
        comparison.Files
        |> List.fold
            (fun c (_, outcome) ->
                match outcome with
                | FileOutcome.Equal -> { c with Equal = c.Equal + 1 }
                | FileOutcome.Mismatch _ -> { c with Mismatch = c.Mismatch + 1 }
                | FileOutcome.Unverifiable _ ->
                    { c with
                        Unverifiable = c.Unverifiable + 1 }
                | FileOutcome.SameKey -> { c with SameKey = c.SameKey + 1 })
            { Equal = 0
              Mismatch = 0
              Unverifiable = 0
              SameKey = 0 }

    let mismatches (comparison: PluginComparison) : Mismatch list =
        comparison.Files
        |> List.choose (fun (file, outcome) ->
            match outcome with
            | FileOutcome.Mismatch(reused, fresh) ->
                Some
                    { Plugin = comparison.Plugin
                      File = file
                      Reused = reused
                      Fresh = fresh }
            | FileOutcome.Equal
            | FileOutcome.Unverifiable _
            | FileOutcome.SameKey -> None)

    /// The files this run keyed by the reused key, by path.
    let sameKeyFiles (comparison: PluginComparison) : string list =
        comparison.Files
        |> List.choose (fun (file, outcome) ->
            match outcome with
            | FileOutcome.SameKey -> Some file
            | FileOutcome.Equal
            | FileOutcome.Mismatch _
            | FileOutcome.Unverifiable _ -> None)

// --- test-prune --------------------------------------------------------------------------

/// A test's outcome in one run's CTRF reports.
[<RequireQualifiedAccess>]
type TestOutcome =
    | Passed
    | Failed
    | Skipped
    /// The run did not report the test.
    | Absent

module TestOutcome =
    let token =
        function
        | TestOutcome.Passed -> "passed"
        | TestOutcome.Failed -> "failed"
        | TestOutcome.Skipped -> "skipped"
        | TestOutcome.Absent -> "absent"

    let tryParse =
        function
        | "passed" -> Some TestOutcome.Passed
        | "failed" -> Some TestOutcome.Failed
        | "skipped" -> Some TestOutcome.Skipped
        | "absent" -> Some TestOutcome.Absent
        | _ -> None

/// One test the nightly ran, against the watermark run.
type TestRow =
    {
        Test: string
        /// Its outcome in the watermark run's CTRF reports.
        Watermark: TestOutcome
        /// Its outcome in the nightly.
        Nightly: TestOutcome
        /// Listed in `outstanding-failures`.
        Quarantined: bool
        /// A non-zero flakiness score over its last 20 runs, or it timed out or was cancelled.
        Unstable: bool
    }

/// How a test counts.
[<RequireQualifiedAccess>]
type Counted =
    /// It failed in the nightly, or its outcome changed since the watermark run.
    | FailedOrChanged
    /// It would count, but it is unstable: listed, never counted.
    | FlakyChanged

/// How `row` counts; None when it neither failed nor changed (a quarantined test that stays
/// red is not a change).
let classify (row: TestRow) : Counted option =
    let stillQuarantinedRed =
        row.Quarantined
        && row.Watermark = TestOutcome.Failed
        && row.Nightly = TestOutcome.Failed

    let failedOrChanged =
        row.Nightly = TestOutcome.Failed || row.Watermark <> row.Nightly

    if stillQuarantinedRed || not failedOrChanged then None
    elif row.Unstable then Some Counted.FlakyChanged
    else Some Counted.FailedOrChanged

type TestFinding =
    { Test: string
      Watermark: TestOutcome
      Nightly: TestOutcome }

/// The temporary impact-DB copy S′ was computed on.
[<RequireQualifiedAccess>]
type ImpactDbCopy =
    /// No servable file's reused analysis differed: S′ is S, and nothing was copied.
    | NotCopied
    /// Its size, and how long the copy took.
    | Copied of bytes: int64 * milliseconds: int64

/// The two selections for C, the symbols changed since the watermark: S over this run's index,
/// S′ over a copy with the reused analyses of the servable files substituted.
type Selections =
    {
        S: Set<string>
        SPrime: Set<string>
        /// Shadow-servable files whose reused analysis differs from the nightly's.
        DifferingAnalyses: int
        /// Shadow-servable files whose reused analysis cannot be read (no record under its
        /// identity, or a record naming no used signatures).
        UnverifiableAnalyses: int
        Copy: ImpactDbCopy
    }

type TestPruneComparison =
    {
        /// Shadow-servable files whose reused analysis differs from the nightly's.
        DifferingAnalyses: int
        /// Shadow-servable files whose reused analysis cannot be read.
        UnverifiableAnalyses: int
        /// Tests outside S′ that failed or changed outcome, flaky ones excluded.
        OutsideSPrimeFailedOrChanged: int
        /// Of those: in S, the fresh selection, but not in S′.
        Attributable: TestFinding list
        /// Of those: not in S either.
        SelectorMiss: TestFinding list
        /// Outside S′, failed or changed, and unstable: listed, not counted.
        FlakyChanged: TestFinding list
        /// The temporary impact-DB copy S′ was computed on.
        ImpactDbCopy: ImpactDbCopy
    }

/// Count the tests outside S′ that failed or changed, split by S.
let compareTests (selections: Selections) (rows: TestRow list) =
    let s = selections.S
    let sPrime = selections.SPrime

    let finding (row: TestRow) : TestFinding =
        { Test = row.Test
          Watermark = row.Watermark
          Nightly = row.Nightly }

    let outside =
        rows
        |> List.filter (fun row -> not (Set.contains row.Test sPrime))
        |> List.choose (fun row -> classify row |> Option.map (fun counted -> counted, row))

    let counted =
        outside
        |> List.choose (fun (c, row) ->
            match c with
            | Counted.FailedOrChanged -> Some row
            | Counted.FlakyChanged -> None)

    let byTest (findings: TestFinding list) = findings |> List.sortBy _.Test

    { DifferingAnalyses = selections.DifferingAnalyses
      UnverifiableAnalyses = selections.UnverifiableAnalyses
      OutsideSPrimeFailedOrChanged = counted.Length
      Attributable =
        counted
        |> List.filter (fun row -> Set.contains row.Test s)
        |> List.map finding
        |> byTest
      SelectorMiss =
        counted
        |> List.filter (fun row -> not (Set.contains row.Test s))
        |> List.map finding
        |> byTest
      FlakyChanged =
        outside
        |> List.choose (fun (c, row) ->
            match c with
            | Counted.FlakyChanged -> Some(finding row)
            | Counted.FailedOrChanged -> None)
        |> byTest
      ImpactDbCopy = selections.Copy }

[<RequireQualifiedAccess>]
type TestPruneSection =
    | Compared of TestPruneComparison
    /// Why S and S′ could not be computed (no watermark run, no test-prune plugin).
    | NotCompared of reason: string

// --- the report --------------------------------------------------------------------------

type Comparison =
    {
        /// Files the nightly's scan found the references key would have served.
        Servable: int
        Analyzers: PluginComparison
        Lint: PluginComparison
        TestPrune: TestPruneSection
    }

type ReportResult =
    /// The comparison, on the land host.
    | Compared of Comparison
    /// Run on another host than the land host, whose records it cannot see: said so, with no
    /// per-file counts, never every file as unverifiable.
    | HostMismatch of host: string * landHost: string

type Report =
    { RunId: string
      WrittenAt: DateTimeOffset
      Result: ReportResult }

/// A host name as compared: lower case, without its domain.
let private hostName (name: string) =
    name.Trim().ToLowerInvariant().Split('.').[0]

/// `compare ()` on the land host; `HostMismatch` anywhere else (and `compare` is not run).
let onHost (host: string) (landHost: string) (compare: unit -> ReportResult) : ReportResult =
    if hostName host = hostName landHost then
        compare ()
    else
        HostMismatch(host, landHost)

// --- the summary line --------------------------------------------------------------------

type TestPruneCounts =
    { DifferingAnalyses: int
      UnverifiableAnalyses: int
      OutsideSPrimeFailedOrChanged: int
      Attributable: int
      SelectorMiss: int
      FlakyChanged: int
      ImpactDbCopy: ImpactDbCopy }

[<RequireQualifiedAccess>]
type TestPruneSummary =
    | Compared of TestPruneCounts
    | NotCompared of reason: string

type SummaryCounts =
    { Servable: int
      Analyzers: Counts
      Lint: Counts
      TestPrune: TestPruneSummary }

[<RequireQualifiedAccess>]
type SummaryResult =
    | Compared of SummaryCounts
    | HostMismatch of host: string * landHost: string

type Summary =
    { RunId: string
      WrittenAt: DateTimeOffset
      Result: SummaryResult }

// --- JSON --------------------------------------------------------------------------------

[<Literal>]
let ReportSchema = "fshw-references-shadow-v1"

[<Literal>]
let SummarySchema = "fshw-references-shadow-summary-v1"

module private Write =
    let finding (f: Finding) : JsonNode =
        let o = JsonObject()

        match f.Code with
        | Some code -> o["code"] <- code
        | None -> ()

        o["line"] <- f.Line
        o["column"] <- f.Column
        o["severity"] <- f.Severity
        o["message"] <- f.Message
        o

    let array (items: JsonNode list) : JsonNode =
        let a = JsonArray()

        for item in items do
            a.Add item

        a

    let counts (c: Counts) : JsonNode =
        let o = JsonObject()
        o["equal"] <- c.Equal
        o["mismatch"] <- c.Mismatch
        o["unverifiable"] <- c.Unverifiable
        o["sameKey"] <- c.SameKey
        o

    let fileOutcome (file: string, outcome: FileOutcome) : JsonNode =
        let o = JsonObject()
        o["file"] <- file

        match outcome with
        | FileOutcome.Equal -> o["outcome"] <- "equal"
        | FileOutcome.Mismatch(reused, fresh) ->
            o["outcome"] <- "mismatch"
            o["reused"] <- array (reused |> List.map finding)
            o["fresh"] <- array (fresh |> List.map finding)
        | FileOutcome.Unverifiable reason ->
            o["outcome"] <- "unverifiable"
            o["reason"] <- UnverifiableReason.token reason
        | FileOutcome.SameKey -> o["outcome"] <- "sameKey"

        o

    let plugin (p: PluginComparison) : JsonNode =
        let o = JsonObject()
        o["plugin"] <- p.Plugin
        o["counts"] <- counts (PluginComparison.counts p)
        o["files"] <- array (p.Files |> List.map fileOutcome)
        o

    let testFinding (t: TestFinding) : JsonNode =
        let o = JsonObject()
        o["test"] <- t.Test
        o["watermark"] <- TestOutcome.token t.Watermark
        o["nightly"] <- TestOutcome.token t.Nightly
        o

    let impactDbCopy (o: JsonObject) (copy: ImpactDbCopy) =
        match copy with
        | ImpactDbCopy.NotCopied -> ()
        | ImpactDbCopy.Copied(bytes, milliseconds) ->
            o["impactDbCopyBytes"] <- bytes
            o["impactDbCopyMs"] <- milliseconds

    let testPrune (section: TestPruneSection) : JsonNode =
        let o = JsonObject()

        match section with
        | TestPruneSection.NotCompared reason ->
            o["result"] <- "notCompared"
            o["reason"] <- reason
        | TestPruneSection.Compared tp ->
            o["result"] <- "compared"
            o["differingAnalyses"] <- tp.DifferingAnalyses
            o["unverifiableAnalyses"] <- tp.UnverifiableAnalyses
            o["outsideSPrimeFailedOrChanged"] <- tp.OutsideSPrimeFailedOrChanged
            o["attributable"] <- array (tp.Attributable |> List.map testFinding)
            o["selectorMiss"] <- array (tp.SelectorMiss |> List.map testFinding)
            o["flakyChanged"] <- array (tp.FlakyChanged |> List.map testFinding)
            impactDbCopy o tp.ImpactDbCopy

        o

    let header (schema: string) (runId: string) (writtenAt: DateTimeOffset) =
        let o = JsonObject()
        o["schema"] <- schema
        o["runId"] <- runId
        o["writtenAt"] <- writtenAt.ToString("O")
        o

    let hostMismatch (o: JsonObject) (host: string) (landHost: string) =
        o["result"] <- "hostMismatch"
        o["host"] <- host
        o["landHost"] <- landHost

/// Thrown by the readers below on a shape they do not accept; caught by `tryOfJson`.
exception private MalformedException of string

module private Read =
    let prop (e: JsonElement) (name: string) : JsonElement =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> v
            | false, _ -> raise (MalformedException $"no %s{name}")
        | _ -> raise (MalformedException $"%s{name}: not an object")

    let str (e: JsonElement) (name: string) : string =
        let v = prop e name

        match v.ValueKind with
        | JsonValueKind.String -> v.ToString()
        | _ -> raise (MalformedException $"%s{name}: not a string")

    let int (e: JsonElement) (name: string) = (prop e name).GetInt32()
    let int64 (e: JsonElement) (name: string) = (prop e name).GetInt64()

    let items (e: JsonElement) (name: string) =
        (prop e name).EnumerateArray() |> List.ofSeq

    let parsed (what: string) (value: 'a option) : 'a =
        match value with
        | Some v -> v
        | None -> raise (MalformedException $"unknown %s{what}")

    let finding (e: JsonElement) : Finding =
        { Code =
            match e.TryGetProperty "code" with
            | true, v -> Some(v.ToString())
            | false, _ -> None
          Line = int e "line"
          Column = int e "column"
          Severity = str e "severity"
          Message = str e "message" }

    let counts (e: JsonElement) : Counts =
        { Equal = int e "equal"
          Mismatch = int e "mismatch"
          Unverifiable = int e "unverifiable"
          SameKey = int e "sameKey" }

    let fileOutcome (e: JsonElement) : string * FileOutcome =
        let outcome =
            match str e "outcome" with
            | "equal" -> FileOutcome.Equal
            | "mismatch" ->
                FileOutcome.Mismatch(items e "reused" |> List.map finding, items e "fresh" |> List.map finding)
            | "unverifiable" ->
                FileOutcome.Unverifiable(UnverifiableReason.tryParse (str e "reason") |> parsed "reason")
            | "sameKey" -> FileOutcome.SameKey
            | other -> raise (MalformedException $"outcome %s{other}")

        str e "file", outcome

    let plugin (e: JsonElement) : PluginComparison =
        { Plugin = str e "plugin"
          Files = items e "files" |> List.map fileOutcome }

    let impactDbCopy (e: JsonElement) : ImpactDbCopy =
        match e.TryGetProperty "impactDbCopyBytes" with
        | true, _ -> ImpactDbCopy.Copied(int64 e "impactDbCopyBytes", int64 e "impactDbCopyMs")
        | false, _ -> ImpactDbCopy.NotCopied

    let testFinding (e: JsonElement) : TestFinding =
        { Test = str e "test"
          Watermark = TestOutcome.tryParse (str e "watermark") |> parsed "outcome"
          Nightly = TestOutcome.tryParse (str e "nightly") |> parsed "outcome" }

    let testPrune (e: JsonElement) : TestPruneSection =
        match str e "result" with
        | "notCompared" -> TestPruneSection.NotCompared(str e "reason")
        | "compared" ->
            TestPruneSection.Compared
                { DifferingAnalyses = int e "differingAnalyses"
                  UnverifiableAnalyses = int e "unverifiableAnalyses"
                  OutsideSPrimeFailedOrChanged = int e "outsideSPrimeFailedOrChanged"
                  Attributable = items e "attributable" |> List.map testFinding
                  SelectorMiss = items e "selectorMiss" |> List.map testFinding
                  FlakyChanged = items e "flakyChanged" |> List.map testFinding
                  ImpactDbCopy = impactDbCopy e }
        | other -> raise (MalformedException $"test-prune result %s{other}")

    /// `read root` for a document of `schema`; None for anything else.
    // MGA-ERROR-REPORT-001:ok — a line or file that does not parse is a normal input (an older
    // or damaged file) and is read as nothing; it decides nothing.
    let document (schema: string) (text: string) (read: JsonElement -> 'a) : 'a option =
        try
            using (JsonDocument.Parse text) (fun doc ->
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then None
                elif str root "schema" <> schema then None
                else Some(read root))
        with
        | MalformedException _
        | :? JsonException
        | :? InvalidOperationException
        | :? FormatException -> None

    let writtenAt (root: JsonElement) =
        DateTimeOffset.Parse(str root "writtenAt", Globalization.CultureInfo.InvariantCulture)

module Report =
    let toJson (report: Report) : string =
        let o = Write.header ReportSchema report.RunId report.WrittenAt

        match report.Result with
        | HostMismatch(host, landHost) -> Write.hostMismatch o host landHost
        | Compared c ->
            o["result"] <- "compared"
            o["servable"] <- c.Servable
            o["analyzers"] <- Write.plugin c.Analyzers
            o["lint"] <- Write.plugin c.Lint
            o["testPrune"] <- Write.testPrune c.TestPrune

        o.ToJsonString()

    let tryOfJson (text: string) : Report option =
        Read.document ReportSchema text (fun root ->
            let result =
                match Read.str root "result" with
                | "hostMismatch" -> HostMismatch(Read.str root "host", Read.str root "landHost")
                | "compared" ->
                    Compared
                        { Servable = Read.int root "servable"
                          Analyzers = Read.plugin (Read.prop root "analyzers")
                          Lint = Read.plugin (Read.prop root "lint")
                          TestPrune = Read.testPrune (Read.prop root "testPrune") }
                | other -> raise (MalformedException $"result %s{other}")

            { RunId = Read.str root "runId"
              WrittenAt = Read.writtenAt root
              Result = result })

module Summary =
    let ofReport (report: Report) : Summary =
        let result =
            match report.Result with
            | HostMismatch(host, landHost) -> SummaryResult.HostMismatch(host, landHost)
            | Compared c ->
                SummaryResult.Compared
                    { Servable = c.Servable
                      Analyzers = PluginComparison.counts c.Analyzers
                      Lint = PluginComparison.counts c.Lint
                      TestPrune =
                        match c.TestPrune with
                        | TestPruneSection.NotCompared reason -> TestPruneSummary.NotCompared reason
                        | TestPruneSection.Compared tp ->
                            TestPruneSummary.Compared
                                { DifferingAnalyses = tp.DifferingAnalyses
                                  UnverifiableAnalyses = tp.UnverifiableAnalyses
                                  OutsideSPrimeFailedOrChanged = tp.OutsideSPrimeFailedOrChanged
                                  Attributable = tp.Attributable.Length
                                  SelectorMiss = tp.SelectorMiss.Length
                                  FlakyChanged = tp.FlakyChanged.Length
                                  ImpactDbCopy = tp.ImpactDbCopy } }

        { RunId = report.RunId
          WrittenAt = report.WrittenAt
          Result = result }

    let toJson (summary: Summary) : string =
        let o = Write.header SummarySchema summary.RunId summary.WrittenAt

        match summary.Result with
        | SummaryResult.HostMismatch(host, landHost) -> Write.hostMismatch o host landHost
        | SummaryResult.Compared c ->
            o["result"] <- "compared"
            o["servable"] <- c.Servable
            o["analyzers"] <- Write.counts c.Analyzers
            o["lint"] <- Write.counts c.Lint
            let tp = JsonObject()

            match c.TestPrune with
            | TestPruneSummary.NotCompared reason ->
                tp["result"] <- "notCompared"
                tp["reason"] <- reason
            | TestPruneSummary.Compared t ->
                tp["result"] <- "compared"
                tp["differingAnalyses"] <- t.DifferingAnalyses
                tp["unverifiableAnalyses"] <- t.UnverifiableAnalyses
                tp["outsideSPrimeFailedOrChanged"] <- t.OutsideSPrimeFailedOrChanged
                tp["attributable"] <- t.Attributable
                tp["selectorMiss"] <- t.SelectorMiss
                tp["flakyChanged"] <- t.FlakyChanged
                Write.impactDbCopy tp t.ImpactDbCopy

            o["testPrune"] <- tp

        o.ToJsonString()

    let tryOfJson (text: string) : Summary option =
        Read.document SummarySchema text (fun root ->
            let result =
                match Read.str root "result" with
                | "hostMismatch" -> SummaryResult.HostMismatch(Read.str root "host", Read.str root "landHost")
                | "compared" ->
                    let tp = Read.prop root "testPrune"

                    SummaryResult.Compared
                        { Servable = Read.int root "servable"
                          Analyzers = Read.counts (Read.prop root "analyzers")
                          Lint = Read.counts (Read.prop root "lint")
                          TestPrune =
                            match Read.str tp "result" with
                            | "notCompared" -> TestPruneSummary.NotCompared(Read.str tp "reason")
                            | "compared" ->
                                TestPruneSummary.Compared
                                    { DifferingAnalyses = Read.int tp "differingAnalyses"
                                      UnverifiableAnalyses = Read.int tp "unverifiableAnalyses"
                                      OutsideSPrimeFailedOrChanged = Read.int tp "outsideSPrimeFailedOrChanged"
                                      Attributable = Read.int tp "attributable"
                                      SelectorMiss = Read.int tp "selectorMiss"
                                      FlakyChanged = Read.int tp "flakyChanged"
                                      ImpactDbCopy = Read.impactDbCopy tp }
                            | other -> raise (MalformedException $"test-prune result %s{other}") }
                | other -> raise (MalformedException $"result %s{other}")

            { RunId = Read.str root "runId"
              WrittenAt = Read.writtenAt root
              Result = result })

// --- the commands the nightly asks -------------------------------------------------------

/// The daemon's own command: the files the references key would have served since it started
/// (`ServableFile`), as `Servable.toJson` writes them.
[<Literal>]
let ServableCommand = "references-shadow-servable"

/// The analyzers plugin's command: given `Servable.toJson`'s list, what reuse would have handed it
/// for each file against what this run found (`PluginReply`).
[<Literal>]
let AnalyzersCommand = "references-shadow-analyzers"

/// The lint plugin's command: as `AnalyzersCommand`, for lint.
[<Literal>]
let LintCommand = "references-shadow-lint"

/// `Servable.toJson`'s schema.
[<Literal>]
let ServableSchema = "fshw-references-shadow-servable-v1"

/// `PluginReply.toJson`'s schema.
[<Literal>]
let PluginReplySchema = "fshw-references-shadow-plugin-v1"

/// The servable files as the daemon hands them to the plugins.
module Servable =
    let toJson (files: ServableFile list) : string =
        let o = JsonObject()
        o["schema"] <- ServableSchema

        o["files"] <-
            Write.array (
                files
                |> List.map (fun f ->
                    let row = JsonObject()
                    row["file"] <- f.File
                    row["project"] <- f.Project
                    row["fcsSignature"] <- f.FcsSignature

                    match f.UsedSignatures with
                    | UsedSignaturesKey.Described hash -> row["usedSignatures"] <- hash
                    | UsedSignaturesKey.Undescribed -> ()

                    row :> JsonNode)
            )

        o.ToJsonString()

    let tryOfJson (text: string) : ServableFile list option =
        Read.document ServableSchema text (fun root ->
            Read.items root "files"
            |> List.map (fun e ->
                ({ File = Read.str e "file"
                   Project = Read.str e "project"
                   FcsSignature = Read.str e "fcsSignature"
                   UsedSignatures =
                     match e.TryGetProperty "usedSignatures" with
                     | true, _ -> UsedSignaturesKey.Described(Read.str e "usedSignatures")
                     | false, _ -> UsedSignaturesKey.Undescribed }
                : ServableFile)))

/// One plugin's comparison as its command replies with it.
module PluginReply =
    let toJson (comparison: PluginComparison) : string =
        let o = Write.plugin comparison
        o["schema"] <- PluginReplySchema
        o.ToJsonString()

    let tryOfJson (text: string) : PluginComparison option =
        Read.document PluginReplySchema text Read.plugin

/// The comparison of `plugin` over `servable`: per file, what `reused` says reuse would have
/// handed it against `fresh`, this run's findings for the file (by repository-relative path).
let comparePlugin
    (plugin: string)
    (reused: ServableFile -> Reused)
    (fresh: string -> ErrorEntry list)
    (servable: ServableFile list)
    : PluginComparison =
    servable
    |> List.map (fun file -> file.File, compareFile (reused file) (fresh file.File))
    |> PluginComparison.ofFiles plugin

/// The test-prune plugin's command: given a `TestPruneRequest`, the selections S and S′
/// (`SelectionsReply`).
[<Literal>]
let TestPruneCommand = "references-shadow-test-prune"

/// `TestPruneRequest.toJson`'s schema.
[<Literal>]
let TestPruneRequestSchema = "fshw-references-shadow-test-prune-request-v1"

/// `SelectionsReply.toJson`'s schema.
[<Literal>]
let SelectionsSchema = "fshw-references-shadow-selections-v1"

/// What the test-prune plugin is asked: the run (whose temporary directory holds the impact-DB
/// copy), C, and the servable files.
type TestPruneRequest =
    { RunId: string
      Changed: string list
      Files: ServableFile list }

module TestPruneRequest =
    let toJson (request: TestPruneRequest) : string =
        let o = JsonObject()
        o["schema"] <- TestPruneRequestSchema
        o["runId"] <- request.RunId
        o["changed"] <- Write.array (request.Changed |> List.map (fun s -> JsonValue.Create s :> JsonNode))
        o["files"] <- JsonNode.Parse(Servable.toJson request.Files)
        o.ToJsonString()

    let tryOfJson (text: string) : TestPruneRequest option =
        Read.document TestPruneRequestSchema text (fun root ->
            { RunId = Read.str root "runId"
              Changed = Read.items root "changed" |> List.map (fun e -> e.ToString())
              Files =
                match Servable.tryOfJson ((Read.prop root "files").GetRawText()) with
                | Some files -> files
                | None -> raise (MalformedException "files") })

module SelectionsReply =
    let private names (set: Set<string>) =
        Write.array (set |> Set.toList |> List.map (fun s -> JsonValue.Create s :> JsonNode))

    let toJson (selections: Selections) : string =
        let o = JsonObject()
        o["schema"] <- SelectionsSchema
        o["s"] <- names selections.S
        o["sPrime"] <- names selections.SPrime
        o["differingAnalyses"] <- selections.DifferingAnalyses
        o["unverifiableAnalyses"] <- selections.UnverifiableAnalyses
        Write.impactDbCopy o selections.Copy
        o.ToJsonString()

    let tryOfJson (text: string) : Selections option =
        Read.document SelectionsSchema text (fun root ->
            let names (name: string) =
                Read.items root name |> List.map (fun e -> e.ToString()) |> Set.ofList

            { S = names "s"
              SPrime = names "sPrime"
              DifferingAnalyses = Read.int root "differingAnalyses"
              UnverifiableAnalyses = Read.int root "unverifiableAnalyses"
              Copy = Read.impactDbCopy root })

// --- files -------------------------------------------------------------------------------

/// How many detailed reports are kept.
[<Literal>]
let RetainedReports = 30

let reportsDirectory (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "references-shadow")

let summaryPath (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "references-shadow.jsonl")

/// File `report`'s detail and its summary line, then keep only the newest `RetainedReports`
/// reports (by when each was written). A diagnostic, never load-bearing: a write that fails is
/// logged and changes no exit code or verdict.
// MGA-ERROR-REPORT-001:ok — the report is measurement; failing to write it must not fail the
// confirm that produced it.
let write (repoRoot: string) (report: Report) : unit =
    try
        let dir = reportsDirectory repoRoot
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, report.RunId + ".json")
        let temporary = path + ".tmp"
        File.WriteAllText(temporary, Report.toJson report)
        File.Move(temporary, path, true)
        File.AppendAllText(summaryPath repoRoot, Summary.toJson (Summary.ofReport report) + "\n")

        let dated =
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun file ->
                let writtenAt =
                    File.ReadAllText file
                    |> Report.tryOfJson
                    |> Option.map _.WrittenAt
                    |> Option.defaultValue DateTimeOffset.MinValue

                file, writtenAt)
            |> Array.sortByDescending snd

        for file, _ in dated |> Array.skip (min RetainedReports dated.Length) do
            File.Delete file
    with ex ->
        Logging.warn "references-shadow" $"report %s{report.RunId} not written: %s{ex.Message}"

/// Every summary line, oldest first; lines that do not parse are skipped. None before the first report.
let readSummaries (repoRoot: string) : Summary list =
    let path = summaryPath repoRoot

    if File.Exists path then
        File.ReadAllLines path |> Array.choose Summary.tryOfJson |> List.ofArray
    else
        []
