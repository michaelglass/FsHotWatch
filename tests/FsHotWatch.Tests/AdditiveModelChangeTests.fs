/// AUTOMATION-1900 step 2: the pure decision whether a project-model change may narrow the
/// "no whole-project run under the current project model" refusal (`AdditiveModelChange.judge`).
/// One test per reason it refuses, the narrowable case, and the line each verdict logs.
module FsHotWatch.Tests.AdditiveModelChangeTests

open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.TestPrune.AdditiveModelChange
open TestPrune.AstAnalyzer

let private shape (items: string list) : ProjectShape =
    { CompileItems = items
      Defines = [ "DEBUG" ]
      References = [ "/nuget/fsharp.core.dll" ]
      Options = [ "--optimize-" ] }

let private uses (target: string) =
    set
        [ { From = "Caller.run"
            To = target
            Kind = Calls } ]

let private libBefore = [ "src/Lib/A.fs"; "src/Lib/B.fs" ]
let private libAfter = [ "src/Lib/A.fs"; "src/Lib/New.fs"; "src/Lib/B.fs" ]
let private testsItems = [ "tests/T/Tests.fs" ]

/// Lib gains `New.fs` between its two files; T references Lib; every pre-existing file's uses
/// are as they were at T.
let private additive: Change =
    { Before = Map.ofList [ "Lib", shape libBefore; "T", shape testsItems ]
      After = Map.ofList [ "Lib", shape libAfter; "T", shape testsItems ]
      Dependents = Map.ofList [ "Lib", set [ "T" ]; "T", Set.empty ]
      UsesAtT =
        Map.ofList
            [ "src/Lib/A.fs", uses "X.a"
              "src/Lib/B.fs", uses "X.b"
              "tests/T/Tests.fs", uses "X.t" ]
      UsesNow =
        Map.ofList
            [ "src/Lib/A.fs", uses "X.a"
              "src/Lib/New.fs", uses "X.new"
              "src/Lib/B.fs", uses "X.b"
              "tests/T/Tests.fs", uses "X.t" ]
      Diff = set [ "src/Lib/New.fs" ] }

let private withLib (lib: ProjectShape) (change: Change) =
    { change with
        After = Map.add "Lib" lib change.After }

[<Fact>]
let ``compile items only added, every pre-existing file's uses unchanged: narrowable, naming the additions and the projects checked``
    ()
    =
    test
        <@
            judge additive = Narrowable
                { Added = Map.ofList [ "Lib", [ "src/Lib/New.fs" ] ]
                  Checked = set [ "Lib"; "T" ] }
        @>

[<Fact>]
let ``an item removed is not narrowable, naming the project and the item`` () =
    let change = withLib (shape [ "src/Lib/New.fs"; "src/Lib/B.fs" ]) additive
    test <@ judge change = NotNarrowable(ItemsRemoved("Lib", [ "src/Lib/A.fs" ])) @>

[<Fact>]
let ``pre-existing items reordered is not narrowable, even with an addition`` () =
    let change =
        withLib (shape [ "src/Lib/B.fs"; "src/Lib/New.fs"; "src/Lib/A.fs" ]) additive

    test <@ judge change = NotNarrowable(ItemsReordered "Lib") @>

[<Fact>]
let ``a define change is not narrowable`` () =
    let change =
        withLib
            { shape libAfter with
                Defines = [ "DEBUG"; "TRACE" ] }
            additive

    test <@ judge change = NotNarrowable(DefinesChanged "Lib") @>

[<Fact>]
let ``a reference or package change is not narrowable`` () =
    let change =
        withLib
            { shape libAfter with
                References = [ "/nuget/fsharp.core.dll"; "/nuget/newtonsoft.json.dll" ] }
            additive

    test <@ judge change = NotNarrowable(ReferencesChanged "Lib") @>

[<Fact>]
let ``any other compiler option change is not narrowable`` () =
    let change =
        withLib
            { shape libAfter with
                Options = [ "--optimize+" ] }
            additive

    test <@ judge change = NotNarrowable(OptionsChanged "Lib") @>

[<Fact>]
let ``a project added to or removed from the model is not narrowable`` () =
    let change =
        { additive with
            After = additive.After |> Map.remove "T" |> Map.add "U" (shape [ "tests/U/U.fs" ]) }

    test <@ judge change = NotNarrowable(ProjectsAddedOrRemoved([ "U" ], [ "T" ])) @>

[<Fact>]
let ``a model change that adds no compile item is not narrowable`` () =
    let change =
        { additive with
            After = additive.Before }

    test <@ judge change = NotNarrowable NoCompileItemsAdded @>

[<Fact>]
let ``a pre-existing file of the changed project whose uses changed and is not in the diff is not narrowable, naming it``
    ()
    =
    let change =
        { additive with
            UsesNow = Map.add "src/Lib/B.fs" (uses "X.shadow") additive.UsesNow }

    test <@ judge change = NotNarrowable(SymbolUsesChanged("Lib", "src/Lib/B.fs")) @>

[<Fact>]
let ``a downstream project's file whose uses changed is not narrowable, naming it`` () =
    let change =
        { additive with
            UsesNow = Map.add "tests/T/Tests.fs" (uses "X.shadow") additive.UsesNow }

    test <@ judge change = NotNarrowable(SymbolUsesChanged("T", "tests/T/Tests.fs")) @>

[<Fact>]
let ``a pre-existing file whose uses changed because its own text changed is in the diff, and still narrowable`` () =
    let change =
        { additive with
            UsesNow = Map.add "src/Lib/B.fs" (uses "X.edited") additive.UsesNow
            Diff = Set.add "src/Lib/B.fs" additive.Diff }

    test <@ judge change |> Verdict.isNarrowable @>

[<Fact>]
let ``a pre-existing file with no analysis at T is not narrowable, naming it`` () =
    let change =
        { additive with
            UsesAtT = Map.remove "src/Lib/A.fs" additive.UsesAtT }

    test <@ judge change = NotNarrowable(NoAnalysisAtT("Lib", "src/Lib/A.fs")) @>

[<Fact>]
let ``a pre-existing file with no analysis now is not narrowable, naming it`` () =
    let change =
        { additive with
            UsesNow = Map.remove "tests/T/Tests.fs" additive.UsesNow }

    test <@ judge change = NotNarrowable(NoAnalysisNow("T", "tests/T/Tests.fs")) @>

[<Fact>]
let ``a project outside the changed one's dependents is never compared`` () =
    let change =
        { additive with
            Before = Map.add "Other" (shape [ "src/Other/O.fs" ]) additive.Before
            After = Map.add "Other" (shape [ "src/Other/O.fs" ]) additive.After
            Dependents = Map.add "Other" Set.empty additive.Dependents }

    test <@ judge change |> Verdict.isNarrowable @>

// --- The log line ------------------------------------------------------------------------

[<Fact>]
let ``a narrowable verdict logs one would-narrow line naming the projects and the additions`` () =
    test
        <@ describe (judge additive) = "additive-model: would-narrow projects=Lib checked=Lib,T added=src/Lib/New.fs" @>

[<Fact>]
let ``every refusal logs one not-narrowable line with a reason token`` () =
    let lines =
        [ NoCompileItemsAdded
          ProjectsAddedOrRemoved([ "U" ], [ "T" ])
          ItemsRemoved("Lib", [ "src/Lib/A.fs" ])
          ItemsReordered "Lib"
          DefinesChanged "Lib"
          ReferencesChanged "Lib"
          OptionsChanged "Lib"
          SymbolUsesChanged("Lib", "src/Lib/B.fs")
          NoAnalysisAtT("Lib", "src/Lib/A.fs")
          NoAnalysisNow("T", "tests/T/Tests.fs") ]
        |> List.map (NotNarrowable >> describe)

    test
        <@
            lines = [ "additive-model: not-narrowable reason=no-compile-items-added"
                      "additive-model: not-narrowable reason=projects-added-or-removed added=U removed=T"
                      "additive-model: not-narrowable reason=items-removed project=Lib items=src/Lib/A.fs"
                      "additive-model: not-narrowable reason=items-reordered project=Lib"
                      "additive-model: not-narrowable reason=defines-changed project=Lib"
                      "additive-model: not-narrowable reason=references-changed project=Lib"
                      "additive-model: not-narrowable reason=options-changed project=Lib"
                      "additive-model: not-narrowable reason=symbol-uses-changed project=Lib file=src/Lib/B.fs"
                      "additive-model: not-narrowable reason=no-analysis-at-t project=Lib file=src/Lib/A.fs"
                      "additive-model: not-narrowable reason=no-analysis-now project=T file=tests/T/Tests.fs" ]
        @>

// --- Reading a project's shape from its compiler options -----------------------------------

[<Fact>]
let ``a project's shape splits its options into compile items, defines, references and the rest, relative to the root``
    ()
    =
    let root = "/repo"

    let options: FSharpProjectOptions =
        { ProjectFileName = "/repo/src/Lib/Lib.fsproj"
          ProjectId = None
          SourceFiles = [| "/repo/src/Lib/B.fs"; "/repo/src/Lib/A.fs" |]
          OtherOptions =
            [| "--define:TRACE"
               "-r:/repo/src/Core/bin/Core.dll"
               "--optimize-"
               "--define:DEBUG"
               "-r:/nuget/fsharp.core.dll"
               "--out:/repo/src/Lib/obj/Lib.dll" |]
          ReferencedProjects = [||]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = System.DateTime.MinValue
          UnresolvedReferences = None
          OriginalLoadReferences = []
          Stamp = None }

    let read = ProjectShape.ofOptions root options

    test <@ read.CompileItems = [ "src/Lib/B.fs"; "src/Lib/A.fs" ] @>
    test <@ read.Defines = [ "DEBUG"; "TRACE" ] @>
    test <@ read.References.Length = 2 @>
    test <@ read.References |> List.exists (fun r -> r.Contains "/nuget/fsharp.core.dll") @>
    test <@ read.References |> List.forall (fun r -> not (r.Contains "/repo/")) @>
    test <@ read.Options.Length = 2 && read.Options[0] = "--optimize-" @>
    test <@ read.Options |> List.forall (fun r -> not (r.Contains "/repo/")) @>

[<Fact>]
let ``two checkouts compiling the same project read the same shape`` () =
    let at (root: string) : FSharpProjectOptions =
        { ProjectFileName = $"%s{root}/src/Lib/Lib.fsproj"
          ProjectId = None
          SourceFiles = [| $"%s{root}/src/Lib/A.fs" |]
          OtherOptions = [| $"-r:%s{root}/src/Core/bin/Core.dll"; $"--out:%s{root}/obj/Lib.dll" |]
          ReferencedProjects = [||]
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = System.DateTime.MinValue
          UnresolvedReferences = None
          OriginalLoadReferences = []
          Stamp = None }

    test <@ ProjectShape.ofOptions "/w/a" (at "/w/a") = ProjectShape.ofOptions "/w/b" (at "/w/b") @>

[<Fact>]
let ``a file's symbol uses are its dependency edges, without their source stamp`` () =
    let analysis =
        AnalysisResult.Create(
            [],
            [ { FromSymbol = "M.f"
                ToSymbol = "M.g"
                Kind = Calls
                Source = "core" }
              { FromSymbol = "M.f"
                ToSymbol = "M.g"
                Kind = Calls
                Source = "falco" } ],
            []
        )

    test
        <@
            SymbolUses.ofAnalysis analysis = set
                [ { From = "M.f"
                    To = "M.g"
                    Kind = Calls } ]
        @>
