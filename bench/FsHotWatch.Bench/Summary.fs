/// Turning a record series into the numbers the repository-host budgets are stated in.
///
/// The headline is the N-session ratio: for one label and phase, the median over
/// repetitions of the SUMMED footprint of all N sessions, divided by the same median for
/// one session. Today's per-worktree daemons should sit near N; the redesign's target is
/// at most 1.75 for four sessions.
///
/// Invalid records are never scored. Contended records are not scored either unless the
/// caller explicitly allows it, and a summary built from them says so on every line.
module FsHotWatch.Bench.Summary

open System

/// Nearest-rank percentile of a non-empty list (`p` in 0..100).
let percentile (p: float) (values: float list) : float option =
    match List.sort values with
    | [] -> None
    | sorted ->
        let n = List.length sorted
        let rank = int (Math.Ceiling(p / 100.0 * float n))
        Some(sorted.[max 0 (min (n - 1) (rank - 1))])

/// The median (50th nearest-rank percentile).
let median (values: float list) = percentile 50.0 values

/// One (label, phase, sessions) group's scores.
type Group =
    {
        Label: string
        Phase: string
        Sessions: int
        /// Reps for which every one of the N sessions has a footprint.
        CompleteReps: int
        /// Per-session footprint, median and p95, bytes.
        SessionFootprintMedian: float option
        SessionFootprintP95: float option
        /// Summed footprint across the N sessions of one rep: median over reps.
        TotalFootprintMedian: float option
        /// Lifetime peak per session, max over the group.
        PeakMax: float option
        ManagedMedian: float option
        NativeMedian: float option
        ManagedLiveMedian: float option
        ShareableLowMedian: float option
        ShareableHighMedian: float option
        /// Heap-graph shareable fraction (import-only + overlap), trusted readings only.
        RetentionHighMedian: float option
        RetentionTypedTreeHighMedian: float option
        PhaseMsMedian: float option
        PhaseMsP95: float option
        /// Distinct `filesChecked` values seen: more than one means sessions did not
        /// check the same work, and the group's memory is not comparable.
        FilesCheckedSeen: int list
        /// Distinct test totals seen, for the same reason.
        TestsTotalSeen: int list
    }

/// The scored summary plus what was left out and why.
type Report =
    {
        Groups: Group list
        /// `(label, phase, N) → ratio` of total footprint to the 1-session total.
        Ratios: ((string * string * int) * float) list
        /// `(phase, N) → host total / legacy total`, medians over repetitions, for every
        /// phase and session count both modes measured.
        HostVsLegacy: ((string * int) * float) list
        ExcludedInvalid: int
        ExcludedContended: int
        ContendedIncluded: bool
    }

let private floats (f: Record.Row -> 'a option) (conv: 'a -> float) (rows: Record.Row list) =
    rows |> List.choose f |> List.map conv

/// Build the report. `allowContended = false` drops contended rows.
let summarize (allowContended: bool) (rows: Record.Row list) : Report =
    let invalid, valid = rows |> List.partition (fun r -> not (List.isEmpty r.Invalid))

    let contended, scored =
        if allowContended then
            [], valid
        else
            valid |> List.partition _.Contended

    let groups =
        scored
        |> List.groupBy (fun r -> r.Label, r.Position.Phase, r.Position.Sessions)
        |> List.sortBy fst
        |> List.map (fun ((label, phase, sessions), rs) ->
            let perRep =
                rs
                |> List.groupBy (fun r -> r.RunId, r.Position.Rep)
                |> List.choose (fun (_, repRows) ->
                    // Host mode: the session-0 record measured the one process that
                    // serves all N sessions, so it IS the total.
                    match repRows |> List.tryFind (fun r -> r.Position.Session = 0) with
                    | Some host -> host.PhysFootprint |> Option.map float
                    | None ->
                        let fps = repRows |> List.choose _.PhysFootprint

                        if List.length fps = sessions then
                            Some(float (List.sum fps))
                        else
                            None)

            // Per-SESSION footprint only means something for per-session processes.
            let fp =
                floats _.PhysFootprint float (rs |> List.filter (fun r -> r.Position.Session > 0))

            let ms = floats _.PhaseMs id rs

            { Label = label
              Phase = phase
              Sessions = sessions
              CompleteReps = List.length perRep
              SessionFootprintMedian = median fp
              SessionFootprintP95 = percentile 95.0 fp
              TotalFootprintMedian = median perRep
              PeakMax =
                match floats _.PhysFootprintPeak float rs with
                | [] -> None
                | peaks -> Some(List.max peaks)
              ManagedMedian = median (floats _.Managed float rs)
              NativeMedian = median (floats _.Native float rs)
              ManagedLiveMedian = median (floats _.ManagedLive float rs)
              ShareableLowMedian = median (floats _.ShareableLow id rs)
              ShareableHighMedian = median (floats _.ShareableHigh id rs)
              RetentionHighMedian = median (floats _.RetentionHigh id rs)
              RetentionTypedTreeHighMedian = median (floats _.RetentionTypedTreeHigh id rs)
              PhaseMsMedian = median ms
              PhaseMsP95 = percentile 95.0 ms
              FilesCheckedSeen = rs |> List.choose _.FilesChecked |> List.distinct |> List.sort
              TestsTotalSeen = rs |> List.choose _.TestsTotal |> List.distinct |> List.sort })

    let ratios =
        groups
        |> List.choose (fun g ->
            let baseline =
                groups
                |> List.tryFind (fun b -> b.Label = g.Label && b.Phase = g.Phase && b.Sessions = 1)
                |> Option.bind _.TotalFootprintMedian

            match baseline, g.TotalFootprintMedian with
            | Some b, Some t when b > 0.0 -> Some((g.Label, g.Phase, g.Sessions), t / b)
            | _ -> None)

    let modeOf =
        scored |> List.map (fun r -> r.Label, r.Mode) |> List.distinct |> Map.ofList

    let totalsFor mode =
        groups
        |> List.filter (fun g -> modeOf |> Map.tryFind g.Label = Some mode)
        |> List.choose (fun g -> g.TotalFootprintMedian |> Option.map (fun t -> (g.Phase, g.Sessions), t))

    let hostVsLegacy =
        [ for key, hostTotal in totalsFor "host" do
              for legacyKey, legacyTotal in totalsFor "legacy" do
                  if key = legacyKey && legacyTotal > 0.0 then
                      yield key, hostTotal / legacyTotal ]

    { Groups = groups
      HostVsLegacy = hostVsLegacy
      Ratios = ratios
      ExcludedInvalid = List.length invalid
      ExcludedContended = List.length contended
      ContendedIncluded = allowContended && valid |> List.exists _.Contended }

let private mb (v: float option) =
    match v with
    | Some b -> $"%.0f{b / 1048576.0}"
    | None -> "-"

let private pct (v: float option) =
    match v with
    | Some f -> $"%.0f{f * 100.0}%%"
    | None -> "-"

let private secs (v: float option) =
    match v with
    | Some ms -> $"%.1f{ms / 1000.0}"
    | None -> "-"

/// Human-readable table (MB, seconds).
let render (report: Report) : string =
    let lines =
        [ if report.ContendedIncluded then
              yield "WARNING: contended samples INCLUDED — these numbers are not comparable with a quiet-box run."
          yield $"excluded: %d{report.ExcludedInvalid} invalid, %d{report.ExcludedContended} contended"
          yield ""
          yield
              "label | phase | N | reps | fp/session med | p95 | total med | x of N=1 | peak max | managed | native | live | shareable by type lo-hi | shareable by graph (typed tree) | phase s med/p95 | files | tests"
          for g in report.Groups do
              let ratio =
                  report.Ratios
                  |> List.tryFind (fun (k, _) -> k = (g.Label, g.Phase, g.Sessions))
                  |> Option.map (fun (_, r) -> $"%.2f{r}")
                  |> Option.defaultValue "-"

              let seen (xs: int list) =
                  if List.isEmpty xs then
                      "-"
                  else
                      xs |> List.map string |> String.concat "/"

              yield
                  $"%s{g.Label} | %s{g.Phase} | %d{g.Sessions} | %d{g.CompleteReps} | %s{mb g.SessionFootprintMedian} | %s{mb g.SessionFootprintP95} | %s{mb g.TotalFootprintMedian} | %s{ratio} | %s{mb g.PeakMax} | %s{mb g.ManagedMedian} | %s{mb g.NativeMedian} | %s{mb g.ManagedLiveMedian} | %s{pct g.ShareableLowMedian}-%s{pct g.ShareableHighMedian} | %s{pct g.RetentionHighMedian} (%s{pct g.RetentionTypedTreeHighMedian}) | %s{secs g.PhaseMsMedian}/%s{secs g.PhaseMsP95} | %s{seen g.FilesCheckedSeen} | %s{seen g.TestsTotalSeen}" ]

    let comparison =
        if List.isEmpty report.HostVsLegacy then
            []
        else
            [ yield ""
              yield "host / legacy total footprint (median over reps), same phase and session count:"
              for (phase, n), ratio in report.HostVsLegacy do
                  yield $"  %s{phase} N=%d{n}: %.2f{ratio}" ]

    String.Join("\n", lines @ comparison)
