/// A key computed in this process equals the key computed under the environment fshw gives a
/// process it launches (AUTOMATION-1737). The defect this guards against is fshw injecting a
/// variable itself: `MSBUILDDISABLENODEREUSE`, which fshw sets for every dotnet child, was keyed
/// and split one evaluation across the ways a daemon is started (AUTOMATION-1725), as
/// `DOTNET_INTERNAL_ThreadSuspendInjection` from the detached launcher had before
/// (AUTOMATION-1438). Each environment here is built by the code that launches the process, so a
/// variable a launcher starts injecting goes red here, whatever any list says.
module FsHotWatch.Tests.InjectedEnvironmentKeyTests

open System
open System.Collections
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

/// Inputs whose files name no variable: only the prefixes and the lists decide.
let private inputs: EvaluationInputs =
    { Reads = [ "/r/src/A/A.fsproj" ]
      Probes = []
      Listings = []
      EnvNames = [] }

let private ofProcess () : (string * string) list =
    [ for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> ->
          string entry.Key, string entry.Value ]

let private ofStartInfo (psi: Diagnostics.ProcessStartInfo) : (string * string) list =
    [ for KeyValue(key, value) in psi.Environment -> key, value ]

/// The variables whose presence or value differs between `here` and `there`.
let private differing (here: (string * string) list) (there: (string * string) list) =
    let a = Map.ofList here
    let b = Map.ofList there

    Set.union (Set.ofSeq a.Keys) (Set.ofSeq b.Keys)
    |> Set.filter (fun k -> Map.tryFind k a <> Map.tryFind k b)
    |> Set.toList

/// Both keys agree between `here` and `there`; on a failure, the variables that differ are named.
let private sameKeys (launch: string) (here: (string * string) list) (there: (string * string) list) =
    let options = optionsKey request inputs here = optionsKey request inputs there

    let toolchain =
        TestEvidenceKey.keyedEnvironment here = TestEvidenceKey.keyedEnvironment there

    test <@ (launch, options, toolchain, differing here there) = (launch, true, true, differing here there) @>

/// The environment of a child `ProcessHelper.runProcess` starts with a command line that names
/// dotnet, so it gets everything fshw gives a dotnet child. The child prints its environment and
/// never reaches dotnet.
let private spawnedEnvironment () : (string * string) list =
    let quick = ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0)

    match ProcessHelper.runProcess "/bin/sh" "-c \"/usr/bin/env -0 || dotnet build\"" "/" [] quick with
    | ProcessHelper.Succeeded(ProcessHelper.ProcessOutput.Drained text) ->
        [ for entry in text.Split('\000', StringSplitOptions.RemoveEmptyEntries) do
              match entry.IndexOf '=' with
              | -1 -> ()
              | at -> entry.Substring(0, at), entry.Substring(at + 1) ]
    | other -> failwith $"the child that prints its environment did not finish cleanly: %A{other}"

[<Fact(Timeout = 60000)>]
let ``a dotnet child fshw spawns is keyed as this process is`` () =
    let there = spawnedEnvironment ()
    test <@ there |> List.exists (fun (k, _) -> k = "PATH") @>
    sameKeys "dotnet child" (ofProcess ()) there

[<Fact>]
let ``a daemon the detached launcher starts is keyed as this process is, on macOS and elsewhere`` () =
    let here = ofProcess ()

    for isMacOS in [ true; false ] do
        let psi =
            FsHotWatch.Cli.DetachedLaunch.helperStartInfo isMacOS (fun _ -> null) "/usr/bin/true" "/" "exit 0"

        sameKeys $"detached launch (macOS=%b{isMacOS})" here (ofStartInfo psi)

[<Fact>]
let ``the comparison sees a variable a launch injects`` () =
    // The negative control: a launcher that injected a keyed variable would fail the facts above.
    let here = ofProcess () |> List.filter (fun (k, _) -> k <> "DOTNET_FSHW_INJECTED")
    let there = here @ [ "DOTNET_FSHW_INJECTED", "1" ]
    test <@ optionsKey request inputs here <> optionsKey request inputs there @>
    test <@ TestEvidenceKey.keyedEnvironment here <> TestEvidenceKey.keyedEnvironment there @>
