module TestPrune.Tests.ImpactAnalysisTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.ImpactAnalysis

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

let private withDb (f: Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f db
    finally
        if File.Exists path then
            File.Delete path

        let walPath = path + "-wal"
        let shmPath = path + "-shm"

        if File.Exists walPath then
            File.Delete walPath

        if File.Exists shmPath then
            File.Delete shmPath

/// Standard test graph: testA -> funcB -> TypeC
let private standardGraph =
    { Symbols =
        [ { FullName = "Tests.testA"
            Kind = Function
            SourceFile = "tests/Tests.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" }
          { FullName = "Lib.funcB"
            Kind = Function
            SourceFile = "src/Lib.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" }
          { FullName = "Domain.TypeC"
            Kind = Type
            SourceFile = "src/Domain.fs"
            LineStart = 1
            LineEnd = 3
            ContentHash = "" }
          { FullName = "Other.unrelated"
            Kind = Function
            SourceFile = "src/Other.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" } ]
      Dependencies =
        [ { FromSymbol = "Tests.testA"
            ToSymbol = "Lib.funcB"
            Kind = Calls }
          { FromSymbol = "Lib.funcB"
            ToSymbol = "Domain.TypeC"
            Kind = UsesType } ]
      TestMethods =
        [ { SymbolFullName = "Tests.testA"
            TestProject = "MyTests"
            TestClass = "Tests"
            TestMethod = "testA" } ] }

module ``Changed symbol with dependent test`` =

    [<Fact>]
    let ``direct dependency returns the test`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // funcB changed
            let currentSymbols =
                Map.ofList
                    [ "src/Lib.fs",
                      [ { FullName = "Lib.funcB"
                          Kind = Function
                          SourceFile = "src/Lib.fs"
                          LineStart = 1
                          LineEnd = 10
                          ContentHash = "changed" } ] ]

            let result = selectTests db [ "src/Lib.fs" ] currentSymbols

            match result with
            | RunSubset tests ->
                test <@ tests.Length = 1 @>
                test <@ tests[0].TestMethod = "testA" @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``Changed symbol with transitive dependent test`` =

    [<Fact>]
    let ``transitive dependency returns the test`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // TypeC changed
            let currentSymbols =
                Map.ofList
                    [ "src/Domain.fs",
                      [ { FullName = "Domain.TypeC"
                          Kind = Type
                          SourceFile = "src/Domain.fs"
                          LineStart = 1
                          LineEnd = 8
                          ContentHash = "changed" } ] ]

            let result = selectTests db [ "src/Domain.fs" ] currentSymbols

            match result with
            | RunSubset tests ->
                test <@ tests.Length = 1 @>
                test <@ tests[0].TestMethod = "testA" @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``Changed symbol with no dependent tests`` =

    [<Fact>]
    let ``production-only code returns empty subset`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // unrelated changed
            let currentSymbols =
                Map.ofList
                    [ "src/Other.fs",
                      [ { FullName = "Other.unrelated"
                          Kind = Function
                          SourceFile = "src/Other.fs"
                          LineStart = 1
                          LineEnd = 10
                          ContentHash = "changed" } ] ]

            let result = selectTests db [ "src/Other.fs" ] currentSymbols

            match result with
            | RunSubset tests -> test <@ tests |> List.isEmpty @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``Multiple changed symbols`` =

    [<Fact>]
    let ``union of all affected tests`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Tests.test1"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "Tests.test2"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 5
                        LineEnd = 8
                        ContentHash = "" }
                      { FullName = "Lib.funcA"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 7
                        LineEnd = 12
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.test1"
                        ToSymbol = "Lib.funcA"
                        Kind = Calls }
                      { FromSymbol = "Tests.test2"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.test1"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test1" }
                      { SymbolFullName = "Tests.test2"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test2" } ] }

            db.RebuildProjects([ "proj", graph ])

            // Both funcA and funcB changed
            let currentSymbols =
                Map.ofList
                    [ "src/Lib.fs",
                      [ { FullName = "Lib.funcA"
                          Kind = Function
                          SourceFile = "src/Lib.fs"
                          LineStart = 1
                          LineEnd = 8
                          ContentHash = "changed-a" }
                        { FullName = "Lib.funcB"
                          Kind = Function
                          SourceFile = "src/Lib.fs"
                          LineStart = 10
                          LineEnd = 18
                          ContentHash = "changed-b" } ] ]

            let result = selectTests db [ "src/Lib.fs" ] currentSymbols

            match result with
            | RunSubset tests ->
                test <@ tests.Length = 2 @>

                let methods = tests |> List.map (fun t -> t.TestMethod) |> Set.ofList

                test <@ methods = set [ "test1"; "test2" ] @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``No changes`` =

    [<Fact>]
    let ``empty changed files returns empty subset`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])
            let result = selectTests db [] Map.empty

            match result with
            | RunSubset tests -> test <@ tests |> List.isEmpty @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``New file not indexed`` =

    [<Fact>]
    let ``new file triggers RunAll`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // brand new file with symbols, not in DB
            let currentSymbols =
                Map.ofList
                    [ "src/NewModule.fs",
                      [ { FullName = "NewModule.newFunc"
                          Kind = Function
                          SourceFile = "src/NewModule.fs"
                          LineStart = 1
                          LineEnd = 5
                          ContentHash = "" } ] ]

            let result = selectTests db [ "src/NewModule.fs" ] currentSymbols

            match result with
            | RunAll _ -> ()
            | RunSubset _ -> failwith "Expected RunAll for new file")

module ``fsproj changed`` =

    [<Fact>]
    let ``fsproj change triggers RunAll`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])
            let result = selectTests db [ "src/MyProject.fsproj" ] Map.empty

            match result with
            | RunAll reason -> test <@ reason.Contains("fsproj", StringComparison.Ordinal) @>
            | RunSubset _ -> failwith "Expected RunAll for fsproj change")

module ``Empty changed files`` =

    [<Fact>]
    let ``empty list returns empty subset`` () =
        withDb (fun db ->
            let result = selectTests db [] Map.empty

            match result with
            | RunSubset tests -> test <@ tests |> List.isEmpty @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``File with no stored symbols and no current symbols`` =

    [<Fact>]
    let ``both stored and current symbols empty returns empty subset`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // "src/Empty.fs" was never indexed (no stored symbols) and has no current symbols either
            let currentSymbols = Map.ofList [ "src/Empty.fs", [] ]

            let result = selectTests db [ "src/Empty.fs" ] currentSymbols

            match result with
            | RunSubset tests -> test <@ tests |> List.isEmpty @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")

module ``File that had symbols but now has none`` =

    [<Fact>]
    let ``all symbols removed from file detects removals and returns affected tests`` () =
        withDb (fun db ->
            db.RebuildProjects([ "proj", standardGraph ])

            // src/Lib.fs has stored symbols (Lib.funcB) but current symbols list is empty — all removed
            let currentSymbols = Map.ofList [ "src/Lib.fs", [] ]

            let result = selectTests db [ "src/Lib.fs" ] currentSymbols

            match result with
            | RunSubset tests ->
                test <@ tests.Length = 1 @>
                test <@ tests[0].TestMethod = "testA" @>
            | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{reason}")
