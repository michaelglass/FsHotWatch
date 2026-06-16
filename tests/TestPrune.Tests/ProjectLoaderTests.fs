module TestPrune.Tests.ProjectLoaderTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.ProjectLoader

let private writeTempFsproj (content: string) =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore
    let fsprojPath = Path.Combine(tmpDir, "Test.fsproj")
    File.WriteAllText(fsprojPath, content)
    (tmpDir, fsprojPath)

module ``parseProjectFile`` =

    [<Fact>]
    let ``extracts compile items in order`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Alpha.fs" />
    <Compile Include="Beta.fs" />
    <Compile Include="Gamma.fs" />
  </ItemGroup>
</Project>"""

        try
            let compileItems, _ = parseProjectFile fsprojPath

            let expected =
                [ Path.GetFullPath(Path.Combine(tmpDir, "Alpha.fs"))
                  Path.GetFullPath(Path.Combine(tmpDir, "Beta.fs"))
                  Path.GetFullPath(Path.Combine(tmpDir, "Gamma.fs")) ]

            test <@ compileItems = expected @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``extracts project references`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../Other/Other.fsproj" />
  </ItemGroup>
</Project>"""

        try
            let _, projectRefs = parseProjectFile fsprojPath
            let expected = [ Path.GetFullPath(Path.Combine(tmpDir, "../Other/Other.fsproj")) ]

            test <@ projectRefs = expected @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``skips Compile elements without Include attribute`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="A.fs" />
    <Compile />
    <Compile Include="B.fs" />
  </ItemGroup>
</Project>"""

        try
            let compileItems, _ = parseProjectFile fsprojPath
            let names = compileItems |> List.map Path.GetFileName
            test <@ names = [ "A.fs"; "B.fs" ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``skips ProjectReference elements without Include attribute`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../Other/Other.fsproj" />
    <ProjectReference />
  </ItemGroup>
</Project>"""

        try
            let _, projectRefs = parseProjectFile fsprojPath
            test <@ projectRefs.Length = 1 @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``handles empty project file`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""

        try
            let compileItems, projectRefs = parseProjectFile fsprojPath
            test <@ compileItems |> List.isEmpty @>
            test <@ projectRefs |> List.isEmpty @>
        finally
            Directory.Delete(tmpDir, true)

module ``parsePackageReferences`` =

    [<Fact>]
    let ``extracts direct PackageReference versions (non-CPM, e.g. intelligence)`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" Version="0.7.0" />
    <PackageReference Include="FSharp.Core" Version="10.1.0" />
  </ItemGroup>
</Project>"""

        try
            let pkgs = parsePackageReferences fsprojPath
            test <@ pkgs = [ ("CommandTree", "0.7.0"); ("FSharp.Core", "10.1.0") ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``resolves CPM versions from an ancestor Directory.Packages.props`` () =
        // Project under <tmp>/proj/, CPM props at <tmp>/Directory.Packages.props.
        let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let projDir = Path.Combine(tmpDir, "proj")
        Directory.CreateDirectory(projDir) |> ignore

        File.WriteAllText(
            Path.Combine(tmpDir, "Directory.Packages.props"),
            """<Project>
  <ItemGroup>
    <PackageVersion Include="CommandTree" Version="0.6.3" />
    <PackageVersion Include="FSharp.Core" Version="10.1.0" />
  </ItemGroup>
</Project>"""
        )

        let fsprojPath = Path.Combine(projDir, "Proj.fsproj")

        File.WriteAllText(
            fsprojPath,
            """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" />
    <PackageReference Include="FSharp.Core" />
  </ItemGroup>
</Project>"""
        )

        try
            let pkgs = parsePackageReferences fsprojPath
            test <@ pkgs = [ ("CommandTree", "0.6.3"); ("FSharp.Core", "10.1.0") ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``a CPM bump in Directory.Packages.props changes the parsed version`` () =
        // The acceptance mechanism: the same .fsproj resolves a different version
        // after the central props file bumps the package.
        let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        let projDir = Path.Combine(tmpDir, "proj")
        Directory.CreateDirectory(projDir) |> ignore
        let propsPath = Path.Combine(tmpDir, "Directory.Packages.props")
        let fsprojPath = Path.Combine(projDir, "Proj.fsproj")

        File.WriteAllText(
            fsprojPath,
            """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" />
  </ItemGroup>
</Project>"""
        )

        try
            File.WriteAllText(
                propsPath,
                """<Project><ItemGroup><PackageVersion Include="CommandTree" Version="0.6.3" /></ItemGroup></Project>"""
            )

            let before = parsePackageReferences fsprojPath

            File.WriteAllText(
                propsPath,
                """<Project><ItemGroup><PackageVersion Include="CommandTree" Version="0.7.0" /></ItemGroup></Project>"""
            )

            let after = parsePackageReferences fsprojPath
            test <@ before = [ ("CommandTree", "0.6.3") ] @>
            test <@ after = [ ("CommandTree", "0.7.0") ] @>
        finally
            Directory.Delete(tmpDir, true)

    [<Fact>]
    let ``an unresolved CPM reference is emitted as "unresolved" (deterministic)`` () =
        let tmpDir, fsprojPath =
            writeTempFsproj
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="NoVersionAnywhere" />
  </ItemGroup>
</Project>"""

        try
            let pkgs = parsePackageReferences fsprojPath
            test <@ pkgs = [ ("NoVersionAnywhere", "unresolved") ] @>
        finally
            Directory.Delete(tmpDir, true)
