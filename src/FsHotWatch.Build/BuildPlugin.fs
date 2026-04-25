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

type BuildOutcome =
    | BuildPassed of output: string
    | BuildOutputFailed of outputs: string list

type BuildPhase =
    | IdlePhase of Lifecycle<Idle, BuildOutcome option> * pendingFiles: FileChangeKind list
    | RunningPhase of Lifecycle<Running, BuildOutcome option>
    /// Test-files-only change — build is logically a no-op but we wait for FCS to finish
    /// checking the changed files before emitting BuildSucceeded. Otherwise downstream
    /// test-prune dispatch would race FCS and read stale AffectedTests.
    | WaitingForFcsPhase of awaiting: Set<string> * Lifecycle<Idle, BuildOutcome option>

type BuildState =
    { Phase: BuildPhase
      SatisfiedDeps: Set<string> }

type BuildMsg = BuildDone of BuildOutcome

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

let create
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (testProjectNames: string list)
    (buildTemplate: string option)
    (dependsOn: string list)
    (getCommitId: (unit -> string option) option)
    (timeoutSec: int option)
    =
    let buildCommand = command
    let buildArgs = args

    let testProjectNameSet = testProjectNames |> Set.ofList

    let buildTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    // Normalize file paths to match what FCS emits in FileCheckResult.File so
    // the WaitingForFcsPhase set drains reliably. Watcher events and FCS go
    // through different pipelines and either may produce non-canonical forms.
    let normalizePath (file: string) = Path.GetFullPath(file)

    let isTestFile (file: string) =
        graph.GetProjectsForFile(AbsFilePath.create file)
        |> List.exists (fun proj ->
            testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj)))

    let isTestProject (proj: AbsProjectPath) =
        testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(AbsProjectPath.value proj))

    let depNames = dependsOn |> Set.ofList
    let allDepsSatisfied deps = Set.isSubset depNames deps

    let countBuiltProjects (output: string) =
        output.Split('\n')
        |> Array.filter (fun line -> line.Contains(" -> ") && not (line.Contains("error")))
        |> Array.length

    let applyBuildOutcome (ctx: PluginCtx<BuildMsg>) (outcome: BuildOutcome) (entries: ErrorEntry list) =
        match outcome with
        | BuildPassed out ->
            let n = countBuiltProjects out
            let summary = if n > 0 then $"built {n} projects" else "build succeeded"
            ctx.Log summary
            ctx.CompleteWithSummary summary

            if entries.IsEmpty then
                ctx.ClearErrors "<build>"
            else
                ctx.ReportErrors "<build>" entries

            ctx.EmitBuildCompleted(BuildSucceeded)
        | BuildOutputFailed outputs ->
            ctx.ReportErrors "<build>" entries

            let errCount =
                entries
                |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Error)
                |> List.length

            ctx.CompleteWithSummary $"build failed: %d{errCount} errors"
            ctx.EmitBuildCompleted(BuildFailed outputs)

        ctx.Post(BuildDone outcome)

    let startBuild (ctx: PluginCtx<BuildMsg>) (idle: Lifecycle<Idle, BuildOutcome option>) =
        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
        let running = Lifecycle.start idle
        ctx.Log $"Running: %s{buildCommand} %s{buildArgs}"

        PluginCtxHelpers.withSubtask
            ctx
            "build"
            "dotnet build"
            (async {
                try
                    let result =
                        runProcessWithTimeout buildCommand buildArgs ctx.RepoRoot environment buildTimeout

                    let (outcome, entries) = decideBuildOutcome (isSucceeded result) (outputOf result)

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

                    applyBuildOutcome ctx outcome entries
                with ex ->
                    ctx.ReportErrors "<build>" [ ErrorEntry.error ex.Message ]

                    ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                    ctx.Post(BuildDone(BuildOutputFailed [ ex.Message ]))
            })
        |> Async.Start

        { Phase = RunningPhase running
          SatisfiedDeps = Set.empty }

    let startTemplateBuild
        (ctx: PluginCtx<BuildMsg>)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (template: string)
        (files: string list)
        =
        let nonTestFiles = files |> List.filter (fun f -> not (isTestFile f))

        let affected =
            graph.GetAffectedProjects(nonTestFiles |> List.map AbsFilePath.create)

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

            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
            let running = Lifecycle.start idle

            PluginCtxHelpers.withSubtask
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
                                let result = runProcessWithTimeout cmd cmdArgs ctx.RepoRoot environment buildTimeout
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

                        let (outcome, entries) =
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

                        applyBuildOutcome ctx outcome entries
                    with ex ->
                        error "build" $"Unexpected error: %s{ex.Message}"
                        ctx.Post(BuildDone(BuildOutputFailed [ ex.Message ]))
                })
            |> Async.Start

            { Phase = RunningPhase running
              SatisfiedDeps = Set.empty }

    let handleSourceChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome option>)
        (files: string list)
        =
        let allTestFiles = not files.IsEmpty && files |> List.forall isTestFile

        if allTestFiles then
            info "build" "Skipping build — only test files changed; waiting for FCS to confirm"
            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

            { state with
                Phase = WaitingForFcsPhase(files |> List.map normalizePath |> Set.ofList, idle) }
        else
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
        { Phase = IdlePhase(Lifecycle.create None, [])
          SatisfiedDeps = Set.empty }
      Update =
        fun ctx state event ->
            async {
                match event, state.Phase with
                // --- CommandCompleted: track dependency satisfaction ---
                | CommandCompleted result, _ when depNames.Contains(result.Name) ->
                    match result.Outcome with
                    | FsHotWatch.Events.CommandFailed _ ->
                        ctx.ReportStatus(PluginStatus.Failed($"dependency failed: %s{result.Name}", DateTime.UtcNow))
                        return state
                    | FsHotWatch.Events.CommandSucceeded _ ->
                        let newDeps = Set.add result.Name state.SatisfiedDeps

                        if allDepsSatisfied newDeps then
                            let pendingFiles =
                                match state.Phase with
                                | IdlePhase(_, pending) -> pending
                                | RunningPhase _
                                | WaitingForFcsPhase _ -> []

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
                                |> List.distinct

                            match hasProjectChange, sourceFiles, updatedState.Phase with
                            | true, _, IdlePhase(idle, _) -> return handleProjectChanged ctx updatedState idle
                            | _, _ :: _, IdlePhase(idle, _) ->
                                return handleSourceChanged ctx updatedState idle sourceFiles
                            | _ -> return updatedState
                        else
                            return { state with SatisfiedDeps = newDeps }

                // --- FileChanged: buffer if deps not yet satisfied ---
                | FileChanged change, IdlePhase(idle, pending) when
                    not depNames.IsEmpty && not (allDepsSatisfied state.SatisfiedDeps)
                    ->
                    info "build" "Buffering file change — waiting for dependencies"

                    return
                        { state with
                            Phase = IdlePhase(idle, pending @ [ change ]) }

                // --- FileChanged: normal handling (no deps or all satisfied) ---
                | FileChanged(SourceChanged files), IdlePhase(idle, _) ->
                    return handleSourceChanged ctx state idle files
                | FileChanged(ProjectChanged _), IdlePhase(idle, _) -> return handleProjectChanged ctx state idle
                | Custom(BuildDone outcome), RunningPhase running ->
                    let idle = Lifecycle.complete (Some outcome) running

                    match outcome with
                    | BuildPassed _ -> ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | BuildOutputFailed outputs ->
                        let summary = outputs |> String.concat "\n" |> truncateOutput 5
                        ctx.ReportStatus(PluginStatus.Failed($"Build failed: %s{summary}", DateTime.UtcNow))

                    return
                        { state with
                            Phase = IdlePhase(idle, [])
                            SatisfiedDeps = Set.empty }
                | FileChanged _, RunningPhase _ ->
                    info "build" "Skipping: build already in progress"
                    return state

                | FileChecked result, WaitingForFcsPhase(awaiting, idle) ->
                    let remaining = Set.remove (normalizePath result.File) awaiting

                    if remaining.IsEmpty then
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))

                        return
                            { state with
                                Phase = IdlePhase(idle, []) }
                    else
                        return
                            { state with
                                Phase = WaitingForFcsPhase(remaining, idle) }

                | FileChanged(ProjectChanged _), WaitingForFcsPhase(_, idle) ->
                    return handleProjectChanged ctx state idle
                | FileChanged(SourceChanged files), WaitingForFcsPhase(awaiting, idle) ->
                    let nonTest = files |> List.filter (fun f -> not (isTestFile f))

                    if nonTest.IsEmpty then
                        let merged = files |> List.fold (fun s f -> Set.add (normalizePath f) s) awaiting

                        return
                            { state with
                                Phase = WaitingForFcsPhase(merged, idle) }
                    else
                        return handleSourceChanged ctx state idle files
                | _ -> return state
            }
      Commands =
        [ "build-status",
          fun state _args ->
              async {
                  let lastResult =
                      match state.Phase with
                      | IdlePhase(idle, _) -> Lifecycle.value idle
                      | RunningPhase running -> Lifecycle.value running
                      | WaitingForFcsPhase(_, idle) -> Lifecycle.value idle

                  match lastResult with
                  | Some(BuildPassed output) ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "passed"
                                 output = truncateOutput 200 output |}
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
        Set.ofList (
            [ SubscribeFileChanged; SubscribeFileChecked ]
            @ (if dependsOn.IsEmpty then
                   []
               else
                   [ SubscribeCommandCompleted ])
        )
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId
      Teardown = None }
