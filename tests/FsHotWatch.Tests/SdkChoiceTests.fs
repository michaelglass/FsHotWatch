/// Which SDK's MSBuild the daemon evaluates projects with (AUTOMATION-1621).
///
/// MSBuild runs inside the daemon, so it must be one this process's runtime can host. A
/// .NET 11 SDK's MSBuild needs System.Runtime 11 and fails every evaluation in a .NET 10
/// daemon with a FileNotFoundException. On GitHub's ubuntu-26.04 image 20261111.196 that
/// reddened every test that loads a project in a scratch repository with no `global.json`.
/// None of these tests needs a .NET 11 SDK installed: the installed list is injected.
module FsHotWatch.Tests.SdkChoiceTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Ionide.ProjInfo
open FsHotWatch
open FsHotWatch.SdkChoice

let private sdk (version: string) : Sdk =
    { Version = SemanticVersioning.Version version
      Path = Path.Combine("/sdks", version) }

let private v (version: string) = SemanticVersioning.Version version

let private installed =
    [ sdk "10.0.302"; sdk "10.0.404"; sdk "11.0.100-preview.1.25" ]

let private versionOf (choice: Choice) =
    match choice with
    | Resolved chosen -> Some(string chosen.Version)
    | Substituted(chosen, _) -> Some(string chosen.Version)
    | Refused _ -> None

[<Fact>]
let ``a net10 host never evaluates with an 11.x SDK: with no global.json it takes the newest 10.x`` () =
    let choice = choose 10 None (v "11.0.100-preview.1.25") installed

    test <@ choice = Substituted(sdk "10.0.404", v "11.0.100-preview.1.25") @>

[<Fact>]
let ``with only newer-major SDKs installed and no global.json, the refusal names both versions and the global.json fix``
    ()
    =
    match choose 10 None (v "11.0.100-preview.1.25") [ sdk "11.0.100-preview.1.25" ] with
    | Refused reason ->
        test <@ reason.Contains "11.0.100-preview.1.25" @>
        test <@ reason.Contains ".NET 10" @>
        test <@ reason.Contains "global.json" @>
    | other -> failwith $"expected a refusal, got %A{other}"

[<Fact>]
let ``a global.json that selects a newer-major SDK is refused, naming both versions and that file, never overridden``
    ()
    =
    match choose 10 (Some "/repo/global.json") (v "11.0.100-preview.1.25") installed with
    | Refused reason ->
        test <@ reason.Contains "11.0.100-preview.1.25" @>
        test <@ reason.Contains ".NET 10" @>
        test <@ reason.Contains "/repo/global.json" @>
    | other -> failwith $"expected a refusal, got %A{other}"

[<Fact>]
let ``positive control: the SDK dotnet resolves is used as is when this runtime can host it`` () =
    // global.json selected 10.0.302 (its version and rollForward, applied by `dotnet --version`):
    // a newer installed 10.x must not override it.
    test <@ choose 10 (Some "/repo/global.json") (v "10.0.302") installed = Resolved(sdk "10.0.302") @>
    test <@ choose 10 None (v "10.0.404") installed = Resolved(sdk "10.0.404") @>

[<Fact>]
let ``a resolved version with no installed SDK at or above it is refused, as Ionide refuses it`` () =
    test <@ versionOf (choose 10 None (v "10.0.500") [ sdk "10.0.302" ]) = None @>

/// A scratch checkout whose global.json asks for 10.0.100 and rolls forward within the
/// 10.0 band: `dotnet` resolves an installed 10.0.x at or above it, and the choice is that
/// SDK, unchanged — global.json and its rollForward still decide.
[<Fact>]
let ``positive control: a real global.json's rollForward still decides the SDK`` () =
    let dir = Directory.CreateTempSubdirectory "fshw-sdkchoice-"

    try
        File.WriteAllText(
            Path.Combine(dir.FullName, "global.json"),
            """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }"""
        )

        let exe =
            match Paths.dotnetRoot.Value with
            | Some muxer -> FileInfo(ProcessHelper.installedDotnet muxer.FullName)
            | None -> failwith "no dotnet muxer on this host"

        let choice = forCheckout Environment.Version.Major exe dir.FullName

        match choice with
        | Resolved chosen ->
            test <@ chosen.Version.Major = 10 && chosen.Version >= v "10.0.100" @>
            test <@ Directory.Exists chosen.Path @>
        | other -> failwith $"expected the global.json's own pick, got %A{other}"
    finally
        dir.Delete true

/// A global.json that pins an SDK no machine has, with no rollForward: `dotnet --version`
/// answers an error, not a version, and the choice is a refusal that says so rather than an
/// evaluation under some other SDK.
[<Fact>]
let ``a global.json pinning an SDK that is not installed is refused, naming the checkout`` () =
    let dir = Directory.CreateTempSubdirectory "fshw-sdkchoice-missing-"

    try
        File.WriteAllText(
            Path.Combine(dir.FullName, "global.json"),
            """{ "sdk": { "version": "10.0.999", "rollForward": "disable" } }"""
        )

        let exe =
            match Paths.dotnetRoot.Value with
            | Some muxer -> FileInfo(ProcessHelper.installedDotnet muxer.FullName)
            | None -> failwith "no dotnet muxer on this host"

        match forCheckout Environment.Version.Major exe dir.FullName with
        | Refused reason ->
            test <@ reason.Contains "did not answer a version" @>
            test <@ reason.Contains dir.FullName @>
        | other -> failwith $"expected a refusal, got %A{other}"
    finally
        dir.Delete true
