/// A `--run-once` gate never runs BESIDE a live daemon on the same workspace.
///
/// `check --run-once` and `confirm --run-once` build their own in-process host over the
/// worktree. A daemon left running there (by an earlier `fshw check`) watches, builds and
/// tests the same tree at the same time, over the same `.fshw/` state and the same
/// `bin/`/`obj/` outputs. The gate's verdict then depends on what that daemon happens to
/// be doing: `mise run ci` went red 3/3 in a workspace holding a leftover daemon and green
/// on the same tree where none was left (AUTOMATION-1657). So the gate refuses before it
/// starts anything, and names the daemon and the command that stops it.
///
/// The daemon is found by `.fshw/daemon.pid`, read through the same liveness probe the
/// stale-pidfile hygiene uses, so a pid file whose process is provably gone never refuses.
module FsHotWatch.Cli.RunOnceBesideDaemon

open FsHotWatch

/// What a `--run-once` gate found in the worktree before starting.
type Neighbour =
    /// No `daemon.pid`, an unreadable one, or one naming a process that is gone.
    | NoLiveDaemon
    /// `daemon.pid` names a process that is (or cannot be shown not to be) running.
    | LiveDaemon of pid: int

/// The exit code of the refusal: 2, "nothing was verified, could not complete" — the same
/// code `beforeRun` refusals and `stop`'s unusable pid file use. Nothing failed (not 1)
/// and no run was attempted that could have earned a verdict (not 3).
[<Literal>]
let RefusedExitCode = 2

/// The daemon registered for `repoRoot`, judged live by `processAlive`.
let inspect (processAlive: int -> bool) (repoRoot: string) : Neighbour =
    match PidFile.tryReadDaemon repoRoot with
    | Some pid when processAlive pid -> LiveDaemon pid
    | Some _
    | None -> NoLiveDaemon

/// The refusal, one line per element, naming the pid and how to stop it.
let refusalLines (verb: string) (pid: int) : string list =
    [ $"fshw %s{verb} --run-once refused: a daemon (pid %d{pid}) is running on this workspace."
      "  A run-once gate builds and tests the same tree in-process; beside a live daemon its verdict"
      "  would depend on what that daemon is doing. Nothing was run and nothing is claimed."
      $"  Stop it with `fshw stop` (or `kill %d{pid}`), then run the gate again." ]

/// `Some RefusedExitCode` after printing the refusal to stderr when a live daemon is
/// registered for `repoRoot`; `None` when the gate may run.
let refuseBesideLiveDaemon (processAlive: int -> bool) (verb: string) (repoRoot: string) : int option =
    match inspect processAlive repoRoot with
    | NoLiveDaemon -> None
    | LiveDaemon pid ->
        refusalLines verb pid |> List.iter (eprintfn "%s")
        Some RefusedExitCode
