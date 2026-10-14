/// The marker a test project leaves in its run directory when its host was torn down at
/// the project's `timeoutSec`: `.fshw/test-runs/<runId>/<Project>.timed-out.json`.
///
/// A host killed at its timeout writes no CTRF report, and its streamed output log ends
/// wherever the kill caught it (often mid-stack-trace). A reader of the run directory —
/// a gate that reconciles reports with logs, a client printing the run — then sees a
/// project that ran and reported nothing, and can only say "no report". The marker
/// names the cause instead: the budget, when the host was spawned, and how the teardown
/// went.
///
/// Not a stub CTRF report: a report from a host that did not finish is not a result, and
/// the verdict deliberately never reads one as such (`classifyTestOutcome`).
module FsHotWatch.TestPrune.TimeoutMarker

open System
open System.IO
open System.Text.Json
open FsHotWatch
open FsHotWatch.ProcessHelper

/// The suffix the marker is named with, beside `<Project>.ctrf.json` and
/// `<Project>.output.log`.
[<Literal>]
let Suffix = ".timed-out.json"

/// Where a project's marker lives inside a run's directory.
let pathIn (runDir: string) (project: string) : string = Path.Combine(runDir, project + Suffix)

/// Everything the marker says.
[<NoComparison>]
type internal Facts =
    {
        Project: string
        /// The project's `timeoutSec`.
        After: TimeSpan
        /// When the host was launched (UTC).
        SpawnedAt: DateTime
        /// What the kill call did.
        Kill: KillOutcome
        /// How the tree was torn down, when the teardown was accounted.
        Teardown: TreeTeardown option
    }

let private iso (at: DateTime) = at.ToString("O")

/// The one line a reader prints in place of "no report".
let internal message (facts: Facts) : string =
    let stop =
        match facts.Teardown |> Option.bind _.Interrupt with
        | Some i when i.Exited -> $"host sent %s{i.Signal}, exited within %.1f{i.Waited.TotalSeconds}s"
        | Some i -> $"host sent %s{i.Signal}, still running after %.1f{i.Waited.TotalSeconds}s, then killed"
        | None -> "host killed"

    $"TIMED OUT after %d{int facts.After.TotalSeconds}s (spawned at %s{iso facts.SpawnedAt}; %s{stop})%s{renderKillBrief facts.Kill}"

let private killName (kill: KillOutcome) =
    match kill with
    | KillOutcome.Killed -> "killed"
    | KillOutcome.AlreadyExited -> "already-exited"
    | KillOutcome.KillFailed _ -> "kill-failed"
    | KillOutcome.KillTimedOut _ -> "kill-timed-out"

/// The marker's JSON.
let internal render (facts: Facts) : string =
    // `null`, never an empty value, where the teardown did not establish the fact: "no
    // survivors" and "survivors unknown" must not read alike.
    let stop: obj =
        match facts.Teardown |> Option.bind _.Interrupt with
        | Some i ->
            box
                {| signal = i.Signal
                   at = iso i.At
                   signalled = i.Signalled.Length
                   exited = i.Exited
                   waitedMs = int i.Waited.TotalMilliseconds |}
        | None -> null

    let survivors: obj =
        match facts.Teardown |> Option.map _.Survivors with
        | Some(Ok rows) -> box (rows |> List.map _.Pid |> Array.ofList)
        | Some(Error _)
        | None -> null

    JsonSerializer.Serialize(
        {| project = facts.Project
           timeoutSec = int facts.After.TotalSeconds
           spawnedAt = iso facts.SpawnedAt
           message = message facts
           stop = stop
           kill = killName facts.Kill
           survivors = survivors |},
        JsonSerializerOptions(WriteIndented = true)
    )

/// Write the marker. A filesystem refusal is logged and otherwise ignored: losing the
/// marker loses a sentence, never the run.
let internal write (runDir: string) (facts: Facts) : unit =
    let path = pathIn runDir facts.Project

    try
        FsHwPaths.atomicWriteAllText path (render facts)
    with ex when RunLog.isExpectedFileFailure ex ->
        Logging.warn "test-prune" $"%s{facts.Project}: could not write the timeout marker %s{path}: %s{ex.Message}"
