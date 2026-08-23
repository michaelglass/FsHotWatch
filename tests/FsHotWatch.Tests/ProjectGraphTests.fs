module FsHotWatch.Tests.ProjectGraphTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ProjectGraph

let fp path = AbsFilePath.create path
let pp path = AbsProjectPath.create path

[<Fact(Timeout = 15000)>]
let ``RegisterProject maps files to project`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/Lib.fs"; fp "/proj/Util.fs" ], [])
    test <@ graph.GetProjectForFile(fp "/proj/Lib.fs") = Some(pp "/proj/A.fsproj") @>
    test <@ graph.GetProjectForFile(fp "/proj/Util.fs") = Some(pp "/proj/A.fsproj") @>
    test <@ graph.GetProjectForFile(fp "/proj/Other.fs") = None @>

[<Fact(Timeout = 15000)>]
let ``GetSourceFiles returns registered files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/Lib.fs"; fp "/proj/Util.fs" ], [])
    let files = graph.GetSourceFiles(pp "/proj/A.fsproj")
    test <@ files.Length = 2 @>
    test <@ files |> List.contains (fp "/proj/Lib.fs") @>

[<Fact(Timeout = 15000)>]
let ``GetReferences returns project references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetReferences(pp "/proj/B.fsproj") = [ pp "/proj/A.fsproj" ] @>

[<Fact(Timeout = 15000)>]
let ``GetDependents returns reverse references`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") = [ pp "/proj/B.fsproj" ] @>
    test <@ graph.GetDependents(pp "/proj/B.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetTransitiveDependents walks the graph`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/B.fsproj" ])
    let deps = graph.GetTransitiveDependents(pp "/proj/A.fsproj")
    test <@ deps = [ pp "/proj/A.fsproj"; pp "/proj/B.fsproj"; pp "/proj/C.fsproj" ] @>

[<Fact(Timeout = 15000)>]
let ``GetAffectedProjects finds projects for changed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let affected = graph.GetAffectedProjects([ fp "/proj/A.fs" ])
    test <@ affected |> List.contains (pp "/proj/A.fsproj") @>
    test <@ affected |> List.contains (pp "/proj/B.fsproj") @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``GetAffectedProjects returns empty for unknown file`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetAffectedProjects([ fp "/proj/Unknown.fs" ]) |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears fileToProject for removed files`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; fp "/proj/Old.fs" ], [])
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetProjectForFile(fp "/proj/Old.fs") = None @>
    test <@ graph.GetProjectForFile(fp "/proj/A.fs") = Some(pp "/proj/A.fsproj") @>

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears deleted projects`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetAllProjects() = [ pp "/proj/A.fsproj" ] @>
    test <@ graph.GetProjectForFile(fp "/proj/B.fs") = None @>
    test <@ graph.GetDependents(pp "/proj/A.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears stale projectDependents`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") = [ pp "/proj/B.fsproj" ] @>
    graph.PrepareForRediscovery()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [])
    test <@ graph.GetDependents(pp "/proj/A.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetParallelTiers groups independent projects in same tier`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [])

    graph.RegisterProject(pp "/proj/C.fsproj", [ fp "/proj/C.fs" ], [ pp "/proj/A.fsproj"; pp "/proj/B.fsproj" ])

    let tiers = graph.GetParallelTiers()
    test <@ tiers.Length = 2 @>
    test <@ tiers.[0] |> List.contains (pp "/proj/A.fsproj") @>
    test <@ tiers.[0] |> List.contains (pp "/proj/B.fsproj") @>
    test <@ tiers.[1] = [ pp "/proj/C.fsproj" ] @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``GetProjectsForFile returns all projects for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let projects = graph.GetProjectsForFile(shared)
    test <@ projects |> List.contains (pp "/proj/A.fsproj") @>
    test <@ projects |> List.contains (pp "/proj/B.fsproj") @>
    test <@ projects.Length = 2 @>

[<Fact(Timeout = 15000)>]
let ``GetProjectsForFile returns empty for unknown file`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    test <@ graph.GetProjectsForFile(fp "/proj/Unknown.fs") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetProjectForFile still works for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ shared ], [])
    test <@ graph.GetProjectForFile(shared).IsSome @>

[<Fact(Timeout = 15000)>]
let ``GetAffectedProjects returns all projects for shared file`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let affected = graph.GetAffectedProjects([ shared ])
    test <@ affected |> List.contains (pp "/proj/A.fsproj") @>
    test <@ affected |> List.contains (pp "/proj/B.fsproj") @>

[<Fact(Timeout = 15000)>]
let ``GetAllFiles does not duplicate shared files`` () =
    let graph = ProjectGraph()
    let shared = fp "/proj/Shared.fs"
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs"; shared ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs"; shared ], [])
    let files = graph.GetAllFiles()
    test <@ files |> List.filter (fun f -> f = shared) |> List.length = 1 @>
    test <@ files.Length = 3 @>

// --- Coverage for uncovered edge cases ---

[<Fact(Timeout = 15000)>]
let ``RegisterProject does not duplicate dependent when registered twice`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let deps = graph.GetDependents(pp "/proj/A.fsproj")
    test <@ deps = [ pp "/proj/B.fsproj" ] @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``GetSourceFiles returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetSourceFiles(pp "/proj/NoSuch.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetReferences returns empty for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetReferences(pp "/proj/NoSuch.fsproj") |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetParallelTiers puts circular dependencies in final tier`` () =
    let graph = ProjectGraph()
    // A -> B -> A is impossible in a real solution, but it is the only way to
    // reach the fallback arm where nothing is ever "ready".
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A.fs" ], [ pp "/proj/B.fsproj" ])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B.fs" ], [ pp "/proj/A.fsproj" ])
    let tiers = graph.GetParallelTiers()
    test <@ tiers.Length = 1 @>
    test <@ tiers.[0] |> List.length = 2 @>

[<Fact(Timeout = 15000)>]
let ``GetParallelTiers returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetParallelTiers() |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetTopologicalOrder returns empty for empty graph`` () =
    let graph = ProjectGraph()
    test <@ graph.GetTopologicalOrder() |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``GetAllFiles returns all registered file paths`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A1.fs"; fp "/proj/A2.fs" ], [])
    graph.RegisterProject(pp "/proj/B.fsproj", [ fp "/proj/B1.fs" ], [])
    let files = graph.GetAllFiles() |> Set.ofList
    test <@ files.Count = 3 @>
    test <@ files.Contains(fp "/proj/A1.fs") @>
    test <@ files.Contains(fp "/proj/A2.fs") @>
    test <@ files.Contains(fp "/proj/B1.fs") @>

[<Fact(Timeout = 15000)>]
let ``GetAllFiles returns empty after PrepareForRediscovery`` () =
    let graph = ProjectGraph()
    graph.RegisterProject(pp "/proj/A.fsproj", [ fp "/proj/A1.fs" ], [])
    test <@ graph.GetAllFiles().Length = 1 @>
    graph.PrepareForRediscovery()
    test <@ graph.GetAllFiles().IsEmpty @>

// --- TargetFramework ---

[<Fact(Timeout = 2000)>]
let ``extractTargetFramework returns single TargetFramework`` () =
    let doc =
        System.Xml.Linq.XDocument.Parse(
            """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

    test <@ extractTargetFramework doc = Some "net10.0" @>

[<Fact(Timeout = 2000)>]
let ``extractTargetFramework returns first entry of TargetFrameworks`` () =
    let doc =
        System.Xml.Linq.XDocument.Parse(
            """<Project><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup></Project>"""
        )

    test <@ extractTargetFramework doc = Some "net10.0" @>

[<Fact(Timeout = 2000)>]
let ``extractTargetFramework returns None when neither tag present`` () =
    let doc =
        System.Xml.Linq.XDocument.Parse("""<Project><PropertyGroup /></Project>""")

    test <@ extractTargetFramework doc = None @>

[<Fact(Timeout = 15000)>]
let ``RegisterFromFsproj captures TargetFramework`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-tfm-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fsproj = Path.Combine(tmpDir, "Foo.fsproj")

        File.WriteAllText(
            fsproj,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(fsproj) |> ignore
        test <@ graph.GetTargetFramework(AbsProjectPath.create fsproj) = Some "net10.0" @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact(Timeout = 2000)>]
let ``GetTargetFramework returns None for unregistered project`` () =
    let graph = ProjectGraph()
    test <@ graph.GetTargetFramework(pp "/nope/Foo.fsproj") = None @>

[<Fact(Timeout = 15000)>]
let ``PrepareForRediscovery clears TargetFramework`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-tfm-clear-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fsproj = Path.Combine(tmpDir, "Foo.fsproj")

        File.WriteAllText(
            fsproj,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(fsproj) |> ignore
        graph.PrepareForRediscovery()
        test <@ graph.GetTargetFramework(AbsProjectPath.create fsproj) = None @>
    finally
        Directory.Delete(tmpDir, true)

// ---------------------------------------------------------------------------
// AUTOMATION-368 — the canonical DLL path must come from MSBuild, not inference
// ---------------------------------------------------------------------------
//
// `GetCanonicalDllPath` inferred `<projDir>/bin/Debug/<TFM>/<projectFileName>.dll`,
// and BOTH halves of that inference fail in production:
//
//   * the TFM reaches this graph ONLY through `RegisterFromFsproj`, which has
//     zero callers in `src/` — so the live daemon supplied none, the path was
//     always `None`, and `verifyArtifactsFresh` returned `[]` unconditionally.
//     Two shipped gates had therefore never examined an artifact, while their
//     tests — which use the fsproj-parse registration path — stayed green.
//
//   * the project FILE NAME is not the assembly name. This repo has a
//     counterexample: FsHotWatch.Rules builds FsHotWatch.ConventionAnalyzers.dll,
//     which the inference reads as permanently missing.
//
// Discovery now records MSBuild's own `TargetPath`, which needs neither guess.

[<Fact(Timeout = 15000)>]
let ``a recorded MSBuild output path is preferred over the inferred one`` () =
    let graph = ProjectGraph()
    let proj = pp "/repo/src/Thing/Thing.fsproj"

    graph.RegisterProject(proj, [ fp "/repo/src/Thing/A.fs" ], [])

    // Without a recorded path AND without a TFM — the live daemon's state before
    // this change — there is nothing to examine. This is the precondition that
    // made both gates inert.
    test <@ graph.GetCanonicalDllPath proj = None @>

    graph.RegisterProjectOutput(proj, "/repo/src/Thing/bin/Debug/net10.0/Thing.dll")

    test <@ graph.GetCanonicalDllPath proj = Some "/repo/src/Thing/bin/Debug/net10.0/Thing.dll" @>

[<Fact(Timeout = 15000)>]
let ``a custom AssemblyName is honoured, not inferred from the file name`` () =
    // The second mis-derivation, with the repo's own real counterexample.
    // Inference would say ".../FsHotWatch.Rules.dll" and find nothing there.
    let graph = ProjectGraph()
    let proj = pp "/repo/analyzers/FsHotWatch.Rules/FsHotWatch.Rules.fsproj"

    let realOutput =
        "/repo/analyzers/FsHotWatch.Rules/bin/Debug/net10.0/FsHotWatch.ConventionAnalyzers.dll"

    graph.RegisterProject(proj, [ fp "/repo/analyzers/FsHotWatch.Rules/R.fs" ], [])
    graph.RegisterProjectOutput(proj, realOutput)

    test <@ graph.GetCanonicalDllPath proj = Some realOutput @>
    // The name the inference would have produced must NOT be what we report.
    test
        <@
            graph.GetCanonicalDllPath proj
            <> Some "/repo/analyzers/FsHotWatch.Rules/bin/Debug/net10.0/FsHotWatch.Rules.dll"
        @>

[<Fact(Timeout = 15000)>]
let ``an empty or whitespace TargetPath is not recorded`` () =
    // MSBuild can hand back an empty string for a project it could not evaluate.
    // Recording that would make `GetCanonicalDllPath` answer `Some ""`, which
    // reads downstream as "the output is missing" — a false staleness claim, and
    // exactly the failure mode this gate must not introduce.
    let graph = ProjectGraph()
    let proj = pp "/repo/src/Thing/Thing.fsproj"

    graph.RegisterProject(proj, [ fp "/repo/src/Thing/A.fs" ], [])
    graph.RegisterProjectOutput(proj, "   ")

    test <@ graph.GetRecordedOutputPath proj = None @>
    test <@ graph.GetCanonicalDllPath proj = None @>

[<Fact(Timeout = 15000)>]
let ``without a recorded output the path is inferred from the TargetFramework`` () =
    // The FALLBACK arm, tested where it lives rather than incidentally through a
    // plugin fixture. `RegisterFromFsproj` has no production callers, so this is the
    // only registration that still reaches the inference — and an untested fallback
    // is how the primary path came to be the one nothing exercised.
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-infer-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fsproj = Path.Combine(tmpDir, "Foo.fsproj")

        File.WriteAllText(
            fsproj,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(fsproj) |> ignore

        test
            <@
                graph.GetCanonicalDllPath(AbsProjectPath.create fsproj) = Some(
                    Path.Combine(tmpDir, "bin", "Debug", "net10.0", "Foo.dll")
                )
            @>
    finally
        Directory.Delete(tmpDir, true)

// ---------------------------------------------------------------------------
// AUTOMATION-368 — a build-generated compile item is not a source EDIT
// ---------------------------------------------------------------------------
//
// `GetMaxSourceMtime` answers one question for one caller: did an edit land after
// the build wrote the DLL? MSBuild's compile-item list is not a list of edits.
// Every SDK project compiles `obj/<cfg>/<tfm>/<Project>.AssemblyInfo.fs`, and
// every design-time evaluation regenerates it — including the one the daemon runs
// to DISCOVER projects. So each discovery pass pushed every project's newest
// "source" past its own freshly-built DLL, and the artifact gate read a tree
// nobody had touched as universally stale.
//
// Measured over the report-only window: 2090 stale findings across ~40 consuming
// workspaces, 91% within 90s of an `MSBuild evaluation` line in the same log.

/// A project whose compile items are what MSBuild really hands the daemon: the
/// authored source, plus the generated `AssemblyInfo.fs` under `obj/`. Registered
/// the way `Daemon.fs` registers — `RegisterProject` + `RegisterProjectOutput`,
/// never `RegisterFromFsproj`.
let private withGeneratedSourceProject (label: string) (body: ProjectGraph * string * string -> 'a) : 'a =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"graph-%s{label}-{Guid.NewGuid():N}")
    let objDir = Path.Combine(tmpDir, "obj", "Debug", "net10.0")
    Directory.CreateDirectory(objDir) |> ignore

    try
        let projPath = Path.Combine(tmpDir, "MyLib.fsproj")
        let authored = Path.Combine(tmpDir, "Lib.fs")
        let generated = Path.Combine(objDir, "MyLib.AssemblyInfo.fs")
        File.WriteAllText(projPath, "<Project />")
        File.WriteAllText(authored, "let x = 1")
        File.WriteAllText(generated, "// generated")

        let graph = ProjectGraph()

        graph.RegisterProject(pp projPath, [ fp authored; fp generated ], [])
        graph.RegisterProjectOutput(pp projPath, Path.Combine(tmpDir, "bin", "Debug", "net10.0", "MyLib.dll"))

        body (graph, authored, generated)
    finally
        Directory.Delete(tmpDir, true)

[<Fact(Timeout = 15000)>]
let ``a regenerated obj compile item does not move the max source mtime`` () =
    withGeneratedSourceProject "objsrc" (fun (graph, authored, generated) ->
        let proj = pp (Path.Combine(Path.GetDirectoryName(authored), "MyLib.fsproj"))
        let authoredAt = DateTime.UtcNow.AddHours(-1.0)
        File.SetLastWriteTimeUtc(authored, authoredAt)

        // POSITIVE CONTROL: the authored source IS read. Without it, an
        // implementation that filtered everything would satisfy the assertion below
        // by answering `None` — the silent-degradation shape this gate exists to
        // avoid, reintroduced by the fix for it.
        test <@ graph.GetMaxSourceMtime proj = Some authoredAt @>

        // Discovery regenerates it, hours after the last real edit.
        File.SetLastWriteTimeUtc(generated, DateTime.UtcNow)

        test <@ graph.GetMaxSourceMtime proj = Some authoredAt @>)

[<Fact(Timeout = 15000)>]
let ``a real source edit still moves the max source mtime`` () =
    // The mutation in the other direction: the exclusion must not have switched the
    // reading off. This is the edit the gate exists to notice.
    withGeneratedSourceProject "realsrc" (fun (graph, authored, _) ->
        let proj = pp (Path.Combine(Path.GetDirectoryName(authored), "MyLib.fsproj"))
        File.SetLastWriteTimeUtc(authored, DateTime.UtcNow.AddHours(-1.0))
        let before = graph.GetMaxSourceMtime proj

        let editedAt = DateTime.UtcNow
        File.SetLastWriteTimeUtc(authored, editedAt)

        test <@ graph.GetMaxSourceMtime proj = Some editedAt @>
        test <@ before <> graph.GetMaxSourceMtime proj @>)

[<Fact(Timeout = 15000)>]
let ``a repo nested under a directory named obj still reads its own sources`` () =
    // The trap in the fix. Matching `obj`/`bin` anywhere in the ABSOLUTE path makes
    // every file of a repo checked out under such a directory a build output, so
    // nothing is ever newer than the DLL and the gate answers FRESH forever — the
    // same silence, arrived at by being more thorough. `isBuildOutput` asks
    // relative to the project directory for exactly this reason.
    let tmpDir =
        Path.Combine(Path.GetTempPath(), $"graph-nested-{Guid.NewGuid():N}", "obj", "checkout", "MyLib")

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let projPath = Path.Combine(tmpDir, "MyLib.fsproj")
        let authored = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(projPath, "<Project />")
        File.WriteAllText(authored, "let x = 1")
        let editedAt = DateTime.UtcNow.AddMinutes(-3.0)
        File.SetLastWriteTimeUtc(authored, editedAt)

        let graph = ProjectGraph()
        graph.RegisterProject(pp projPath, [ fp authored ], [])

        test <@ graph.GetMaxSourceMtime(pp projPath) = Some editedAt @>
    finally
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(tmpDir)), true)
