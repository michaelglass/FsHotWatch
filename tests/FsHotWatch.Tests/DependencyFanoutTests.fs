module FsHotWatch.Tests.DependencyFanoutTests

open System.IO
open System.Text
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune.DependencyFanout

// Unit coverage for the pure dependency-fanout primitives. The end-to-end
// daemon behaviour (skip-gate closing) is covered in TestPrunePluginTests; these
// pin the fingerprint/diff building blocks directly, including the missing-DLL,
// unreadable-fsproj, and PackageReference-parse branches.

let private writeTempProject (body: string) =
    let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    let p = Path.Combine(dir, "Proj.fsproj")
    File.WriteAllText(p, body)
    dir, p

module ``directPackageVersions`` =

    [<Fact>]
    let ``extracts inline PackageReference versions, sorted`` () =
        let dir, fsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="CommandTree" Version="0.7.0" />
    <PackageReference Include="Aaa" Version="1.0.0" />
  </ItemGroup>
</Project>"""

        try
            test <@ directPackageVersions fsproj = [ ("Aaa", "1.0.0"); ("CommandTree", "0.7.0") ] @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a CPM reference (no inline Version) yields an empty version`` () =
        let dir, fsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" /></ItemGroup>
</Project>"""

        try
            test <@ directPackageVersions fsproj = [ ("CommandTree", "") ] @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``an unreadable / missing project yields an empty list`` () =
        test <@ List.isEmpty (directPackageVersions "/no/such/path/Nope.fsproj") @>

module ``computeProjectFingerprint`` =

    /// A graph where `testFsproj` references one library whose DLL is `dllPath`.
    let private graphWith (testFsproj: string) (libFsproj: string) (dllPath: string) =
        { ProjectGraphAccessor.none with
            GetProjectReferences = fun p -> if p = testFsproj then [ libFsproj ] else []
            GetCanonicalDllPath = fun p -> if p = libFsproj then Some dllPath else None }

    [<Fact>]
    let ``referenced-DLL content change flips the fingerprint`` () =
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let libFsproj = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(libFsproj, "<Project></Project>")
        let dll = Path.Combine(dir, "Lib.dll")

        try
            File.WriteAllBytes(dll, Encoding.UTF8.GetBytes "lib-v1")
            let graph = graphWith testFsproj libFsproj dll
            let before = computeProjectFingerprint graph testFsproj

            File.WriteAllBytes(dll, Encoding.UTF8.GetBytes "lib-v2-different")
            let after = computeProjectFingerprint graph testFsproj

            test <@ before <> after @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``a missing referenced DLL hashes to a stable sentinel (deterministic)`` () =
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let libFsproj = Path.Combine(dir, "Lib.fsproj")
        File.WriteAllText(libFsproj, "<Project></Project>")
        let missingDll = Path.Combine(dir, "does-not-exist.dll")

        try
            let graph = graphWith testFsproj libFsproj missingDll
            let a = computeProjectFingerprint graph testFsproj
            let b = computeProjectFingerprint graph testFsproj
            test <@ a = b @> // stable across reads (the "missing" sentinel)
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``the test project's own direct PackageReference version is folded in`` () =
        let dir, testFsproj =
            writeTempProject
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" Version="0.6.3" /></ItemGroup>
</Project>"""

        try
            let graph = ProjectGraphAccessor.none // no project refs
            let before = computeProjectFingerprint graph testFsproj

            File.WriteAllText(
                testFsproj,
                """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><PackageReference Include="CommandTree" Version="0.7.0" /></ItemGroup>
</Project>"""
            )

            let after = computeProjectFingerprint graph testFsproj
            test <@ before <> after @>
        finally
            Directory.Delete(dir, true)

    [<Fact>]
    let ``transitive references are walked (a deduped diamond doesn't loop)`` () =
        // test -> a, test -> b, a -> c, b -> c (c reached twice; must not loop).
        let dir, testFsproj = writeTempProject "<Project></Project>"
        let a = Path.Combine(dir, "A.fsproj")
        let b = Path.Combine(dir, "B.fsproj")
        let c = Path.Combine(dir, "C.fsproj")
        let cDll = Path.Combine(dir, "C.dll")

        for p in [ a; b; c ] do
            File.WriteAllText(p, "<Project></Project>")

        File.WriteAllBytes(cDll, Encoding.UTF8.GetBytes "c-binary")

        let graph =
            { ProjectGraphAccessor.none with
                GetProjectReferences =
                    fun p ->
                        if p = testFsproj then [ a; b ]
                        elif p = a || p = b then [ c ]
                        else []
                GetCanonicalDllPath = fun p -> if p = c then Some cDll else None }

        try
            // Terminates (no infinite loop) and incorporates C's DLL.
            let fp = computeProjectFingerprint graph testFsproj
            test <@ fp.Length > 0 @>
        finally
            Directory.Delete(dir, true)

module ``ProjectGraphAccessor none`` =

    [<Fact>]
    let ``the no-op accessor returns empty/None for every query`` () =
        let g = ProjectGraphAccessor.none
        test <@ List.isEmpty (g.GetAllProjects()) @>
        test <@ List.isEmpty (g.GetTransitiveDependentProjects "X.fsproj") @>
        test <@ List.isEmpty (g.GetProjectReferences "X.fsproj") @>
        test <@ g.GetCanonicalDllPath "X.fsproj" = None @>

module ``changedProjects`` =

    [<Fact>]
    let ``reports only projects whose fingerprint moved against a known prior`` () =
        let prior = Map.ofList [ "A", "fp1"; "B", "fp1" ]
        let current = Map.ofList [ "A", "fp2"; "B", "fp1" ] // A moved, B same
        test <@ changedProjects prior current = Set.ofList [ "A" ] @>

    [<Fact>]
    let ``a project with no prior fingerprint is not reported`` () =
        let current = Map.ofList [ "A", "fp1" ]
        test <@ Set.isEmpty (changedProjects Map.empty current) @>
