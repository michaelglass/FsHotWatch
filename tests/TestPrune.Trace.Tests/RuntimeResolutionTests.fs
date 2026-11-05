/// Predicting a traced run's environment fingerprint before it starts: which runtime its
/// apphost will load, and the fingerprint the run will then record (plan Task 3, D4).
module TestPrune.Trace.Tests.RuntimeResolutionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.RuntimeResolution

let private installedSet =
    [ "8.0.8"; "8.0.11"; "9.0.1"; "10.0.0"; "10.0.2"; "11.0.0-preview.1.25" ]

[<Theory>]
[<InlineData("10.0.0", "Minor", "10.0.2")>] // same major.minor: its latest patch
[<InlineData("8.0.0", "Minor", "8.0.11")>]
[<InlineData("8.1.0", "Minor", null)>] // no 8.x at or above 8.1 installed
[<InlineData("9.0.0", "LatestPatch", "9.0.1")>]
[<InlineData("9.2.0", "LatestPatch", null)>]
[<InlineData("8.0.0", "Major", "8.0.11")>]
[<InlineData("7.0.0", "Major", "8.0.11")>] // the lowest higher major, at its latest patch
[<InlineData("8.0.0", "LatestMinor", "8.0.11")>]
[<InlineData("8.0.0", "LatestMajor", "10.0.2")>] // releases only, while one satisfies
[<InlineData("10.0.0", "Disable", "10.0.0")>]
[<InlineData("10.0.1", "Disable", null)>]
[<InlineData("10.0.0", "sometimes", null)>] // a policy the host does not know resolves nothing
let ``roll-forward picks the runtime the host would`` (requested: string, rollForward: string, expected: string) =
    test <@ resolve requested rollForward installedSet = Option.ofObj expected @>

[<Fact>]
let ``a prerelease is chosen only when the request is a prerelease`` () =
    test <@ resolve "11.0.0-preview.1.25" "Minor" installedSet = Some "11.0.0-preview.1.25" @>
    test <@ resolve "11.0.0" "Minor" installedSet = None @>
    test <@ resolve "not-a-version" "Minor" installedSet = None @>

[<Fact>]
let ``runtimeconfig framework and rollForward are read`` () =
    let json =
        """{"runtimeOptions":{"tfm":"net10.0","rollForward":"Major","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"""

    test <@ requestedFramework json = Some("10.0.0", Some "Major") @>

[<Fact>]
let ``an app with several frameworks is keyed on Microsoft.NETCore.App`` () =
    let json =
        """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.AspNetCore.App","version":"10.0.0"},{"name":"Microsoft.NETCore.App","version":"10.0.1"}]}}"""

    test <@ requestedFramework json = Some("10.0.1", None) @>
    test <@ requestedFramework """{"runtimeOptions":{}}""" = None @>

[<Fact>]
let ``the runtimeconfig's rollForward wins over DOTNET_ROLL_FORWARD, which wins over Minor`` () =
    let dir = Directory.CreateTempSubdirectory("tp-rr-").FullName

    try
        for v in installedSet do
            Directory.CreateDirectory(Path.Combine(dir, "shared", "Microsoft.NETCore.App", v))
            |> ignore

        test <@ installed dir = List.sort installedSet @>
        let config = Path.Combine(dir, "app.runtimeconfig.json")

        File.WriteAllText(
            config,
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}"""
        )

        test <@ predictRuntime dir config None = Some ".NET 8.0.11" @>
        test <@ predictRuntime dir config (Some "LatestMajor") = Some ".NET 10.0.2" @>

        File.WriteAllText(
            config,
            """{"runtimeOptions":{"rollForward":"Disable","framework":{"name":"Microsoft.NETCore.App","version":"8.0.8"}}}"""
        )

        test <@ predictRuntime dir config (Some "LatestMajor") = Some ".NET 8.0.8" @>
        test <@ predictRuntime dir (Path.Combine(dir, "missing.json")) None = None @>
        test <@ installed (Path.Combine(dir, "nowhere")) |> List.isEmpty @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``the description is the recorder's for this process`` () =
    let running = Environment.Version.ToString 3
    test <@ describe running = TestPrune.Trace.Recorder.RuntimeIdentity.runtime () @>

/// A project directory holding one build output: `bin/Debug/net10.0/App.dll`, its deps.json
/// and a runtimeconfig asking for this process's runtime.
let private withBuild (f: string -> string -> unit) =
    let root = Directory.CreateTempSubdirectory("tp-predict-").FullName

    try
        let out = Path.Combine(root, "app", "bin", "Debug", "net10.0")
        Directory.CreateDirectory out |> ignore
        File.WriteAllText(Path.Combine(out, "App.dll"), "")
        File.WriteAllText(Path.Combine(out, "App.deps.json"), """{"libraries":{}}""")

        File.WriteAllText(
            Path.Combine(out, "App.runtimeconfig.json"),
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"""
            + "\""
            + Environment.Version.ToString 3
            + "\"}}}"
        )

        File.WriteAllText(Path.Combine(root, "global.json"), "{}")
        f root (Path.Combine(root, "app"))
    finally
        Directory.Delete(root, true)

/// The runtime root this process runs on (`<root>/shared/Microsoft.NETCore.App/<v>/`).
let private thisDotnetRoot () =
    Path.GetFullPath(Path.Combine(Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))

let private request root projectDir : FingerprintPrediction.Request =
    { RepoRoot = root
      ProjectDir = projectDir
      AssemblyName = "App"
      Files = [ "global.json" ]
      Env = [ "HOME" ]
      WeaveTests = SitesOnly
      DotnetRoot = thisDotnetRoot ()
      RollForwardEnv = None }

[<Fact>]
let ``the predicted fingerprint is the one ingestion gathers from a dump of this runtime`` () =
    withBuild (fun root projectDir ->
        let predicted = FingerprintPrediction.predict (request root projectDir)

        let dump: ProcessDump =
            { Pid = 1
              ParentScope = None
              Runtime = TestPrune.Trace.Recorder.RuntimeIdentity.runtime ()
              Os = TestPrune.Trace.Recorder.RuntimeIdentity.os ()
              Arch = TestPrune.Trace.Recorder.RuntimeIdentity.arch ()
              IdCount = 0
              CpuMs = 0L
              Counters =
                { Test = 0L
                  Class = 0L
                  Collection = 0L
                  Assembly = 0L
                  Override = 0L
                  StaticInit = 0L
                  Ambient = 0L
                  Overflow = 0L }
              Scopes = [] }

        let deps =
            FingerprintPrediction.depsJsonSha256 (
                File.ReadAllText(Path.Combine(projectDir, "bin", "Debug", "net10.0", "App.deps.json"))
            )

        let gathered =
            Fingerprint.gather root [ "global.json" ] [ "HOME" ] dump deps SitesOnly

        test <@ predicted = Ok gathered @>)

[<Fact>]
let ``no build output, two target frameworks, or no resolvable runtime predict nothing, and say why`` () =
    withBuild (fun root projectDir ->
        let r = request root projectDir

        test
            <@
                match FingerprintPrediction.predict { r with AssemblyName = "Other" } with
                | Error why -> why.StartsWith "no build output"
                | Ok _ -> false
            @>

        test
            <@
                match FingerprintPrediction.predict { r with DotnetRoot = root } with
                | Error why -> why.Contains "runtime"
                | Ok _ -> false
            @>

        let second = Path.Combine(projectDir, "bin", "Debug", "net9.0")
        Directory.CreateDirectory second |> ignore
        File.WriteAllText(Path.Combine(second, "App.dll"), "")

        test
            <@
                match FingerprintPrediction.predict r with
                | Error why -> why.StartsWith "more than one target framework"
                | Ok _ -> false
            @>)

[<Fact>]
let ``a build without a deps.json predicts nothing`` () =
    withBuild (fun root projectDir ->
        File.Delete(Path.Combine(projectDir, "bin", "Debug", "net10.0", "App.deps.json"))

        test
            <@
                match FingerprintPrediction.predict (request root projectDir) with
                | Error why -> why.StartsWith "no deps.json at "
                | Ok _ -> false
            @>)
