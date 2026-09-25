module TestPrune.Tests.NamedDispatchTests

// Named dispatch: a handler REGISTERED under a string name and a test that reaches it
// only by DISPATCHING that name (an HTTP path, a queue message, a CLI verb). The two
// never name each other, so the AST has no path between them; when the registry is also
// a composition root, the walk from the handler stops there and the test is never
// selected. The extension joins them by matching the literal name.
//
// Every narrowing-looking assertion here has its positive control beside it: the same
// fixture WITHOUT the dispatch literal must produce no edge, so an empty answer from a
// broken extension cannot pass for a correct one.

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.NamedDispatch
open TestPrune.Ports

let private appSource =
    """module App

type DispatchedAsAttribute(channel: string, name: string) =
    inherit System.Attribute()

type DispatchTemplateAttribute(channel: string, template: string) =
    inherit System.Attribute()

type CompositionRootAttribute() =
    inherit System.Attribute()

let mapRow (x: int) = x + 1

[<DispatchedAs("job", "Purge")>]
let runPurge () = mapRow 1

[<DispatchedAs("job", "PurgeAll")>]
let runPurgeAll () = 2

[<CompositionRoot>]
[<DispatchTemplate("job", "/admin/jobs/{action}/{name}")>]
let run (name: string) =
    match name with
    | "Purge" -> runPurge ()
    | _ -> runPurgeAll ()
"""

let private testSource (purgeUrl: string) =
    $"""module Tests

type FactAttribute() =
    inherit System.Attribute()

let post (url: string) = url.Length

[<Fact>]
let schedulesPurge () =
    post %s{purgeUrl} |> ignore

[<Fact>]
let runsPurgeAll () =
    post "/admin/jobs/run/PurgeAll" |> ignore

[<Fact>]
let rejectsUnknownJob () =
    post "/admin/jobs/run/NotAJob" |> ignore
"""

let private literalPurgeUrl = "\"/admin/jobs/schedule/Purge\""

/// The positive control's spelling: the same test, reaching no job by literal name.
let private noLiteralPurgeUrl = "(\"/admin/jobs/schedule/\" + string 42)"

[<Collection("FCS-NamedDispatch")>]
module ``named dispatch edges`` =

    let private checker = FSharpChecker.Create()

    /// Write the fixture to a fresh repo root and analyze each file for real, so the
    /// attributes and symbols come from FCS exactly as an indexer would store them.
    /// Write `source` to `relative` under `root` (replacing what was there) and analyze it.
    let private analyze (root: string) (relative: string) (projectName: string) (source: string) =
        File.WriteAllText(Path.Combine(root, relative), source)
        let fileName = Path.Combine(root, relative)
        let options = getScriptOptions checker fileName source |> Async.RunSynchronously

        match
            analyzeSource checker fileName source options projectName
            |> Async.RunSynchronously
        with
        | Ok result -> result
        | Error error -> failwith $"Analysis failed for {relative}: {error}"

    let private analyzeFixture (purgeUrl: string) =
        let root =
            Path.Combine(Path.GetTempPath(), $"testprune-dispatch-{Guid.NewGuid():N}")

        Directory.CreateDirectory root |> ignore

        let app = analyze root "App.fsx" "App" appSource
        let tests = analyze root "Tests.fsx" "Tests" (testSource purgeUrl)
        root, app, tests

    let private extension () =
        NamedDispatchExtension(checker) :> ITestPruneExtension

    let private edgesFor (root: string) (store: SymbolStore) = extension().AnalyzeEdges store root

    /// An index of `results` at `root/name`, with the extension's edges refreshed into it
    /// the way a host stores them.
    let private indexWithRefresh (root: string) (name: string) (results: AnalysisResult list) =
        let db = Database.create (Path.Combine(root, name))
        db.RebuildProjects results
        test <@ refreshExtensionEdges db root [ extension () ] |> List.forall _.IsRefreshed @>
        db

    /// The edges stored under the extension's owner, as (from, to) pairs.
    let private storedEdges (db: Database) =
        use conn = db.OpenConnection()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT f.full_name, t.full_name
            FROM dependencies d
            JOIN symbols f ON f.id = d.from_symbol_id
            JOIN symbols t ON t.id = d.to_symbol_id
            WHERE d.source_file = @owner
            """

        cmd.Parameters.AddWithValue("@owner", extensionEdgeOwner "Named Dispatch")
        |> ignore

        use reader = cmd.ExecuteReader()

        [ while reader.Read() do
              yield reader.GetString 0, reader.GetString 1 ]
        |> Set.ofList

    let private selectedByMapper (db: Database) =
        db.QueryAffectedTests [ "App.mapRow" ]
        |> List.map _.SymbolFullName
        |> Set.ofList

    let private pairs (edges: Dependency list) =
        edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

    [<Fact>]
    let ``a test dispatching a registered name by literal gets an edge to that handler`` () =
        let root, app, tests = analyzeFixture literalPurgeUrl

        let store = TestPrune.InMemoryStore.fromAnalysisResults [ app; tests ]
        let edges = edgesFor root store

        // Exactly the two registered names dispatched. `PurgeAll` is not `Purge` plus a
        // suffix, and `NotAJob` is dispatched but never registered.
        test
            <@
                pairs edges = set
                    [ "Tests.schedulesPurge", "App.runPurge"
                      "Tests.runsPurgeAll", "App.runPurgeAll" ]
            @>

        test
            <@
                edges
                |> List.forall (fun e -> e.Source = "named-dispatch" && e.Kind = SharedState)
            @>

    [<Fact>]
    let ``positive control - without the dispatch literal the edge is gone`` () =
        let root, app, tests = analyzeFixture noLiteralPurgeUrl

        let store = TestPrune.InMemoryStore.fromAnalysisResults [ app; tests ]
        let edges = edgesFor root store

        test <@ pairs edges = set [ "Tests.runsPurgeAll", "App.runPurgeAll" ] @>

    [<Fact>]
    let ``the edge carries selection past a composition-root registry`` () =
        let selectAfterEditingMapper (purgeUrl: string) =
            let root, app, tests = analyzeFixture purgeUrl
            indexWithRefresh root "cache.db" [ app; tests ] |> selectedByMapper

        // `mapRow` → `runPurge` → `run`, which is marked as a composition root: without
        // the edge the walk reaches no test at all.
        test <@ selectAfterEditingMapper literalPurgeUrl = set [ "Tests.schedulesPurge" ] @>
        test <@ selectAfterEditingMapper noLiteralPurgeUrl |> Set.isEmpty @>

    [<Fact>]
    let ``two index builds of one tree store the same edges`` () =
        let root, app, tests = analyzeFixture literalPurgeUrl

        let expected =
            set
                [ "Tests.schedulesPurge", "App.runPurge"
                  "Tests.runsPurgeAll", "App.runPurgeAll" ]

        let first = indexWithRefresh root "first.db" [ app; tests ]
        let second = indexWithRefresh root "second.db" [ tests; app ]

        test <@ storedEdges first = expected @>
        test <@ storedEdges second = expected @>

        // A repeat refresh of an unchanged tree changes nothing.
        refreshExtensionEdges first root [ extension () ] |> ignore
        test <@ storedEdges first = expected @>

        // The in-memory store answers the same as the indexed one.
        test <@ pairs (edgesFor root (TestPrune.InMemoryStore.fromAnalysisResults [ tests; app ])) = expected @>

    [<Fact>]
    let ``removing a dispatch literal removes its edge on the next refresh`` () =
        let root, app, tests = analyzeFixture literalPurgeUrl
        let db = indexWithRefresh root "cache.db" [ app; tests ]

        // Positive control: before the edit, the edge is stored and selects the test.
        test <@ storedEdges db |> Set.contains ("Tests.schedulesPurge", "App.runPurge") @>
        test <@ selectedByMapper db = set [ "Tests.schedulesPurge" ] @>

        // Edit ONLY the test file, re-index it, and refresh: the handler side never
        // changed, so nothing but the extension's own replace can drop the edge.
        let edited = analyze root "Tests.fsx" "Tests" (testSource noLiteralPurgeUrl)
        db.RebuildProjects [ edited ]
        test <@ refreshExtensionEdges db root [ extension () ] |> List.forall _.IsRefreshed @>

        test <@ storedEdges db = set [ "Tests.runsPurgeAll", "App.runPurgeAll" ] @>
        test <@ selectedByMapper db |> Set.isEmpty @>

    /// The consumer shape: an xUnit class whose backticked member dispatches the name
    /// from inside an interpolated triple-quoted string (browser JS), beside a member
    /// that dispatches nothing.
    [<Fact>]
    let ``a class member dispatching inside an interpolated string is the edge's source`` () =
        let root, app, _ = analyzeFixture literalPurgeUrl

        let classSource =
            "module ClassTests\n\n"
            + "type FactAttribute() =\n    inherit System.Attribute()\n\n"
            + "type JobTests() =\n"
            + "    [<Fact>]\n"
            + "    member _.``schedules purge from the browser``() =\n"
            + "        let js = $\"\"\"fetch('/admin/jobs/schedule/Purge', {{ method: 'POST' }})\"\"\"\n"
            + "        js.Length |> ignore\n\n"
            + "    [<Fact>]\n"
            + "    member _.``renders the jobs page``() =\n"
            + "        \"/admin/jobs\".Length |> ignore\n"

        let fileName = Path.Combine(root, "ClassTests.fsx")
        File.WriteAllText(fileName, classSource)

        let options =
            getScriptOptions checker fileName classSource |> Async.RunSynchronously

        let classTests =
            match
                analyzeSource checker fileName classSource options "ClassTests"
                |> Async.RunSynchronously
            with
            | Ok result -> result
            | Error error -> failwith $"Analysis failed: {error}"

        let store = TestPrune.InMemoryStore.fromAnalysisResults [ app; classTests ]
        let edges = edgesFor root store

        test <@ edges |> List.map _.ToSymbol = [ "App.runPurge" ] @>
        test <@ edges[0].FromSymbol.Contains "schedules purge from the browser" @>

module ``dispatch templates`` =

    [<Fact>]
    let ``a template captures the whole name token, not a prefix`` () =
        let regex =
            match templateRegex "/admin/jobs/{action}/{name}" with
            | Ok regex -> regex
            | Error message -> failwith message

        let names (text: string) =
            regex.Matches text |> Seq.map (fun m -> m.Groups["name"].Value) |> List.ofSeq

        test <@ names "post \"/admin/jobs/run/PurgeAll\"" = [ "PurgeAll" ] @>
        test <@ names "fetch('/admin/jobs/schedule/Purge', {" = [ "Purge" ] @>
        test <@ List.isEmpty (names "\"/admin/jobs/run\"") @>

    [<Fact>]
    let ``a template without exactly one name placeholder is refused`` () =
        test <@ Result.isError (templateRegex "/admin/jobs/run") @>
        test <@ Result.isError (templateRegex "/{name}/{name}") @>

/// The paths a real FCS fixture cannot easily reach, over a hand-built store: what the
/// extension does when it cannot attribute a match, cannot read a file, or is given a
/// declaration it cannot use.
module ``unattributable and malformed input`` =

    let private symbol (file: string) (line: int) (name: string) : SymbolInfo =
        { FullName = name
          Kind = Function
          SourceFile = file
          LineStart = line
          LineEnd = line
          ContentHash = name
          IsExtern = false }

    let private testMethod (name: string) : TestMethodInfo =
        { SymbolFullName = name
          TestProject = "Tests"
          TestClass = "Tests"
          TestMethod = name }

    let private attribute (symbolName: string) (name: string) (argsJson: string) : SymbolAttribute =
        { SymbolFullName = symbolName
          AttributeName = name
          ArgsJson = argsJson }

    let private template =
        attribute "App.run" "DispatchTemplateAttribute" "[\"job\", \"/jobs/{name}\"]"

    let private registration =
        attribute "App.runPurge" "DispatchedAs" "[\"job\", \"Purge\"]"

    /// A repo root holding `Tests.fs` with `testSource`, and a store in which that file
    /// declares two tests, `first` on line 3 and `second` on line 5.
    let private storeWith (testSource: string option) (attributes: SymbolAttribute list) =
        let root =
            Path.Combine(Path.GetTempPath(), $"testprune-dispatch-{Guid.NewGuid():N}")

        Directory.CreateDirectory root |> ignore

        testSource
        |> Option.iter (fun text -> File.WriteAllText(Path.Combine(root, "Tests.fs"), text))

        let result =
            { AnalysisResult.Create(
                  [ symbol "App.fs" 1 "App.run"
                    symbol "App.fs" 2 "App.runPurge"
                    symbol "Tests.fs" 3 "Tests.first"
                    symbol "Tests.fs" 5 "Tests.second" ],
                  [],
                  [ testMethod "Tests.first"; testMethod "Tests.second" ]
              ) with
                Attributes = attributes }

        root, TestPrune.InMemoryStore.fromAnalysisResults [ result ]

    /// The coarse answer: every test declared in `Tests.fs` dispatches the handler.
    let private everyTestInFile =
        [ "Tests.first", "App.runPurge"; "Tests.second", "App.runPurge" ]

    let private edges (root, store) =
        (NamedDispatchExtension() :> ITestPruneExtension).AnalyzeEdges store root
        |> List.map (fun e -> e.FromSymbol, e.ToSymbol)

    [<Fact>]
    let ``a match in a file the parser rejects couples every test in that file`` () =
        let unparsable = "module Tests\n\nlet first () = ( \"/jobs/Purge\"\n"

        test <@ edges (storeWith (Some unparsable) [ template; registration ]) = everyTestInFile @>

    [<Fact>]
    let ``a match outside every binding couples every test in that file`` () =
        let header = "// dispatches \"/jobs/Purge\"\nmodule Tests\n\nlet unrelated () = 1\n"

        test <@ edges (storeWith (Some header) [ template; registration ]) = everyTestInFile @>

    [<Fact>]
    let ``positive control - the same files without a registration yield nothing`` () =
        let header = "// dispatches \"/jobs/Purge\"\nmodule Tests\n\nlet unrelated () = 1\n"
        test <@ List.isEmpty (edges (storeWith (Some header) [ template ])) @>
        test <@ List.isEmpty (edges (storeWith (Some header) [ registration ])) @>

    [<Fact>]
    let ``a match inside a binding the store does not track couples every test in that file`` () =
        let untracked =
            "module Tests\n\nlet helper () = 0\n\nlet untracked () = \"/jobs/Purge\"\n"

        test <@ edges (storeWith (Some untracked) [ template; registration ]) = everyTestInFile @>

    [<Fact>]
    let ``a test file that dispatches nothing contributes no edges`` () =
        let quiet = "module Tests\n\nlet first () = \"/jobs\"\n"
        test <@ List.isEmpty (edges (storeWith (Some quiet) [ template; registration ])) @>

    [<Fact>]
    let ``the extension names itself`` () =
        test <@ (NamedDispatchExtension() :> ITestPruneExtension).Name = "Named Dispatch" @>

    [<Fact>]
    let ``an unreadable test file contributes no edges and no failure`` () =
        test <@ List.isEmpty (edges (storeWith None [ template; registration ])) @>

    [<Fact>]
    let ``a declaration missing its second argument is ignored`` () =
        let header = "// dispatches \"/jobs/Purge\"\nmodule Tests\n"

        let malformed =
            [ attribute "App.run" "DispatchTemplate" "[\"job\"]"
              attribute "App.runPurge" "DispatchedAsAttribute" "[\"job\"]" ]

        test <@ List.isEmpty (edges (storeWith (Some header) (template :: malformed))) @>
        test <@ List.isEmpty (edges (storeWith (Some header) (registration :: malformed))) @>

    [<Fact>]
    let ``an invalid template in the tree fails loudly rather than matching nothing`` () =
        let header = "// dispatches \"/jobs/Purge\"\nmodule Tests\n"
        let invalid = attribute "App.run" "DispatchTemplate" "[\"job\", \"/jobs/run\"]"

        raises<ArgumentException> <@ edges (storeWith (Some header) [ invalid; registration ]) @>

    [<Fact>]
    let ``a handler dispatching its own name gets no self-edge`` () =
        let source = "module Tests\n\nlet first () = \"/jobs/Purge\"\n\nlet second () = 2\n"
        let selfRegistered = attribute "Tests.first" "DispatchedAs" "[\"job\", \"Purge\"]"

        test <@ List.isEmpty (edges (storeWith (Some source) [ template; selfRegistered ])) @>
