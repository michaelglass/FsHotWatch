/// Running a built test app (apphost) to completion.
module TestPrune.Trace.Launch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

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

/// Prefix of every variable the recorder reads.
[<Literal>]
let internal TraceEnvPrefix = "TESTPRUNE_TRACE_"

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

/// The most characters a launch keeps of each output stream: its end, where a failing run
/// says why. A test app's full output can run to megabytes.
[<Literal>]
let OutputTailChars = 65536

/// Starts a stream's kept tail when its earlier output was dropped.
[<Literal>]
let DroppedOutputMarker = "[earlier output dropped]\n"

/// Reads `reader` to its end, keeping the last `limit` characters, prefixed with
/// `DroppedOutputMarker` when anything before them was dropped.
let internal drainTail (reader: TextReader) (limit: int) : Task<string> =
    task {
        let buffer = Array.zeroCreate<char> 8192
        let kept = StringBuilder()
        let mutable dropped = false
        let mutable reading = true

        while reading do
            let! read = reader.ReadAsync(buffer, 0, buffer.Length)
            reading <- read > 0
            kept.Append(buffer, 0, read) |> ignore

            // Trim once the buffer holds twice the limit, so a trim costs amortized O(1).
            if kept.Length > 2 * limit then
                kept.Remove(0, kept.Length - limit) |> ignore
                dropped <- true

        if kept.Length > limit then
            kept.Remove(0, kept.Length - limit) |> ignore
            dropped <- true

        return (if dropped then DroppedOutputMarker else "") + kept.ToString()
    }

/// A launch's output as report lines: one per non-blank line, line endings removed.
let outputLines (output: string) : string list =
    output.Split '\n'
    |> Seq.map (fun l -> l.TrimEnd '\r')
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.toList

/// The default launcher: starts the child directly. Both pipes are drained concurrently, each
/// kept to its last `OutputTailChars` characters. The
/// child inherits this process's environment except its `TESTPRUNE_TRACE_*` variables.
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

        // The child is its own run, never part of this process's trace: when this process runs
        // traced, an inherited TESTPRUNE_TRACE_* would make a woven child dump into this run's
        // directory. The child gets only the trace variables `req.Env` names.
        for k in
            psi.Environment.Keys
            |> Seq.filter (fun k -> k.StartsWith TraceEnvPrefix)
            |> Seq.toList do
            psi.Environment.Remove k |> ignore

        for k, v in req.Env do
            psi.Environment.[k] <- v

        // `using` rather than `use`: `use` compiles a null check on the process that can never
        // be taken (Process.Start returns null only for UseShellExecute=true).
        using (Process.Start psi) (fun p ->
            let out = drainTail p.StandardOutput OutputTailChars
            let err = drainTail p.StandardError OutputTailChars

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
/// code and the combined output (the tail of each stream, stdout first): `direct`, uncancellable. The child inherits this process's
/// environment except its `TESTPRUNE_TRACE_*` variables.
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
