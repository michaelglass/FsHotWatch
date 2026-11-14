/// AUTOMATION-1577 at a daemon's start (ADR-074 decision 6; ADR-059 §2, a miss means run): a
/// declared-input plugin with nothing on record for its inputs as they are now runs exactly once,
/// with no file changed; one with a record does not run; with the shadow off, nothing runs.
///
/// A real daemon over a real checkout, its plugins registered as `fshw start` registers them,
/// and the start step both starts call (`ShadowVerdict.rerunUnrecordedDeclaredIn`). The file
/// command appends a line to a counter file each time it runs, so a run is counted, not inferred.
module FsHotWatch.Tests.ShadowStartRerunTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private fsx = "file-cmd-*.fsx"

/// A checkout whose one plugin is a `*.fsx` file command that counts its runs in `runs.txt`.
let private checkout (repo: string) =
    let script = Path.Combine(repo, "count.sh")
    let runs = Path.Combine(repo, "runs.txt")
    File.WriteAllText(script, $"#!/bin/sh\necho run >> '%s{runs}'\n")
    File.WriteAllText(Path.Combine(repo, "build.fsx"), "printfn \"build\"")
    Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore

    File.WriteAllText(
        Path.Combine(repo, ".fshw.json"),
        $"""{{ "format": "off", "lint": false, "build": false,
               "fileCommands": [ {{ "pattern": "*.fsx", "command": "sh", "args": "%s{script}" }} ] }}"""
    )

    runs

/// A daemon over `repo` with the plugins its configuration registers.
let private started (repo: string) =
    let daemon =
        FsHotWatch.Daemon.Daemon.createWith (Unchecked.defaultof<_>) repo oneShotDaemonOptions

    DaemonConfig.registerPlugins daemon repo (DaemonConfig.loadConfig repo)
    daemon

let private runCount (runs: string) =
    if File.Exists runs then
        File.ReadAllLines(runs).Length
    else
        0

/// Every plugin drained, and the file command out of Running: whatever a start launched has ended.
let private settled (daemon: FsHotWatch.Daemon.Daemon) =
    waitForSettled daemon.Host fsx 30_000
    waitForQuiescent daemon.Host 30_000

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a declared-input plugin with no record runs exactly once at start, with no file changed`` () =
    withTempDir "shadow-start-rerun" (fun repo ->
        let runs = checkout repo
        let daemon = started repo

        test <@ rerunUnrecordedDeclaredIn true repo daemon = [ fsx ] @>
        waitUntil (fun () -> runCount runs >= 1) 30_000
        settled daemon

        test <@ runCount runs = 1 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a declared-input plugin with a record for its inputs does not run at start`` () =
    withTempDir "shadow-start-recorded" (fun repo ->
        let runs = checkout repo

        // The record a publication files after the command passed on these inputs, by this binary.
        let producer =
            DaemonIdentity.BinaryIdentity.render (DaemonIdentity.currentIdentity ())

        match configuredOf repo with
        | Error reason -> failwith reason
        | Ok configured ->
            let publication: Observation =
                { Command = Verdict.Check
                  Tree = "sha256:t0"
                  Producer = producer
                  Worktree = repo
                  Today = Class.Green
                  TodayOutcome = "green"
                  ExitCode = 0
                  Plugins = [ fsx, Some OutcomeStore.Outcome.Pass ]
                  DiagnosticsFailed = false
                  GradedRunId = None
                  BaselineRunId = None
                  Scope = GradedScope.Unscoped
                  Suites = []
                  RunOrder = []
                  RecordedAt = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)
                  OutcomeKeys = Map.empty }

            observeWith repo (storeAt repo) None configured publication |> ignore

        let daemon = started repo
        test <@ rerunUnrecordedDeclaredIn true repo daemon = [] @>
        settled daemon
        test <@ runCount runs = 0 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``with the shadow off nothing runs at start`` () =
    withTempDir "shadow-start-off" (fun repo ->
        let runs = checkout repo
        let daemon = started repo
        test <@ rerunUnrecordedDeclaredIn false repo daemon = [] @>
        settled daemon
        test <@ runCount runs = 0 @>
        // `FSHW_OUTCOME_SHADOW=0` is what turns it off at a real start (`startDeclaredInputs`).
        test <@ not (enabledBy "0") @>)
