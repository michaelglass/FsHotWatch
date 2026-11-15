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

/// Every variable that is no key's business: the named host knobs, and what the evaluating
/// process writes into its own environment.
let private unkeyed =
    (SessionScope.hostKnobs |> List.map fst) @ SessionScope.processOwned

[<Fact>]
let ``the host knobs are named, each with its reason, and include the two seen to split keys`` () =
    let names = SessionScope.hostKnobs |> List.map fst
    test <@ List.contains "MSBUILDDISABLENODEREUSE" names @>
    test <@ List.contains "DOTNET_CLI_USE_MSBUILD_SERVER" names @>
    test <@ List.distinct names = names @>
    test <@ SessionScope.hostKnobs |> List.forall (fun (_, why) -> why.Length > 10) @>

[<Fact>]
let ``an options key is the same with each host knob set or unset`` () =
    let inputs = inputsNaming [ "HOME" ]
    let key = optionsKey request inputs environment

    for name in unkeyed do
        let withKnob = optionsKey request inputs (environment @ [ name, "1" ])
        test <@ (name, withKnob) = (name, key) @>

[<Fact>]
let ``a test-evidence toolchain is the same with each host knob set or unset`` () =
    let keyed = TestEvidenceKey.keyedEnvironment environment

    for name in unkeyed do
        let withKnob = TestEvidenceKey.keyedEnvironment (environment @ [ name, "1" ])
        test <@ (name, withKnob) = (name, keyed) @>

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
