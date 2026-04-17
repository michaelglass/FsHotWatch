module FsHotWatch.Tests.ProjectGraphTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ProjectGraph

let fp path = AbsFilePath.create path
let pp path = AbsProjectPath.create path

[<Fact(Timeout = 30000)>]
let ``RegisterProject maps files to project`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/Lib.fs"; fp "/proj/Util.fs" ], [])
    test <@ graph.GetProjectForFile(fp "/proj/Lib.fs") = Some(pp "/proj/A.fsproj") @>
    test <@ graph.GetProjectForFile(fp "/proj/Util.fs") = Some(pp "/proj/A.fsproj") @>
    test <@ graph.GetProjectForFile(fp "/proj/Other.fs") = None @>

[<Fact(Timeout = 30000)>]
let ``GetSourceFiles returns registered files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/Lib.fs"; fp "/proj/Util.fs" ], [])
    let files = graph.GetSourceFiles(pp "/proj/A.fsproj")
    test <@ files.Length = 2 @>
    test <@ files |> List.contains (fp "/proj/Lib.fs") @>

[<Fact(Timeout = 30000)>]
let ``GetReferences returns project references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetReferences(pp "/proj/B.fsproj") = [ pp "/proj/A.fsproj" ] @>

[<Fact(Timeout = 30000)>]
let ``GetDependents returns reverse references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") = [ pp "/proj/B.fsproj" ] @>
    test <@ graph.GetDependents(pp "/proj/B.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``GetTransitiveDependents walks the graph`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/B.fsproj" ])
    let deps = graph.GetTransitiveDependents(pp "/proj/A.fsproj")
    test <@ deps = [ pp "/proj/A.fsproj"; pp "/proj/B.fsproj"; pp "/proj/C.fsproj" ] @>

[<Fact(Timeout = 30000)>]
let ``GetAffectedProjects finds projects for changed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let affected = graph.GetAffectedProjects([ fp "/proj/A.fs" ])
    test <@ affected |> List.contains (pp "/proj/A.fsproj") @>
    test <@ affected |> List.contains (pp "/proj/B.fsproj") @>

[<Fact(Timeout = 30000)>]
let ``GetTopologicalOrder returns deps before dependents`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/A.fsproj" ])
    let order = graph.GetTopologicalOrder()
    let idxA = order |> List.findIndex (fun p -> p = pp "/proj/A.fsproj")
    let idxB = order |> List.findIndex (fun p -> p = pp "/proj/B.fsproj")
    let idxC = order |> List.findIndex (fun p -> p = pp "/proj/C.fsproj")
    test <@ idxA < idxB @>
    test <@ idxA < idxC @>

[<Fact(Timeout = 30000)>]
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
        test <@ (AbsProjectPath.value refs.[0]).EndsWith("B.fsproj") @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact(Timeout = 30000)>]
let ``GetAffectedProjects returns empty for unknown file`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetAffectedProjects([ fp "/proj/Unknown.fs" ]) |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``PrepareForRediscovery clears fileToProject for removed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; fp "/proj/Old.fs" ], [])
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetProjectForFile(fp "/proj/Old.fs") = None @>
    test <@ graph.GetProjectForFile(fp "/proj/A.fs") = Some(pp "/proj/A.fsproj") @>

[<Fact(Timeout = 30000)>]
let ``PrepareForRediscovery clears deleted projects`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetAllProjects() = [ pp "/proj/A.fsproj" ] @>
    test <@ graph.GetProjectForFile(fp "/proj/B.fs") = None @>
    test <@ graph.GetDependents(pp "/proj/A.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``PrepareForRediscovery clears stale projectDependents`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") = [ pp "/proj/B.fsproj" ] @>
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``GetParallelTiers groups independent projects in same tier`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [])

    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/A.fsproj"; pp "/proj/B.fsproj" ])

    let tiers = graph.GetParallelTiers()
    // A and B have no deps, so they should be in tier 0
    // C depends on A and B, so it should be in tier 1
    test <@ tiers.Length = 2 @>
    test <@ tiers.[0] |> List.contains (pp "/proj/A.fsproj") @>
    test <@ tiers.[0] |> List.contains (pp "/proj/B.fsproj") @>
    test <@ tiers.[1] = [ pp "/proj/C.fsproj" ] @>

[<Fact(Timeout = 30000)>]
let ``GetParallelTiers handles linear chain`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/B.fsproj" ])
    let tiers = graph.GetParallelTiers()
    test <@ tiers.Length = 3 @>
    test <@ tiers.[0] = [ pp "/proj/A.fsproj" ] @>
    test <@ tiers.[1] = [ pp "/proj/B.fsproj" ] @>
    test <@ tiers.[2] = [ pp "/proj/C.fsproj" ] @>

// --- Shared source files (linked items) ---

[<Fact(Timeout = 30000)>]
let ``GetProjectsForFile returns all projects for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let projects = graph.GetProjectsForFile(shared)
    test <@ projects |> List.contains (pp "/proj/A.fsproj") @>
    test <@ projects |> List.contains (pp "/proj/B.fsproj") @>
    test <@ projects.Length = 2 @>

[<Fact(Timeout = 30000)>]
let ``GetProjectsForFile returns empty for unknown file`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetProjectsForFile(fp "/proj/Unknown.fs") |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``GetProjectForFile still works for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ shared ], [])
    test <@ graph.GetProjectForFile(shared).IsSome @>

[<Fact(Timeout = 30000)>]
let ``GetAffectedProjects returns all projects for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let affected = graph.GetAffectedProjects([ shared ])
    test <@ affected |> List.contains (pp "/proj/A.fsproj") @>
    test <@ affected |> List.contains (pp "/proj/B.fsproj") @>

[<Fact(Timeout = 30000)>]
let ``GetAllFiles does not duplicate shared files`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let files = graph.GetAllFiles()
    test <@ files |> List.filter (fun f -> f = shared) |> List.length = 1 @>
    test <@ files.Length = 3 @>

// --- Coverage for uncovered edge cases ---

// Line 41: RegisterProject duplicate dependent (existing list already contains project)
[<Fact(Timeout = 30000)>]
let ``RegisterProject does not duplicate dependent when registered twice`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    // Re-register B with same reference — should not add duplicate dependent
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let deps = graph.GetDependents(pp "/proj/A.fsproj")
    test <@ deps = [ pp "/proj/B.fsproj" ] @>

// Line 61: RegisterFromFsproj with Compile element missing Include attribute
[<Fact(Timeout = 30000)>]
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
[<Fact(Timeout = 30000)>]
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
[<Fact(Timeout = 30000)>]
let ``GetSourceFiles returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetSourceFiles(pp "/proj/NoSuch.fsproj") |> List.isEmpty @>

// Line 94: GetReferences for unregistered project returns empty list
[<Fact(Timeout = 30000)>]
let ``GetReferences returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetReferences(pp "/proj/NoSuch.fsproj") |> List.isEmpty @>

// Line 165: GetParallelTiers with circular dependency (blocked projects forced into final tier)
[<Fact(Timeout = 30000)>]
let ``GetParallelTiers puts circular dependencies in final tier`` () =
    let graph = ProjectGraph()
    // Create a cycle: A -> B -> A (impossible in reality, but tests the fallback path)
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [ pp "/proj/B.fsproj" ])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let tiers = graph.GetParallelTiers()
    // Neither can be "ready" — both should end up in the remaining tier
    test <@ tiers.Length = 1 @>
    test <@ tiers.[0] |> List.length = 2 @>

// GetParallelTiers with empty graph
[<Fact(Timeout = 30000)>]
let ``GetParallelTiers returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetParallelTiers() |> List.isEmpty @>

// GetTopologicalOrder returns empty for empty graph
[<Fact(Timeout = 30000)>]
let ``GetTopologicalOrder returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetTopologicalOrder() |> List.isEmpty @>

[<Fact(Timeout = 30000)>]
let ``GetAllFiles returns all registered file paths`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A1.fs"; fp "/proj/A2.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B1.fs" ], [])
    let files = graph.GetAllFiles() |> Set.ofList
    test <@ files.Count = 3 @>
    test <@ files.Contains(fp "/proj/A1.fs") @>
    test <@ files.Contains(fp "/proj/A2.fs") @>
    test <@ files.Contains(fp "/proj/B1.fs") @>

[<Fact(Timeout = 30000)>]
let ``GetAllFiles returns empty after PrepareForRediscovery`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A1.fs" ], [])
    test <@ graph.GetAllFiles().Length = 1 @>
    graph.PrepareForRediscovery()
    test <@ graph.GetAllFiles().IsEmpty @>
