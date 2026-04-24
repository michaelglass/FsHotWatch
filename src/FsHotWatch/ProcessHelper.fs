module FsHotWatch.ProcessHelper

open System
open System.Diagnostics
open System.Threading.Tasks

/// Prefix on the output string when `runProcessWithTimeout` killed the
/// process for exceeding its timeout. Plugins use this to distinguish a
/// timeout from a normal nonzero exit.
[<Literal>]
let TimedOutPrefix = "timed out after "

/// Run a process with a timeout. Returns (exitCode = 0, combined output).
/// If the process exceeds `timeout`, it and its child process tree are killed,
/// the tuple returns `false` with output prefixed by `TimedOutPrefix`.
/// Reads stdout and stderr concurrently to avoid deadlock.
let runProcessWithTimeout
    (command: string)
    (args: string)
    (workDir: string)
    (env: (string * string) list)
    (timeout: TimeSpan)
    : bool * string =
    let psi = ProcessStartInfo(command, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- workDir

    for (key, value) in env do
        psi.Environment[key] <- value

    use proc = Process.Start(psi)
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

        let tail = $"%s{stdout}\n%s{stderr}".Trim()
        false, $"%s{TimedOutPrefix}%d{int timeout.TotalSeconds}s\n%s{tail}"
    else
        Task.WaitAll(stdoutTask, stderrTask)
        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        let output = $"%s{stdout}\n%s{stderr}".Trim()
        (proc.ExitCode = 0, output)

/// Run a process and return (exitCode = 0, combined output).
/// Reads stdout and stderr concurrently to avoid deadlock.
let runProcess (command: string) (args: string) (workDir: string) (env: (string * string) list) : bool * string =
    runProcessWithTimeout command args workDir env Threading.Timeout.InfiniteTimeSpan
