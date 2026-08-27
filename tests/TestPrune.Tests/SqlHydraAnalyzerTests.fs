module TestPrune.Tests.SqlHydraAnalyzerTests

open Xunit
open Swensen.Unquote
open TestPrune
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Sql
open TestPrune.SqlHydra
open TestPrune.Tests.TestHelpers

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

    [<Theory>]
    [<InlineData(null)>]
    [<InlineData("")>]
    [<InlineData(" ")>]
    [<InlineData(".Generated")>]
    [<InlineData("Generated.")>]
    [<InlineData("Generated..Database")>]
    [<InlineData(" Generated")>]
    let ``invalid generated module prefix is rejected instead of disabling attribution`` (prefix: string) =
        let store = InMemoryStore.fromAnalysisResults []

        raises<System.ArgumentException>
            <@ (SqlHydraExtension(prefix) :> ITestPruneExtension).AnalyzeEdges store [] "" @>

    [<Fact>]
    let ``detects read when function calls selectTask and generated table value`` () =
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
                    Kind = Value
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
                    Kind = Calls
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "public.articles" @>
        test <@ facts[0].Access = Read @>

    [<Fact>]
    let ``detects write when function calls insertTask and generated table value`` () =
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
                    Kind = Value
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
                    Kind = Calls
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "public.articles" @>
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
                    Kind = Value
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
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = Calls
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

// -----------------------------------------------------------------------------
// Edge scoping: audit against the cross-product over-selection FalcoRoute suffered from,
// and the access-collapse under-selection it turned up.
// -----------------------------------------------------------------------------

/// Terse builders for a symbol graph — the ceremony above obscures the shape.
let private fn (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 10
      ContentHash = fullName
      IsExtern = false }

let private dsl (fullName: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = "_extern"
      LineStart = 0
      LineEnd = 0
      ContentHash = ""
      IsExtern = true }

let private table (fullName: string) : SymbolInfo =
    { FullName = fullName
      Kind = Value
      SourceFile = "src/DbTypes.fs"
      LineStart = 1
      LineEnd = 5
      ContentHash = fullName
      IsExtern = false }

let private calls (source: string) (dest: string) : Dependency =
    { FromSymbol = source
      ToSymbol = dest
      Kind = Calls
      Source = "core" }

let private usesType (source: string) (dest: string) : Dependency =
    { FromSymbol = source
      ToSymbol = dest
      Kind = UsesType
      Source = "core" }

module ``SqlHydra edge scoping`` =

    [<Fact>]
    let ``unrelated DSL-shaped call does not classify a generated table access`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.touchArticle" "src/Queries.fs"
                  dsl "Other.Query.updateTask"
                  table "Generated.public.articles" ],
                [ calls "Queries.touchArticle" "Other.Query.updateTask"
                  calls "Queries.touchArticle" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        test <@ SqlHydraExtension.extractFacts "Generated" store |> List.isEmpty @>

    [<Fact>]
    let ``real generated graph accepts only a called table value`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.SelectBuilders.selectTask"
                  { FullName = "Generated.public"
                    Kind = Module
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 500
                    ContentHash = "schema"
                    IsExtern = false }
                  { FullName = "Generated.public.article_status"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 10
                    LineEnd = 15
                    ContentHash = "enum"
                    IsExtern = false }
                  { FullName = "Generated.public.articles"
                    Kind = Value
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 100
                    LineEnd = 100
                    ContentHash = "table-value"
                    IsExtern = false } ],
                [ calls "Queries.getArticles" "SqlHydra.Query.SelectBuilders.selectTask"
                  // Real FCS graphs carry broad type-use edges to the schema module and
                  // enums as well as the table record. They are not database resources.
                  usesType "Queries.getArticles" "Generated.public"
                  usesType "Queries.getArticles" "Generated.public.article_status"
                  usesType "Queries.getArticles" "Generated.public.articles"
                  // The generated `let articles = table<articles>` value is the precise
                  // table signal: query expressions call that value.
                  calls "Queries.getArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        test <@ facts |> List.map (fun fact -> fact.Table, fact.Access) = [ "public.articles", Read ] @>

    [<Fact>]
    let ``prefix must be a namespace boundary and table shape must be exact`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  { FullName = "GeneratedElse.public.articles"
                    Kind = Value
                    SourceFile = "src/OtherDbTypes.fs"
                    LineStart = 1
                    LineEnd = 1
                    ContentHash = "lookalike-prefix"
                    IsExtern = false }
                  { FullName = "Generated.public.articles.columns"
                    Kind = Value
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 1
                    ContentHash = "lookalike-shape"
                    IsExtern = false } ],
                [ calls "Queries.getArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.getArticles" "GeneratedElse.public.articles"
                  calls "Queries.getArticles" "Generated.public.articles.columns"
                  // Keep type-use edges in the fixture so the historical broad
                  // `Contains(prefix)` implementation demonstrably accepts both.
                  usesType "Queries.getArticles" "GeneratedElse.public.articles"
                  usesType "Queries.getArticles" "Generated.public.articles.columns" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        test <@ SqlHydraExtension.extractFacts "Generated" store |> List.isEmpty @>

    /// The FalcoRoute cross-product bug is NOT present here.
    /// `extractFacts` filters each symbol's dependencies to `d.FromSymbol = sym.FullName`,
    /// and the AST attributes every `Calls` edge to the *enclosing function*, not to
    /// the file. So two queries sharing one source file stay independent: a change to
    /// `getArticles` cannot pull tests that only touch `briefs`. This test pins that —
    /// it fails the moment anyone re-scopes the dependency lookup to the file.
    [<Fact>]
    let ``queries sharing a source file do not smear tables across each other`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  fn "Queries.createBrief" "src/Queries.fs" // SAME file
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles"
                  table "Generated.public.briefs" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.getArticles" "Generated.public.articles"
                  calls "Queries.createBrief" "SqlHydra.Query.insertTask"
                  calls "Queries.createBrief" "Generated.public.briefs" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let triples = facts |> List.map (fun f -> f.Symbol, f.Table, f.Access) |> Set.ofList

        // Each function is scoped to the table IT references — never its file-mate's.
        // A file-level cross-product would additionally emit
        // (getArticles, briefs, _) and (createBrief, articles, _).
        test
            <@
                triples = set
                    [ "Queries.getArticles", "public.articles", Read
                      "Queries.createBrief", "public.briefs", Write ]
            @>

    /// A symbol that BOTH reads and writes must keep both accesses. Keeping only the first
    /// DSL access records an upsert (select listed first) as a pure READER, its table ends
    /// up with no writer, `buildEdges` produces ZERO edges, and a change to the upsert
    /// selects none of the tests that read the table.
    [<Fact>]
    let ``symbol that both reads and writes keeps both accesses`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Repo.upsertArticle" "src/Repo.fs"
                  fn "Queries.listArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles" ],
                [ // The select is listed FIRST — precisely the ordering under which
                  // `List.tryHead` classified this write-performing symbol as read-only.
                  calls "Repo.upsertArticle" "SqlHydra.Query.selectTask"
                  calls "Repo.upsertArticle" "SqlHydra.Query.insertTask"
                  calls "Repo.upsertArticle" "Generated.public.articles"
                  calls "Queries.listArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.listArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let accesses =
            facts
            |> List.filter (fun f -> f.Symbol = "Repo.upsertArticle")
            |> List.map (fun f -> f.Access)
            |> Set.ofList

        test <@ accesses = set [ Read; Write ] @>

        // ...and the edge the dropped write destroyed is back: the reader of `articles`
        // now depends on the upsert that writes it.
        let edges =
            (SqlHydraExtension("Generated") :> ITestPruneExtension).AnalyzeEdges store [] ""

        let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList
        test <@ pairs = set [ "Queries.listArticles", "Repo.upsertArticle" ] @>

    /// A single-access symbol stays exactly as precise as before — keeping every access
    /// does not fan a pure reader out into a writer.
    [<Fact>]
    let ``single-access symbol emits exactly one fact per table`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  table "Generated.public.articles" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.getArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        test <@ facts |> List.map (fun f -> f.Table, f.Access) = [ "public.articles", Read ] @>

    /// A join reads several tables through ONE select: every table gets the read, and no
    /// spurious write appears.
    [<Fact>]
    let ``joined select marks every joined table as read`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.articlesWithBriefs" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  table "Generated.public.articles"
                  table "Generated.public.briefs" ],
                [ calls "Queries.articlesWithBriefs" "SqlHydra.Query.selectTask"
                  calls "Queries.articlesWithBriefs" "Generated.public.articles"
                  calls "Queries.articlesWithBriefs" "Generated.public.briefs" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let pairs = facts |> List.map (fun f -> f.Table, f.Access) |> Set.ofList
        test <@ pairs = set [ "public.articles", Read; "public.briefs", Read ] @>

module ``SqlHydra under-selection`` =

    [<Fact>]
    let ``writer intervention selects reader test in the same schema only`` () =
        withDb (fun db ->
            let symbols =
                [ fn "Tests.testPublicArticles" "tests/DatabaseTests.fs"
                  fn "Tests.testAuditArticles" "tests/DatabaseTests.fs"
                  fn "Queries.listPublicArticles" "src/ArticleQueries.fs"
                  fn "Queries.listAuditArticles" "src/AuditQueries.fs"
                  fn "Commands.createPublicArticle" "src/ArticleQueries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Intelligence.Database.Generated.public.articles"
                  table "Intelligence.Database.Generated.audit.articles" ]

            let coreDeps =
                [ calls "Tests.testPublicArticles" "Queries.listPublicArticles"
                  calls "Tests.testAuditArticles" "Queries.listAuditArticles"
                  calls "Queries.listPublicArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.listPublicArticles" "Intelligence.Database.Generated.public.articles"
                  calls "Queries.listAuditArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.listAuditArticles" "Intelligence.Database.Generated.audit.articles"
                  calls "Commands.createPublicArticle" "SqlHydra.Query.insertTask"
                  calls "Commands.createPublicArticle" "Intelligence.Database.Generated.public.articles" ]

            let testMethods =
                [ { SymbolFullName = "Tests.testPublicArticles"
                    TestProject = "Intelligence.Tests.Database"
                    TestClass = "Database.ArticleQueriesTests"
                    TestMethod = "testPublicArticles" }
                  { SymbolFullName = "Tests.testAuditArticles"
                    TestProject = "Intelligence.Tests.Database"
                    TestClass = "Database.AuditQueriesTests"
                    TestMethod = "testAuditArticles" } ]

            let store =
                InMemoryStore.fromAnalysisResults [ AnalysisResult.Create(symbols, coreDeps, testMethods) ]

            let sqlEdges =
                (SqlHydraExtension("Intelligence.Database.Generated") :> ITestPruneExtension).AnalyzeEdges store [] ""

            let edgePairs = sqlEdges |> List.map (fun edge -> edge.FromSymbol, edge.ToSymbol)

            test <@ edgePairs = [ "Queries.listPublicArticles", "Commands.createPublicArticle" ] @>

            db.RebuildProjects([ AnalysisResult.Create(symbols, coreDeps @ sqlEdges, testMethods) ])

            let affected = db.QueryAffectedTests([ "Commands.createPublicArticle" ])
            test <@ affected |> List.map (fun test -> test.TestMethod) = [ "testPublicArticles" ] @>)

    /// NO UNDER-SELECTION, end-to-end through the core's recursive reverse-walk:
    /// `testListsArticles` calls `Queries.listArticles`, which reads `articles`;
    /// `Repo.upsertArticle` writes `articles`. Changing the upsert MUST select the test,
    /// reached only via the sql-hydra SharedState edge:
    ///
    ///     upsertArticle ←(SharedState)← listArticles ←(Calls)← testListsArticles
    ///
    /// Drop the write and there is no SharedState edge, so this selects ZERO tests — a
    /// genuinely-affected test silently skipped.
    [<Fact>]
    let ``changing a read-write symbol still selects tests that read the table`` () =
        withDb (fun db ->
            let symbols =
                [ fn "Tests.testListsArticles" "tests/Tests.fs"
                  fn "Queries.listArticles" "src/Queries.fs"
                  fn "Repo.upsertArticle" "src/Repo.fs"
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles" ]

            let coreDeps =
                [ calls "Tests.testListsArticles" "Queries.listArticles"
                  calls "Queries.listArticles" "SqlHydra.Query.selectTask"
                  calls "Queries.listArticles" "Generated.public.articles"
                  calls "Repo.upsertArticle" "SqlHydra.Query.selectTask"
                  calls "Repo.upsertArticle" "SqlHydra.Query.insertTask"
                  calls "Repo.upsertArticle" "Generated.public.articles" ]

            let testMethods =
                [ { SymbolFullName = "Tests.testListsArticles"
                    TestProject = "MyTests"
                    TestClass = "Tests"
                    TestMethod = "testListsArticles" } ]

            // The extension reads the core graph, then its edges are merged back in —
            // the same order the orchestrator uses.
            let store =
                InMemoryStore.fromAnalysisResults [ AnalysisResult.Create(symbols, coreDeps, testMethods) ]

            let sqlEdges =
                (SqlHydraExtension("Generated") :> ITestPruneExtension).AnalyzeEdges store [] ""

            db.RebuildProjects([ AnalysisResult.Create(symbols, coreDeps @ sqlEdges, testMethods) ])

            let affected = db.QueryAffectedTests([ "Repo.upsertArticle" ])
            test <@ affected |> List.map (fun t -> t.TestMethod) = [ "testListsArticles" ] @>)
