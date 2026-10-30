/// Types declared in F#'s global namespace (`namespace global`).
///
/// .NET requires some types to live there — a `DOTNET_STARTUP_HOOKS` hook must be a
/// global-namespace `StartupHook` class — and FCS names such a type by its bare
/// identifier: `StartupHook`. `symbols_full_name_is_qualified` rightly rejects a bare
/// non-module name, because a bare name is how unrelated things merge into one
/// repo-wide hub; before this fix, one such type failed the WHOLE flush.
///
/// The fixture also declares a type of the same name in a real namespace spelled
/// ``` ``global`` ``` — legal F#, and compiled to a CLR namespace named `global` — so the
/// spelling chosen for the global namespace is proven not to collide with it.
module TestPrune.Tests.GlobalNamespaceTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff

let private hookSource (answer: int) =
    $"""namespace global

module GlobalHelpers =
    let helper () = {answer}

type StartupHook() =
    static member Initialize() = GlobalHelpers.helper () + 1
"""

/// A real namespace whose name is the keyword `global`, escaped with backticks.
let private twinSource =
    """namespace ``global``

type StartupHook() =
    static member Initialize() = 0
"""

let private testsSource =
    """module HookTests

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let initializes () = StartupHook.Initialize() |> ignore

[<Fact>]
let initializesTheTwin () = ``global``.StartupHook.Initialize() |> ignore
"""

/// The canonical name of the global-namespace `StartupHook`.
let private globalHook = GlobalNamespaceQualifier + ".StartupHook"

/// Analyze the three-file fixture through the real compiler; results in file order, with
/// symbol paths relative to the fixture directory.
let private analyzeFixture (answer: int) =
    let directory =
        Path.Combine(Path.GetTempPath(), $"global-namespace-{Guid.NewGuid():N}")

    Directory.CreateDirectory directory |> ignore

    let sources =
        [ "Hook.fs", hookSource answer
          "Twin.fs", twinSource
          "HookTests.fs", testsSource ]
        |> List.map (fun (name, source) -> Path.Combine(directory, name), source)

    try
        for path, source in sources do
            File.WriteAllText(path, source)

        let checker = FSharpChecker.Create()
        let lastPath, lastSource = List.last sources

        let scriptOptions =
            getScriptOptions checker lastPath lastSource |> Async.RunSynchronously

        let options =
            { scriptOptions with
                SourceFiles = sources |> List.map fst |> List.toArray }

        sources
        |> List.map (fun (path, source) ->
            match analyzeSource checker path source options "Fixture" |> Async.RunSynchronously with
            | Ok result ->
                { result with
                    Symbols = normalizeSymbolPaths directory result.Symbols }
            | Error message -> failwith message)
    finally
        Directory.Delete(directory, true)

let private allNames (results: AnalysisResult list) =
    results
    |> List.collect (fun r ->
        (r.Symbols |> List.map _.FullName)
        @ (r.Dependencies |> List.collect (fun d -> [ d.FromSymbol; d.ToSymbol ]))
        @ (r.ParentLinks |> List.collect (fun l -> [ l.Child; l.Parent ]))
        @ (r.TestMethods |> List.map _.SymbolFullName))
    |> List.distinct

[<Collection("FCS-AstAnalyzer")>]
module ``Global namespace naming`` =

    [<Fact>]
    let ``a global-namespace type and its member are indexed under qualified names`` () =
        let results = analyzeFixture 41
        let hook = results[0]
        let defined = hook.Symbols |> List.map (fun s -> s.FullName, s.Kind)

        test <@ defined |> List.contains (globalHook, Type) @>
        test <@ defined |> List.contains (globalHook + ".Initialize", Function) @>
        // A global-namespace MODULE keeps its bare name: `Module` is the one kind the
        // constraint exempts, and it is the same CLR entity as a top-level `module X`.
        test <@ defined |> List.contains ("GlobalHelpers", Module) @>
        test <@ defined |> List.contains ("GlobalHelpers.helper", Function) @>

        // The member's containment link points at the type's canonical name.
        test
            <@
                hook.ParentLinks
                |> List.exists (fun l -> l.Child = globalHook + ".Initialize" && l.Parent = globalHook)
            @>

        // No name anywhere in the analysis would be rejected by the symbols table.
        let bare =
            results
            |> List.collect _.Symbols
            |> List.filter (fun s -> s.Kind <> Module && not (s.FullName.Contains '.'))
            |> List.map _.FullName

        test <@ List.isEmpty bare @>
        test <@ not (allNames results |> List.contains "StartupHook") @>

    [<Fact>]
    let ``the global namespace does not collide with a namespace spelled global`` () =
        let results = analyzeFixture 41
        let twin = results[1]
        let twinNames = twin.Symbols |> List.map _.FullName

        test <@ twinNames |> List.contains "global.StartupHook" @>
        test <@ globalHook <> "global.StartupHook" @>
        test <@ not (twinNames |> List.contains globalHook) @>

        let edgesFrom (name: string) =
            results[2].Dependencies
            |> List.filter (fun d -> d.FromSymbol = name)
            |> List.map _.ToSymbol

        test <@ edgesFrom "HookTests.initializes" |> List.contains (globalHook + ".Initialize") @>

        test
            <@
                not (
                    edgesFrom "HookTests.initializes"
                    |> List.contains "global.StartupHook.Initialize"
                )
            @>

        test
            <@
                edgesFrom "HookTests.initializesTheTwin"
                |> List.contains "global.StartupHook.Initialize"
            @>

        test
            <@
                not (
                    edgesFrom "HookTests.initializesTheTwin"
                    |> List.contains (globalHook + ".Initialize")
                )
            @>

[<Collection("FCS-AstAnalyzer")>]
module ``Global namespace selection`` =

    [<Fact>]
    let ``the index accepts a global-namespace type and a change to it selects its test`` () =
        let before = analyzeFixture 41
        let after = analyzeFixture 42

        TestPrune.Tests.TestHelpers.withDb (fun db ->
            // Before the fix this threw `CHECK constraint failed: symbols_full_name_is_qualified`.
            db.RebuildProjects before

            let stored = db.GetSymbolsInFile "Hook.fs"
            test <@ stored |> List.exists (fun s -> s.FullName = globalHook) @>

            let changes, _events = detectChanges after[0].Symbols stored
            let changed = changedSymbolNames changes
            test <@ changed |> List.contains "GlobalHelpers.helper" @>

            let affected = db.QueryAffectedTests changed |> List.map _.TestMethod |> List.sort

            test <@ affected = [ "initializes" ] @>

            // A change to the type itself (not only through its member) selects the test.
            let typeAffected = db.QueryAffectedTests [ globalHook ] |> List.map _.TestMethod
            test <@ typeAffected = [ "initializes" ] @>)
