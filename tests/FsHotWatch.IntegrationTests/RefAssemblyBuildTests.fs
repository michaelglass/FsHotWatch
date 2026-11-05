/// AUTOMATION-1498 against the real compiler: a public signature change in an F# file that
/// 32 or more files follow does not change the library's reference-assembly MVID, so
/// MSBuild's `CopyRefAssembly` skips refreshing `obj/<cfg>/<tfm>/ref/`, and a dependent
/// compiles against the OLD signature. The build plugin repairs the published reference
/// assembly and builds again.
module FsHotWatch.Tests.RefAssemblyBuildTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

/// One file whose signature changes, followed by enough files to shift it out of the
/// compiler's 32-bit signature hash (`(acc <<< 1) + h + 631` per file).
let private followingFiles = 33

let private library =
    let compiles =
        [ 0..followingFiles ]
        |> List.map (fun i -> $"    <Compile Include=\"F%d{i}.fs\" />")
        |> String.concat "\n"

    $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
%s{compiles}
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

let private app =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="App.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Lib/Lib.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""

let private write (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private checkout (root: string) =
    write root "Lib/Lib.fsproj" library
    write root "Lib/F0.fs" "module F0\nlet f (a: int) = a\n"

    for i in 1..followingFiles do
        write root $"Lib/F%d{i}.fs" $"module F%d{i}\nlet g%d{i} (x: int) = x + %d{i}\n"

    write root "App/App.fsproj" app
    write root "App/App.fs" "module App\nlet g () = F0.f 1\n"

/// F0.f gains a parameter, and App calls it with two arguments.
let private changeSignature (root: string) =
    write root "Lib/F0.fs" "module F0\nlet f (a: int) (b: string) = a + b.Length\n"
    write root "App/App.fs" "module App\nlet g () = F0.f 1 \"x\"\n"

let private buildArgs = [ "build"; "App/App.fsproj"; "--disable-build-servers" ]

let private build clock (root: string) = run clock root "dotnet" buildArgs

let private refPair (root: string) =
    let obj = Path.Combine(root, "Lib", "obj", "Debug", "net10.0")
    Path.Combine(obj, "refint", "Lib.dll"), Path.Combine(obj, "ref", "Lib.dll")

[<Fact(Timeout = 900000)>]
let ``a signature change the reference-assembly MVID misses leaves the dependent on the old API`` () =
    // The positive control for the test below: under this SDK the defect is real, so the
    // repair is exercised rather than passing on a build that never went stale.
    let clock = budget ()

    withTempDir "ref-assembly-stale" (fun root ->
        checkout root
        let code, text = build clock root
        Assert.True((code = 0), text)

        changeSignature root
        let code, text = build clock root
        let intermediate, published = refPair root

        Assert.True((code <> 0 && text.Contains "FS0003"), $"expected FS0003 against the stale reference: %s{text}")
        test <@ File.ReadAllBytes intermediate <> File.ReadAllBytes published @>)

[<Fact(Timeout = 900000)>]
let ``the build plugin repairs the stale reference assembly and the build passes`` () =
    let clock = budget ()

    withTempDir "ref-assembly-repair" (fun root ->
        checkout root
        let code, text = build clock root
        Assert.True((code = 0), text)
        changeSignature root

        let graph = ProjectGraph()

        let project name =
            AbsProjectPath.create (Path.Combine(root, name, name + ".fsproj"))

        let lib = project "Lib"
        let appProject = project "App"

        let libSources =
            [ 0..followingFiles ]
            |> List.map (fun i -> AbsFilePath.create (Path.Combine(root, "Lib", $"F%d{i}.fs")))

        graph.RegisterProject(lib, libSources, [])
        graph.RegisterProject(appProject, [ AbsFilePath.create (Path.Combine(root, "App", "App.fs")) ], [ lib ])
        graph.RegisterProjectOutput(lib, Path.Combine(root, "Lib", "bin", "Debug", "net10.0", "Lib.dll"))
        graph.RegisterProjectOutput(appProject, Path.Combine(root, "App", "bin", "Debug", "net10.0", "App.dll"))

        let host = PluginHost.create (Unchecked.defaultof<_>) root

        host.RegisterHandler(BuildPlugin.create "dotnet" (String.concat " " buildArgs) [] graph [] None [] None)

        host.EmitFileChanged(SourceChanged [ Path.Combine(root, "Lib", "F0.fs") ])
        waitForTerminalStatus host "build" (remaining clock 600000)

        let intermediate, published = refPair root
        test <@ File.ReadAllBytes intermediate = File.ReadAllBytes published @>

        match host.GetStatus("build") with
        | Some(Completed _) -> ()
        | other -> failwithf "expected the rebuilt build to pass, got %A" other)
