module TestPrune.Tests.AttributesFSharpCoreTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote

// TestPrune uses Central Package Management, under which the F# SDK sets
// DisableImplicitFSharpCoreReference=true. A project that does not reference FSharp.Core
// explicitly then restores with NO FSharp.Core at all: its design-time build hands FCS zero
// FSharp.Core references (FCS silently substitutes its host's FSharp.Core, which for a
// net10.0-hosted checker yields false errors on netstandard2.0 such as "The type 'string' is
// not defined"), and `dotnet pack` emits dependency groups with no FSharp.Core even though
// the compiled assembly references it. Restore output is what both consumers read, so assert
// on it rather than on the project file's text.

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: no TestPrune.slnx in any ancestor of the test binary"
        elif File.Exists(Path.Combine(directory.FullName, "TestPrune.slnx")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private assetsPath () =
    Path.Combine(repoRoot (), "src", "TestPrune.Attributes", "obj", "project.assets.json")

let private tryProperty (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value -> Some value
    | false, _ -> None

[<Theory>]
// Floors are deliberately LOW: a PackageReference version is a minimum, and this package is
// meant to be referenced from any F# project without raising that project's FSharp.Core.
[<InlineData("netstandard2.0", "6.0.7")>]
[<InlineData("net10.0", "10.1.201")>]
let ``TestPrune.Attributes restores an explicit low FSharp.Core floor per target framework``
    (targetFramework: string, expectedVersion: string)
    =
    let path = assetsPath ()

    // A missing assets file means the solution was not restored, not that the reference is
    // fine: fail rather than pass vacuously.
    test <@ File.Exists path @>

    use assets = JsonDocument.Parse(File.ReadAllText path)

    let declaredRange =
        assets.RootElement
            .GetProperty("project")
            .GetProperty("frameworks")
            .GetProperty(targetFramework)
            .GetProperty("dependencies")
        |> tryProperty "FSharp.Core"
        |> Option.map (fun dependency -> dependency.GetProperty("version").GetString())

    test <@ declaredRange = Some $"[{expectedVersion}, )" @>

    let compileAssets =
        assets.RootElement.GetProperty("targets").GetProperty(targetFramework)
        |> tryProperty $"FSharp.Core/{expectedVersion}"
        |> Option.map (fun library ->
            library.GetProperty("compile").EnumerateObject()
            |> Seq.map (fun asset -> asset.Name)
            |> Seq.toList)
        |> Option.defaultValue []

    test <@ compileAssets |> List.exists (fun asset -> asset.EndsWith("/FSharp.Core.dll")) @>
