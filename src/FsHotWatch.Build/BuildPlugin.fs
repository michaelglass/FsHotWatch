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
    | IdlePhase of Lifecycle<Idle, BuildOutcome>
    | RunningPhase of Lifecycle<Running, BuildOutcome>

type BuildState = { Phase: BuildPhase }

type BuildMsg = BuildDone of BuildOutcome

let create
    (command: string)
    (args: string)
    (environment: (string * string) list)
    (graph: FsHotWatch.ProjectGraph.ProjectGraph)
    (testProjectNames: string list)
    (buildTemplate: string option)
    =
    let buildCommand = command
    let buildArgs = args
    let env = environment
    let testProjectNameSet = testProjectNames |> Set.ofList

    let isTestFile (file: string) =
        match graph.GetProjectForFile(file) with
        | Some proj -> testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(proj))
        | None -> false

    let isTestProject (proj: string) =
        testProjectNameSet.Contains(Path.GetFileNameWithoutExtension(proj))

    let startBuild (ctx: PluginCtx<BuildMsg>) (idle: Lifecycle<Idle, BuildOutcome>) =
        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
        let running = Lifecycle.start idle
        info "build" $"Running: %s{buildCommand} %s{buildArgs}"

        async {
            try
                try
                    let (success, output) = runProcess buildCommand buildArgs ctx.RepoRoot env

                    if success then
                        info "build" "Build succeeded"
                        ctx.ClearErrors "<build>"
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.Post(BuildDone(BuildPassed output))
                    else
                        error "build" "Build FAILED"

                        ctx.ReportErrors "<build>" [ ErrorEntry.error output ]
                        ctx.EmitBuildCompleted(BuildFailed [ output ])
                        ctx.Post(BuildDone(BuildOutputFailed output))
                with ex ->
                    ctx.ReportErrors "<build>" [ ErrorEntry.error ex.Message ]

                    ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                    ctx.Post(BuildDone(BuildOutputFailed ex.Message))
            with ex ->
                error "build" $"Unexpected error: %s{ex.Message}"
                ctx.Post(BuildDone(BuildOutputFailed ex.Message))
        }
        |> Async.Start

        { Phase = RunningPhase running }

    let startTemplateBuild
        (ctx: PluginCtx<BuildMsg>)
        (idle: Lifecycle<Idle, BuildOutcome>)
        (template: string)
        (files: string list)
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

            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
            let running = Lifecycle.start idle

            async {
                try
                    let mutable failures = []
                    let mutable outputs = []

                    for root in roots do
                        let rendered = template.Replace("{project}", root)
                        let (cmd, cmdArgs) = splitCommand rendered
                        info "build" $"Running template: %s{cmd} %s{cmdArgs}"

                        try
                            let (success, output) = runProcess cmd cmdArgs ctx.RepoRoot env
                            outputs <- output :: outputs

                            if not success then
                                error "build" $"Template build FAILED for %s{root}"
                                failures <- output :: failures
                        with ex ->
                            error "build" $"Template build exception for %s{root}: %s{ex.Message}"
                            failures <- ex.Message :: failures

                    let combinedOutput = outputs |> List.rev |> String.concat "\n"

                    if failures.IsEmpty then
                        info "build" "All template builds succeeded"
                        ctx.ClearErrors "<build>"
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.Post(BuildDone(BuildPassed combinedOutput))
                    else
                        let errors = failures |> List.rev
                        ctx.ReportErrors "<build>" (errors |> List.map ErrorEntry.error)
                        ctx.EmitBuildCompleted(BuildFailed errors)
                        ctx.Post(BuildDone(BuildOutputFailed(errors |> String.concat "\n")))
                with ex ->
                    error "build" $"Unexpected error: %s{ex.Message}"
                    ctx.Post(BuildDone(BuildOutputFailed ex.Message))
            }
            |> Async.Start

            { Phase = RunningPhase running }

    { Name = "build"
      Init = { Phase = IdlePhase(Lifecycle.create NotBuilt) }
      Update =
        fun ctx state event ->
            async {
                match event, state.Phase with
                | FileChanged(SourceChanged files), IdlePhase idle ->
                    let allTestFiles = not files.IsEmpty && files |> List.forall isTestFile

                    if allTestFiles then
                        info "build" "Skipping build — only test files changed"
                        ctx.EmitBuildCompleted(BuildSucceeded)
                        ctx.ReportStatus(Completed(DateTime.UtcNow))
                        return { Phase = IdlePhase idle }
                    else
                        match buildTemplate with
                        | Some template -> return startTemplateBuild ctx idle template files
                        | None -> return startBuild ctx idle
                | FileChanged(ProjectChanged _), IdlePhase idle -> return startBuild ctx idle
                | Custom(BuildDone outcome), RunningPhase running ->
                    let idle = Lifecycle.complete outcome running

                    match outcome with
                    | BuildPassed _ -> ctx.ReportStatus(Completed(DateTime.UtcNow))
                    | BuildOutputFailed output ->
                        let summary = truncateOutput 5 output
                        ctx.ReportStatus(PluginStatus.Failed($"Build failed: %s{summary}", DateTime.UtcNow))
                    | NotBuilt -> ()

                    return { Phase = IdlePhase idle }
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
                      | IdlePhase idle -> Lifecycle.value idle
                      | RunningPhase running -> Lifecycle.value running

                  match lastResult with
                  | BuildPassed output ->
                      let escapedOutput = JsonSerializer.Serialize(truncateOutput 200 output)
                      return $"{{\"status\": \"passed\", \"output\": %s{escapedOutput}}}"
                  | BuildOutputFailed output ->
                      let escapedOutput = JsonSerializer.Serialize(truncateOutput 200 output)
                      return $"{{\"status\": \"failed\", \"output\": %s{escapedOutput}}}"
                  | NotBuilt -> return "{\"status\": \"not run\"}"
              } ]
      Subscriptions =
        { PluginSubscriptions.none with
            FileChanged = true }
      CacheKey = None }
