module TestPrune.Tests.SqlCouplingTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Sql

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
        test <@ edges[0].FromSymbol = "Queries.saveArticle" @>
        test <@ edges[0].ToSymbol = "Queries.getArticle" @>
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
        test <@ edges[0].ToSymbol = "R.loadStatus" @>

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
