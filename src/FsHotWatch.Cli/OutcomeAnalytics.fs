/// `fshw analytics` (AUTOMATION-1368): flaky, slow and trust, as queries over the one
/// outcome store (ADR-059 §2) and the shadow ledger (ADR-059 §7).
///
/// A pure read. It contacts no daemon, triggers no run, and changes neither the store (it
/// reads with `OutcomeStore.readAll`, which quarantines nothing) nor any ledger.
///
/// It reports only what the store records. The store files one outcome per action — per
/// test PROJECT run, not per test — so flaky and slow are per project. A metric the data
/// cannot answer is named in `Gaps`, never reported as a zero.
///
/// Definitions:
///   * flaky: one action key (the same inputs) recorded both pass and fail;
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
let Schema = "fshw-analytics-v1"

/// One line of `.fshw/shadow/lookup-verdict.jsonl`, reduced to what analytics reads.
type ShadowLine =
    {
        At: DateTimeOffset
        Command: string
        Direction: string
        /// The lookup's class: `green`, `red` or `missing`.
        Lookup: string
    }

type ShadowRead =
    {
        Lines: ShadowLine list
        /// Lines that are not `fshw-shadow-v1` lines.
        UnreadableLines: int
        /// The logs that exist and were read.
        Logs: string list
    }

/// One subject's input sets that were recorded both green and red.
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
      Directions: (string * int) list }

type LookupReads =
    { Reads: int
      Satisfied: int
      Missing: int }

type Report =
    { Since: DateTimeOffset option
      RecordsRead: int
      UnreadableRecords: int
      Kinds: (string * int) list
      DurationsRecorded: int
      Flaky: Flaky list
      Slow: Slow list
      Reds: Reds
      Reuse: Reuse list
      StoreDays: StoreDay list
      ShadowDays: ShadowDay list
      ShadowUnreadable: int
      ShadowLogs: string list
      LookupReads: LookupReads
      Gaps: string list }

/// A shadow log line, or `None` for anything that is not a complete `fshw-shadow-v1` line.
let parseShadowLine (text: string) : ShadowLine option =
    try
        use doc = JsonDocument.Parse text
        let o = doc.RootElement

        match
            Json.tryString o "schema", Json.tryString o "at", Json.tryString o "command", Json.tryString o "direction"
        with
        | Some "fshw-shadow-v1", Some at, Some command, Some direction ->
            let lookup =
                Json.tryProp o "lookup"
                |> Option.bind (fun l -> Json.tryString l "outcome")
                |> Option.defaultValue "missing"

            match
                DateTimeOffset.TryParse(
                    at,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.None
                )
            with
            | true, parsed ->
                Some
                    { At = parsed
                      Command = command
                      Direction = direction
                      Lookup = lookup }
            | false, _ -> None
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

let private gaps (durations: int) (records: int) (shadow: ShadowRead) (lookupReads: int) =
    [ yield
          "flaky and slow are per test project, not per test: a record holds one outcome per project run. \
           Smallest addition: file the failed test ids in a fail record's `outputs`."
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

/// The report over `scan`'s records and `shadow`'s lines, both limited to `since` when given.
let analyze (since: DateTimeOffset option) (scan: OutcomeStore.Scan) (shadow: ShadowRead) : Report =
    let inWindow (at: DateTimeOffset) =
        since |> Option.forall (fun s -> at >= s)

    let records = scan.Records |> List.filter (fun r -> inWindow r.RecordedAt)
    let lines = shadow.Lines |> List.filter (fun l -> inWindow l.At)

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

    let shadowDays =
        lines
        |> List.groupBy (fun l -> day l.At)
        |> List.sortBy fst
        |> List.map (fun (d, ls) ->
            { ShadowDay.Day = d
              Lines = ls.Length
              Agree = ls |> List.filter (fun l -> l.Direction = "agree") |> List.length
              Directions = ls |> List.countBy _.Direction |> List.sortBy fst })

    let reads = lines |> List.filter (fun l -> l.Command = "verdict")
    let missing = reads |> List.filter (fun l -> l.Lookup = "missing") |> List.length

    let durations = records |> List.filter (fun r -> r.DurationMs.IsSome) |> List.length

    { Since = since
      RecordsRead = records.Length
      UnreadableRecords = scan.Unreadable
      Kinds = records |> List.countBy _.Key.Kind |> List.sortBy fst
      DurationsRecorded = durations
      Flaky = flaky
      Slow = slow
      Reds =
        { Fails = fails.Length
          LaterGreen = fails |> List.filter (fun r -> laterGreen (historyOf r) r) |> List.length }
      Reuse = reuse
      StoreDays = storeDays
      ShadowDays = shadowDays
      ShadowUnreadable = shadow.UnreadableLines
      ShadowLogs = shadow.Logs
      LookupReads =
        { Reads = reads.Length
          Satisfied = reads.Length - missing
          Missing = missing }
      Gaps = gaps durations records.Length shadow reads.Length }

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

/// The human report; `top` bounds the flaky and slow lists.
let render (top: int) (r: Report) : string list =
    [ let window =
          match r.Since with
          | Some s -> $" since %s{stamp s}"
          | None -> ""

      let kinds =
          match r.Kinds with
          | [] -> ""
          | kinds ->
              let named = kinds |> List.map (fun (k, n) -> $"%s{k} %d{n}") |> String.concat ", "
              $" (%s{named})"

      yield $"Outcome store: %d{r.RecordsRead} records%s{window}%s{kinds}; %d{r.UnreadableRecords} unreadable"
      yield ""
      yield "Flaky (one input set recorded both pass and fail):"

      match r.Flaky with
      | [] -> yield "  no input set recorded both outcomes"
      | flaky ->
          for f in List.truncate top flaky do
              yield
                  $"  %s{f.Subject} [%s{f.Kind}]: %d{f.FlakyKeys} of %d{f.Keys} input sets; %d{f.Passes} pass / %d{f.Fails} fail; \
                    %d{f.Worktrees} checkout(s); last %s{stamp f.LastSeen}"

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
      yield $"  reds later green on identical inputs: %s{rate r.Reds.LaterGreen r.Reds.Fails}"

      for u in r.Reuse do
          yield $"  runs a lookup would have answered: %s{rate u.RedundantRuns u.Records} [%s{u.Kind}, %d{u.Keys} keys]"

      yield $"  verdict reads satisfied by the store: %s{rate r.LookupReads.Satisfied r.LookupReads.Reads}"

      for d in r.StoreDays do
          yield $"  %s{d.Day} store: %d{d.Records} records, %d{d.Fails} fail, %d{d.LaterGreen} later green"

      let total = r.ShadowDays |> List.sumBy _.Lines
      let agree = r.ShadowDays |> List.sumBy _.Agree

      yield
          $"  shadow ledger: agree %s{rate agree total} over %d{r.ShadowLogs.Length} log(s); %d{r.ShadowUnreadable} unreadable line(s)"

      for d in r.ShadowDays do
          let directions =
              d.Directions |> List.map (fun (k, n) -> $"%s{k} %d{n}") |> String.concat ", "

          yield $"  %s{d.Day} shadow: agree %d{d.Agree}/%d{d.Lines} (%s{directions})"

      yield ""
      yield "Not recorded:"

      for g in r.Gaps do
          yield $"  - %s{g}" ]

/// The report as one `fshw-analytics-v1` JSON object.
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

    let root =
        obj
            [ "schema", js Schema
              "since",
              (match r.Since with
               | Some s -> time s
               | None -> null)
              "records", ji r.RecordsRead
              "unreadable", ji r.UnreadableRecords
              "kinds", counts r.Kinds
              "durationsRecorded", ji r.DurationsRecorded
              "flaky",
              arr (
                  r.Flaky
                  |> List.map (fun f ->
                      obj
                          [ "kind", js f.Kind
                            "subject", js f.Subject
                            "flakyKeys", ji f.FlakyKeys
                            "keys", ji f.Keys
                            "passes", ji f.Passes
                            "fails", ji f.Fails
                            "worktrees", ji f.Worktrees
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
                  [ "reds", obj [ "fails", ji r.Reds.Fails; "laterGreen", ji r.Reds.LaterGreen ]
                    "reuse",
                    arr (
                        r.Reuse
                        |> List.map (fun u ->
                            obj
                                [ "kind", js u.Kind
                                  "keys", ji u.Keys
                                  "records", ji u.Records
                                  "redundantRuns", ji u.RedundantRuns ]
                            :> JsonNode)
                    )
                    "lookupReads",
                    obj
                        [ "reads", ji r.LookupReads.Reads
                          "satisfied", ji r.LookupReads.Satisfied
                          "missing", ji r.LookupReads.Missing ]
                    "storeDays",
                    arr (
                        r.StoreDays
                        |> List.map (fun d ->
                            obj
                                [ "day", js d.Day
                                  "records", ji d.Records
                                  "fails", ji d.Fails
                                  "laterGreen", ji d.LaterGreen ]
                            :> JsonNode)
                    )
                    "shadowDays",
                    arr (
                        r.ShadowDays
                        |> List.map (fun d ->
                            obj
                                [ "day", js d.Day
                                  "lines", ji d.Lines
                                  "agree", ji d.Agree
                                  "directions", counts d.Directions ]
                            :> JsonNode)
                    )
                    "shadowUnreadable", ji r.ShadowUnreadable
                    "shadowLogs", arr (r.ShadowLogs |> List.map js) ]
              "gaps", arr (r.Gaps |> List.map js) ]

    root.ToJsonString()
