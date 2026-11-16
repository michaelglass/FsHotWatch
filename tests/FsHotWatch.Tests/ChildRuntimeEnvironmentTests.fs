/// A runtime setting in the environment fshw spawns from either never reaches the process it
/// starts, or moves the test-evidence key with its value (AUTOMATION-1749). Without that, a result
/// recorded under one setting is replayed under another: AUTOMATION-1622's A2 arm ran fshw's host
/// with `DOTNET_ROLL_FORWARD=LatestMajor`, its test hosts rolled forward to .NET 11 too, and a
/// .NET 11 change failed an app test (AUTOMATION-1748).
///
/// Each case starts a real child through `ProcessHelper.runProcess`, under a session environment
/// holding the variable (as a repository host spawns from its client's environment), so the
/// environment observed is the one the spawn code builds, not one a list predicts.
module FsHotWatch.Tests.ChildRuntimeEnvironmentTests

open System
open System.Collections
open Xunit
open Swensen.Unquote
open FsHotWatch

let private ofProcess () : Map<string, string> =
    Map.ofList
        [ for entry in Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry> ->
              string entry.Key, string entry.Value ]

/// The environment a child gets from `runProcess`, under a session holding `name=value`, and the
/// keyed environment the test-evidence key reads under that same session.
let private spawnedUnder (name: string) (value: string) : (string * string) list * string list =
    let session =
        SessionScope.SessionEnvironment.create "/" (ofProcess () |> Map.add name value)

    use _ = SessionScope.SessionEnvironment.install session
    let quick = ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0)

    let child =
        match ProcessHelper.runProcess "/bin/sh" "-c \"/usr/bin/env -0 || dotnet build\"" "/" [] quick with
        | ProcessHelper.Succeeded(ProcessHelper.ProcessOutput.Drained text) ->
            [ for entry in text.Split('\000', StringSplitOptions.RemoveEmptyEntries) do
                  match entry.IndexOf '=' with
                  | -1 -> ()
                  | at -> entry.Substring(0, at), entry.Substring(at + 1) ]
        | other -> failwith $"the child that prints its environment did not finish cleanly: %A{other}"

    child, TestEvidenceKey.childKeyedEnvironment ()

/// Each runtime setting with two values and what the ticket decided for it.
let cases: obj[] list =
    [ "DOTNET_ROLL_FORWARD", "LatestMajor", "Minor"
      "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1", "0"
      "DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX", "2", "0"
      "DOTNET_gcServer", "1", "0"
      "DOTNET_GCHeapHardLimit", "0x10000000", "0x20000000"
      "DOTNET_GCgen0size", "0x4000000", "0x8000000"
      "COMPlus_gcServer", "1", "0"
      "COMPlus_GCHeapHardLimit", "0x10000000", "0x20000000"
      "DOTNET_TieredCompilation", "0", "1"
      "DOTNET_TC_QuickJitForLoops", "0", "1"
      "DOTNET_TieredPGO", "0", "1"
      "DOTNET_ReadyToRun", "0", "1"
      "COMPlus_TieredCompilation", "0", "1"
      "DOTNET_ROOT", "/opt/a", "/opt/b"
      "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1", "0"
      "DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY", "1", "0"
      "DOTNET_ROOT_X64", "/opt/a", "/opt/b"
      "MSBuildSDKsPath", "/opt/a", "/opt/b" ]
    |> List.map (fun (name, a, b) -> [| box name; box a; box b |])

[<Theory(Timeout = 60000)>]
[<MemberData(nameof cases)>]
let ``a runtime setting never reaches a child, or moves the test-evidence key with its value``
    (name: string)
    (a: string)
    (b: string)
    =
    let childA, keyA = spawnedUnder name a
    let childB, keyB = spawnedUnder name b
    let reached = childA |> List.exists (fun (k, _) -> k = name)
    // Stripped, or keyed: the two values give two keys.
    test <@ (name, not reached || keyA <> keyB) = (name, true) @>

[<Fact(Timeout = 60000)>]
let ``the test-evidence key reads the environment a child gets, not this process's`` () =
    let child, keyed = spawnedUnder "COMPlus_gcServer" "1"
    test <@ keyed = TestEvidenceKey.keyedEnvironment child @>
