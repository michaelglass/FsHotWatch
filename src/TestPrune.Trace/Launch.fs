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

/// Ends the note a launch adds when it stopped processes its child left running.
[<Literal>]
let LeftRunningNote = "process(es) the run left running"

/// Starts the note a launch adds when its output was still held open after the run ended and
/// every process found holding it was stopped.
[<Literal>]
let HeldOpenNote = "[launch: output still held open after the run ended"

/// How long a launch waits for the output pipes to close, once its child has exited and what
/// it left running is stopped; and again after stopping what holds them.
let DrainGrace = TimeSpan.FromSeconds 2.0

/// The last `limit` characters of a stream read so far. A launch reads it when the stream ends,
/// or when it stops waiting for the end.
type internal Tail(limit: int) =
    let kept = StringBuilder()
    let mutable dropped = false

    // Monitor rather than `lock`, whose compiled lock-taken check is a branch no input can take.
    member _.Append(buffer: char[], count: int) =
        Monitor.Enter kept

        try
            kept.Append(buffer, 0, count) |> ignore

            // Trim once the buffer holds twice the limit, so a trim costs amortized O(1).
            if kept.Length > 2 * limit then
                kept.Remove(0, kept.Length - limit) |> ignore
                dropped <- true
        finally
            Monitor.Exit kept

    /// The kept text, prefixed with `DroppedOutputMarker` when anything before it was dropped.
    member _.Text() =
        Monitor.Enter kept

        try
            let over = kept.Length - limit

            let text =
                if over > 0 then
                    kept.ToString(over, limit)
                else
                    kept.ToString()

            (if dropped || over > 0 then DroppedOutputMarker else "") + text
        finally
            Monitor.Exit kept

/// Reads `reader` to its end into `tail`.
let internal drainInto (reader: TextReader) (tail: Tail) : Task =
    task {
        let buffer = Array.zeroCreate<char> 8192
        let mutable reading = true

        while reading do
            let! read = reader.ReadAsync(buffer, 0, buffer.Length)
            reading <- read > 0
            tail.Append(buffer, read)
    }

/// How a launch's output pipes ended once its child had exited and what carried its id was
/// stopped.
type internal Ending =
    /// They closed within `DrainGrace`.
    | Closed
    /// They closed once the `stopped` processes holding them were.
    | ClosedByStopping of stopped: int
    /// They are still open after the `stopped` processes found holding them were: something
    /// not found holds them.
    | StillHeld of stopped: int
    /// They are still open, and what holds them could not be looked for.
    | Unsearched of why: string

/// End what a launch's child left running (AUTOMATION-1734), and say what was done, as notes
/// for the launch's output. Every process carrying `launchId` is stopped. If the output pipes
/// (`streams`, read by `draining`) are still open `DrainGrace` later, the processes holding
/// them are found by the pipes' identity (`Leftovers.holders`, in `table`) and stopped, and the launch
/// waits once more. It never waits longer, and never throws for what it could not find.
let internal endLeftovers
    (table: Leftovers.ProcessTable)
    (launchId: string)
    (streams: Stream list)
    (draining: Task)
    : string =
    // A process re-parented when the child exited is outside `Kill true`'s tree, and one that
    // inherited the output pipes holds them open: waiting for them to close waits for it.
    let tagged = Leftovers.carrying table launchId |> Result.map Leftovers.terminate

    let ending =
        if draining.Wait DrainGrace then
            Closed
        else
            match Leftovers.holders table streams with
            | Error why -> Unsearched why
            | Ok pids ->
                let stopped = Leftovers.terminate pids

                if draining.Wait DrainGrace then
                    ClosedByStopping stopped
                else
                    StillHeld stopped

    let taggedNote =
        match tagged with
        | Ok 0 -> ""
        | Ok n -> $"[launch: stopped %d{n} %s{LeftRunningNote}]\n"
        | Error why -> $"[launch: could not look for the processes the run left running: %s{why}]\n"

    let endingNote =
        match ending with
        | Closed -> ""
        | ClosedByStopping n -> $"[launch: stopped %d{n} %s{LeftRunningNote}, holding its output]\n"
        | StillHeld n -> $"%s{HeldOpenNote} by a process not found (stopped %d{n} that held it); the rest is dropped]\n"
        | Unsearched why -> $"%s{HeldOpenNote}; what holds it could not be looked for: %s{why}; the rest is dropped]\n"

    taggedNote + endingNote

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

        // The launch's own id, which everything the child starts inherits (`endLeftovers`).
        let launchId = Guid.NewGuid().ToString "N"
        psi.Environment.[Leftovers.LaunchIdEnv] <- launchId

        for k, v in req.Env do
            psi.Environment.[k] <- v

        // `using` rather than `use`: `use` compiles a null check on the process that can never
        // be taken (Process.Start returns null only for UseShellExecute=true).
        using (Process.Start psi) (fun p ->
            let out = Tail OutputTailChars
            let err = Tail OutputTailChars

            let draining =
                Task.WhenAll(drainInto p.StandardOutput out, drainInto p.StandardError err)

            // Killing a child that already exited is a no-op, so a cancellation racing
            // the child's own exit is harmless. Disposing the registration waits for a
            // kill already running.
            let exited =
                using (ct.Register(fun () -> p.Kill true)) (fun _ -> p.WaitForExit req.Timeout)

            if not exited then
                p.Kill true

            p.WaitForExit()

            let notes =
                endLeftovers
                    Leftovers.current
                    launchId
                    [ p.StandardOutput.BaseStream; p.StandardError.BaseStream ]
                    draining

            ct.ThrowIfCancellationRequested()
            p.ExitCode, out.Text() + err.Text() + notes)

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
