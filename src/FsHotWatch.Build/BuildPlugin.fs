module FsHotWatch.Build.BuildPlugin

open System
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ErrorLedger
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.Lifecycle
open FsHotWatch.AgentHost

type private BuildPhase =
    | IdlePhase of Lifecycle<Idle, (bool * string) option>
    | RunningPhase of Lifecycle<Running, (bool * string) option>

type private BuildState = { Phase: BuildPhase }

type private BuildMsg =
    | FileChanged of FileChangeKind
    | BuildDone of (bool * string) option

/// Runs a build command when source files change and emits BuildCompleted events.
/// Debounces rapid file changes — waits for 2s of quiet before building.
type BuildPlugin(?command: string, ?args: string) =
    let buildCommand = defaultArg command "dotnet"
    let buildArgs = defaultArg args "build --no-restore"

    interface IFsHotWatchPlugin with
        /// Returns "build".
        member _.Name = "build"

        /// Subscribe to file changes and run builds; registers the "build-status" command.
        member _.Initialize(ctx) =
            // doBuild runs outside the agent, posts BuildDone when complete
            let doBuild (agent: Agent<BuildState, BuildMsg>) =
                try
                    Logging.info "build" $"Running: %s{buildCommand} %s{buildArgs}"
                    ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                    try
                        let (success, output) = runProcess buildCommand buildArgs ctx.RepoRoot []
                        let result = Some(success, output)
                        agent.Post(BuildDone result)

                        if success then
                            Logging.info "build" "Build succeeded"
                        else
                            Logging.error "build" "Build FAILED"

                        if success then
                            ctx.ClearErrors "<build>"
                            ctx.EmitBuildCompleted(BuildSucceeded)
                            ctx.ReportStatus(Completed(box result, DateTime.UtcNow))
                        else
                            ctx.ReportErrors
                                "<build>"
                                [ { Message = output
                                    Severity = DiagnosticSeverity.Error
                                    Line = 0
                                    Column = 0 } ]

                            ctx.EmitBuildCompleted(BuildFailed [ output ])
                            let lines = output.Split('\n')

                            let summary = lines |> Array.skip (max 0 (lines.Length - 5)) |> String.concat "\n"

                            ctx.ReportStatus(Failed($"Build failed: %s{summary}", DateTime.UtcNow))
                    with ex ->
                        let result = Some(false, ex.Message)
                        agent.Post(BuildDone result)

                        ctx.ReportErrors
                            "<build>"
                            [ { Message = ex.Message
                                Severity = DiagnosticSeverity.Error
                                Line = 0
                                Column = 0 } ]

                        ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                with ex ->
                    Logging.error "build" $"Unexpected error: %s{ex.Message}"
                    agent.Post(BuildDone(Some(false, ex.Message)))

            let agentRef = ref Unchecked.defaultof<Agent<BuildState, BuildMsg>>

            let agent =
                createAgent<BuildState, BuildMsg> "build" { Phase = IdlePhase(Lifecycle.create None) } (fun state msg ->
                    async {
                        match msg, state.Phase with
                        | FileChanged SolutionChanged, _ -> return state
                        | FileChanged _, RunningPhase _ ->
                            Logging.info "build" "Skipping: build already in progress"
                            return state
                        | FileChanged _, IdlePhase idle ->
                            let running = Lifecycle.start idle
                            let newState = { Phase = RunningPhase running }
                            // Run the build synchronously within the handler so the agent
                            // stays in RunningPhase while the build executes, causing
                            // subsequent FileChanged messages to be skipped.
                            doBuild agentRef.Value
                            return newState
                        | BuildDone result, RunningPhase running ->
                            let completed = Lifecycle.complete result running
                            return { Phase = IdlePhase completed }
                        | BuildDone _, IdlePhase _ -> return state
                    })

            agentRef.Value <- agent

            ctx.OnFileChanged.Add(fun change ->
                match change with
                | SourceChanged _
                | ProjectChanged _ ->
                    agent.Post(FileChanged change)
                    agent.GetState() |> Async.RunSynchronously |> ignore
                | SolutionChanged -> ())

            ctx.RegisterCommand(
                "build-status",
                fun _args ->
                    async {
                        let! state = agent.GetState()

                        let lastResult =
                            match state.Phase with
                            | IdlePhase idle -> Lifecycle.value idle
                            | RunningPhase running -> Lifecycle.value running

                        match lastResult with
                        | Some(ok, output) ->
                            let status = if ok then "passed" else "failed"
                            let lines = output.Split('\n')

                            let truncated =
                                lines |> Array.skip (max 0 (lines.Length - 200)) |> String.concat "\n"

                            let escapedOutput = JsonSerializer.Serialize(truncated)
                            return $"{{\"status\": \"%s{status}\", \"output\": %s{escapedOutput}}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        /// No resources to dispose.
        member _.Dispose() = ()
