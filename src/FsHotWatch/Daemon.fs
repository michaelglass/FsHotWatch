module FsHotWatch.Daemon

// FS0057: `TransparentCompiler.CacheSizes` is marked experimental in FCS 43.*.
#nowarn "57"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open System.Text.Json
open FsHotWatch.CheckCache
open FsHotWatch.CheckPipeline
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Ipc
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.Watcher
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph

/// Extract FCS diagnostics from check results and report to the error ledger.
/// Reports all severity levels (Error, Warning, Info, Hidden) with configurable
/// suppressed diagnostic codes.
let internal mapFcsSeverity =
    function
    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> DiagnosticSeverity.Error
    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> DiagnosticSeverity.Warning
    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Info -> DiagnosticSeverity.Info
    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Hidden -> DiagnosticSeverity.Hint

/// The projection that `reportFcsDiagnostics` reports FROM: FCS diagnostics in,
/// ledger entries plus self-incompatible faults out. Split out of the reporting
/// function rather than inlined there so the guarantee that matters can be
/// asserted against REAL `FSharpDiagnostic` values, instead of against a
/// re-implementation of the same mapping inside a test.
///
/// The guarantee: nothing in the returned `ErrorEntry list` was classified
/// `SelfIncompatible`. No filter here enforces that. It holds because
/// `FcsDiagnosticClass.SelfIncompatible` carries a `SelfIncompatibleDiagnostic`
/// and there is no function anywhere that produces an `ErrorEntry` from one.
let internal classifyFcsDiagnostics
    (allSuppressed: Set<int>)
    (diagnostics: FSharp.Compiler.Diagnostics.FSharpDiagnostic[])
    : ErrorEntry list * FcsDiagnosticFilter.SelfIncompatibleDiagnostic list =
    let classified =
        diagnostics
        |> Array.map (fun d ->
            classifyDiagnostic
                allSuppressed
                d.ErrorNumber
                d.Message
                (mapFcsSeverity d.Severity)
                d.StartLine
                d.StartColumn)

    let reportable =
        classified
        |> Array.choose (function
            | FcsDiagnosticClass.Reportable entry -> Some entry
            | FcsDiagnosticClass.Suppressed
            | FcsDiagnosticClass.SelfIncompatible _ -> None)
        |> Array.toList

    let selfIncompatible =
        classified
        |> Array.choose (function
            | FcsDiagnosticClass.SelfIncompatible d -> Some d
            | _ -> None)
        |> Array.toList

    reportable, selfIncompatible

/// What one file's FCS diagnostics write to the ledger, by key.
type internal FcsLedgerWrites =
    {
        /// Under `fcs`: findings about the reader's code.
        Findings: ErrorEntry list
        /// Under `fcs-internal`: faults in fshw's own checking.
        Internal: ErrorEntry list
        /// The self-incompatible diagnostics, for the warn lines.
        SelfIncompatible: FcsDiagnosticFilter.SelfIncompatibleDiagnostic list
    }

/// The ledger writes one file's FCS diagnostics become. Pure, and over real
/// `FSharpDiagnostic` values, so what reaches the verdict is asserted against the
/// function the daemon reports through rather than a re-implementation of it.
///
/// A file whose diagnostics include ANY self-incompatible one is a SUSPECT check:
/// every other diagnostic it produced goes under `fcs-internal` too (see
/// `FcsDiagnosticFilter.suspectCheckEntries` for why at its own severity, and never
/// as `Info`), so `Findings` is empty for it.
let internal fcsLedgerWrites
    (allSuppressed: Set<int>)
    (frame: PathFrame.PathFrame option)
    (project: string)
    (diagnostics: FSharp.Compiler.Diagnostics.FSharpDiagnostic[])
    : FcsLedgerWrites =
    let reportable, selfIncompatible = classifyFcsDiagnostics allSuppressed diagnostics

    // Checked under a virtual root, a diagnostic can name a path in its text: the
    // reader gets the worktree's.
    let reportable =
        reportable
        |> List.map (fun entry ->
            { entry with
                Message = PathFrame.textFrom frame entry.Message
                Detail = entry.Detail |> Option.map (PathFrame.textFrom frame) })

    match selfIncompatible with
    | [] ->
        { Findings = reportable
          Internal = []
          SelfIncompatible = [] }
    | _ ->
        { Findings = []
          Internal =
            selfIncompatibleLedgerEntries project selfIncompatible
            @ suspectCheckEntries reportable
          SelfIncompatible = selfIncompatible }

let private reportFcsDiagnostics (suppressedCodes: Set<int>) (host: PluginHost) (checkResult: Events.FileCheckResult) =
    match checkResult.CheckResults with
    | ParseOnly -> ()
    | FullCheck checkResults ->
        // Merge global suppressed codes with per-file #nowarn directives.
        // Workaround for https://github.com/dotnet/fsharp/issues/9796.
        // Shared with TestPrunePlugin's `hasFcsErrors` cache-poisoning gate
        // via `FcsDiagnosticFilter.allSuppressedCodes` so the user-visible
        // diagnostic stream and the gate agree on which codes are noise.
        //
        // The self-incompatible guard is deliberately NOT shared with that gate.
        // The gate's job is to REFUSE to trust a check that looks wrong, and a
        // self-incompatible diagnostic is exactly that signal; teaching the gate
        // to ignore it would make it flush symbols from the one run it most needs
        // to distrust. This path's job is the opposite: never tell a reader their
        // code is wrong on evidence that says nothing about it. The asymmetry
        // matters more now than when it was written — with the cause unknown,
        // this guard is the only thing between those diagnostics and a red gate,
        // so the conservative side must stay conservative.
        let allSuppressed = allSuppressedCodes suppressedCodes checkResult.Source
        let fileName = AbsFilePath.value checkResult.File
        let project = Path.GetFileName checkResult.ProjectOptions.ProjectFileName

        let writes =
            fcsLedgerWrites allSuppressed checkResult.Frame project checkResults.Diagnostics

        // A self-incompatible diagnostic still here is a fault in THIS process. The
        // check that produced it may have been re-checked once in `CheckPipeline`, or
        // may not have been (the daemon log says which); either way it survived. Say
        // so, loudly enough to be counted — the point of the guard is to MEASURE how
        // often this happens, not to make it invisible, and with the cause unknown
        // (see `FcsDiagnosticFilter`) the count is the only evidence anyone will
        // have to work from.
        selfIncompatibleLogLines project (Path.GetFileName fileName) writes.SelfIncompatible
        |> List.iter (Logging.warn "fcs")

        // Both ledger keys are written on EVERY check, one of them usually with
        // an empty list. That is the point: a key left alone keeps whatever the
        // previous check put there, so a fault that has since been cured would
        // outlive its cause under `fcs-internal` exactly as a fixed compile error
        // would under `fcs`.
        for plugin, entries in
            [ PluginActivity.FcsPluginName, writes.Findings
              PluginActivity.FcsInternalPluginName, writes.Internal ] do
            if entries.IsEmpty then
                host.ClearErrors(plugin, fileName, version = checkResult.Version)
            else
                host.ReportErrors(plugin, fileName, entries, version = checkResult.Version)

/// Apply the deps-freshness gate to one project before its files are checked.
/// Returns `true` when FCS analysis should proceed for the project, `false`
/// when it must be skipped (stale assets that couldn't be auto-recovered, or a
/// still-stale state we already attempted). On a fail-fast, exactly one Error
/// diagnostic is reported through the ledger under the `deps` plugin so the
/// aggregate verdict is non-zero and the phantom error-storm is never produced.
/// `None` gate (test daemons) always proceeds. Pure of FCS — only consults the
/// injected gate and reports/clears via the host.
let internal applyDepsGate
    (gate: (string -> DepsFreshness.GateResult) option)
    (host: PluginHost)
    (projPath: string)
    : bool =
    match gate with
    | None -> true
    | Some g ->
        match g projPath with
        | DepsFreshness.Proceed ->
            host.ClearErrors(DepsFreshness.pluginName, projPath)
            true
        | DepsFreshness.RecoveredOk ->
            Logging.info "deps" $"%s{Path.GetFileName projPath}: deps were stale — auto-restored OK"
            host.ClearErrors(DepsFreshness.pluginName, projPath)
            true
        | DepsFreshness.SkipAlreadyAttempted ->
            // Skipping is right — a project whose deps will not restore must not be
            // type-checked against an empty reference set, which produces a phantom
            // "namespace not found" storm. Skipping SILENTLY is not: this drops every
            // file of the project from the scan, and the scan still publishes its
            // generation. The diagnostic used to be assumed — inherited from the
            // `FailFast` of the first recovery attempt — but nothing keeps that entry
            // alive across a re-discovery, so the project could leave the scan with
            // nothing said about it at all.
            let message =
                $"%s{Path.GetFileName projPath}: deps still stale (recovery already attempted) — skipping FCS analysis"

            Logging.warn "deps" message

            let detail =
                "Every file in this project was left unchecked, so this scan did not cover the whole tree. "
                + "Restore the project's dependencies and re-run; the gate re-arms once its deps are observed fresh."

            host.ReportErrors(
                DepsFreshness.pluginName,
                projPath,
                [ ErrorLedger.ErrorEntry.errorWithDetail message detail ]
            )

            false
        | DepsFreshness.FailFast(message, detail) ->
            Logging.error "deps" message

            let entry =
                if System.String.IsNullOrEmpty detail then
                    ErrorLedger.ErrorEntry.error message
                else
                    ErrorLedger.ErrorEntry.errorWithDetail message detail

            host.ReportErrors(DepsFreshness.pluginName, projPath, [ entry ])
            false

/// Fingerprint fsproj files by path + last-write-time. Used by ScanAll to skip
/// expensive MSBuild re-evaluation when no project files have changed.
///
/// Shares both the discovery roots (`Discovery.findFsprojFiles`) and the exclude
/// semantics (`PathFilter.isExcludedPath`) with `discoverAndRegisterProjects`, so
/// the fingerprint tracks exactly the set of projects discovery would register: an
/// edit to a user-excluded fsproj must not churn it and trigger a spurious full
/// MSBuild re-evaluation.
let internal fingerprintFsprojFiles (repoRoot: string) (excludePatterns: string list) =
    let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

    Discovery.findFsprojFiles repoRoot
    |> List.filter (fun f -> not (isExcluded f))
    |> List.map (fun f -> f, File.GetLastWriteTimeUtc(f).Ticks)
    |> Set.ofList

/// `<projDir>/obj/project.assets.json` for a project file — restore's materialized
/// package graph, and the path `resolveAffectedProjects` maps back to this same
/// `.fsproj`. Both directions derive the shape here so neither can start naming a
/// path the other does not.
///
/// `None` when the project path has no directory part, which no discovered project
/// has.
let internal projectAssetsFileFor (fsproj: string) : string option =
    let directory = Path.GetDirectoryName(fsproj: string)

    if String.IsNullOrEmpty directory then
        None
    else
        Some(Path.Combine(directory, "obj", "project.assets.json"))

/// Record the discovered project files' current content in `tracker`, so a watcher
/// echo carrying the SAME bytes the model was just built from is not admitted as a
/// change.
///
/// `ContentDedup.Tracker` answers "changed" for a path it has never seen. For a source
/// file that is the only honest answer — there is no prior. For a project file the
/// daemon is about to load its model from, a prior exists, and not supplying it costs a
/// full rediscovery: on a COLD daemon the tracker is empty, so the first `ProjectChanged`
/// for every `.fsproj` re-discovered the workspace and advanced the model generation,
/// whatever the file actually said. A cold `check` provokes exactly those echoes itself
/// — its `beforeRun` restore/build touches project state before the first scan — and the
/// generation that advanced underneath that scan is what superseded it.
///
/// TAKEN BEFORE THE LOADER READS, never after, and the ordering is the entire safety
/// argument. A write landing between this read and the loader's leaves the tracker
/// holding the OLDER bytes, so the echo still reports a change and the model is
/// re-discovered: that race costs a redundant rediscovery, never a missed one. Seeding
/// AFTER the load would invert it — the tracker would hold bytes the model was not built
/// from, and the one event that would have repaired it would be swallowed.
///
/// COVERS `obj/project.assets.json` as well as `.fsproj`. `Watcher.isRelevantFile`
/// admits both at the project tier, and a build's restore rewrites every project's
/// assets file — so seeding only `.fsproj` left the input a cold `check` is GUARANTEED
/// to touch with no prior at all. One redundant echo per project file is a bounded
/// cost; a scoped invalidation of every project in the tree, arriving while the initial
/// scan is still blocked on the build that provoked it, cancels that scan's in-flight
/// checks and buys a second full pass over the workspace.
let internal observeProjectContent
    (repoRoot: string)
    (excludePatterns: string list)
    (tracker: ContentDedup.Tracker)
    : unit =
    let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

    let projects =
        Discovery.findFsprojFiles repoRoot |> List.filter (fun f -> not (isExcluded f))

    projects |> List.iter tracker.Observe

    // Seeded WITHOUT the exclude filter, because every assets file lives under `obj/`
    // and `isExcludedPath` excludes that whole subtree. `Watcher.isProjectAssetsJson`
    // bypasses the same filter, for the same reason and in the same direction: the
    // watcher admits these paths, so the tracker has to hold a prior for them or the
    // first echo of each is answered "changed" on its content-free default.
    projects |> List.choose projectAssetsFileFor |> List.iter tracker.Observe

[<Literal>]
let internal projInfoBinlogEnvVar = "FSHW_PROJINFO_BINLOG"

let internal projInfoLogDir (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "logs", "projinfo")

let internal isTruthyEnv (name: string) =
    match Environment.GetEnvironmentVariable(name) with
    | null
    | "" -> false
    | v ->
        let v = v.Trim()

        not (v.Equals("0", StringComparison.OrdinalIgnoreCase))
        && not (v.Equals("false", StringComparison.OrdinalIgnoreCase))

let internal countReferences (otherOptions: string[]) =
    otherOptions |> Array.sumBy (fun o -> if o.StartsWith("-r:") then 1 else 0)

/// Dumps source files, OtherOptions, and referenced project outputs to
/// `<logDir>/<ProjectName>.opts.txt` for diffing vs `dotnet build`.
let internal dumpProjectOptions (logDir: string) (fcsOptions: FSharpProjectOptions) : unit =
    try
        let name = Path.GetFileNameWithoutExtension(fcsOptions.ProjectFileName)
        let file = Path.Combine(logDir, $"%s{name}.opts.txt")

        let lines =
            seq {
                yield $"# Project: %s{fcsOptions.ProjectFileName}"
                yield $"# SourceFiles ({fcsOptions.SourceFiles.Length}):"
                yield! fcsOptions.SourceFiles |> Seq.map (fun f -> $"  %s{f}")
                yield $"# OtherOptions ({fcsOptions.OtherOptions.Length}):"
                yield! fcsOptions.OtherOptions |> Seq.map (fun o -> $"  %s{o}")
                yield $"# ReferencedProjects ({fcsOptions.ReferencedProjects.Length}):"
                yield! fcsOptions.ReferencedProjects |> Seq.map (fun r -> $"  %s{r.OutputFile}")
            }

        File.WriteAllLines(file, lines)
    with ex ->
        Logging.debug "discover" $"Could not dump options for %s{fcsOptions.ProjectFileName}: %s{ex.Message}"

/// TOTAL DISCOVERY FAILURE: `.fsproj` files were found on disk
/// and MSBuild evaluation loaded NONE of them.
///
/// WHAT HAPPENED. A transitive downgrade of `Microsoft.NET.StringTools` in a
/// published CLI made `LoadProject` throw `Method not found: 'Boolean
/// Microsoft.NET.StringTools.SpanBasedStringBuilder.Equals(System.String,
/// System.StringComparison)'` for all 18 projects of the consuming repo.
/// Discovery registered zero projects, so no plugin ever reported, and `confirm`
/// spent the FULL 60-minute verdict deadline waiting for a quiescence that could
/// not arrive — then blamed a wedged plugin and reported `coverage could not be
/// confirmed` with `plugins: []`. Every user-visible message pointed AWAY from
/// the cause; only `LoadProject FAILED` in the daemon log named it. An hour of
/// wall clock and a misdirecting reason string were most of the cost.
///
/// WHY `RunOnceOutput.failIfNoProjects` CANNOT SEE THIS, and why this is a second
/// guard rather than a fix to that one. That guard counts `.fsproj` files ON DISK
/// before any daemon work, and all 18 existed — it is structurally blind to a
/// load failure. The two ask different questions and both are worth asking: "is
/// there anything to load?" and "did any of it load?".
///
/// `None` for BOTH healthy states, and the first is deliberate: a tree with no
/// project files at all belongs to `failIfNoProjects` (and is already warned
/// about at the top of discovery), not here. Reporting it here would name a load
/// failure that did not happen — the exact sin this guard exists to end.
let internal totalDiscoveryFailure (fsprojFilesFound: int) (projectsLoaded: int) : string option =
    if fsprojFilesFound > 0 && projectsLoaded = 0 then
        Some
            $"PROJECT LOADING FAILED: MSBuild evaluation loaded 0 of %d{fsprojFilesFound} discovered project(s). \
              Nothing is registered, so no plugin can report and no test can run. This is NOT a \
              coverage, quiescence or wedged-plugin problem, whatever a later verdict says. \
              Read the per-project reason from the `LoadProject FAILED` lines — in \
              `logs/daemon.log`, or on stderr just above this line under `--run-once`, \
              which has no daemon. A silently downgraded app-local MSBuild support \
              assembly is one known cause."
    else
        None

/// Recognize the terminal across the JSON-RPC exception boundary, which does not
/// preserve the concrete `ConfigError` type. The prefix is deliberately the same
/// stable phrase the human/log/verdict surfaces carry.
let internal isTotalDiscoveryFailureMessage (message: string) : bool =
    not (isNull message)
    && message.Contains("PROJECT LOADING FAILED:", StringComparison.Ordinal)

/// A captured cohort cannot publish after discovery admits a newer model.
type internal ModelSupersededException(generation: int64) =
    inherit
        InvalidOperationException(
            $"The captured project model generation {generation} was invalidated before scan publication."
        )

/// The generation a captured epoch stamps on its results: `Some` only when a
/// completed model was captured.
let internal modelGenerationOf ((epoch, snapshot): int64 * ProjectModel.Counts option) : int64 option =
    snapshot |> Option.map (fun _ -> epoch)

/// The verdict-wait admission decision. Kept separate from the RPC closure so
/// both branches are deterministic unit-testable: a known total loader failure
/// must never enter the potentially hour-long host wait.
type internal DiscoveryAdmission =
    { Generation: int64
      Failure: string option }

let internal waitForVerdictUnlessDiscoveryFailed
    (discoveryAdmission: unit -> Task<DiscoveryAdmission>)
    (waitForVerdict: TimeSpan -> Task<unit>)
    (timeout: TimeSpan)
    : Task<unit> =
    task {
        let failIfNeeded admission =
            match admission.Failure with
            | Some reason -> raise (InvalidOperationException reason)
            | None -> ()

        let! admitted = discoveryAdmission ()
        failIfNeeded admitted
        let mutable admittedGeneration = admitted.Generation
        let mutable settled = false

        while not settled do
            do! waitForVerdict timeout
            let! afterWait = discoveryAdmission ()
            failIfNeeded afterWait

            if afterWait.Generation = admittedGeneration then
                settled <- true
            else
                admittedGeneration <- afterWait.Generation
    }

/// The four distinct facts at the project-discovery boundary. `Loaded` is the
/// number returned by Ionide/MSBuild into the project graph; `Registered` is the
/// later FCS pipeline count. Keeping both prevents a registration defect from
/// being mislabeled as a loader failure.
/// The same four counts `ProjectModel` classifies, so a coordinator outcome and
/// a published observation can never disagree about what was discovered.
type internal DiscoverySnapshot = ProjectModel.Counts

/// Serializes every clear/load/map/register transaction and publishes only one
/// immutable, completed outcome. `InProgress` deliberately hides the preceding
/// outcome: a check arriving while a repair discovery is running must wait for
/// that attempt, not fail from either transient empty stores or stale failure.
type internal DiscoveryCoordinator(?publish: ProjectModel.Observation -> unit) =
    // A scan reading project state while rediscovery is clearing
    // it sees an empty model and reports NO TESTS RAN, which a consumer can read
    // as a pass. `Completed` already hides the preceding outcome while an attempt
    // is pending, but hiding it makes IN-FLIGHT and NOTHING-DISCOVERED the same
    // answer. Publishing the observation says WHICH, so a reader can wait instead
    // of concluding.
    let announce =
        match publish with
        | Some f -> f
        | None -> ignore

    let admission = new SemaphoreSlim(1, 1)
    let stateGate = obj ()
    let mutable generation = 0L
    let mutable completed: (int64 * DiscoverySnapshot) option = None
    let mutable pendingAttempts = 0
    let mutable quiescence: TaskCompletionSource<unit> option = None

    // Announcements are published outside `stateGate`, so two of them can race. Each
    // takes its place in line under `stateGate`; one that arrives after a later
    // announcement was published is dropped rather than overwriting the newer answer.
    let announcementGate = obj ()
    let mutable announcementsIssued = 0L
    let mutable announcementPublished = 0L

    let issueAnnouncement () =
        announcementsIssued <- announcementsIssued + 1L
        announcementsIssued

    let publishInOrder (place: int64) (observation: ProjectModel.Observation) =
        lock announcementGate (fun () ->
            if place > announcementPublished then
                announcementPublished <- place
                announce observation)

    let waitForStableAdmission () : Task<int64 * DiscoverySnapshot option> =
        task {
            let mutable searching = true
            let mutable stable = 0L, None

            while searching do
                let observed =
                    lock stateGate (fun () ->
                        if pendingAttempts = 0 then
                            let snapshot = completed |> Option.map snd
                            Choice1Of2(generation, snapshot)
                        else
                            Choice2Of2(quiescence.Value.Task))

                match observed with
                | Choice1Of2 completed ->
                    stable <- completed
                    searching <- false
                | Choice2Of2 pending ->
                    let! _ = pending
                    ()

            return stable
        }

    // The epoch a cohort captures: the completed attempt and its counts, or the
    // requested generation with no completed model.
    let currentEpoch () =
        match completed with
        | Some(attempt, snapshot) -> attempt, Some snapshot
        | None -> generation, None

    member _.Completed =
        lock stateGate (fun () ->
            if pendingAttempts = 0 then
                completed |> Option.map snd
            else
                None)

    member _.RequestedGeneration = lock stateGate (fun () -> generation)

    /// What a reader should believe about the project model right
    /// now, as a value rather than an absence. `Completed` returns `None` both
    /// when an attempt is in flight and when nothing was ever observed; this
    /// distinguishes them, so a scan can wait for `Rediscovering` instead of
    /// treating it as an empty model.
    member _.Observation: ProjectModel.Observation =
        lock stateGate (fun () ->
            if pendingAttempts > 0 then
                ProjectModel.Observation.Rediscovering generation
            else
                match completed with
                | Some(epoch, snapshot) -> ProjectModel.ofCompleted epoch snapshot
                | None -> ProjectModel.Observation.Unobserved)

    member _.WaitForCompletion() : Task<DiscoverySnapshot option> =
        task {
            let! _, completed = waitForStableAdmission ()
            return completed
        }

    member _.WaitForStableAdmission() = waitForStableAdmission ()

    /// Capture the model a cohort will publish against. Waits until no attempt is
    /// pending, then reads under the admission lease so no writer is between clear and
    /// completion while `read` copies state. `read` must only copy: an attempt admitted
    /// while it runs is found by `WithCurrent` at publication. A pending attempt that
    /// fails fails the capture with its error, as it fails verdict admission.
    member _.Capture<'T>(read: int64 * DiscoverySnapshot option -> 'T) : Async<'T> =
        let rec attempt () =
            async {
                let! _ = waitForStableAdmission () |> Async.AwaitTask
                let! ct = Async.CancellationToken
                do! admission.WaitAsync(ct) |> Async.AwaitTask

                let captured =
                    try
                        lock stateGate (fun () -> if pendingAttempts = 0 then Some(currentEpoch ()) else None)
                        |> Option.map read
                    finally
                        admission.Release() |> ignore

                match captured with
                | Some value -> return value
                | None -> return! attempt ()
            }

        attempt ()

    /// Publish only while the captured epoch is still the completed model and no
    /// attempt is pending. Validation and `write` share `stateGate`, so no attempt can
    /// begin between them. `write` must be a short publication that never waits on this
    /// coordinator.
    member _.WithCurrent<'T>(captured: int64 * DiscoverySnapshot option, write: unit -> 'T) : 'T =
        lock stateGate (fun () ->
            if pendingAttempts <> 0 || currentEpoch () <> captured then
                raise (ModelSupersededException(fst captured))

            write ())

    member _.Run<'T>(work: unit -> Async<DiscoverySnapshot * 'T>) : Async<'T> =
        async {
            let attempt, place =
                lock stateGate (fun () ->
                    generation <- generation + 1L
                    pendingAttempts <- pendingAttempts + 1

                    if pendingAttempts = 1 then
                        quiescence <-
                            Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))

                    generation, issueAnnouncement ())

            // Announced OUTSIDE `stateGate`: a subscriber that reads the
            // coordinator back would deadlock against the lock it was published
            // under.
            publishInOrder place (ProjectModel.Observation.Rediscovering attempt)

            do! admission.WaitAsync() |> Async.AwaitTask

            try
                try
                    let! snapshot, result = work ()

                    let completion =
                        lock stateGate (fun () ->
                            completed <- Some(attempt, snapshot)
                            pendingAttempts <- pendingAttempts - 1

                            if pendingAttempts = 0 then
                                let completion = quiescence
                                quiescence <- None
                                Some(completion, issueAnnouncement ())
                            else
                                None)

                    // Only the attempt that quiesced the coordinator announces a
                    // settled model. While others are still pending the honest
                    // answer is still `Rediscovering`, which `Observation` reports.
                    // The announcement is made while this attempt still holds admission,
                    // so no cohort can capture the model before it is published.
                    match completion with
                    | Some(waiters, place) ->
                        waiters |> Option.iter (fun pending -> pending.TrySetResult() |> ignore)
                        publishInOrder place (ProjectModel.ofCompleted attempt snapshot)
                    | None -> ()

                    return result
                with ex ->
                    let completion =
                        lock stateGate (fun () ->
                            pendingAttempts <- pendingAttempts - 1

                            if pendingAttempts = 0 then
                                completed <- None
                                let completion = quiescence
                                quiescence <- None
                                Some(completion, issueAnnouncement ())
                            else
                                None)

                    // A failed attempt that quiesced the coordinator clears the
                    // completed outcome, so the honest published answer is that
                    // nothing has been observed — never a stale success.
                    match completion with
                    | Some(waiters, place) ->
                        waiters |> Option.iter (fun pending -> pending.TrySetException(ex) |> ignore)
                        publishInOrder place ProjectModel.Observation.Unobserved
                    | None -> ()

                    return raise ex
            finally
                admission.Release() |> ignore
        }

/// Discover .fsproj files and register them with the graph and pipeline.
/// Uses Ionide.ProjInfo for MSBuild design-time evaluation to get real
/// assembly references, NuGet packages, and compiler flags.
let private discoverAndRegisterProjects
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (mapOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    (phases: DaemonPhases.Ledger)
    (excludePatterns: string list)
    (clearCheckCache: bool)
    : Async<DiscoverySnapshot> =
    async {
        // Discovery is wall time a `check` blocks on — 8 s
        // cold, 47 s when a project-file change provokes it mid-run — and no plugin
        // owns it. Recorded on every exit, with the evaluation's own summary line.
        use phase = phases.Begin DaemonPhases.Phase.Discover
        let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

        let fsprojFiles =
            Discovery.findFsprojFiles repoRoot |> List.filter (fun f -> not (isExcluded f))

        if List.isEmpty fsprojFiles then
            // Surface zero-project discoveries: this is almost always a
            // misconfiguration (wrong working directory, an over-eager
            // .fshw.json `exclude` pattern, or an empty repo) and silently
            // running with no work to do hides the problem from users.
            let searched =
                Discovery.existingDiscoveryRoots repoRoot
                |> List.map (fun d -> Path.GetFileName(d.TrimEnd('/')))
                |> String.concat ", "

            let searched = if searched = "" then "src/, tests/" else searched

            Logging.warn
                "discover"
                $"No .fsproj files discovered under %s{searched} of %s{repoRoot}. Check `.fshw.json` exclude patterns or working directory."

        graph.PrepareForRediscovery()
        pipeline.PrepareForRediscovery(clearCheckCache = clearCheckCache)

        let logDir = projInfoLogDir repoRoot
        Directory.CreateDirectory(logDir) |> ignore

        let binlogDir =
            if isTruthyEnv projInfoBinlogEnvVar then
                let dir = Path.Combine(logDir, "binlogs")
                Directory.CreateDirectory(dir) |> ignore
                Some(DirectoryInfo(dir))
            else
                None

        // Subscribe to per-project load notifications so failures (ProjectNotRestored,
        // ReferencesNotLoaded, etc.) surface instead of being silently dropped from the
        // LoadProjects return seq. Without this, a project that fails design-time eval
        // just doesn't appear in `loaded` — callers never learn why references are missing.
        use _sub =
            loader.Notifications.Subscribe(fun state ->
                match state with
                | Types.WorkspaceProjectState.Failed(proj, reason) ->
                    Logging.error "discover" $"LoadProject FAILED for %s{Path.GetFileName proj}: %A{reason}"
                | Types.WorkspaceProjectState.Loading proj ->
                    Logging.debug "discover" $"Loading %s{Path.GetFileName proj}"
                | Types.WorkspaceProjectState.Loaded _ -> ())

        let sw = System.Diagnostics.Stopwatch.StartNew()
        Logging.info "discover" "Loading project options via MSBuild evaluation..."

        let mutable loadedCount = 0
        let mutable optionsMappedCount = 0
        let mutable registeredCount = 0

        try
            let loaded: Types.ProjectOptions list =
                match binlogDir with
                | Some dir ->
                    Logging.info "discover" $"Binlog capture enabled: %s{dir.FullName}"

                    loader.LoadProjects(fsprojFiles, [], BinaryLogGeneration.Within dir)
                    |> Seq.toList
                | None -> loader.LoadProjects(fsprojFiles) |> Seq.toList

            loadedCount <- loaded.Length

            // Register projects in the graph using Ionide-derived data (not XML parse)
            // so source file lists match what FCS sees (handles globs, conditionals, generated files)
            for proj in loaded do
                if not (isExcluded proj.ProjectFileName) then
                    let absProject = AbsProjectPath.create proj.ProjectFileName
                    let sourceFiles = proj.SourceFiles |> List.map AbsFilePath.create

                    let references =
                        proj.ReferencedProjects
                        |> List.map (fun r -> AbsProjectPath.create r.ProjectFileName)

                    graph.RegisterProject(absProject, sourceFiles, references)

                    // Record MSBuild's OWN output path. Without it
                    // `GetCanonicalDllPath` returns None for every project in a live
                    // daemon — the TFM it needs arrives only via `RegisterFromFsproj`,
                    // which nothing in `src/` calls — so artifact examination has never
                    // run in production. `TargetPath` also survives a custom
                    // `<AssemblyName>`, which the filename-based inference cannot.
                    graph.RegisterProjectOutput(absProject, proj.TargetPath)

            let fcsOptionsList = mapOptions loaded
            optionsMappedCount <- fcsOptionsList.Length
            sw.Stop()

            Logging.info
                "discover"
                $"MSBuild evaluation complete: %d{loadedCount} loaded, %d{optionsMappedCount} options mapped in %.1f{sw.Elapsed.TotalSeconds}s"

            phase.Complete(Some $"MSBuild evaluation: %d{loadedCount} loaded, %d{optionsMappedCount} options mapped")

            // The line above reports the LOADED count and then the
            // loop below iterates it — and an empty list iterates zero times, which
            // is why total discovery failure used to read exactly like a repository
            // with nothing to do. This is the assertion that tells those two apart,
            // at ERROR so it is the loudest line in the one file the diagnosis has
            // to open anyway.
            match totalDiscoveryFailure fsprojFiles.Length loadedCount with
            | Some message -> Logging.error "discover" message
            | None -> ()

            for fcsOptions in fcsOptionsList do
                if not (isExcluded fcsOptions.ProjectFileName) then
                    try
                        let absProject = Path.GetFullPath(fcsOptions.ProjectFileName)
                        pipeline.RegisterProject(absProject, fcsOptions)
                        registeredCount <- registeredCount + 1
                        dumpProjectOptions logDir fcsOptions
                        let refCount = countReferences fcsOptions.OtherOptions

                        Logging.info
                            "discover"
                            $"Registered %s{Path.GetFileName fcsOptions.ProjectFileName} (%d{fcsOptions.SourceFiles.Length} files, %d{fcsOptions.OtherOptions.Length} opts, %d{refCount} refs)"
                    with ex ->
                        Logging.error
                            "discover"
                            $"Failed to register %s{Path.GetFileName fcsOptions.ProjectFileName}: %s{ex.Message}"
        with ex ->
            sw.Stop()
            Logging.error "discover" $"MSBuild evaluation failed (%.1f{sw.Elapsed.TotalSeconds}s): %s{ex.Message}"
            phase.Complete(Some $"MSBuild evaluation failed: %s{ex.Message}")

        return
            { Discovered = fsprojFiles.Length
              Loaded = loadedCount
              OptionsMapped = optionsMappedCount
              Registered = registeredCount }
    }

/// How many changed projects a scoped-invalidation line names before eliding.
[<Literal>]
let internal scopedChangeNamesLogged = 8

/// Render the changed projects for the scoped-invalidation log line.
///
/// The COUNT is not the actionable fact; the NAMES are. "2 changed + 14 dependent"
/// tells a reader that something rewrote two project inputs mid-scan and refuses to
/// say which two — and the paths are in hand at the call site, emitted one line
/// earlier at DEBUG, which is the level nobody runs a long gate at. Recovering them
/// meant re-running the whole gate with logging raised.
///
/// Repo-relative rather than by file name: two projects in different directories can
/// share a `.fsproj` name, and a name that silently merges two subjects is how a
/// count becomes wrong rather than merely coarse.
///
/// Elides past `scopedChangeNamesLogged` so a tree-wide invalidation cannot turn one
/// line into a screenful, and SAYS it elided rather than truncating silently.
let internal describeChangedProjects (repoRoot: string) (projects: string list) : string =
    let relative (path: string) =
        let rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/')

        if rel.StartsWith("..", StringComparison.Ordinal) then
            path
        else
            rel

    match projects with
    | [] -> ""
    | _ ->
        let shown = projects |> List.truncate scopedChangeNamesLogged |> List.map relative
        let elided = projects.Length - shown.Length
        let names = String.concat ", " shown

        if elided > 0 then
            $" [%s{names}, and %d{elided} more]"
        else
            $" [%s{names}]"

/// Map a batch of changed project-tier paths (`.fsproj`, `.props`, or
/// `obj/project.assets.json`) to the set of *known* `.fsproj` paths whose FCS
/// state should be scoped-invalidated.
///
/// Returns `None` when the change is repo-wide and a full re-discovery is the
/// only safe response:
///   - any `.props` file (Directory.Build.props et al. affect every project),
///   - a `project.assets.json` or `.fsproj` under a directory that doesn't
///     match a currently-known project (a brand-new project the graph hasn't
///     registered yet — needs full discovery to pick up).
///
/// `Some projects` lists the affected known projects; the caller still expands
/// to transitive dependents before invalidating/re-checking. Order is not
/// significant.
let internal resolveAffectedProjects (knownProjects: string list) (changedPaths: string list) : string list option =
    let normalize (p: string) = Path.GetFullPath(p).Replace('\\', '/')

    let knownNorm = knownProjects |> List.map normalize

    let projDirToFsproj =
        knownNorm
        |> List.map (fun p -> (Path.GetDirectoryName(p: string)), p)
        |> Map.ofList

    let rec loop (acc: string list) (remaining: string list) =
        match remaining with
        | [] -> Some(List.distinct acc)
        | path :: rest ->
            let basename = Path.GetFileName(path: string)

            if basename.EndsWith(".props", StringComparison.OrdinalIgnoreCase) then
                None
            elif basename.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) then
                // <projDir>/obj/project.assets.json → owning <projDir>/<name>.fsproj
                let objDir = Path.GetDirectoryName(path: string)

                if isNull objDir then
                    None
                else
                    let projDir = normalize (Path.GetDirectoryName(objDir: string))

                    match Map.tryFind projDir projDirToFsproj with
                    | Some fsproj -> loop (fsproj :: acc) rest
                    | None -> None
            elif basename.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                let norm = normalize path

                if List.contains norm knownNorm then
                    loop (norm :: acc) rest
                else
                    None
            else
                // Unexpected non-project path in a project-tier batch — be safe.
                None

    loop [] changedPaths

/// Re-discover projects and clear FCS errors for any files that were removed.
/// `clearCheckCache` controls whether the full check-result cache is dropped:
///   - `true` (full re-discovery): the conservative behavior — every cached
///     check result is discarded, so the subsequent re-check recomputes
///     everything. Used when the change is repo-wide (`.props`, solution,
///     new/removed project) and we can't reason about which projects are stale.
///   - `false` (scoped re-discovery): the project→options maps are rebuilt
///     (so file/project membership is fresh) but cached check results are
///     kept. Projects whose options didn't change keep their warm cache; the
///     caller is responsible for explicitly invalidating the affected project
///     and its transitive dependents (see `InvalidateProjectFiles`).
/// Returns the set of removed files.
let private rediscoverAndClearRemoved
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (mapOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
    (discovery: DiscoveryCoordinator)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    (host: PluginHost)
    (logTag: string)
    (excludePatterns: string list)
    (contentTracker: ContentDedup.Tracker)
    (clearCheckCache: bool)
    =
    discovery.Run(fun () ->
        async {
            let oldFiles = graph.GetAllFiles() |> Set.ofList

            // Before the loader reads them — see `observeProjectContent`.
            observeProjectContent repoRoot excludePatterns contentTracker

            let! completed =
                discoverAndRegisterProjects
                    repoRoot
                    loader
                    mapOptions
                    graph
                    pipeline
                    host.Phases
                    excludePatterns
                    clearCheckCache

            let newFiles = graph.GetAllFiles() |> Set.ofList
            let removedFiles = Set.difference oldFiles newFiles

            // EVERY plugin, not just `fcs`. The phantom finding that
            // makes a renamed file a permanently red gate is TestPrune's
            // "symbol analysis failed — Parse errors", and clearing only the FCS
            // ledger left it standing.
            for file in removedFiles do
                host.ClearFileEverywhere(AbsFilePath.value file)

            if not removedFiles.IsEmpty then
                Logging.info logTag $"Cleared errors for %d{removedFiles.Count} removed files"

            return completed, (completed, removedFiles)
        })

/// Manages TaskCompletionSource instances for signal-based WaitForScan.
[<NoComparison; NoEquality>]
type private ScanSignalMsg =
    | WaitFor of afterGen: int64 * TaskCompletionSource<unit>
    | Signal of newGen: int64
    /// A scan request was admitted. Waiters registered from now on belong to it.
    | ObserveScan of Task<unit>
    /// That request's receipt settled: its completed generation, or its failure.
    | ScanSettled of Task<unit> * Result<int64, exn>
    /// Test seam: resolves once every message posted before it has been handled.
    | Drained of TaskCompletionSource<unit>
    /// Test seam: see ErrorLedger.LedgerMsg.RaiseFaultForTest
    /// for the rationale. Production messages don't have a natural failure
    /// mode, so this is the only realistic way to verify the agent surfaces
    /// programming bugs instead of swallowing them.
    | RaiseFaultForTest of exn

/// Wakes `WaitForScan` callers. A waiter resolves when the scan generation passes the one
/// it asked about, and only once the scan request it is bound to has settled. A waiter
/// bound to a request that fails receives that failure instead of hanging on a
/// generation that request will never produce.
type ScanSignal(?cancellationToken: CancellationToken) =
    let satisfied afterGeneration generation =
        if afterGeneration >= 0L then
            generation > afterGeneration
        else
            generation > 0L

    let agent =
        MailboxProcessor.Start(
            (fun inbox ->
                // latestGeneration latches the most recent SignalGeneration so a
                // WaitFor that arrives after the signal can resolve immediately.
                // Without this, a race between a scan signalling completion and the
                // client posting WaitFor leaves the waiter hanging. latestReceipt is
                // the most recently admitted scan request, kept after it fails so a
                // waiter that registers late still learns why.
                let rec loop
                    (latestGeneration: int64)
                    (latestReceipt: Task<unit> option)
                    (waiters: (int64 * Task<unit> option * TaskCompletionSource<unit>) list)
                    =
                    async {
                        let! msg = inbox.Receive()

                        // No inner try/with: the body is a typed match over messages
                        // we own, so anything that throws is a programming bug and
                        // must surface via `agent.Error` (exposed as `AgentCrashed`)
                        // rather than silently looping in the original state.
                        match msg with
                        | WaitFor(afterGeneration, tcs) ->
                            let pending =
                                latestReceipt
                                |> Option.filter (fun receipt -> not receipt.IsCompletedSuccessfully)

                            match pending with
                            | _ when satisfied afterGeneration latestGeneration ->
                                Logging.debug
                                    "scan-signal"
                                    $"WaitFor(%d{afterGeneration}) — already satisfied (latest=%d{latestGeneration}), resolving"

                                tcs.TrySetResult(()) |> ignore
                                return! loop latestGeneration latestReceipt waiters
                            | Some receipt when receipt.IsCompleted ->
                                // Receipts settle only by result or exception, never by
                                // cancellation, so a completed unsuccessful one carries it.
                                tcs.TrySetException(receipt.Exception.GetBaseException()) |> ignore
                                return! loop latestGeneration latestReceipt waiters
                            | _ ->
                                Logging.debug "scan-signal" $"WaitFor(%d{afterGeneration}) — registering waiter"

                                return!
                                    loop latestGeneration latestReceipt ((afterGeneration, pending, tcs) :: waiters)

                        | ObserveScan receipt ->
                            // A waiter already bound to an unsettled request keeps it: a
                            // recovery queued behind a failing scan cannot turn that
                            // failure green.
                            let bound =
                                waiters
                                |> List.map (fun (afterGeneration, previous, tcs) ->
                                    match previous with
                                    | Some earlier when not earlier.IsCompletedSuccessfully ->
                                        afterGeneration, previous, tcs
                                    | _ -> afterGeneration, Some receipt, tcs)

                            return! loop latestGeneration (Some receipt) bound

                        | ScanSettled(receipt, Result.Error failure) ->
                            let failed, remaining =
                                waiters
                                |> List.partition (fun (_, bound, _) ->
                                    bound |> Option.exists (fun task -> obj.ReferenceEquals(task, receipt)))

                            for _, _, tcs in failed do
                                tcs.TrySetException(failure) |> ignore

                            return! loop latestGeneration latestReceipt remaining

                        | ScanSettled(_, Ok newGeneration)
                        | Signal newGeneration ->
                            let toSignal, remaining =
                                waiters
                                |> List.partition (fun (afterGen, bound, _) ->
                                    satisfied afterGen newGeneration
                                    && (bound |> Option.forall (fun receipt -> receipt.IsCompletedSuccessfully)))

                            Logging.debug
                                "scan-signal"
                                $"SignalGeneration(%d{newGeneration}) — resolving %d{toSignal.Length} waiters, %d{remaining.Length} remaining"

                            for _, _, tcs in toSignal do
                                tcs.TrySetResult(()) |> ignore

                            return! loop (max latestGeneration newGeneration) latestReceipt remaining

                        | Drained tcs ->
                            tcs.TrySetResult(()) |> ignore
                            return! loop latestGeneration latestReceipt waiters

                        | RaiseFaultForTest ex -> raise ex
                    }

                loop 0L None []),
            ?cancellationToken = cancellationToken
        )

    do
        agent.Error.Add(fun ex ->
            // An unhandled exception inside the agent loop is a programming bug.
            // Logged with the full stack trace (ex.ToString(), not ex.Message); the
            // agent stops and pending waiters' WaitForGeneration tasks remain
            // unresolved, which is a visible hang at the next caller.
            Logging.error "scan-signal" $"Mailbox loop crashed (programming bug, agent stopped): %s{ex.ToString()}")

    /// Register a waiter that resolves when generation exceeds afterGeneration.
    /// If afterGeneration < 0, resolves on the next generation increment. A waiter bound
    /// to a scan request that fails receives that failure.
    member _.WaitForGeneration(afterGeneration: int64, currentGeneration: int64) : Task<unit> =
        if satisfied afterGeneration currentGeneration then
            Logging.debug
                "scan-signal"
                $"WaitForGeneration(%d{afterGeneration}, %d{currentGeneration}) — already satisfied, returning immediately"

            Task.FromResult(())
        else
            let tcs =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            agent.Post(WaitFor(afterGeneration, tcs))
            tcs.Task

    /// Bind later waiters to an admitted scan request, and settle them with its outcome
    /// once `receipt` settles, whether or not the requesting caller is still waiting.
    member _.ObserveScan(receipt: Task<unit>, generation: unit -> int64) =
        agent.Post(ObserveScan receipt)

        receipt.ContinueWith(
            (fun (settled: Task<unit>) ->
                let outcome =
                    if settled.IsCompletedSuccessfully then
                        Ok(generation ())
                    else
                        Result.Error(settled.Exception.GetBaseException())

                agent.Post(ScanSettled(receipt, outcome))),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        )
        |> ignore

    /// Signal all waiters whose afterGeneration is now satisfied.
    member _.SignalGeneration(newGeneration: int64) = agent.Post(Signal newGeneration)

    /// Unhandled exceptions inside the mailbox loop surface here. Subscribe to
    /// observe programming bugs.
    member _.AgentCrashed: IEvent<exn> = agent.Error

    /// Test seam: deterministically raise inside the agent loop. See
    /// `ErrorLedger.RaiseFaultForTest` for rationale.
    member internal _.RaiseFaultForTest(ex: exn) = agent.Post(RaiseFaultForTest ex)

    /// Test seam: completes once every message posted before this call has been handled,
    /// so a test can order a scan's outcome after its waiters are bound.
    member internal _.Drained() : Task<unit> =
        let tcs =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        agent.Post(Drained tcs)
        tcs.Task

/// The scan's published state. Discovery and checks never run in its writer.
type private ScanAgentState =
    {
        ScanState: ScanState
        Generation: int64
        LastFingerprint: Set<string * int64>
        /// The last completed scan that checked every file it dispatched: the moment it
        /// began reading the tree (`Stopwatch` ticks) and its completed state. A request
        /// admitted before that moment is answered by that scan — see `scanAnswers`.
        Covered: (int64 * ScanState) option
    }

[<NoComparison; NoEquality>]
type private ScanRequest =
    /// A scan request and the moment it was admitted (`Stopwatch` ticks).
    | RunScan of admittedAt: int64
    /// Test seam: replace the published scan state.
    | SetScanState of ScanState

/// The scan supervisor and the signal its waiters use. Reads come from the published row
/// and never wait for a scan.
[<NoComparison; NoEquality>]
type ScanAgent = private ScanAgent of SupervisedWork.Queue<ScanAgentState, ScanRequest> * ScanSignal

/// Admit a scan request and bind later `WaitForScan` waiters to it. Returns the request's
/// receipt once the admission is published.
let private admitScan (ScanAgent(owner, signal)) ct : Async<Task<unit>> =
    async {
        match!
            owner.SubmitAsync(RunScan(Diagnostics.Stopwatch.GetTimestamp()), ct)
            |> Async.AwaitTask
        with
        | Ok receipt ->
            signal.ObserveScan(receipt, fun () -> owner.State.Generation)
            return receipt
        | Result.Error failure -> return raise failure
    }

/// Admit a scan and wait for its receipt, raising the scan's own failure.
let private requestScan scanAgent ct =
    async {
        let! receipt = admitScan scanAgent ct
        let! settled = receipt.ContinueWith(fun (settled: Task<unit>) -> settled) |> Async.AwaitTask

        if not settled.IsCompletedSuccessfully then
            raise (settled.Exception.GetBaseException())
    }

let private getScanGeneration (ScanAgent(owner, _)) = owner.State.Generation

let private getScanStatus (ScanAgent(owner, _)) = owner.State.ScanState

let private setScanStatus (ScanAgent(owner, _)) state =
    owner.Submit(SetScanState state, CancellationToken.None)
    |> SupervisedWork.waitWithin SupervisedWork.AdmissionBound

let private closeScan (ScanAgent(owner, _)) = owner.Close()

/// Centralized failure handler for daemon batch/scan steps. `processBatch` and
/// `performScan` transitively call FCS, MSBuild, and arbitrary plugin Update
/// functions — there isn't a smaller exception type that captures "anything from
/// this layer", so the broad `Exception` catch is justified at this boundary. The
/// policy lives here rather than inlined at three call sites:
///   - `OperationCanceledException` is NOT a failure: it's the cancellation
///     signal threaded through `CancellationToken`. Re-raise so async
///     pipelines unwind; the caller decides whether to enter idle.
///   - Anything else is logged with `ex.ToString()` (full stack + inner
///     chain — `ex.Message` alone strips the diagnostic trail) and returned
///     as `Error`. Callers fall back to idle to keep the daemon responsive.
let internal runDaemonStep (label: string) (work: Async<'T>) : Async<Result<'T, exn>> =
    async {
        try
            let! r = work
            return Ok r
        with
        | :? OperationCanceledException as ex ->
            Logging.debug "daemon" $"%s{label} cancelled"
            return raise ex
        | ex ->
            Logging.error "daemon" $"%s{label} failed: %s{ex.ToString()}"
            return Result.Error ex
    }

/// Dependencies for processBatch, bundled to avoid a long closure capture list.
[<NoComparison; NoEquality>]
type internal BatchContext =
    {
        Host: PluginHost
        /// Global FCS invalidation — drops every project's checker state plus
        /// the language-service root caches. Used by the full re-discovery
        /// path (repo-wide `.props` / solution / new-project changes).
        InvalidateFcs: (unit -> unit) option
        /// Scoped FCS invalidation — drops just the given projects' checker
        /// configurations, leaving every other project's state warm. Used by
        /// the scoped project-change path. Receives each affected project's
        /// *current* (pre-re-discovery) `FSharpProjectOptions`. `None` disables
        /// the scoped path (test daemons with a null checker).
        InvalidateFcsForProjects: (FSharpProjectOptions list -> unit) option
        RepoRoot: string
        Loader: IWorkspaceLoader
        MapOptions: Types.ProjectOptions list -> FSharpProjectOptions list
        Discovery: DiscoveryCoordinator
        Graph: ProjectGraph.ProjectGraph
        Pipeline: CheckPipeline
        DaemonCt: CancellationToken ref
        FcsSuppressedCodes: Set<int>
        ExcludePatterns: string list
        /// Per-daemon content-dedup tracker. Scoped per instance so a hash
        /// written by one daemon never suppresses a genuine first-observation
        /// change event in another daemon sharing the process (keys are absolute
        /// paths, so a stale global entry would collide exactly). See
        /// `ContentDedup.Tracker`.
        ContentTracker: ContentDedup.Tracker
        /// Monotonic counter bumped per `InSessionBatch` `BatchChecked` emitted
        /// from `processBatch`. Per-trigger generation lets subscribers dedup
        /// "latest in-session cohort" without colliding with scan generations
        /// (which use the scan supervisor's own counter). Boxed `ref` so the
        /// long-lived BatchContext shares state across batches.
        InSessionBatchGen: int64 ref
        /// Deps-freshness gate: given a project's `.fsproj` path, decides
        /// whether its restored `obj/project.assets.json` is in sync with its
        /// declared deps (running a one-shot restore to recover) before FCS
        /// analysis. `None` disables the gate (test daemons with a null
        /// checker). See `DepsFreshness.evaluateProject`.
        DepsGate: (string -> DepsFreshness.GateResult) option
        /// What this daemon does differently as a session of a repository host.
        Seams: DaemonHosting.HostingSeams
    }

/// One cohort of watcher changes, and the `fshw format` requests flushed with it.
[<NoComparison; NoEquality>]
type private ChangeRequest =
    {
        Changes: FileChangeKind list
        FormatReplies: TaskCompletionSource<string> list
        /// When the watcher reported the earliest change in this request.
        SeenAt: DateTime
    }

/// The line a checked change cohort writes: its in-session epoch, how long after the
/// watcher reported its first change it was checked, and how many files it checked.
/// Benchmarks read settle latency from it, so its shape is kept stable.
let internal settledLine (epoch: int64) (after: TimeSpan) (files: int) : string =
    $"settled epoch=%d{epoch} after=%d{int64 after.TotalMilliseconds}ms files=%d{files}"

/// How long a change batch waited for a settled project model before checking
/// anything, when that wait is long enough to explain a slow settle.
let internal captureWaitLine (waited: TimeSpan) : string option =
    if waited >= TimeSpan.FromMilliseconds 100.0 then
        Some $"change batch waited %d{int64 waited.TotalMilliseconds}ms for the project model"
    else
        None

/// The reply `fshw format` prints. It names the set that was offered, and the formatter
/// that ran over it — or the reason none did. `formatted 0 files` on its own was the
/// defect: the same text for "every registered file is clean" and "no
/// formatter was consulted", and no version to compare against CI's.
let renderFormatAll (offered: string list) (run: PluginHost.PreprocessorsRun) : string =
    match run.Refused with
    | [] ->
        match run.Evidence with
        | [] ->
            $"formatted 0 of %d{offered.Length} files — no preprocessor is registered (`format` is off in .fshw.json)"
        | evidence ->
            $"formatted %d{run.Modified.Length} of %d{offered.Length} files — "
            + String.concat "; " evidence
    | refused ->
        let reasons =
            refused
            |> List.map (fun (name, reason) -> $"%s{name}: %s{reason}")
            |> String.concat "; "

        $"format refused — %s{reasons}"

/// How many paths a log line names before it summarizes the rest.
let private namedPathLimit = 10

/// The line a change batch writes before it checks: how many files it will check and the
/// changed files that caused it (the rest are dependents), repository-relative, the first
/// `namedPathLimit` by name. Info level, because a batch that re-checks a thousand files
/// is the question the log has to answer, and at debug it answered nothing.
let internal checkingAfterChangeLine (repoRoot: string) (triggers: string list) (total: int) : string =
    let rel (path: string) =
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/')

    let named =
        triggers |> List.truncate namedPathLimit |> List.map rel |> String.concat ", "

    let more =
        if triggers.Length > namedPathLimit then
            $" and %d{triggers.Length - namedPathLimit} more"
        else
            ""

    $"Checking %d{total} files after change — %d{triggers.Length} changed [%s{named}%s{more}], %d{max 0 (total - triggers.Length)} dependent"

/// The line naming the project inputs whose content changed, repository-relative, or
/// `None` when none did. An `obj/project.assets.json` is a restore's write (package
/// graph), a `.fsproj` / `.props` an edit of the project itself.
let internal projectChangeLine (repoRoot: string) (changed: string list) : string option =
    match changed with
    | [] -> None
    | paths ->
        let describe (path: string) =
            let rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/')

            if Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) then
                $"%s{rel} (restore rewrote the package graph)"
            else
                rel

        let named = paths |> List.map describe |> String.concat ", "
        Some $"project input content changed: %d{paths.Length} [%s{named}]"

/// Process a batch of debounced file changes: filter, re-discover projects if needed,
/// run preprocessors, emit events, and check files. Raises `ModelSupersededException`
/// when a publication meets a model newer than the one the attempt captured.
let private processBatchAttempt
    (ctx: BatchContext)
    (seenAt: DateTime)
    (changes: FileChangeKind list)
    (suppressed: Set<string>)
    (hasContentChanged: string -> bool)
    =
    async {
        // An incremental batch — the FCS re-check a file
        // change provokes while a check is already waiting — is daemon wall time no
        // plugin owns. One record per batch, on every exit.
        use batchPhase = ctx.Host.Phases.Begin DaemonPhases.Phase.Check
        // The cohort reads the live graph, and publishes only while the model it
        // captured is still current. An in-batch rediscovery captures its own result.
        let captureModel () = ctx.Discovery.Capture id
        let captureStarted = System.Diagnostics.Stopwatch.StartNew()
        let! initialModel = captureModel ()
        captureWaitLine captureStarted.Elapsed |> Option.iter (Logging.debug "daemon")
        let mutable batchModel = initialModel
        // What the seal carries from the model this cohort replaced. See `RetainedResults`.
        let mutable retained: RetainedResults option = None

        let publishCurrent write =
            ctx.Discovery.WithCurrent(batchModel, write)

        let mutable sourceFiles = []
        let mutable projFiles = []
        let mutable hasSolution = false

        for c in changes do
            match c with
            | SourceChanged files -> sourceFiles <- files @ sourceFiles
            | ProjectChanged files -> projFiles <- files @ projFiles
            | SolutionChanged -> hasSolution <- true

        Logging.debug
            "daemon"
            $"processChanges: %d{sourceFiles.Length} source, %d{projFiles.Length} project, solution=%b{hasSolution}"

        for f in sourceFiles do
            Logging.debug "daemon" $"source: %s{f}"

        for f in projFiles do
            Logging.debug "daemon" $"project: %s{f}"

        // Filter out files written by preprocessors (suppress re-trigger)
        let filteredSourceFiles, remainingSuppressed =
            sourceFiles
            |> List.distinct
            |> List.fold
                (fun (accepted, sup) f ->
                    if Set.contains f sup then
                        Logging.debug "daemon" $"suppressed: %s{f}"
                        (accepted, Set.remove f sup)
                    else
                        (f :: accepted, sup))
                ([], suppressed)

        let mutable allSourceFiles =
            filteredSourceFiles
            |> List.rev
            |> List.filter (fun f ->
                let changed = hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        let projFilesChanged =
            projFiles
            |> List.distinct
            |> List.filter (fun f ->
                let changed = hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        // The WRITE that made a project look changed, named by its own path. The scoped
        // line below names the `.fsproj` a change maps to, which for an
        // `obj/project.assets.json` rewrite is a file nobody touched.
        projectChangeLine ctx.RepoRoot projFilesChanged
        |> Option.iter (Logging.info "daemon")

        if hasSolution then
            publishCurrent (fun () -> ctx.Host.EmitFileChanged(SolutionChanged))

        if not projFilesChanged.IsEmpty || hasSolution then
            // Generated obj/ files (MSBuild's AssemblyInfo / AssemblyAttributes)
            // are in the ProjectGraph (it stores the raw ProjInfo SourceFiles)
            // but the CheckPipeline filters them out of its options, so feeding
            // them to FCS yields a spurious "not part of the project" error.
            // The pipeline's source list is authoritative for what's checkable.
            let checkableFilesOf (projects: AbsProjectPath list) =
                projects
                |> List.collect ctx.Graph.GetSourceFiles
                |> List.map AbsFilePath.value
                |> List.filter (fun f -> not (PathFilter.isGeneratedPath f))
                |> List.distinct

            // Decide scoped vs. full. Scoped applies only when every changed
            // path maps to a known project (no `.props`, no new project, no
            // solution edit) AND a scoped FCS invalidator is wired.
            let scopedProjects =
                if hasSolution then
                    None
                else
                    resolveAffectedProjects (ctx.Pipeline.GetRegisteredProjects()) projFilesChanged

            match scopedProjects, ctx.InvalidateFcsForProjects with
            | Some affectedFsprojs, Some invalidateScoped when not (List.isEmpty affectedFsprojs) ->
                // ── Scoped path ──────────────────────────────────────────────
                // Re-check the affected projects AND their transitive
                // dependents (a dependent's compilation can break when the
                // changed project's public surface changes). Everything else
                // keeps its warm FCS + cached check results.
                let recheckProjects =
                    affectedFsprojs
                    |> List.map AbsProjectPath.create
                    |> List.collect ctx.Graph.GetTransitiveDependents
                    |> List.distinct

                // Snapshot current options BEFORE re-discovery — these are the
                // configs FCS currently holds and the keys the cache currently
                // uses. Used for FCS invalidation and (via InvalidateFile) cache
                // eviction so dependents whose options-hash is unchanged still
                // recompute instead of serving a stale cached result.
                let oldOpts =
                    recheckProjects
                    |> List.choose (fun p -> ctx.Pipeline.GetProjectOptions(AbsProjectPath.value p))

                // Every registered project's options, so the projects this path leaves warm
                // can be shown to be unchanged by the re-discovery rather than assumed to be.
                let registeredOptions () =
                    ctx.Pipeline.GetRegisteredProjects()
                    |> List.choose (fun p ->
                        ctx.Pipeline.GetProjectOptions p
                        |> Option.map (fun options -> AbsProjectPath.create p, options))
                    |> Map.ofList

                let optionsBefore = registeredOptions ()

                for f in checkableFilesOf recheckProjects |> List.map AbsFilePath.create do
                    ctx.Pipeline.InvalidateFile f

                invalidateScoped oldOpts

                Logging.info
                    "daemon"
                    $"Scoped project change — %d{affectedFsprojs.Length} changed%s{describeChangedProjects ctx.RepoRoot affectedFsprojs} + %d{recheckProjects.Length - affectedFsprojs.Length} dependent project(s) invalidated; rest stay warm"

                let! _ =
                    rediscoverAndClearRemoved
                        ctx.RepoRoot
                        ctx.Loader
                        ctx.MapOptions
                        ctx.Discovery
                        ctx.Graph
                        ctx.Pipeline
                        ctx.Host
                        "daemon"
                        ctx.ExcludePatterns
                        ctx.ContentTracker
                        false // keep unrelated projects' check cache

                let! refreshedModel = captureModel ()
                batchModel <- refreshedModel

                // A project outside the re-check set whose options the re-discovery changed
                // (or which it newly registered) cannot keep its results: it joins the
                // re-check. Every other one kept its options, and so its results.
                let recheckSet = Set.ofList recheckProjects
                let optionsHash = CheckCache.getProjectOptionsHash

                let drifted, unchanged =
                    registeredOptions ()
                    |> Map.toList
                    |> List.filter (fun (project, _) -> not (Set.contains project recheckSet))
                    |> List.partition (fun (project, options) ->
                        Map.tryFind project optionsBefore |> Option.map optionsHash
                        <> Some(optionsHash options))

                if not drifted.IsEmpty then
                    Logging.info
                        "daemon"
                        $"Scoped re-discovery changed the options of %d{drifted.Length} project(s) outside the change; re-checking them too"

                    let driftedProjects = drifted |> List.map fst

                    for f in checkableFilesOf driftedProjects |> List.map AbsFilePath.create do
                        ctx.Pipeline.InvalidateFile f

                    invalidateScoped (driftedProjects |> List.choose (fun p -> Map.tryFind p optionsBefore))

                if not projFilesChanged.IsEmpty then
                    publishCurrent (fun () -> ctx.Host.EmitFileChanged(ProjectChanged projFilesChanged))

                // Re-derive source files from the refreshed graph (membership
                // may have shifted) for the same project set.
                allSourceFiles <-
                    (allSourceFiles @ checkableFilesOf (recheckProjects @ List.map fst drifted))
                    |> List.distinct

                retained <-
                    modelGenerationOf initialModel
                    |> Option.map (fun generation ->
                        { FromModelGeneration = generation
                          Files =
                            checkableFilesOf (List.map fst unchanged)
                            |> List.map AbsFilePath.create
                            |> Set.ofList })

            | _ ->
                // ── Full path ────────────────────────────────────────────────
                // Repo-wide change (.props / solution), a project the graph
                // doesn't know yet, or no scoped invalidator (null-checker test
                // daemon). Drop everything and re-check the whole graph.
                Logging.info "daemon" "Project/solution change detected — full re-discovery"

                ctx.InvalidateFcs |> Option.iter (fun invalidate -> invalidate ())

                let! _ =
                    rediscoverAndClearRemoved
                        ctx.RepoRoot
                        ctx.Loader
                        ctx.MapOptions
                        ctx.Discovery
                        ctx.Graph
                        ctx.Pipeline
                        ctx.Host
                        "daemon"
                        ctx.ExcludePatterns
                        ctx.ContentTracker
                        true

                let! refreshedModel = captureModel ()
                batchModel <- refreshedModel

                Logging.info
                    "daemon"
                    $"Re-discovery complete: %d{ctx.Graph.GetAllProjects().Length} projects, %d{ctx.Pipeline.GetAllRegisteredFiles().Length} files"

                if not projFilesChanged.IsEmpty then
                    publishCurrent (fun () -> ctx.Host.EmitFileChanged(ProjectChanged projFilesChanged))

                allSourceFiles <-
                    (allSourceFiles @ checkableFilesOf (ctx.Graph.GetAllProjects()))
                    |> List.distinct

        let batchStartedAt = System.DateTime.UtcNow
        let dispatchedFiles = ResizeArray<AbsFilePath>()

        let seal () =
            publishCurrent (fun () ->
                let nextGen =
                    System.Threading.Interlocked.Increment(&ctx.InSessionBatchGen.contents)

                let completedAt = System.DateTime.UtcNow

                ctx.Host.EmitBatchChecked
                    { Trigger = InSessionBatch changes
                      Files = dispatchedFiles |> List.ofSeq
                      Generation = nextGen
                      ModelGeneration = modelGenerationOf batchModel
                      Retained = retained
                      StartedAt = batchStartedAt
                      CompletedAt = completedAt }

                Logging.info "check" (settledLine nextGen (completedAt - seenAt) dispatchedFiles.Count))

        if not allSourceFiles.IsEmpty then
            let modifiedByPreprocessors = ctx.Host.RunPreprocessors(allSourceFiles).Modified
            // A file a preprocessor rewrote is what the build must now see, whether or not
            // the batch held it: a generated source joins the batch it was produced for.
            allSourceFiles <- (allSourceFiles @ modifiedByPreprocessors) |> List.distinct
            // After the preprocessors' rewrites, before any check. See `BeginGeneration`.
            ctx.Pipeline.BeginGeneration()

            let newSuppressed =
                Set.union remainingSuppressed (Set.ofList modifiedByPreprocessors)

            let absSourceFiles = allSourceFiles |> List.map AbsFilePath.create

            let changedProjects =
                absSourceFiles
                |> List.collect (fun f -> ctx.Graph.GetProjectsForFile(f))
                |> List.distinct

            let changedProjectSet = Set.ofList changedProjects

            let dependentProjectFiles =
                changedProjects
                |> List.collect (fun p -> ctx.Graph.GetTransitiveDependents(p))
                |> List.distinct
                |> List.filter (fun p -> not (Set.contains p changedProjectSet))
                |> List.collect (fun proj -> ctx.Graph.GetSourceFiles(proj))
                |> List.map AbsFilePath.value

            let allFilesToCheck =
                (allSourceFiles @ dependentProjectFiles)
                |> List.map AbsFilePath.create
                |> List.distinct

            publishCurrent (fun () ->
                ctx.Host.EmitFileChanged(SourceChanged(allFilesToCheck |> List.map AbsFilePath.value)))

            Logging.info "daemon" (checkingAfterChangeLine ctx.RepoRoot allSourceFiles allFilesToCheck.Length)
            let mutable checkedFiles = Set.empty
            let filesToCheckSet = allFilesToCheck |> Set.ofList

            // A file this cohort checks is not carried, even if its check never completes:
            // its standing result may describe content or dependencies this cohort changed.
            retained <-
                retained
                |> Option.map (fun carried ->
                    { carried with
                        Files = Set.difference carried.Files filesToCheckSet })

            let tiers = ctx.Graph.GetParallelTiers()

            let emitResults (results: FileCheckResult option array) =
                for result in results do
                    match result with
                    | Some checkResult ->
                        Logging.debug
                            "daemon"
                            $"EmitFileChecked: %s{Path.GetFileName(AbsFilePath.value checkResult.File)}"

                        publishCurrent (fun () ->
                            let checkResult =
                                { checkResult with
                                    ModelGeneration = modelGenerationOf batchModel }

                            dispatchedFiles.Add(checkResult.File)
                            ctx.Host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics ctx.FcsSuppressedCodes ctx.Host checkResult)
                    // Unlike the cold scan (see `runChecksWithRetry`), the batch
                    // path does NOT retry a cancelled (`None`) check. Batch
                    // cancellations are self-healing: `CancelPreviousCheck` only
                    // cancels an in-flight check when a NEWER event's check of the
                    // same file supersedes it, so that newer check (or the next
                    // debounced batch) covers the file. The cold scan has no such
                    // later event, which is why the retry lives there, not here.
                    | None -> ()

            for tier in tiers do
                let tierChecks = ResizeArray<Async<FileCheckResult option>>()

                for proj in tier do
                    let projPath = AbsProjectPath.value proj

                    let projFiles =
                        ctx.Graph.GetSourceFiles(proj) |> List.filter filesToCheckSet.Contains

                    checkedFiles <- Set.union checkedFiles (Set.ofList projFiles)

                    // Deps-freshness gate — see `applyDepsGate`.
                    if applyDepsGate ctx.DepsGate ctx.Host projPath then
                        match ctx.Pipeline.GetProjectOptions(projPath) with
                        | Some options ->
                            for file in projFiles do
                                tierChecks.Add(ctx.Pipeline.CheckFileWithOptions(file, options, ctx.DaemonCt.Value))
                        | None ->
                            for file in projFiles do
                                tierChecks.Add(ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))

                let! results = tierChecks |> Seq.toList |> Async.Parallel |> CheckCaller.within "change batch"

                emitResults results

            // Check files not belonging to any project (e.g. standalone .fsx files)
            let uncovered = Set.difference filesToCheckSet checkedFiles |> Set.toList

            if not uncovered.IsEmpty then
                let! results =
                    uncovered
                    |> List.map (fun file -> ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))
                    |> Async.Parallel
                    |> CheckCaller.within "change batch"

                emitResults results

            // Empty cohorts (every file filtered as content-unchanged or no
            // results from the pipeline) skip the emit — there's nothing to
            // "flush and decide" against.
            if dispatchedFiles.Count > 0 then
                seal ()

            batchPhase.Complete(Some $"change batch: %d{dispatchedFiles.Count} file(s) checked")
            return newSuppressed
        else
            // A batch that REPLACED the model with one that has no checkable files still
            // owes the new model its seal, exactly as the cold scan seals an empty cohort:
            // "every checkable file of this generation has been dealt with" is true of an
            // empty model too. Unsealed, the previous generation's seal no longer describes
            // the model, an analysis-only daemon earns no receipt for the new one, and its
            // check reports "no evidence receipt" (exit 2) until something else re-scans.
            if modelGenerationOf batchModel <> modelGenerationOf initialModel then
                seal ()

            return remainingSuppressed
    }

/// How many attempts a change cohort makes before a model that keeps changing fails it.
let internal changeBatchAttemptLimit = 5

/// Changes a failed cohort still owes, with the paths whose content it already admitted.
/// The content tracker answers "changed" once per content, so these paths would
/// otherwise be dropped as unchanged when the same bytes arrive again.
type internal OwedChanges =
    { Changes: FileChangeKind list
      Admitted: Set<string> }

/// A cohort whose model was replaced on every attempt. Its changes stay owed.
type internal ModelKeptChangingException(attempts: int, owed: OwedChanges) =
    inherit
        InvalidOperationException(
            $"The project model kept changing during the batch: it was replaced during each of %d{attempts} attempts. Its changes stay owed to the next batch."
        )

    member _.Owed = owed

/// The change worker's state: paths whose watcher echo is suppressed, and the changes a
/// failed batch still owes.
[<NoComparison; NoEquality>]
type private ChangeWorkerState =
    { Suppressed: Set<string>
      Owed: OwedChanges option }

/// Run a change cohort against the current model. A superseded attempt may have published
/// some results, but never its seal; the next attempt runs the same changes against the
/// model that replaced it, inside the same owned request. After
/// `changeBatchAttemptLimit` superseded attempts the cohort fails with
/// `ModelKeptChangingException`, which carries what it owes.
let internal processBatch
    (ctx: BatchContext)
    (seenAt: DateTime)
    (changes: FileChangeKind list)
    (suppressed: Set<string>)
    (alreadyAdmitted: Set<string>)
    =
    // Asking the tracker again on a retry would drop exactly the inputs this cohort admitted.
    let admitted =
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal)

    for path in alreadyAdmitted do
        admitted[path] <- true

    let hasContentChanged path =
        admitted.GetOrAdd(path, ctx.ContentTracker.HasContentChanged)

    let rec completeCurrent attempt =
        async {
            ctx.DaemonCt.Value.ThrowIfCancellationRequested()

            try
                return! processBatchAttempt ctx seenAt changes suppressed hasContentChanged
            with :? ModelSupersededException when attempt < changeBatchAttemptLimit ->
                Logging.debug "changes" "model superseded; running the cohort against the current model"
                return! completeCurrent (attempt + 1)
        }

    async {
        try
            return! completeCurrent 1
        with :? ModelSupersededException ->
            let owed =
                { Changes = changes
                  Admitted =
                    admitted
                    |> Seq.choose (fun entry -> if entry.Value then Some entry.Key else None)
                    |> Set.ofSeq }

            return raise (ModelKeptChangingException(changeBatchAttemptLimit, owed))
    }

/// Format elapsed as human-readable "5m 3s" / "45s" / "1h 12m". Public so
/// consumers + tests can share the same cadence. Delegates to
/// `PluginWedge.formatElapsed` — ONE definition, so the daemon's wait logs and
/// the wedge wording can never drift apart.
let formatElapsed (ts: System.TimeSpan) = PluginWedge.formatElapsed ts

/// Pure formatter for "Waiting for plugins" log line. Includes plugin elapsed
/// time + each active subtask's label + elapsed, so a stuck daemon is
/// diagnosable from a single log line.
///
/// Example output: `test-prune (25m 12s) [Intelligence.Tests.Unit 12m 3s, Intelligence.Tests.Database 10m 1s]`
/// The bounded work `plugin` declared over itself and still has in flight, by the label
/// it declared (`SupervisedWork.declare` names it `<plugin>: <label>`).
let internal boundedWorkOf (work: PluginWorkOwner.HostSnapshot) (plugin: string) =
    let prefix = $"%s{plugin}: "

    work.OperationsInFlight
    |> List.choose (fun (name, startedAt, deadline) ->
        if name.StartsWith(prefix, System.StringComparison.Ordinal) then
            Some(name.Substring prefix.Length, startedAt, deadline)
        else
            None)

let formatPluginWait
    (now: System.DateTime)
    (pluginName: string)
    (since: System.DateTime)
    (subtasks: (string * System.DateTime) list)
    : string =
    let elapsed = formatElapsed (now - since)

    let subtaskPart =
        match subtasks with
        | [] -> ""
        | ts ->
            let joined =
                ts
                |> List.map (fun (label, startedAt) -> $"%s{label} %s{formatElapsed (now - startedAt)}")
                |> String.concat ", "

            $" [%s{joined}]"

    $"%s{pluginName} (%s{elapsed}){subtaskPart}"

/// How long "nothing is Running, yet some plugin still reports work in flight"
/// may persist before the wait declares that plugin WEDGED and fails.
///
/// That state is legitimate only for the hand-off between a run finishing and its
/// completion message being handled — milliseconds. While a run is genuinely
/// executing the plugin's status is `Running`, a different branch entirely, so a
/// long test suite never trips this.
///
/// The alternative is a plugin whose inflight count never drains holding
/// `WaitForComplete` for the full hour-long timeout and then reporting "all
/// terminal but quiescence check failed". Failing in five minutes and naming the
/// plugin is the point.
let internal waitForAllTerminalBusyStallThreshold = System.TimeSpan.FromMinutes(5.0)

/// What the wedge detector knows about progress: how much work the host had FINISHED
/// when that count last moved, and when that was.
///
/// Keyed on progress, NOT on busy-set identity: one plugin draining a long
/// `FileChecked` backlog is busy continuously with nothing Running, and the busy set
/// stays exactly `["test-prune"]` for the whole drain, so a clock keyed on that set
/// never resets and fires on a healthy check of a large repo (observed: three
/// uninterrupted minutes of it on a green run). `CompletedDispatches` moves on every
/// event a plugin finishes, so a drain can never look stalled and a stopped agent
/// always does.
///
/// Pure over its samples, so whether a sequence of samples is a stall does not depend on
/// how quickly a scheduler delivers them.
type internal StallWatch =
    { Finished: int64
      Since: System.DateTime }

module internal StallWatch =
    /// No count seen yet: the first sample always moves it.
    let start (now: System.DateTime) : StallWatch = { Finished = -1L; Since = now }

    /// Fold in one sample of the finished count. A count that moved restarts the clock.
    let observe (finished: int64) (now: System.DateTime) (watch: StallWatch) : StallWatch =
        if finished <> watch.Finished then
            { Finished = finished; Since = now }
        else
            watch

    /// Has nothing finished for `threshold`, as of `now`?
    let stalled (threshold: System.TimeSpan) (now: System.DateTime) (watch: StallWatch) : bool =
        now - watch.Since >= threshold

/// Sentinel message for shutdown-driven cancellation. Anything that surfaces
/// this is a daemon-teardown event, not a plugin failure.
[<Literal>]
let internal daemonShuttingDownMessage = "daemon shutting down"

/// Wait for all plugins to settle: one pinned publication of the host's owned work
/// holds none, and the host has been quiet through a 200ms quiescence window measured
/// from the most recent host activity (event dispatch or status change).
///
/// Rest is read from the owner publication, never from reported statuses: an event is
/// owned from admission until its state is committed, an exclusive run from its claim
/// until its result fold commits, and a dispatch fan-out until every recipient has
/// admitted its event. A plugin that merely reports `Running` owns nothing. The
/// quiescence window covers work the daemon has not yet handed to the host (a scan
/// between two files). Times out with TimeoutException after the specified timeout.
///
/// `ct` is the daemon's shutdown token. When it fires mid-wait, the returned
/// task faults with OperationCanceledException so the in-flight WaitForComplete
/// RPC propagates to the client as an error — without this, foreground
/// processes blocked on the daemon could either hang (in-process callers) or
/// race the OS pipe teardown for a clean exit.
///
/// The one wait loop. `restRequires` is the caller's EXTRA condition on the same pinned
/// publication that answers rest — nothing for a settling wait, evidence for the current
/// model for the verdict-bearing one. Taken as a parameter rather than read from a flag so
/// the two waits differ by the question they ask, not by a mode the loop switches on.
let private waitCoreWith
    (restRequires: PluginWorkOwner.HostSnapshot -> bool)
    (host: PluginHost)
    (timeout: System.TimeSpan)
    (stallThreshold: System.TimeSpan)
    (ct: CancellationToken)
    : Task<unit> =
    // TimeSpan.MaxValue signals "no timeout"; adding it to UtcNow overflows, so skip
    // deadline computation entirely in that case and rely on the MaxValue guard in loop.
    let deadline =
        if timeout = System.TimeSpan.MaxValue then
            System.DateTime.MaxValue
        else
            System.DateTime.UtcNow + timeout

    let mutable lastLogTime = System.DateTime.UtcNow

    let getRunningPlugins () =
        let now = System.DateTime.UtcNow
        let work = host.WorkSnapshot

        host.GetAllStatuses()
        |> Map.toList
        |> List.choose (fun (name, s) ->
            match s with
            | Running since ->
                let subtasks =
                    host.GetActivitySnapshot(name).Subtasks
                    |> List.map (fun t -> t.Key, t.StartedAt)

                // The subtasks are already in the wait form; the bounded work and the
                // backlog are what the form did not say, and what a reader of a long
                // wait needs to see the plugin is on.
                let beyondSubtasks =
                    PluginWedge.describeAwaiting now [] (boundedWorkOf work name) (work.PendingEventsOf name)

                let rendered = formatPluginWait now name since subtasks

                Some(
                    if beyondSubtasks = "" then
                        rendered
                    else
                        $"%s{rendered} — %s{beyondSubtasks}"
                )
            | _ -> None)

    /// Is anything Running? `getRunningPlugins` answers this too, but pays for a
    /// per-plugin activity snapshot (up to 64 log lines and a 16-element history
    /// array) plus a formatted wait string, all of which the 50ms poll loop throws
    /// away.
    let anyRunning () =
        host.GetAllStatuses()
        |> Map.exists (fun _ s ->
            match s with
            | Running _ -> true
            | _ -> false)

    let formatTimeoutDetail () =
        match getRunningPlugins () with
        | [] ->
            // Nothing is Running, so the wait died on one of the two legs
            // `allPluginsAtRest` requires: owned work that never retires, or — for the
            // verdict-bearing wait — a model nothing has earned an answer about. Name the
            // one that actually blocked rather than reporting both at once.
            let busy = host.BusyPluginNames()
            let statuses = host.GetAllStatuses()
            let snapshot = host.WorkSnapshot

            if statuses.IsEmpty then
                // A plugin-free host owes no evidence, so only work the host itself owns
                // (a scan, a discovery) can hold it — name that work.
                let owned = String.concat ", " snapshot.BusyNames
                $"no plugins are registered, but the host still owns work: %s{owned}"
            else
                let busyNames = String.concat ", " busy

                let reasons =
                    [ if not busy.IsEmpty then
                          $"work still OWNED (an event not yet committed, a queued command, or an exclusive run before its result commits): %s{busyNames}"

                      // Only the verdict-bearing wait can block here, and only on a host
                      // that mints evidence: say WHICH model went unanswered, because the
                      // ordinary cause is a run that was superseded by a newer model
                      // rather than anything being stuck.
                      if not (restRequires snapshot) then
                          match snapshot.ProjectModel with
                          | ProjectModel.Observation.Available model ->
                              $"nothing has earned evidence for project model generation %d{model.Generation}: no test receipt, no analysis receipt and no completed build failure names it"
                          | other -> $"no evidence, and no available project model to earn it for (%A{other})" ]

                match reasons with
                | [] ->
                    // Every named leg looks satisfiable, yet the loop did not exit:
                    // report the raw state rather than a reassuring summary.
                    let dump =
                        statuses
                        |> Map.toList
                        |> List.map (fun (n, s) -> $"%s{n}=%A{s}")
                        |> String.concat ", "

                    $"all legs appear satisfied yet the wait did not resolve — statuses: %s{dump}"
                | rs -> "nothing running, but " + String.concat "; " rs
        | running -> $"""still running: %s{String.concat ", " running}"""

    let logRunningPlugins () =
        let now = System.DateTime.UtcNow

        if (now - lastLogTime).TotalSeconds >= 10.0 then
            lastLogTime <- now

            // "still running: X" is progress, which is what someone watching a
            // check wants, so it stays at info. The "nothing running, but ..."
            // breakdown is a DIAGNOSTIC at debug: a healthy run emits it for
            // minutes at a stretch while a plugin drains a large FileChecked
            // backlog, and it is not the wedge signature (a dead agent is
            // identified by `FaultedPlugins`). It is still printed in full where it
            // decides something — the timeout message and the wedge failure below.
            //
            // `Logging.debug` takes an ALREADY-BUILT string, so guard the debug arm
            // on the level: at the default Info level the whole detail would
            // otherwise be computed every 10s and thrown away — a status
            // round-trip, an activity snapshot per running plugin, and in the
            // all-legs-satisfied case a reflection-based `%A` dump of every status.
            if anyRunning () then
                Logging.info "wait" (formatTimeoutDetail ())
            elif Logging.isEnabled Logging.LogLevel.Debug then
                Logging.debug "wait" (formatTimeoutDetail ())

    let allPluginsAtRest () =
        // ONE publication answers it. An event is owned from admission until its state is
        // committed, an exclusive run from its claim until its result fold commits, and a
        // dispatch fan-out until every recipient has admitted its event — so a handoff
        // from one owner to the next can never be read as rest between two reads, and
        // there is nothing left for a quiescence window to cover.
        //
        // A host with no plugins registered is at rest once it owns nothing: every
        // caller registers its plugins before it can wait, so an empty registry is the
        // configuration, not a host that has not started yet.
        let snapshot = host.WorkSnapshot

        not snapshot.IsBusy && restRequires snapshot

    // Wedge detection state, keyed on `CompletedDispatches` — see `StallWatch`.
    let mutable progress = StallWatch.start System.DateTime.UtcNow

    let checkForWedgedPlugin () =
        // A plugin whose message loop died reports work in flight forever, so
        // waiting on it can only time out. This is the cheapest check and the only
        // certain one — no threshold, no inference from silence. Everything below
        // is the heuristic backstop for a stall nobody reported.
        match host.FaultedPlugins() with
        | (name, ex) :: _ ->
            raise (
                System.TimeoutException(
                    $"WaitForComplete: plugin '%s{name}' is DEAD — its message loop crashed, so it will report work in flight forever and this wait can never resolve. "
                    + $"The crash: %s{ex.Message}. "
                    + "This is a bug in fshw, not in the tree being checked. See logs/daemon.log for the full stack, then `fshw stop` to reclaim the daemon."
                )
            )
        | [] ->

            // Cheapest test first, and the one that is almost always false.
            // `AnyPluginBusy` is N volatile reads with short-circuiting and no
            // allocation; everything below it costs a blocking round-trip to the
            // status agent, and this runs 20x a second for a wait designed to last
            // up to an hour (~72,000 round-trips, each copying every running
            // plugin's activity tail).
            let busy = if host.AnyPluginBusy() then host.BusyPluginNames() else []

            let now = System.DateTime.UtcNow
            progress <- StallWatch.observe (host.CompletedDispatches()) now progress

            // Only meaningful when NOTHING is Running: a busy plugin that is also
            // Running is simply working. `anyRunning` is checked LAST because it is
            // the dearest of the three and, given no progress for the threshold, the
            // rarest to change the answer.
            //
            // A BOUNDED operation that is live and inside its deadline is working too,
            // and is the one shape this detector used to get wrong: a cold discovery owns
            // work for minutes while no plugin event completes anywhere, which is
            // byte-for-byte the signature of a stuck inflight count. The difference is not
            // in how it looks but in what will happen — that work has a deadline which
            // will fail it, and an event fold that never returns has nothing. So a live
            // supervised operation counts as progress while it is inside its deadline;
            // past it, its own failure is recorded, `SupervisedWorkInFlight` goes false,
            // and this fires as before.
            if
                not busy.IsEmpty
                && StallWatch.stalled stallThreshold now progress
                && not (anyRunning ())
                && not host.WorkSnapshot.SupervisedWorkInFlight
            then
                let joined = String.concat ", " busy

                raise (
                    System.TimeoutException(
                        $"WaitForComplete: plugin(s) WEDGED — %s{joined} reported work in flight for %s{formatElapsed stallThreshold} with nothing Running and no event finishing anywhere in the host. "
                        + "That hand-off should take milliseconds, so this is a stuck inflight count (an event whose handler never returned, or an exclusive run whose completion was never posted), not slow work. "
                        + "Inspect logs/daemon.log around this timestamp, then `fshw stop` to reclaim the daemon."
                    )
                )

    let rec loop () =
        async {
            // Catches the race between cancellation and the first Async.Sleep —
            // see waitForAllTerminal's doc-comment for the full contract.
            if ct.IsCancellationRequested then
                raise (System.OperationCanceledException(daemonShuttingDownMessage, ct))

            checkForWedgedPlugin ()

            if timeout <> System.TimeSpan.MaxValue && System.DateTime.UtcNow >= deadline then
                let detail = formatTimeoutDetail ()

                raise (System.TimeoutException($"WaitForComplete timed out after %O{timeout} — %s{detail}"))

            if allPluginsAtRest () then
                return ()
            else
                logRunningPlugins ()
                do! Async.Sleep 50
                return! loop ()
        }

    Async.StartAsTask(loop (), cancellationToken = ct)

/// Settling wait: the host owns no work. Used by the in-process `RunOnce`/scan path,
/// which needs "everything admitted has been committed" and asks nothing about evidence.
/// Settling rest: the host owns no work, and nothing further is asked of it.
let internal waitForAllTerminalCore
    (host: PluginHost)
    (timeout: System.TimeSpan)
    (stallThreshold: System.TimeSpan)
    (ct: CancellationToken)
    : Task<unit> =
    waitCoreWith (fun _ -> true) host timeout stallThreshold ct

let internal waitForAllTerminal (host: PluginHost) (timeout: System.TimeSpan) (ct: CancellationToken) : Task<unit> =
    waitForAllTerminalCore host timeout waitForAllTerminalBusyStallThreshold ct

/// Does this publication hold evidence for the model it was taken under?
///
/// Three ways to have an answer about the current model: a test run earned a receipt, an
/// analysis-only cohort sealed one, or a build failed and said so. A host whose plugins
/// mint no evidence is asked nothing — the same rule the CLI's receipt gate uses, for the
/// same reason: "nobody owes a receipt" is not "a receipt is missing". A model that is not
/// available is not waited on either: the verdict refuses a green on it anyway, and
/// blocking here would hide that answer behind a timeout.
let internal hasEvidenceForCurrentModel (snapshot: PluginWorkOwner.HostSnapshot) =
    match snapshot.ProjectModel with
    | ProjectModel.Observation.Available model when snapshot.OffersEvidence ->
        let forModel generation = generation = model.Generation

        (snapshot.Evidence |> List.exists (fun evidence -> forModel evidence.Generation))
        || (snapshot.AnalysisEvidence
            |> List.exists (fun analysis -> forModel analysis.Generation))
        || (snapshot.CompletedFailures
            |> List.exists (fun failure -> forModel failure.Generation))
    | ProjectModel.Observation.Available _
    | ProjectModel.Observation.Unobserved
    | ProjectModel.Observation.Rediscovering _
    | ProjectModel.Observation.Unavailable _ -> true

/// Verdict-bearing wait, used ONLY by the `WaitForComplete` RPC path: the host owns no
/// work AND holds evidence for the model it is answering about. A cold daemon that has
/// simply never run therefore cannot resolve as a vacuous clean — not because a status
/// says so, but because nothing earned a receipt yet.
/// Evidence-bearing rest: the host owns no work AND holds an answer about the model it
/// would be answering for.
let internal waitForEvidenceCore
    (host: PluginHost)
    (timeout: System.TimeSpan)
    (stallThreshold: System.TimeSpan)
    (ct: CancellationToken)
    : Task<unit> =
    // THE WAIT IS THE OBSERVATION. A client blocked here is a reason not to exit for
    // idleness, and the lease says so in the same publication that answers whether the
    // host owns any work — so the two facts are read together and cannot disagree. This
    // replaces a counter the daemon incremented around the RPC and idle-exit read
    // separately: a watcher is not work, so the lease never makes the host busy.
    //
    // Taken HERE rather than at the RPC so every exit releases it — verdict, timeout and
    // shutdown cancellation alike — instead of relying on a `finally` at one call site.
    task {
        use _observation = host.WorkStore.Observe()
        return! waitCoreWith hasEvidenceForCurrentModel host timeout stallThreshold ct
    }

let internal waitForVerdict (host: PluginHost) (timeout: System.TimeSpan) (ct: CancellationToken) : Task<unit> =
    waitForEvidenceCore host timeout waitForAllTerminalBusyStallThreshold ct

/// Wait for a single named plugin to leave Running. Returns immediately if the
/// plugin is not registered or is already terminal. Polling-based; bounded by
/// `timeout`. Used by `performScan` to serialize "BuildPlugin completes"
/// before "FCS tier checks begin" on cold scans.
///
/// Why this exists: cold scans emit `FileChanged(SourceChanged files)` to all
/// subscribers, which kicks BuildPlugin into running `dotnet build`. MSBuild
/// then rewrites `obj/Debug/.../ref/*.dll`. If FCS tier checks run in parallel,
/// they read those same `-r:` paths mid-write, producing spurious
/// "type 'X' does not match type 'X'" CCU-mismatch diagnostics. Awaiting the
/// build's terminal status before FCS reads stabilizes the obj/ tree.
///
/// On a warm cache this costs milliseconds: BuildPlugin's task-cache replay emits
/// `BuildCompleted` synchronously inside the captured-events handler.
///
/// Parameterised on a status-reader so unit tests can drive deterministic state
/// sequences without Task.Delay; the PluginHost adapter below is the production
/// caller.
let internal waitForPluginTerminalIfRunningWith
    (getStatus: string -> PluginStatus option)
    (pluginName: string)
    (timeout: System.TimeSpan)
    : Async<unit> =
    async {
        let deadline =
            if timeout = System.TimeSpan.MaxValue then
                System.DateTime.MaxValue
            else
                System.DateTime.UtcNow + timeout

        // A status read is a snapshot read (`PluginHost.GetStatus`): it cannot time
        // out or wait on another thread, so there is no "unknown" answer to handle.
        let running () =
            match getStatus pluginName with
            | Some(Running _) -> true
            | _ -> false

        let before (limit: System.DateTime) = System.DateTime.UtcNow < limit

        // EmitFileChanged dispatches to mailboxes synchronously but plugins
        // transition to Running asynchronously when their handler runs. Give
        // the dispatch a brief settle window so we don't observe the plugin
        // as "Idle" right before it enters Running.
        let settleDeadline =
            System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(200.0)

        let mutable last = running ()

        while not last && before settleDeadline do
            do! Async.Sleep 25
            last <- running ()

        // If the plugin never entered Running (not registered, or finished
        // before we polled), there's nothing to wait for.
        last <- running ()

        if last then
            let mutable lastLogTime = System.DateTime.UtcNow

            while last && (timeout = System.TimeSpan.MaxValue || before deadline) do
                let now = System.DateTime.UtcNow

                if (now - lastLogTime).TotalSeconds >= 10.0 then
                    lastLogTime <- now
                    Logging.info "scan" $"Waiting for plugin '%s{pluginName}' to leave Running..."

                do! Async.Sleep 50
                last <- running ()

            if last then
                Logging.warn
                    "scan"
                    $"waitForPluginTerminalIfRunning: '%s{pluginName}' still Running after %O{timeout}; proceeding anyway"
    }

let internal waitForPluginTerminalIfRunning
    (host: PluginHost)
    (pluginName: string)
    (timeout: System.TimeSpan)
    : Async<unit> =
    waitForPluginTerminalIfRunningWith host.GetStatus pluginName timeout

/// Render a scan-state line from a LIVE completeness reading. `registered` and
/// `unchecked` come from the host's coverage set at read time (registered minus
/// currently-checked), so the rendered `complete:`/`incomplete:` numbers never
/// rot after incremental edits. The exact string formats are load-bearing:
/// `status`/`WaitForScan`/external tooling greps `complete:`/`incomplete:`/
/// `scanning:` — only the SOURCE of the numbers changed, not the shapes.
let internal formatScanStatusWith (registered: int) (unchecked: int) (scanState: ScanState) : string =
    match scanState with
    | ScanIdle -> "idle"
    | Scanning(total, completed, _) ->
        let pct = if total > 0 then completed * 100 / total else 0
        $"scanning: %d{completed}/%d{total} files (%d{pct}%%)"
    | ScanComplete elapsed ->
        if unchecked > 0 then
            // Non-ok: a truncated or stale-incomplete scan must not present as
            // clean. The checked count is (registered - unchecked) so the
            // discrepancy is explicit to an agent/user reading status output.
            $"incomplete: %d{registered - unchecked} files checked, %d{unchecked} unchecked in %.1f{elapsed.TotalSeconds}s"
        else
            $"complete: %d{registered} files checked in %.1f{elapsed.TotalSeconds}s"

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
type Daemon
    internal
    (
        host: PluginHost,
        watcher: FileWatcher option,
        pipeline: CheckPipeline,
        graph: ProjectGraph,
        repoRoot: string,
        workspaceLoader: IWorkspaceLoader,
        mapProjectOptions: Types.ProjectOptions list -> FSharpProjectOptions list,
        discovery: DiscoveryCoordinator,
        scanAgent: ScanAgent,
        cancellationTokenRef: CancellationToken ref,
        ready: ManualResetEventSlim,
        scanSignal: ScanSignal,
        _fcsSuppressedCodes: Set<int>,
        lifetime: CancellationTokenSource,
        formatAllFn: (unit -> Async<string>) option,
        excludePatterns: string list,
        idleExitMin: int option,
        pressureIdleFloorMin: int option,
        // Live scan-activity leases, shared with the scan supervisor that takes them.
        // Read by the idle-exit scheduler and the heartbeat so
        // a cold or forced scan is never mistaken for idleness.
        scanLeases: ScanActivity.ScanLeases,
        // Per-daemon process registry. Plugin-spawned children (test runners,
        // playwright drivers, file-command processes) register against it, and
        // Dispose kills everything still tracked — that is how `fshw stop` and
        // the wedge self-heal reap in-flight children instead of orphaning them.
        //
        // Taken as a PARAMETER, never constructed here: `createWith` must install
        // it before anything captures an ExecutionContext (see the comment there),
        // and a parameter makes that ordering the only constructible one.
        processRegistry: ProcessRegistry.Registry,
        // Closes watcher input and the change-batch worker.
        closeChanges: unit -> unit,
        // The checker this daemon checks through: its own, or its partition's in a host.
        checker: FSharpChecker
    ) =

    let mutable disposed = false

    // Expose a read-only view of the live project graph to plugins (via
    // PluginCtx.ProjectGraph) BEFORE DaemonConfig.registerPlugins runs, so the
    // TestPrune plugin can compute dependency fingerprints + fan out to dependent
    // test projects. All paths are absolute .fsproj strings.
    // `GetTransitiveDependents` includes the project itself; we drop it here so
    // the accessor's contract ("dependents, excluding self") holds.
    do
        host.SetProjectGraph
            { ObserveModel = fun () -> host.WorkSnapshot.ProjectModel
              ObserveCheckableFiles = fun () -> host.WorkSnapshot.ProjectModelFiles
              GetAllProjects = fun () -> graph.GetAllProjects() |> List.map AbsProjectPath.value
              GetTransitiveDependentProjects =
                fun fsproj ->
                    let self = AbsProjectPath.create fsproj

                    graph.GetTransitiveDependents(self)
                    |> List.filter (fun p -> p <> self)
                    |> List.map AbsProjectPath.value
              GetProjectReferences =
                fun fsproj ->
                    graph.GetReferences(AbsProjectPath.create fsproj)
                    |> List.map AbsProjectPath.value
              GetCanonicalDllPath = fun fsproj -> graph.GetCanonicalDllPath(AbsProjectPath.create fsproj) }

    /// The single live-coverage computation reused by every "incomplete" consumer
    /// (`status`/`WaitForScan`/logs via FormatScanStatus, and `check` via the
    /// IPC GetUncheckedCount closure). Both `host` and `pipeline` are in scope
    /// here, which is why it lives at this seam. Returns
    /// (registeredCount, uncheckedCount) where unchecked = registered files that
    /// currently lack a valid FULL check in the host's live coverage set. The
    /// pipeline's registered-files denominator already excludes generated
    /// obj/bin files.
    let liveCoverage () =
        let registered = pipeline.GetAllRegisteredFiles()
        registered.Length, registered |> List.filter (host.IsFileChecked >> not) |> List.length

    /// The live completeness signal as `(registered, unchecked)`.
    ///
    /// THE SAME `liveCoverage` the IPC `GetUncheckedCount` closure serves to `fshw
    /// check` — exposed, not re-derived, so the daemon-backed check and the
    /// in-process `--run-once` check answer "did we actually check every file?" from
    /// ONE computation that cannot disagree with itself.
    member _.LiveCoverage() : int * int = liveCoverage ()

    /// The last atomically completed project-discovery attempt. `None` means no
    /// attempt has completed or one is currently between clear and completion.
    member internal _.DiscoverySnapshot() : DiscoverySnapshot option = discovery.Completed

    /// The project model as a value rather than an absence:
    /// `Rediscovering` while an attempt is in flight, `Unobserved` before any attempt
    /// completed (or after the settling one faulted), and otherwise the classified
    /// completed outcome. The one reading a verdict may be green on is `Available`.
    member _.ProjectModel() : ProjectModel.Observation = discovery.Observation

    /// Only TOTAL loader failure is terminal here. A project that loaded but did
    /// not register is a distinct later-stage defect and must not be called an
    /// MSBuild evaluation failure.
    member internal this.TotalDiscoveryFailure() : string option =
        this.DiscoverySnapshot()
        |> Option.bind (fun snapshot -> totalDiscoveryFailure snapshot.Discovered snapshot.Loaded)

    member internal _.WaitForDiscoveryFailure() : Task<string option> =
        task {
            let! completed = discovery.WaitForCompletion()

            return
                completed
                |> Option.bind (fun snapshot -> totalDiscoveryFailure snapshot.Discovered snapshot.Loaded)
        }

    member internal _.WaitForDiscoveryAdmission() : Task<DiscoveryAdmission> =
        task {
            let! generation, completed = discovery.WaitForStableAdmission()

            return
                { Generation = generation
                  Failure =
                    completed
                    |> Option.bind (fun snapshot -> totalDiscoveryFailure snapshot.Discovered snapshot.Loaded) }
        }

    /// The plugin host that manages plugin lifecycle and event dispatch.
    member _.Host = host

    /// The checker this daemon checks through. In a repository host, sessions of one
    /// checker configuration share it.
    member _.Checker: FSharpChecker = checker

    member internal _.ProcessRegistry = processRegistry

    /// The check pipeline that performs incremental file checking.
    member _.Pipeline = pipeline

    /// The project dependency graph.
    member _.Graph = graph

    /// The repository root directory.
    member _.RepoRoot = repoRoot

    /// Signalled when the daemon is ready to accept file change events.
    member _.Ready = ready

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true

                // Close admission first. Queued requests settle and live work is asked to
                // cancel, so nothing can launch a child after the registry reaps. Live
                // callbacks stay owned until they actually return.
                for close in [ closeChanges; (fun () -> closeScan scanAgent) ] do
                    try
                        close ()
                    with failure ->
                        Logging.error "daemon" $"closing supervised work failed: %O{failure}"

                // Call directly on the daemon's own registry rather than the
                // AsyncLocal current one — Dispose may run from a different
                // async context than the one that installed it.
                processRegistry.KillAll()
                lifetime.Cancel()
                lifetime.Dispose()
                watcher |> Option.iter (fun w -> (w :> IDisposable).Dispose())

    /// Register a declarative framework-managed plugin handler.
    ///
    /// Registration starts the plugin's worker, which captures the current context: the
    /// daemon's process scope is installed for it, so what the plugin spawns is the
    /// daemon's to reap, whoever registers it.
    member _.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        use _processScope = ProcessRegistry.install processRegistry
        host.RegisterHandler(handler)

    /// Register a preprocessor (e.g., formatter) that runs before events are dispatched.
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) = host.RegisterPreprocessor(preprocessor)

    /// Register a project's options so its files can be checked incrementally.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        pipeline.RegisterProject(projectPath, options)

    /// Get current scan state. Read from the published scan row; never waits for a scan.
    member _.GetScanState() = getScanStatus scanAgent

    /// Get current scan generation (incremented after each completed scan).
    member _.GetScanGeneration() = getScanGeneration scanAgent

    /// Set scan state (internal, for testing).
    member internal _.SetScanState(state: ScanState) = setScanStatus scanAgent state

    /// Scan all registered files — check each one and emit events to plugins.
    /// Completes when this request's scan has settled; a scan already running finishes
    /// first. Raises the scan's failure, or `ObjectDisposedException` once the daemon is
    /// disposed. Cancelling the caller cancels this request's scan.
    member _.ScanAll() =
        async {
            let! ct = Async.CancellationToken

            do! requestScan scanAgent ct
        }

    /// Admit a scan without waiting for it. The result resolves once the request is
    /// published and bound to later `WaitForScan` waiters, and carries its receipt.
    member internal _.AdmitScan() : Task<Task<unit>> =
        Async.StartAsTask(admitScan scanAgent CancellationToken.None)

    /// Run a single full scan in-process without watcher or IPC.
    /// Discovers projects, scans all files, waits for plugins to complete, returns statuses.
    member this.RunOnce() =
        async {
            do! this.ScanAll()

            do! this.Settle()

            return host.GetAllStatuses()
        }

    /// Block until every plugin reaches a terminal state — the settle half of
    /// `RunOnce`, WITHOUT the scan.
    ///
    /// Exposed for run-once `confirm`, which forces a full test
    /// run through the plugin host after the scan has already settled and must then
    /// wait out THAT run — a second `RunOnce` would re-scan and rebuild the world just
    /// to wait for a run that is already in flight.
    ///
    /// Tolerates an all-Idle host (plugins with no work this cycle) — it must never
    /// hang on a legitimately never-run plugin, so it asks only for rest, never for
    /// earned evidence.
    member _.Settle() =
        waitForAllTerminal host (System.TimeSpan.FromMinutes(30.0)) lifetime.Token
        |> Async.AwaitTask

    /// Run the daemon until cancellation is requested.
    member this.Run(cancellationToken: CancellationToken) =
        async {
            ready.Set()

            try
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
        }
        |> ProcessRegistry.withRegistryAsync processRegistry

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member _.DiscoverAndRegisterProjects() =
        discovery.Run(fun () ->
            async {
                let! completed =
                    discoverAndRegisterProjects
                        repoRoot
                        workspaceLoader
                        mapProjectOptions
                        graph
                        pipeline
                        host.Phases
                        excludePatterns
                        true

                return completed, ()
            })

    /// Format scan state as a human-readable string. Completeness is read LIVE
    /// from the host's coverage set (registered minus currently-checked) so the
    /// rendered numbers agree with `fshw check` and never rot after incremental
    /// edits — only the scan marker comes from the scan supervisor's published row.
    member _.FormatScanStatus() =
        let (registered, unchecked) = liveCoverage ()
        formatScanStatusWith registered unchecked (getScanStatus scanAgent)

    /// Run the daemon with IPC server on the given pipe name.
    /// Discovers projects, performs initial scan, then watches for changes.
    /// Serve this daemon until `cts` is cancelled, then dispose it.
    ///
    /// `serve` is handed the daemon's RPC configuration and must run until `cts` is
    /// cancelled: a per-worktree daemon serves its own pipe (`RunWithIpc`), a session
    /// of a repository host registers with the host's endpoint. After cancellation the
    /// daemon waits at most `serveBound` for `serve` to finish. `startedAt` is when this
    /// daemon's start began — the process start for a per-worktree daemon, the attach
    /// for a hosted session — and is what the Startup phase is measured from.
    member this.RunWith
        (
            serve: DaemonRpcConfig -> CancellationTokenSource -> Async<unit>,
            serveBound: TimeSpan,
            startedAt: DateTime,
            cts: CancellationTokenSource
        ) =
        // What this starts runs in the daemon's process scope, and the caller's is
        // untouched however it is started.
        async {
            try
                // Admitted before the `Scan` RPC replies, so the `WaitForScan` a client
                // sends next is bound to this request rather than to an earlier one
                // that failed. The scan itself runs on without the caller.
                let onScan () =
                    this.AdmitScan()
                    |> SupervisedWork.waitWithin SupervisedWork.AdmissionBound
                    |> ignore

                let triggerBuild () =
                    async {
                        let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value

                        if not files.IsEmpty then
                            host.EmitFileChanged(SourceChanged files)
                    }

                let formatAll () =
                    match formatAllFn with
                    | Some fn -> fn ()
                    | None ->
                        async {
                            let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value
                            return renderFormatAll files (host.RunPreprocessors(files))
                        }

                let rerunPlugin (name: string) =
                    async {
                        return
                            host.RerunPlugin(
                                name,
                                (fun () -> pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value)
                            )
                    }

                // Request-time completeness signal for `check`'s exit code. ONE
                // definition of "registered minus checked" — shared with
                // FormatScanStatus via the daemon-level `liveCoverage`.
                let getUncheckedCount () = snd (liveCoverage ())

                // How many clients are watching this host: every in-flight verdict wait
                // holds an observation lease, published with the work it is waiting on
                // (`waitForEvidenceCore`). Feeds idle-exit and the heartbeat below, so the
                // daemon is NEVER treated as idle while a client is blocked on a verdict —
                // a client can be waiting while every plugin is momentarily quiet, and
                // idle-exit firing mid-wait drops it with a connection error instead of a
                // verdict. Read from the same publication that answers whether the host
                // owns work, rather than from a counter kept beside it.
                let observingClients () = host.WorkSnapshot.ObserverCount

                let rpcConfig: DaemonRpcConfig =
                    { Host = host
                      RequestShutdown = fun () -> cts.Cancel()
                      RequestScan = onScan
                      GetScanStatus = this.FormatScanStatus
                      GetScanGeneration = this.GetScanGeneration
                      TriggerBuild = triggerBuild
                      FormatAll = formatAll
                      WaitForScanGeneration =
                        fun afterGen ->
                            // Race the scan-signal waiter against daemon shutdown so
                            // `fshw stop` faults the in-flight RPC. The linked CTS
                            // is disposed on every path so the inner `Task.Delay`'s
                            // registration on `cts.Token` doesn't outlive the call.
                            task {
                                use linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)

                                let waiter =
                                    scanSignal.WaitForGeneration(afterGen, this.GetScanGeneration()) :> Task

                                let cancel = Task.Delay(System.Threading.Timeout.Infinite, linked.Token)
                                let! winner = Task.WhenAny([| waiter; cancel |])

                                if obj.ReferenceEquals(winner, cancel) then
                                    raise (System.OperationCanceledException(daemonShuttingDownMessage, cts.Token))

                                linked.Cancel()
                                // The waiter may have settled with its scan's failure.
                                do! waiter
                            }
                      // The WaitForComplete RPC path: it must not report a vacuous clean on
                      // a cold / never-ran daemon, so it waits for EVIDENCE about the
                      // current model, not for a reported status. The wait takes the
                      // observation lease itself, so an in-flight client wait inhibits
                      // idle-exit on every exit path (verdict, timeout, or shutdown
                      // cancellation) without a bracket at this call site to forget.
                      WaitForAllTerminal =
                        fun timeout ->
                            waitForVerdictUnlessDiscoveryFailed
                                this.WaitForDiscoveryAdmission
                                (fun timeout -> waitForVerdict host timeout cts.Token)
                                timeout
                      RerunPlugin = rerunPlugin
                      InvalidateCache =
                        fun () ->
                            task { do! System.Threading.Tasks.Task.Run(System.Action(fun () -> host.ClearTaskCache())) }
                      GetUncheckedCount = getUncheckedCount
                      GetProjectModel = this.ProjectModel
                      // Captured here, with this daemon's process scope installed: in a
                      // repository host the session's endpoint is served from the host's
                      // context, and the session's RPCs still run in the session's.
                      Context = Option.ofObj (ExecutionContext.Capture()) }

                // Everything before the daemon serves — runtime boot (per-worktree),
                // config and analyzer loading, the singleton lock — is wall time a cold
                // `check` waits on.
                host.Phases.Record(
                    DaemonPhases.Phase.Startup,
                    startedAt,
                    DateTime.UtcNow - startedAt,
                    Some "daemon start to serving"
                )

                // Started on this thread: a server creates its listening instances
                // before its first wait, so the daemon accepts connections before this
                // method goes on. Queued to the thread pool instead, a loaded box could
                // hold the start back while a client probing for the daemon finds none.
                let ipcTask = Async.StartImmediateAsTask(serve rpcConfig cts)

                // Idle-exit scheduler. When a threshold is configured, arm a 30s
                // timer that gracefully shuts the daemon down once it has been idle
                // for the window, via the SAME `cts.Cancel()` path the IPC Shutdown
                // request uses: that unblocks the wait below, runs this method's
                // `finally` (Dispose: KillAll + lifetime.Cancel + watcher dispose),
                // and lets the CLI clean up the pidfile — no `Environment.Exit`.
                // The next `fshw` command auto-restarts the daemon.
                //
                // The decision logic lives in the pure, unit-tested `IdleExit`
                // module; injected here are the live clock/busy/activity/pressure
                // and the shutdown action. Memory pressure (a live
                // `GC.GetGCMemoryInfo()` read) SHORTENS the effective window to
                // `min(N, pressureFloor)` per tick so a tight machine sheds idle
                // workspace daemons fast; the default workspace stays exempt
                // because its `idleExitMin` is `None` and pressure never creates a
                // window.
                use _idleExitTimer: IDisposable =
                    match idleExitMin with
                    | Some minutes when minutes > 0 ->
                        let deps: IdleExit.IdleExitDeps =
                            { BaseThresholdMin = minutes
                              PressureFloorMin = pressureIdleFloorMin
                              Pressure = IdleExit.readGcPressure
                              Now = fun () -> System.DateTime.UtcNow
                              // Inhibited by plugin work in flight (mailbox events
                              // or an exclusive background run), a client blocked
                              // on a verdict wait, or a scan in flight. The wait
                              // leg keeps idle-exit from firing out from under a
                              // connected `fshw check` that is blocked waiting for
                              // evidence; the scan leg covers the
                              // cold FCS analysis that raises neither of the other
                              // two and used to be terminated as "idle".
                              Inhibitors =
                                fun () ->
                                    IdleExit.idleInhibitors
                                        (host.AnyPluginBusy())
                                        (observingClients ())
                                        (ScanActivity.ScanLeases.inFlight scanLeases)
                              LastActivityAt = host.LastActivityAt
                              Shutdown = fun () -> cts.Cancel()
                              Log = fun message -> Logging.info "idle-exit" message }

                        IdleExit.createTimer deps :> IDisposable
                    | _ ->
                        // No-op disposable when idle-exit is off.
                        { new IDisposable with
                            member _.Dispose() = () }

                // Activity heartbeat. Publishes `<repoRoot>/.fshw/heartbeat` — Unix
                // epoch seconds, rewritten every 15s — for exactly as long as a run
                // is in progress, and NEVER while idle. That "only while running"
                // property is the value: a daemon that is alive but wedged does not
                // beat, so process liveness and activity can be told apart. Beat
                // failures are logged and swallowed inside `runTick` — the daemon
                // must never die for failing to announce itself.
                use _heartbeat: IDisposable =
                    Heartbeat.createBeat
                        { Now = fun () -> System.DateTime.UtcNow
                          // Same two live signals idle-exit reads. `AnyPluginBusy`
                          // is the leg that spans long quiet phases: a plugin's
                          // inflight count is held for the whole lifetime of an
                          // exclusive run, so a ten-minute silent browser suite
                          // keeps beating without emitting anything.
                          RunActive =
                            fun () ->
                                Heartbeat.runActive
                                    (host.AnyPluginBusy())
                                    (observingClients ())
                                    (ScanActivity.ScanLeases.anyInFlight scanLeases)
                          Write = Heartbeat.writeTo repoRoot
                          Log = Logging.warn "heartbeat"
                          Cadence = Heartbeat.DefaultCadence }

                // Plugin-wedge monitor. A plugin that reported Running and posts no
                // completion past the bound is wedged. The monitor logs escalating
                // "still running … (no completion posted)" lines every 5 min so a
                // silent log is not mistaken for a healthy idle daemon; past the
                // bound it names the wedge, writes the last-wedge breadcrumb (the
                // next CLI command prints what happened), and gracefully restarts
                // the daemon via the SAME `cts.Cancel()` path as `fshw stop`, so
                // children are reaped and SQLite is never killed mid-write. The
                // bound sits above the verdict deadline plus grace, so a client
                // blocked on WaitForComplete gets its own more specific
                // TimeoutException first and a healthy long run is never restarted.
                use _wedgeMonitor: IDisposable =
                    PluginWedge.createMonitor
                        { Bound = PluginWedge.ambientBound ()
                          ResultQueuedBound = PluginWedge.DefaultResultQueuedBound
                          EscalateEvery = PluginWedge.DefaultEscalateEvery
                          Now = fun () -> System.DateTime.UtcNow
                          RunningPlugins =
                            fun () ->
                                host.GetAllStatuses()
                                |> Map.toList
                                |> List.choose (fun (name, s) ->
                                    match s with
                                    | Running since -> Some(name, since)
                                    | _ -> None)
                          QueuedResults =
                            fun () ->
                                host.GetAllStatuses()
                                |> Map.toList
                                |> List.collect (fun (name, s) ->
                                    match s with
                                    | Running _ ->
                                        host.GetActivitySnapshot(name).Subtasks
                                        |> List.filter (fun t -> PluginFramework.QueuedResult.isSubtaskKey t.Key)
                                        |> List.map (fun t -> name, t.StartedAt)
                                    | _ -> [])
                          Awaiting =
                            fun name ->
                                let work = host.WorkSnapshot

                                PluginWedge.describeAwaiting
                                    System.DateTime.UtcNow
                                    (host.GetActivitySnapshot(name).Subtasks
                                     |> List.map (fun t -> t.Key, t.StartedAt))
                                    (boundedWorkOf work name)
                                    (work.PendingEventsOf name)
                          AnyBusy = fun () -> host.AnyPluginBusy()
                          LastActivityAt = host.LastActivityAt
                          Log = Logging.info "wedge"
                          OnWedged =
                            fun message ->
                                Logging.error "wedge" message
                                PluginWedge.writeBreadcrumb repoRoot message
                                cts.Cancel() }

                cancellationTokenRef.Value <- cts.Token
                ready.Set()

                // Register cancellation before starting the scan so that cancellation during
                // the initial scan unblocks RunWithIpc immediately rather than waiting for the
                // scan to complete. This prevents test-process hangs when cts is cancelled while
                // the scan is still running (e.g. under thread-pool contention in test suites).
                //
                // Continuations run asynchronously: `cts.Cancel()` is called from inside
                // RPC handlers (`Shutdown`), and an inline continuation would run this
                // whole teardown — including the wait for the IPC server, which waits for
                // that very handler's connection — before the handler could reply.
                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<unit>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                    )

                use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                // Race against cancellation so a slow scan doesn't block shutdown.
                let scanTask = Async.StartAsTask(this.ScanAll())

                do!
                    [| scanTask :> System.Threading.Tasks.Task
                       tcs.Task :> System.Threading.Tasks.Task |]
                    |> System.Threading.Tasks.Task.WhenAny
                    |> Async.AwaitTask
                    |> Async.Ignore

                do! tcs.Task |> Async.AwaitTask

                let! _ =
                    System.Threading.Tasks.Task.WhenAny(ipcTask, System.Threading.Tasks.Task.Delay serveBound)
                    |> Async.AwaitTask

                if ipcTask.IsFaulted then
                    Logging.debug "daemon" $"IPC shutdown: %s{ipcTask.Exception.GetBaseException().Message}"
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
        }
        |> ProcessRegistry.withRegistryAsync processRegistry

    /// Serve this daemon on its own pipe until `cts` is cancelled.
    member this.RunWithIpc(pipeName: string, cts: CancellationTokenSource) =
        // Measured from the process start, the earliest instant this process can
        // vouch for.
        let processStartedAt =
            try
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
            with _ ->
                DateTime.UtcNow

        // The server drains its connections, then waits for its pipe name to be
        // released; a daemon disposed before that could hand a CLI a pipe that still
        // accepts connections but has no daemon behind it.
        let serverBound =
            IpcServer.ConnectionDrainBound
            + IpcServer.ReleaseBound
            + System.TimeSpan.FromSeconds(1.0)

        this.RunWith(IpcServer.start pipeName, serverBound, processStartedAt, cts)

/// What one tier's bounded check/retry loop settled on (`runChecksWithRetry`).
///
/// The bound existed; the OUTCOME of reaching it did not. A
/// scan that gave up is a different event from a scan that converged, and the
/// difference belongs in the type so the log line and the scan-completeness
/// count cannot disagree about which happened.
[<RequireQualifiedAccess>]
type ScanCheckOutcome =
    /// Every file produced a result. Carries how many EXTRA rounds beyond the
    /// first pass were needed — 0 on a clean scan, and the direct measure of
    /// the cancellation amplification this retry loop bounds.
    | AllChecked of extraRounds: int
    /// The retry budget ran out with files still unchecked. Carries the files,
    /// the extra rounds spent, and the budget that was exhausted.
    | BudgetExhausted of unchecked: AbsFilePath list * extraRounds: int * budget: int

/// Accessors over `ScanCheckOutcome`, so callers read the two numbers a scan
/// summary needs without re-matching the union at each site.
module ScanCheckOutcome =
    /// Files left unchecked (0 for `AllChecked`).
    let uncheckedCount (outcome: ScanCheckOutcome) : int =
        match outcome with
        | ScanCheckOutcome.AllChecked _ -> 0
        | ScanCheckOutcome.BudgetExhausted(unchecked, _, _) -> List.length unchecked

    /// Extra retry rounds beyond the first pass.
    let extraRounds (outcome: ScanCheckOutcome) : int =
        match outcome with
        | ScanCheckOutcome.AllChecked rounds -> rounds
        | ScanCheckOutcome.BudgetExhausted(_, rounds, _) -> rounds

/// The files of a tier that no earlier tier has already claimed, paired with the
/// extended claim set for the next tier.
///
/// The scan walks dependency-ordered tiers and resolves each tier's files to check
/// thunks in a PER-TIER dictionary. A file compiled into two projects in the SAME
/// tier collapses in that dictionary. One reached through projects in DIFFERENT
/// tiers does not: it gets a thunk in each, and is checked and emitted twice. That
/// is how a scan comes to report checking more files than it registered, while
/// missing none.
///
/// The FIRST tier to reach a file wins it. Tiers are dependency-ordered, so that is
/// the more upstream project — a helper linked from its owning project into a
/// downstream one is checked in the project that owns it. Either project's options
/// would type-check the file; ordering makes WHICH of them deterministic rather than
/// "whichever tier happened to run last".
///
/// Pure, and threaded by the caller, so the cross-tier invariant is testable without
/// a daemon, a checker or a project graph.
let internal freshForTier
    (claimed: Set<AbsFilePath>)
    (candidates: AbsFilePath list)
    : AbsFilePath list * Set<AbsFilePath> =
    let fresh, seen =
        (([], claimed), candidates)
        ||> List.fold (fun (fresh, seen) file ->
            if Set.contains file seen then
                (fresh, seen)
            else
                (file :: fresh, Set.add file seen))

    (List.rev fresh, seen)

/// Drive per-file checks with a bounded retry on `None` results.
///
/// Guards the cold-scan silent-truncation race: while the initial scan runs its FCS
/// tiers, BuildPlugin's `dotnet build` touches `obj/**/ref/*.dll`, the watcher
/// fires, `processBatch` re-checks the affected files, and `CancelPreviousCheck`
/// cancels the scan-side in-flight check of the same file. A cancelled check
/// surfaces as `None`, and a scan emit loop that drops `None` reports green while
/// the ErrorLedger never saw diagnostics for the dropped files (neither reported NOR
/// cleared).
///
/// Files whose `check` returned `None` are re-enqueued for up to `maxRetries`
/// additional rounds. The retry calls the SAME `check` thunk, which (via
/// `CheckFile`/`CheckFileWithOptions` → `CancelPreviousCheck`) re-reads current disk
/// content, so a NEWER user edit that legitimately superseded the in-flight check is
/// observed on retry rather than duplicated. Each file is emitted at most once.
///
/// The outcome is TYPED. The budget was always bounded, but
/// exhausting it produced a bare `int` that the caller folded into a running
/// total, so "this tier gave up on 4 files after 3 extra rounds" was
/// indistinguishable in the code from "4 files happened to be unchecked". A
/// `BudgetExhausted` case carries the files and the rounds spent, and the caller
/// says so in the log instead of looping on in silence.
///
/// Pure of FCS and disk: `check` is injected, making the retry/convergence and
/// honest-completion invariants unit-testable at the narrowest seam.
let internal runChecksWithRetry
    (maxRetries: int)
    (check: AbsFilePath -> Async<'r option>)
    (emit: 'r -> unit)
    (files: AbsFilePath list)
    : Async<ScanCheckOutcome> =
    async {
        let mutable pending = files
        let mutable round = 0

        // Initial pass plus up to `maxRetries` retry rounds over the residual
        // `None` set. Converges (pending shrinks or we hit the budget).
        while not pending.IsEmpty && round <= maxRetries do
            let! results =
                pending
                |> List.map (fun file ->
                    async {
                        let! r = check file
                        return file, r
                    })
                |> Async.Parallel

            let mutable stillPending = []

            for file, result in results do
                match result with
                | Some r -> emit r
                | None -> stillPending <- file :: stillPending

            pending <- List.rev stillPending
            round <- round + 1

        // `round` counts the initial pass too, so extra rounds is `round - 1`
        // (0 for an empty file list, which never enters the loop).
        let extraRounds = max 0 (round - 1)

        if List.isEmpty pending then
            return ScanCheckOutcome.AllChecked extraRounds
        else
            return ScanCheckOutcome.BudgetExhausted(pending, extraRounds, maxRetries)
    }

/// Execute the full scan logic, returning the updated agent state.
/// split the registered set into what can still be scanned and
/// what has vanished, with existence injected so the decision is testable
/// without touching a disk.
///
/// Extracted from `performScan` because the pruning had no test of its own: the
/// clearing primitive was covered on a synthetic ledger, and the partition that
/// decides WHAT to clear — the half a rename actually exercises — was not.
///
/// The property worth pinning is the SECOND element never reaching the scan.
/// `performScan` clears the vanished paths before scanning, and the ordering is
/// load-bearing: clearing afterwards would leave a finding keyed to a path
/// nothing will look at again, which is the wedge this ordering exists to close.
/// A test asserting the scan list excludes a vanished path catches a reordering,
/// because a scan that saw the path is a scan the clear did not precede.
let internal partitionVanished (exists: string -> bool) (registered: string list) : string list * string list =
    registered |> List.partition exists

/// Whether a scan request admitted at `admittedAt` is already answered by the last
/// completed scan, `covered` = the moment that scan began reading the tree.
///
/// A scan request asks for every registered file to be checked as it is on disk NOW.
/// A scan that began reading after the request was admitted did exactly that, so a
/// second full pass would re-ask FCS the same questions about the same bytes. `fshw
/// check` against a daemon still in its cold scan sends exactly such a request, and
/// each one used to cost another full scan. A request admitted after that moment is
/// a real scan: files read before it may have changed unseen.
let internal scanAnswers (covered: (int64 * ScanState) option) (admittedAt: int64) : bool =
    match covered with
    | Some(readFrom, _) -> admittedAt < readFrom
    | None -> false

/// Which kind of scan this is, for the activity lease and the metrics record.
/// Generation 0 means nothing has completed yet, so this is the daemon's cold
/// scan — the phase the idle exit used to terminate.
let private scanKindFor (state: ScanAgentState) =
    if state.Generation = 0L then
        ScanActivity.ScanKind.Cold
    else
        ScanActivity.ScanKind.Forced

/// How many times a scan re-captures the project model before a model that keeps
/// being replaced fails it. The same bound, for the same reason, as
/// `changeBatchAttemptLimit`: a mid-scan rediscovery is a normal event and one
/// retry almost always settles it, but "capture the current model" is not a
/// convergent loop on a repository something is rewriting continuously, and an
/// unbounded scan is a check that never returns.
let internal scanAttemptLimit = 5

/// A scan whose captured model was replaced on every attempt. Named, because the
/// alternative an operator saw was `Could not connect to daemon` — the daemon was
/// answering perfectly well and the fault is in the tree, not the pipe.
type internal ModelKeptChangingDuringScanException(attempts: int) =
    inherit
        InvalidOperationException(
            $"SCAN MODEL KEPT CHANGING: the project model was replaced during each of %d{attempts} scan attempts, \
              so no scan could publish results about the model it had checked. Nothing was published and no \
              results are being withheld. Something rewrote project files throughout the scan — a build, \
              restore or code generator running alongside the check is the usual cause; the `Rediscovering` \
              lines in `logs/daemon.log` name each replacement and what triggered it."
        )

/// Recognize the terminal across the JSON-RPC exception boundary, which does not
/// preserve the concrete type. Same stable-prefix technique as
/// `isTotalDiscoveryFailureMessage`, for the same reason.
let internal isModelKeptChangingDuringScanMessage (message: string) : bool =
    not (isNull message)
    && message.Contains("SCAN MODEL KEPT CHANGING:", StringComparison.Ordinal)

let private performScan
    (ctx: BatchContext)
    (scanLeases: ScanActivity.ScanLeases)
    (state: ScanAgentState)
    (ct: CancellationToken)
    // Publishes the scan's progress into its supervisor row while the scan runs.
    (publish: ScanAgentState -> unit)
    =
    // Survives a re-capture. An attempt that already re-discovered and memoized the
    // project fingerprint must not pay a second MSBuild evaluation on the next one:
    // a supersession whose cause was a real `.fsproj` edit moves the timestamps too,
    // so the next attempt still re-discovers when — and only when — it must.
    let fingerprintMemo = ref state.LastFingerprint

    let scanAttempt =
        async {
            let host = ctx.Host
            let pipeline = ctx.Pipeline
            let graph = ctx.Graph

            // The scan is the single largest phase a cold
            // `check` waits on (12 min of FCS tiers on a 22-project repository) and
            // the one `WaitForScan` blocks on without any plugin owning it. One
            // record for the WHOLE scan — discovery admission, build settlement and
            // the check tiers — so the verdict can cover that wait by name.
            use scanPhase =
                host.Phases.Begin(DaemonPhases.Phase.Scan(ScanActivity.ScanKind.describe (scanKindFor state)))

            // Re-discover projects before scanning so that removed files/projects
            // are cleared before results are returned to the client. Without this,
            // a concurrent processChanges re-discovery (triggered by the file watcher
            // with debounce delay) may complete AFTER the scan signals waiters,
            // leaving stale FCS errors visible for one cycle.
            // Guarded by fsproj fingerprint to skip expensive MSBuild evaluation
            // when no project files have changed.
            let currentFingerprint = fingerprintFsprojFiles ctx.RepoRoot ctx.ExcludePatterns
            let mutable lastFingerprint = fingerprintMemo.Value
            // See `scanAnswers`; set once this attempt starts reading the tree.
            let mutable readFrom: int64 option = None

            if currentFingerprint <> lastFingerprint then
                let! completed, _ =
                    rediscoverAndClearRemoved
                        ctx.RepoRoot
                        ctx.Loader
                        ctx.MapOptions
                        ctx.Discovery
                        graph
                        pipeline
                        host
                        "scan"
                        ctx.ExcludePatterns
                        ctx.ContentTracker
                        true

                // A total loader failure is retryable even when no .fsproj bytes
                // changed (for example after repairing an app-local assembly). Do
                // not memoize the failed fingerprint and suppress the next attempt.
                if totalDiscoveryFailure completed.Discovered completed.Loaded |> Option.isNone then
                    lastFingerprint <- currentFingerprint
                    fingerprintMemo.Value <- currentFingerprint

            // A fingerprint hit skips OUR discovery, not a concurrent writer's. Capture
            // membership, dependency tiers and options together once no writer is
            // between clear and completion; every publication below is refused if a
            // later attempt replaced this model.
            let! capturedModel, registeredProjects, registeredFiles, scanTiers =
                ctx.Discovery.Capture(fun epoch ->
                    let tiers =
                        graph.GetParallelTiers()
                        |> List.map (
                            List.map (fun project ->
                                project,
                                graph.GetSourceFiles(project) |> List.map AbsFilePath.value,
                                pipeline.GetProjectOptions(AbsProjectPath.value project))
                        )

                    epoch,
                    pipeline.GetRegisteredProjects(),
                    pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value,
                    tiers)

            let modelGeneration = modelGenerationOf capturedModel

            let publishCurrent write =
                ctx.Discovery.WithCurrent(capturedModel, write)

            // PRUNE VANISHED PATHS BEFORE SCANNING.
            //
            // A file that is no longer on disk cannot be analyzed, and analyzing it
            // anyway produces a finding about nothing: "symbol analysis failed —
            // Parse errors" for a path that cannot be opened. That finding is keyed
            // to the dead path, so nothing later clears it and the gate stays red
            // until the daemon is stopped — the one command the docs tell you not to
            // run. Renames are routine here (the compiler is the migration
            // checklist), so this taxed exactly the refactoring the codebase asks for.
            //
            // The removed-file clearing above does NOT cover this on its own: it is
            // gated on the fsproj fingerprint changing, so a rename that leaves every
            // `.fsproj` byte-identical — a glob-matched file — never reaches it.
            // Checking existence here is the backstop that does not depend on how the
            // rename happened to touch the project files.
            // The moment this scan starts reading the tree for its checks, taken
            // BEFORE the existence probe and every source read. Kept only when the
            // project files still match the fingerprint the model was captured under:
            // discovery read them before this moment, so a changed fingerprint means a
            // request admitted in between is NOT answered by this scan.
            let readStartedAt = Diagnostics.Stopwatch.GetTimestamp()

            readFrom <-
                if fingerprintFsprojFiles ctx.RepoRoot ctx.ExcludePatterns = lastFingerprint then
                    Some readStartedAt
                else
                    None

            let files, vanished = partitionVanished System.IO.File.Exists registeredFiles

            if not vanished.IsEmpty then
                // Clear first, THEN scan: a finding left behind here outlives the
                // scan that would have replaced it, because nothing will check that
                // path again.
                host.ClearFilesEverywhere vanished

                Logging.info
                    "scan"
                    $"Dropped %d{vanished.Length} registered file(s) that no longer exist (renamed or deleted) and cleared their findings"

            let total = files.Length
            Logging.info "scan" $"%d{registeredProjects.Length} projects, %d{total} files registered"
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let scanStartedAt = System.DateTime.UtcNow

            publish
                { state with
                    ScanState = Scanning(total, 0, scanStartedAt) }

            let dispatchedFiles = ResizeArray<AbsFilePath>()
            // Files whose check never returned Some, even after the bounded
            // scan-retry budget (the silent-truncation race: a scan-side check
            // cancelled by processBatch's same-file re-check, or a hard failure).
            // Surfaced in the scan-complete state and log so a truncated scan can
            // never read as clean. See `runChecksWithRetry`.
            let mutable uncheckedCount = 0
            // Extra retry rounds beyond each tier's first pass, summed. The direct
            // measure of the cold-scan cancellation amplification the retry budget
            // bounds; recorded in the per-scan metrics below.
            let mutable retryRounds = 0
            // Files that produced a result and were emitted, hoisted out of the
            // `if` so the metrics record can read it on an empty scan too.
            let mutable checkedTotal = 0
            // Files the scan never attempted, and the subset of those dropped because
            // the deps-freshness gate refused their project. Hoisted for the same
            // reason, and recorded because a scan that silently covered a third of the
            // tree was otherwise indistinguishable in the ledger from a complete one.
            let mutable skippedTotal = 0
            let mutable depsGatedTotal = 0
            // Cohort files that reached no tier at all. The incremental path
            // reconciles this category (`uncovered`, ~line 1406); the scan path does
            // not dispatch them, so without this they were invisible in every count.
            let mutable uncoveredTotal = 0

            if not files.IsEmpty then
                // Run preprocessors (e.g., formatter) before dispatching
                let modified = host.RunPreprocessors(files).Modified

                if modified.Length > 0 then
                    Logging.info "scan" $"Preprocessors modified %d{modified.Length} files (watcher may re-trigger)"

                // After the preprocessors' rewrites, before any check: one upstream
                // fingerprint table per project for the whole scan.
                pipeline.BeginGeneration()

                publishCurrent (fun () -> host.EmitFileChanged(SourceChanged files))

                // Serialize: BuildPlugin must leave Running BEFORE the FCS check tiers
                // read the obj/ refs it rewrites. See
                // `waitForPluginTerminalIfRunningWith` for the race this closes.
                do! waitForPluginTerminalIfRunning host "build" (System.TimeSpan.FromMinutes(10.0))

                let mutable completed = 0

                let mutable checkedCount = 0
                let mutable skippedCount = 0
                let mutable depsGatedCount = 0

                let filesToCheckSet = Set.ofList files
                // Every cohort file a tier claimed, whether it was then dispatched or
                // refused by the deps gate. What remains is the uncovered set.
                let tierCovered = System.Collections.Generic.HashSet<string>()
                // Scan-scoped, where `tierThunks` below is per-tier. See `freshForTier`.
                let mutable claimedInScan: Set<AbsFilePath> = Set.empty

                // Check files in parallel tiers based on project dependency graph
                let tiers = scanTiers

                // Bounded retry budget for cancelled/aborted/failed scan checks.
                // The common case (a single processBatch race per file) converges
                // in one retry; the budget caps work for files under continuous
                // concurrent edit, which are then honestly reported as unchecked.
                let scanRetryBudget = 3

                for tier in tiers do
                    // Resolve each checkable file in the tier to its check thunk,
                    // honouring the deps-freshness gate and per-project options. The
                    // thunk is re-runnable (an Async description), so
                    // `runChecksWithRetry` can re-invoke it on a cancelled result.
                    let tierThunks =
                        System.Collections.Generic.Dictionary<AbsFilePath, Async<FileCheckResult option>>()

                    for proj, projectFiles, projectOptions in tier do
                        let projPath = AbsProjectPath.value proj
                        let projFiles = projectFiles |> List.filter filesToCheckSet.Contains
                        skippedCount <- skippedCount + (projectFiles.Length - projFiles.Length)

                        for claimed in projFiles do
                            tierCovered.Add claimed |> ignore

                        // Deps-freshness gate — see `applyDepsGate`.
                        if applyDepsGate ctx.DepsGate host projPath then
                            // Claimed only where the file is actually dispatched, so a
                            // file whose project the deps gate refuses stays available
                            // to a later tier whose project passes it.
                            let fresh, extended =
                                projFiles |> List.map AbsFilePath.create |> freshForTier claimedInScan

                            claimedInScan <- extended

                            match projectOptions with
                            | Some options ->
                                for absFile in fresh do
                                    tierThunks[absFile] <- pipeline.CheckFileWithOptions(absFile, options, ct)
                            | None ->
                                for absFile in fresh do
                                    tierThunks[absFile] <- pipeline.CheckFile(absFile, ct)
                        else
                            skippedCount <- skippedCount + projFiles.Length
                            depsGatedCount <- depsGatedCount + projFiles.Length

                    let tierFiles = tierThunks.Keys |> Seq.toList

                    let emitChecked (checkResult: FileCheckResult) =
                        publishCurrent (fun () ->
                            let checkResult =
                                { checkResult with
                                    ModelGeneration = modelGeneration }

                            checkedCount <- checkedCount + 1
                            dispatchedFiles.Add(checkResult.File)
                            host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics ctx.FcsSuppressedCodes host checkResult
                            completed <- completed + 1

                            publish
                                { state with
                                    ScanState = Scanning(total, completed, System.DateTime.UtcNow) })

                    let! tierOutcome =
                        runChecksWithRetry scanRetryBudget (fun f -> tierThunks[f]) emitChecked tierFiles
                        |> CheckCaller.within "scan"

                    match tierOutcome with
                    | ScanCheckOutcome.AllChecked _ -> ()
                    | ScanCheckOutcome.BudgetExhausted(unchecked, rounds, budget) ->
                        // Say it once, explicitly, naming the bound
                        // that was reached. The alternative this replaces was adding
                        // a number to a total and moving on, which is how repeated
                        // self-cancellation stayed invisible for a whole gate run.
                        let named =
                            unchecked |> List.truncate 5 |> List.map AbsFilePath.value |> String.concat ", "

                        Logging.warn
                            "scan"
                            $"Retry budget exhausted after %d{rounds} extra round(s) of %d{budget}: %d{unchecked.Length} file(s) still unchecked (%s{named})"

                    uncheckedCount <- uncheckedCount + ScanCheckOutcome.uncheckedCount tierOutcome
                    retryRounds <- retryRounds + ScanCheckOutcome.extraRounds tierOutcome

                // Keep the existing "Checked N files (T tiers), skipped M" prefix
                // intact (external tooling greps it); append the unchecked count so
                // a truncated scan is never silently green in the log either.
                Logging.info
                    "scan"
                    $"Checked %d{checkedCount} files (%d{tiers.Length} tiers), skipped %d{skippedCount}, unchecked %d{uncheckedCount}"

                uncoveredTotal <- files |> List.filter (tierCovered.Contains >> not) |> List.length

                if uncoveredTotal > 0 then
                    // Named rather than folded into a total: these files belong to no
                    // project the scan walked, so no retry round and no gate decision
                    // will ever account for them.
                    Logging.warn
                        "scan"
                        $"%d{uncoveredTotal} registered file(s) belonged to no project in this scan's tiers and were never dispatched"

                checkedTotal <- checkedCount
                // The cohort files the scan did not attempt. `skippedCount` also
                // counts project files OUTSIDE the cohort, which is right for its log
                // line and wrong for coverage arithmetic, so the metric is built from
                // the two cohort reasons instead.
                depsGatedTotal <- depsGatedCount
                skippedTotal <- depsGatedCount + uncoveredTotal

            sw.Stop()
            let finalScanState = ScanComplete(sw.Elapsed)

            scanPhase.Complete(
                Some
                    $"%s{ScanActivity.ScanKind.describe (scanKindFor state)} scan: checked %d{checkedTotal} of %d{total} registered file(s), unchecked %d{uncheckedCount}"
            )

            let newGeneration = state.Generation + 1L

            // Emit BatchChecked before the scan returns: the generation is signalled
            // only after the supervisor publishes this scan's completed state, so
            // WaitForScanGeneration callers (IPC) can assume BatchChecked has already
            // been dispatched by the time `fshw scan --wait` returns. Empty cohorts (no
            // registered files) skip — there's nothing to "flush and decide" against.
            // The seal is guarded even when there is nothing to seal: a scan whose model
            // was replaced must not complete as though it had checked that model.
            //
            // An EMPTY cohort is sealed too. The seal is the scan's answer about the whole
            // model — "every checkable file of this generation has been dealt with" — and a
            // model with no checkable files has that answer just as much as one with 2,000.
            // Without it an analysis-only repository earns no receipt and can never be
            // green, and a plugin waiting for the cohort waits forever.
            publishCurrent (fun () ->
                host.EmitBatchChecked
                    { Trigger = BootScan
                      Files = dispatchedFiles |> List.ofSeq
                      Generation = newGeneration
                      ModelGeneration = modelGeneration
                      Retained = None
                      StartedAt = scanStartedAt
                      CompletedAt = System.DateTime.UtcNow })

            // One measurement record per completed scan generation,
            // appended to `.fshw/scan-metrics.jsonl`. A later run reads the same file
            // and compares; `ScanMetrics.fitRetention` turns the RSS series into a
            // slope. A write failure is logged, never fatal.
            let reading =
                ScanMetrics.readResources (
                    ctx.Seams.MayForceGc
                    && ScanMetrics.forceGcEnabled Environment.GetEnvironmentVariable
                )

            let directSpawns, helperSpawns = ProcessHelper.spawnCounts ()

            let sample: ScanMetrics.ScanSample =
                { Generation = newGeneration
                  Kind = ScanActivity.ScanKind.describe (scanKindFor state)
                  DurationMs = sw.Elapsed.TotalMilliseconds
                  FilesRegistered = total
                  FilesChecked = checkedTotal
                  FilesUnchecked = uncheckedCount
                  FilesSkipped = skippedTotal
                  FilesDepsGated = depsGatedTotal
                  FilesUncovered = uncoveredTotal
                  RetryRounds = retryRounds
                  RssBytes = reading.RssBytes
                  ManagedBytes = reading.ManagedBytes
                  ForcedGc = reading.ForcedGc
                  Gen2Collections = reading.Gen2Collections
                  DirectSpawns = directSpawns
                  HelperSpawns = helperSpawns
                  Scope = ctx.Seams.ResourceScope
                  SampledAt = System.DateTime.UtcNow }

            match ScanMetrics.tryAppend (ScanMetrics.recordPath ctx.RepoRoot) sample with
            | Ok() -> ()
            | Result.Error message -> Logging.debug "scan" $"scan-metrics append failed: %s{message}"

            return
                { ScanState = finalScanState
                  Generation = newGeneration
                  LastFingerprint = lastFingerprint
                  Covered =
                    match readFrom with
                    | Some started when uncheckedCount = 0 -> Some(started, finalScanState)
                    | _ -> None }
        }

    // A rediscovery that lands mid-scan is a NORMAL event — on a cold `check` the
    // run's own beforeRun build is usually what causes it — and `WithCurrent` is
    // right to refuse the stale publication. What it must not do is end the
    // caller's scan: the recovery is to capture the model that replaced this one
    // and scan it, which is the same shape `processBatch` gives a change cohort.
    // Every attempt publishes only under the epoch it captured, so the refusal's
    // guarantee is untouched: no results about a superseded model ever escape.
    let rec completeCurrent attempt =
        async {
            ct.ThrowIfCancellationRequested()

            try
                return! scanAttempt
            with :? ModelSupersededException when attempt < scanAttemptLimit ->
                Logging.info
                    "scan"
                    $"Project model replaced mid-scan; re-capturing and scanning it (attempt %d{attempt + 1} of %d{scanAttemptLimit})"

                return! completeCurrent (attempt + 1)
        }

    let scanBody =
        async {
            try
                return! completeCurrent 1
            with :? ModelSupersededException ->
                return raise (ModelKeptChangingDuringScanException scanAttemptLimit)
        }

    // Hold an activity lease for the WHOLE scan, released by
    // `withLease`'s finally on completion, exception, and cancellation alike.
    // Everything in `scanBody` (re-discovery, preprocessors, build settlement,
    // the FCS tiers, verdict signalling) runs inside it — every attempt of it, so
    // a scan recovering from a superseded model is not mistaken for idleness
    // between attempts by the idle-exit scheduler or the heartbeat.
    ScanActivity.withLease scanLeases (scanKindFor state) scanBody

/// Functions for creating and managing daemons.
module Daemon =
    let private sourceDebounceMs = 500
    let private projectDebounceMs = 200

    /// Whether the daemon keeps observing the repository after its first scan.
    /// Decided by the command that constructs the host, so a one-shot run cannot
    /// reach watcher construction by accident: a `--run-once`
    /// check used to inherit the persistent daemon's FSEvents startup and fail
    /// there, in a stream it would never have read from.
    [<RequireQualifiedAccess>]
    type RunMode =
        /// Persistent daemon: a `FileWatcher` (native events, or the polling
        /// fallback) feeds edits into the change-batch supervisor for the daemon's lifetime.
        | Watching
        /// One shot (`--run-once`): a single scan settles the verdict and the host
        /// is disposed. No native FSEvents stream, `FileSystemWatcher`, or polling
        /// thread is ever constructed — `ExtraWatchPatterns` and
        /// `FsEventsLatencySeconds` are watcher inputs and have nothing to act on.
        | OneShot

    /// Constructs the repository watcher for a `Watching` daemon. The arguments
    /// are `FileWatcher.create`'s; the seam exists so a test can prove a
    /// `OneShot` host never calls it.
    type WatcherFactory = DaemonHosting.WatcherFactory

    /// The TransparentCompiler cache size factor when `.fshw.json` sets none: FCS's
    /// own default (its internal `TransparentCompiler.CacheSizes.Default` is `Create 100`), so
    /// leaving the key out changes nothing.
    ///
    /// The factor scales ENTRY counts, not bytes. At 100 the checker keeps roughly
    /// 2,000 type-check intermediates strongly held, 5,000 parse results, and 100
    /// strong plus 200 weak full check results. A smaller factor holds less and
    /// re-typechecks more, so it trades memory for CPU. FsAutoComplete runs at 10.
    [<Literal>]
    let DefaultCheckerCacheSizeFactor = 100

    /// Options controlling daemon construction. Callers use `DaemonOptions.defaults`
    /// and modify only what they need.
    [<NoComparison; NoEquality>]
    type DaemonOptions =
        {
            /// `Watching` (the default) for a persistent daemon; `OneShot` for a
            /// `--run-once` host, which then constructs no file watcher at all.
            RunMode: RunMode
            CacheBackend: ICheckCacheBackend option
            CacheKeyProvider: ICacheKeyProvider option
            /// FCS diagnostic codes to suppress globally. `None` means no
            /// daemon-level suppression — projects opt in via `<NoWarn>` in
            /// their fsproj or `#nowarn "code"` in source.
            FcsSuppressedCodes: int list option
            /// `PathFilter.isExcludedPath` patterns applied during project discovery.
            ExcludePatterns: string list
            /// Extra file patterns from FileCommandPlugin configs that the watcher
            /// should monitor beyond the default F# source/project set.
            ExtraWatchPatterns: FilePattern list
            /// macOS FSEvents coalescing window in seconds, passed through to
            /// `MacFsEvents.createWithCoalesced`. Resolved by the caller from the
            /// `fsEventsLatencyMs` config key (`float ms / 1000.0`). Default 0.25
            /// (250 ms). Ignored on non-macOS. See `DaemonConfiguration`.
            FsEventsLatencySeconds: float
            /// Resolved idle-exit threshold in minutes. `Some n` arms a 30s timer
            /// that gracefully shuts the daemon down after `n` minutes of
            /// idleness (no events, no running work); the next `fshw` command
            /// auto-restarts. `None` (the default) disables it — no timer is
            /// created. Resolution from the `idleExitMin` config + repo path is
            /// done by the caller (`IdleExit.resolveThreshold`).
            IdleExitMin: int option
            /// Resolved memory-pressure idle floor (minutes). When idle-exit is
            /// eligible (`IdleExitMin = Some n`) AND the machine is under memory
            /// pressure, the effective idle window is shortened to `min(n,
            /// PressureIdleFloorMin)` so a tight machine sheds idle daemons fast.
            /// `None` disables pressure-shortening (the full window is always
            /// used). Pressure NEVER makes a non-eligible daemon (`IdleExitMin =
            /// None`, e.g. the default workspace) eligible. Resolution from the
            /// `pressureIdleFloorMin` config is done by the caller
            /// (`IdleExit.resolvePressureFloor`).
            PressureIdleFloorMin: int option
            /// TransparentCompiler cache size factor, from the `checker.cacheSizeFactor`
            /// config key. See `DefaultCheckerCacheSizeFactor`.
            CheckerCacheSizeFactor: int
            /// `DaemonHosting.standalone ()` (the default) for a per-worktree daemon;
            /// `DaemonHosting.hostedBy` for a session of a repository host.
            Hosting: DaemonHosting.Hosting
        }

    module DaemonOptions =
        let defaults: DaemonOptions =
            { RunMode = RunMode.Watching
              CacheBackend = None
              CacheKeyProvider = None
              FcsSuppressedCodes = None
              ExcludePatterns = []
              ExtraWatchPatterns = []
              FsEventsLatencySeconds = 0.25
              IdleExitMin = None
              PressureIdleFloorMin = None
              CheckerCacheSizeFactor = DefaultCheckerCacheSizeFactor
              Hosting = DaemonHosting.standalone () }

    /// Resolve the configured FCS-suppression option to the runtime `Set<int>`.
    /// `None` resolves to `Set.empty` — fshw deliberately ships no built-in
    /// suppressions. Projects that need to silence a code declare it via
    /// `<NoWarn>` in the fsproj or `#nowarn "code"` in source.
    let resolveFcsSuppressedCodes (configured: int list option) : Set<int> =
        configured |> Option.defaultValue [] |> Set.ofList

    /// What a full rediscovery drops from the checker: every project it is handed moves
    /// to a new generation (`ProjectSnapshots.invalidate`), so the checks after it
    /// type-check them again under new cache keys while the checks already under way
    /// finish against the entries they started with. Nothing is removed from the
    /// checker's caches — `InvalidateAll` and
    /// `ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients` both replace
    /// them under the running checks. The previous generation's entries are demoted to
    /// weak references as the new generation recomputes them, and released by the next
    /// collection (see `ProjectSnapshots.invalidate`). A hosted session is
    /// handed only its own projects, so its siblings' entries stay warm.
    let internal dropForRediscovery (checker: FSharpChecker) (projects: FSharpProjectOptions list) =
        for options in projects do
            ProjectSnapshots.invalidate checker options

    /// Build the daemon, with `processRegistry` installed (see `createWithCore`).
    let private constructDaemon
        (processRegistry: ProcessRegistry.Registry)
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (workspaceLoader: IWorkspaceLoader option)
        (mapProjectOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
        (watcherIsMacOSOverride: bool option)
        (watcherFactory: WatcherFactory)
        =
        let seams = DaemonHosting.seams opts.Hosting
        let watcherFactory = seams.Watcher watcherFactory

        let cacheBackend = opts.CacheBackend
        let cacheKeyProvider = opts.CacheKeyProvider

        let fcsSuppressedCodes = resolveFcsSuppressedCodes opts.FcsSuppressedCodes

        let excludePatterns = opts.ExcludePatterns
        let extraWatchPatterns = opts.ExtraWatchPatterns
        let fsEventsLatencySeconds = opts.FsEventsLatencySeconds
        let lifetime = new CancellationTokenSource()

        try
            let errorDir = Path.Combine(FsHwPaths.root repoRoot, "errors")

            let fileReporter: IErrorReporter =
                FsHotWatch.FileErrorReporter.FileErrorReporter(errorDir)

            fileReporter.ClearAll()
            // TWO stores, and which one a plugin's entries land in is decided by
            // `CacheResidency`, not by whichever directory happens to be handy.
            //
            // The workspace-local store keeps its historical home inside `.fshw/`. It
            // holds every verdict that asserts something about THIS checkout — a build's
            // artifacts, a test run that happened.
            //
            // The shared store lives OUTSIDE any checkout, namespaced by the repository
            // (not the workspace), so a freshly created workspace starts warm: its
            // entries are keyed purely on content, and a store under `.fshw/` would be
            // destroyed by the very act this exists to make cheap. It is handed the
            // repo root so the paths inside an entry are stored relatively and rebound
            // into whichever checkout reads them back.
            let taskCacheDir = Path.Combine(FsHwPaths.root repoRoot, "cache", "tasks")
            let localTaskCache = FsHotWatch.FileTaskCache.FileTaskCache(taskCacheDir)

            let sharedTaskCacheDir =
                Path.Combine(FsHwPaths.sharedCacheHome (), "cache", "tasks", RepoIdentity.namespaceOf repoRoot)

            let sharedTaskCache =
                FsHotWatch.FileTaskCache.FileTaskCache(sharedTaskCacheDir, repoRoot = repoRoot)

            let taskCache =
                FsHotWatch.CacheResidency.RoutedTaskCache(
                    localTaskCache,
                    sharedTaskCache,
                    FsHotWatch.CacheResidency.of_
                )

            let stats = localTaskCache.Stats
            let sharedStats = sharedTaskCache.Stats

            Logging.info
                "task-cache"
                $"Workspace cache: %d{stats.EntryCount} entries, %.1f{float stats.SizeBytes / 1024.0 / 1024.0} MB"

            let sharedPluginList = String.concat ", " FsHotWatch.CacheResidency.sharedPlugins

            Logging.info
                "task-cache"
                $"Shared cache (%s{sharedTaskCacheDir}): %d{sharedStats.EntryCount} entries, %.1f{float sharedStats.SizeBytes / 1024.0 / 1024.0} MB — shared plugins: %s{sharedPluginList}"

            let host =
                PluginHost(
                    checker,
                    repoRoot,
                    reporters = [ fileReporter ],
                    taskCache = taskCache,
                    fcsSuppressedCodes = fcsSuppressedCodes
                )

            let fcsSink = host.ActivitySinkFor(PluginActivity.FcsPluginName)

            let pipeline =
                match cacheBackend, cacheKeyProvider with
                | Some b, Some kp ->
                    CheckPipeline(
                        checker,
                        cacheBackend = b,
                        cacheKeyProvider = kp,
                        activity = fcsSink,
                        repoRoot = repoRoot,
                        frames = seams.Frames
                    )
                | Some b, None ->
                    CheckPipeline(
                        checker,
                        cacheBackend = b,
                        activity = fcsSink,
                        repoRoot = repoRoot,
                        frames = seams.Frames
                    )
                | _ -> CheckPipeline(checker, activity = fcsSink, repoRoot = repoRoot, frames = seams.Frames)

            Logging.info "cache" (FsHotWatch.InMemoryCheckCache.describeCheckCache cacheBackend)

            let graph = ProjectGraph()

            let loader =
                match workspaceLoader with
                | Some loader -> loader
                | None ->
                    // Init.init writes the muxer it is given into this process's
                    // DOTNET_HOST_PATH when that is unset, and in-process FCS finds the SDK
                    // as dirname(DOTNET_HOST_PATH). Its own lookup returns the first PATH
                    // entry as spelled, so hand it the installed muxer instead.
                    let muxer =
                        Paths.dotnetRoot.Value
                        |> Option.map (fun exe -> FileInfo(ProcessHelper.installedDotnet exe.FullName))

                    let toolsPath = Init.init (DirectoryInfo(repoRoot)) muxer
                    WorkspaceLoader.Create(toolsPath, [])

            // Plugins read the model from the host publication. A settled model is
            // announced while its attempt still holds discovery admission, so the
            // registered files it publishes are that attempt's membership.
            let discovery =
                DiscoveryCoordinator(
                    publish =
                        fun observation ->
                            host.WorkStore.PublishProjectModelWithFiles(
                                observation,
                                pipeline.GetAllRegisteredFiles() |> Set.ofList
                            )
                )

            let daemonCtRef = ref CancellationToken.None

            let delayForChange change =
                match change with
                | ProjectChanged _
                | SolutionChanged -> projectDebounceMs
                | SourceChanged _ -> sourceDebounceMs

            let batchCtx: BatchContext =
                { Host = host
                  InvalidateFcs =
                    if isNull (box checker) then
                        None
                    else
                        Some(fun () ->
                            pipeline.GetRegisteredProjects()
                            |> List.choose pipeline.GetProjectOptions
                            |> dropForRediscovery checker)
                  InvalidateFcsForProjects =
                    if isNull (box checker) then
                        None
                    else
                        Some(fun optsList ->
                            // The changed projects only; the full path hands
                            // `dropForRediscovery` every registered project.
                            for opts in optsList do
                                ProjectSnapshots.invalidate checker opts)
                  RepoRoot = repoRoot
                  Loader = loader
                  MapOptions = mapProjectOptions
                  Discovery = discovery
                  Graph = graph
                  Pipeline = pipeline
                  DaemonCt = daemonCtRef
                  FcsSuppressedCodes = fcsSuppressedCodes
                  ExcludePatterns = excludePatterns
                  ContentTracker = ContentDedup.Tracker()
                  InSessionBatchGen = ref 0L
                  Seams = seams
                  DepsGate =
                    if isNull (box checker) then
                        // No FCS analysis happens with a null checker (test
                        // daemons), so the gate would have nothing to guard.
                        None
                    else
                        let tracker = DepsFreshness.RecoveryTracker()
                        let runner = DepsFreshness.productionRestoreRunner repoRoot

                        Some(fun projPath ->
                            DepsFreshness.evaluateProject
                                (DepsFreshness.detectProjectFreshness repoRoot)
                                (DepsFreshness.depRelevantSignature repoRoot)
                                DepsFreshness.assetsPresent
                                runner
                                tracker
                                projPath) }

            // Change batches run one at a time under a supervisor. The worker's state is
            // the set of paths preprocessors wrote, whose watcher echoes are suppressed, and
            // the changes a failed batch still owes, which run ahead of the next request.
            let changeWorker =
                SupervisedWork.Queue(
                    host.WorkStore,
                    "changes",
                    { Suppressed = Set.empty; Owed = None },
                    Ipc.ambientRpcDeadline (),
                    (fun state (_: ChangeRequest) -> state),
                    (fun state failure ->
                        match failure.GetBaseException() with
                        | :? ModelKeptChangingException as kept -> { state with Owed = Some kept.Owed }
                        | _ -> state),
                    ignore,
                    (fun state request ct _ ->
                        async {
                            let owed = state.Owed |> Option.defaultValue { Changes = []; Admitted = Set.empty }

                            let changes = owed.Changes @ request.Changes

                            let! suppressed =
                                if List.isEmpty changes then
                                    async.Return state.Suppressed
                                else
                                    async {
                                        // Failure policy lives in `runDaemonStep`.
                                        match!
                                            runDaemonStep
                                                "processChanges"
                                                (processBatch
                                                    { batchCtx with DaemonCt = ref ct }
                                                    request.SeenAt
                                                    changes
                                                    state.Suppressed
                                                    owed.Admitted)
                                        with
                                        | Ok next -> return next
                                        | Result.Error failure -> return raise failure
                                    }

                            match request.FormatReplies with
                            | [] -> return { Suppressed = suppressed; Owed = None }
                            | replies ->
                                ct.ThrowIfCancellationRequested()
                                let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value
                                let run = host.RunPreprocessors(files)
                                let rendered = renderFormatAll files run

                                for reply in replies do
                                    reply.TrySetResult(rendered) |> ignore

                                return
                                    { Suppressed = Set.union suppressed (Set.ofList run.Modified)
                                      Owed = None }
                        })
                )

            // Watcher input is owned from the callback onward: while it debounces, while
            // it waits for the worker, and while the worker runs it.
            let changeInput =
                DebouncedWork.Queue(
                    host.WorkStore,
                    "changes",
                    changeWorker,
                    (fun earlier later ->
                        { Changes = earlier.Changes @ later.Changes
                          FormatReplies = earlier.FormatReplies @ later.FormatReplies
                          SeenAt = min earlier.SeenAt later.SeenAt })
                )

            let onChange change =
                Logging.debug "watcher" $"%O{change}"

                try
                    let receipt =
                        changeInput.Post(
                            { Changes = [ change ]
                              FormatReplies = []
                              SeenAt = DateTime.UtcNow },
                            TimeSpan.FromMilliseconds(float (delayForChange change))
                        )

                    // The batch logged its own failure; a receipt nobody else awaits is
                    // observed here so it is never an unobserved task exception.
                    receipt.ContinueWith(
                        (fun (failed: Task<unit>) ->
                            Logging.debug
                                "watcher"
                                $"change batch settled with %s{failed.Exception.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default
                    )
                    |> ignore
                with failure ->
                    Logging.warn "watcher" $"change not admitted (%O{change}): %s{failure.Message}"

            // The ONLY place a watcher can come from. A `OneShot` host never reaches
            // the factory, so its verdict cannot depend on native watcher startup.
            let watcher =
                match opts.RunMode with
                | RunMode.Watching ->
                    Some(
                        watcherFactory
                            repoRoot
                            onChange
                            watcherIsMacOSOverride
                            extraWatchPatterns
                            fsEventsLatencySeconds
                    )
                | RunMode.OneShot -> None

            let scanSignal = ScanSignal(cancellationToken = lifetime.Token)

            // One lease set per daemon: every scan takes a lease for its span, the
            // idle-exit scheduler and heartbeat read it.
            let scanLeases = ScanActivity.ScanLeases.create ()

            // Scans run one at a time under a supervisor. `scan-status` and the
            // generation read its published row, so they never wait for discovery.
            let scanOwner =
                SupervisedWork.Queue(
                    host.WorkStore,
                    "scan",
                    { ScanState = ScanIdle
                      Generation = 0L
                      LastFingerprint = Set.empty
                      Covered = None },
                    Ipc.ambientRpcDeadline (),
                    (fun state request ->
                        match request with
                        | RunScan _ ->
                            { state with
                                ScanState = Scanning(0, 0, DateTime.UtcNow) }
                        | SetScanState _ -> state),
                    (fun state _ -> { state with ScanState = ScanIdle }),
                    // Waiters wake only after the completed generation is published.
                    (fun state -> scanSignal.SignalGeneration state.Generation),
                    (fun state request ct publish ->
                        async {
                            match request with
                            | SetScanState value -> return { state with ScanState = value }
                            | RunScan admittedAt when scanAnswers state.Covered admittedAt ->
                                let _, completedState = state.Covered.Value

                                Logging.info
                                    "scan"
                                    "Scan request answered by the scan that read the tree after it was admitted — not scanning again"

                                return
                                    { state with
                                        ScanState = completedState }
                            | RunScan _ ->
                                // Failure policy lives in `runDaemonStep`.
                                match!
                                    runDaemonStep "performScan" (performScan batchCtx scanLeases state ct publish)
                                with
                                | Ok next -> return next
                                | Result.Error failure -> return raise failure
                        })
                )

            let formatAllViaAgent () =
                async {
                    let reply =
                        TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

                    try
                        // A zero delay flushes pending watcher input first, so formatting
                        // runs after the changes that preceded it.
                        let receipt =
                            changeInput.Post(
                                { Changes = []
                                  FormatReplies = [ reply ]
                                  SeenAt = DateTime.UtcNow },
                                TimeSpan.Zero
                            )

                        do! receipt |> Async.AwaitTask
                        return! reply.Task |> Async.AwaitTask
                    with failure ->
                        Logging.error "daemon" $"format request failed: %O{failure}"
                        return "format failed"
                }

            new Daemon(
                host,
                watcher,
                pipeline,
                graph,
                repoRoot,
                loader,
                mapProjectOptions,
                discovery,
                ScanAgent(scanOwner, scanSignal),
                daemonCtRef,
                new ManualResetEventSlim(false),
                scanSignal,
                fcsSuppressedCodes,
                lifetime,
                Some formatAllViaAgent,
                excludePatterns,
                opts.IdleExitMin,
                opts.PressureIdleFloorMin,
                scanLeases,
                processRegistry,
                changeInput.Close,
                checker
            )
        with _ ->
            lifetime.Dispose()
            reraise ()

    /// Build a daemon, its process registry installed while it is built and not after.
    ///
    /// The registry is scoped by an `AsyncLocal`, and an AsyncLocal value is only
    /// visible to ExecutionContexts captured AFTER it is set. Construction captures
    /// every context the daemon works in: the PluginHost's agents, the change/scan
    /// supervisors, the plugin handlers registered later. So the registry is installed
    /// first, and a plugin's spawned child resolves to it; installed any later, the
    /// child would resolve NO registry, `KillAll` would reap nothing, and a wedged
    /// plugin's process would outlive the daemon as an init-reparented orphan.
    ///
    /// Once built, the caller's own registry is back. The daemon's is the daemon's: left
    /// in the caller's context, a spawn the caller makes later would land in it, and
    /// after the daemon is disposed be refused by a registry that has shut down.
    let private createWithCore
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (workspaceLoader: IWorkspaceLoader option)
        (mapProjectOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
        (watcherIsMacOSOverride: bool option)
        (watcherFactory: WatcherFactory)
        =
        let processRegistry = ProcessRegistry.Registry()

        using (ProcessRegistry.install processRegistry) (fun _ ->
            constructDaemon
                processRegistry
                checker
                repoRoot
                opts
                workspaceLoader
                mapProjectOptions
                watcherIsMacOSOverride
                watcherFactory)

    /// Create a daemon with the given checker (internal, for testing).
    let internal createWith (checker: FSharpChecker) (repoRoot: string) (opts: DaemonOptions) =
        createWithCore
            checker
            repoRoot
            opts
            None
            (Ionide.ProjInfo.FCS.mapManyOptions >> Seq.toList)
            None
            FileWatcher.create

    /// Deterministic watcher-construction seam: proves `RunMode.OneShot` never
    /// invokes the factory and `RunMode.Watching` invokes it exactly once.
    let internal createWithWatcherFactory
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (watcherFactory: WatcherFactory)
        =
        createWithCore checker repoRoot opts None (Ionide.ProjInfo.FCS.mapManyOptions >> Seq.toList) None watcherFactory

    /// Deterministic watcher-platform seam for daemon integration tests. Native
    /// FSEvents behavior has dedicated tests; scoped daemon tests use the
    /// FileSystemWatcher path so stale pre-subscription events cannot enter their
    /// project-selection assertions.
    let internal createWithWatcherPlatform
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (isMacOSOverride: bool option)
        =
        createWithCore
            checker
            repoRoot
            opts
            None
            (Ionide.ProjInfo.FCS.mapManyOptions >> Seq.toList)
            isMacOSOverride
            FileWatcher.create

    /// Deterministic loader/mapping seam for discovery concurrency tests.
    let internal createWithWorkspaceLoader
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (loader: IWorkspaceLoader)
        (mapProjectOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
        =
        createWithCore checker repoRoot opts (Some loader) mapProjectOptions None FileWatcher.create

    /// Loader and watcher seam: a test holds discovery and delivers watcher changes
    /// itself, with no native watcher behind them.
    let internal createWithWorkspaceLoaderAndWatcher
        (checker: FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        (loader: IWorkspaceLoader)
        (mapProjectOptions: Types.ProjectOptions list -> FSharpProjectOptions list)
        (watcherFactory: WatcherFactory)
        =
        createWithCore checker repoRoot opts (Some loader) mapProjectOptions None watcherFactory

    /// The warm checker a daemon runs on.
    ///
    /// `keepAssemblyContents = true` is what lets an analyzer walk a TYPED TREE:
    /// `AnalyzersPlugin` reads `ImplementationFile` off the check results and hands
    /// it to the SDK as `CliContext.TypedTree`. An analyzer that needs typed
    /// information and receives `None` returns no findings rather than failing, so
    /// dropping this flag would not surface as an error — it would silently weaken
    /// every typed-tree rule. The retention is therefore a capability, and its cost
    /// is the thing to measure, not the thing to assume.
    ///
    /// Named rather than inlined into `create` so that capability has a test: a test
    /// checks a file through this checker and asserts implementation contents came
    /// back.
    /// `keepAllBackgroundResolutions = false` because nothing reads them.
    ///
    /// That flag retains every symbol-use resolution for a project so that
    /// `GetAllUsesOfAllSymbolsInFile`, `GetUsesOfSymbolInFile` and the semantic
    /// classification APIs can be answered later. This daemon calls none of them.
    /// Audited across `src/`: exactly two things are read off a check result —
    /// `checkResults.Diagnostics` and `.ImplementationFile` — and the second is fed
    /// by `keepAssemblyContents`, not by this. The flag appeared nowhere but its own
    /// construction site.
    ///
    /// Retained resolutions are held for the whole project graph, so on a 2000-file
    /// tree this is retention proportional to the repository for a capability with
    /// no consumer. Scans on that tree were recorded at a 9.4 GB median RSS and a
    /// 26.6 GB managed peak.
    ///
    /// If a future feature needs symbol uses — a rename, a find-references, a
    /// semantic highlight — turn this back on WITH that feature, and measure it
    /// then. It is cheap to restore and expensive to leave on speculatively.
    ///
    /// `cacheSizes` bounds the TransparentCompiler's caches; see
    /// `DefaultCheckerCacheSizeFactor`.
    let createCheckerWithCacheSizes (cacheSizes: TransparentCompiler.CacheSizes) =
        FSharpChecker.Create(
            keepAssemblyContents = true,
            keepAllBackgroundResolutions = false,
            parallelReferenceResolution = true,
            useTransparentCompiler = true,
            transparentCompilerCacheSizes = cacheSizes
        )

    /// `createCheckerWithCacheSizes` at `DefaultCheckerCacheSizeFactor`.
    let createChecker () =
        createCheckerWithCacheSizes (TransparentCompiler.CacheSizes.Create DefaultCheckerCacheSizeFactor)

    /// `create` with the checker constructor as a parameter, so a test can see the
    /// cache sizes that `opts.CheckerCacheSizeFactor` turns into.
    let internal createUsing
        (makeChecker: TransparentCompiler.CacheSizes -> FSharpChecker)
        (repoRoot: string)
        (opts: DaemonOptions)
        =
        Logging.info "config" $"checker: cacheSizeFactor=%d{opts.CheckerCacheSizeFactor}"

        let makeChecker = (DaemonHosting.seams opts.Hosting).Checker makeChecker

        let checker =
            makeChecker (TransparentCompiler.CacheSizes.Create opts.CheckerCacheSizeFactor)

        createWith checker repoRoot opts

    /// Create a new daemon for the given repository root with a warm FSharpChecker.
    /// Pass `DaemonOptions.defaults` and override only the fields you need.
    let create (repoRoot: string) (opts: DaemonOptions) =
        createUsing createCheckerWithCacheSizes repoRoot opts
