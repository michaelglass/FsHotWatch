/// `fshw analytics` (AUTOMATION-1368): flaky, slow and trust, as queries over one or more
/// outcome stores (ADR-059 §2), read as a union, and the shadow ledger (ADR-059 §7).
///
/// A pure read. It contacts no daemon, triggers no run, and changes neither the store (it
/// reads with `OutcomeStore.readAll`, which quarantines nothing) nor any ledger.
///
/// It reports only what the store records. The store files one outcome per action — per
/// test PROJECT run — so flaky input sets and slow are per project; flaky TESTS come from the
/// per-test rows filed beside each test record (AUTOMATION-1744). A metric the data cannot
/// answer is named in `Gaps`, never reported as a zero.
///
/// Definitions:
///   * flaky: one action key (the same inputs) recorded both pass and fail;
///   * flaky test: one test, on one tree by one tool, recorded both passed and red (failed,
///     timed out, cancelled or other), with each outcome counted and a red and a green run;
///   * slow: nearest-rank percentiles of `durationMs`, per subject;
///   * reds later green: a fail record followed by a pass record under the same key;
///   * runs a lookup would have answered: records filed under a key that already held a
///     pass, earlier. While the store is a shadow, every one of them was a run;
///   * verdict reads satisfied: shadowed `fshw verdict` reads whose lookup found an
///     answer (green or red) rather than `missing`. A `check`/`confirm` line looks up
///     after its own run filed records, so it says nothing about hits.
module FsHotWatch.Cli.OutcomeAnalytics

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch

[<Literal>]
let Schema = "fshw-analytics-v2"

/// One line of `.fshw/shadow/lookup-verdict.jsonl`, reduced to what analytics reads.
type ShadowLine =
    {
        At: DateTimeOffset
        Command: string
        Direction: ShadowVerdict.Direction
        /// The lookup's class.
        Lookup: ShadowVerdict.Class
    }

type ShadowRead =
    {
        Lines: ShadowLine list
        /// Lines that are not `fshw-shadow-v1` lines.
        UnreadableLines: int
        /// The logs that exist and were read.
        Logs: string list
    }

/// One subject's input sets that were recorded both green and red, in one store.
type Flaky =
    {
        Kind: string
        Subject: string
        /// Keys with both a pass and a fail.
        FlakyKeys: int
        /// Every key of the subject.
        Keys: int
        /// Passes and fails under the flaky keys.
        Passes: int
        Fails: int
        /// Distinct checkouts that produced the flaky keys' records.
        Worktrees: int
        LastSeen: DateTimeOffset
    }

/// How a finding's runs were grouped (AUTOMATION-1744). Each section of the report has one.
[<RequireQualifiedAccess>]
type Grouping =
    /// One action key, the same inputs. A key names its machine (its toolchain half carries the
    /// kernel build), so this grouping never pairs runs across machines; it stays within one store.
    | SameKey
    /// One test on one tree by one tool, with no key and no machine: it pairs runs across stores
    /// and machines.
    | TestTreeTool

module Grouping =
    /// The token the JSON report writes.
    let token (grouping: Grouping) : string =
        match grouping with
        | Grouping.SameKey -> "key"
        | Grouping.TestTreeTool -> "test-tree-tool"

    /// How the human report names it.
    let label (grouping: Grouping) : string =
        match grouping with
        | Grouping.SameKey -> "same key, within one store"
        | Grouping.TestTreeTool -> "test, tree and tool, across stores and machines"

/// An outcome store analysed, named by its directory's full path: its identity (AUTOMATION-1744).
type StoreSource =
    private
    | StoreSource of string

    /// The store's directory, as a full path.
    member this.Path =
        let (StoreSource path) = this
        path

module StoreSource =
    /// The store in directory `path`, relative paths taken from the current directory.
    let ofPath (path: string) : StoreSource = StoreSource(Path.GetFullPath path)

/// Where, within a store, a run's per-test rows were read from (AUTOMATION-1744).
[<RequireQualifiedAccess>]
type RowSource =
    /// The index filed beside the run's record, under its key: the strict same-key join.
    | Index
    /// The daily test-row log, which outlives the record.
    | DailyLog

module RowSource =
    /// The token the JSON report writes.
    let token (source: RowSource) : string =
        match source with
        | RowSource.Index -> "index"
        | RowSource.DailyLog -> "log"

    /// How the human report names it.
    let label (source: RowSource) : string =
        match source with
        | RowSource.Index -> "index"
        | RowSource.DailyLog -> "daily log"

/// One place a run's rows were read: a store, and where in it.
type Origin = { Store: StoreSource; Via: RowSource }

/// What one store holds, read without changing it.
type StoreRead =
    { Source: StoreSource
      Scan: OutcomeStore.Scan
      Rows: OutcomeStore.TestRowsScan
      Log: TestRowsLog.LogRead }

/// Read the store `source` names: its records, the row indexes beside them, and its daily log.
let readStore (source: StoreSource) : StoreRead =
    { Source = source
      Scan = OutcomeStore.readAll source.Path
      Rows = OutcomeStore.readAllTestRows source.Path
      Log = TestRowsLog.read source.Path }

/// Read every store in `sources`, in order.
let readStores (sources: FsHotWatch.Core.NonEmpty<StoreSource>) : FsHotWatch.Core.NonEmpty<StoreRead> =
    { Head = readStore sources.Head
      Tail = List.map readStore sources.Tail }

/// One run's per-test rows, with every place they were read.
type SourcedRun =
    { Index: TestRows.Index
      Sources: FsHotWatch.Core.NonEmpty<Origin> }

/// Each run in `read`, once. A run is its run id, project and machine: read beside its record and
/// in the daily log, or in two stores (a log copied between them), it counts once and names every
/// place it was read, in reading order. Its rows are its first reading's, which the others copy.
let mergeRuns (read: (Origin * TestRows.Index) list) : SourcedRun list =
    read
    |> List.groupBy (fun (_, i) -> i.Run.RunId, i.Run.Project, i.Run.Machine)
    |> List.map (fun (_, seen) ->
        let origins = seen |> List.map fst |> List.distinct

        { Index = snd (List.head seen)
          Sources =
            { Head = List.head origins
              Tail = List.tail origins } })

/// One side of a flaky test: the run, the machine it ran on, and where it was read.
type RunSeen =
    { RunId: string
      Machine: string
      From: FsHotWatch.Core.NonEmpty<Origin> }

/// How many runs recorded a test red, and how many passed: the rate's parts. A skip is neither.
type Tally = { Red: int; Passed: int }

/// One test recorded both green and red on the same tree by the same tool (AUTOMATION-1744),
/// from the per-test rows of every store read, beside their records and in their daily logs.
/// Grouped by `Grouping.TestTreeTool`, so a red and a green may come from different stores and
/// machines; each side names its own.
type FlakyTest =
    {
        Project: string
        Test: string
        Tree: string
        Tool: string
        /// How many runs recorded each outcome, in `TestRows.Outcome` order; only outcomes seen.
        Outcomes: (TestRows.Outcome * int) list
        /// The newest run that recorded it red (failed, timed out, cancelled or other).
        Red: RunSeen
        /// The newest run that recorded it passed.
        Green: RunSeen
        LastSeen: DateTimeOffset
        /// Every place the runs that recorded it were read, stores in the order given.
        Sources: FsHotWatch.Core.NonEmpty<Origin>
        /// Per store, in the order given: the runs it holds. A run two stores hold counts in each.
        ByStore: FsHotWatch.Core.NonEmpty<StoreSource * Tally>
        /// Per machine, by name: each run once.
        ByMachine: FsHotWatch.Core.NonEmpty<string * Tally>
    }

type Slow =
    { Kind: string
      Subject: string
      Samples: int
      P50Ms: int64
      P90Ms: int64
      MaxMs: int64 }

/// Per kind: records filed under a key that already held an earlier pass.
type Reuse =
    { Kind: string
      Keys: int
      Records: int
      RedundantRuns: int }

type Reds = { Fails: int; LaterGreen: int }

type StoreDay =
    { Day: string
      Records: int
      Fails: int
      LaterGreen: int }

type ShadowDay =
    { Day: string
      Lines: int
      Agree: int
      Directions: (ShadowVerdict.Direction * int) list }

type LookupReads =
    { Reads: int
      Satisfied: int
      Missing: int }

/// One store's part of the report: what it held, and every section grouped by
/// `Grouping.SameKey`, which stays within a store.
type StoreReport =
    {
        Source: StoreSource
        /// Its readable records in the window, and files that are not a record.
        Records: int
        Unreadable: int
        /// Runs read beside its records, and index files that are not one.
        IndexRuns: int
        UnreadableTestRows: int
        /// Runs read from its daily log; its files that could not be read (each losing only its own
        /// run); and lines, in files that were read, that are not a log line.
        LogRuns: int
        UnreadableLogFiles: int
        UnreadableLogLines: int
        Flaky: Flaky list
        Reds: Reds
        Reuse: Reuse list
        StoreDays: StoreDay list
    }

type Report =
    {
        Since: DateTimeOffset option
        /// Every store read, in the order given.
        Stores: FsHotWatch.Core.NonEmpty<StoreReport>
        /// Records over every store, a record two stores hold counted once.
        RecordsRead: int
        Kinds: (string * int) list
        DurationsRecorded: int
        /// Runs of per-test rows over every store and source, each counted once.
        TestRowRuns: int
        FlakyTests: FlakyTest list
        Slow: Slow list
        ShadowDays: ShadowDay list
        ShadowUnreadable: int
        ShadowLogs: string list
        LookupReads: LookupReads
        /// The nightly's output-level shadow reports in the window (AUTOMATION-1816 step 2b),
        /// oldest first, from `.fshw/references-shadow.jsonl`.
        ReferencesShadow: FsHotWatch.ReferencesShadowReport.Summary list
        Gaps: string list
    }

/// A shadow log line, or `None` for anything that is not a complete `fshw-shadow-v1` line.
let parseShadowLine (text: string) : ShadowLine option =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        match
            Json.tryString o "schema", Json.tryString o "at", Json.tryString o "command", Json.tryString o "direction"
        with
        | Some "fshw-shadow-v1", Some at, Some command, Some direction ->
            // A line with no lookup class names none; one naming an unknown class or
            // direction is not a line this schema wrote.
            let lookup =
                match Json.tryProp o "lookup" |> Option.bind (fun l -> Json.tryString l "outcome") with
                // A line the shadow could not compare established nothing (AUTOMATION-1568 rework).
                | Some "not-compared" -> Some ShadowVerdict.Class.Missing
                | Some token -> ShadowVerdict.Class.tryParse token
                | None -> Some ShadowVerdict.Class.Missing

            match
                DateTimeOffset.TryParse(
                    at,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.None
                ),
                ShadowVerdict.Direction.tryParse direction,
                lookup
            with
            | (true, parsed), Some direction, Some lookup ->
                Some
                    { At = parsed
                      Command = command
                      Direction = direction
                      Lookup = lookup }
            | _ -> None
        | _ -> None
    with :? JsonException ->
        None

/// Read every log in `paths` that exists; a log that does not exist is skipped.
let readShadowLogs (paths: string list) : ShadowRead =
    let existing = paths |> List.filter File.Exists

    let parsed =
        [ for path in existing do
              for line in File.ReadAllLines path do
                  if not (String.IsNullOrWhiteSpace line) then
                      yield parseShadowLine line ]

    { Lines = parsed |> List.choose id
      UnreadableLines = parsed |> List.filter Option.isNone |> List.length
      Logs = existing }

/// The shadow logs read by default: this checkout's, then those of every worktree the
/// store's records name, in the order the records name them. Only logs that exist.
let shadowLogsFor (repoRoot: string) (records: OutcomeStore.Record list) : string list =
    let logOf (root: string) = ShadowVerdict.logPath root

    (repoRoot :: (records |> List.map _.Producer.Worktree))
    |> List.map logOf
    |> List.distinctBy (fun p -> p.ToUpperInvariant())
    |> List.filter File.Exists

/// Nearest-rank percentile of an ascending, non-empty array.
let private percentile (sorted: int64[]) (p: float) =
    let rank = int (Math.Ceiling(p / 100.0 * float sorted.Length))
    sorted[max 0 (rank - 1)]

let private day (at: DateTimeOffset) =
    at.UtcDateTime.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

let private pass (r: OutcomeStore.Record) = r.Outcome = OutcomeStore.Outcome.Pass

let private fail (r: OutcomeStore.Record) = r.Outcome = OutcomeStore.Outcome.Fail

/// A fail with a pass after it under the same key: `history` is one key's records, oldest first.
let private laterGreen (history: OutcomeStore.Record list) (r: OutcomeStore.Record) =
    fail r
    && history
       |> List.exists (fun later -> pass later && later.RecordedAt > r.RecordedAt)

let private gaps (rowRuns: int) (durations: int) (records: int) (shadow: ShadowRead) (lookupReads: int) =
    [ if rowRuns = 0 then
          yield
              "flaky tests: no run in the window filed per test rows (beside each test record, or in the daily \
               test-row log, since AUTOMATION-1744), so flaky is per test project only."
      if records > 0 && durations = 0 then
          yield
              "slow: no record carries durationMs (the runner files its K_P test records with DurationMs = None). \
               Smallest addition: file the run's elapsed wall clock as durationMs."
      if shadow.Logs.IsEmpty then
          yield "trust: no shadow ledger (.fshw/shadow/lookup-verdict.jsonl) was found, so no agree/disagree rate."
      elif lookupReads = 0 then
          yield "cache hits: no `fshw verdict` read was shadowed in the window, so no lookup-satisfied rate."
      yield
          "cache hits: the store is still a shadow (ADR-059 §7), so no lookup has yet replaced a run; \
           'runs a lookup would have answered' counts the runs it could have." ]

/// Whether `outcome` is a red: anything that ran and did not pass.
let private isRed (outcome: TestRows.Outcome) =
    match outcome with
    | TestRows.Outcome.Failed
    | TestRows.Outcome.TimedOut
    | TestRows.Outcome.Cancelled
    | TestRows.Outcome.Other -> true
    | TestRows.Outcome.Passed
    | TestRows.Outcome.Skipped -> false

/// Each test that `runs` recorded both passed and red on one tree by one tool (AUTOMATION-1744),
/// newest first, naming where each side and every run was read, with its rates per store (in the
/// order of `stores`) and per machine. A skip is neither: it did not run.
let flakyTests (stores: StoreSource list) (runs: SourcedRun list) : FlakyTest list =
    let storeRank (store: StoreSource) = List.findIndex ((=) store) stores

    let tally (seen: (SourcedRun * TestRows.Row) list) =
        { Red = seen |> List.filter (fun (_, row) -> isRed row.Outcome) |> List.length
          Passed =
            seen
            |> List.filter (fun (_, row) -> row.Outcome = TestRows.Outcome.Passed)
            |> List.length }

    runs
    |> List.collect (fun sourced ->
        FsHotWatch.Core.NonEmpty.toList sourced.Index.Rows
        |> List.map (fun row -> sourced, row))
    |> List.groupBy (fun (sourced, row) ->
        let run = sourced.Index.Run
        run.Project, row.Name, run.Tree, run.Tool)
    |> List.choose (fun ((project, test, tree, tool), seen) ->
        let newestRun (counts: TestRows.Outcome -> bool) =
            seen
            |> List.filter (fun (_, row) -> counts row.Outcome)
            |> List.sortBy (fun (sourced, _) -> sourced.Index.Run.RecordedAt)
            |> List.tryLast
            |> Option.map (fun (sourced, _) ->
                { RunId = sourced.Index.Run.RunId
                  Machine = sourced.Index.Run.Machine
                  From = sourced.Sources })

        let origins =
            seen
            |> List.collect (fun (sourced, _) -> FsHotWatch.Core.NonEmpty.toList sourced.Sources)
            |> List.distinct
            |> List.sortBy (fun o -> storeRank o.Store, o.Via)

        let byStore =
            origins
            |> List.map _.Store
            |> List.distinct
            |> List.map (fun store ->
                store,
                seen
                |> List.filter (fun (sourced, _) ->
                    FsHotWatch.Core.NonEmpty.toList sourced.Sources
                    |> List.exists (fun o -> o.Store = store))
                |> tally)

        let byMachine =
            seen
            |> List.groupBy (fun (sourced, _) -> sourced.Index.Run.Machine)
            |> List.sortBy fst
            |> List.map (fun (machine, onIt) -> machine, tally onIt)

        // Each list holds at least the first run's: its store, its origin and its machine.
        let nonEmpty (items: 'T list) : FsHotWatch.Core.NonEmpty<'T> =
            { Head = List.head items
              Tail = List.tail items }

        match newestRun isRed, newestRun ((=) TestRows.Outcome.Passed) with
        | Some red, Some green ->
            Some
                { Project = project
                  Test = test
                  Tree = tree
                  Tool = tool
                  Outcomes = seen |> List.countBy (fun (_, row) -> row.Outcome) |> List.sortBy fst
                  Red = red
                  Green = green
                  Sources = nonEmpty origins
                  ByStore = nonEmpty byStore
                  ByMachine = nonEmpty byMachine
                  LastSeen = seen |> List.map (fun (sourced, _) -> sourced.Index.Run.RecordedAt) |> List.max }
        | _ -> None)
    |> List.sortBy (fun f -> -f.LastSeen.UtcTicks, f.Project, f.Test)

/// Records oldest first, a fail after a pass filed at the same instant: the store's own order.
let private chronological (records: OutcomeStore.Record list) =
    records |> List.sortBy (fun r -> r.RecordedAt, (if fail r then 1 else 0))

/// The sections grouped by `Grouping.SameKey` over one store's `records`, with what it held.
let private storeReport
    (read: StoreRead)
    (records: OutcomeStore.Record list)
    (indexed: TestRows.Index list)
    (logged: TestRows.Index list)
    : StoreReport =
    let byKey = records |> List.groupBy _.Key.Value |> Map.ofList

    let historyOf (r: OutcomeStore.Record) = Map.find r.Key.Value byKey

    let flaky =
        records
        |> List.groupBy (fun r -> r.Key.Kind, r.Subject)
        |> List.choose (fun ((kind, subject), rs) ->
            let keys = rs |> List.groupBy _.Key.Value

            let flakyKeys =
                keys |> List.filter (fun (_, h) -> List.exists pass h && List.exists fail h)

            match flakyKeys with
            | [] -> None
            | _ ->
                let flakyRecords = flakyKeys |> List.collect snd

                Some
                    { Kind = kind
                      Subject = subject
                      FlakyKeys = flakyKeys.Length
                      Keys = keys.Length
                      Passes = flakyRecords |> List.filter pass |> List.length
                      Fails = flakyRecords |> List.filter fail |> List.length
                      Worktrees = flakyRecords |> List.distinctBy _.Producer.Worktree |> List.length
                      LastSeen = flakyRecords |> List.map _.RecordedAt |> List.max })
        |> List.sortBy (fun f -> -f.FlakyKeys, -f.LastSeen.UtcTicks)

    let fails = records |> List.filter fail

    let reuse =
        records
        |> List.groupBy _.Key.Kind
        |> List.map (fun (kind, rs) ->
            let keys = rs |> List.groupBy _.Key.Value

            let redundant =
                keys
                |> List.sumBy (fun (_, h) ->
                    match h |> List.tryFindIndex pass with
                    | Some first -> h.Length - first - 1
                    | None -> 0)

            { Kind = kind
              Keys = keys.Length
              Records = rs.Length
              RedundantRuns = redundant })
        |> List.sortBy _.Kind

    let storeDays =
        records
        |> List.groupBy (fun r -> day r.RecordedAt)
        |> List.sortBy fst
        |> List.map (fun (d, rs) ->
            let dayFails = rs |> List.filter fail

            { StoreDay.Day = d
              Records = rs.Length
              Fails = dayFails.Length
              LaterGreen = dayFails |> List.filter (fun r -> laterGreen (historyOf r) r) |> List.length })

    { Source = read.Source
      Records = records.Length
      Unreadable = read.Scan.Unreadable
      IndexRuns = indexed.Length
      UnreadableTestRows = read.Rows.Unreadable
      LogRuns = logged.Length
      UnreadableLogFiles = read.Log.UnreadableFiles
      UnreadableLogLines = read.Log.UnreadableLines
      Flaky = flaky
      Reds =
        { Fails = fails.Length
          LaterGreen = fails |> List.filter (fun r -> laterGreen (historyOf r) r) |> List.length }
      Reuse = reuse
      StoreDays = storeDays }

/// The report over `stores`, analysed as a union (AUTOMATION-1744), and `shadow`'s lines, each
/// limited to `since` when given. The sections grouped by key stay per store; the flaky tests pair
/// runs across stores and machines; a record or run two stores hold counts once in the union.
let analyze
    (since: DateTimeOffset option)
    (stores: FsHotWatch.Core.NonEmpty<StoreRead>)
    (shadow: ShadowRead)
    (referencesShadow: FsHotWatch.ReferencesShadowReport.Summary list)
    : Report =
    let inWindow (at: DateTimeOffset) =
        since |> Option.forall (fun s -> at >= s)

    let windowed (read: StoreRead) =
        read,
        read.Scan.Records |> List.filter (fun r -> inWindow r.RecordedAt),
        read.Rows.Indexes |> List.filter (fun i -> inWindow i.Run.RecordedAt),
        read.Log.Runs |> List.filter (fun i -> inWindow i.Run.RecordedAt)

    let parts = FsHotWatch.Core.NonEmpty.toList stores |> List.map windowed
    let report (read, records, indexed, logged) = storeReport read records indexed logged

    let records =
        parts
        |> List.collect (fun (_, records, _, _) -> records)
        |> List.distinct
        |> chronological

    let rowRuns =
        parts
        |> List.collect (fun (read, _, indexed, logged) ->
            (indexed
             |> List.map (fun i ->
                 { Store = read.Source
                   Via = RowSource.Index },
                 i))
            @ (logged
               |> List.map (fun i ->
                   { Store = read.Source
                     Via = RowSource.DailyLog },
                   i)))
        |> mergeRuns

    let lines = shadow.Lines |> List.filter (fun l -> inWindow l.At)

    let slow =
        records
        |> List.choose (fun r -> r.DurationMs |> Option.map (fun d -> (r.Key.Kind, r.Subject), d))
        |> List.groupBy fst
        |> List.map (fun ((kind, subject), ds) ->
            let sorted = ds |> List.map snd |> List.sort |> Array.ofList

            { Kind = kind
              Subject = subject
              Samples = sorted.Length
              P50Ms = percentile sorted 50.0
              P90Ms = percentile sorted 90.0
              MaxMs = Array.last sorted })
        |> List.sortBy (fun s -> -s.P90Ms, s.Subject)

    let shadowDays =
        lines
        |> List.groupBy (fun l -> day l.At)
        |> List.sortBy fst
        |> List.map (fun (d, ls) ->
            { ShadowDay.Day = d
              Lines = ls.Length
              Agree =
                ls
                |> List.filter (fun l -> l.Direction = ShadowVerdict.Direction.Agree)
                |> List.length
              Directions =
                ls
                |> List.countBy _.Direction
                |> List.sortBy (fst >> ShadowVerdict.Direction.token) })

    let reads = lines |> List.filter (fun l -> l.Command = "verdict")

    let missing =
        reads
        |> List.filter (fun l -> l.Lookup = ShadowVerdict.Class.Missing)
        |> List.length

    let durations = records |> List.filter (fun r -> r.DurationMs.IsSome) |> List.length

    { Since = since
      Stores =
        { Head = report (List.head parts)
          Tail = List.tail parts |> List.map report }
      RecordsRead = records.Length
      Kinds = records |> List.countBy _.Key.Kind |> List.sortBy fst
      DurationsRecorded = durations
      TestRowRuns = rowRuns.Length
      FlakyTests = flakyTests (parts |> List.map (fun (read, _, _, _) -> read.Source)) rowRuns
      Slow = slow
      ShadowDays = shadowDays
      ShadowUnreadable = shadow.UnreadableLines
      ShadowLogs = shadow.Logs
      LookupReads =
        { Reads = reads.Length
          Satisfied = reads.Length - missing
          Missing = missing }
      ReferencesShadow = referencesShadow |> List.filter (fun s -> inWindow s.WrittenAt)
      Gaps =
        gaps rowRuns.Length durations records.Length shadow reads.Length
        @ [ if referencesShadow |> List.forall (fun s -> not (inWindow s.WrittenAt)) then
                "references shadow: no nightly report in the window (.fshw/references-shadow.jsonl): cache.nightlyShadow is off, or no fresh confirm has run with it." ] }

let private seconds (ms: int64) =
    (float ms / 1000.0).ToString("0.0", Globalization.CultureInfo.InvariantCulture)
    + "s"

let private stamp (at: DateTimeOffset) =
    at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Globalization.CultureInfo.InvariantCulture)
    + "Z"

let private rate (part: int) (whole: int) =
    if whole = 0 then
        $"%d{part} of %d{whole}"
    else
        let pct =
            (100.0 * float part / float whole).ToString("0.#", Globalization.CultureInfo.InvariantCulture)

        $"%d{part} of %d{whole} (%s{pct}%%)"

/// Where runs were read, store by store: `/s: index and daily log; /t: daily log`.
let private originsText (origins: FsHotWatch.Core.NonEmpty<Origin>) =
    FsHotWatch.Core.NonEmpty.toList origins
    |> List.groupBy _.Store
    |> List.map (fun (store, inIt) ->
        let vias = inIt |> List.map (_.Via >> RowSource.label) |> String.concat " and "
        $"%s{store.Path}: %s{vias}")
    |> String.concat "; "

/// Rates by store or machine: `server red 2 of 3 (66.7%); laptop red 0 of 2 (0%)`.
let private talliesText (name: 'K -> string) (tallies: FsHotWatch.Core.NonEmpty<'K * Tally>) =
    FsHotWatch.Core.NonEmpty.toList tallies
    |> List.map (fun (k, t) -> $"%s{name k} red %s{rate t.Red (t.Red + t.Passed)}")
    |> String.concat "; "

/// The human report; `top` bounds the flaky and slow lists.
/// One nightly shadow summary, as `render` shows it.
let private referencesShadowText (result: FsHotWatch.ReferencesShadowReport.SummaryResult) =
    match result with
    | FsHotWatch.ReferencesShadowReport.SummaryResult.HostMismatch(host, landHost) ->
        $"host mismatch (ran on %s{host}, land host %s{landHost}): nothing compared"
    | FsHotWatch.ReferencesShadowReport.SummaryResult.Compared c ->
        let plugin (name: string) (counts: FsHotWatch.ReferencesShadowReport.Counts) =
            $"%s{name} equal %d{counts.Equal} / mismatch %d{counts.Mismatch} / unverifiable %d{counts.Unverifiable} / same key %d{counts.SameKey}"

        let testPrune =
            match c.TestPrune with
            | FsHotWatch.ReferencesShadowReport.TestPruneSummary.NotCompared reason ->
                $"test-prune not compared (%s{reason})"
            | FsHotWatch.ReferencesShadowReport.TestPruneSummary.Invalid reason ->
                $"test-prune INVALID, S' refused (%s{reason})"
            | FsHotWatch.ReferencesShadowReport.TestPruneSummary.Compared tp ->
                let copy =
                    match tp.ImpactDbCopy with
                    | FsHotWatch.ReferencesShadowReport.ImpactDbCopy.NotCopied -> "no impact-DB copy"
                    | FsHotWatch.ReferencesShadowReport.ImpactDbCopy.Copied(bytes, ms) ->
                        $"impact-DB copy %d{bytes} bytes in %d{ms} ms"

                $"test-prune attributable %d{tp.Attributable}, selector miss %d{tp.SelectorMiss}, flaky %d{tp.FlakyChanged}, differing analyses %d{tp.DifferingAnalyses}, unverifiable %d{tp.UnverifiableAnalyses}, %s{copy}"

        let spot (name: string) (counts: FsHotWatch.ReferencesShadowReport.SpotCheckCounts) =
            $"%s{name} %d{counts.Sampled} sampled: equal %d{counts.Equal} / mismatch %d{counts.Mismatch} / not recomputed %d{counts.NotRecomputed}"

        let analyzers = plugin "analyzers" c.Analyzers
        let lint = plugin "lint" c.Lint

        let spotChecks =
            let analyzersSpot = spot "analyzers" c.SpotChecks.Analyzers
            let lintSpot = spot "lint" c.SpotChecks.Lint
            $"same-key spot-check (seed %d{c.SpotChecks.Seed}): %s{analyzersSpot}; %s{lintSpot}"

        $"servable %d{c.Servable}; %s{analyzers}; %s{lint}; %s{spotChecks}; %s{testPrune}"

let render (top: int) (r: Report) : string list =
    [ let stores = FsHotWatch.Core.NonEmpty.toList r.Stores

      let window =
          match r.Since with
          | Some s -> $" since %s{stamp s}"
          | None -> ""

      let kinds =
          match r.Kinds with
          | [] -> ""
          | kinds ->
              let named = kinds |> List.map (fun (k, n) -> $"%s{k} %d{n}") |> String.concat ", "
              $" (%s{named})"

      yield
          $"Outcome stores: %d{stores.Length}; %d{r.RecordsRead} records%s{window}%s{kinds}, a record several stores hold counted once"

      for s in stores do
          yield
              $"  store %s{s.Source.Path}: %d{s.Records} records, %d{s.Unreadable} unreadable; \
                per test rows: %d{s.IndexRuns} run(s) beside records, %d{s.UnreadableTestRows} unreadable; \
                %d{s.LogRuns} in the daily log, %d{s.UnreadableLogFiles} unreadable log file(s), \
                %d{s.UnreadableLogLines} unreadable log line(s)"

      yield ""
      yield $"Flaky (one input set recorded both pass and fail; grouping: %s{Grouping.label Grouping.SameKey}):"

      for s in stores do
          yield $"  store %s{s.Source.Path}:"

          match s.Flaky with
          | [] -> yield "    no input set recorded both outcomes"
          | flaky ->
              for f in List.truncate top flaky do
                  yield
                      $"    %s{f.Subject} [%s{f.Kind}]: %d{f.FlakyKeys} of %d{f.Keys} input sets; %d{f.Passes} pass / %d{f.Fails} fail; \
                        %d{f.Worktrees} checkout(s); last %s{stamp f.LastSeen}"

      yield ""

      yield
          $"Flaky tests (grouping: %s{Grouping.label Grouping.TestTreeTool}; \
             %d{r.TestRowRuns} run(s) of per test rows, each counted once):"

      match r.FlakyTests with
      | [] -> yield "  no test recorded both outcomes"
      | flaky ->
          for f in List.truncate top flaky do
              let outcomes =
                  f.Outcomes
                  |> List.map (fun (o, n) -> $"%d{n} %s{TestRows.Outcome.token o}")
                  |> String.concat ", "

              yield
                  $"  %s{f.Test} [%s{f.Project}]: %s{outcomes}; \
                    red in run %s{f.Red.RunId} on %s{f.Red.Machine} (%s{originsText f.Red.From}), \
                    passed in run %s{f.Green.RunId} on %s{f.Green.Machine} (%s{originsText f.Green.From}); \
                    tree %s{f.Tree}, tool %s{f.Tool}; from %s{originsText f.Sources}; last %s{stamp f.LastSeen}"

              yield $"      per store: %s{talliesText (fun (s: StoreSource) -> s.Path) f.ByStore}"
              yield $"      per machine: %s{talliesText id f.ByMachine}"

      yield ""
      yield $"Slow (by recorded durationMs; %d{r.DurationsRecorded} of %d{r.RecordsRead} records carry one):"

      match r.Slow with
      | [] -> yield "  no durations recorded"
      | slow ->
          for s in List.truncate top slow do
              yield
                  $"  %s{s.Subject} [%s{s.Kind}]: p50 %s{seconds s.P50Ms}, p90 %s{seconds s.P90Ms}, max %s{seconds s.MaxMs} \
                    over %d{s.Samples} run(s)"

      yield ""
      yield "Trust:"

      for s in stores do
          yield $"  store %s{s.Source.Path} (grouping: %s{Grouping.label Grouping.SameKey}):"
          yield $"    reds later green on identical inputs: %s{rate s.Reds.LaterGreen s.Reds.Fails}"

          for u in s.Reuse do
              yield
                  $"    runs a lookup would have answered: %s{rate u.RedundantRuns u.Records} [%s{u.Kind}, %d{u.Keys} keys]"

          for d in s.StoreDays do
              yield $"    %s{d.Day} store: %d{d.Records} records, %d{d.Fails} fail, %d{d.LaterGreen} later green"

      yield $"  verdict reads satisfied by the store: %s{rate r.LookupReads.Satisfied r.LookupReads.Reads}"

      let total = r.ShadowDays |> List.sumBy _.Lines
      let agree = r.ShadowDays |> List.sumBy _.Agree

      yield
          $"  shadow ledger: agree %s{rate agree total} over %d{r.ShadowLogs.Length} log(s); %d{r.ShadowUnreadable} unreadable line(s)"

      for d in r.ShadowDays do
          let directions =
              d.Directions
              |> List.map (fun (k, n) -> $"%s{ShadowVerdict.Direction.token k} %d{n}")
              |> String.concat ", "

          yield $"  %s{d.Day} shadow: agree %d{d.Agree}/%d{d.Lines} (%s{directions})"

      for s in r.ReferencesShadow do
          yield $"  references shadow %s{stamp s.WrittenAt} run %s{s.RunId}: %s{referencesShadowText s.Result}"

      yield ""
      yield "Not recorded:"

      for g in r.Gaps do
          yield $"  - %s{g}" ]

/// The report as one `fshw-analytics-v2` JSON object.
let toJson (r: Report) : string =
    let arr (items: JsonNode seq) =
        let a = JsonArray()

        for i in items do
            a.Add i

        a

    let obj (fields: (string * JsonNode) list) =
        let o = JsonObject()

        for (k, v) in fields do
            o[k] <- v

        o

    let js (x: string) : JsonNode = JsonValue.Create x
    let ji (x: int) : JsonNode = JsonValue.Create x
    let jl (x: int64) : JsonNode = JsonValue.Create x
    let time (at: DateTimeOffset) = js (at.ToString "o")

    let counts (pairs: (string * int) list) =
        obj (pairs |> List.map (fun (k, n) -> k, ji n))

    let origins (from: FsHotWatch.Core.NonEmpty<Origin>) =
        arr (
            FsHotWatch.Core.NonEmpty.toList from
            |> List.map (fun o -> obj [ "store", js o.Store.Path; "via", js (RowSource.token o.Via) ] :> JsonNode)
        )

    let runSeen (run: RunSeen) =
        obj [ "runId", js run.RunId; "machine", js run.Machine; "from", origins run.From ]

    let tallies (label: string) (name: 'K -> string) (pairs: FsHotWatch.Core.NonEmpty<'K * Tally>) =
        arr (
            FsHotWatch.Core.NonEmpty.toList pairs
            |> List.map (fun (k, t) -> obj [ label, js (name k); "red", ji t.Red; "passed", ji t.Passed ] :> JsonNode)
        )

    let store (s: StoreReport) =
        obj
            [ "path", js s.Source.Path
              "records", ji s.Records
              "unreadable", ji s.Unreadable
              "testRowIndexRuns", ji s.IndexRuns
              "unreadableTestRows", ji s.UnreadableTestRows
              "testRowLogRuns", ji s.LogRuns
              "unreadableTestRowLogFiles", ji s.UnreadableLogFiles
              "unreadableTestRowLogLines", ji s.UnreadableLogLines
              "flaky",
              arr (
                  s.Flaky
                  |> List.map (fun f ->
                      obj
                          [ "grouping", js (Grouping.token Grouping.SameKey)
                            "kind", js f.Kind
                            "subject", js f.Subject
                            "flakyKeys", ji f.FlakyKeys
                            "keys", ji f.Keys
                            "passes", ji f.Passes
                            "fails", ji f.Fails
                            "worktrees", ji f.Worktrees
                            "lastSeen", time f.LastSeen ]
                      :> JsonNode)
              )
              "trust",
              obj
                  [ "reds", obj [ "fails", ji s.Reds.Fails; "laterGreen", ji s.Reds.LaterGreen ]
                    "reuse",
                    arr (
                        s.Reuse
                        |> List.map (fun u ->
                            obj
                                [ "kind", js u.Kind
                                  "keys", ji u.Keys
                                  "records", ji u.Records
                                  "redundantRuns", ji u.RedundantRuns ]
                            :> JsonNode)
                    )
                    "storeDays",
                    arr (
                        s.StoreDays
                        |> List.map (fun d ->
                            obj
                                [ "day", js d.Day
                                  "records", ji d.Records
                                  "fails", ji d.Fails
                                  "laterGreen", ji d.LaterGreen ]
                            :> JsonNode)
                    ) ] ]

    let root =
        obj
            [ "schema", js Schema
              "since",
              (match r.Since with
               | Some s -> time s
               | None -> null)
              "stores",
              arr (
                  FsHotWatch.Core.NonEmpty.toList r.Stores
                  |> List.map (fun s -> store s :> JsonNode)
              )
              "records", ji r.RecordsRead
              "kinds", counts r.Kinds
              "durationsRecorded", ji r.DurationsRecorded
              "testRowRuns", ji r.TestRowRuns
              "flakyTests",
              arr (
                  r.FlakyTests
                  |> List.map (fun f ->
                      obj
                          [ "grouping", js (Grouping.token Grouping.TestTreeTool)
                            "project", js f.Project
                            "test", js f.Test
                            "tree", js f.Tree
                            "tool", js f.Tool
                            "outcomes", counts (f.Outcomes |> List.map (fun (o, n) -> TestRows.Outcome.token o, n))
                            "red", runSeen f.Red
                            "green", runSeen f.Green
                            "sources", origins f.Sources
                            "byStore", tallies "store" (fun (s: StoreSource) -> s.Path) f.ByStore
                            "byMachine", tallies "machine" id f.ByMachine
                            "lastSeen", time f.LastSeen ]
                      :> JsonNode)
              )
              "slow",
              arr (
                  r.Slow
                  |> List.map (fun s ->
                      obj
                          [ "kind", js s.Kind
                            "subject", js s.Subject
                            "samples", ji s.Samples
                            "p50Ms", jl s.P50Ms
                            "p90Ms", jl s.P90Ms
                            "maxMs", jl s.MaxMs ]
                      :> JsonNode)
              )
              "trust",
              obj
                  [ "lookupReads",
                    obj
                        [ "reads", ji r.LookupReads.Reads
                          "satisfied", ji r.LookupReads.Satisfied
                          "missing", ji r.LookupReads.Missing ]
                    "shadowDays",
                    arr (
                        r.ShadowDays
                        |> List.map (fun d ->
                            obj
                                [ "day", js d.Day
                                  "lines", ji d.Lines
                                  "agree", ji d.Agree
                                  "directions",
                                  counts (d.Directions |> List.map (fun (k, n) -> ShadowVerdict.Direction.token k, n)) ]
                            :> JsonNode)
                    )
                    "shadowUnreadable", ji r.ShadowUnreadable
                    "shadowLogs", arr (r.ShadowLogs |> List.map js) ]
              "referencesShadow",
              arr (
                  r.ReferencesShadow
                  |> List.map (fun s -> JsonNode.Parse(FsHotWatch.ReferencesShadowReport.Summary.toJson s))
              )
              "gaps", arr (r.Gaps |> List.map js) ]

    root.ToJsonString()
