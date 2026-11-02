module FsHotWatch.Tests.ThreadSuspendInjectionTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.ThreadSuspendInjection

let private envOf (pairs: (string * string) list) : string -> string =
    let map = Map.ofList pairs
    fun name -> map |> Map.tryFind name |> Option.toObj

let private helperEnvironment (isMacOS: bool) (getEnv: string -> string) =
    DetachedLaunch.helperStartInfo isMacOS getEnv "/usr/bin/true" "/" "exit 0"
    |> _.Environment

/// What the launched daemon inherits for `name` when the launch adds nothing.
let private inherited (name: string) =
    Environment.GetEnvironmentVariable name |> Option.ofObj

let private valueIn (environment: Collections.Generic.IDictionary<string, string>) (name: string) =
    match environment.TryGetValue name with
    | true, value -> Option.ofObj value
    | _ -> None

[<Fact>]
let ``on macOS the launched daemon's environment disables thread-suspend injection`` () =
    test <@ launchOverride true (Setting.read (envOf [])) = Some(RuntimeVariable, "0") @>
    test <@ valueIn (helperEnvironment true (envOf [])) RuntimeVariable = Some "0" @>

[<Fact>]
let ``off macOS the launch leaves the environment it inherits`` () =
    test <@ launchOverride false (Setting.read (envOf [])) = None @>
    test <@ valueIn (helperEnvironment false (envOf [])) RuntimeVariable = inherited RuntimeVariable @>

[<Theory>]
[<InlineData("DOTNET_INTERNAL_ThreadSuspendInjection", "1")>]
[<InlineData("DOTNET_INTERNAL_ThreadSuspendInjection", "0")>]
[<InlineData("COMPlus_INTERNAL_ThreadSuspendInjection", "1")>]
let ``a value the user set explicitly is never overridden`` (name: string, value: string) =
    let getEnv = envOf [ name, value ]
    test <@ launchOverride true (Setting.read getEnv) = None @>
    test <@ valueIn (helperEnvironment true getEnv) RuntimeVariable = inherited RuntimeVariable @>

[<Fact>]
let ``the opt-out keeps the runtime default on macOS`` () =
    let getEnv = envOf [ OptOutEnvVar, "1" ]
    test <@ launchOverride true (Setting.read getEnv) = None @>
    test <@ valueIn (helperEnvironment true getEnv) RuntimeVariable = inherited RuntimeVariable @>

[<Theory>]
[<InlineData("0")>]
[<InlineData("")>]
[<InlineData("yes")>]
let ``any opt-out value other than 1 still disables injection`` (value: string) =
    test <@ launchOverride true (Setting.read (envOf [ OptOutEnvVar, value ])) = Some(RuntimeVariable, "0") @>

[<Fact>]
let ``startup line names the active mode and the OS`` () =
    test
        <@
            startupLine "macOS 27.0 (26A428)" (Setting.read (envOf [ RuntimeVariable, "0" ])) = "thread-suspend: injection disabled (DOTNET_INTERNAL_ThreadSuspendInjection=0); os: macOS 27.0 (26A428)"
        @>

    test
        <@
            startupLine "Linux" (Setting.read (envOf [])) = "thread-suspend: injection enabled (runtime default); os: Linux"
        @>

    test
        <@
            startupLine "macOS 27.0" (Setting.read (envOf [ LegacyRuntimeVariable, "1"; OptOutEnvVar, "1" ])) = "thread-suspend: injection enabled (COMPlus_INTERNAL_ThreadSuspendInjection=1), FSHW_THREAD_SUSPEND_INJECTION=1; os: macOS 27.0"
        @>

[<Fact>]
let ``the current prefix wins over the legacy one, as in the runtime`` () =
    test
        <@
            startupLine "x" (Setting.read (envOf [ RuntimeVariable, "0"; LegacyRuntimeVariable, "1" ])) = "thread-suspend: injection disabled (DOTNET_INTERNAL_ThreadSuspendInjection=0); os: x"
        @>

[<Fact>]
let ``os description names macOS with its build on macOS`` () =
    let description = osDescription ()

    if OperatingSystem.IsMacOS() then
        test <@ description.StartsWith "macOS " @>
        test <@ description.Contains "(" @>
    else
        test <@ description = Runtime.InteropServices.RuntimeInformation.OSDescription @>

[<Fact>]
let ``os description on macOS carries the build when there is one`` () =
    let version = Environment.OSVersion.Version.ToString()

    test <@ describeOs true (fun () -> Some "26A428") = $"macOS %s{version} (26A428)" @>
    test <@ describeOs true (fun () -> None) = $"macOS %s{version}" @>

[<Fact>]
let ``os description off macOS is the runtime's, and asks for no build`` () =
    let description =
        describeOs false (fun () -> failwith "a build is only asked for on macOS")

    test <@ description = Runtime.InteropServices.RuntimeInformation.OSDescription @>

[<Fact>]
let ``a build is read only from a sysctl that succeeded and wrote something`` () =
    let answer = Text.Encoding.ASCII.GetBytes "26A428\000\000"

    // unativeint constants cannot appear in a quotation.
    let written, empty = 7un, 0un

    test <@ decodeBuild 0 answer written = Some "26A428" @>
    test <@ decodeBuild 0 answer empty = None @>
    test <@ decodeBuild -1 answer written = None @>

// ---------------------------------------------------------------------------
// Daemonless hosts (AUTOMATION-1425): `--run-once` and an in-process `test-rerun` start
// test hosts, builds and helpers with no daemon in between. The CLI puts the setting in
// its own environment, which every child inherits; its own runtime has already started.
// ---------------------------------------------------------------------------

/// An environment the code under test may change, as `getEnv` and `setEnv`.
let private mutableEnv (pairs: (string * string) list) =
    let values = Collections.Generic.Dictionary<string, string>(dict pairs)

    let getEnv name =
        match values.TryGetValue name with
        | true, value -> value
        | _ -> null

    values, getEnv, (fun (name: string) (value: string) -> values[name] <- value)

[<Fact>]
let ``on macOS the children of a daemonless CLI inherit injection disabled`` () =
    let _, getEnv, setEnv = mutableEnv []

    inheritByChildren true getEnv setEnv |> ignore

    test <@ getEnv RuntimeVariable = "0" @>

[<Fact>]
let ``the CLI's own mode is the one its runtime started with, not the one its children inherit`` () =
    let _, getEnv, setEnv = mutableEnv []

    let own = inheritByChildren true getEnv setEnv

    test <@ own.Configured = None @>

    test
        <@
            startupLine "macOS 27.0 (26A428)" own = "thread-suspend: injection enabled (runtime default); os: macOS 27.0 (26A428)"
        @>

[<Theory>]
[<InlineData("DOTNET_INTERNAL_ThreadSuspendInjection", "1")>]
[<InlineData("COMPlus_INTERNAL_ThreadSuspendInjection", "1")>]
[<InlineData("FSHW_THREAD_SUSPEND_INJECTION", "1")>]
let ``a daemonless CLI leaves an explicit value or the opt-out alone`` (name: string, value: string) =
    let values, getEnv, setEnv = mutableEnv [ name, value ]

    inheritByChildren true getEnv setEnv |> ignore

    test <@ values.Count = 1 && values[name] = value @>

[<Fact>]
let ``off macOS a daemonless CLI changes nothing`` () =
    let values, getEnv, setEnv = mutableEnv []

    inheritByChildren false getEnv setEnv |> ignore

    test <@ values.Count = 0 @>

[<Fact>]
let ``the setting is read once: the variable the runtime honours, and a non-blank opt-out`` () =
    test <@ Setting.read (envOf []) = { Configured = None; OptOut = None } @>

    test
        <@
            Setting.read (envOf [ RuntimeVariable, " 0 "; LegacyRuntimeVariable, "1"; OptOutEnvVar, " 1 " ]) = { Configured =
                                                                                                                   Some(
                                                                                                                       RuntimeVariable,
                                                                                                                       "0"
                                                                                                                   )
                                                                                                                 OptOut =
                                                                                                                   Some
                                                                                                                       "1" }
        @>

    test
        <@
            Setting.read (envOf [ LegacyRuntimeVariable, "1"; RuntimeVariable, "  "; OptOutEnvVar, "  " ]) = { Configured =
                                                                                                                 Some(
                                                                                                                     LegacyRuntimeVariable,
                                                                                                                     "1"
                                                                                                                 )
                                                                                                               OptOut =
                                                                                                                 None }
        @>
