/// The CoreCLR thread-suspend activation setting a launched daemon runs with.
///
/// To stop the world for a GC, CoreCLR interrupts threads running managed code in
/// cooperative mode by sending them an activation signal (SIGUSR1 on Unix). On some
/// macOS releases that signal is intermittently delivered with a NULL handler, and the
/// process dies with SIGSEGV at pc=0. A daemon suspends threads constantly, with many
/// threads unwinding exceptions and forking children, so it is the process most exposed.
///
/// `DOTNET_INTERNAL_ThreadSuspendInjection=0`, a configuration the release runtime
/// reads, turns activation injection off: the GC then reaches a safe point through the
/// threads' own GC polls and return-address hijacks, which can lengthen GC pauses.
///
/// On macOS the CLI sets it in the environment of every daemon it launches, unless the
/// user's environment already names a value, or `FSHW_THREAD_SUSPEND_INJECTION=1` asks
/// for the runtime default. The variable has to be in place before the runtime starts,
/// so a daemon started in the foreground runs with whatever its shell provides.
module FsHotWatch.Cli.ThreadSuspendInjection

open System
open System.Runtime.InteropServices

/// The runtime configuration, under its current prefix.
[<Literal>]
let RuntimeVariable = "DOTNET_INTERNAL_ThreadSuspendInjection"

/// The same configuration under the runtime's legacy prefix.
[<Literal>]
let LegacyRuntimeVariable = "COMPlus_INTERNAL_ThreadSuspendInjection"

/// `1` keeps the runtime's default (injection on) for daemons this CLI launches.
[<Literal>]
let OptOutEnvVar = "FSHW_THREAD_SUSPEND_INJECTION"

let private isSet (value: string) = not (String.IsNullOrWhiteSpace value)

/// The variable and value to add to a launched daemon's environment, or `None` when
/// the launch keeps the environment it inherits: off macOS, when the user set either
/// runtime variable, or when the opt-out asks for the runtime default.
let launchOverride (isMacOS: bool) (getEnv: string -> string) : (string * string) option =
    if
        not isMacOS
        || isSet (getEnv RuntimeVariable)
        || isSet (getEnv LegacyRuntimeVariable)
        || (getEnv OptOutEnvVar |> Option.ofObj |> Option.map _.Trim()) = Some "1"
    then
        None
    else
        Some(RuntimeVariable, "0")

/// The mode THIS process runs in, read from its own environment: the variable the
/// runtime honours (`DOTNET_` before `COMPlus_`) and its value, or `None` when neither
/// is set and injection is on by default.
let private effectiveSetting (getEnv: string -> string) : (string * string) option =
    [ RuntimeVariable; LegacyRuntimeVariable ]
    |> List.tryPick (fun name ->
        match getEnv name with
        | value when isSet value -> Some(name, value.Trim())
        | _ -> None)

/// The startup log line naming the thread-suspend mode this process runs in and the
/// OS it runs on. Printed on every platform, so a log from anywhere says which mode
/// produced it.
let startupLine (osDescription: string) (getEnv: string -> string) : string =
    let mode =
        match effectiveSetting getEnv with
        | Some(name, "0") -> $"injection disabled (%s{name}=0)"
        | Some(name, value) -> $"injection enabled (%s{name}=%s{value})"
        | None -> "injection enabled (runtime default)"

    let optOut =
        match getEnv OptOutEnvVar with
        | value when isSet value -> $", %s{OptOutEnvVar}=%s{value.Trim()}"
        | _ -> ""

    $"thread-suspend: %s{mode}%s{optOut}; os: %s{osDescription}"

[<DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)>]
extern int private sysctlByName(string name, byte[] value, unativeint& length, nativeint newValue, unativeint newLength)

/// The macOS build identifier (`kern.osversion`, e.g. `26A428`), or `None`.
let private macOSBuild () : string option =
    try
        let buffer = Array.zeroCreate<byte> 64
        let mutable length = unativeint buffer.Length

        if
            sysctlByName ("kern.osversion", buffer, &length, IntPtr.Zero, 0un) = 0
            && length > 0un
        then
            Some(Text.Encoding.ASCII.GetString(buffer, 0, int length).TrimEnd(char 0))
        else
            None
    with _ ->
        None

/// This machine's OS for the startup line: `macOS <version> (<build>)` on macOS, the
/// runtime's description elsewhere.
let osDescription () : string =
    if OperatingSystem.IsMacOS() then
        let version = Environment.OSVersion.Version.ToString()

        match macOSBuild () with
        | Some build -> $"macOS %s{version} (%s{build})"
        | None -> $"macOS %s{version}"
    else
        RuntimeInformation.OSDescription
