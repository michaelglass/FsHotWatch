/// Progress while `check` and `confirm` wait for the daemon to settle.
///
/// The settle is one blocking `WaitForComplete`, and on a loaded box it holds the gate for
/// hours: builds, a forced scan, test runs that starve, re-run alone and are queued again.
/// A terminal redraws the plugin statuses while it waits; a non-interactive reader (a land
/// log, CI, an agent) saw nothing at all. Its last line was the phase label printed before
/// the scan wait, so a gate that spent its whole timeout in test runs read "Scanning..."
/// from start to kill (AUTOMATION-1370). This module names what the wait is on, at a fixed
/// interval, from the same status reading the terminal renders.
module FsHotWatch.Cli.SettleHeartbeat

open System
open FsHotWatch.Cli.RunOnceOutput

/// How often a non-interactive settle is reported. Twice a minute keeps a two-hour wait to
/// a few hundred lines, and no reader goes longer than this without hearing from it.
let interval = TimeSpan.FromSeconds 30.0

/// The line announcing the settle, printed when the scan wait hands over to it.
let settlingLabel = "Waiting for the daemon to settle (build, checks, tests)..."

/// What a settle that has waited `waited` is on, from one status reading taken at `now`.
///
/// Each Running plugin is named with its elapsed time and its subtasks, in the form the
/// daemon's own `[wait]` log lines use (`Daemon.formatPluginWait`). With nothing Running the
/// wait is on work the daemon still owns or on test evidence it still owes, which the status
/// map cannot name — so the line says that, and where the daemon names it, rather than
/// printing a reassuring nothing.
let progressLine (now: DateTime) (waited: TimeSpan) (statuses: Map<string, ParsedPluginStatus>) : string =
    let running =
        statuses
        |> Map.toList
        |> List.choose (fun (name, s) ->
            match s.Status with
            | StatusView.Running since ->
                let subtasks = s.Subtasks |> List.map (fun t -> t.Key, t.StartedAt)
                Some(FsHotWatch.Daemon.formatPluginWait now name since subtasks)
            | _ -> None)

    let elapsed = FsHotWatch.Daemon.formatElapsed waited

    match running with
    | [] ->
        $"settling %s{elapsed}: no plugin is running — the daemon still owns work or owes test evidence for the current project model (its `[wait]` log lines name which)"
    | plugins -> $"""settling %s{elapsed}: still running: %s{String.concat ", " plugins}"""

/// When the settle began, and when it was last reported: a report is due one interval after
/// the last one (or after the start). Driven by the caller's clock, never read here.
type Ticker =
    { Started: DateTime
      LastReport: DateTime }

module Ticker =
    let start (now: DateTime) : Ticker = { Started = now; LastReport = now }

    /// The report due at `now`, if any, and the ticker to carry forward.
    let tick
        (every: TimeSpan)
        (now: DateTime)
        (statuses: Map<string, ParsedPluginStatus>)
        (ticker: Ticker)
        : string option * Ticker =
        if now - ticker.LastReport >= every then
            Some(progressLine now (now - ticker.Started) statuses), { ticker with LastReport = now }
        else
            None, ticker

/// Write one progress line to stderr — the stderr of the moment it prints, so agent mode's
/// transcript (`AgentEnvelope.capture`) receives it.
let emitLine (line: string) : unit = Console.Error.Write($"  %s{line}\n")
