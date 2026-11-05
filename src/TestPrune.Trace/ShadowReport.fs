/// The phase-2 promotion bar (D5 of the phase-2 plan) over one project's shadow rows,
/// verified-failure misses and mutation samples. Every bar is a measured value against a
/// threshold; the report passes only when every bar does, and no bar passes on no data.
module TestPrune.Trace.ShadowReport

open System
open System.Globalization
open TestPrune.Trace.TraceStore

/// The thresholds.
type Bars =
    {
        /// The window spans at least this many days between its first and last row.
        MinDays: float
        MinImpactRuns: int
        MinFullRuns: int
        /// Measured symbol samples.
        MinMutationSamples: int
        /// Measured union-type samples.
        MinUnionTypeSamples: int
        /// How many union types the project has whose mutant fails a test, when known: the
        /// union-type bar is then the smaller of this and `MinUnionTypeSamples`.
        UnionTypeCandidates: int option
        MinEligibleShare: float
        /// Safe fingerprint misses, as a share of the rows that recorded traces.
        MaxSafeMissShare: float
        MaxSelectMsP95: int64
        /// Median of trace-launched over static-launched tests, on impact rows.
        MaxLaunchRatio: float
    }

/// D5's thresholds.
let defaults =
    { MinDays = 14.0
      MinImpactRuns = 100
      MinFullRuns = 10
      MinMutationSamples = 50
      MinUnionTypeSamples = 5
      UnionTypeCandidates = None
      MinEligibleShare = 0.95
      MaxSafeMissShare = 0.05
      MaxSelectMsP95 = 1000L
      MaxLaunchRatio = 0.5 }

/// One bar's verdict.
type BarResult =
    { Name: string
      Measured: string
      Threshold: string
      Pass: bool }

let private result name measured threshold pass =
    { Name = name
      Measured = measured
      Threshold = threshold
      Pass = pass }

let private inv (x: float) =
    x.ToString("0.###", CultureInfo.InvariantCulture)

/// Nearest-rank percentile of a non-empty list.
let private nearestRank (p: float) (xs: 'a list) =
    let sorted = List.sort xs
    let rank = int (ceil (p * float sorted.Length))
    sorted.[rank - 1]

let private median (xs: float list) =
    match List.sort xs with
    | [] -> None
    | sorted when sorted.Length % 2 = 1 -> Some sorted.[sorted.Length / 2]
    | sorted -> Some((sorted.[sorted.Length / 2 - 1] + sorted.[sorted.Length / 2]) / 2.0)

/// `r/t`, or `None` when the recall was not measurable.
let private parseRecall (text: string) =
    match text.Split '/' |> List.ofArray |> List.map Int32.TryParse with
    | [ (true, r); (true, t) ] -> Some(r, t)
    | _ -> None

let private window (b: Bars) (rows: ShadowRow list) =
    let impact = rows |> List.filter (fun r -> r.Mode = ImpactRun)
    let full = rows |> List.filter (fun r -> r.Mode = FullSuiteRun)

    let days =
        match rows with
        | [] -> 0.0
        | _ ->
            let times = rows |> List.map _.RecordedAt
            (List.max times - List.min times).TotalDays

    let notEveryRun =
        impact |> List.filter (fun r -> r.RecordPolicy <> "every-run") |> List.length

    result
        "window"
        $"%d{impact.Length} impact, %d{full.Length} full over %s{inv days} days; %d{notEveryRun} impact rows not under every-run"
        $">= %d{b.MinImpactRuns} impact, >= %d{b.MinFullRuns} full, >= %s{inv b.MinDays} days, all every-run"
        (impact.Length >= b.MinImpactRuns
         && full.Length >= b.MinFullRuns
         && days >= b.MinDays
         && notEveryRun = 0)

let private verifiedFailures (rows: ShadowRow list) (misses: (string * string) list) =
    let eligibleRun =
        rows
        |> List.map (fun r -> r.RunId, r.Eligible = Some true)
        |> List.distinctBy fst
        |> Map.ofList

    let inWindow = misses |> List.filter (fst >> eligibleRun.ContainsKey)
    let counted, listed = inWindow |> List.partition (fun (run, _) -> eligibleRun.[run])

    let names (xs: (string * string) list) =
        xs |> List.map (fun (run, key) -> $"%s{key} (%s{run})") |> String.concat ", "

    let measured =
        match counted, listed with
        | [], [] -> "0"
        | _ ->
            $"%d{counted.Length} on eligible rows: [%s{names counted}]; %d{listed.Length} on ineligible rows: [%s{names listed}]"

    result "verified-failure misses" measured "0 on eligible rows" counted.IsEmpty

let private recall (rows: ShadowRow list) =
    let full = rows |> List.filter (fun r -> r.Mode = FullSuiteRun)
    let parsed = full |> List.map (fun r -> r, parseRecall r.TraceRecall)
    let measured = parsed |> List.choose (fun (r, p) -> p |> Option.map (fun p -> r, p))
    let below = measured |> List.filter (fun (_, (r, t)) -> r <> t)
    let unmeasurable = parsed |> List.filter (snd >> Option.isNone) |> List.length

    let belowText =
        below
        |> List.map (fun (row, (r, t)) -> $"%s{row.RunId} %d{r}/%d{t}")
        |> String.concat ", "

    result
        "trace recall on full runs"
        $"%d{measured.Length} measured, %d{below.Length} below 1 [%s{belowText}], %d{unmeasurable} not measurable"
        "every measured value r/r, at least one measured"
        (not measured.IsEmpty && below.IsEmpty)

let private mutation (b: Bars) (samples: MutationSample list) =
    let measured kind =
        samples
        |> List.filter (fun s -> s.Kind = kind && s.Outcome = Measured)
        |> List.length

    let symbols = measured SymbolMutant
    let unions = measured UnionTypeMutant

    let unionBar =
        match b.UnionTypeCandidates with
        | Some n -> min n b.MinUnionTypeSamples
        | None -> b.MinUnionTypeSamples

    let missed = samples |> List.sumBy (fun s -> s.Missed.Length)

    result
        "mutation"
        $"%d{symbols} measured symbol, %d{unions} measured union-type, %d{missed} missed tests"
        $">= %d{b.MinMutationSamples} symbol, >= %d{unionBar} union-type, 0 missed"
        (symbols >= b.MinMutationSamples && unions >= unionBar && missed = 0)

let private eligibility (b: Bars) (rows: ShadowRow list) =
    let eligible = rows |> List.filter (fun r -> r.Eligible = Some true) |> List.length

    let share =
        if rows.IsEmpty then
            0.0
        else
            float eligible / float rows.Length

    result
        "eligibility"
        $"%d{eligible}/%d{rows.Length} rows eligible (%s{inv share})"
        $">= %s{inv b.MinEligibleShare}"
        (not rows.IsEmpty && share >= b.MinEligibleShare)

let private fingerprint (b: Bars) (rows: ShadowRow list) =
    let count o =
        rows |> List.filter (fun r -> r.FingerprintOutcome = o) |> List.length

    let hits, safe, unsafe = count PredictionHit, count SafeMiss, count UnsafeMiss
    let traced = hits + safe + unsafe

    let safeShare = if traced = 0 then 0.0 else float safe / float traced

    result
        "fingerprint prediction"
        $"%d{traced} traced rows: %d{hits} hit, %d{safe} safe-miss (%s{inv safeShare}), %d{unsafe} unsafe-miss"
        $"0 unsafe-miss, safe-miss <= %s{inv b.MaxSafeMissShare}"
        (traced > 0 && unsafe = 0 && safeShare <= b.MaxSafeMissShare)

let private latency (b: Bars) (rows: ShadowRow list) =
    match rows with
    | [] -> result "latency" "no rows" $"p95 <= %d{b.MaxSelectMsP95} ms" false
    | _ ->
        let p95 = rows |> List.map _.SelectMs |> nearestRank 0.95
        result "latency" $"p95 %d{p95} ms" $"p95 <= %d{b.MaxSelectMsP95} ms" (p95 <= b.MaxSelectMsP95)

let private payoff (b: Bars) (rows: ShadowRow list) =
    let impact =
        rows |> List.filter (fun r -> r.Mode = ImpactRun && r.StaticClassTests > 0)

    let classRatio =
        impact
        |> List.map (fun r -> float r.TraceClassTests / float r.StaticClassTests)
        |> median

    let methodRatio =
        impact
        |> List.filter (fun r -> r.StaticTests > 0)
        |> List.map (fun r -> float r.TraceTests / float r.StaticTests)
        |> median

    let text (x: float option) =
        x |> Option.map inv |> Option.defaultValue "n/a"

    result
        "payoff"
        $"median class-level ratio %s{text classRatio} over %d{impact.Length} impact rows; method-level %s{text methodRatio}"
        $"class-level median <= %s{inv b.MaxLaunchRatio}"
        (classRatio |> Option.exists (fun r -> r <= b.MaxLaunchRatio))

/// Every bar, in D5's order.
let evaluate
    (b: Bars)
    (rows: ShadowRow list)
    (misses: (string * string) list)
    (samples: MutationSample list)
    : BarResult list =
    [ window b rows
      verifiedFailures rows misses
      recall rows
      mutation b samples
      eligibility b rows
      fingerprint b rows
      latency b rows
      payoff b rows ]

/// The report passes when every bar does.
let passes (results: BarResult list) = results |> List.forall _.Pass

/// The rows recorded at or after `from`; every row when `None`.
let since (from: DateTimeOffset option) (rows: ShadowRow list) =
    match from with
    | None -> rows
    | Some t -> rows |> List.filter (fun r -> r.RecordedAt >= t)

/// The Markdown table of bars, then the untraced-children decision point (plan, review
/// Q2), which never affects `passes`.
let render (results: BarResult list) (rows: ShadowRow list) : string =
    let cell (s: string) = s.Replace("|", "\\|")

    let row (r: BarResult) =
        let verdict = if r.Pass then "PASS" else "FAIL"
        $"| %s{r.Name} | %s{cell r.Measured} | %s{cell r.Threshold} | %s{verdict} |"

    let table =
        [ "| Name | Measured | Threshold | Verdict |"; "|---|---|---|---|" ]
        @ List.map row results

    let children =
        rows
        |> List.filter (fun r -> r.Mode = ImpactRun && r.TraceClassTests > 0)
        |> List.map (fun r -> float r.ChildUntracedTests / float r.TraceClassTests)
        |> median

    let childLine =
        match children with
        | None -> "untraced children: no impact row launched a traced test"
        | Some m when m > 0.05 -> $"untraced children: median share %s{inv m} > 0.05: allowlist task due"
        | Some m -> $"untraced children: median share %s{inv m} <= 0.05: no allowlist task"

    let verdict = if passes results then "PASS" else "FAIL"

    String.concat "\n" (table @ [ ""; childLine; ""; $"shadow-report: %s{verdict}" ])
    + "\n"
