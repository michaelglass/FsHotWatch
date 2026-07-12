module TestPrune.Tests.EdgeEmissionTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.EdgeEmission

let private sym (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 2
      ContentHash = "h"
      IsExtern = false }

let private handlerFile =
    [ sym "App.Handlers.Multi.getUser" "src/Multi.fs"
      sym "App.Handlers.Multi.getOrder" "src/Multi.fs"
      sym "App.Handlers.Multi.helper" "src/Multi.fs" ]

let private tests =
    [ sym "App.Tests.UsersTests.GetUser" "tests/UsersTests.fs"
      sym "App.Tests.UsersTests.ListUsers" "tests/UsersTests.fs" ]

let private pairs (edges: Dependency list) =
    edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList

module ``resolveTargets`` =

    [<Fact>]
    let ``UnnamedSymbol keeps every candidate`` () =
        test <@ resolveTargets handlerFile UnnamedSymbol = handlerFile @>

    [<Fact>]
    let ``NamedSymbol matches a fully-qualified name exactly`` () =
        let resolved = resolveTargets handlerFile (NamedSymbol "App.Handlers.Multi.getUser")
        test <@ resolved |> List.map (fun s -> s.FullName) = [ "App.Handlers.Multi.getUser" ] @>

    [<Fact>]
    let ``NamedSymbol matches a short Module dot function name by dotted suffix`` () =
        // The seed carries `Multi.getUser`; the store holds the fully-qualified name.
        let resolved = resolveTargets handlerFile (NamedSymbol "Multi.getUser")
        test <@ resolved |> List.map (fun s -> s.FullName) = [ "App.Handlers.Multi.getUser" ] @>

    [<Fact>]
    let ``NamedSymbol does not match on a bare substring of a longer name`` () =
        // Suffix matching is dotted, so `User` must not hijack `...Multi.getUser`.
        let resolved = resolveTargets handlerFile (NamedSymbol "User")
        test <@ resolved = handlerFile @> // unresolvable → coarse fallback, not a false scope

    [<Fact>]
    let ``an unresolvable NamedSymbol falls back to every candidate, never to none`` () =
        // A seed can name a function that has since been renamed. Emitting nothing would
        // under-select — the one failure mode a test-impact tool must not have.
        let resolved = resolveTargets handlerFile (NamedSymbol "Multi.longGone")
        test <@ resolved = handlerFile @>

    [<Fact>]
    let ``no candidates resolves to no targets`` () =
        test <@ resolveTargets [] (NamedSymbol "Multi.getUser") |> List.isEmpty @>

module ``edgesTo`` =

    [<Fact>]
    let ``a named target scopes edges to that symbol, never a cross-product`` () =
        let edges =
            edgesTo "falco" SharedState handlerFile (NamedSymbol "Multi.getUser") tests

        test
            <@
                pairs edges = set
                    [ "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getUser"
                      "App.Tests.UsersTests.ListUsers", "App.Handlers.Multi.getUser" ]
            @>

        // The siblings in the same file are NOT linked.
        test <@ edges |> List.forall (fun e -> e.ToSymbol = "App.Handlers.Multi.getUser") @>

    [<Fact>]
    let ``kind and source are stamped on every edge`` () =
        let edges =
            edgesTo "falco" SharedState handlerFile (NamedSymbol "Multi.getUser") tests

        test <@ edges |> List.forall (fun e -> e.Kind = SharedState && e.Source = "falco") @>

    [<Fact>]
    let ``an unnamed target degrades to the whole candidate set`` () =
        let edges =
            edgesTo "falco" SharedState handlerFile UnnamedSymbol [ List.head tests ]

        test
            <@
                pairs edges = set
                    [ "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getUser"
                      "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.getOrder"
                      "App.Tests.UsersTests.GetUser", "App.Handlers.Multi.helper" ]
            @>

    [<Fact>]
    let ``no dependents yields no edges`` () =
        test
            <@
                edgesTo "falco" SharedState handlerFile (NamedSymbol "Multi.getUser") []
                |> List.isEmpty
            @>

    [<Fact>]
    let ``no candidates yields no edges`` () =
        test
            <@
                edgesTo "falco" SharedState [] (NamedSymbol "Multi.getUser") tests
                |> List.isEmpty
            @>

    [<Fact>]
    let ``a symbol never gets an edge to itself`` () =
        let edges =
            edgesTo "falco" SharedState handlerFile (NamedSymbol "Multi.getUser") [ List.head handlerFile ]

        test <@ edges |> List.isEmpty @>

    [<Fact>]
    let ``duplicate dependents collapse to one edge`` () =
        let dependent = List.head tests

        let edges =
            edgesTo "falco" SharedState handlerFile (NamedSymbol "Multi.getUser") [ dependent; dependent ]

        test <@ edges.Length = 1 @>
