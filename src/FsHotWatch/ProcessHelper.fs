module FsHotWatch.ProcessHelper

open System.Diagnostics

/// Run a process and return (exitCode = 0, combined output).
/// Reads stdout and stderr concurrently to avoid deadlock.
let runProcess (command: string) (args: string) (workDir: string) (env: (string * string) list) : bool * string =
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
    // Await both concurrently to avoid deadlock when either buffer fills
    System.Threading.Tasks.Task.WaitAll(stdoutTask, stderrTask)
    let stdout = stdoutTask.Result
    let stderr = stderrTask.Result
    proc.WaitForExit()
    let output = $"%s{stdout}\n%s{stderr}".Trim()
    (proc.ExitCode = 0, output)
