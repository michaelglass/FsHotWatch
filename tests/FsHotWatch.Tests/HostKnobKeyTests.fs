/// A variable that configures the host or the CLI around an evaluation, and never what the
/// evaluation produces, is not part of any key (AUTOMATION-1725). fshw injects
/// `MSBUILDDISABLENODEREUSE` into the dotnet children it starts, so keying it split one
/// evaluation across two start modes: the 11-15 audit's options keys differed in that one label
/// out of 1,150. The knobs are a named list, each with its reason, and a knob the evaluated files
/// themselves name is still keyed.
module FsHotWatch.Tests.HostKnobKeyTests

open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OptionsRecords

let private request: Request =
    { Root = "/r"
      Projects = [ "/r/src/A/A.fsproj" ]
      Toolchain = "toolchain"
      Sdk = "/sdk"
      GlobalProperties = [] }

let private inputsNaming (names: string list) : EvaluationInputs =
    { Reads = [ "/r/src/A/A.fsproj" ]
      Probes = []
      Listings = []
      EnvNames = names }

/// An environment that keys something of each kind: an SDK location, a referenced name, a prefix.
let private environment =
    [ "DOTNET_ROOT", "/sdk"
      "NUGET_PACKAGES", "/nuget"
      "HOME", "/home/u"
      "PATH", "/bin" ]

let private hostKnobNames = SessionScope.hostKnobs |> List.map fst

/// The names of `names` that move the options key when set. Every one is listed, so one list's
/// failure never hides another's.
let private movingTheOptionsKey (names: string list) =
    let inputs = inputsNaming [ "HOME" ]
    let key = optionsKey request inputs environment

    names
    |> List.filter (fun name -> optionsKey request inputs (environment @ [ name, "1" ]) <> key)

/// The names of `names` that move the test-evidence toolchain when set.
let private movingTheToolchain (names: string list) =
    let keyed = TestEvidenceKey.keyedEnvironment environment

    names
    |> List.filter (fun name -> TestEvidenceKey.keyedEnvironment (environment @ [ name, "1" ]) <> keyed)

[<Fact>]
let ``the host knobs are named, each with its reason, and include the two seen to split keys`` () =
    let names = SessionScope.hostKnobs |> List.map fst
    test <@ List.contains "MSBUILDDISABLENODEREUSE" names @>
    test <@ List.contains "DOTNET_CLI_USE_MSBUILD_SERVER" names @>
    test <@ List.distinct names = names @>
    test <@ SessionScope.hostKnobs |> List.forall (fun (_, why) -> why.Length > 10) @>

[<Fact>]
let ``an options key is the same with each host knob set or unset`` () =
    test <@ List.isEmpty (movingTheOptionsKey hostKnobNames) @>

[<Fact>]
let ``an options key is the same with each process-owned variable set or unset`` () =
    test <@ List.isEmpty (movingTheOptionsKey SessionScope.processOwned) @>

[<Fact>]
let ``a test-evidence toolchain is the same with each host knob set or unset`` () =
    test <@ List.isEmpty (movingTheToolchain hostKnobNames) @>

[<Fact>]
let ``a test-evidence toolchain is the same with each process-owned variable set or unset`` () =
    test <@ List.isEmpty (movingTheToolchain SessionScope.processOwned) @>

[<Fact>]
let ``a variable an evaluation reads still moves both keys`` () =
    // Negative controls: the exclusion is the named list, not the prefixes.
    let inputs = inputsNaming [ "HOME" ]
    let key = optionsKey request inputs environment

    let moved (name: string) (value: string) =
        environment
        |> List.filter (fun (n, _) -> n <> name)
        |> List.append [ name, value ]

    test <@ optionsKey request inputs (moved "DOTNET_ROOT" "/other-sdk") <> key @>
    test <@ optionsKey request inputs (moved "HOME" "/home/v") <> key @>

    test
        <@
            optionsKey request inputs (environment @ [ "MSBUILDFAILONDRIVEENUMERATINGWILDCARD", "1" ])
            <> key
        @>

    let keyed = TestEvidenceKey.keyedEnvironment environment
    test <@ TestEvidenceKey.keyedEnvironment (moved "DOTNET_ROOT" "/other-sdk") <> keyed @>

    test
        <@
            TestEvidenceKey.keyedEnvironment (environment @ [ "Configuration", "Release" ])
            <> keyed
        @>

[<Fact>]
let ``a host knob the evaluated files name is keyed like any variable they name`` () =
    let inputs = inputsNaming [ "HOME"; "MSBUILDTERMINALLOGGER" ]

    let off =
        optionsKey request inputs (environment @ [ "MSBUILDTERMINALLOGGER", "off" ])

    let on = optionsKey request inputs (environment @ [ "MSBUILDTERMINALLOGGER", "on" ])
    test <@ off <> on @>

// --- the toolchain negative control ------------------------------------------------------
//
// DOTNET_HOST_PATH, MSBUILD_EXE_PATH, MSBuildExtensionsPath and MSBuildSDKsPath say WHICH
// toolchain evaluates. Leaving them out of the key is safe only because the key names that
// toolchain through its own inputs: `sdk` (the resolved MSBuild's location), `toolchain` (the
// loaded Microsoft.Build's identity, whose MVID differs per build: its assembly version is
// 15.1.0.0 in every SDK) and the SDK props and targets an evaluation imports (`read:` labels, by
// content). Here the four are present and EQUAL on both sides, the worst case, and the key still
// tells the toolchains apart.

/// The four process-owned variables, at the same values for both evaluations.
let private sameProcessOwned =
    [ "DOTNET_HOST_PATH", "/usr/local/share/dotnet/dotnet"
      "MSBUILD_EXE_PATH", "/usr/local/share/dotnet/sdk/current/MSBuild.dll"
      "MSBuildExtensionsPath", "/usr/local/share/dotnet/sdk/current/"
      "MSBuildSDKsPath", "/usr/local/share/dotnet/sdk/current/Sdks" ]

/// Microsoft.Build's identity as `CheckCache.assemblyIdentity` gives it, with the MVIDs measured
/// in the SDKs installed on the laptop, 2026-11-15 (10.0.400: file 18.9.6.38015; 10.0.401: file
/// 18.9.11.42413).
let private msbuild10_0_400 =
    "Microsoft.Build@15.1.0.0/15c2ac97-ef2c-45ee-975e-b4150e1c1275"

let private msbuild10_0_401 =
    "Microsoft.Build@15.1.0.0/31676592-ab40-4933-a875-d74ab6defcd2"

let private requestUnder (sdk: string) (msbuild: string) : Request =
    { request with
        Sdk = sdk
        Toolchain =
            OptionsInputs.toolchainOf "Ionide.ProjInfo@0.74.2.0/ionide" msbuild OptionsInputs.OptionsRecordSchema }

[<Fact>]
let ``another SDK gives another options key through the key's own inputs, the process-owned variables excluded`` () =
    let env = environment @ sameProcessOwned
    let inputs = inputsNaming [ "HOME" ]

    let under400 =
        optionsKey (requestUnder "/dotnet/sdk/10.0.400/MSBuild.dll" msbuild10_0_400) inputs env

    // A different SDK, at a different location, with a different MSBuild.
    let under401 =
        optionsKey (requestUnder "/dotnet/sdk/10.0.401/MSBuild.dll" msbuild10_0_401) inputs env

    test <@ under401 <> under400 @>

    // The same location upgraded in place: only the loaded MSBuild's identity differs.
    let inPlace =
        optionsKey (requestUnder "/dotnet/sdk/10.0.400/MSBuild.dll" msbuild10_0_401) inputs env

    test <@ inPlace <> under400 @>

[<Fact>]
let ``an SDK file evaluation imports moves the options key when its bytes change in place`` () =
    // An SDK whose MSBuild is unchanged but whose props or targets differ: the import is a read,
    // keyed by its content, at the same path.
    TestHelpers.withTempDir "sdk-targets" (fun dir ->
        let targets = System.IO.Path.Combine(dir, "Sdk.targets")
        System.IO.File.WriteAllText(targets, "<Project />")
        let env = environment @ sameProcessOwned

        let inputs =
            { inputsNaming [ "HOME" ] with
                Reads = [ "/r/src/A/A.fsproj"; targets ] }

        let req = requestUnder "/dotnet/sdk/10.0.400/MSBuild.dll" msbuild10_0_400
        let before = optionsKey req inputs env
        System.IO.File.WriteAllText(targets, "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>")
        let after = optionsKey req inputs env
        test <@ after <> before @>)
