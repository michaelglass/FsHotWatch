/// Trace-based selection in shadow (`tests.traces.select: "shadow"`): at a run's launch,
/// compute which tests each traced project's recorded traces say must run, compare that
/// with the static selection the run actually launches, and log one line per project; at
/// completion, store the comparison and every failed test the trace selection left out
/// (a verified-failure miss) in the shadow store (`shadowStorePath`), which every
/// checkout of the repository shares.
///
/// Shadow never changes a run: nothing here feeds the selection, the verdict, the
/// evidence or any cache, and every failure is one log line and a missing row.
namespace FsHotWatch.TestPrune

open System
open System.Diagnostics
open System.IO
open TestPrune.AstAnalyzer
open TestPrune.Trace

/// One project's shadow comparison, computed at launch.
type ShadowReading =
    {
        /// The environment fingerprint whose traces were read: the project's newest recorded run's.
        Fingerprint: string
        Selection: TraceSelect.Selection
        Comparison: TraceSelect.Comparison
        SelectMs: int64
    }

[<RequireQualifiedAccess>]
module TraceShadow =
    /// The trace key of an indexed test: the compiled method name xUnit reports.
    let keyOf (t: TestMethodInfo) =
        TraceIngest.testKey t.TestProject t.TestClass (canonicalShortName t.TestMethod)

    /// The trace key of a failed test, from its project, class and method.
    let failureKey (project: string) (cls: string) (meth: string) = TraceIngest.testKey project cls meth

    let private dbPath (settings: TraceSettings) (repoRoot: string) = Path.Combine(repoRoot, settings.DbPath)

    /// Where shadow rows and misses are stored: `tests.traces.shadowDb` resolved against the
    /// repository root, else the repository's box-wide shadow store
    /// (`<shared cache>/cache/trace-shadow/<repository namespace>/shadow.db`). The traces
    /// stay in each checkout's own database; the measurement is shared, because a window
    /// needs more runs over more days than any one short-lived checkout sees.
    let shadowStorePath (settings: TraceSettings) (repoRoot: string) =
        match settings.ShadowDbPath with
        | Some path -> Path.Combine(repoRoot, path)
        | None ->
            Path.Combine(
                FsHotWatch.FsHwPaths.sharedCacheHome (),
                "cache",
                "trace-shadow",
                FsHotWatch.RepoIdentity.namespaceOf repoRoot,
                "shadow.db"
            )

    /// One project's reading against the traces of its recorded `run`.
    let private readingOf
        (store: TraceStore.Store)
        (repoRoot: string)
        (universe: TestMethodInfo list)
        (versions: Lazy<Map<string, string>>)
        (staticSelection: TestMethodInfo list)
        (project: string)
        (run: TraceStore.TraceRun)
        : ShadowReading =
        let clock = Stopwatch.StartNew()

        let selection =
            TraceSelect.select
                project
                (universe |> List.map keyOf)
                (fun key -> store.TryRead(key, run.EnvFingerprint))
                versions.Value.TryFind
                (TraceIngest.currentInputHash repoRoot)

        let staticKeys =
            staticSelection
            |> List.filter (fun t -> t.TestProject = project)
            |> List.map keyOf

        { Fingerprint = run.EnvFingerprint
          Selection = selection
          Comparison = TraceSelect.compare staticKeys selection
          SelectMs = clock.ElapsedMilliseconds }

    /// The trace selection of every project in `projects` that has a recorded run, next to
    /// `staticSelection`. `universeOf` is a project's indexed tests; `versions` the
    /// symbol version hashes of the tree now (`TraceIngest.versionHashes`), forced only
    /// when some project has traces. Each line goes to `log`. With `select` off, or when
    /// the store cannot be read, nothing is selected and the run is unaffected.
    let atLaunch
        (settings: TraceSettings)
        (repoRoot: string)
        (projects: string list)
        (universeOf: string -> TestMethodInfo list)
        (versions: Lazy<Map<string, string>>)
        (staticSelection: TestMethodInfo list)
        (log: string -> unit)
        : ShadowReading list =
        match settings.Select with
        | SelectOff -> []
        | SelectShadow ->
            try
                using (TraceStore.Store.Open(dbPath settings repoRoot)) (fun store ->
                    projects
                    |> List.choose (fun project ->
                        store.Runs project
                        |> List.tryFind (fun r -> r.Status = TraceStore.Recorded)
                        |> Option.map (readingOf store repoRoot (universeOf project) versions staticSelection project))
                    |> List.map (fun r ->
                        log (TraceSelect.logLine r.Selection r.Comparison r.SelectMs)
                        r))
            with ex ->
                log $"trace-shadow: skipped — %s{ex.GetType().Name}: %s{ex.Message}"
                []

    /// The fingerprint the run recorded `project`'s traces under, read from the checkout's
    /// trace database (ingestion has finished by completion), and whether anything was ever
    /// stored under `predicted`. `None` when the run recorded no traces for the project.
    let private recordedFingerprint (settings: TraceSettings) (repoRoot: string) (runId: string) (r: ShadowReading) =
        let path = dbPath settings repoRoot

        if File.Exists path then
            using (TraceStore.Store.Open path) (fun traces ->
                let project = r.Selection.TestProject

                let actual =
                    traces.Runs project
                    |> List.tryFind (fun run -> run.RunId = runId && run.Status = TraceStore.Recorded)
                    |> Option.map _.EnvFingerprint

                actual, not (traces.TestKeysOf(project, r.Fingerprint)).IsEmpty)
        else
            None, false

    /// Store each reading under `runId`, with the tests of `failedKeys` its selection left
    /// out as verified-failure misses (each also logged). `ranInFull` says whether a project
    /// ran every test in this run: its row is a full-run row and records the trace
    /// selection's recall over the run's failures. `failedKeys` is `None` when the run left
    /// no complete report: nothing is judged, and a full row's recall is not measurable.
    /// Never throws.
    let atCompletion
        (settings: TraceSettings)
        (repoRoot: string)
        (readings: ShadowReading list)
        (runId: string)
        (ranInFull: string -> bool)
        (failedKeys: string list option)
        (log: string -> unit)
        : unit =
        if not readings.IsEmpty then
            try
                let recordPolicy =
                    match settings.Record with
                    | RecordFullRuns -> "full-runs"
                    | RecordEveryRun -> "every-run"

                let fingerprints =
                    readings |> List.map (recordedFingerprint settings repoRoot runId)

                using (TraceStore.Store.Open(shadowStorePath settings repoRoot)) (fun store ->
                    let at = DateTimeOffset.UtcNow
                    let failed = failedKeys |> Option.defaultValue []

                    List.zip readings fingerprints
                    |> List.iter (fun (r, (actual, predictedHadTraces)) ->
                        let full = ranInFull r.Selection.TestProject

                        let recall =
                            match full, failedKeys with
                            | false, _ -> "n/a: impact run"
                            | true, None -> "n/a: no complete report"
                            | true, Some keys -> TraceSelect.traceRecall r.Selection keys

                        let facts: TraceSelect.RowFacts =
                            { RunId = runId
                              Mode =
                                (if full then
                                     TraceStore.FullSuiteRun
                                 else
                                     TraceStore.ImpactRun)
                              RecordPolicy = recordPolicy
                              Fingerprint = r.Fingerprint
                              ActualFingerprint = actual
                              PredictedHadTraces = predictedHadTraces
                              RecordedAt = at
                              SelectMs = r.SelectMs
                              TraceRecall = recall }

                        store.RecordShadow(TraceSelect.row facts r.Selection r.Comparison)
                        let unknown = TraceSelect.unknownFailures r.Selection failed

                        if not unknown.IsEmpty then
                            let names = String.concat ", " unknown

                            log
                                $"trace-shadow: %d{unknown.Length} failed test(s) in %s{r.Selection.TestProject} that no traced key names: %s{names} — not judged as misses"

                        let misses = TraceSelect.verifiedFailureMisses r.Selection failed

                        if not misses.IsEmpty then
                            let names = String.concat ", " misses
                            store.RecordShadowMisses(runId, r.Selection.TestProject, at, misses)

                            log
                                $"trace-shadow: %d{misses.Length} verified-failure miss(es) in %s{r.Selection.TestProject}: %s{names} — failed while their traces said nothing they ran changed"))
            with ex ->
                log $"trace-shadow: not stored — %s{ex.GetType().Name}: %s{ex.Message}"
