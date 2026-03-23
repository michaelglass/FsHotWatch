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
