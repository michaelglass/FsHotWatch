/// Start a background command that outlives the CLI AND the CLI's process group.
///
/// `/bin/sh -c "nohup … &"` alone is not enough: a non-interactive shell has no job
/// control, so the backgrounded daemon stays in the caller's process group, and
/// anything that signals that group (a terminal closing, a test runner or agent
/// harness tearing down its tree) kills the daemon along with the command that
/// merely asked for it.
///
/// The fix runs in a SHORT-LIVED COPY of this CLI, never in the caller: it calls
/// `setsid` (a new session and process group, with no controlling terminal), then
/// `execv`s the launch shell in place of itself. The shell backgrounds the daemon
/// inside that new group and exits; the caller waits a bounded time for it.
///
/// Rejected alternatives: `setsid` in the caller (it would move the CLI itself, and
/// fails outright for a group leader); a parent-side `setpgid` racing the child's
/// exec; forking the multithreaded runtime; a `setsid` executable (macOS has none);
/// Python or Perl in the launcher. `ProcessStartInfo.CreateNewProcessGroup` is
/// Windows-only on .NET 10.
module FsHotWatch.Cli.DetachedLaunch

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

/// The hidden argument that turns a CLI process into the launch helper. Only the
/// EXACT shape `[| HelperFlag; command |]` is honoured.
[<Literal>]
let HelperFlag = "--internal-detached-launch"

/// How long the caller waits for the helper, and again for its reaping. A healthy
/// helper exits within a runtime start-up; the bound only has to catch a stuck one, and
/// must not turn a heavily loaded machine into a failed launch — so it matches the
/// CLI's own deadline for a daemon to come up.
let HelperBound = TimeSpan.FromSeconds 30.0

/// Single-quote `value` for `/bin/sh`: every character is literal.
let shellQuote (value: string) : string = "'" + value.Replace("'", "'\\''") + "'"

/// The shell line that backgrounds the daemon. `toolPrefix` and `extraArgs` are
/// already rendered for the shell (each ends in a space when non-empty); the
/// executable and log paths are quoted here. stdin is `/dev/null`, so the daemon
/// never holds a terminal or a caller's pipe open.
let daemonShellCommand (exe: string) (toolPrefix: string) (extraArgs: string) (logFile: string) : string =
    $"nohup %s{shellQuote exe} %s{toolPrefix}%s{extraArgs}start >> %s{shellQuote logFile} 2>&1 < /dev/null &"

/// Why the helper returned instead of becoming the shell. A successful `execv`
/// never returns, so a helper that gets to classify anything has failed.
[<RequireQualifiedAccess>]
type HelperFailure =
    | SessionFailed of errno: int
    | ExecFailed of errno: int

let describeHelperFailure (failure: HelperFailure) : string =
    match failure with
    | HelperFailure.SessionFailed errno -> $"Detached daemon launch: setsid failed (errno %d{errno})"
    | HelperFailure.ExecFailed errno -> $"Detached daemon launch: execv failed (errno %d{errno})"

/// The helper's decision logic, with the native calls injected. `lastError` is read
/// straight after the failing call, before anything else can overwrite it. A failed
/// session change never reaches exec: without a new group, a shell started here would
/// reproduce exactly the shared-group launch this exists to prevent.
let internal runHelperWith
    (createSession: unit -> int)
    (replaceProcess: string * nativeint -> int)
    (lastError: unit -> int)
    (command: string)
    : HelperFailure =
    if createSession () = -1 then
        HelperFailure.SessionFailed(lastError ())
    else
        let arguments = [| "/bin/sh"; "-c"; command |]
        let values = Array.zeroCreate<nativeint> arguments.Length
        let argv = Marshal.AllocHGlobal((arguments.Length + 1) * IntPtr.Size)

        try
            for index in 0 .. arguments.Length - 1 do
                values[index] <- Marshal.StringToCoTaskMemUTF8 arguments[index]
                Marshal.WriteIntPtr(argv, index * IntPtr.Size, values[index])

            Marshal.WriteIntPtr(argv, arguments.Length * IntPtr.Size, IntPtr.Zero)
            replaceProcess ("/bin/sh", argv) |> ignore
            HelperFailure.ExecFailed(lastError ())
        finally
            Marshal.FreeHGlobal argv

            // Freeing a slot that was never allocated (zero) is a no-op.
            for index in 0 .. values.Length - 1 do
                Marshal.FreeCoTaskMem values[index]

[<DllImport("libc", EntryPoint = "setsid", SetLastError = true)>]
extern int private createSession()

[<DllImport("libc", EntryPoint = "execv", SetLastError = true)>]
extern int private replaceProcess([<MarshalAs(UnmanagedType.LPUTF8Str)>] string path, nativeint argv)

/// The real native helper. Only ever runs inside the dedicated helper process: in
/// any other process a successful call would move that process into a new session
/// and then replace it with a shell.
let private runHelper (command: string) : HelperFailure =
    runHelperWith createSession replaceProcess Marshal.GetLastPInvokeError command

/// Route a helper invocation, or `None` for every ordinary command line. Checked
/// before normal CLI parsing so the hidden flag never reaches the command tree.
let internal tryRunWith (run: string -> HelperFailure) (report: string -> unit) (args: string array) : int option =
    match args with
    | [| flag; command |] when flag = HelperFlag ->
        report (describeHelperFailure (run command))
        Some 1
    | _ -> None

let tryRun (args: string array) : int option =
    tryRunWith runHelper (eprintfn "%s") args

/// The `dotnet` host that runs the helper copy of this CLI. The muxer this process
/// was started by is preferred; otherwise the host of the runtime actually loaded
/// here (`<root>/shared/Microsoft.NETCore.App/<version>/` → `<root>/dotnet`), which
/// also covers an apphost or a test host.
///
/// Raises when neither exists: nothing can run the helper, so nothing is launched.
///
/// `processPath` is `Environment.ProcessPath`, which may be null.
let internal helperHost (processPath: string) (runtimeDirectory: string) (exists: string -> bool) : string =
    let runningMuxer =
        processPath
        |> Option.ofObj
        |> Option.filter (fun path -> Path.GetFileNameWithoutExtension path = "dotnet")

    let runtimeMuxer =
        Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", "dotnet"))

    match [ yield! Option.toList runningMuxer; runtimeMuxer ] |> List.tryFind exists with
    | Some host -> host
    | None -> invalidOp $"Detached daemon launch: no dotnet host found for runtime %s{runtimeDirectory}"

/// What the caller observed of its helper.
[<RequireQualifiedAccess>]
type HelperObservation =
    | Exited of exitCode: int
    /// Still running at the bound; `reaped` says whether it died after being killed.
    | Stuck of reaped: bool

/// The failure a helper observation means, naming the command it was launching.
let internal launchFailure (bound: TimeSpan) (command: string) (observation: HelperObservation) : exn option =
    match observation with
    | HelperObservation.Exited 0 -> None
    | HelperObservation.Exited code ->
        Some(InvalidOperationException $"Detached daemon launch helper exited %d{code} launching: %s{command}")
    | HelperObservation.Stuck true ->
        Some(
            TimeoutException
                $"Detached daemon launch helper exceeded %g{bound.TotalSeconds} seconds and was killed, launching: %s{command}"
        )
    | HelperObservation.Stuck false ->
        Some(
            TimeoutException
                $"Detached daemon launch helper exceeded %g{bound.TotalSeconds} seconds and could not be reaped, launching: %s{command}"
        )

type private AssemblyAnchor = class end

/// The start info for the helper that runs `command` with `host`. It inherits this
/// process's environment, which the shell it execs and the daemon inherit in turn, plus
/// the thread-suspend setting `ThreadSuspendInjection.launchOverride` decides from
/// `isMacOS` and `getEnv`.
let internal helperStartInfo
    (isMacOS: bool)
    (getEnv: string -> string)
    (host: string)
    (workingDirectory: string)
    (command: string)
    : ProcessStartInfo =
    let psi = ProcessStartInfo(host)
    psi.ArgumentList.Add(typeof<AssemblyAnchor>.Assembly.Location)
    psi.ArgumentList.Add(HelperFlag)
    psi.ArgumentList.Add(command)
    psi.WorkingDirectory <- workingDirectory
    psi.UseShellExecute <- false
    psi.RedirectStandardInput <- true

    ThreadSuspendInjection.launchOverride isMacOS getEnv
    |> Option.iter (fun (name, value) -> psi.Environment[name] <- value)

    psi

/// Run `command` through the helper from `workingDirectory` and wait at most `bound`
/// for it. Raises when the helper cannot be started, fails, or has to be killed.
let internal launchWithin (bound: TimeSpan) (workingDirectory: string) (command: string) : unit =
    let host =
        helperHost Environment.ProcessPath (RuntimeEnvironment.GetRuntimeDirectory()) File.Exists

    let psi =
        helperStartInfo (OperatingSystem.IsMacOS()) Environment.GetEnvironmentVariable host workingDirectory command

    // FSHW-SPAWN-001 ok: the helper is started here rather than through ProcessHelper
    // because its environment passes, unmodified, to the shell it execs and so to the
    // daemon. ProcessHelper strips `DOTNET_ROOT_<arch>` and forces
    // `MSBUILDDISABLENODEREUSE` — right for a build child, wrong for a long-lived apphost
    // daemon that may locate its runtime through exactly that variable. The helper is
    // still bounded and reaped below; what it launches detaches on purpose and must not
    // be registered, or the next CLI shutdown would kill the daemon it asked for.
    use helper = Process.Start psi
    helper.StandardInput.Close()
    let boundMs = int bound.TotalMilliseconds

    let observation =
        if helper.WaitForExit boundMs then
            HelperObservation.Exited helper.ExitCode
        else
            // Our own just-started helper, never a pidfile-discovered process.
            helper.Kill(entireProcessTree = true)
            HelperObservation.Stuck(helper.WaitForExit boundMs)

    launchFailure bound command observation |> Option.iter raise

/// Launch `command` detached from this process's session and group.
let launch (workingDirectory: string) (command: string) : unit =
    launchWithin HelperBound workingDirectory command
