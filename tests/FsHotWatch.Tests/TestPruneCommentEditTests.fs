/// An edit that changes a file's bytes but none of its symbols — a comment in a test
/// file — after a green run. The symbol diff owes nothing, but the tree the evidence was
/// earned on is gone, so what `test-scope` reports next must still be a verdict or a
/// named reason, never a silent "no tests ran".
module FsHotWatch.Tests.TestPruneCommentEditTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.TestPrune

let private testSource comment =
    $"module P1Tests\n\n// %s{comment}\nlet ``adds`` () = 1 + 1\n"

let private scopeOf (host: PluginHost) =
    match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
    | Some reply -> (parseTestRunReport reply).Scope
    | None -> failwith "no test-scope command"

/// Land one build and wait for the plugin to settle on it, whether it ran or replayed.
let private landBuild (host: PluginHost) =
    let terminal = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted BuildSucceeded
    Assert.True(terminal.Wait(TimeSpan.FromSeconds 15.0), "test-prune never reported a terminal status")
    waitForQuiescent host 20000

[<Fact(Timeout = 60000)>]
let ``a comment-only edit to a test file after a green run is re-verified`` () =
    withTempDir "tp-comment-edit" (fun tmpDir ->
        let testFile = Path.Combine(tmpDir, "tests", "P1", "Tests.fs")
        Directory.CreateDirectory(Path.GetDirectoryName testFile) |> ignore
        File.WriteAllText(testFile, testSource "original comment")
        let runs = Path.Combine(tmpDir, "p1-runs")
        // A repository that has earned its full-suite baseline before, as a checkout
        // used day to day has: the first run of this session is then an ordinary one,
        // and its green is what a later build of the same symbols is keyed against.
        seedBaseline tmpDir [ "P1" ]

        let config =
            { PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) with
                Args = $"-c \"echo run >> {runs}; exit 0\"" }

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some [ config ]) None None None None [])

        try
            // The session's first run executes P1 in full and earns evidence on this tree.
            landBuild host

            match scopeOf host with
            | FullSuite(1, _) -> ()
            | other -> Assert.Fail($"positive control: the first build runs P1 in full, got %A{other}")

            let runCount () = File.ReadAllLines(runs).Length
            let runsBefore = runCount ()

            // Only a comment changes: the symbols, the project files and the build
            // outcome are what they were.
            File.WriteAllText(testFile, testSource "edited comment")

            // Before any build: the evidence is for the old tree, and the reading says so.
            match scopeOf host with
            | NoTestsRun NoTestsReason.TreeMoved -> ()
            | other -> Assert.Fail($"an edited tree with no run on it must read tree-moved, got %A{other}")

            landBuild host

            match scopeOf host with
            | FullSuite(1, _) -> Assert.Equal(runsBefore + 1, runCount ())
            | other -> Assert.Fail($"a comment-only edit must be re-verified on the edited tree, got %A{other}")

            // The tree is verified now: another build over it runs nothing more.
            landBuild host
            Assert.Equal(runsBefore + 1, runCount ())

            match scopeOf host with
            | FullSuite(1, _) -> ()
            | other -> Assert.Fail($"the verified tree keeps its evidence, got %A{other}")
        finally
            host.Teardown())

[<Fact(Timeout = 30000)>]
let ``before any run the scope says no run has completed`` () =
    withTempDir "tp-no-run-yet" (fun tmpDir ->
        let config =
            PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never"))

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some [ config ]) None None None None [])

        try
            match scopeOf host with
            | NoTestsRun NoTestsReason.NoRunYet -> ()
            | other -> Assert.Fail($"expected no-run-yet, got %A{other}")
        finally
            host.Teardown())

let private receipt tree =
    { InputTreeHash = tree
      ModelGeneration = None
      ProjectInputs = None
      RunId = Guid.NewGuid()
      Coverage = RunCoverage.none
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero
      FullSuiteCause = FullSuiteCause.SelectionReachedEveryProject }

[<Fact(Timeout = 10000)>]
let ``every reading with no evidence for the tree names why`` () =
    let run = [ Guid.NewGuid() ]
    let classify = UnverifiedTree.classify

    Assert.Equal(UnverifiedTree.CoversNothing, classify (Some(receipt (Some "a"))) (Some "a") None run)
    Assert.Equal(UnverifiedTree.TreeMoved, classify (Some(receipt (Some "a"))) (Some "b") None run)
    Assert.Equal(UnverifiedTree.TreeUnreadable, classify (Some(receipt (Some "a"))) None None run)
    Assert.Equal(UnverifiedTree.Revoked "aborted", classify None (Some "a") (Some "aborted") run)
    Assert.Equal(UnverifiedTree.NoRunYet, classify None (Some "a") None [])

    match classify None (Some "a") None run with
    | UnverifiedTree.Revoked reason -> Assert.Contains("no test evidence", reason)
    | other -> Assert.Fail($"a completed run with no receipt and no revocation is still named, got %A{other}")

    Assert.Equal(("no-run-yet", None), UnverifiedTree.wire UnverifiedTree.NoRunYet)
    Assert.Equal(("tree-moved", None), UnverifiedTree.wire UnverifiedTree.TreeMoved)
    Assert.Equal(("tree-unreadable", None), UnverifiedTree.wire UnverifiedTree.TreeUnreadable)
    Assert.Equal(("evidence-revoked", Some "why"), UnverifiedTree.wire (UnverifiedTree.Revoked "why"))
    Assert.Equal(("covers-nothing", None), UnverifiedTree.wire UnverifiedTree.CoversNothing)

/// A two-file repository: `src/Lib.fs`, whose `Lib.f` the class `LibTests` covers, and
/// a model that compiles it. The runner appends its filter (or `full`) to `runs` and
/// fails while `flag` exists — a behaviour change the symbol diff never saw.
type private NarrowingRepo =
    { Host: PluginHost
      Source: string
      Runs: string
      Flag: string
      Root: string }

let private libSource comment =
    $"module Lib\n\n// %s{comment}\nlet f () = 1\n"

let private withNarrowingRepo (body: NarrowingRepo -> unit) =
    withTempDir "tp-symbol-free" (fun tmpDir ->
        let source = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.GetDirectoryName source) |> ignore
        File.WriteAllText(source, libSource "original")
        let runs = Path.Combine(tmpDir, "p1-runs")
        let flag = Path.Combine(tmpDir, "behaviour-changed")
        seedBaseline tmpDir [ "P1" ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = TestPrune.Database.Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "P1" "LibTests" "f works"

        let config =
            { PendingQueueHelpers.flagConfig tmpDir "P1" flag with
                // `$0 $1` is the filter `--filter-class <classes>`, or `sh` for a whole-project run.
                Args = $"-c \"echo $0 $1 >> {runs}; if [ -f {flag} ]; then exit 1; else exit 0; fi\""
                FilterTemplate = Some "--filter-class {classes}" }

        let host =
            PluginHost(
                Unchecked.defaultof<_>,
                tmpDir,
                taskCache = (FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache)
            )

        host.WorkStore.PublishProjectModelWithFiles(fixtureModel, Set.ofList [ AbsFilePath.create source ])
        host.RegisterHandler(create dbPath tmpDir (Some [ config ]) None None None None [])

        try
            body
                { Host = host
                  Source = source
                  Runs = runs
                  Flag = flag
                  Root = tmpDir }
        finally
            host.Teardown())

let private lastRun (repo: NarrowingRepo) =
    File.ReadAllLines(repo.Runs) |> Array.last

let private runCount (repo: NarrowingRepo) = File.ReadAllLines(repo.Runs).Length

/// The symbol diff folds the edited file and reports nothing changed, then the build lands.
let private foldAndBuild (repo: NarrowingRepo) =
    emitBatchAndQuiesce repo.Host [ repo.Source ]
    landBuild repo.Host

[<Fact(Timeout = 60000)>]
let ``a comment-only edit to a covered source file runs only the classes covering it`` () =
    withNarrowingRepo (fun repo ->
        landBuild repo.Host
        // Positive control: the session's first run takes P1 whole.
        test <@ lastRun repo = "sh" @>
        let before = runCount repo

        File.WriteAllText(repo.Source, libSource "edited")
        foldAndBuild repo

        test <@ runCount repo = before + 1 @>
        test <@ lastRun repo = "--filter-class LibTests" @>

        match scopeOf repo.Host with
        | ImpactFiltered _ -> ()
        | other -> Assert.Fail($"the narrowed run's evidence is bound to the edited tree, got %A{other}")

        // The edited tree is verified: another build over it runs nothing.
        landBuild repo.Host
        test <@ runCount repo = before + 1 @>)

[<Fact(Timeout = 60000)>]
let ``a behaviour change the symbol diff does not see still fails through the covering tests`` () =
    withNarrowingRepo (fun repo ->
        landBuild repo.Host
        let before = runCount repo

        // The diff reports nothing changed, but the code under `LibTests` now fails.
        File.WriteAllText(repo.Flag, "")
        File.WriteAllText(repo.Source, libSource "edited")
        foldAndBuild repo

        test <@ runCount repo = before + 1 @>
        test <@ lastRun repo = "--filter-class LibTests" @>

        match repo.Host.GetStatus("test-prune") with
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"the covering tests failed, so the check must be red, got %A{other}"))

[<Fact(Timeout = 60000)>]
let ``a symbol-free edit to a file no model compiles still runs every project in full`` () =
    withNarrowingRepo (fun repo ->
        landBuild repo.Host
        let before = runCount repo

        let notes = Path.Combine(repo.Root, "src", "notes.txt")
        File.WriteAllText(notes, "a note")
        emitBatchAndQuiesce repo.Host [ repo.Source ]
        landBuild repo.Host

        test <@ runCount repo = before + 1 @>
        test <@ lastRun repo = "sh" @>)

// --- SymbolFreeEdit.select: every refusal keeps the whole-project run.

let private tree (entries: (string * string) list) : ReceiptTree =
    { Identity = entries |> List.map (fun (p, h) -> $"%s{p}=%s{h}") |> String.concat ";"
      Entries = entries }

let private before = tree [ "src/Lib.fs", "h1"; "src/Other.fs", "o1" ]
let private after = tree [ "src/Lib.fs", "h2"; "src/Other.fs", "o1" ]

let private method project cls : TestMethodInfo =
    { SymbolFullName = $"%s{cls}.m"
      TestProject = project
      TestClass = cls
      TestMethod = "m" }

let private index: SymbolFreeEdit.Index =
    { SymbolsIn =
        function
        | "src/Lib.fs" -> [ "Lib.f"; "Lib.g" ]
        | _ -> []
      TestsIn = fun _ -> []
      Affected =
        fun seeds ->
            [ if List.contains "Lib.f" seeds then
                  method "P1" "LibTests"
              if List.contains "Lib.g" seeds then
                  method "P2" "GTests"
                  method "Unrun" "Elsewhere" ] }

let private compiledAll = Some(fun (_: string) -> true)
let private runnable project = project <> "Unrun"

let private selectWith ev cur sealedTree compiled ix =
    SymbolFreeEdit.select ev cur sealedTree compiled runnable ix

let private refusedWith (fragment: string) =
    function
    | SymbolFreeEdit.Refused reason -> reason.Contains fragment
    | SymbolFreeEdit.Covering _ -> false

[<Fact>]
let ``a symbol-free edit to a compiled source file runs the classes covering every symbol it holds`` () =
    let narrowed =
        selectWith (Some before) (Some after) (Some after.Identity) compiledAll index

    test
        <@
            narrowed = SymbolFreeEdit.Covering(
                [ "src/Lib.fs" ],
                [ "Lib.f"; "Lib.g" ],
                Map.ofList [ "P1", [ "LibTests" ]; "P2", [ "GTests" ] ]
            )
        @>

[<Fact>]
let ``tests declared in the edited file are run with those covering it`` () =
    let ix =
        { index with
            TestsIn =
                function
                | "src/Lib.fs" -> [ method "P1" "InFileTests" ]
                | _ -> [] }

    match selectWith (Some before) (Some after) (Some after.Identity) compiledAll ix with
    | SymbolFreeEdit.Covering(_, _, classes) -> test <@ classes["P1"] = [ "InFileTests"; "LibTests" ] @>
    | other -> Assert.Fail $"expected a narrowing, got %A{other}"

[<Fact>]
let ``an edit the symbol diff has not folded yet is not narrowed`` () =
    test
        <@
            selectWith (Some before) (Some after) None compiledAll index
            |> refusedWith "not folded"
        @>

    test
        <@
            selectWith (Some before) (Some after) (Some before.Identity) compiledAll index
            |> refusedWith "not folded"
        @>

[<Fact>]
let ``without the evidence tree's manifest, a readable tree or a model, nothing is narrowed`` () =
    test
        <@
            selectWith None (Some after) (Some after.Identity) compiledAll index
            |> refusedWith "manifest"
        @>

    test
        <@
            selectWith (Some before) None (Some after.Identity) compiledAll index
            |> refusedWith "could not be read"
        @>

    test
        <@
            selectWith (Some before) (Some after) (Some after.Identity) None index
            |> refusedWith "project model"
        @>

[<Fact>]
let ``any path that is not an edited compiled F# source file keeps the whole-project run`` () =
    let moved entries =
        selectWith (Some before) (Some(tree entries)) (Some (tree entries).Identity) compiledAll index

    // A non-source file beside the edit.
    test
        <@
            moved [ "src/Lib.fs", "h2"; "src/Other.fs", "o1"; "src/data.json", "d" ]
            |> refusedWith "src/data.json"
        @>
    // A removed file.
    test <@ moved [ "src/Lib.fs", "h2" ] |> refusedWith "removed" @>
    // A signature file, a project file.
    let swapped name =
        selectWith
            (Some(tree [ name, "a" ]))
            (Some(tree [ name, "b" ]))
            (Some (tree [ name, "b" ]).Identity)
            compiledAll
            index

    test <@ swapped "src/Lib.fsi" |> refusedWith "src/Lib.fsi" @>
    test <@ swapped "src/Lib.fsproj" |> refusedWith "src/Lib.fsproj" @>

    // A source file the model does not compile.
    test
        <@
            selectWith (Some before) (Some after) (Some after.Identity) (Some(fun _ -> false)) index
            |> refusedWith "src/Lib.fs"
        @>

[<Fact>]
let ``a file the index holds no symbol or covering test for keeps the whole-project run`` () =
    let unknown = { index with SymbolsIn = fun _ -> [] }

    test
        <@
            selectWith (Some before) (Some after) (Some after.Identity) compiledAll unknown
            |> refusedWith "no symbol"
        @>

    let uncovered =
        { index with
            Affected = fun _ -> [ method "Unrun" "Elsewhere" ] }

    test
        <@
            selectWith (Some before) (Some after) (Some after.Identity) compiledAll uncovered
            |> refusedWith "no runnable test covers"
        @>

[<Fact>]
let ``a tree whose identity moved with no path moving is not narrowed`` () =
    let relabelled = { before with Identity = "other" }

    test
        <@
            selectWith (Some before) (Some relabelled) (Some relabelled.Identity) compiledAll index
            |> refusedWith "no path moved"
        @>
