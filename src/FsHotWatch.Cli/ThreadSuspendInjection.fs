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
/// so a daemon started in the foreground, or a CLI hosting the work itself (`--run-once`,
/// an in-process `test-rerun`), runs with whatever its shell provides. Every process the
/// CLI starts inherits the setting, though (`inheritByChildren`).
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

/// What an environment says about thread-suspend injection, read once, so nothing after
/// the read asks whether a value is set or trims it again.
type Setting =
    {
        /// The variable the runtime honours (`DOTNET_` before `COMPlus_`) and its trimmed
        /// value; `None` when neither is set, and injection is on by default.
        Configured: (string * string) option
        /// `FSHW_THREAD_SUSPEND_INJECTION`, trimmed; `None` when unset or blank.
        OptOut: string option
    }

module Setting =
    let private valueOf (getEnv: string -> string) (name: string) =
        match getEnv name with
        | value when isSet value -> Some(value.Trim())
        | _ -> None

    /// The setting `getEnv` describes.
    let read (getEnv: string -> string) : Setting =
        { Configured =
            [ RuntimeVariable; LegacyRuntimeVariable ]
            |> List.tryPick (fun name -> valueOf getEnv name |> Option.map (fun value -> name, value))
          OptOut = valueOf getEnv OptOutEnvVar }

/// The variable and value to add to a launched daemon's environment, or `None` when
/// the launch keeps the environment it inherits: off macOS, when the user set either
/// runtime variable, or when the opt-out asks for the runtime default.
let launchOverride (isMacOS: bool) (setting: Setting) : (string * string) option =
    if not isMacOS || setting.Configured.IsSome || setting.OptOut = Some "1" then
        None
    else
        Some(RuntimeVariable, "0")

/// The startup log line naming the thread-suspend mode this process runs in and the
/// OS it runs on. Printed on every platform, so a log from anywhere says which mode
/// produced it.
let startupLine (osDescription: string) (setting: Setting) : string =
    let mode =
        match setting.Configured with
        | Some(name, "0") -> $"injection disabled (%s{name}=0)"
        | Some(name, value) -> $"injection enabled (%s{name}=%s{value})"
        | None -> "injection enabled (runtime default)"

    let optOut =
        match setting.OptOut with
        | Some value -> $", %s{OptOutEnvVar}=%s{value}"
        | None -> ""

    $"thread-suspend: %s{mode}%s{optOut}; os: %s{osDescription}"

[<DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)>]
extern int private sysctlByName(string name, byte[] value, unativeint& length, nativeint newValue, unativeint newLength)

/// The build `sysctlbyname("kern.osversion", ...)` answered with `status`, `buffer` and
/// `length`: `None` unless the call succeeded and wrote something.
let internal decodeBuild (status: int) (buffer: byte[]) (length: unativeint) : string option =
    if status = 0 && length > 0un then
        Some(Text.Encoding.ASCII.GetString(buffer, 0, int length).TrimEnd(char 0))
    else
        None

/// The macOS build identifier (`kern.osversion`, e.g. `26A428`), or `None`.
let private macOSBuild () : string option =
    try
        let buffer = Array.zeroCreate<byte> 64
        let mutable length = unativeint buffer.Length
        let status = sysctlByName ("kern.osversion", buffer, &length, IntPtr.Zero, 0un)
        decodeBuild status buffer length
    with _ ->
        None

/// `osDescription` with the platform and the build probe passed in. The probe runs
/// only on macOS.
let internal describeOs (isMacOS: bool) (build: unit -> string option) : string =
    if isMacOS then
        let version = Environment.OSVersion.Version.ToString()

        match build () with
        | Some build -> $"macOS %s{version} (%s{build})"
        | None -> $"macOS %s{version}"
    else
        RuntimeInformation.OSDescription

/// This machine's OS for the startup line: `macOS <version> (<build>)` on macOS, the
/// runtime's description elsewhere.
let osDescription () : string =
    describeOs (OperatingSystem.IsMacOS()) macOSBuild

/// Give every process this one starts the setting `launchOverride` decides, by putting it
/// in this process's environment, which children inherit (AUTOMATION-1425). A daemon's
/// children inherit it from the daemon; a CLI that hosts the work itself (`--run-once`,
/// an in-process `test-rerun`) starts test hosts, builds and helpers with no daemon in
/// between, and they ran with injection at the runtime default.
///
/// This process's OWN mode cannot change: the runtime read the variable when it started,
/// and `runtimeconfig.json` does not reach this knob (measured: a `configProperties` entry
/// changes `System.GC.Server` but not `EnableHWIntrinsic`, a knob read the same way). So
/// the result is the setting as it was, for `startupLine` to describe this process.
let inheritByChildren (isMacOS: bool) (getEnv: string -> string) (setEnv: string -> string -> unit) : Setting =
    let own = Setting.read getEnv

    launchOverride isMacOS own
    |> Option.iter (fun (name, value) -> setEnv name value)

    own

/// This process's own setting, as its runtime started with it: `install` records it
/// before it changes the environment children inherit.
let mutable private own: Setting option = None

/// The setting a process describes itself by: the one `install` `recorded`, else what
/// `getEnv` says now, since a process that never installed changed nothing.
let internal ownSettingFrom (recorded: Setting option) (getEnv: string -> string) : Setting =
    match recorded with
    | Some setting -> setting
    | None -> Setting.read getEnv

/// The setting `startupLine` describes this process from.
let ownSetting () : Setting =
    ownSettingFrom own Environment.GetEnvironmentVariable

let private setInThisProcess (name: string) (value: string) =
    Environment.SetEnvironmentVariable(name, value)

/// `inheritByChildren` on this process, once, before it starts anything.
let install () : unit =
    own <- Some(inheritByChildren (OperatingSystem.IsMacOS()) Environment.GetEnvironmentVariable setInThisProcess)
