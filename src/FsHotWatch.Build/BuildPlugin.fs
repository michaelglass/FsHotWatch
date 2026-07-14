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
/// Every source change — test files included — drives a real build: there is
/// no longer a "skip the build for test-only changes" phase, because that
/// skip left a stale on-disk test DLL for `dotnet run --no-build` to execute
/// (see ADR-012). `LastBuild` carries the most recent build's lifecycle.
type BuildState =
    { LastBuild: Lifecycle<Idle, BuildOutcome option>
      PendingFiles: FileChangeKind list
      SatisfiedDeps: Set<string> }

/// Internal message posted from the async build runner back to the plugin's
/// own mailbox. Carries the outcome AND the parsed diagnostic entries so the
/// synchronous Custom handler can apply them to the error ledger and emit
/// BuildCompleted within the framework's per-event capture window — required
/// for the §2a cache to record errors and downstream emissions on terminal
/// status.
type BuildMsg = BuildDone of outcome: BuildOutcome * entries: ErrorEntry list * elapsed: TimeSpan * summary: string

/// Pure decision logic: given a subprocess's success flag and combined output,
/// determine the BuildOutcome and the list of ErrorEntry diagnostics to surface.
/// On failure with no parsed MSBuild diagnostics, the raw output is wrapped as
/// a single error entry so callers always have something to report.
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

/// §2a: stable merkle key for the build cache, independent of the cold-start
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

/// §2a: build all-inputs merkle. Hashes the on-disk CONTENT of every source
/// file the project graph knows about, plus every .fsproj.
///
/// Bug 1 (mtime-preserved content rewrite): a previous implementation memoized
/// the content hash under `(path, mtimeTicks)`. `rsync -a`/`cp -p`/`tar -x`/a
/// git checkout that restores an old mtime all change CONTENT while PRESERVING
/// mtime, so the `(path, mtime)` key was unchanged → the memo returned the
/// STALE content hash → the build merkle never moved → the build-plugin task
/// cache replayed a stale `BuildDone` (an FS1178 phantom) forever. mtime is NOT
/// a safe proxy for content equality, so the memo is gone: every input is
/// content-hashed each Compute.
///
/// Perf tradeoff: each Compute reads+hashes every input rather than skipping
/// unchanged (path, mtime) tuples. Compute runs once per build trigger (not per
/// file event) over `graph.GetAllFiles()`; for the repo sizes fshw targets the
/// SHA-256 over source text is dominated by the actual `dotnet build` it gates.
/// Correctness (never serving a stale verdict) outranks the micro-optimization
/// the memo bought — a memo keyed on (path, size, mtime) would still be fooled
/// by a same-size, mtime-preserved rewrite, which is exactly the class of bug
/// this fixes.
type internal BuildInputsHasher(graph: FsHotWatch.ProjectGraph.IProjectGraphReader) =
    // Honest "missing" sentinel for non-existent files; let real IO exceptions
    // (UnauthorizedAccessException, IOException for locked files, etc.) propagate
    // up to decideBuildOutcome instead of folding "read-error" into the merkle.
    let hashFile (path: string) : string option =
        if not (File.Exists path) then
            None
        else
            // Content hash, every time — mtime is never trusted to prove
            // content equality (let real IO exns propagate).
            Some(FsHotWatch.CheckCache.sha256Hex (File.ReadAllText path))

    member _.Compute() : string =
        let sourceFiles = graph.GetAllFiles() |> List.map AbsFilePath.value

        let projectFiles = graph.GetAllProjects() |> List.map AbsProjectPath.value

        let allInputs = (sourceFiles @ projectFiles) |> List.sort
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

    let testProjectNameSet = testProjectNames |> Set.ofList

    let buildTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    // A build command is a SILENT child: `dotnet build -v q` prints nothing until
    // it finishes, and a `sh -c "dotnet build 2> log; cat log"` wrapper (the shape
    // real repos use) buffers everything to the very end. So its output proves
    // nothing about liveness and a launch deadline would false-kill a healthy slow
    // build — `buildTimeout` is the bound. It still gets the polled-exit and
    // bounded post-exit drain that every spawn gets, which is what closes the
    // machine-sleep and grandchild-pipe wedges here.
    let buildBounds = ProcessBounds.silent buildTimeout

    // Path normalization happens once at the SourceChanged → AbsFilePath boundary
    // (callers inject `AbsFilePath.create` per file). `isTestFile` drives the
    // template-build path's non-test-project filter (`startTemplateBuild`).
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
    /// mtime IS the right signal HERE (unlike the build-input merkle — Bug 1 —
    /// and the deps-freshness signature, which are content-hashed because mtime
    /// lied to them under preserved-mtime rewrites). This guard answers a
    /// strictly temporal question: "was the DLL regenerated *after* the newest
    /// source?" — i.e. did MSBuild's incremental cache lie and skip relinking an
    /// artifact a real edit should have rebuilt. In that failure mode the edit
    /// bumped the source mtime, so DLL < source is exactly the tell. The
    /// preserved-mtime content-rewrite class (rsync -a / git checkout) is NOT
    /// this guard's job: the content-hashed BuildInputsHasher already invalidates
    /// the build-cache key on a content change with preserved mtime, forcing a
    /// real rebuild whose fresh DLL then post-dates the (old-mtime) source — so
    /// this check correctly sees it as fresh. There is no "expected DLL content"
    /// to hash against, so content-hashing would not even be expressible here;
    /// the merkle is the content guard, and this is its temporal complement.
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

    /// Wrap a stale-artifact list in a "MSBuild lied" diagnostic suitable for
    /// the error ledger / BuildFailed payload.
    let staleDiagnostic (stale: StaleArtifact list) : string =
        "Build subprocess reported success but post-build verification\n"
        + "found stale artifacts (MSBuild incremental cache likely lied).\n"
        + "Re-run with `dotnet build --no-incremental` (or delete bin/ and obj/).\n\n"
        + (stale |> List.map formatStaleArtifact |> String.concat "\n")

    /// Run from the async build worker. Logs+summary happens here (live UI),
    /// but the *captured* operations (ReportErrors / ClearErrors / EmitBuildCompleted)
    /// are deferred to the synchronous Custom BuildDone handler so the framework's
    /// per-event capture window records them for the §2a cache. Returns the
    /// completion message; the framework posts it back via RunExclusive.
    let applyBuildOutcome
        (ctx: PluginCtx<BuildMsg>)
        (outcome: BuildOutcome)
        (entries: ErrorEntry list)
        (elapsed: TimeSpan)
        =
        let summary =
            match outcome with
            | BuildPassed out ->
                let n = countBuiltProjects out
                let summary = if n > 0 then $"built {n} projects" else "build succeeded"
                // No CompleteWithSummary here: the Completed status the BuildDone
                // handler reports CARRIES this summary (RunVerdict), and the host
                // routes it into the run record — one channel, no disagreement.
                ctx.Log summary
                summary
            | BuildArtifactsStale(stale, _) ->
                let summary = $"build failed: %d{stale.Length} stale artifacts"
                // Log the per-project detail (which DLL, and how far its mtime trails
                // its newest source), not just the count, so an intermittent stale-
                // artifact failure names the project and the mtime delta in the live
                // log/test output — otherwise it surfaces only as un-actionable
                // "build failed: 1 stale artifacts" with no way to tell which project.
                ctx.Log(staleDiagnostic stale)
                error "build" (staleDiagnostic stale)
                ctx.CompleteWithSummary summary
                summary
            | BuildOutputFailed _ ->
                let errCount =
                    entries
                    |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Error)
                    |> List.length

                let summary = $"build failed: %d{errCount} errors"
                ctx.CompleteWithSummary summary
                summary

        BuildDone(outcome, entries, elapsed, summary)

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
        ctx.ReportStatus(PluginStatus.Running(since = buildStarted))
        ctx.Log $"Running: %s{buildCommand} %s{buildArgs}"

        // RunExclusive "build": the framework guarantees only one build runs
        // at a time. Concurrent FileChanged-while-building triggers are dropped by
        // the framework, replacing the old per-plugin RunningPhase guard.
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
                        | BuildOutputFailed _, TimedOut(after, _) ->
                            let summary = $"timed out after %d{int after.TotalSeconds}s"
                            ctx.Log "Build TIMED OUT"
                            error "build" "Build TIMED OUT"
                            ctx.CompleteWithTimeout summary
                        | BuildOutputFailed _, Failed(exitCode, output) ->
                            ctx.Log "Build FAILED"
                            error "build" "Build FAILED"

                            let parsedCount = BuildDiagnostics.parseMSBuildDiagnostics output |> List.length

                            if parsedCount = 0 then
                                let detail = formatSilentFailureDiagnostic exitCode output
                                ctx.Log detail
                                error "build" detail
                        | BuildOutputFailed _, _ ->
                            ctx.Log "Build FAILED"
                            error "build" "Build FAILED"
                        | _ -> ()

                        return applyBuildOutcome ctx outcome entries (DateTime.UtcNow - buildStarted)
                    with ex ->
                        let crashEntry = ErrorEntry.error ex.Message
                        // ReportErrors / EmitBuildCompleted moved into the synchronous
                        // BuildDone handler (see applyBuildOutcome doc) so they're captured
                        // for cache replay.
                        return
                            BuildDone(
                                BuildOutputFailed [ ex.Message ],
                                [ crashEntry ],
                                DateTime.UtcNow - buildStarted,
                                $"build crashed: %s{ex.Message}"
                            )
                }))

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
            ctx.ReportStatus(PluginStatus.Running(since = buildStarted))

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
                                    | TimedOut(after, _) ->
                                        let summary = $"timed out after %d{int after.TotalSeconds}s"
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
                                    DateTime.UtcNow - buildStarted,
                                    $"build crashed: %s{ex.Message}"
                                )
                    }))

            { LastBuild = idle
              PendingFiles = []
              SatisfiedDeps = Set.empty }

    let handleSourceChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (files: AbsFilePath list)
        =
        // A test-file-only change is NOT a build no-op: the changed test project
        // is run by test-prune via `dotnet run --no-build`, which executes the
        // on-disk assembly. Only MSBuild re-emits that assembly — FCS's in-memory
        // `BatchChecked` type-check signal does not. The previous "skip build,
        // wait for BatchChecked, emit BuildSucceeded" path therefore left a STALE
        // DLL on disk and let `--no-build` run it → false green (see ADR-012).
        // Every source change, test or not, runs the real build so `verifyArtifactsFresh`
        // confirms the DLL post-dates its sources before `BuildSucceeded` fires.
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
                        ctx.ReportStatus(PluginStatus.Failed($"dependency failed: %s{result.Name}", DateTime.UtcNow))
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
                | Custom(BuildDone(outcome, entries, elapsed, summary)) ->
                    // Build single-flight is owned by the framework (RunExclusive "build").
                    // The completion message arrives carrying the pre-build idle lifecycle;
                    // we advance the lifecycle through Running ▸ Completed for activity-log
                    // bookkeeping.
                    let prevIdle = state.LastBuild

                    let idle = Lifecycle.complete (Some outcome) (Lifecycle.start prevIdle)

                    // Apply captured operations within this synchronous handler so
                    // the framework's §2a cache-write window records them. Replay
                    // of a cached BuildDone re-fires these via EmittedEvents +
                    // captured Errors.
                    // Contract: BuildSucceeded means every project's DLL is up-to-date
                    // with its sources. The async worker already demoted BuildPassed to
                    // BuildArtifactsStale when verifyArtifactsFresh found anything
                    // wrong, so this handler just dispatches the three terminal cases.
                    match outcome with
                    | BuildPassed _ ->
                        if entries.IsEmpty then
                            ctx.ClearErrors "<build>"
                        else
                            ctx.ReportErrors "<build>" entries

                        ctx.EmitBuildCompleted(BuildSucceeded)

                        ctx.ReportStatus(Completed(DateTime.UtcNow, { Summary = summary; Elapsed = elapsed }))
                    | BuildArtifactsStale(stale, _) ->
                        let detail = staleDiagnostic stale
                        let entry = ErrorEntry.error detail
                        ctx.ReportErrors "<build>" (entry :: entries)
                        ctx.EmitBuildCompleted(BuildFailed [ detail ])
                        ctx.ReportStatus(PluginStatus.Failed("Build artifact verification failed", DateTime.UtcNow))
                    | BuildOutputFailed outputs ->
                        ctx.ReportErrors "<build>" entries
                        ctx.EmitBuildCompleted(BuildFailed outputs)
                        let summary = outputs |> String.concat "\n" |> truncateOutput 5
                        ctx.ReportStatus(PluginStatus.Failed($"Build failed: %s{summary}", DateTime.UtcNow))

                    return
                        { state with
                            LastBuild = idle
                            PendingFiles = []
                            SatisfiedDeps = Set.empty }

                | _ -> return state
            }
      Commands =
        [ "build-status",
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
        // The build plugin no longer subscribes to `BatchChecked`: every source
        // change (test files included) drives a real MSBuild build, so there is
        // no test-only-skip phase that waited on the FCS cohort signal. TestPrune
        // keeps its own `BatchChecked` subscription (the AffectedTests seal).
        Set.ofList (
            [ SubscribeFileChanged ]
            @ (if dependsOn.IsEmpty then
                   []
               else
                   [ SubscribeCommandCompleted ])
        )
      CacheKey =
        // §2a: content-merkle key over all build-relevant files in the project graph.
        // FileChanged and Custom BuildDone share the same key so a stored result
        // is found on the next matching FileChanged. The merkle hashes EVERY source
        // file (test files included), so a test-file edit moves the key → cache miss
        // → a real build runs and re-emits the test DLL before it's executed.
        let inputsHasher = lazy BuildInputsHasher(graph)

        let cacheKey (_event: PluginEvent<BuildMsg>) : ContentHash option =
            Some(computeBuildCacheKey buildCommand buildArgs dependsOn (inputsHasher.Value.Compute()))

        Some cacheKey
      // Framework gate: suppresses cache replay until this plugin completes once
      // in-session. Replacing the local hasBuiltInSessionRef flag — same semantics,
      // but framework-owned so all plugins share one cold-start contract.
      Teardown = None }
