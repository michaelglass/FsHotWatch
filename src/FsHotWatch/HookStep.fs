module FsHotWatch.HookStep

open System

/// One step of a hook chain while it runs: which step of how many, what it runs, the
/// child it runs as and the bound it runs under. A wait or wedge line that falls inside
/// a hook names this, so the time is attributed to a step and a process, not to the run.
type Running =
    { Label: string
      StepIndex: int
      StepCount: int
      Command: string
      Pid: int
      Bound: TimeSpan option }

/// The one rendering of a running step — the subtask key a wait line prints before the
/// step's elapsed time, and the text the step's start is logged with, so a log search
/// for either finds the other.
let describe (step: Running) : string =
    let bound =
        match step.Bound with
        | Some b -> $"bound %s{PluginWedge.formatElapsed b}"
        | None -> "no bound"

    $"%s{step.Label} step %d{step.StepIndex}/%d{step.StepCount} `%s{step.Command}` (pid %d{step.Pid}, %s{bound})"

/// Where a step is held for as long as it runs. The runner calls it once the child has
/// started and disposes the handle once the child has exited — green, red, or reaped.
type Tracker = Running -> IDisposable

/// Holds each step as a subtask of a plugin run, keyed by its description.
let asSubtasks (startSubtask: string -> string -> unit) (endSubtask: string -> unit) : Tracker =
    fun step ->
        let key = describe step
        startSubtask key key

        { new IDisposable with
            member _.Dispose() = endSubtask key }
