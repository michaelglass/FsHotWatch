module FsHotWatch.Build.BuildPlugin

open System
open System.IO
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.Lifecycle
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers

/// Why a project's compiled artifact is considered stale after a "successful"
/// build. Detected by post-build verification — either the DLL never appeared
/// on disk, or the DLL's mtime is older than the newest source file.
type StaleReason =
    | DllMissing of dllPath: string
    | DllOlderThanSources of dllTime: DateTime * srcTime: DateTime

type StaleArtifact =
    { Project: string; Reason: StaleReason }

type BuildOutcome =
    | BuildPassed of output: string
    /// Build subprocess returned success but post-build verification found one
    /// or more stale DLLs (MSBuild's incremental cache likely lied). Demoted
    /// here so downstream plugins receive BuildFailed and never run against
    /// stale artifacts. Carries the structured stale-artifact list so cache
    /// replay reproduces the same diagnostic deterministically.
    | BuildArtifactsStale of stale: StaleArtifact list * output: string
    | BuildOutputFailed of outputs: string list

/// The build plugin has no in-flight build of its own (the framework's
/// `RunExclusive "build"` owns single-flighting). `PendingFiles` buffers
/// file changes that arrived before this plugin's `dependsOn` were satisfied.
/// Every source change — test files included — drives a real build; skipping it
/// for test-only changes leaves a stale on-disk test DLL for
/// `dotnet run --no-build` to execute (see ADR-012). `LastBuild` carries the most
/// recent build's lifecycle.
type BuildState =
    { LastBuild: Lifecycle<Idle, BuildOutcome option>
      PendingFiles: FileChangeKind list
      SatisfiedDeps: Set<string> }

/// Internal message posted from the async build runner back to the plugin's
/// own mailbox. Carries the outcome AND the parsed diagnostic entries so the
/// synchronous Custom handler can apply them to the error ledger and emit
/// BuildCompleted within the framework's per-event capture window — required
/// for the task cache to record errors and downstream emissions on terminal
/// status. The summary is deliberately NOT carried: the handler derives it from
/// the outcome via `buildSummary`, the same pure helper the worker logs with, so
/// the two can never disagree.
type BuildMsg = BuildDone of outcome: BuildOutcome * entries: ErrorEntry list * elapsed: TimeSpan

/// Diagnostic for the "MSBuild exited non-zero but produced no parseable
/// diagnostics" failure mode (typically a bail during evaluation/restore).
/// Surfaces exit code, output size, and any "Time Elapsed" tail to give the
/// next debugging session a starting point.
let formatSilentFailureDiagnostic (exitCode: int) (output: string) : string =
    let elapsed =
        let m =
            System.Text.RegularExpressions.Regex.Match(output, @"Time Elapsed ([\d:.]+)")

        if m.Success then $" elapsed={m.Groups.[1].Value}" else ""

    $"MSBuild aborted before producing diagnostics: exit=%d{exitCode} output=%d{output.Length} bytes%s{elapsed}"

let decideBuildOutcome (success: bool) (output: string) : BuildOutcome * ErrorEntry list =
    let parsed = BuildDiagnostics.parseMSBuildDiagnostics output

    if success then
        BuildPassed output, parsed
    else
        let entries =
            if parsed.IsEmpty then
                [ ErrorEntry.error output ]
            else
                parsed

        BuildOutputFailed [ output ], entries

/// stable merkle key for the build cache, independent of the cold-start
/// guard. Pure function — exposed `internal` so unit tests can assert the key
/// responds to its inputs without driving a full plugin lifecycle to flip the
/// guard. Production use goes through the closure in `create` below.
let internal computeBuildCacheKey
    (buildCommand: string)
    (buildArgs: string)
    (dependsOn: string list)
    (inputsHash: string)
    : ContentHash =
    FsHotWatch.TaskCache.merkleCacheKey
        [ "plugin-version", "build-merkle-v1"
          "command", buildCommand
          "args", buildArgs
          "depends-on", String.concat "," (List.sort dependsOn)
          "inputs", inputsHash ]

/// build all-inputs merkle. Hashes the on-disk CONTENT of every source
/// file the project graph knows about, plus every .fsproj — every input, every
/// `Compute`, with no memo.
///
/// Do NOT reintroduce a memo keyed on mtime. `rsync -a`/`cp -p`/`tar -x`/a git
/// checkout restoring an old mtime all change CONTENT while PRESERVING mtime, so a
/// `(path, mtime)` key never moves, the merkle never moves, and the task cache
/// replays a stale `BuildDone` forever. `(path, size, mtime)` is fooled by the same
/// rewrite. See docs/adr-008-mtime-is-not-a-content-oracle.md.
///
/// The cost is real but small: `Compute` runs once per build TRIGGER (not per file
/// event), and SHA-256 over source text is dominated by the `dotnet build` it gates.
type internal BuildInputsHasher(graph: FsHotWatch.ProjectGraph.IProjectGraphReader) =
    // Honest "missing" sentinel for non-existent files; let real IO exceptions
    // (UnauthorizedAccessException, IOException for locked files, etc.) propagate
    // up to decideBuildOutcome instead of folding "read-error" into the merkle.
    let hashFile (path: string) : string option =
        if not (File.Exists path) then
            None
        else
            Some(FsHotWatch.CheckCache.sha256Hex (File.ReadAllText path))

    member _.Compute() : string =
        let sourceFiles = graph.GetAllFiles() |> List.map AbsFilePath.value

        let projectFiles = graph.GetAllProjects() |> List.map AbsProjectPath.value

        // AUTOMATION-303 case 2. MSBuild's IMPLICIT IMPORTS — `Directory.Build.props`,
        // `Directory.Build.targets`, `Directory.Packages.props` — are inputs to the
        // build that no project NAMES, so they appear in neither list above: they are
        // not compile items (so not in `GetAllFiles`) and not projects (so not in
        // `GetAllProjects`). A `<Compile Include=…>` in one of them adds a file to every
        // project beneath it while leaving every project file and every already-known
        // source file byte-identical — the merkle does not move, the task cache replays
        // "built N projects (cached)", and the new file is never compiled. That is case
        // 2's exact shape (a cached build hiding a real compile error), reached through
        // the one door the project-file merkle left open.
        //
        // Found per project by MSBuild's own nearest-ancestor rule
        // (`StructureFiles.implicitImportsFor`), so a file MSBuild would not import
        // cannot invalidate a build it could not have affected.
        let implicitImports =
            projectFiles
            |> List.collect FsHotWatch.StructureFiles.implicitImportsFor
            |> List.distinct

        let allInputs = (sourceFiles @ projectFiles @ implicitImports) |> List.distinct |> List.sort
        let sb = System.Text.StringBuilder()

        for path in allInputs do
            let h =
                match hashFile path with
                | Some h -> h
                | None -> "missing" // distinct from any real sha256 hash

            sb.Append(path.Length) |> ignore
            sb.Append(':') |> ignore
            sb.Append(path) |> ignore
            sb.Append('@') |> ignore
            sb.Append(h) |> ignore
            sb.Append('\n') |> ignore

        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

let create
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (testProjectNames: string list)
    (buildTemplate: string option)
    (dependsOn: string list)
    (timeoutSec: int option)
    =
    let buildCommand = command
    let buildArgs = args

    /// Force the NEXT build to be real instead of a cache replay (AUTOMATION-224).
    ///
    /// The cache key below is a content merkle over SOURCE files only, so a hit
    /// asserts the OUTPUTS are current on evidence that never looked at them. That
    /// holds right up until `bin/` is changed out from under a tree whose sources
    /// are unchanged — a working-copy flip is the common way. Then the build
    /// replays "built N projects (cached)" without running, TestPrune's freshness
    /// gate correctly finds the output stale and defers ("waiting on build"), and
    /// nothing ever rebuilds: a deadlock that blocked a production deploy 3x.
    ///
    /// Set by the `force-rebuild` command, which `confirm` issues. Consumed by
    /// `cacheKey` on the LOOKUP (a `FileChanged`) and cleared once a build has
    /// actually completed, so the fresh result still gets stored normally.
    let forceRebuild = ref false

    let testProjectNameSet = testProjectNames |> Set.ofList

    let buildTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    // A build command is a SILENT child: `dotnet build -v q` prints nothing until
    // it finishes, and a `sh -c "dotnet build 2> log; cat log"` wrapper (the shape
    // real repos use) buffers everything to the very end. So its output proves
    // nothing about liveness and a launch deadline would false-kill a healthy slow
    // build — `buildTimeout` is the bound.
    let buildBounds = ProcessBounds.silent buildTimeout

    // Path normalization happens once at the SourceChanged → AbsFilePath boundary
    // (callers inject `AbsFilePath.create` per file).
    let isTestFile (file: AbsFilePath) =
        graph.GetProjectsForFile(file)
        |> List.exists (fun proj ->
            testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj)))

    let isTestProject (proj: AbsProjectPath) =
        testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj))

    let projectStem (p: AbsProjectPath) =
        Path.GetFileNameWithoutExtension(AbsProjectPath.value p)

    /// Post-build contract enforcement. For every project the graph knows
    /// about, compare the canonical DLL's mtime against the max source mtime.
    /// Returns the stale projects so the worker can demote BuildPassed to
    /// BuildArtifactsStale and downstream plugins (TestPrune) receive a
    /// BuildFailed signal instead of running against artifacts MSBuild's
    /// incremental cache silently failed to update.
    ///
    /// mtime IS the right signal HERE, unlike the content-hashed build-input
    /// merkle, because the question is strictly temporal: "was the DLL regenerated
    /// *after* the newest source?" — i.e. did MSBuild's incremental cache skip
    /// relinking an artifact a real edit should have rebuilt. In that failure mode
    /// the edit bumped the source mtime, so a DLL older than its source is the tell.
    /// There is no "expected DLL content" to hash against, so a content check is
    /// not even expressible here; the merkle is the content guard and this is its
    /// temporal complement.
    /// See docs/adr-008-mtime-is-not-a-content-oracle.md.
    let verifyArtifactsFresh () : StaleArtifact list =
        [ for proj in graph.GetAllProjects() do
              match graph.GetCanonicalDllPath(proj) with
              | None -> () // no TFM — nothing to verify
              | Some dllPath ->
                  let stem = projectStem proj

                  if not (File.Exists dllPath) then
                      yield
                          { Project = stem
                            Reason = DllMissing dllPath }
                  else
                      let dllTime = File.GetLastWriteTimeUtc dllPath

                      match graph.GetMaxSourceMtime(proj) with
                      | Some srcTime when dllTime < srcTime ->
                          yield
                              { Project = stem
                                Reason = DllOlderThanSources(dllTime, srcTime) }
                      | _ -> () ]

    let depNames = dependsOn |> Set.ofList
    let allDepsSatisfied deps = Set.isSubset depNames deps

    let countBuiltProjects (output: string) =
        BuildDiagnostics.parseDllPaths output |> Map.count

    /// Phrase a single stale-artifact case for human-readable diagnostics.
    /// Worker-side so cache replay reproduces the same message verbatim.
    let formatStaleArtifact (s: StaleArtifact) : string =
        match s.Reason with
        | DllMissing path -> $"%s{s.Project}: DLL missing at %s{path}"
        | DllOlderThanSources(dllTime, srcTime) ->
            sprintf "%s: DLL %O older than newest source %O" s.Project dllTime srcTime

    /// The same stale-artifact list phrased as a cache-BYPASS diagnostic.
    ///
    /// Distinct from `staleDiagnostic`, which condemns a build that just RAN. This one
    /// explains why no stored result was served, and it says the recovery out loud:
    /// the rebuild it triggers IS the fix. The wedge this closes cost ~90 minutes of a
    /// night to the belief that `dotnet fshw stop` was required (it is not, and the
    /// on-disk task cache survives a restart anyway — so that folklore was never even
    /// a reliable cure).
    let replayBypassDiagnostic (stale: StaleArtifact list) : string =
        "Build cache bypassed: a stored result may not be replayed over outputs it no\n"
        + "longer describes. Rebuilding now — no daemon restart is needed.\n\n"
        + (stale |> List.map formatStaleArtifact |> String.concat "\n")

    /// Wrap a stale-artifact list in a "MSBuild lied" diagnostic suitable for
    /// the error ledger / BuildFailed payload.
    let staleDiagnostic (stale: StaleArtifact list) : string =
        "Build subprocess reported success but post-build verification\n"
        + "found stale artifacts (MSBuild incremental cache likely lied).\n"
        + "Re-run with `dotnet build --no-incremental` (or delete bin/ and obj/).\n\n"
        + (stale |> List.map formatStaleArtifact |> String.concat "\n")

    /// The one-line human verdict for each outcome. Pure, shared by the async
    /// worker's live log line and the synchronous BuildDone handler's terminal
    /// verdict, so what the log said and what the status carries can never
    /// disagree.
    let buildSummary (outcome: BuildOutcome) (entries: ErrorEntry list) : string =
        match outcome with
        | BuildPassed out ->
            let n = countBuiltProjects out
            if n > 0 then $"built {n} projects" else "build succeeded"
        | BuildArtifactsStale(stale, _) -> $"build failed: %d{stale.Length} stale artifacts"
        | BuildOutputFailed _ ->
            let errCount =
                entries
                |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Error)
                |> List.length

            $"build failed: %d{errCount} errors"

    /// Run from the async build worker. Logging happens here (live UI), but
    /// the *captured* operations (ReportErrors / ClearErrors /
    /// EmitBuildCompleted / the terminal status) are deferred to the
    /// synchronous Custom BuildDone handler so the framework's per-event
    /// capture window records them for the task cache. Returns the completion
    /// message; the framework posts it back via RunExclusive.
    let applyBuildOutcome
        (ctx: PluginCtx<BuildMsg>)
        (outcome: BuildOutcome)
        (entries: ErrorEntry list)
        (elapsed: TimeSpan)
        =
        match outcome with
        | BuildPassed _ -> ctx.Log(buildSummary outcome entries)
        | BuildArtifactsStale(stale, _) ->
            // Per-project detail, not just the count: an intermittent stale-artifact
            // failure otherwise surfaces only as "build failed: 1 stale artifacts",
            // with no way to tell which project.
            ctx.Log(staleDiagnostic stale)
            error "build" (staleDiagnostic stale)
        | BuildOutputFailed _ -> ()

        BuildDone(outcome, entries, elapsed)

    /// Run verifyArtifactsFresh on a BuildPassed outcome and demote to
    /// BuildArtifactsStale if any project's DLL is stale. Other outcomes
    /// pass through. Worker-side: keeps the per-project mtime stat calls off
    /// the synchronous handler's capture window and lets cache replay re-emit
    /// the identical structured stale list.
    let verifyAndDemote (outcome: BuildOutcome) : BuildOutcome =
        match outcome with
        | BuildPassed out ->
            match verifyArtifactsFresh () with
            | [] -> outcome
            | stale -> BuildArtifactsStale(stale, out)
        | _ -> outcome

    let startBuild (ctx: PluginCtx<BuildMsg>) (idle: Lifecycle<Idle, BuildOutcome option>) =
        let buildStarted = DateTime.UtcNow
        ctx.Log $"Running: %s{buildCommand} %s{buildArgs}"

        // RunExclusive "build": the framework guarantees only one build runs at a
        // time (and reports Running at the claim). Concurrent
        // FileChanged-while-building triggers land on SlotBusy and are safe to
        // skip — the next FileChanged re-triggers.
        let claim =
            ctx.RunExclusive
                "build"
                (PluginCtxHelpers.withSubtask
                    ctx
                    "build"
                    "dotnet build"
                    (async {
                        try
                            let result = runProcess buildCommand buildArgs ctx.RepoRoot environment buildBounds

                            let (rawOutcome, entries) =
                                decideBuildOutcome (isSucceeded result) (outputOf result)

                            let outcome = verifyAndDemote rawOutcome

                            match outcome, result with
                            | BuildOutputFailed _, TimedOut(after, _, kill) ->
                                // The full diagnostic already rides in `entries` (via
                                // `outputOf`); this is the one-liner, so it gets the short
                                // marker — a build tree we could not kill still holds the
                                // obj/ locks the next build is about to trip over.
                                let summary = $"timed out after %d{int after.TotalSeconds}s%s{renderKillBrief kill}"

                                ctx.Log "Build TIMED OUT"
                                error "build" "Build TIMED OUT"
                                ctx.CompleteWithTimeout summary
                            | BuildOutputFailed _, Failed(exitCode, output) ->
                                ctx.Log "Build FAILED"
                                error "build" "Build FAILED"

                                let parsedCount =
                                    BuildDiagnostics.parseMSBuildDiagnostics (ProcessOutput.text output)
                                    |> List.length

                                if parsedCount = 0 then
                                    // "Build FAILED / 0 diagnostics" is precisely the shape an
                                    // unfinished drain fakes — so this diagnostic renders the
                                    // capture, which NAMES the incomplete read rather than
                                    // letting a silence we never heard read as a silent build.
                                    let detail = formatSilentFailureDiagnostic exitCode (renderOutput output)
                                    ctx.Log detail
                                    error "build" detail
                            | BuildOutputFailed _, _ ->
                                ctx.Log "Build FAILED"
                                error "build" "Build FAILED"
                            | _ -> ()

                            return applyBuildOutcome ctx outcome entries (DateTime.UtcNow - buildStarted)
                        with ex ->
                            let crashEntry = ErrorEntry.error ex.Message
                            // ReportErrors / EmitBuildCompleted belong to the synchronous
                            // BuildDone handler, not here — see `applyBuildOutcome`.
                            return
                                BuildDone(
                                    BuildOutputFailed [ ex.Message ],
                                    [ crashEntry ],
                                    DateTime.UtcNow - buildStarted
                                )
                    }))

        match claim with
        | Claimed -> ()
        | SlotBusy ->
            // The FileChanged guard normally catches this earlier; this is the
            // race-free backstop.
            info "build" "Skipping: build already in progress"

        // State carries the prior idle lifecycle. The synchronous BuildDone
        // handler advances Lifecycle.start ▸ complete when the framework posts
        // the completion message back. "is the build running" is owned by
        // ctx.IsRunning "build".
        { LastBuild = idle
          PendingFiles = []
          SatisfiedDeps = Set.empty }

    let startTemplateBuild
        (ctx: PluginCtx<BuildMsg>)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (template: string)
        (files: AbsFilePath list)
        =
        let nonTestFiles = files |> List.filter (fun f -> not (isTestFile f))

        let affected = graph.GetAffectedProjects(nonTestFiles)

        let buildable = affected |> List.filter (fun p -> not (isTestProject p))

        if buildable.IsEmpty then
            startBuild ctx idle
        else
            let buildableSet = buildable |> Set.ofList

            let roots =
                buildable
                |> List.filter (fun proj ->
                    let dependents = graph.GetDependents(proj)
                    dependents |> List.exists (fun d -> buildableSet.Contains(d)) |> not)

            let buildStarted = DateTime.UtcNow

            let claim =
                ctx.RunExclusive
                    "build"
                    (PluginCtxHelpers.withSubtask
                        ctx
                        "build"
                        $"dotnet build ({roots.Length} roots)"
                        (async {
                            try
                                let mutable failures = []
                                let mutable outputs = []

                                for root in roots do
                                    let rootStr = AbsProjectPath.value root
                                    let rendered = template.Replace("{project}", rootStr)
                                    let (cmd, cmdArgs) = splitCommand rendered
                                    ctx.Log $"Running template: %s{cmd} %s{cmdArgs}"

                                    try
                                        let result = runProcess cmd cmdArgs ctx.RepoRoot environment buildBounds
                                        let output = outputOf result
                                        outputs <- output :: outputs

                                        match result with
                                        | Succeeded _ -> ()
                                        | TimedOut(after, _, kill) ->
                                            let summary =
                                                $"timed out after %d{int after.TotalSeconds}s%s{renderKillBrief kill}"

                                            ctx.Log $"Template build TIMED OUT for %s{rootStr}"
                                            error "build" $"Template build TIMED OUT for %s{rootStr}"
                                            ctx.CompleteWithTimeout summary
                                            failures <- output :: failures
                                        | Failed _ ->
                                            ctx.Log $"Template build FAILED for %s{rootStr}"
                                            error "build" $"Template build FAILED for %s{rootStr}"
                                            failures <- output :: failures
                                    with ex ->
                                        ctx.Log $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                        error "build" $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                        failures <- ex.Message :: failures

                                let failedOutputs = failures |> List.rev

                                let (rawOutcome, entries) =
                                    if failures.IsEmpty then
                                        let combinedOutput = outputs |> List.rev |> String.concat "\n"
                                        decideBuildOutcome true combinedOutput
                                    else
                                        let failedText = failedOutputs |> String.concat "\n"
                                        let parsed = BuildDiagnostics.parseMSBuildDiagnostics failedText

                                        let entries =
                                            if parsed.IsEmpty then
                                                failedOutputs |> List.map ErrorEntry.error
                                            else
                                                parsed

                                        BuildOutputFailed failedOutputs, entries

                                return
                                    applyBuildOutcome
                                        ctx
                                        (verifyAndDemote rawOutcome)
                                        entries
                                        (DateTime.UtcNow - buildStarted)
                            with ex ->
                                error "build" $"Unexpected error: %s{ex.Message}"

                                return
                                    BuildDone(
                                        BuildOutputFailed [ ex.Message ],
                                        [ ErrorEntry.error ex.Message ],
                                        DateTime.UtcNow - buildStarted
                                    )
                        }))

            match claim with
            | Claimed -> ()
            | SlotBusy ->
                // Race-free backstop; the FileChanged guard normally catches this.
                info "build" "Skipping: build already in progress"

            { LastBuild = idle
              PendingFiles = []
              SatisfiedDeps = Set.empty }

    let handleSourceChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (files: AbsFilePath list)
        =
        // A test-file-only change is NOT a build no-op: the changed test project is
        // run by test-prune via `dotnet run --no-build`, which executes the on-disk
        // assembly, and only MSBuild re-emits that assembly — FCS's in-memory
        // `BatchChecked` type-check signal does not. Skipping the build for such a
        // change leaves a stale DLL for `--no-build` to run → false green (ADR-012).
        match buildTemplate with
        | Some template ->
            { (startTemplateBuild ctx idle template files) with
                SatisfiedDeps = state.SatisfiedDeps }
        | None ->
            { (startBuild ctx idle) with
                SatisfiedDeps = state.SatisfiedDeps }

    let handleProjectChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        =
        { (startBuild ctx idle) with
            SatisfiedDeps = state.SatisfiedDeps }

    { Name = PluginName.create "build"
      Init =
        { LastBuild = Lifecycle.create None
          PendingFiles = []
          SatisfiedDeps = Set.empty }
      Update =
        fun ctx state event ->
            async {
                match event with
                // --- CommandCompleted: track dependency satisfaction ---
                | CommandCompleted result when depNames.Contains(result.Name) ->
                    match result.Outcome with
                    | FsHotWatch.Events.CommandFailed _ ->
                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"dependency failed: %s{result.Name}"
                                $"dependency failed: %s{result.Name}"
                                TimeSpan.Zero
                        )

                        return state
                    | FsHotWatch.Events.CommandSucceeded _ ->
                        let newDeps = Set.add result.Name state.SatisfiedDeps

                        if allDepsSatisfied newDeps then
                            let pendingFiles = state.PendingFiles

                            let updatedState = { state with SatisfiedDeps = newDeps }

                            let hasProjectChange =
                                pendingFiles
                                |> List.exists (function
                                    | ProjectChanged _ -> true
                                    | _ -> false)

                            let sourceFiles =
                                pendingFiles
                                |> List.collect (function
                                    | SourceChanged files -> files
                                    | _ -> [])
                                |> List.map AbsFilePath.create
                                |> List.distinct

                            match hasProjectChange, sourceFiles with
                            | true, _ -> return handleProjectChanged ctx updatedState updatedState.LastBuild
                            | _, _ :: _ ->
                                return handleSourceChanged ctx updatedState updatedState.LastBuild sourceFiles
                            | _ -> return updatedState
                        else
                            return { state with SatisfiedDeps = newDeps }

                // --- FileChanged: buffer if deps not yet satisfied ---
                | FileChanged change when not depNames.IsEmpty && not (allDepsSatisfied state.SatisfiedDeps) ->
                    info "build" "Buffering file change — waiting for dependencies"

                    return
                        { state with
                            PendingFiles = state.PendingFiles @ [ change ] }

                // --- FileChanged: drop while a build is in flight (framework single-flight) ---
                | FileChanged _ when ctx.IsRunning "build" ->
                    info "build" "Skipping: build already in progress"
                    return state

                // --- FileChanged: normal handling (no deps or all satisfied) ---
                | FileChanged(SourceChanged files) ->
                    return handleSourceChanged ctx state state.LastBuild (files |> List.map AbsFilePath.create)
                | FileChanged(ProjectChanged _) -> return handleProjectChanged ctx state state.LastBuild
                | Custom(BuildDone(outcome, entries, elapsed)) ->
                    // A build has now actually run, which is what `force-rebuild`
                    // asked for. Cleared HERE rather than where `cacheKey` consumed
                    // it, so a lookup that never reached a build (a suppressed or
                    // superseded dispatch) cannot silently spend the request and
                    // leave the artifacts stale anyway.
                    forceRebuild.Value <- false

                    // The completion message arrives carrying the pre-build idle
                    // lifecycle; advance it through Running ▸ Completed for
                    // activity-log bookkeeping.
                    let prevIdle = state.LastBuild

                    let idle = Lifecycle.complete (Some outcome) (Lifecycle.start prevIdle)

                    // Apply captured operations within this synchronous handler so
                    // the framework's cache-write window records them; replay of
                    // a cached BuildDone re-fires them via EmittedEvents + Errors.
                    //
                    // Contract: BuildSucceeded means every project's DLL is
                    // up-to-date with its sources. The async worker already demoted
                    // BuildPassed to BuildArtifactsStale where it wasn't, so this
                    // handler only dispatches the three terminal cases — each
                    // carrying the same `buildSummary` line the worker logged, so
                    // log and status can never disagree.
                    match outcome with
                    | BuildPassed _ ->
                        if entries.IsEmpty then
                            ctx.ClearErrors "<build>"
                        else
                            ctx.ReportErrors "<build>" entries

                        ctx.EmitBuildCompleted(BuildSucceeded)

                        PluginCtxHelpers.completeWith ctx (buildSummary outcome entries) elapsed
                    | BuildArtifactsStale(stale, _) ->
                        let detail = staleDiagnostic stale
                        let entry = ErrorEntry.error detail
                        ctx.ReportErrors "<build>" (entry :: entries)
                        ctx.EmitBuildCompleted(BuildFailed [ detail ])

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                "Build artifact verification failed"
                                (buildSummary outcome entries)
                                elapsed
                        )
                    | BuildOutputFailed outputs ->
                        ctx.ReportErrors "<build>" entries
                        ctx.EmitBuildCompleted(BuildFailed outputs)
                        let errorDetail = outputs |> String.concat "\n" |> truncateOutput 5

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"Build failed: %s{errorDetail}"
                                (buildSummary outcome entries)
                                elapsed
                        )

                    return
                        { state with
                            LastBuild = idle
                            PendingFiles = []
                            SatisfiedDeps = Set.empty }

                | _ -> return state
            }
      Commands =
        [ // Idempotent and cheap: it sets a flag, it does not build. The next cache
          // LOOKUP misses, so the build runs for real and re-emits the artifacts
          // TestPrune gates on. `confirm` calls it because the merge verb does not
          // get to trust a cache when its job is to be the thing you trust.
          //
          // The literal, not a shared constant: plugins live BELOW the CLI, so
          // `IpcParsing.ForceRebuildCommand` is not visible here. Same split as
          // "set-scope"/"run-tests" in TestPrunePlugin. The CLI-side constant
          // carries the contract doc; a test pins the two spellings together.
          "force-rebuild",
          fun _ctx _state _args ->
              async {
                  forceRebuild.Value <- true
                  return JsonSerializer.Serialize({| status = "ok"; forced = true |})
              }
          "build-status",
          fun _ctx state _args ->
              async {
                  let lastResult = Lifecycle.value state.LastBuild

                  match lastResult with
                  | Some(BuildPassed output) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "passed"
                                 output = truncateOutput 200 output |}
                          )
                  | Some(BuildArtifactsStale(stale, _)) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "failed"
                                 output = staleDiagnostic stale |> truncateOutput 200 |}
                          )
                  | Some(BuildOutputFailed outputs) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "failed"
                                 output = outputs |> String.concat "\n" |> truncateOutput 200 |}
                          )
                  | None -> return JsonSerializer.Serialize({| status = "not run" |})
              } ]
      Subscriptions =
        // Deliberately NOT `BatchChecked`: every source change drives a real
        // MSBuild build, so there is no test-only-skip phase to wait on the FCS
        // cohort signal for. TestPrune keeps its own subscription (AffectedTests).
        Set.ofList (
            [ SubscribeFileChanged ]
            @ (if dependsOn.IsEmpty then
                   []
               else
                   [ SubscribeCommandCompleted ])
        )
      CacheKey =
        // content-merkle key over all build-relevant files in the project graph.
        // FileChanged and Custom BuildDone share the same key so a stored result
        // is found on the next matching FileChanged. The merkle hashes EVERY source
        // file (test files included), so a test-file edit moves the key → cache miss
        // → a real build runs and re-emits the test DLL before it's executed.
        let inputsHasher = lazy BuildInputsHasher(graph)

        let merkleKey () =
            Some(computeBuildCacheKey buildCommand buildArgs dependsOn (inputsHasher.Value.Compute()))

        let cacheKey (event: PluginEvent<BuildMsg>) : ContentHash option =
            // While `forceRebuild` is set the LOOKUP must miss so a real build runs.
            // `None` is the framework's documented "outputs missing" bypass — skip
            // the cache, run Update.
            //
            // Deliberately asymmetric. Only the lookup (a `FileChanged`) is
            // suppressed; `Custom BuildDone` still computes a real key, so the fresh
            // build's result IS stored and the very next run is cacheable again.
            // Suppressing both would make every forced build permanently uncached.
            match event with
            | FileChanged _ when forceRebuild.Value -> None

            // Re-verify the ARTIFACTS at cache-replay time, not only after a real
            // build (AUTOMATION-245).
            //
            // Two correct answers, deadlocked. The key below is a content merkle over
            // SOURCES, so it is structurally blind to what happened to the OUTPUTS —
            // a `bin/` deleted, half-written, produced by another workspace, or a
            // source rewritten to byte-identical content with a NEW mtime all leave it
            // unmoved, and the entry replays "built N projects (cached)" without
            // looking at a single artifact. TestPrune's freshness gate then compares
            // MTIMES, correctly finds the output older than the source, and refuses to
            // run `--no-build` on stale code. Both are right; together they never move,
            // and re-running `check` reproduces it verbatim because nothing in the loop
            // is a function of the previous attempt.
            //
            // `verifyArtifactsFresh` is the arbiter because outputs are the ground
            // truth for a claim ABOUT outputs: a cache hit may not assert freshness it
            // has never confirmed. This is the SAME predicate the real-build path runs
            // (`verifyAndDemote`), so cache-hit and real-build become genuinely
            // indistinguishable downstream — and no repo can be newly condemned to
            // rebuild-every-time by it, because a repo whose artifacts fail this
            // predicate after a build was already being demoted to
            // `BuildArtifactsStale` (i.e. permanently red) today.
            //
            // Cheap on the hot path, and it reads no file CONTENTS: two stats per
            // project (canonical DLL) and per graph source (`GetMaxSourceMtime` walks
            // every source of every project), so ~2 × (projects + sources) — measured
            // at 0.46 ms for this repo's 14 projects / 135 sources against a merkle
            // that SHA-256s 3.5 MB in 30-40 ms.
            //
            // Ordered BEFORE the merkle so a bypass skips that hash entirely: the
            // WEDGE path is now cheaper than the warm path, not dearer. The warm path
            // pays both and is ~1% slower — the trade this change buys.
            | FileChanged _ ->
                match verifyArtifactsFresh () with
                | [] -> merkleKey ()
                | stale ->
                    info "build" (replayBypassDiagnostic stale)
                    None
            | _ -> merkleKey ()

        Some cacheKey
      // There is NO cold-start gate in the framework — a comment here used to claim
      // one ("replay is suppressed until this plugin completes once in-session"), and
      // nothing in `PluginFramework`/`PluginHost` implements it. The task cache is
      // file-backed (`FileTaskCache`), so it also survives `fshw stop`: the FIRST
      // dispatch of a brand-new daemon can and does replay a stored build. The gate
      // above is the only thing standing between a stored verdict and a `bin/` that
      // never earned it.
      Teardown = None }
