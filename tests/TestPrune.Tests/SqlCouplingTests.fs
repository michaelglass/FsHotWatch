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
                Column = "*"
                Access = Write }
              { Symbol = "Queries.getArticle"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
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
                Column = "status"
                Access = Write }
              { Symbol = "R.loadTitle"
                Table = "articles"
                Column = "title"
                Access = Read }
              { Symbol = "R.loadStatus"
                Table = "articles"
                Column = "status"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 1 @>
        test <@ edges[0].FromSymbol = "R.loadStatus" @>
        test <@ edges[0].ToSymbol = "W.save" @>

    [<Fact>]
    let ``wildcard writer couples to all column readers`` () =
        let facts =
            [ { Symbol = "W.save"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "R.loadTitle"
                Table = "articles"
                Column = "title"
                Access = Read }
              { Symbol = "R.loadStatus"
                Table = "articles"
                Column = "status"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 2 @>

    [<Fact>]
    let ``wildcard reader couples to all column writers`` () =
        let facts =
            [ { Symbol = "W.saveStatus"
                Table = "articles"
                Column = "status"
                Access = Write }
              { Symbol = "W.saveTitle"
                Table = "articles"
                Column = "title"
                Access = Write }
              { Symbol = "R.load"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.Length = 2 @>

    [<Fact>]
    let ``no self-edges`` () =
        let facts =
            [ { Symbol = "Q.upsert"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "Q.upsert"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``no edges when only writers`` () =
        let facts =
            [ { Symbol = "W.save"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "W.update"
                Table = "articles"
                Column = "*"
                Access = Write } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``no edges when only readers`` () =
        let facts =
            [ { Symbol = "R.load1"
                Table = "articles"
                Column = "*"
                Access = Read }
              { Symbol = "R.load2"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``different tables do not couple`` () =
        let facts =
            [ { Symbol = "W.saveArticle"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "R.loadUser"
                Table = "users"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        test <@ edges.IsEmpty @>

    [<Fact>]
    let ``multiple writers and readers produce cartesian edges`` () =
        let facts =
            [ { Symbol = "W1.save"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "W2.update"
                Table = "articles"
                Column = "*"
                Access = Write }
              { Symbol = "R1.load"
                Table = "articles"
                Column = "*"
                Access = Read }
              { Symbol = "R2.list"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let edges = SqlCoupling.buildEdges facts
        // 2 writers x 2 readers = 4 edges
        test <@ edges.Length = 4 @>

module ``SQL coupling end-to-end`` =

    [<Fact>]
    let ``changing a writer selects tests that read same table`` () =
        withDb (fun db ->
            let result =
                AnalysisResult.Create(
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "t1"
                        IsExtern = false }
                      { FullName = "Queries.getArticle"
                        Kind = Function
                        SourceFile = "src/Queries.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "q1"
                        IsExtern = false }
                      { FullName = "Queries.saveArticle"
                        Kind = Function
                        SourceFile = "src/Commands.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "q2"
                        IsExtern = false } ],
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
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "t1"
                        IsExtern = false }
                      { FullName = "Service.process"
                        Kind = Function
                        SourceFile = "src/Service.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "s1"
                        IsExtern = false }
                      { FullName = "Queries.readItems"
                        Kind = Function
                        SourceFile = "src/Queries.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "q1"
                        IsExtern = false }
                      { FullName = "Jobs.writeItems"
                        Kind = Function
                        SourceFile = "src/Jobs.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "j1"
                        IsExtern = false } ],
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
                Column = "*"
                Access = Write }
              { Symbol = "Queries.load"
                Table = "articles"
                Column = "*"
                Access = Read } ]

        let extension = SqlExtension(facts)
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges Unchecked.defaultof<_> [] ""
        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql" @>

    [<Fact>]
    let ``SqlExtension with no facts produces no edges`` () =
        let extension = SqlExtension([])
        let edges = (extension :> ITestPruneExtension).AnalyzeEdges Unchecked.defaultof<_> [] ""
        test <@ edges.IsEmpty @>
