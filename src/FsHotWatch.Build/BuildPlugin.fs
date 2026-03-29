module FsHotWatch.Build.BuildPlugin

open System
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

let create (command: string) (args: string) (environment: (string * string) list) =
    let buildCommand = command
    let buildArgs = args
    let env = environment

    { Name = "build"
      Init = { Phase = IdlePhase(Lifecycle.create NotBuilt) }
      Update =
        fun ctx state event ->
            async {
                match event, state.Phase with
                | FileChanged(SourceChanged _ | ProjectChanged _), IdlePhase idle ->
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

                                    ctx.ReportErrors
                                        "<build>"
                                        [ { Message = output
                                            Severity = DiagnosticSeverity.Error
                                            Line = 0
                                            Column = 0 } ]

                                    ctx.EmitBuildCompleted(BuildFailed [ output ])
                                    ctx.Post(BuildDone(BuildOutputFailed output))
                            with ex ->
                                ctx.ReportErrors
                                    "<build>"
                                    [ { Message = ex.Message
                                        Severity = DiagnosticSeverity.Error
                                        Line = 0
                                        Column = 0 } ]

                                ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                                ctx.Post(BuildDone(BuildOutputFailed ex.Message))
                        with ex ->
                            error "build" $"Unexpected error: %s{ex.Message}"
                            ctx.Post(BuildDone(BuildOutputFailed ex.Message))
                    }
                    |> Async.Start

                    return { Phase = RunningPhase running }
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
            FileChanged = true } }
