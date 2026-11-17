/// AUTOMATION-1684, shadow only: the portable options shadow agrees across two checkouts of one
/// tree that differ only in whether they were built. A real MSBuild evaluation, through the
/// daemon's own options-records loader with `optionsRecordsPortable: "shadow"`, files the portable
/// record in checkout A, which was built (bin/ and obj/Debug/ written). Checkout B, restored but
/// never built, evaluates the same tree, and its shadow line must say the portable key matched
/// and the options agree. This is the positive control for the serving flip criterion's
/// built-versus-fresh pairs.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.OptionsShadowBuiltFreshTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

let private library =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

let private dotnet clock (root: string) (args: string list) =
    let code, text = run clock root "dotnet" (args @ [ "--disable-build-servers" ])
    let command = String.concat " " args
    Assert.True((code = 0), $"dotnet %s{command}: %s{text}")

/// A restored checkout of the one-project tree at `root`: its project file.
let private checkoutAt clock (root: string) =
    let dir = Path.Combine(root, "Lib")
    Directory.CreateDirectory dir |> ignore
    let project = Path.Combine(dir, "Lib.fsproj")
    File.WriteAllText(project, library)
    File.WriteAllText(Path.Combine(dir, "Lib.fs"), "module Lib\nlet answer = 42\n")
    dotnet clock root [ "restore"; project ]
    project

[<WallClock("real file times: setup must be older than the filer's racy window, a span of file-system time; the assertions are on the shadow's verdicts, never on a duration")>]
[<Fact(Timeout = 900000)>]
let ``the shadow agrees between a built checkout and a fresh one of the same tree`` () =
    let clock = budget ()

    withTempDir "options-shadow-built-fresh" (fun scratch ->
        // Portable roots are spelled as the file system spells them (macOS: /var -> /private/var).
        let scratch =
            match RepositoryIdentity.canonicalize scratch with
            | Ok path -> path.Value
            | Error e -> failwith $"%A{e}"

        let rootA = Path.Combine(scratch, "a")
        let rootB = Path.Combine(scratch, "b-a-longer-checkout")
        let projectA = checkoutAt clock rootA
        dotnet clock rootA [ "build"; projectA; "--no-restore" ]
        let projectB = checkoutAt clock rootB
        test <@ Directory.Exists(Path.Combine(rootA, "Lib", "bin")) @>
        test <@ not (Directory.Exists(Path.Combine(rootB, "Lib", "bin"))) @>
        Threading.Thread.Sleep(CheckCache.FileContentHasher.RacyWindow + TimeSpan.FromSeconds 1.0)

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(scratch, "store", "v1")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        let load (root: string) (project: string) =
            let loader =
                Daemon.Daemon.optionsRecordsLoaderOver id store root OptionsPortable.Mode.Shadow

            loader.LoadProjects [ project ] |> Seq.length

        test <@ load rootA projectA = 1 @>
        test <@ load rootB projectB = 1 @>

        let log = OptionsPortable.logPathFor store.Root

        let line =
            File.ReadAllLines log
            |> Array.filter (fun l -> l.Trim() <> "")
            |> Array.last
            |> JsonDocument.Parse

        let root = line.RootElement
        let said = root.GetRawText()
        let crossRoot = root.GetProperty("crossRoot").GetBoolean()
        let keyMatch = root.GetProperty("keyMatch").GetBoolean()
        let agree = root.GetProperty("agree").ValueKind = JsonValueKind.True
        // The line itself rides along, so a failure shows what moved or differed.
        test <@ (crossRoot, keyMatch, agree, said) = (true, true, true, said) @>)
