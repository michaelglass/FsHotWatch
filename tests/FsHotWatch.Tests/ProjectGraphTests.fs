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

// --- Coverage for uncovered edge cases ---

// Line 41: RegisterProject duplicate dependent (existing list already contains project)
[<Fact>]
let ``RegisterProject does not duplicate dependent when registered twice`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    // Re-register B with same reference — should not add duplicate dependent
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    let deps = graph.GetDependents("/proj/A.fsproj")
    test <@ deps = [ "/proj/B.fsproj" ] @>

// Line 61: RegisterFromFsproj with Compile element missing Include attribute
[<Fact>]
let ``RegisterFromFsproj ignores Compile elements without Include attribute`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-noinclude-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    File.WriteAllText(
        Path.Combine(tmpDir, "A.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
    <Compile />
  </ItemGroup>
</Project>"""
    )

    File.WriteAllText(Path.Combine(tmpDir, "Lib.fs"), "module Lib")

    try
        let graph = ProjectGraph()
        let (sourceFiles, refs) = graph.RegisterFromFsproj(Path.Combine(tmpDir, "A.fsproj"))
        test <@ sourceFiles.Length = 1 @>
        test <@ refs |> List.isEmpty @>
    finally
        Directory.Delete(tmpDir, true)

// Line 72: RegisterFromFsproj with ProjectReference element missing Include attribute
[<Fact>]
let ``RegisterFromFsproj ignores ProjectReference elements without Include attribute`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-noref-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    File.WriteAllText(
        Path.Combine(tmpDir, "A.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Compile Include="Lib.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../B/B.fsproj" />
    <ProjectReference />
  </ItemGroup>
</Project>"""
    )

    File.WriteAllText(Path.Combine(tmpDir, "Lib.fs"), "module Lib")

    try
        let graph = ProjectGraph()
        let (sourceFiles, refs) = graph.RegisterFromFsproj(Path.Combine(tmpDir, "A.fsproj"))
        test <@ sourceFiles.Length = 1 @>
        test <@ refs.Length = 1 @>
    finally
        Directory.Delete(tmpDir, true)

// Line 88: GetSourceFiles for unregistered project returns empty list
[<Fact>]
let ``GetSourceFiles returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetSourceFiles("/proj/NoSuch.fsproj") |> List.isEmpty @>

// Line 94: GetReferences for unregistered project returns empty list
[<Fact>]
let ``GetReferences returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetReferences("/proj/NoSuch.fsproj") |> List.isEmpty @>

// Line 165: GetParallelTiers with circular dependency (blocked projects forced into final tier)
[<Fact>]
let ``GetParallelTiers puts circular dependencies in final tier`` () =
    let graph = ProjectGraph()
    // Create a cycle: A -> B -> A (impossible in reality, but tests the fallback path)
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A.fs" ], [ "/proj/B.fsproj" ])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B.fs" ], [ "/proj/A.fsproj" ])
    let tiers = graph.GetParallelTiers()
    // Neither can be "ready" — both should end up in the remaining tier
    test <@ tiers.Length = 1 @>
    test <@ tiers.[0] |> List.length = 2 @>

// GetParallelTiers with empty graph
[<Fact>]
let ``GetParallelTiers returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetParallelTiers() |> List.isEmpty @>

// GetTopologicalOrder returns empty for empty graph
[<Fact>]
let ``GetTopologicalOrder returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetTopologicalOrder() |> List.isEmpty @>

[<Fact>]
let ``GetAllFiles returns all registered file paths`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A1.fs"; "/proj/A2.fs" ], [])
    graph.RegisterProject("/proj/B.fsproj", [ "/proj/B1.fs" ], [])
    let files = graph.GetAllFiles() |> Set.ofList
    test <@ files.Count = 3 @>
    test <@ files.Contains("/proj/A1.fs") @>
    test <@ files.Contains("/proj/A2.fs") @>
    test <@ files.Contains("/proj/B1.fs") @>

[<Fact>]
let ``GetAllFiles returns empty after PrepareForRediscovery`` () =
    let graph = ProjectGraph()
    graph.RegisterProject("/proj/A.fsproj", [ "/proj/A1.fs" ], [])
    test <@ graph.GetAllFiles().Length = 1 @>
    graph.PrepareForRediscovery()
    test <@ graph.GetAllFiles().IsEmpty @>
