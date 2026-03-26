module FsHotWatch.Tests.ProjectGraphTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ProjectGraph

[<Fact>]
let ``RegisterProject maps files to project`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/Lib.fs"; "/proj/Util.fs" ], [])
    test <@ graph.GetProjectForFile("/proj/Lib.fs") = Some "/proj/A.fsproj" @>
    test <@ graph.GetProjectForFile("/proj/Util.fs") = Some "/proj/A.fsproj" @>
    test <@ graph.GetProjectForFile("/proj/Other.fs") = None @>

[<Fact>]
let ``GetSourceFiles returns registered files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/Lib.fs"; "/proj/Util.fs" ], [])
    let files = graph.GetSourceFiles("/proj/A.fsproj")
    test <@ files.Length = 2 @>
    test <@ files |> List.contains "/proj/Lib.fs" @>

[<Fact>]
let ``GetReferences returns project references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    test <@ graph.GetReferences("/proj/B.fsproj") = [ "/proj/A.fsproj" ] @>

[<Fact>]
let ``GetDependents returns reverse references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    test <@ graph.GetDependents("/proj/A.fsproj") = [ "/proj/B.fsproj" ] @>
    test <@ graph.GetDependents("/proj/B.fsproj") |> List.isEmpty @>

[<Fact>]
let ``GetTransitiveDependents walks the graph`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    graph.RegisterProject("/proj/C.fsproj", [ "/proj/C.fs" ], [ "/proj/B.fsproj" ])
    let deps = graph.GetTransitiveDependents("/proj/A.fsproj")
    test <@ deps = [ "/proj/A.fsproj"; "/proj/B.fsproj"; "/proj/C.fsproj" ] @>

[<Fact>]
let ``GetAffectedProjects finds projects for changed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    let affected = graph.GetAffectedProjects([ "/proj/A.fs" ])
    test <@ affected |> List.contains "/proj/A.fsproj" @>
    test <@ affected |> List.contains "/proj/B.fsproj" @>

[<Fact>]
let ``GetTopologicalOrder returns deps before dependents`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    graph.RegisterProject("/proj/C.fsproj", [ "/proj/C.fs" ], [ "/proj/A.fsproj" ])
    let order = graph.GetTopologicalOrder()
    let idxA = order |> List.findIndex (fun p -> p = "/proj/A.fsproj")
    let idxB = order |> List.findIndex (fun p -> p = "/proj/B.fsproj")
    let idxC = order |> List.findIndex (fun p -> p = "/proj/C.fsproj")
    test <@ idxA < idxB @>
    test <@ idxA < idxC @>

[<Fact>]
let ``RegisterFromFsproj parses real fsproj`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    File.WriteAllText(
        Path.Combine(tmpDir, "A.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
    <Compile Include="Util.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../B/B.fsproj" />
  </ItemGroup>
</Project>"""
    )

    File.WriteAllText(Path.Combine(tmpDir, "Lib.fs"), "module Lib")
    File.WriteAllText(Path.Combine(tmpDir, "Util.fs"), "module Util")

    try
        let graph = ProjectGraph()
        let (sourceFiles, refs) = graph.RegisterFromFsproj(Path.Combine(tmpDir, "A.fsproj"))
        test <@ sourceFiles.Length = 2 @>
        test <@ refs.Length = 1 @>
        test <@ refs.[0].EndsWith("B.fsproj") @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``GetAffectedProjects returns empty for unknown file`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    test <@ graph.GetAffectedProjects([ "/proj/Unknown.fs" ]) |> List.isEmpty @>

[<Fact>]
let ``PrepareForRediscovery clears fileToProject for removed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs"; "/proj/Old.fs" ], [])
    graph.PrepareForRediscovery()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    test <@ graph.GetProjectForFile("/proj/Old.fs") = None @>
    test <@ graph.GetProjectForFile("/proj/A.fs") = Some "/proj/A.fsproj" @>

[<Fact>]
let ``PrepareForRediscovery clears deleted projects`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    graph.PrepareForRediscovery()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    test <@ graph.GetAllProjects() = [ "/proj/A.fsproj" ] @>
    test <@ graph.GetProjectForFile("/proj/B.fs") = None @>
    test <@ graph.GetDependents("/proj/A.fsproj") |> List.isEmpty @>

[<Fact>]
let ``PrepareForRediscovery clears stale projectDependents`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    test <@ graph.GetDependents("/proj/A.fsproj") = [ "/proj/B.fsproj" ] @>
    graph.PrepareForRediscovery()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [])
    test <@ graph.GetDependents("/proj/A.fsproj") |> List.isEmpty @>

[<Fact>]
let ``GetParallelTiers groups independent projects in same tier`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [])
    graph.RegisterProject("/proj/C.fsproj", [ "/proj/C.fs" ], [ "/proj/A.fsproj"; "/proj/B.fsproj" ])
    let tiers = graph.GetParallelTiers()
    // A and B have no deps, so they should be in tier 0
    // C depends on A and B, so it should be in tier 1
    test <@ tiers.Length = 2 @>
    test <@ tiers.[0] |> List.contains "/proj/A.fsproj" @>
    test <@ tiers.[0] |> List.contains "/proj/B.fsproj" @>
    test <@ tiers.[1] = [ "/proj/C.fsproj" ] @>

[<Fact>]
let ``GetParallelTiers handles linear chain`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    graph.RegisterProject("/proj/C.fsproj", [ "/proj/C.fs" ], [ "/proj/B.fsproj" ])
    let tiers = graph.GetParallelTiers()
    test <@ tiers.Length = 3 @>
    test <@ tiers.[0] = [ "/proj/A.fsproj" ] @>
    test <@ tiers.[1] = [ "/proj/B.fsproj" ] @>
    test <@ tiers.[2] = [ "/proj/C.fsproj" ] @>
