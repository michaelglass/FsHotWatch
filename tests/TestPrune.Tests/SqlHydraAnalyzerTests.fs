module TestPrune.Tests.SqlHydraAnalyzerTests

open Xunit
open Swensen.Unquote
open TestPrune
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Sql
open TestPrune.SqlHydra

module ``DSL context classification`` =

    [<Fact>]
    let ``selectTask is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "selectTask" = Some Read @>

    [<Fact>]
    let ``selectAsync is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "selectAsync" = Some Read @>

    [<Fact>]
    let ``select is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "select" = Some Read @>

    [<Fact>]
    let ``insertTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "insertTask" = Some Write @>

    [<Fact>]
    let ``updateTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "updateTask" = Some Write @>

    [<Fact>]
    let ``deleteTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "deleteTask" = Some Write @>

    [<Fact>]
    let ``unknown context returns None`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "someOtherFunction" = None @>

module ``Table reference parsing`` =

    [<Fact>]
    let ``parses schema-qualified table name`` () =
        let result = SqlHydraAnalyzer.parseTableReference "Generated.public.briefs"
        test <@ result = Some { Schema = "public"; Table = "briefs" } @>

    [<Fact>]
    let ``parses table name without schema`` () =
        let result = SqlHydraAnalyzer.parseTableReference "Generated.dbo.users"
        test <@ result = Some { Schema = "dbo"; Table = "users" } @>

    [<Fact>]
    let ``returns None for non-matching pattern`` () =
        let result = SqlHydraAnalyzer.parseTableReference "SomeOther.Module"
        test <@ result = None @>

    [<Fact>]
    let ``handles deeply nested generated module`` () =
        let result = SqlHydraAnalyzer.parseTableReference "MyDb.Generated.public.articles"

        test
            <@
                result = Some
                    { Schema = "public"
                      Table = "articles" }
            @>

module ``SqlHydraExtension graph analysis`` =

    [<Fact>]
    let ``detects read when function calls selectTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Read @>

    [<Fact>]
    let ``detects write when function calls insertTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Write @>

    [<Fact>]
    let ``produces SharedState edges when reader and writer exist`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "b"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let extension = SqlHydraExtension("Generated")

        let edges =
            (extension :> TestPrune.Extensions.ITestPruneExtension).AnalyzeEdges store [] ""

        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql-hydra" @>

    [<Fact>]
    let ``ignores functions that use table type but no DSL function`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Helpers.mapArticle"
                    Kind = Function
                    SourceFile = "src/Helpers.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Helpers.mapArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.IsEmpty @>
