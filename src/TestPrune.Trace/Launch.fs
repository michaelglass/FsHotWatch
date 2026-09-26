/// Running a built test app (apphost) to completion.
module TestPrune.Trace.Launch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading

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

/// One child to run to completion.
type LaunchRequest =
    {
        Exe: string
        Args: string list
        /// Environment to add to the child's own. Carries `DOTNET_ROOT` whenever the child is
        /// an apphost, so a launcher never has to resolve it.
        Env: (string * string) list
        WorkDir: string
        /// How long the child may run before its process tree is killed.
        Timeout: TimeSpan
    }

/// Runs a child to completion and returns its exit code and combined output. A launcher
/// kills the child's process tree at `Timeout`, and once the token is cancelled it kills
/// the tree and raises `OperationCanceledException`. A host that owns its children (a
/// daemon that reaps them on shutdown) supplies its own so every child joins its scope.
type Launcher = LaunchRequest -> CancellationToken -> int * string

/// The default launcher: starts the child directly. Both pipes are drained concurrently.
let direct: Launcher =
    fun req ct ->
        ct.ThrowIfCancellationRequested()

        let psi =
            ProcessStartInfo(
                req.Exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = req.WorkDir
            )

        for a in req.Args do
            psi.ArgumentList.Add a

        psi.Environment.["DOTNET_ROOT"] <- dotnetRoot ()

        for k, v in req.Env do
            psi.Environment.[k] <- v

        // `using` rather than `use`: `use` compiles a null check on the process that can never
        // be taken (Process.Start returns null only for UseShellExecute=true).
        using (Process.Start psi) (fun p ->
            let out = p.StandardOutput.ReadToEndAsync()
            let err = p.StandardError.ReadToEndAsync()

            // Killing a child that already exited is a no-op, so a cancellation racing
            // the child's own exit is harmless. Disposing the registration waits for a
            // kill already running.
            let exited =
                using (ct.Register(fun () -> p.Kill true)) (fun _ -> p.WaitForExit req.Timeout)

            if not exited then
                p.Kill true

            p.WaitForExit()
            ct.ThrowIfCancellationRequested()
            p.ExitCode, out.Result + err.Result)

/// Run `exe` to completion (or kill its process tree at `timeout`), returning the exit
/// code and the combined output: `direct`, uncancellable.
let run
    (exe: string)
    (args: string list)
    (env: (string * string) list)
    (workDir: string)
    (timeout: TimeSpan)
    : int * string =
    direct
        { Exe = exe
          Args = args
          Env = env
          WorkDir = workDir
          Timeout = timeout }
        CancellationToken.None
