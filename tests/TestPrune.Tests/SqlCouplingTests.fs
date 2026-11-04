module TestPrune.Tests.SqlCouplingTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.Sql
open TestPrune.Tests.TestHelpers

module ``SqlCoupling buildEdges`` =

    [<Fact>]
    let ``writer and reader of same table get SharedState edge`` () =
        let facts =
            [ { Symbol = "Queries.saveArticle"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "Queries.getArticle"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.Length = 1 @>
        test <@ edges[0].FromSymbol = "Queries.getArticle" @>
        test <@ edges[0].ToSymbol = "Queries.saveArticle" @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql" @>

    [<Fact>]
    let ``column-level coupling only matches same column`` () =
        let facts =
            [ { Symbol = "W.save"
                Table = "articles"
                Column = Column "status"
                Access = Write }
              { Symbol = "R.loadTitle"
                Table = "articles"
                Column = Column "title"
                Access = Read }
              { Symbol = "R.loadStatus"
                Table = "articles"
                Column = Column "status"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.Length = 1 @>
        test <@ edges[0].FromSymbol = "R.loadStatus" @>
        test <@ edges[0].ToSymbol = "W.save" @>

    [<Fact>]
    let ``wildcard writer couples to all column readers`` () =
        let facts =
            [ { Symbol = "W.save"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "R.loadTitle"
                Table = "articles"
                Column = Column "title"
                Access = Read }
              { Symbol = "R.loadStatus"
                Table = "articles"
                Column = Column "status"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.Length = 2 @>

    [<Fact>]
    let ``wildcard reader couples to all column writers`` () =
        let facts =
            [ { Symbol = "W.saveStatus"
                Table = "articles"
                Column = Column "status"
                Access = Write }
              { Symbol = "W.saveTitle"
                Table = "articles"
                Column = Column "title"
                Access = Write }
              { Symbol = "R.load"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.Length = 2 @>

    [<Fact>]
    let ``no self-edges`` () =
        let facts =
            [ { Symbol = "Q.upsert"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "Q.upsert"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``no edges when only writers`` () =
        let facts =
            [ { Symbol = "W.save"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "W.update"
                Table = "articles"
                Column = AllColumns
                Access = Write } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``no edges when only readers`` () =
        let facts =
            [ { Symbol = "R.load1"
                Table = "articles"
                Column = AllColumns
                Access = Read }
              { Symbol = "R.load2"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``different tables do not couple`` () =
        let facts =
            [ { Symbol = "W.saveArticle"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "R.loadUser"
                Table = "users"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``multiple writers and readers produce cartesian edges`` () =
        let facts =
            [ { Symbol = "W1.save"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "W2.update"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "R1.load"
                Table = "articles"
                Column = AllColumns
                Access = Read }
              { Symbol = "R2.list"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges Set.empty facts
        // 2 writers x 2 readers = 4 edges
        test <@ edges.Length = 4 @>

    [<Fact>]
    let ``a test method that writes a table is not a writer the table's readers depend on`` () =
        let facts =
            [ { Symbol = "Tests.seedsArticles"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "Queries.getArticle"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        test
            <@
                SqlCoupling.buildEdges (Set.singleton "Tests.seedsArticles") facts
                |> List.isEmpty
            @>
        // The same facts without the test marked: the edge the marking removes.
        test <@ (SqlCoupling.buildEdges Set.empty facts).Length = 1 @>

    [<Fact>]
    let ``a test method that reads a table still depends on the table's writers`` () =
        let facts =
            [ { Symbol = "Commands.saveArticle"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "Tests.readsArticles"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let edges = SqlCoupling.buildEdges (Set.singleton "Tests.readsArticles") facts

        test
            <@ edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) = [ "Tests.readsArticles", "Commands.saveArticle" ] @>

module ``SQL coupling end-to-end`` =

    [<Fact>]
    let ``changing a writer selects tests that read same table`` () =
        withDb (fun db ->
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        Location = InSource("tests/Tests.fs", 1, 5, "t1") }
                      { FullName = "Queries.getArticle"
                        Kind = Function
                        Location = InSource("src/Queries.fs", 1, 5, "q1") }
                      { FullName = "Queries.saveArticle"
                        Kind = Function
                        Location = InSource("src/Commands.fs", 1, 5, "q2") } ],
                    [ // testA calls getArticle (direct)
                      { FromSymbol = "Tests.testA"
                        ToSymbol = "Queries.getArticle"
                        Kind = Calls
                        Source = "core" }
                      // getArticle depends on saveArticle via shared table
                      { FromSymbol = "Queries.getArticle"
                        ToSymbol = "Queries.saveArticle"
                        Kind = SharedState
                        Source = "sql" } ],
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                )

            db.RebuildProjects([ result ])

            // When saveArticle changes, testA should be affected
            // (saveArticle → getArticle → testA, via transitive closure)
            let affected = db.QueryAffectedTests([ "Queries.saveArticle" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)

    [<Fact>]
    let ``SharedState edges participate in transitive closure`` () =
        withDb (fun db ->
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        Location = InSource("tests/Tests.fs", 1, 5, "t1") }
                      { FullName = "Service.process"
                        Kind = Function
                        Location = InSource("src/Service.fs", 1, 5, "s1") }
                      { FullName = "Queries.readItems"
                        Kind = Function
                        Location = InSource("src/Queries.fs", 1, 5, "q1") }
                      { FullName = "Jobs.writeItems"
                        Kind = Function
                        Location = InSource("src/Jobs.fs", 1, 5, "j1") } ],
                    [ // testA → Service.process → Queries.readItems (direct calls)
                      { FromSymbol = "Tests.testA"
                        ToSymbol = "Service.process"
                        Kind = Calls
                        Source = "core" }
                      { FromSymbol = "Service.process"
                        ToSymbol = "Queries.readItems"
                        Kind = Calls
                        Source = "core" }
                      // readItems depends on writeItems via shared table
                      { FromSymbol = "Queries.readItems"
                        ToSymbol = "Jobs.writeItems"
                        Kind = SharedState
                        Source = "sql" } ],
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ]
                )

            db.RebuildProjects([ result ])

            // Changing Jobs.writeItems should select testA via:
            // writeItems →(SharedState)→ readItems →(Calls)→ process →(Calls)→ testA
            let affected = db.QueryAffectedTests([ "Jobs.writeItems" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)

module ``SqlExtension as ITestPruneExtension`` =

    [<Fact>]
    let ``SqlExtension produces edges from facts`` () =
        let facts =
            [ { Symbol = "Queries.save"
                Table = "articles"
                Column = AllColumns
                Access = Write }
              { Symbol = "Queries.load"
                Table = "articles"
                Column = AllColumns
                Access = Read } ]

        let extension = SqlExtension(facts)

        let edges =
            (extension :> ITestPruneExtension).AnalyzeEdges (TestPrune.InMemoryStore.fromAnalysisResults []) ""

        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql" @>

    [<Fact>]
    let ``SqlExtension with no facts produces no edges`` () =
        let extension = SqlExtension([])

        let edges =
            (extension :> ITestPruneExtension).AnalyzeEdges (TestPrune.InMemoryStore.fromAnalysisResults []) ""

        test <@ edges.IsEmpty @>

module ``SqlExtension auto-discovery`` =

    [<Fact>]
    let ``discovers ReadsFrom and WritesTo from symbol attributes`` () =
        let result =
            { AnalysisResult.Create(
                  [ { FullName = "Queries.save"
                      Kind = Function
                      Location = InSource("src/Queries.fs", 1, 5, "a") }
                    { FullName = "Queries.load"
                      Kind = Function
                      Location = InSource("src/Queries.fs", 6, 10, "b") } ],
                  [],
                  []
              ) with
                Attributes =
                    [ { SymbolFullName = "Queries.save"
                        AttributeName = "WritesToAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" }
                      { SymbolFullName = "Queries.load"
                        AttributeName = "ReadsFromAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" } ] }

        let store = TestPrune.InMemoryStore.fromAnalysisResults [ result ]
        let extension = AutoSqlExtension()
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store ""
        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>

    [<Fact>]
    let ``no edges when no sql attributes`` () =
        let store = TestPrune.InMemoryStore.fromAnalysisResults [ standardGraph ]
        let extension = AutoSqlExtension()
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store ""
        test <@ edges.IsEmpty @>

    /// The FalcoRoute cross-product bug is NOT present here.
    /// SQL facts are keyed by the SYMBOL the attribute sits on and coupled per
    /// (table, column) — the source file never enters the computation, so three
    /// symbols sharing one file stay independent. A file-level cross-product would
    /// link the `articles` reader to the `users` writer as well; it must not.
    [<Fact>]
    let ``auto-discovery scopes edges per symbol and table, not per file`` () =
        let symbolIn file name : SymbolInfo =
            { FullName = name
              Kind = Function
              Location = InSource(file, 1, 5, name) }

        // Every symbol lives in ONE file.
        let result =
            { AnalysisResult.Create(
                  [ symbolIn "src/Queries.fs" "Queries.saveArticle"
                    symbolIn "src/Queries.fs" "Queries.saveUser"
                    symbolIn "src/Queries.fs" "Queries.loadArticle" ],
                  [],
                  []
              ) with
                Attributes =
                    [ { SymbolFullName = "Queries.saveArticle"
                        AttributeName = "WritesToAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" }
                      { SymbolFullName = "Queries.saveUser"
                        AttributeName = "WritesToAttribute"
                        ArgsJson = "[\"users\", \"*\"]" }
                      { SymbolFullName = "Queries.loadArticle"
                        AttributeName = "ReadsFromAttribute"
                        ArgsJson = "[\"articles\", \"*\"]" } ] }

        let store = TestPrune.InMemoryStore.fromAnalysisResults [ result ]
        let edges = (AutoSqlExtension() :> ITestPruneExtension).AnalyzeEdges store ""
        let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

        // Only the same-table pair couples. The file-mate `saveUser` writer is absent.
        test <@ pairs = set [ "Queries.loadArticle", "Queries.saveArticle" ] @>

module ``SqlColumn`` =

    [<Fact>]
    let ``star is every column, any other name is that column`` () =
        test <@ SqlColumn.parse "*" = AllColumns @>
        test <@ SqlColumn.parse "status" = Column "status" @>

    [<Fact>]
    let ``every column overlaps any column; two named columns overlap only when equal`` () =
        test <@ SqlColumn.overlaps AllColumns (Column "a") @>
        test <@ SqlColumn.overlaps (Column "a") AllColumns @>
        test <@ SqlColumn.overlaps (Column "a") (Column "a") @>
        test <@ not (SqlColumn.overlaps (Column "a") (Column "b")) @>

    [<Fact>]
    let ``auto-discovered facts parse the column argument, and a missing one means every column`` () =
        let symbol name =
            { FullName = name
              Kind = Function
              Location = InSource("src/Queries.fs", 1, 1, name) }

        let attribute symbolName attributeName argsJson =
            { SymbolFullName = symbolName
              AttributeName = attributeName
              ArgsJson = argsJson }

        let result =
            { AnalysisResult.Create([ symbol "Q.a"; symbol "Q.b"; symbol "Q.c" ], [], []) with
                Attributes =
                    [ attribute "Q.a" "ReadsFromAttribute" "[\"t\", \"*\"]"
                      attribute "Q.b" "ReadsFrom" "[\"t\", \"status\"]"
                      attribute "Q.c" "WritesToAttribute" "[\"t\"]" ] }

        let facts =
            AutoSqlExtension.ExtractFacts(TestPrune.InMemoryStore.fromAnalysisResults [ result ])
            |> List.map (fun f -> f.Symbol, f.Column)
            |> List.sort

        test <@ facts = [ "Q.a", AllColumns; "Q.b", Column "status"; "Q.c", AllColumns ] @>

    [<Fact>]
    let ``an unreadable SQL payload reads as no arguments: table-less, every column`` () =
        let result =
            { AnalysisResult.Create([], [], []) with
                Attributes =
                    [ { SymbolFullName = "Q.broken"
                        AttributeName = "ReadsFromAttribute"
                        ArgsJson = "[\"t" } ] }

        let facts =
            AutoSqlExtension.ExtractFacts(TestPrune.InMemoryStore.fromAnalysisResults [ result ])
            |> List.map (fun f -> f.Symbol, f.Table, f.Column)

        test <@ facts = [ "Q.broken", "", AllColumns ] @>
