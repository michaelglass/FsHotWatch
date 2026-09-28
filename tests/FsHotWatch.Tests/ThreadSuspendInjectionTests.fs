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
    test <@ launchOverride true (envOf []) = Some(RuntimeVariable, "0") @>
    test <@ valueIn (helperEnvironment true (envOf [])) RuntimeVariable = Some "0" @>

[<Fact>]
let ``off macOS the launch leaves the environment it inherits`` () =
    test <@ launchOverride false (envOf []) = None @>
    test <@ valueIn (helperEnvironment false (envOf [])) RuntimeVariable = inherited RuntimeVariable @>

[<Theory>]
[<InlineData("DOTNET_INTERNAL_ThreadSuspendInjection", "1")>]
[<InlineData("DOTNET_INTERNAL_ThreadSuspendInjection", "0")>]
[<InlineData("COMPlus_INTERNAL_ThreadSuspendInjection", "1")>]
let ``a value the user set explicitly is never overridden`` (name: string, value: string) =
    let getEnv = envOf [ name, value ]
    test <@ launchOverride true getEnv = None @>
    test <@ valueIn (helperEnvironment true getEnv) RuntimeVariable = inherited RuntimeVariable @>

[<Fact>]
let ``the opt-out keeps the runtime default on macOS`` () =
    let getEnv = envOf [ OptOutEnvVar, "1" ]
    test <@ launchOverride true getEnv = None @>
    test <@ valueIn (helperEnvironment true getEnv) RuntimeVariable = inherited RuntimeVariable @>

[<Theory>]
[<InlineData("0")>]
[<InlineData("")>]
[<InlineData("yes")>]
let ``any opt-out value other than 1 still disables injection`` (value: string) =
    test <@ launchOverride true (envOf [ OptOutEnvVar, value ]) = Some(RuntimeVariable, "0") @>

[<Fact>]
let ``startup line names the active mode and the OS`` () =
    test
        <@
            startupLine "macOS 27.0 (26A428)" (envOf [ RuntimeVariable, "0" ]) = "thread-suspend: injection disabled (DOTNET_INTERNAL_ThreadSuspendInjection=0); os: macOS 27.0 (26A428)"
        @>

    test <@ startupLine "Linux" (envOf []) = "thread-suspend: injection enabled (runtime default); os: Linux" @>

    test
        <@
            startupLine "macOS 27.0" (envOf [ LegacyRuntimeVariable, "1"; OptOutEnvVar, "1" ]) = "thread-suspend: injection enabled (COMPlus_INTERNAL_ThreadSuspendInjection=1), FSHW_THREAD_SUSPEND_INJECTION=1; os: macOS 27.0"
        @>

[<Fact>]
let ``the current prefix wins over the legacy one, as in the runtime`` () =
    test
        <@
            startupLine "x" (envOf [ RuntimeVariable, "0"; LegacyRuntimeVariable, "1" ]) = "thread-suspend: injection disabled (DOTNET_INTERNAL_ThreadSuspendInjection=0); os: x"
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
