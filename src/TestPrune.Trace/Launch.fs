/// Running a built test app (apphost) to completion.
module TestPrune.Trace.Launch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

/// `dotnetRoot` given the DOTNET_ROOT value (null or empty when unset).
let internal dotnetRootFrom (environmentValue: string) : string =
    if String.IsNullOrEmpty environmentValue then
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))
    else
        environmentValue

/// The dotnet root an apphost needs. DOTNET_ROOT wins; otherwise the root of the
/// runtime THIS process runs on (…/shared/Microsoft.NETCore.App/<v>/ → three levels up).
/// Deliberately not "the `dotnet` on PATH": a repository may put a wrapper script there.
let dotnetRoot () : string =
    dotnetRootFrom (Environment.GetEnvironmentVariable "DOTNET_ROOT")

/// Run `exe` to completion (or kill its process tree at `timeout`), returning the exit
/// code and the combined output. Both pipes are drained concurrently.
let run
    (exe: string)
    (args: string list)
    (env: (string * string) list)
    (workDir: string)
    (timeout: TimeSpan)
    : int * string =
    let psi =
        ProcessStartInfo(
            exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir
        )

    for a in args do
        psi.ArgumentList.Add a

    psi.Environment.["DOTNET_ROOT"] <- dotnetRoot ()

    for k, v in env do
        psi.Environment.[k] <- v

    // `using` rather than `use`: `use` compiles a null check on the process that can never
    // be taken (Process.Start returns null only for UseShellExecute=true).
    using (Process.Start psi) (fun p ->
        let out = p.StandardOutput.ReadToEndAsync()
        let err = p.StandardError.ReadToEndAsync()

        if not (p.WaitForExit timeout) then
            p.Kill true

        p.WaitForExit()
        p.ExitCode, out.Result + err.Result)
