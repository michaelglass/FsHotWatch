module TestPrune.Tests.SqlHydraAnalyzerTests

open Xunit
open Swensen.Unquote
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
        test <@ result = Some { Schema = "public"; Table = "articles" } @>
