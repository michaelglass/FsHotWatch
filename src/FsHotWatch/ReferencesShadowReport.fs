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
/// This module holds the model; `ReferencesShadowJson` its JSON forms (the report, the summary
/// line, the commands the nightly asks the plugins) and the files they are kept in.
module FsHotWatch.ReferencesShadowReport

open System
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

// --- the spot-check of sameKey files -----------------------------------------------------

/// How many `sameKey` files each plugin recomputes per nightly (all of them when there are fewer).
[<Literal>]
let SpotCheckSize = 50

/// A real FCS check of a file under its project options, never answered from a filed check record
/// (`CheckPipeline.CheckFileLive`): what a plugin recomputes a sampled `sameKey` file on, its own
/// result cache bypassed. None when the check did not complete.
type Recheck =
    Events.AbsFilePath -> FSharp.Compiler.CodeAnalysis.FSharpProjectOptions -> Async<Events.FileCheckResult option>

/// One sampled `sameKey` file: the plugin's result recomputed with its cache bypassed, against
/// the result this run served for it.
[<RequireQualifiedAccess>]
type SpotOutcome =
    | Equal
    /// Both lists, normalised: what this run served (the cached result), then the recomputed one.
    | Mismatch of cached: Finding list * recomputed: Finding list
    /// The result could not be recomputed (no live check, a plugin failure), and why.
    | NotRecomputed of reason: string

/// Each plugin's spot-check: RECORD AND COMPARE ONLY, nothing recomputed is reported, filed or served.
type SpotChecks =
    {
        /// The seed every sample of this report was drawn with.
        Seed: int
        /// Each sampled file, by path, and how its recomputed result compared.
        Analyzers: (string * SpotOutcome) list
        Lint: (string * SpotOutcome) list
    }

type SpotCheckCounts =
    {
        Sampled: int
        Equal: int
        /// The sampled files whose recomputed result differed from what the run served, in path
        /// order: kept by name in the summary line, so every night's mismatches can be rolled up.
        Mismatched: string list
        NotRecomputed: int
    }

    member this.Mismatch = this.Mismatched.Length

module SpotCheck =
    /// Up to `size` of `files`, drawn by `seed`, in path order: the same seed and files draw the
    /// same sample whatever order the files are named in.
    let sample (seed: int) (size: int) (files: string list) : string list =
        let pool = files |> List.distinct |> List.sort |> Array.ofList
        let drawn = min size pool.Length
        let random = Random seed

        for i in 0 .. drawn - 1 do
            let j = random.Next(i, pool.Length)
            let picked = pool[j]
            pool[j] <- pool[i]
            pool[i] <- picked

        pool |> Array.truncate drawn |> Array.sort |> List.ofArray

    /// The `cached` result against the `recomputed` one, normalised as `compareFile` normalises.
    let compare (cached: ErrorEntry list) (recomputed: Result<ErrorEntry list, string>) : SpotOutcome =
        match recomputed with
        | Result.Error reason -> SpotOutcome.NotRecomputed reason
        | Result.Ok again ->
            let normal (entries: ErrorEntry list) =
                entries |> List.map Finding.ofEntry |> Finding.normalise

            if normal cached = normal again then
                SpotOutcome.Equal
            else
                SpotOutcome.Mismatch(normal cached, normal again)

    /// Recompute each of `files` (`recompute`, the plugin's own computation, its cache bypassed) and
    /// compare it with `cached`, one file at a time.
    let run
        (files: string list)
        (recompute: string -> Async<Result<ErrorEntry list, string>>)
        (cached: string -> ErrorEntry list)
        : Async<(string * SpotOutcome) list> =
        async {
            let outcomes = ResizeArray()

            for file in files do
                let! again = recompute file
                outcomes.Add(file, compare (cached file) again)

            return List.ofSeq outcomes
        }

    let counts (outcomes: (string * SpotOutcome) list) : SpotCheckCounts =
        outcomes
        |> List.fold
            (fun (c: SpotCheckCounts) (file, outcome) ->
                match outcome with
                | SpotOutcome.Equal -> { c with Equal = c.Equal + 1 }
                | SpotOutcome.Mismatch _ ->
                    { c with
                        Mismatched = file :: c.Mismatched }
                | SpotOutcome.NotRecomputed _ ->
                    { c with
                        NotRecomputed = c.NotRecomputed + 1 })
            { Sampled = outcomes.Length
              Equal = 0
              Mismatched = []
              NotRecomputed = 0 }
        |> fun (c: SpotCheckCounts) ->
            { c with
                Mismatched = List.sort c.Mismatched }

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

/// What test-prune answers a nightly's request. S' diffs the reused analyses against the rows
/// the impact index holds, so it means something only when those rows are THIS run's analysis.
[<RequireQualifiedAccess>]
type SelectionsAnswer =
    | Selected of Selections
    /// The index did not hold this run's analysis when asked, so S' was not computed: why.
    | Refused of reason: string

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
    /// S′ was refused because its premise did not hold (the impact index did not hold this
    /// run's analysis): a defect signal, unlike `NotCompared`, and never a gate failure.
    | Invalid of reason: string

// --- the report --------------------------------------------------------------------------

type Comparison =
    {
        /// Files the nightly's scan found the references key would have served.
        Servable: int
        Analyzers: PluginComparison
        Lint: PluginComparison
        TestPrune: TestPruneSection
        /// Each plugin's recomputed sample of its `sameKey` files.
        SpotChecks: SpotChecks
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
    | Invalid of reason: string

type SpotCheckSummary =
    { Seed: int
      Analyzers: SpotCheckCounts
      Lint: SpotCheckCounts }

type SummaryCounts =
    { Servable: int
      Analyzers: Counts
      Lint: Counts
      TestPrune: TestPruneSummary
      SpotChecks: SpotCheckSummary }

[<RequireQualifiedAccess>]
type SummaryResult =
    | Compared of SummaryCounts
    | HostMismatch of host: string * landHost: string

type Summary =
    { RunId: string
      WrittenAt: DateTimeOffset
      Result: SummaryResult }

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
                      SpotChecks =
                        { Seed = c.SpotChecks.Seed
                          Analyzers = SpotCheck.counts c.SpotChecks.Analyzers
                          Lint = SpotCheck.counts c.SpotChecks.Lint }
                      TestPrune =
                        match c.TestPrune with
                        | TestPruneSection.NotCompared reason -> TestPruneSummary.NotCompared reason
                        | TestPruneSection.Invalid reason -> TestPruneSummary.Invalid reason
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

// --- every night's spot-checks -----------------------------------------------------------

/// One spot-check mismatch as a summary line recorded it: the night (its run, and when its report
/// was written), the plugin, and the file.
type SpotCheckMismatch =
    { RunId: string
      WrittenAt: DateTimeOffset
      Plugin: string
      File: string }

/// The spot-checks of every recorded night together: every line of `references-shadow.jsonl`,
/// not only the nights a window or the retained reports keep. A night on another host than the
/// land host has no spot-check and is not counted.
type SpotCheckRollUp =
    {
        /// Nights compared on the land host.
        Nights: int
        /// Files sampled over those nights, both plugins.
        Sampled: int
        /// Every mismatch, oldest night first, then by plugin and file.
        Mismatches: SpotCheckMismatch list
    }

module SpotCheckRollUp =
    /// `summaries` (oldest first, as `ReferencesShadowJson.readSummaries` returns them) rolled up.
    let ofSummaries (summaries: Summary list) : SpotCheckRollUp =
        let compared =
            summaries
            |> List.choose (fun s ->
                match s.Result with
                | SummaryResult.Compared c -> Some(s, c.SpotChecks)
                | SummaryResult.HostMismatch _ -> None)

        { Nights = compared.Length
          Sampled =
            compared
            |> List.sumBy (fun (_, spot) -> spot.Analyzers.Sampled + spot.Lint.Sampled)
          Mismatches =
            compared
            |> List.collect (fun (summary, spot) ->
                [ "analyzers", spot.Analyzers; "lint", spot.Lint ]
                |> List.collect (fun (plugin, counts) ->
                    counts.Mismatched
                    |> List.map (fun file ->
                        { RunId = summary.RunId
                          WrittenAt = summary.WrittenAt
                          Plugin = plugin
                          File = file }))) }
