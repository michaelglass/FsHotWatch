module FsHotWatch.Tests.ScanMetricsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ScanMetrics
open FsHotWatch.Tests.TestHelpers

// The scan metrics exist as measurement that a LATER RUN CAN COMPARE. That makes
// the record's round-trip — emitted, re-read, and fitted — the property under
// test, not the fact that some number was logged.

let private sample generation rss =
    { Generation = generation
      Kind = "cold"
      DurationMs = 1234.5
      FilesRegistered = 1552
      FilesChecked = 1552
      FilesUnchecked = 0
      FilesSkipped = 0
      FilesDepsGated = 0
      FilesUncovered = 0
      RetryRounds = 0
      RssBytes = rss
      ManagedBytes = rss / 4L
      ForcedGc = true
      Gen2Collections = 3
      DirectSpawns = 12L
      HelperSpawns = 30L
      Scope = DaemonHosting.ResourceScope.Process
      SampledAt = DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc) }

[<Fact>]
let ``a sample round-trips through its JSON line`` () =
    let original = sample 3L 4_000_000_000L

    let parsed = toJsonLine original |> tryParseLine

    test <@ parsed = Some original @>

[<Fact>]
let ``a JSON line is a single line`` () =
    // JSON Lines only works if it is: an indented record would make every
    // subsequent append unparseable.
    test <@ not ((toJsonLine (sample 1L 1L)).Contains "\n") @>

[<Fact>]
let ``a malformed line is dropped without losing the rest of the series`` () =
    // A daemon killed mid-append leaves a truncated last line. One bad line must
    // not cost the whole measurement history.
    let text =
        String.Join(
            "\n",
            [ toJsonLine (sample 1L 100L)
              "{\"generation\": 2, truncated"
              toJsonLine (sample 3L 300L) ]
        )

    let series = parseSeries text

    test <@ series |> List.map (fun s -> s.Generation) = [ 1L; 3L ] @>

[<Fact>]
let ``a line missing a field is dropped rather than half-parsed`` () =
    test <@ tryParseLine "{\"generation\":1,\"kind\":\"cold\"}" = None @>

[<Fact>]
let ``blank lines parse to nothing`` () =
    test <@ tryParseLine "" = None @>
    test <@ tryParseLine "   " = None @>

[<Fact>]
let ``appending builds a readable series in order`` () =
    withTempDir "scan-metrics" (fun tmpDir ->
        let path = recordPath tmpDir

        for generation in 1L .. 5L do
            test <@ tryAppend path (sample generation (1_000L * generation)) = Ok() @>

        let series = readSeries path

        test <@ series |> List.map (fun s -> s.Generation) = [ 1L; 2L; 3L; 4L; 5L ] @>
        test <@ series |> List.map (fun s -> s.RssBytes) = [ 1000L; 2000L; 3000L; 4000L; 5000L ] @>)

[<Fact>]
let ``reading a repository that has never scanned yields an empty series`` () =
    withTempDir "scan-metrics-empty" (fun tmpDir -> test <@ List.isEmpty (readSeries (recordPath tmpDir)) @>)

[<Fact>]
let ``recordPath lives under the repository's .fshw directory`` () =
    let path = recordPath "/repo"

    test <@ path.EndsWith(Path.Combine(".fshw", "scan-metrics.jsonl")) @>

// --- the retention fit ---

[<Fact>]
let ``a flat RSS series is within the bound`` () =
    let series = [ for g in 1L .. 5L -> sample g 4_000_000_000L ]

    test <@ fitRetention DefaultRetentionBound series = RetentionVerdict.WithinBound(0.0, DefaultRetentionBound) @>

[<Fact>]
let ``growth under ten percent per generation is within the bound`` () =
    // +1% of the first generation's RSS per generation.
    let series = [ for g in 0L .. 4L -> sample (g + 1L) (1_000_000L + 10_000L * g) ]

    match fitRetention DefaultRetentionBound series with
    | RetentionVerdict.WithinBound(slope, bound) ->
        test <@ bound = DefaultRetentionBound @>
        test <@ abs (slope - 0.01) < 1e-9 @>
    | other -> failwith $"expected WithinBound, got %A{other}"

[<Fact>]
let ``growth past the bound is reported as exceeding, with the slope`` () =
    // +25% of the first generation's RSS per generation.
    let series = [ for g in 0L .. 4L -> sample (g + 1L) (1_000_000L + 250_000L * g) ]

    match fitRetention DefaultRetentionBound series with
    | RetentionVerdict.ExceedsBound(slope, bound) ->
        test <@ bound = DefaultRetentionBound @>
        test <@ abs (slope - 0.25) < 1e-9 @>
    | other -> failwith $"expected ExceedsBound, got %A{other}"

[<Fact>]
let ``a single generation cannot be fitted and says so`` () =
    // The rule is explicit that a failed threshold must not be relabelled as
    // proof of a leak; the same honesty applies to having no data at all.
    test <@ fitRetention DefaultRetentionBound [] = RetentionVerdict.NotEnoughData 0 @>
    test <@ fitRetention DefaultRetentionBound [ sample 1L 100L ] = RetentionVerdict.NotEnoughData 1 @>

[<Fact>]
let ``a shrinking series has a negative slope and is within the bound`` () =
    let series = [ for g in 0L .. 4L -> sample (g + 1L) (1_000_000L - 50_000L * g) ]

    match fitRetention DefaultRetentionBound series with
    | RetentionVerdict.WithinBound(slope, _) -> test <@ slope < 0.0 @>
    | other -> failwith $"expected WithinBound, got %A{other}"

// --- the forced-GC opt-in ---

[<Fact>]
let ``forced GC is off unless the environment asks`` () =
    test <@ forceGcEnabled (fun _ -> null) = false @>
    test <@ forceGcEnabled (fun _ -> "") = false @>
    test <@ forceGcEnabled (fun _ -> "0") = false @>

[<Fact>]
let ``forced GC is on for 1 or true`` () =
    test <@ forceGcEnabled (fun _ -> "1") @>
    test <@ forceGcEnabled (fun _ -> "true") @>
    test <@ forceGcEnabled (fun _ -> " TRUE ") @>

[<Fact>]
let ``a live resource reading is plausible and records whether GC was forced`` () =
    let reading = readResources false

    test <@ reading.RssBytes > 0L @>
    test <@ reading.ManagedBytes > 0L @>
    test <@ reading.ForcedGc = false @>
    test <@ reading.Gen2Collections >= 0 @>

// --- failure paths: measurement is diagnostic and must never be load-bearing ---

[<Fact>]
let ``appending to an unwritable path reports the failure instead of throwing`` () =
    // A daemon must not die because it could not write a measurement. The
    // failure is a returned value, so a caller can log it and carry on.
    withTempDir "scan-metrics-unwritable" (fun tmpDir ->
        // A FILE where the record's parent directory should be: creating the
        // directory fails, and so does the append.
        let blocker = Path.Combine(tmpDir, ".fshw")
        File.WriteAllText(blocker, "not a directory")

        match tryAppend (recordPath tmpDir) (sample 1L 100L) with
        | Ok() -> failwith "expected the append to fail with a file in place of the .fshw directory"
        | Result.Error message -> test <@ not (String.IsNullOrWhiteSpace message) @>)

[<Fact>]
let ``reading a path that is not a readable file yields an empty series`` () =
    // Every caller already handles "not enough generations to judge"; an
    // unreadable file is that, not an exception out of a diagnostic read.
    withTempDir "scan-metrics-unreadable" (fun tmpDir ->
        Directory.CreateDirectory(recordPath tmpDir) |> ignore

        test <@ List.isEmpty (readSeries (recordPath tmpDir)) @>)

[<Fact>]
let ``reading a locked file yields an empty series rather than throwing`` () =
    // The other unreadable shape, and the one that actually happens: the daemon
    // is appending to the record while something else reads it. A diagnostic
    // read must never throw into a caller that only wanted a measurement.
    withTempDir "scan-metrics-locked" (fun tmpDir ->
        let path = recordPath tmpDir
        tryAppend path (sample 1L 100L) |> ignore

        use _exclusive =
            new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        test <@ List.isEmpty (readSeries path) @>)

[<Fact>]
let ``a forced-GC reading records that a collection preceded it`` () =
    // The retention figure is only meaningful post-GC, and the
    // record has to say which kind of reading it is.
    let reading = readResources true

    test <@ reading.ForcedGc @>
    test <@ reading.RssBytes > 0L @>
    test <@ reading.ManagedBytes > 0L @>

[<Fact>]
let ``a series with no RSS baseline reports a zero slope rather than dividing by it`` () =
    // Defensive: a reading of 0 bytes is impossible from a live process, but a
    // hand-written or truncated series can carry one, and a NaN slope would be
    // reported as exceeding the bound — a fabricated leak.
    let series = [ sample 1L 0L; sample 2L 0L ]

    test <@ fitRetention DefaultRetentionBound series = RetentionVerdict.WithinBound(0.0, DefaultRetentionBound) @>

[<Fact>]
let ``two identical generations fit a flat slope`` () =
    // The smallest fittable series: two points. Pins that `NotEnoughData` stops
    // at one, so a second generation is immediately comparable.
    let series = [ sample 1L 500L; sample 2L 500L ]

    test <@ fitRetention DefaultRetentionBound series = RetentionVerdict.WithinBound(0.0, DefaultRetentionBound) @>

// --- Where a scan's files went ---

/// A record written BEFORE `filesSkipped`/`filesDepsGated` existed. Verbatim from
/// `.fshw/scan-metrics.jsonl` — the format two weeks of real measurements are in.
let private legacyLine =
    """{"durationMs":456431.303,"filesChecked":1835,"filesRegistered":1812,"filesUnchecked":0,"forcedGc":false,"gen2Collections":297,"generation":7,"kind":"forced","managedBytes":6343362800,"retryRounds":0,"rssBytes":7049527296,"sampledAt":"2026-09-10T23:15:12.1385310Z"}"""

[<Fact>]
let ``a record written before the skip fields still parses`` () =
    // The ledger is the measurement record, and its value is the SERIES. A new field
    // that made every existing line unparseable would silently discard the history
    // this file exists to accumulate — `tryParseLine` answers `None` for a malformed
    // line, so the loss would look like an empty series rather than an error.
    match tryParseLine legacyLine with
    | None -> Assert.Fail "a pre-existing record must still parse, or the recorded history is lost"
    | Some parsed ->
        test <@ parsed.Generation = 7L @>
        test <@ parsed.FilesChecked = 1835 @>
        // Absent means "not recorded", which reads as zero rather than as a guess.
        test <@ parsed.FilesSkipped = 0 @>
        test <@ parsed.FilesDepsGated = 0 @>
        test <@ parsed.FilesUncovered = 0 @>
        test <@ parsed.DirectSpawns = 0L @>
        test <@ parsed.HelperSpawns = 0L @>

[<Fact>]
let ``skipped and deps-gated counts round-trip`` () =
    // The two reasons are recorded SEPARATELY on purpose. A file outside this scan's
    // cohort is ordinary; a project the deps-freshness gate refused is a whole
    // project's files dropped while the scan still reports success, which is the case
    // the ledger could not previously distinguish from a completed scan.
    let original =
        { sample 4L 1_000L with
            FilesSkipped = 900
            FilesDepsGated = 512
            FilesUncovered = 388 }

    match tryParseLine (toJsonLine original) with
    | None -> Assert.Fail "round-trip failed"
    | Some parsed ->
        test <@ parsed.FilesSkipped = 900 @>
        test <@ parsed.FilesDepsGated = 512 @>
        test <@ parsed.FilesUncovered = 388 @>

[<Fact>]
let ``a host-scoped sample says so on its line, and round-trips`` () =
    // Sessions of a repository host share one process: its RSS is the host's total,
    // and a reader must never attribute it to one worktree.
    let hosted =
        { sample 1L 9_000_000_000L with
            Scope = DaemonHosting.ResourceScope.Host }

    let line = toJsonLine hosted
    test <@ line.Contains "\"scope\":\"host\"" @>
    test <@ tryParseLine line = Some hosted @>

[<Fact>]
let ``a per-worktree daemon's sample is process-scoped`` () =
    test <@ (toJsonLine (sample 1L 1L)).Contains "\"scope\":\"process\"" @>

[<Fact>]
let ``a line written before samples carried a scope reads as process-scoped`` () =
    let legacy = (toJsonLine (sample 2L 5L)).Replace(",\"scope\":\"process\"", "")

    test <@ not (legacy.Contains "scope") @>
    test <@ (tryParseLine legacy |> Option.map (fun s -> s.Scope)) = Some DaemonHosting.ResourceScope.Process @>
