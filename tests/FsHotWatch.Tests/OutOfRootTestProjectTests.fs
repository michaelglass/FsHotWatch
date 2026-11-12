/// AUTOMATION-1617: a test project named in `.fshw.json` `tests.projects` that lives
/// OUTSIDE the discovery roots (`src/`, `tests/`) — FsHotWatch's own
/// `FsHotWatch.Bench.Tests` under `bench/` is the live instance.
///
/// The positive control the ticket asks for: after a green, edit a test (and,
/// separately, a source file) under that project. The next check must either RUN it
/// or SAY it cannot vouch for it. Before the fix neither happened: the edit did not move
/// the verdict tree, so a stored green still applied, and a first whole run of the
/// project was carried as whole-project coverage for the rest of the model generation.
module FsHotWatch.Tests.OutOfRootTestProjectTests

open System
open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private projectReferencing (references: string list) =
    let items =
        references
        |> List.map (fun r -> $"""<ProjectReference Include="%s{r}" />""")
        |> String.concat ""

    $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>%s{items}</ItemGroup></Project>"""

/// A repository with one in-root library and test project, and one CONFIGURED test
/// project under `bench/` that references a project beside it, laid out as
/// FsHotWatch's own repository is. `bench/notes/` belongs to no project.
let private makeRepo (root: string) =
    write root "src/Lib/Lib.fsproj" (projectReferencing [])
    write root "src/Lib/Lib.fs" "module Lib\nlet answer = 42\n"
    write root "tests/Lib.Tests/Lib.Tests.fsproj" (projectReferencing [ "../../src/Lib/Lib.fsproj" ])
    write root "tests/Lib.Tests/Tests.fs" "module Tests\nlet t () = ()\n"
    write root "bench/Bench/Bench.fsproj" (projectReferencing [ "../../src/Lib/Lib.fsproj" ])
    write root "bench/Bench/Workload.fs" "module Workload\nlet size = 10\n"
    write root "bench/Bench.Tests/Bench.Tests.fsproj" (projectReferencing [ "../Bench/Bench.fsproj" ])
    write root "bench/Bench.Tests/BenchTests.fs" "module BenchTests\nlet t () = ()\n"
    write root "bench/notes/README.md" "notes\n"

    write
        root
        ".fshw.json"
        """{
  "tests": {
    "projects": [
      { "project": "Lib.Tests", "command": "dotnet",
        "args": "run --project tests/Lib.Tests --no-build --" },
      { "project": "Bench.Tests", "command": "dotnet",
        "args": "run --project bench/Bench.Tests --no-build --" }
    ]
  }
}"""

/// The verdict tree before and after `edit`: equal means a green stored before the edit
/// still applies after it (`Verdict.report` compares exactly this hash).
let private treeAcross (edit: string -> unit) =
    withTempDir "out-of-root" (fun root ->
        makeRepo root
        let before = TreeHash.compute root []
        edit root
        let after = TreeHash.compute root []
        before.Hash, after.Hash)

let private rewrite (rel: string) (text: string) (root: string) = write root rel text

[<Fact>]
let ``CONTROL an edit inside the discovery roots moves the verdict tree`` () =
    let before, after =
        treeAcross (rewrite "tests/Lib.Tests/Tests.fs" "module Tests\nlet t () = failwith \"x\"\n")

    test <@ before <> after @>

[<Fact>]
let ``an edit to a TEST under a configured out-of-root test project moves the verdict tree`` () =
    let before, after =
        treeAcross (rewrite "bench/Bench.Tests/BenchTests.fs" "module BenchTests\nlet t () = failwith \"x\"\n")

    test <@ before <> after @>

[<Fact>]
let ``an edit to a SOURCE file under a configured out-of-root test project moves the verdict tree`` () =
    // `bench/Bench` is the project `Bench.Tests` references: no test project itself, and
    // outside every discovery root, but in the closure of one the config runs.
    let before, after =
        treeAcross (rewrite "bench/Bench/Workload.fs" "module Workload\nlet size = 11\n")

    test <@ before <> after @>

[<Fact>]
let ``CONTROL an edit outside the roots and every configured project's closure does not move the tree`` () =
    // The hash grows by what is built and tested, not by everything beside it: a file no
    // configured project reaches is not an input.
    let before, after = treeAcross (rewrite "bench/notes/README.md" "more notes\n")
    test <@ before = after @>

[<Fact>]
let ``the walk names a configured out-of-root project and its closure as roots`` () =
    withTempDir "out-of-root-roots" (fun root ->
        makeRepo root
        let walked = TreeHash.files root []
        test <@ walked.Roots = [ "src"; "tests"; "bench/Bench"; "bench/Bench.Tests" ] @>

        test
            <@
                walked.Files
                |> List.exists (fun (rel, _) -> rel = "bench/Bench.Tests/BenchTests.fs")
            @>

        test
            <@
                walked.Files
                |> List.forall (fun (rel, _) -> not (rel.StartsWith "bench/notes/"))
            @>

        test <@ List.isEmpty walked.Unlocated @>)

[<Fact>]
let ``a configured test project that cannot be located is an entry of the hash, named`` () =
    withTempDir "out-of-root-unlocated" (fun root ->
        makeRepo root

        let config (args: string) =
            $"""{{ "tests": {{ "projects": [ {{ "project": "Ghost.Tests", "command": "dotnet", "args": "%s{args}" }} ] }} }}"""

        // No `--project` path, and no `Ghost.Tests.fsproj` under the roots.
        write root ".fshw.json" (config "test")
        let walked = TreeHash.files root []
        test <@ walked.Unlocated |> List.map fst = [ "Ghost.Tests" ] @>
        let unlocatedHash = (TreeHash.ofWalked walked).Hash
        let withoutEntry = TreeHash.ofWalked { walked with Unlocated = [] }
        test <@ unlocatedHash <> withoutEntry.Hash @>

        test
            <@
                TreeHash.describeCoverage (TreeHash.coverage root walked)
                |> List.exists (fun line -> line.Contains "Ghost.Tests")
            @>

        // A `--project` path that names no project is unlocated too.
        write root ".fshw.json" (config "run --project bench/Missing --no-build")
        test <@ (TreeHash.files root []).Unlocated |> List.map fst = [ "Ghost.Tests" ] @>

        // A configured project found by name under the roots is located: the walk hashes it.
        write root "tests/Ghost.Tests/Ghost.Tests.fsproj" (projectReferencing [])
        write root ".fshw.json" (config "test")
        test <@ List.isEmpty (TreeHash.files root []).Unlocated @>)

let private completed runId results =
    { RunId = runId
      TotalElapsed = TimeSpan.Zero
      Outcome = Normal
      Results = results
      Verification = RunVerification.ofResults results }

[<Fact>]
let ``a configured project the model does not name is never carried as whole-project coverage`` () =
    // The model knows `Lib.Tests` only: `Bench.Tests` ran (its command needs no model), so
    // its result is in the completion, but no `.fsproj` or compile-input identity is known
    // for it. Nothing can show a later run's `Bench.Tests` is the one that passed here, and
    // nothing re-selects it (no symbol edges, no graph fanout), so carrying it would make
    // every later edit under `bench/` invisible for the rest of the generation.
    let inputs = Map.ofList [ "tests/Lib.Tests/Lib.Tests.fsproj", "t" ]

    let results =
        Map.ofList
            [ "Lib.Tests", TestsPassed("passed", false, TimeSpan.Zero)
              "Bench.Tests", TestsPassed("passed", false, TimeSpan.Zero) ]

    let completion = completed (Guid.NewGuid()) results

    let earn (expected: string list) =
        EarnedEvidence.fromCompletion
            completion.RunId
            (Some 1L)
            (Some 1L)
            (Some inputs)
            (Map.ofList [ "Lib.Tests", "tests/Lib.Tests/Lib.Tests.fsproj" ])
            (Set.ofList expected)
            0
            None
            completion
        |> Option.get

    let evidence = earn [ "Lib.Tests" ]

    let carried = EarnedEvidence.carriedCoverage (Some 1L) (Some inputs) (Some evidence)

    // CONTROL: the modelled project is covered, so the assertion below is not met by a
    // carry that covers nothing.
    test <@ carried.Contains "Lib.Tests" @>
    test <@ not (carried.Contains "Bench.Tests") @>

    // Expected and run in full here, it vouches for THIS completion — it ran — without a
    // refusal; it is only never carried to the next one.
    let both = earn [ "Lib.Tests"; "Bench.Tests" ]
    test <@ List.isEmpty both.FailureReasons @>

    test <@ not ((EarnedEvidence.carriedCoverage (Some 1L) (Some inputs) (Some both)).Contains "Bench.Tests") @>

    // So the next launch owes it a whole run, every time.
    test
        <@
            EarnedEvidence.wholeProjectGap
                (Some 1L)
                (Some inputs)
                (Set.ofList [ "Lib.Tests"; "Bench.Tests" ])
                (Some both) = Set.singleton "Bench.Tests"
        @>

let private captureLogs () =
    let lines = ConcurrentQueue<string>()

    let sink =
        Logging.installSink
            { Write = lines.Enqueue
              Level = Logging.LogLevel.Debug
              File = None }

    lines, sink

[<Fact>]
let ``a configured project the model does not name is dropped from evidence with a line naming it`` () =
    let graph =
        { FsHotWatch.PluginFramework.ProjectGraphAccessor.none with
            GetAllProjects = fun () -> [ "/repo/tests/Lib.Tests/Lib.Tests.fsproj" ] }

    let configs =
        [ TestPrunePluginTestSupport.testConfigNamed "Lib.Tests"
          TestPrunePluginTestSupport.testConfigNamed "Bench.Tests.1617" ]

    let lines, sink = captureLogs ()

    let candidates =
        try
            PluginSteps.evidenceCandidates graph configs
        finally
            sink.Dispose()

    test <@ candidates |> List.map _.Project = [ "Lib.Tests" ] @>

    let named =
        lines |> Seq.filter (fun line -> line.Contains "Bench.Tests.1617") |> List.ofSeq

    test <@ named.Length = 1 @>
    test <@ named.Head.Contains "runs in full" @>
    test <@ lines |> Seq.forall (fun line -> not (line.Contains "Lib.Tests:")) @>

[<Fact>]
let ``a project whose K_P key failed is logged with the error, not silently dropped`` () =
    withTempDir "out-of-root-keyfail" (fun dir ->
        let wiring: TestEvidenceRun.Wiring =
            { Store =
                TestEvidenceStore.Store(
                    { Root = Path.Combine(dir, "evidence")
                      Ttl = TimeSpan.FromHours 1.0
                      MaxBytes = 1_000_000L
                      Now = fun () -> DateTimeOffset.UtcNow
                      Log = ignore }
                )
              Configs = Map.empty
              Toolchain = lazy "tc"
              Loaded = TestEvidenceKey.LoadedEnvironment.empty
              ProducerHash = "p"
              ExcludePatterns = []
              Outcomes = None }

        // No compile-input identity is known for the project: it cannot be keyed.
        let candidate: TestEvidenceRun.Candidate =
            { Project = "KeyFail.Tests.1617"
              ProjectFile = Path.Combine(dir, "tests", "KeyFail.Tests", "KeyFail.Tests.fsproj")
              Closure = []
              TestConfig = "{}" }

        let lines, sink = captureLogs ()

        let keys =
            try
                TestEvidenceRun.currentOutcomeKeys wiring dir (fun _ -> None) [ candidate ]
            finally
                sink.Dispose()

        test <@ Map.isEmpty keys @>

        let named =
            lines
            |> Seq.filter (fun line -> line.Contains "KeyFail.Tests.1617")
            |> List.ofSeq

        test <@ named.Length = 1 @>
        test <@ named.Head.Contains "no K_P key" @>)

// --- ConfiguredTestRoots: every way a configured project is located, or named as not ---

[<Fact>]
let ``projectArg reads both spellings, unquoted, and nothing else`` () =
    test <@ ConfiguredTestRoots.projectArg "run --project bench/X --no-build" = Some "bench/X" @>
    test <@ ConfiguredTestRoots.projectArg "run --project=\"bench/X\" --" = Some "bench/X" @>
    test <@ ConfiguredTestRoots.projectArg "run --project" = None @>
    test <@ ConfiguredTestRoots.projectArg "run --project=" = None @>
    test <@ ConfiguredTestRoots.projectArg "test" = None @>

[<Fact>]
let ``a config that cannot be read, or names no projects, configures nothing`` () =
    withTempDir "configured-none" (fun root ->
        test <@ List.isEmpty (ConfiguredTestRoots.configuredTestProjects root) @>

        for json in
            [ "{ not json"
              "{}"
              """{ "tests": {} }"""
              """{ "tests": [] }"""
              """{ "tests": { "projects": {} } }"""
              """{ "tests": { "projects": [ 1, { "project": "" }, { "args": "x" } ] } }""" ] do
            write root ".fshw.json" json
            test <@ List.isEmpty (ConfiguredTestRoots.configuredTestProjects root) @>

        write root ".fshw.json" """{ "tests": { "projects": [ { "project": "P" } ] } }"""
        test <@ ConfiguredTestRoots.configuredTestProjects root = [ "P", "" ] @>)

let private locateWith (root: string) (projects: (string * string) list) =
    let entries =
        projects
        |> List.map (fun (p, a) -> $"""{{ "project": "%s{p}", "args": "%s{a}" }}""")
        |> String.concat ","

    write root ".fshw.json" $"""{{ "tests": {{ "projects": [ %s{entries} ] }} }}"""

    let fsprojs =
        [ "src"; "tests" ]
        |> List.map (fun d -> Path.Combine(root, d))
        |> List.filter Directory.Exists
        |> List.collect (fun d ->
            SafeWalk.bestEffortFilePaths SafeWalk.ToolingExcludedDirs "*.fsproj" d
            |> List.ofSeq)

    ConfiguredTestRoots.locate root fsprojs

[<Fact>]
let ``a --project directory must hold exactly one project`` () =
    withTempDir "configured-dir" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "bench", "Empty")) |> ignore
        write root "bench/Two/A.fsproj" (projectReferencing [])
        write root "bench/Two/B.fsproj" (projectReferencing [])

        let located =
            locateWith root [ "Empty", "run --project bench/Empty"; "Two", "run --project bench/Two" ]

        test <@ List.isEmpty located.Directories @>
        test <@ located.Unlocated |> List.map fst = [ "Empty"; "Two" ] @>
        test <@ (snd located.Unlocated[0]).Contains "holds no .fsproj" @>
        test <@ (snd located.Unlocated[1]).Contains "more than one .fsproj" @>)

[<Fact>]
let ``a name that is ambiguous under the roots is not located`` () =
    withTempDir "configured-ambiguous" (fun root ->
        write root "src/A/P.fsproj" (projectReferencing [])
        write root "tests/B/P.fsproj" (projectReferencing [])
        let located = locateWith root [ "P", "test" ]
        test <@ located.Unlocated |> List.map fst = [ "P" ] @>
        test <@ (snd located.Unlocated.Head).Contains "ambiguous" @>)

[<Fact>]
let ``a closure is followed through the roots, and every reference it cannot follow is named`` () =
    withTempDir "configured-closure" (fun root ->
        // T (out of root, by .fsproj path) -> src/Lib (in root) -> extra/Deep (out of root).
        // T also references a project that does not exist, one outside the repository, and
        // one whose project file is not XML; and one at the repository root.
        write
            root
            "bench/T/T.fsproj"
            (projectReferencing
                [ "../../src/Lib/Lib.fsproj"
                  "../Gone/Gone.fsproj"
                  "../../../Elsewhere/E.fsproj"
                  "../Broken/Broken.fsproj"
                  "../../Root.fsproj"
                  "../../tests/Top.fsproj"
                  "Inner/Inner.fsproj" ])

        write root "bench/T/Inner/Inner.fsproj" (projectReferencing [])
        write root "src/Lib/Lib.fsproj" (projectReferencing [ "../../extra/Deep/Deep.fsproj" ])
        write root "extra/Deep/Deep.fsproj" (projectReferencing [ "../../src/Lib/Lib.fsproj" ])
        write root "bench/Broken/Broken.fsproj" "<Project"
        write root "Root.fsproj" (projectReferencing [])
        // A project directly in a discovery root is inside it: the walk already hashes it.
        write root "tests/Top.fsproj" (projectReferencing [])

        let located = locateWith root [ "T", "run --project bench/T/T.fsproj" ]

        let full (rel: string) =
            Path.GetFullPath(Path.Combine(root, rel))

        // `bench/T/Inner` lies inside `bench/T`: one walk covers both.
        test <@ located.Directories = [ full "bench/Broken"; full "bench/T"; full "extra/Deep" ] @>

        test <@ located.Unlocated |> List.map fst = [ "Broken"; "E"; "Gone"; "Root" ] @>

        let why name =
            located.Unlocated |> List.find (fst >> (=) name) |> snd

        test <@ (why "Broken").Contains "could not be read" @>
        test <@ (why "E").Contains "outside the repository" @>
        test <@ (why "Gone").Contains "does not exist" @>
        test <@ (why "Root").Contains "repository root" @>)
