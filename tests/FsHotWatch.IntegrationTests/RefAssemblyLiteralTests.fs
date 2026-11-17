/// AUTOMATION-1759: a changed `[<Literal>]` reaches the projects that inline it. The F# compiler
/// can give a changed reference assembly the old module version id (a literal's value in a file
/// that 32 or more files follow), and MSBuild's `CopyRefAssembly` copies `obj/ref` only on an
/// id change, so a dependent kept compiling against the old value and was never rebuilt. The
/// repository's Directory.Build.targets makes `obj/ref` follow `obj/refint` by content; this is
/// the reproduction, built twice with that file in place.
module FsHotWatch.Tests.RefAssemblyLiteralTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

/// Files after the one holding the literal: enough to shift it out of the signature hash.
let private followingFiles = 33

let private library =
    let compiles =
        [ 1..followingFiles ]
        |> List.map (fun i -> $"    <Compile Include=\"F%02d{i}.fs\" />")
        |> String.concat "\n"

    $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ProduceReferenceAssembly>true</ProduceReferenceAssembly>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="F00.fs" />
%s{compiles}
  </ItemGroup>
</Project>
"""

let private app =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="App.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.fsproj" />
  </ItemGroup>
</Project>
"""

let private write (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private literal (root: string) (value: string) =
    write root "Lib/F00.fs" $"module F00\n\n[<Literal>]\nlet Schema = \"%s{value}\"\n"

/// The repository's own Directory.Build.targets, the file under test.
let private repositoryTargets =
    FsHotWatch.TestRoots.RepositoryTree.path "Directory.Build.targets"

let private dotnet clock (root: string) (args: string list) =
    let code, text = run clock root "dotnet" (args @ [ "--disable-build-servers" ])
    let command = String.concat " " args
    Assert.True((code = 0), $"dotnet %s{command}: %s{text}")
    text

[<Fact(Timeout = 900000)>]
let ``a changed literal reaches a project that inlines it, in an incremental build`` () =
    let clock = budget ()

    withTempDir "ref-assembly-literal" (fun root ->
        File.Copy(repositoryTargets, Path.Combine(root, "Directory.Build.targets"))
        write root "Lib/Lib.fsproj" library

        for i in 1..followingFiles do
            write root $"Lib/F%02d{i}.fs" $"module F%02d{i}\nlet g%d{i} (x: int) = x + %d{i}\n"

        write root "App/App.fsproj" app
        write root "App/App.fs" "printfn \"schema=%s\" F00.Schema\n"

        literal root "v1"
        dotnet clock root [ "build"; "App/App.fsproj" ] |> ignore
        let first = dotnet clock root [ "run"; "--project"; "App/App.fsproj"; "--no-build" ]
        test <@ first.Contains "schema=v1" @>

        // Only the literal's value changes: the reference assembly's bytes change, its id may not.
        literal root "v2"
        dotnet clock root [ "build"; "App/App.fsproj" ] |> ignore

        let second =
            dotnet clock root [ "run"; "--project"; "App/App.fsproj"; "--no-build" ]

        test <@ second.Contains "schema=v2" @>)
