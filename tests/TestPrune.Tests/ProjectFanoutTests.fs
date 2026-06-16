module TestPrune.Tests.ProjectFanoutTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.ProjectFanout
open TestPrune.ProjectLoader

// A small project graph used across these tests:
//
//   Lib.Ops        (library; references the NuGet package "CommandTree")
//     ▲
//     │ ProjectReference
//   Lib.Build      (library; ProjectReferences Lib.Ops)
//     ▲
//     │ ProjectReference
//   Lib.Build.Tests (test project; ProjectReferences Lib.Build)
//
// A bump of CommandTree flips Lib.Ops's DependencyFingerprint even though no F#
// symbol in Lib.Ops changes. The fanout must select every test in the test
// projects that transitively ProjectReference Lib.Ops.

let private projects =
    [ { ProjectName = "Lib.Ops"
        ProjectReferences = []
        DependencyFingerprint = "ops-fp-v1" }
      { ProjectName = "Lib.Build"
        ProjectReferences = [ "Lib.Ops" ]
        DependencyFingerprint = "build-fp-v1" }
      { ProjectName = "Lib.Build.Tests"
        ProjectReferences = [ "Lib.Build" ]
        DependencyFingerprint = "tests-fp-v1" }
      // An unrelated test project that does NOT reference Lib.Ops.
      { ProjectName = "Other.Tests"
        ProjectReferences = []
        DependencyFingerprint = "other-fp-v1" } ]

let private testMethods =
    [ { SymbolFullName = "Lib.Build.Tests.SizingConfigTests.a"
        TestProject = "Lib.Build.Tests"
        TestClass = "SizingConfigTests"
        TestMethod = "a" }
      { SymbolFullName = "Lib.Build.Tests.SizingConfigTests.b"
        TestProject = "Lib.Build.Tests"
        TestClass = "SizingConfigTests"
        TestMethod = "b" }
      { SymbolFullName = "Other.Tests.UnrelatedTests.c"
        TestProject = "Other.Tests"
        TestClass = "UnrelatedTests"
        TestMethod = "c" } ]

module ``affectedTestProjects (transitive reverse-walk over project references)`` =

    [<Fact>]
    let ``a changed project selects every test project that transitively references it`` () =
        // Lib.Ops fingerprint changed → Lib.Build (direct) and Lib.Build.Tests
        // (transitive) are downstream.
        let affected = affectedTestProjects projects (Set.ofList [ "Lib.Ops" ])
        // The changed project itself plus all transitive dependents.
        test <@ affected = Set.ofList [ "Lib.Ops"; "Lib.Build"; "Lib.Build.Tests" ] @>

    [<Fact>]
    let ``an unrelated project's change does not select the chain`` () =
        let affected = affectedTestProjects projects (Set.ofList [ "Other.Tests" ])
        test <@ affected = Set.ofList [ "Other.Tests" ] @>

    [<Fact>]
    let ``a leaf change selects only itself`` () =
        let affected = affectedTestProjects projects (Set.ofList [ "Lib.Build.Tests" ])
        test <@ affected = Set.ofList [ "Lib.Build.Tests" ] @>

module ``selectTestsForChangedProjects (fanout to dependent test methods)`` =

    [<Fact>]
    let ``a dependency-fingerprint change in Lib.Ops selects all tests in Lib.Build.Tests`` () =
        // THE ACCEPTANCE CASE: CommandTree bump in Lib.Ops, no symbol change,
        // must run every test in Lib.Build.Tests (which transitively references Ops).
        let selected =
            selectTestsForChangedProjects projects testMethods (Set.ofList [ "Lib.Ops" ])

        let methods = selected |> List.map (fun t -> t.SymbolFullName) |> List.sort

        test <@ methods = [ "Lib.Build.Tests.SizingConfigTests.a"; "Lib.Build.Tests.SizingConfigTests.b" ] @>

    [<Fact>]
    let ``an unrelated project change selects no dependent tests`` () =
        let selected =
            selectTestsForChangedProjects projects testMethods (Set.ofList [ "Other.Tests" ])

        let methods = selected |> List.map (fun t -> t.SymbolFullName) |> List.sort
        test <@ methods = [ "Other.Tests.UnrelatedTests.c" ] @>

module ``computeDependencyFingerprint - package versions and transitive ref fingerprints`` =

    [<Fact>]
    let ``bumping a package version flips the fingerprint`` () =
        let before =
            computeDependencyFingerprint [ "CommandTree", "0.6.3"; "FSharp.Core", "10.1.0" ] []

        let after =
            computeDependencyFingerprint [ "CommandTree", "0.7.0"; "FSharp.Core", "10.1.0" ] []

        test <@ before <> after @>

    [<Fact>]
    let ``reordering package references does NOT flip the fingerprint`` () =
        let a =
            computeDependencyFingerprint [ "CommandTree", "0.7.0"; "FSharp.Core", "10.1.0" ] []

        let b =
            computeDependencyFingerprint [ "FSharp.Core", "10.1.0"; "CommandTree", "0.7.0" ] []

        test <@ a = b @>

    [<Fact>]
    let ``a referenced project's fingerprint change flips this project's fingerprint (transitivity)`` () =
        // Build's fingerprint folds in Ops's fingerprint. When Ops's package bump
        // flips Ops's fingerprint, Build's fingerprint must move too even though
        // Build's own packages are unchanged.
        let opsBefore = computeDependencyFingerprint [ "CommandTree", "0.6.3" ] []
        let opsAfter = computeDependencyFingerprint [ "CommandTree", "0.7.0" ] []

        let buildBefore =
            computeDependencyFingerprint [ "FSharp.Core", "10.1.0" ] [ opsBefore ]

        let buildAfter =
            computeDependencyFingerprint [ "FSharp.Core", "10.1.0" ] [ opsAfter ]

        test <@ buildBefore <> buildAfter @>

    [<Fact>]
    let ``identical inputs produce identical fingerprints (deterministic)`` () =
        let a = computeDependencyFingerprint [ "CommandTree", "0.7.0" ] [ "ref-fp" ]
        let b = computeDependencyFingerprint [ "CommandTree", "0.7.0" ] [ "ref-fp" ]
        test <@ a = b @>

module ``diffFingerprints (detecting which projects' fingerprints changed)`` =

    [<Fact>]
    let ``a changed fingerprint is detected`` () =
        let previous = Map.ofList [ "Lib.Ops", "ops-fp-v1"; "Lib.Build", "build-fp-v1" ]

        let current =
            [ { ProjectName = "Lib.Ops"
                ProjectReferences = []
                DependencyFingerprint = "ops-fp-v2" } // bumped
              { ProjectName = "Lib.Build"
                ProjectReferences = [ "Lib.Ops" ]
                DependencyFingerprint = "build-fp-v1" } ] // unchanged

        let changed = diffFingerprints previous current
        test <@ changed = Set.ofList [ "Lib.Ops" ] @>

    [<Fact>]
    let ``a project with no prior fingerprint is NOT treated as changed`` () =
        // A first-ever index has no baseline; "new project" handling belongs to
        // the existing NewFileNotIndexed/RunAll path, not the fingerprint diff.
        // diffFingerprints reports only projects with a DIFFERENT known prior.
        let previous = Map.empty

        let current =
            [ { ProjectName = "Lib.Ops"
                ProjectReferences = []
                DependencyFingerprint = "ops-fp-v1" } ]

        let changed = diffFingerprints previous current
        test <@ Set.isEmpty changed @>

    [<Fact>]
    let ``an unchanged fingerprint is not reported`` () =
        let previous = Map.ofList [ "Lib.Ops", "ops-fp-v1" ]

        let current =
            [ { ProjectName = "Lib.Ops"
                ProjectReferences = []
                DependencyFingerprint = "ops-fp-v1" } ]

        let changed = diffFingerprints previous current
        test <@ Set.isEmpty changed @>

// =============================================================================
// END-TO-END acceptance: a real on-disk 3-project graph
//
//   Ops.fsproj          (PackageReference CommandTree, via CPM props)
//     ▲ ProjectReference
//   Build.fsproj
//     ▲ ProjectReference
//   Build.Tests.fsproj  (the test project)
//
// Bumping CommandTree in Directory.Packages.props (no .fs symbol changes) must,
// through the real parse → fingerprint → diff → fanout chain, select every test
// in Build.Tests. Mirrors the intelligence CommandTree 0.6.3 → 0.7.0 regression
// (there it is a direct <PackageReference Version>; the CPM variant is exercised
// here and the direct variant by parsePackageReferences tests).
// =============================================================================

module ``end-to-end: CPM dependency bump fans out to dependent test project`` =

    /// Build the 3-project fixture; returns (root, opsFsproj, buildFsproj, testsFsproj).
    let private writeFixture (commandTreeVersion: string) =
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        Directory.CreateDirectory(root) |> ignore

        let mk (sub: string) (name: string) (body: string) =
            let dir = Path.Combine(root, sub)
            Directory.CreateDirectory(dir) |> ignore
            let p = Path.Combine(dir, name)
            File.WriteAllText(p, body)
            p

        // Central package versions for the whole fixture.
        File.WriteAllText(
            Path.Combine(root, "Directory.Packages.props"),
            $"""<Project><ItemGroup>
  <PackageVersion Include="CommandTree" Version="%s{commandTreeVersion}" />
</ItemGroup></Project>"""
        )

        let opsFsproj =
            mk
                "Ops"
                "Ops.fsproj"
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" /></ItemGroup>
</Project>"""

        let buildFsproj =
            mk
                "Build"
                "Build.fsproj"
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Ops/Ops.fsproj" /></ItemGroup>
</Project>"""

        let testsFsproj =
            mk
                "Build.Tests"
                "Build.Tests.fsproj"
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Build/Build.fsproj" /></ItemGroup>
</Project>"""

        root, opsFsproj, buildFsproj, testsFsproj

    /// Compute the ProjectInfo list for the fixture in topological order so each
    /// project's fingerprint folds in its referenced projects' fingerprints.
    let private projectInfosFor (opsFsproj: string) (buildFsproj: string) (testsFsproj: string) =
        let nameOf (p: string) = Path.GetFileNameWithoutExtension p

        let opsFp = computeDependencyFingerprint (parsePackageReferences opsFsproj) []

        let buildFp =
            computeDependencyFingerprint (parsePackageReferences buildFsproj) [ opsFp ]

        let testsFp =
            computeDependencyFingerprint (parsePackageReferences testsFsproj) [ buildFp ]

        [ { ProjectName = nameOf opsFsproj
            ProjectReferences = []
            DependencyFingerprint = opsFp }
          { ProjectName = nameOf buildFsproj
            ProjectReferences = [ nameOf opsFsproj ]
            DependencyFingerprint = buildFp }
          { ProjectName = nameOf testsFsproj
            ProjectReferences = [ nameOf buildFsproj ]
            DependencyFingerprint = testsFp } ]

    let private buildTests =
        [ { SymbolFullName = "Build.Tests.SizingConfigTests.roundtrips"
            TestProject = "Build.Tests"
            TestClass = "SizingConfigTests"
            TestMethod = "roundtrips" } ]

    [<Fact>]
    let ``CommandTree bump in Ops selects all Build.Tests tests via fingerprint fanout`` () =
        let root1, ops1, build1, tests1 = writeFixture "0.6.3"

        try
            let before = projectInfosFor ops1 build1 tests1

            let priorFingerprints =
                before
                |> List.map (fun p -> p.ProjectName, p.DependencyFingerprint)
                |> Map.ofList

            // Bump CommandTree to 0.7.0 in the SAME fixture's central props.
            File.WriteAllText(
                Path.Combine(root1, "Directory.Packages.props"),
                """<Project><ItemGroup>
  <PackageVersion Include="CommandTree" Version="0.7.0" />
</ItemGroup></Project>"""
            )

            let after = projectInfosFor ops1 build1 tests1

            // Ops's fingerprint changed (no symbol touched), and transitivity flips
            // Build's and Build.Tests's too.
            let changed = diffFingerprints priorFingerprints after
            test <@ Set.contains "Ops" changed @>

            // Fanout from the changed projects selects Build.Tests's tests.
            let selected = selectTestsForChangedProjects after buildTests changed
            let methods = selected |> List.map (fun t -> t.SymbolFullName)
            test <@ methods = [ "Build.Tests.SizingConfigTests.roundtrips" ] @>
        finally
            Directory.Delete(root1, true)

    [<Fact>]
    let ``no bump selects nothing (no spurious fanout)`` () =
        let root, ops, build, tests = writeFixture "0.6.3"

        try
            let before = projectInfosFor ops build tests

            let prior =
                before
                |> List.map (fun p -> p.ProjectName, p.DependencyFingerprint)
                |> Map.ofList
            // Re-read with NO change.
            let after = projectInfosFor ops build tests
            let changed = diffFingerprints prior after
            test <@ Set.isEmpty changed @>
            test <@ List.isEmpty (selectTestsForChangedProjects after buildTests changed) @>
        finally
            Directory.Delete(root, true)
