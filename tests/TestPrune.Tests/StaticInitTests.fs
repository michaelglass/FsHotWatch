module TestPrune.Tests.StaticInitTests

// AUTOMATION-1733 — reading one module-level value runs its FILE's static initializer.
//
// F# compiles the module-level values of a file (`let empty = { … }`, `let all = [ … ]`)
// into one static initializer for that file, and the first READ of any of them runs all of
// them. So a reader of one value depends on every other value of that file: if
// `let empty = { … }` throws, reading any value of the file fails with a
// TypeInitializationException.
//
// Measured 2026-11-16 on SDK 10.0.401, with `--realsig-` and `--realsig+` alike: reading a
// computed value in ANY module of the file ran the initializer, including a function-typed
// value built by partial application (`let f: int -> int = (+) 1`), and so did constructing a
// class with a `static let` or a `static member val`. Calling a function, constructing a
// record, using a union case, or a class or static member of a type without static fields did
// NOT, and neither did reading a constant (`let c = 5`, which is inlined).
//
// Measured in a consumer (2026-11-15): made to throw, `OrgDetails`'s constructor failed 24
// tests and `DsaSubjectFacts`'s 3, and static selection for a change to either selected none
// of them. Each type is constructed by a module-level value of its file (`OrgDetails.empty`,
// `DsaFactSchema.unknownSubject`), and the failing tests read OTHER values of that file
// (`DsaFactSchema.bindEvaluator` reads the evaluator values beside `unknownSubject`).

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.InMemoryStore

[<Collection("FCS-StaticInit")>]
module ``File static initialization`` =

    let private checker = FSharpChecker.Create()

    let private analyzeFile (fileName: string) (projectName: string) (source: string) =
        let options = getScriptOptions checker fileName source |> Async.RunSynchronously

        match
            analyzeSource checker fileName source options projectName
            |> Async.RunSynchronously
        with
        | Ok result -> result
        | Error error -> failwith $"Analysis failed for {fileName}: {error}"

    /// The incident's shape: a record, a module-level value that constructs it, and another
    /// value of the same file that a function reads without naming either.
    let private domainSource =
        """module Domain

type Facts =
    { Country: string option
      Count: int option }

let unknownSubject: Facts = { Country = None; Count = None }

type Category =
    | Hosting
    | Caching

// A partial application, so a static field the initializer evaluates (a lambda would be
// compiled as a method instead).
let isHosting: string -> bool = (=) "hosting"

let bindEvaluator (name: string) : (string -> bool) option =
    if name = "hosting" then Some isHosting else None

let describe (category: Category) =
    match category with
    | Hosting -> "hosting"
    | Caching -> "caching"

[<Literal>]
let Version = "1"

// A `static let`: its class constructor runs the file's initializer, so constructing the
// class runs it too (the consumer's case was a JSON converter with a defaults table).
type Converter() =
    static let defaults = [ "jurisdictions" ]
    member _.Count = List.length defaults

module Counters =
    let mutable counter = 0
"""

    let private testsSource =
        """// `#load` puts the domain file BEFORE this one, as a project's compile order would.
#load "Domain.fsx"

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let bindsAnEvaluator () =
    if (Domain.bindEvaluator "hosting").IsNone then failwith "unbound"

[<Fact>]
let describesACategory () =
    if Domain.describe Domain.Hosting <> "hosting" then failwith "wrong"

[<Fact>]
let convertsWithDefaults () =
    if Domain.Converter().Count <> 1 then failwith "no defaults"
"""

    /// Analyse the two files the way a project would see them, in a scratch directory.
    let private withAnalysedFiles (f: string -> AnalysisResult -> AnalysisResult -> unit) =
        let dir = FsHotWatch.TestRoots.TestRoot.freshPath "testprune-static-init"

        Directory.CreateDirectory dir |> ignore

        try
            let domainFile = Path.Combine(dir, "Domain.fsx")
            let testsFile = Path.Combine(dir, "Tests.fsx")
            File.WriteAllText(domainFile, domainSource)
            File.WriteAllText(testsFile, testsSource)

            f domainFile (analyzeFile domainFile "Domain" domainSource) (analyzeFile testsFile "Tests" testsSource)
        finally
            try
                Directory.Delete(dir, true)
            with _ ->
                ()

    let private selectedFor (store: TestPrune.Ports.SymbolStore) (changed: string) =
        store.QueryAffectedTests [ changed ]
        |> List.map _.TestMethod
        |> List.distinct
        |> List.sort

    [<Fact>]
    let ``a change to a type another value of its file constructs selects the readers of that file's values`` () =
        withAnalysedFiles (fun _ domain tests ->
            let store = fromAnalysisResults [ domain; tests ]

            // `bindsAnEvaluator` never names `Facts` or `unknownSubject`: it reaches them only
            // because `bindEvaluator` reads `isHosting`, whose read runs the initializer that
            // constructs `unknownSubject`. Before AUTOMATION-1733 the walk from `Domain.Facts`
            // stopped at `Domain.unknownSubject` and selected nothing. `convertsWithDefaults`
            // constructs a class whose `static let` runs the same initializer.
            test <@ selectedFor store "Domain.Facts" = [ "bindsAnEvaluator"; "convertsWithDefaults" ] @>

            test <@ selectedFor store "Domain.unknownSubject" = [ "bindsAnEvaluator"; "convertsWithDefaults" ] @>)

    [<Fact>]
    let ``a file's functions and types run no initializer, so they gain no edge`` () =
        // NEGATIVE CONTROL: `describesACategory` calls a function and uses a union case of the
        // same file and reads no value; measured, neither runs the file's initializer.
        withAnalysedFiles (fun _ domain tests ->
            let store = fromAnalysisResults [ domain; tests ]

            test <@ selectedFor store "Domain.describe" = [ "describesACategory" ] @>
            test <@ selectedFor store "Domain.bindEvaluator" = [ "bindsAnEvaluator" ] @>

            let staticInitSources =
                domain.Dependencies
                |> List.filter (fun d -> d.Kind = StaticInit)
                |> List.map _.FromSymbol
                |> Set.ofList

            for nonValue in
                [ "Domain.describe"
                  "Domain.bindEvaluator"
                  "Domain.Category"
                  "Domain.Facts"
                  "Domain.Version" ] do
                test <@ not (staticInitSources.Contains nonValue) @>)

    [<Fact>]
    let ``each value of a file reaches every other value its initializer evaluates`` () =
        withAnalysedFiles (fun _ domain _ ->
            let edges =
                domain.Dependencies
                |> List.filter (fun d -> d.Kind = StaticInit)
                |> List.groupBy _.FromSymbol
                |> List.map (fun (from, deps) -> from, deps |> List.map _.ToSymbol)
                |> Map.ofList

            let rec walk seen frontier =
                match frontier with
                | [] -> seen
                | x :: rest when Set.contains x seen -> walk seen rest
                | x :: rest -> walk (Set.add x seen) ((Map.tryFind x edges |> Option.defaultValue []) @ rest)

            let values =
                set
                    [ "Domain.unknownSubject"
                      "Domain.isHosting"
                      "Domain.Converter"
                      "Domain.Counters.counter" ]

            for value in values do
                test <@ walk Set.empty [ value ] = values @>)

    [<Fact>]
    let ``static initialization edges carry their own kind, are not self edges and skip literals`` () =
        withAnalysedFiles (fun _ domain _ ->
            let staticInit = domain.Dependencies |> List.filter (fun d -> d.Kind = StaticInit)

            test <@ staticInit.Length = 4 @>
            test <@ staticInit |> List.forall (fun d -> d.FromSymbol <> d.ToSymbol) @>
            test <@ staticInit |> List.forall (fun d -> d.Source = CoreSource) @>

            let targets = staticInit |> List.map _.ToSymbol |> List.sort

            test
                <@
                    targets = [ "Domain.Converter"
                                "Domain.Counters.counter"
                                "Domain.isHosting"
                                "Domain.unknownSubject" ]
                @>)

    [<Fact>]
    let ``static initialization edges round-trip through the database and drive its selection`` () =
        withAnalysedFiles (fun domainFile domain tests ->
            let dbPath = FsHotWatch.TestRoots.TestRoot.freshPath "testprune-static-init" + ".db"

            try
                let db = Database.create dbPath
                db.RebuildProjects [ domain; tests ]

                let stored =
                    db.GetDependenciesFromFile domainFile
                    |> List.filter (fun d -> d.Kind = StaticInit)

                test <@ stored.Length = 4 @>

                let selected =
                    db.QueryAffectedTests [ "Domain.Facts" ]
                    |> List.map _.TestMethod
                    |> List.distinct
                    |> List.sort

                test <@ selected = [ "bindsAnEvaluator"; "convertsWithDefaults" ] @>
            finally
                TestPrune.Tests.TestHelpers.clearSqlitePool dbPath
                TestPrune.Tests.TestHelpers.cleanupDb dbPath)

    [<Fact>]
    let ``dead-code reachability does not count a file's initializer as a use of its values`` () =
        // A module-level value nothing names is still dead code, even though the file's
        // initializer evaluates it: the edge exists for selection, not for reachability.
        withAnalysedFiles (fun _ domain _ ->
            let store = fromAnalysisResults [ domain ]
            let reachable = store.GetReachableSymbols [ "Domain.bindEvaluator" ]

            test <@ reachable.Contains "Domain.isHosting" @>
            test <@ not (reachable.Contains "Domain.unknownSubject") @>
            test <@ not (reachable.Contains "Domain.Counters.counter") @>

            let dbPath =
                FsHotWatch.TestRoots.TestRoot.freshPath "testprune-static-init-dead" + ".db"

            try
                let db = Database.create dbPath
                db.RebuildProjects [ domain ]
                let stored = db.GetReachableSymbols [ "Domain.bindEvaluator" ]
                test <@ stored.Contains "Domain.isHosting" @>
                test <@ not (stored.Contains "Domain.unknownSubject") @>

                let incoming = db.GetIncomingEdgesBatch [ "Domain.unknownSubject" ]
                test <@ incoming |> Map.tryFind "Domain.unknownSubject" |> Option.isNone @>
            finally
                TestPrune.Tests.TestHelpers.clearSqlitePool dbPath
                TestPrune.Tests.TestHelpers.cleanupDb dbPath)
