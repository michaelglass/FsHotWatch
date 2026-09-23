/// Per-scan resource and orchestration measurements, appended as JSON Lines to
/// `<repoRoot>/.fshw/scan-metrics.jsonl`.
///
/// The memory-retention question behind this module asks for MEASUREMENT, not a knob:
/// "does FCS retain memory across scan generations?" is unanswerable from a log that
/// prints durations. One record per completed scan carries the generation, the
/// orchestration counts that question needs (attempts, retries, unchecked files), and the
/// process/heap footprint sampled at the same instant — so a later run compares
/// against an earlier one by reading the same file, and a fitted slope over the
/// generations is a number rather than an impression.
///
/// The file is append-only and self-describing; a failed write is swallowed
/// (measurement must never take the daemon down) but reported through the return
/// value so a caller that cares can log it.
module FsHotWatch.ScanMetrics

open System
open System.Text.Json

/// Whose resources a sample's RSS and managed figures measure.
[<RequireQualifiedAccess>]
type ResourceScope =
    /// The process is this worktree's own daemon.
    | Process
    /// The process is a repository host serving several worktrees: the figures are
    /// the host's totals, never this worktree's share.
    | Host

/// One completed scan generation's measurement. Field names are the JSON keys.
type ScanSample =
    {
        /// Scan generation this record describes (1 for the first completed scan).
        Generation: int64
        /// `cold` or `forced` — `ScanActivity.ScanKind.describe`.
        Kind: string
        /// Wall-clock duration of the scan, milliseconds.
        DurationMs: float
        /// Files registered with the pipeline when the scan started.
        FilesRegistered: int
        /// Files that produced a check result and were emitted.
        FilesChecked: int
        /// Files still unchecked after the retry budget (see
        /// `Daemon.runChecksWithRetry`): the honest truncation count.
        FilesUnchecked: int
        /// Files of this scan's cohort that were never attempted — the sum of the
        /// two reasons below. `FilesChecked + FilesUnchecked + FilesSkipped` against
        /// `FilesRegistered` is the coverage arithmetic a reader needs to tell a
        /// complete scan from one that published a third of the tree as authoritative.
        FilesSkipped: int
        /// Cohort files dropped because the deps-freshness gate refused their project
        /// (`DepsFreshness.SkipAlreadyAttempted` / `FailFast`).
        FilesDepsGated: int
        /// Cohort files that belonged to NO project in the scan's tiers, so nothing
        /// ever dispatched them. The incremental change path reconciles this category
        /// explicitly (its `uncovered` set) and the scan path does not, so these files
        /// are checked by neither and were counted by nothing — which is how a scan
        /// reported `unchecked 0` while covering a fraction of what it registered.
        FilesUncovered: int
        /// Extra rounds `runChecksWithRetry` needed beyond the first pass,
        /// summed over tiers. 0 on a clean scan; the retry amplification
        /// bounds shows up here.
        RetryRounds: int
        /// Process working set at the end of the scan, bytes.
        RssBytes: int64
        /// Managed heap, bytes (`GC.GetTotalMemory`). `ForcedGc` says whether a
        /// collection preceded the read.
        ManagedBytes: int64
        /// True when a blocking collection was forced before sampling, making
        /// `ManagedBytes` a post-GC (retention) figure rather than a live one.
        ForcedGc: bool
        /// Cumulative gen-2 collections at sample time — lets a reader normalise
        /// a live `ManagedBytes` series by how much GC actually ran.
        Gen2Collections: int
        /// Whose resources `RssBytes` and `ManagedBytes` measure.
        Scope: ResourceScope
        /// UTC sample instant, round-trip ("o") format.
        SampledAt: DateTime
    }

/// The process/heap half of a sample, read at one instant. Split out so the
/// caller assembles a `ScanSample` from counts it owns plus one live read, and
/// so tests can build samples without touching the real process.
type ResourceReading =
    {
        /// Process working set, bytes.
        RssBytes: int64
        /// Managed heap, bytes.
        ManagedBytes: int64
        /// Whether a blocking collection preceded the managed read.
        ForcedGc: bool
        /// Cumulative gen-2 collection count.
        Gen2Collections: int
    }

/// Sample this process now. `forceGc = true` runs a blocking, compacting
/// collection first, so `ManagedBytes` is what the scan RETAINED rather than
/// what it had not yet collected — the figure a fitted retention slope needs. It
/// costs a full GC, which is why it is a parameter and not the default: the
/// daemon forces one only when `FSHW_SCAN_METRICS_GC` asks (see `forceGcEnabled`).
let readResources (forceGc: bool) : ResourceReading =
    if forceGc then
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()

    // `Environment.WorkingSet`, not `Process.GetCurrentProcess().WorkingSet64`:
    // the same number for this process, without a disposable handle to own on a
    // path that runs after every scan.
    { RssBytes = Environment.WorkingSet
      ManagedBytes = GC.GetTotalMemory(false)
      ForcedGc = forceGc
      Gen2Collections = GC.CollectionCount(2) }

/// Whether the daemon should force a collection before sampling. Opt-in via
/// `FSHW_SCAN_METRICS_GC=1`: a forced full GC per scan is exactly what a
/// retention EXPERIMENT wants and exactly what a working daemon does not.
let forceGcEnabled (getEnv: string -> string) : bool =
    match getEnv "FSHW_SCAN_METRICS_GC" with
    | null -> false
    | value -> value.Trim() = "1" || value.Trim().ToLowerInvariant() = "true"

/// Whether a scan in `scope` forces a collection before sampling. Never in a host: a
/// forced full GC there pauses every sibling session to measure one of them.
let forcesGc (scope: ResourceScope) (getEnv: string -> string) : bool =
    scope = ResourceScope.Process && forceGcEnabled getEnv

/// Path of the JSON Lines record for a repository.
let recordPath (repoRoot: string) : string =
    System.IO.Path.Combine(repoRoot, ".fshw", "scan-metrics.jsonl")

/// Render one sample as a single JSON line (no trailing newline).
let toJsonLine (sample: ScanSample) : string =
    JsonSerializer.Serialize(
        {| generation = sample.Generation
           kind = sample.Kind
           durationMs = sample.DurationMs
           filesRegistered = sample.FilesRegistered
           filesChecked = sample.FilesChecked
           filesUnchecked = sample.FilesUnchecked
           filesSkipped = sample.FilesSkipped
           filesDepsGated = sample.FilesDepsGated
           filesUncovered = sample.FilesUncovered
           retryRounds = sample.RetryRounds
           rssBytes = sample.RssBytes
           managedBytes = sample.ManagedBytes
           forcedGc = sample.ForcedGc
           gen2Collections = sample.Gen2Collections
           scope =
            match sample.Scope with
            | ResourceScope.Process -> "process"
            | ResourceScope.Host -> "host"
           sampledAt = sample.SampledAt.ToString("o") |}
    )

/// Parse one JSON line back into a sample. `None` for blank lines, malformed
/// JSON, or a record missing a field — a half-written last line (the daemon was
/// killed mid-append) must not poison a whole series.
// MGA-ERROR-REPORT-001:ok — a malformed line is a normal input here, reported as
// `None` rather than thrown; the caller drops it and keeps the rest of the series.
let tryParseLine (line: string) : ScanSample option =
    if String.IsNullOrWhiteSpace line then
        None
    else
        try
            use doc = JsonDocument.Parse(line)
            let root = doc.RootElement
            // A missing key throws here and is caught below as `None`: the
            // record is all-or-nothing, so a partial line is not half a sample.
            let field (name: string) = root.GetProperty(name)

            // Fields added after this ledger was already accumulating records. ABSENT
            // must mean "not recorded", not "unparseable": every line written before
            // they existed is still evidence, and a series that silently dropped its
            // own history would be worse than one that never had these counts.
            let optionalInt (name: string) =
                match root.TryGetProperty name with
                | true, value -> value.GetInt32()
                | false, _ -> 0

            Some
                { Generation = (field "generation").GetInt64()
                  Kind = (field "kind").GetString()
                  DurationMs = (field "durationMs").GetDouble()
                  FilesRegistered = (field "filesRegistered").GetInt32()
                  FilesChecked = (field "filesChecked").GetInt32()
                  FilesUnchecked = (field "filesUnchecked").GetInt32()
                  FilesSkipped = optionalInt "filesSkipped"
                  FilesDepsGated = optionalInt "filesDepsGated"
                  FilesUncovered = optionalInt "filesUncovered"
                  RetryRounds = (field "retryRounds").GetInt32()
                  RssBytes = (field "rssBytes").GetInt64()
                  ManagedBytes = (field "managedBytes").GetInt64()
                  ForcedGc = (field "forcedGc").GetBoolean()
                  Gen2Collections = (field "gen2Collections").GetInt32()
                  Scope =
                    match root.TryGetProperty "scope" with
                    | true, value when value.GetString() = "host" -> ResourceScope.Host
                    | _ -> ResourceScope.Process
                  // RoundtripKind, not the default: without it the "o" string's
                  // trailing Z is applied and then the value is converted to LOCAL
                  // time, so a series written in one timezone reads back shifted.
                  SampledAt =
                    DateTime.Parse(
                        (field "sampledAt").GetString(),
                        Globalization.CultureInfo.InvariantCulture,
                        Globalization.DateTimeStyles.RoundtripKind
                    ) }
        with _ ->
            None

/// Parse a whole JSON Lines document, dropping unparseable lines.
let parseSeries (text: string) : ScanSample list =
    text.Split('\n') |> Array.toList |> List.choose tryParseLine

/// Append one sample to `path`, creating the directory. Returns the failure
/// message rather than throwing — a daemon must not die because it could not
/// write a measurement.
// MGA-ERROR-REPORT-001:ok — the failure is the returned `Error`, deliberately
// non-fatal: measurement is diagnostic and never load-bearing for a verdict.
let tryAppend (path: string) (sample: ScanSample) : Result<unit, string> =
    try
        let dir = System.IO.Path.GetDirectoryName(path: string)

        if not (String.IsNullOrEmpty dir) then
            System.IO.Directory.CreateDirectory(dir) |> ignore

        System.IO.File.AppendAllText(path, toJsonLine sample + "\n")
        Ok()
    with ex ->
        Result.Error(ex.Message)

/// Read a series back from disk. Missing file → empty (nothing measured yet).
// MGA-ERROR-REPORT-001:ok — an unreadable metrics file yields an empty series,
// which every caller already handles as "not enough generations to judge".
let readSeries (path: string) : ScanSample list =
    try
        if System.IO.File.Exists path then
            parseSeries (System.IO.File.ReadAllText path)
        else
            []
    with _ ->
        []

/// The verdict of a retention fit over a series of generations.
[<RequireQualifiedAccess>]
type RetentionVerdict =
    /// Fewer than two generations — nothing to fit. Carries the count seen.
    | NotEnoughData of generations: int
    /// Slope, as a FRACTION of the first generation's RSS per generation, is at
    /// or below the bound.
    | WithinBound of slopePerGeneration: float * bound: float
    /// Slope exceeds the bound. Not proof of a leak — the raw series is the
    /// evidence, and this only says the fit crossed the line that was set.
    | ExceedsBound of slopePerGeneration: float * bound: float

/// Least-squares slope of `RssBytes` against generation index, expressed as a
/// fraction of the FIRST sample's RSS per generation, judged against `bound`
/// (sets 0.10 = 10% per generation).
///
/// A ratio rather than raw bytes because the acceptance criterion is relative
/// growth, and because it makes runs on different machines comparable.
let fitRetention (bound: float) (samples: ScanSample list) : RetentionVerdict =
    match samples with
    | [] -> RetentionVerdict.NotEnoughData 0
    | [ _ ] -> RetentionVerdict.NotEnoughData 1
    | first :: _ ->
        let points = samples |> List.mapi (fun i s -> float i, float s.RssBytes)
        let meanX = points |> List.averageBy fst
        let meanY = points |> List.averageBy snd

        let covariance = points |> List.sumBy (fun (x, y) -> (x - meanX) * (y - meanY))

        // Variance is strictly positive here: `points` has at least two entries
        // (one and none are `NotEnoughData` above) and its x values are the
        // distinct indices 0, 1, 2, … — so there is no zero-variance case to
        // guard, and a guard for one would be untestable by construction.
        let variance = points |> List.sumBy (fun (x, _) -> (x - meanX) * (x - meanX))

        let slopeBytes = covariance / variance

        let baseline = float first.RssBytes

        let ratio = if baseline = 0.0 then 0.0 else slopeBytes / baseline

        if ratio <= bound then
            RetentionVerdict.WithinBound(ratio, bound)
        else
            RetentionVerdict.ExceedsBound(ratio, bound)

/// The bound specifies: post-GC RSS growth per generation no
/// greater than 10% of the first generation's post-GC RSS.
[<Literal>]
let DefaultRetentionBound = 0.10
