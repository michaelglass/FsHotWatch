/// AUTOMATION-1478 against the real compiler: one library built by `dotnet build` in two
/// checkouts has one `OutputIdentity`, though each build embeds its own checkout's path,
/// and an edit to what the library declares gives it another.
module FsHotWatch.Tests.OutputIdentityBuildTests

open System
open System.IO
open Xunit
open FsHotWatch
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

let private library =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="A.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

// A record, a union, an inline function (optimization data) and an incomplete match (whose
// failure names this file's absolute path in a method body).
let private source =
    """module Lib.A

type Point = { X: int; Y: int }
type Shape = Circle of int | Square of int
let inline twice x = x * 2
let describe (shape: Shape) =
    match shape with
    | Circle r -> $"circle {r}"
"""

let private build clock (root: string) =
    let code, text =
        run clock root "dotnet" [ "build"; "Lib/Lib.fsproj"; "--disable-build-servers" ]

    Assert.True((code = 0), text)
    Path.Combine(root, "Lib", "bin", "Debug", "net10.0", "Lib.dll")

let private checkout (root: string) =
    Directory.CreateDirectory(Path.Combine(root, "Lib")) |> ignore
    File.WriteAllText(Path.Combine(root, "Lib", "Lib.fsproj"), library)
    File.WriteAllText(Path.Combine(root, "Lib", "A.fs"), source)

let private identity (root: string) (dll: string) =
    let plain (path: string) =
        Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

    OutputIdentity.identity (Some root) plain dll

[<Fact(Timeout = 900000)>]
let ``one library built in two checkouts has one identity, and an edit to it another`` () =
    let clock = budget ()

    withTempDir "output-identity-build" (fun dir ->
        // Roots of different lengths, so a path's pickled length differs between them.
        let short = Path.Combine(dir, "c")
        let long = Path.Combine(dir, "checkout-" + String('w', 90))
        checkout short
        checkout long
        let shortDll = build clock short
        let longDll = build clock long

        // Positive control: the builds' bytes differ, each naming its own checkout.
        Assert.NotEqual<byte[]>(File.ReadAllBytes shortDll, File.ReadAllBytes longDll)
        let named = identity short shortDll
        Assert.StartsWith(OutputIdentity.Schema + ":", named)
        Assert.Equal(named, identity long longDll)

        // A type abbreviation lives only in the F# signature data: no IL changes.
        File.AppendAllText(Path.Combine(long, "Lib", "A.fs"), "type Length = int\n")
        let rebuilt = build clock long
        Assert.NotEqual<string>(named, identity long rebuilt))
