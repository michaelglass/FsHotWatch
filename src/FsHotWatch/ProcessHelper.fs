module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
open System.Threading.Tasks

/// Outcome of running an external process. Tagged so callers can tell a
/// nonzero exit from a timeout-induced kill without parsing the output.
type ProcessOutcome =
    /// Process exited with code 0. `output` is combined stdout+stderr (trimmed).
    | Succeeded of output: string
    /// Process exited with a nonzero code. `output` is combined stdout+stderr.
    | Failed of exitCode: int * output: string
    /// Process did not exit within the timeout and was killed (along with its
    /// child process tree). `tail` is whatever stdout+stderr we drained before
    /// the kill — best-effort, may be empty.
    | TimedOut of after: TimeSpan * tail: string

/// Outcome of an in-process unit of work bounded by a wall-clock timeout.
type WorkOutcome<'a> =
    | WorkCompleted of 'a
    | WorkTimedOut of after: TimeSpan

let isSucceeded =
    function
    | Succeeded _ -> true
    | _ -> false

let isTimedOut =
    function
    | TimedOut _ -> true
    | _ -> false

/// Combined output regardless of outcome — for callers that just want the text
/// to render in a status line. Preserves the historical message format.
let outputOf (outcome: ProcessOutcome) : string =
    match outcome with
    | Succeeded out -> out
    | Failed(_, out) -> out
    | TimedOut(after, tail) -> $"timed out after %d{int after.TotalSeconds}s\n%s{tail}"

/// Run a process with a timeout. Reads stdout and stderr concurrently to avoid
/// deadlock. On timeout the process tree is killed and TimedOut is returned.
let runProcessWithTimeout
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (timeout: TimeSpan)
    : ProcessOutcome =
    let psi = ProcessStartInfo(command, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- workDir

    for (key, value) in env do
        psi.Environment[key] <- value

    use proc = Process.Start(psi)
    // Register so a daemon shutdown can tear down in-flight children.
    ProcessRegistry.track proc

    try
        let stdoutTask = proc.StandardOutput.ReadToEndAsync()
        let stderrTask = proc.StandardError.ReadToEndAsync()

        let timeoutMs =
            if timeout = Threading.Timeout.InfiniteTimeSpan then
                -1
            else
                int timeout.TotalMilliseconds

        let exited = proc.WaitForExit(timeoutMs)

        if not exited then
            try
                proc.Kill(entireProcessTree = true)
            with _ ->
                ()

            // best-effort drain so we still report partial output
            let drainMs = 500

            try
                Task.WaitAll([| stdoutTask :> Task; stderrTask :> Task |], drainMs) |> ignore
            with _ ->
                ()

            let stdout =
                if stdoutTask.IsCompletedSuccessfully then
                    stdoutTask.Result
                else
                    ""

            let stderr =
                if stderrTask.IsCompletedSuccessfully then
                    stderrTask.Result
                else
                    ""

            TimedOut(timeout, $"%s{stdout}\n%s{stderr}".Trim())
        else
            Task.WaitAll(stdoutTask, stderrTask)
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result
            let output = $"%s{stdout}\n%s{stderr}".Trim()

            if proc.ExitCode = 0 then
                Succeeded output
            else
                Failed(proc.ExitCode, output)
    finally
        ProcessRegistry.untrack proc

/// Run a process to completion (no timeout).
let runProcess (command: string) (args: string) (workDir: string) (env: (string * string) list) : ProcessOutcome =
    runProcessWithTimeout command args workDir env Threading.Timeout.InfiniteTimeSpan

/// Run a synchronous unit of work with a wall-clock timeout. The orphan task
/// continues running in the background — there is no cancellation hook.
/// Acceptable for plugin handlers where the next event will start a fresh unit
/// of work.
///
/// Uses `TaskCreationOptions.LongRunning` so the work runs on a dedicated
/// thread rather than a pool worker. Plugin work can be CPU-heavy (FCS,
/// analyzers) and the timeout-test path injects `Thread.Sleep` to force
/// expiry; both starve the default thread pool under parallel test load and
/// caused 5s xUnit timeouts to fire spuriously on unrelated tests.
let runWithTimeout (timeout: TimeSpan) (work: unit -> 'a) : WorkOutcome<'a> =
    if timeout = Threading.Timeout.InfiniteTimeSpan then
        WorkCompleted(work ())
    else
        let task =
            Task.Factory.StartNew((fun () -> work ()), TaskCreationOptions.LongRunning)

        if task.Wait(timeout) then
            WorkCompleted task.Result
        else
            WorkTimedOut timeout
