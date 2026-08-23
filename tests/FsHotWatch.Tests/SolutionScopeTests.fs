module FsHotWatch.Tests.SolutionScopeTests

// AUTOMATION-158. The contract under test, in one sentence: a test project that
// is in the solution and in no gated list cannot produce a green.
//
// The end-to-end regression the ticket asks for is
// ``a dummy test project in the solution and absent from the config makes the
// config load FAIL`` — a real solution file, a real project file with a real
// runner marker, and `loadConfig` (the function every fshw verb calls) refusing.
// Everything above it is the matrix, proved on the pure functions.

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.SolutionScope
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

/// A project that declares a test runner. The marker is the modern MTP one; the
/// VSTest-era markers get their own test below.
let private testProjectXml =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.2" />
  </ItemGroup>
</Project>"""

/// A LIBRARY that mentions xunit and runs nothing — the shape a directory
/// heuristic or a bare "mentions xunit" match gets wrong.
let private libraryXml =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3.extensibility.core" Version="3.2.2" />
  </ItemGroup>
</Project>"""

let private gated (project: string) (args: string) : GatedProject = { Project = project; Args = args }

let private excluded (project: string) (reason: string) : Exclusion = { Project = project; Reason = reason }

/// Stage a repo: a solution naming `projects`, each project written to
/// `<dir>/<name>.fsproj` with the given XML.
let private stageRepo (root: string) (solutionName: string) (projects: (string * string) list) =
    let entries =
        projects
        |> List.map (fun (dir, _) ->
            let name = dir.Split('/') |> Array.last
            $"""  <Project Path="{dir}/{name}.fsproj" />""")
        |> String.concat "\n"

    File.WriteAllText(Path.Combine(root, solutionName), $"<Solution>\n{entries}\n</Solution>\n")

    for dir, xml in projects do
        let full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory full |> ignore
        let name = dir.Split('/') |> Array.last
        File.WriteAllText(Path.Combine(full, $"{name}.fsproj"), xml)

// ---------------------------------------------------------------------------
// Reading a solution
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``solutionProjects reads the .slnx element form`` () =
    let text =
        """<Solution>
  <Project Path="src/App/App.fsproj" />
  <Folder Name="/tests/">
    <Project Path="tests/App.Tests/App.Tests.fsproj" />
  </Folder>
</Solution>"""

    test <@ solutionProjects text = [ "src/App/App.fsproj"; "tests/App.Tests/App.Tests.fsproj" ] @>

[<Fact(Timeout = 15000)>]
let ``solutionProjects reads the classic .sln line form, backslashes and all`` () =
    let text =
        "Microsoft Visual Studio Solution File, Format Version 12.00\n\
         Project(\"{6EC3EE1D}\") = \"App\", \"src\\App\\App.fsproj\", \"{A}\"\n\
         EndProject\n\
         Project(\"{6EC3EE1D}\") = \"App.Tests\", \"tests\\App.Tests\\App.Tests.fsproj\", \"{B}\"\n\
         EndProject\n"

    test <@ solutionProjects text = [ "src/App/App.fsproj"; "tests/App.Tests/App.Tests.fsproj" ] @>

[<Fact(Timeout = 15000)>]
let ``solutionProjects sees csproj too — a shim project is still a project`` () =
    let text = """<Solution><Project Path="tools/shim/shim.csproj" /></Solution>"""
    test <@ solutionProjects text = [ "tools/shim/shim.csproj" ] @>

// ---------------------------------------------------------------------------
// Classifying
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``isTestProject: a runner marker is what makes a test project`` () = test <@ isTestProject testProjectXml @>

[<Fact(Timeout = 15000)>]
let ``isTestProject: a library that merely references xunit runs nothing`` () =
    test <@ not (isTestProject libraryXml) @>

[<Fact(Timeout = 15000)>]
let ``isTestProject: the VSTest-era markers count too`` () =
    // A suite added with the older runner must not slip through a detector built
    // for the newer one — the false-negative direction is the one that is fatal.
    let vstest =
        """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" />
  </ItemGroup>
</Project>"""

    test <@ isTestProject vstest @>

[<Fact(Timeout = 15000)>]
let ``isTestProject: an explicit IsTestProject property is enough`` () =
    test <@ isTestProject "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>" @>

// ---------------------------------------------------------------------------
// Identities — the same project, spelled four ways
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``normalisePath makes the four spellings of one project equal`` () =
    let expected = "tests/App.Tests"

    test <@ normalisePath "tests/App.Tests" = expected @>
    test <@ normalisePath "tests/App.Tests/" = expected @>
    test <@ normalisePath "./tests/App.Tests" = expected @>
    test <@ normalisePath "tests\\App.Tests\\App.Tests.fsproj" = expected @>

[<Fact(Timeout = 15000)>]
let ``a bare project name resolves to its solution directory`` () =
    // `.fshw.json` in this very repo spells its suite `"project": "FsHotWatch.Tests"`
    // while the solution spells it `tests/FsHotWatch.Tests/FsHotWatch.Tests.fsproj`.
    // If those did not reconcile, every repo on earth would report its own gated
    // suites as phantoms.
    let findings =
        decide [ "tests/App.Tests" ] [ "tests/App.Tests" ] [ gated "App.Tests" "" ] []

    test <@ List.isEmpty findings @>

[<Fact(Timeout = 15000)>]
let ``a --project path in args resolves even when the label does not`` () =
    let findings =
        decide
            [ "tests/App.Tests" ]
            [ "tests/App.Tests" ]
            [ gated "the friendly name" "run --project tests/App.Tests --no-build --" ]
            []

    test <@ List.isEmpty findings @>

// ---------------------------------------------------------------------------
// THE DEFECT
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a solution test project the config does not gate is an UndeclaredTestProject`` () =
    let findings =
        decide
            [ "src/App"; "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ gated "App.Tests" "" ]
            []

    test <@ findings = [ UndeclaredTestProject "tests/App.IntegrationTests" ] @>

[<Fact(Timeout = 15000)>]
let ``the SAME omission, declared with a reason, is clean`` () =
    // The other direction, and the ticket's own rule: a declared, reasoned
    // exclusion is not the bug — the silence is. If this went red, the escape
    // hatch would be no escape and every repo would be forced to run everything.
    let findings =
        decide
            [ "src/App"; "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ gated "App.Tests" "" ]
            [ excluded "tests/App.IntegrationTests" "slow end-to-end suite; run by `mise run test-integration`" ]

    test <@ List.isEmpty findings @>

[<Fact(Timeout = 15000)>]
let ``an exclusion with a blank reason is a silence wearing a declaration's clothes`` () =
    let findings =
        decide
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ gated "App.Tests" "" ]
            [ excluded "tests/App.IntegrationTests" "   " ]

    test <@ findings = [ ExclusionMissingReason "tests/App.IntegrationTests" ] @>

[<Fact(Timeout = 15000)>]
let ``an exclusion naming nothing in the solution is stale — and its target goes undeclared`` () =
    // Both findings matter and both fire: the exclusion has rotted, AND the
    // project it was meant to cover is now governed by nothing.
    let findings =
        decide
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ "tests/App.Tests"; "tests/App.IntegrationTests" ]
            [ gated "App.Tests" "" ]
            [ excluded "tests/App.IntegrationTest" "typo — trailing s dropped" ]

    test
        <@
            findings = [ UndeclaredTestProject "tests/App.IntegrationTests"
                         StaleExclusion "tests/App.IntegrationTest" ]
        @>

[<Fact(Timeout = 15000)>]
let ``gated AND excluded is a contradiction with no safe guess`` () =
    let findings =
        decide
            [ "tests/App.Tests" ]
            [ "tests/App.Tests" ]
            [ gated "App.Tests" "" ]
            [ excluded "tests/App.Tests" "we changed our mind halfway through the edit" ]

    test <@ findings = [ GatedAndExcluded "tests/App.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``a gated project the solution does not contain is named, not ignored`` () =
    let findings =
        decide [ "tests/App.Tests" ] [ "tests/App.Tests" ] [ gated "App.Tests" ""; gated "App.Deleted.Tests" "" ] []

    test <@ findings = [ GatedProjectNotInSolution "App.Deleted.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``a gated project counts as a test project even when the classifier misses its runner`` () =
    // A repo whose runner this module does not recognise must not be told its
    // detector is blind and handed no way to act on it. The config's own claim is
    // stronger evidence than the heuristic.
    let findings = decide [ "tests/App.Tests" ] [] [ gated "App.Tests" "" ] []
    test <@ List.isEmpty findings @>

[<Fact(Timeout = 15000)>]
let ``FLOOR: a scan that recognised nothing at all cannot report a clean bill`` () =
    // The failure mode this floor exists for: a scan that saw nothing produces an
    // empty offender list, which is byte-identical to a fully governed repo.
    let findings = decide [ "src/App" ] [] [ gated "App.Tests" "" ] []
    test <@ findings = [ NoTestProjectsFound ] @>

// ---------------------------------------------------------------------------
// reconcile — on disk
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``reconcile: a dummy test project in the solution and out of the config is refused`` () =
    withTempDir "solscope-undeclared" (fun root ->
        stageRepo
            root
            "Repo.slnx"
            [ "src/App", libraryXml
              "tests/App.Tests", testProjectXml
              "tests/App.Dummy.Tests", testProjectXml ]

        let findings = reconcile root None [ gated "App.Tests" "" ] []

        test <@ findings = [ UndeclaredTestProject "tests/App.Dummy.Tests" ] @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: the same repo with a reasoned exclusion is clean`` () =
    withTempDir "solscope-declared" (fun root ->
        stageRepo
            root
            "Repo.slnx"
            [ "src/App", libraryXml
              "tests/App.Tests", testProjectXml
              "tests/App.Dummy.Tests", testProjectXml ]

        let findings =
            reconcile root None [ gated "App.Tests" "" ] [ excluded "tests/App.Dummy.Tests" "a written reason" ]

        test <@ List.isEmpty findings @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: no solution file means there is no universe to be complete against`` () =
    withTempDir "solscope-nosln" (fun root ->
        let findings = reconcile root None [ gated "App.Tests" "" ] []
        test <@ List.isEmpty findings @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: two solutions at the root and no tests.solution is ambiguous, not a guess`` () =
    withTempDir "solscope-ambiguous" (fun root ->
        stageRepo root "Repo.slnx" [ "tests/App.Tests", testProjectXml ]
        File.WriteAllText(Path.Combine(root, "Other.sln"), "")

        let findings = reconcile root None [ gated "App.Tests" "" ] []

        test <@ findings = [ AmbiguousSolution [ "Other.sln"; "Repo.slnx" ] ] @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: an explicit tests.solution picks the authority out of the ambiguity`` () =
    withTempDir "solscope-explicit" (fun root ->
        stageRepo root "Repo.slnx" [ "tests/App.Tests", testProjectXml ]
        File.WriteAllText(Path.Combine(root, "Other.sln"), "")

        let findings = reconcile root (Some "Repo.slnx") [ gated "App.Tests" "" ] []

        test <@ List.isEmpty findings @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: a tests.solution that is not there is a contradiction, not an absence`` () =
    withTempDir "solscope-missing-sln" (fun root ->
        let findings = reconcile root (Some "Nope.slnx") [ gated "App.Tests" "" ] []
        test <@ findings = [ SolutionNotFound "Nope.slnx" ] @>)

// ---------------------------------------------------------------------------
// Reading the declarations
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``parseExclusions: a config that cannot be parsed does not get to say "nothing excluded"`` () =
    test <@ parseExclusions "{ not json" = None @>
    test <@ parseExclusions "{}" = Some [] @>

[<Fact(Timeout = 15000)>]
let ``parseExclusions: an entry with no reason survives with an empty one`` () =
    let json = """{"tests":{"excluded":[{"project":"tests/X"}]}}"""

    test <@ parseExclusions json = Some [ { Project = "tests/X"; Reason = "" } ] @>

[<Fact(Timeout = 30000)>]
let ``readExclusions: no .fshw.json is "does not say", not "excludes nothing"`` () =
    withTempDir "solscope-noconfig" (fun root -> test <@ readExclusions root = None @>)

// ---------------------------------------------------------------------------
// The message
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``the message names the project, the solution, and both ways out`` () =
    let message =
        describeFindings "Repo.slnx" [ UndeclaredTestProject "tests/App.Dummy.Tests" ]

    test <@ message.Contains "tests/App.Dummy.Tests" @>
    test <@ message.Contains "Repo.slnx" @>
    test <@ message.Contains "tests.projects" @>
    test <@ message.Contains "tests.excluded" @>

// ---------------------------------------------------------------------------
// Every finding renders to something a reader can act on
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``every finding renders a line that names what is wrong`` () =
    // A refusal nobody can act on is its own kind of silence, so each case gets
    // its own sentence and each sentence has to survive a rename. Exhaustive by
    // construction: a new `Finding` with no arm here is a compile error in
    // `renderFinding` and an absent row here.
    let render f = renderFinding "Repo.slnx" f

    test <@ (render (UndeclaredTestProject "tests/X")).Contains "tests/X" @>
    test <@ (render (ExclusionMissingReason "tests/X")).Contains "reason" @>
    test <@ (render (StaleExclusion "tests/X")).Contains "Repo.slnx" @>
    test <@ (render (GatedAndExcluded "tests/X")).Contains "Pick one" @>
    test <@ (render (GatedProjectNotInSolution "tests/X")).Contains "tests/X" @>
    test <@ (render NoTestProjectsFound).Contains "blind" @>
    test <@ (render (AmbiguousSolution [ "A.sln"; "B.slnx" ])).Contains "tests.solution" @>
    test <@ (render (SolutionNotFound "Nope.slnx")).Contains "Nope.slnx" @>

// ---------------------------------------------------------------------------
// The awkward inputs
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``normalisePath: a project file at the repo root keeps its name, not its extension`` () =
    // No directory to fall back to. `App.fsproj` would match nothing a config can
    // plausibly spell; `App` matches the obvious one.
    test <@ normalisePath "App.fsproj" = "App" @>
    test <@ normalisePath "App" = "App" @>

[<Fact(Timeout = 15000)>]
let ``normalisePath and the classifiers survive null without throwing`` () =
    // These read files and JSON off disk. A parser that throws on the absurd input
    // turns a config error into a crash, and a crash is not a finding.
    test <@ normalisePath null = "" @>
    test <@ List.isEmpty (solutionProjects null) @>
    test <@ not (isTestProject null) @>

[<Fact(Timeout = 15000)>]
let ``identitiesOf: a root-level project has ONE identity, not a duplicated pair`` () =
    test <@ identitiesOf "App.fsproj" = [ "App" ] @>
    test <@ identitiesOf "tests/App.Tests/App.Tests.fsproj" = [ "tests/App.Tests"; "App.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``projectFromArgs finds the path, and says None rather than guessing`` () =
    test <@ projectFromArgs "run --project tests/App.Tests --no-build --" = Some "tests/App.Tests" @>
    test <@ projectFromArgs "test --filter Foo" = None @>
    // `--project` with nothing after it: a truncated command line, not a project.
    test <@ projectFromArgs "run --project" = None @>
    test <@ projectFromArgs "" = None @>
    test <@ projectFromArgs "   " = None @>

[<Fact(Timeout = 15000)>]
let ``identitiesOfGated: the label and the args path are both offered, deduplicated`` () =
    test
        <@
            identitiesOfGated
                { Project = "App.Tests"
                  Args = "run --project tests/App.Tests --" } = [ "App.Tests"; "tests/App.Tests" ]
        @>

    // Same project spelled twice is one identity, not two.
    test
        <@
            identitiesOfGated
                { Project = "tests/App.Tests"
                  Args = "run --project tests/App.Tests --" } = [ "tests/App.Tests" ]
        @>

    test <@ List.isEmpty (identitiesOfGated { Project = ""; Args = "" }) @>

[<Fact(Timeout = 30000)>]
let ``reconcile: a test project at the REPO ROOT is seen, not lost with its directory`` () =
    // Regression. `solutionProjects` used to reduce every entry to its directory,
    // which for `App.Tests.fsproj` at the root is the file's own name minus the
    // extension — a path that does not exist, so the project read as "not a test
    // project" and vanished from the scope. The exact silence this module exists
    // to end, reintroduced by the module itself.
    withTempDir "solscope-rootproj" (fun root ->
        File.WriteAllText(Path.Combine(root, "App.Tests.fsproj"), testProjectXml)

        File.WriteAllText(
            Path.Combine(root, "Repo.slnx"),
            """<Solution><Project Path="App.Tests.fsproj" /></Solution>"""
        )

        // Both findings, and both right: the root project is SEEN (it used to
        // vanish), and the gated name that is in no solution is called out.
        test
            <@
                reconcile root None [ gated "Other.Tests" "" ] [] = [ UndeclaredTestProject "App.Tests"
                                                                      GatedProjectNotInSolution "Other.Tests" ]
            @>

        // ...and it is gateable under the name a config would spell.
        test <@ List.isEmpty (reconcile root None [ gated "App.Tests" "" ] []) @>)

[<Fact(Timeout = 30000)>]
let ``reconcile: a solution naming a project that is not on disk classifies as not-a-test`` () =
    // The solution's problem, not this check's. Reporting it here would bury the
    // one thing this check can speak to.
    withTempDir "solscope-ghost" (fun root ->
        stageRepo root "Repo.slnx" [ "tests/App.Tests", testProjectXml ]

        File.WriteAllText(
            Path.Combine(root, "Repo.slnx"),
            """<Solution>
  <Project Path="tests/App.Tests/App.Tests.fsproj" />
  <Project Path="tests/Gone/Gone.fsproj" />
</Solution>"""
        )

        test <@ List.isEmpty (reconcile root None [ gated "App.Tests" "" ] []) @>)

[<Fact(Timeout = 15000)>]
let ``parseExclusions ignores shapes that are not declarations`` () =
    // A wrongly-typed `excluded`, and entries with no `project`, carry no
    // declaration. They must not become one, and must not crash the load either.
    test <@ parseExclusions """{"tests":{"excluded":"nope"}}""" = Some [] @>
    test <@ parseExclusions """{"tests":{"excluded":[{"reason":"orphan"},42]}}""" = Some [] @>
    test <@ parseExclusions """{"tests":{}}""" = Some [] @>

[<Fact(Timeout = 30000)>]
let ``readExclusions reads what is on disk`` () =
    withTempDir "solscope-readexcl" (fun root ->
        File.WriteAllText(
            Path.Combine(root, ".fshw.json"),
            """{"tests":{"excluded":[{"project":"tests/X","reason":"why"}]}}"""
        )

        test <@ readExclusions root = Some [ { Project = "tests/X"; Reason = "why" } ] @>)

[<Fact(Timeout = 30000)>]
let ``solutionNameFor names the authority, or says there is none to name`` () =
    withTempDir "solscope-name" (fun root ->
        test <@ solutionNameFor root None = "the solution" @>

        File.WriteAllText(Path.Combine(root, "Repo.slnx"), "<Solution />")
        test <@ solutionNameFor root None = "Repo.slnx" @>

        File.WriteAllText(Path.Combine(root, "Other.sln"), "")
        test <@ solutionNameFor root None = "the solution" @>
        test <@ solutionNameFor root (Some "Chosen.slnx") = "Chosen.slnx" @>)

[<Fact(Timeout = 15000)>]
let ``solutionCandidates on a directory that is not there is empty, not a throw`` () =
    test <@ List.isEmpty (solutionCandidates (Path.Combine(Path.GetTempPath(), "fshw-no-such-dir-158"))) @>
