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
    | NotBuilt
    | BuildPassed of output: string
    | BuildOutputFailed of output: string

type BuildPhase =
    | IdlePhase of Lifecycle<Idle, BuildOutcome> * pendingFiles: FileChangeKind list
    | RunningPhase of Lifecycle<Running, BuildOutcome>

type BuildState =
    { Phase: BuildPhase
      SatisfiedDeps: Set<string> }

type BuildMsg = BuildDone of BuildOutcome

let create
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.IProjectGraphReader)
    (testProjectNames: string list)
    (buildTemplate: string option)
    (dependsOn: string list)
    (getCommitId: (unit -> string option) option)
    =
    let buildCommand = command
    let buildArgs = args
    let env = environment
    let testProjectNameSet = testProjectNames |> Set.ofList

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

    let startBuild (ctx: PluginCtx<BuildMsg>) (idle: Lifecycle<Idle, BuildOutcome>) =
        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
        let running = Lifecycle.start idle
        ctx.Log $"Running: %s{buildCommand} %s{buildArgs}"

        PluginCtxHelpers.withSubtask
            ctx
            "build"
            "dotnet build"
            (async {
                try
                    let (success, output) = runProcess buildCommand buildArgs ctx.RepoRoot env
                    let parsed = BuildDiagnostics.parseMSBuildDiagnostics output

                    if success then
                        let n = countBuiltProjects output

                        let summary = if n > 0 then $"built {n} projects" else "build succeeded"

                        ctx.Log summary
                        ctx.CompleteWithSummary summary

                        if parsed.IsEmpty then
                            ctx.ClearErrors "<build>"
                        else
                            ctx.ReportErrors "<build>" parsed

                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.Post(BuildDone(BuildPassed output))
                    else
                        ctx.Log "Build FAILED"
                        error "build" "Build FAILED"

                        let entries =
                            if parsed.IsEmpty then
                                [ ErrorEntry.error output ]
                            else
                                parsed

                        ctx.ReportErrors "<build>" entries
                        ctx.EmitBuildCompleted(BuildFailed [ output ])
                        ctx.Post(BuildDone(BuildOutputFailed output))
                with ex ->
                    ctx.ReportErrors "<build>" [ ErrorEntry.error ex.Message ]

                    ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                    ctx.Post(BuildDone(BuildOutputFailed ex.Message))
            })
        |> Async.Start

        { Phase = RunningPhase running
          SatisfiedDeps = Set.empty }

    let startTemplateBuild
        (ctx: PluginCtx<BuildMsg>)
        (idle: Lifecycle<Idle, BuildOutcome>)
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
                                let (success, output) = runProcess cmd cmdArgs ctx.RepoRoot env
                                outputs <- output :: outputs

                                if not success then
                                    ctx.Log $"Template build FAILED for %s{rootStr}"
                                    error "build" $"Template build FAILED for %s{rootStr}"
                                    failures <- output :: failures
                            with ex ->
                                ctx.Log $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                error "build" $"Template build exception for %s{rootStr}: %s{ex.Message}"
                                failures <- ex.Message :: failures

                        let combinedOutput = outputs |> List.rev |> String.concat "\n"

                        if failures.IsEmpty then
                            let n = countBuiltProjects combinedOutput

                            let summary = if n > 0 then $"built {n} projects" else "build succeeded"

                            ctx.Log summary
                            ctx.CompleteWithSummary summary
                            let parsed = BuildDiagnostics.parseMSBuildDiagnostics combinedOutput

                            if parsed.IsEmpty then
                                ctx.ClearErrors "<build>"
                            else
                                ctx.ReportErrors "<build>" parsed

                            ctx.EmitBuildCompleted(BuildSucceeded)
                            ctx.Post(BuildDone(BuildPassed combinedOutput))
                        else
                            let errors = failures |> List.rev

                            let parsed = BuildDiagnostics.parseMSBuildDiagnostics (errors |> String.concat "\n")

                            let entries =
                                if parsed.IsEmpty then
                                    errors |> List.map ErrorEntry.error
                                else
                                    parsed

                            ctx.ReportErrors "<build>" entries
                            ctx.EmitBuildCompleted(BuildFailed errors)
                            ctx.Post(BuildDone(BuildOutputFailed(errors |> String.concat "\n")))
                    with ex ->
                        error "build" $"Unexpected error: %s{ex.Message}"
                        ctx.Post(BuildDone(BuildOutputFailed ex.Message))
                })
            |> Async.Start

            { Phase = RunningPhase running
              SatisfiedDeps = Set.empty }

    let handleSourceChanged
        (ctx: PluginCtx<BuildMsg>)
        (state: BuildState)
        (idle: Lifecycle<Idle, BuildOutcome>)
        (files: string list)
        =
        let allTestFiles = not files.IsEmpty && files |> List.forall isTestFile

        if allTestFiles then
            info "build" "Skipping build — only test files changed"
            ctx.EmitBuildCompleted(BuildSucceeded)
            ctx.ReportStatus(Completed(DateTime.UtcNow))

            { state with
                Phase = IdlePhase(idle, []) }
        else
            match buildTemplate with
            | Some template ->
                { (startTemplateBuild ctx idle template files) with
                    SatisfiedDeps = state.SatisfiedDeps }
            | None ->
                { (startBuild ctx idle) with
                    SatisfiedDeps = state.SatisfiedDeps }

    let handleProjectChanged (ctx: PluginCtx<BuildMsg>) (state: BuildState) (idle: Lifecycle<Idle, BuildOutcome>) =
        { (startBuild ctx idle) with
            SatisfiedDeps = state.SatisfiedDeps }

    { Name = PluginName.create "build"
      Init =
        { Phase = IdlePhase(Lifecycle.create NotBuilt, [])
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
                                | RunningPhase _ -> []

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
                    let idle = Lifecycle.complete outcome running

                    match outcome with
                    | BuildPassed _ -> ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | BuildOutputFailed output ->
                        let summary = truncateOutput 5 output
                        ctx.ReportStatus(PluginStatus.Failed($"Build failed: %s{summary}", DateTime.UtcNow))
                    | NotBuilt -> ()

                    return
                        { state with
                            Phase = IdlePhase(idle, [])
                            SatisfiedDeps = Set.empty }
                | FileChanged _, RunningPhase _ ->
                    info "build" "Skipping: build already in progress"
                    return state
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

                  match lastResult with
                  | BuildPassed output ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "passed"
                                 output = truncateOutput 200 output |}
                          )
                  | BuildOutputFailed output ->
                      return
                          JsonSerializer.Serialize(
                              {| status = "failed"
                                 output = truncateOutput 200 output |}
                          )
                  | NotBuilt -> return JsonSerializer.Serialize({| status = "not run" |})
              } ]
      Subscriptions =
        Set.ofList (
            [ SubscribeFileChanged ]
            @ (if dependsOn.IsEmpty then
                   []
               else
                   [ SubscribeCommandCompleted ])
        )
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId
      Teardown = None }
