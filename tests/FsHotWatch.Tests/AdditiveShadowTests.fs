/// AUTOMATION-1900 step 3: the additive-model shadow (`AdditiveShadow`). It judges the model
/// change between the retained box tree T and now from T's filed compile model and analyses,
/// and says so in one line. It never decides anything a launch reads.
module FsHotWatch.Tests.AdditiveShadowTests

open System
open System.Text.Json.Nodes
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.AdditiveModelChange
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = 1_000_000_000L
          Log = ignore }
    )

[<Literal>]
let private Root = "/ws/a"

[<Literal>]
let private LibPath = "src/A/A.fs"

[<Literal>]
let private TestsPath = "tests/TA/T.fs"

[<Literal>]
let private NewPath = "src/A/New.fs"

let private identities = Map.ofList [ LibPath, "id-a"; TestsPath, "id-t" ]

let private projectsOf (path: string) =
    match path with
    | LibPath -> [ "A" ]
    | TestsPath -> [ "TA" ]
    | _ -> []

let private calls (from: string) (target: string) : Dependency =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = CoreSource }

let private libAnalysis = AnalysisResult.Create([], [ calls "A.f" "A.g" ], [])

let private testsAnalysis = AnalysisResult.Create([], [ calls "TA.t" "A.f" ], [])

let private shape (items: string list) : ProjectShape =
    { CompileItems = items
      Defines = []
      References = "refs"
      Options = "opts" }

let private modelAtT =
    Map.ofList [ "A", shape [ LibPath ]; "TA", shape [ TestsPath ] ]

let private manifestAtT =
    [ LibPath, "sha256:a"; "src/A/A.fsproj", "sha256:ap"; TestsPath, "sha256:t" ]

let private treeOf (entries: (string * string) list) : ReceiptTree =
    let sorted = List.sortBy fst entries

    { Identity = TreeHash.hashEntries sorted
      Entries = sorted }

let private treeT = treeOf manifestAtT

/// The working tree after the land: `New.fs` added, the project file changed.
let private treeNow =
    treeOf (
        (manifestAtT
         |> List.map (fun (path, hash) ->
             if path = "src/A/A.fsproj" then
                 path, "sha256:ap2"
             else
                 path, hash))
        @ [ NewPath, "sha256:new" ]
    )

let private at = DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero)

/// A judgement whose relied-on files show no blind spot.
let private judged (tree: string, verdict: Verdict) =
    AdditiveShadow.Outcome.Judged
        { Tree = tree
          Verdict = verdict
          BlindSpots =
            { Dropped = NoDroppedEdges
              Overloads = NoOverloads } }

/// File T as a green full run would: each file's analysis record (`lib` and `tests`), TA's full
/// pass, and the test-baseline record with `model` as T's compile model.
let private fileTWith
    (store: OutcomeStore.Store)
    (model: Map<string, ProjectShape>)
    (lib: AnalysisResult)
    (tests: AnalysisResult)
    =
    for path, analysis in [ LibPath, lib; TestsPath, tests ] do
        let key = AnalysisRecords.keyOf identities[path] path (List.head (projectsOf path))

        test <@ AnalysisRecords.file store Root path key analysis @>

    let fullKey =
        TestEvidenceRun.testActionKey
            { Value = "kp-TA"
              Labels = [ "closure", "c-TA" ] }
            "full"

    let pass: OutcomeStore.Record =
        { Key = fullKey
          Subject = "TA"
          Outcome = OutcomeStore.Outcome.Pass
          Counts = None
          DurationMs = None
          Producer =
            { Binary = "fshw-test"
              Worktree = Root
              RunId = Some "run-1" }
          RecordedAt = at }

    test <@ store.Record pass @>

    let facts: TestBaseline.Facts =
        { RunId = "run-1"
          EarnsBaseline = true
          RunnableProjects = Set.ofList [ "TA" ]
          SealedTree = Some treeT.Identity
          ReceiptTree = Some(treeT.Identity, treeT.Entries)
          CompileModel = model }

    let producer: TestBaseline.Producer =
        { Binary = "fshw-test"
          Worktree = Root
          Now = at }

    match
        TestBaseline.file
            (Some store)
            Root
            projectsOf
            (fun () -> Map.ofList [ "TA", Ok fullKey.Value.Text ])
            (fun () -> identities)
            producer
            facts
    with
    | Ok _ -> ()
    | Error refusals -> failwith $"T was not filed: %A{refusals}"

let private fileT (store: OutcomeStore.Store) (model: Map<string, ProjectShape>) =
    fileTWith store model libAnalysis testsAnalysis

/// The shadow's view after the land: `A` gained `New.fs` before `A.fs`; the index holds T's own
/// analysis of `A.fs` and a new one of `T.fs`, whose uses are `testsUsesNow`.
let private envAfter (store: OutcomeStore.Store option) (testsUsesNow: Set<SymbolUse>) : AdditiveShadow.Env =
    { Store = store |> Option.map (fun s -> s :> OutcomeRecords.IOutcomeStore)
      Root = Root
      Current = Some treeNow
      ShapesNow = Map.ofList [ "A", shape [ NewPath; LibPath ]; "TA", shape [ TestsPath ] ]
      Dependents =
        fun project ->
            match project with
            | "A" -> set [ "TA" ]
            | _ -> Set.empty
      FileKeys = Map.ofList [ LibPath, "id-a"; TestsPath, "id-t-now"; NewPath, "id-new" ]
      UsesNow =
        fun path ->
            match path with
            | LibPath -> SymbolUses.ofAnalysis libAnalysis
            | TestsPath -> testsUsesNow
            | _ -> Set.empty }

[<Fact>]
let ``a test-baseline record files T's compile model, and reads it back`` () =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir
        fileT store modelAtT

        test <@ TestBaseline.checkoutRecordFor store treeT.Identity |> Result.map _.CompileModel = Ok modelAtT @>)

[<Fact>]
let ``an insert that leaves every pre-existing file's uses as at T would narrow, naming the addition and the tree`` () =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir
        fileT store modelAtT

        // T.fs was re-analysed (its index key moved), so its uses at T are read from the record T
        // pinned; A.fs's index entry IS T's analysis.
        let outcome =
            AdditiveShadow.observe (envAfter (Some store) (SymbolUses.ofAnalysis testsAnalysis))

        test
            <@
                outcome = judged (
                    treeT.Identity,
                    Narrowable
                        { Added = Map.ofList [ "A", [ NewPath ] ]
                          Checked = set [ "A"; "TA" ] }
                )
            @>

        test
            <@
                AdditiveShadow.describe outcome = $"additive-model: would-narrow projects=A checked=A,TA added=%s{NewPath} tree=%s{treeT.Identity} dropped-edges=0 dropped-max=0 multi-overload=no"
            @>)

[<Fact>]
let ``a downstream file the insert rebound is not narrowable, naming it`` () =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir
        fileT store modelAtT

        let rebound =
            set
                [ { From = "TA.t"
                    To = "A.Shadow.f"
                    Kind = Calls } ]

        test
            <@
                AdditiveShadow.observe (envAfter (Some store) rebound) = judged (
                    treeT.Identity,
                    NotNarrowable(SymbolUsesChanged("TA", TestsPath))
                )
            @>)

let private text (value: string) = JsonValue.Create value :> JsonNode

let private hashAtT (path: string) =
    Map.find path (Map.ofList treeT.Entries)

/// A manifest row that pins `path`'s analysis to `key` in `project`'s record.
let private pinnedRow (path: string) (identity: string) (project: string) (key: string) =
    JsonArray(text path, text (hashAtT path), text identity, text project, text key)

/// A manifest row with no analysis, or an identity with no pinned record (filed before keys
/// were pinned).
let private plainRow (path: string) (identity: string option) =
    match identity with
    | Some id -> JsonArray(text path, text (hashAtT path), text id)
    | None -> JsonArray(text path, text (hashAtT path))

/// T's record filed by hand, with `rows` as its manifest and `compileModel` as given (or none),
/// and named in the index so a restart retains it.
let private fileHand (store: OutcomeStore.Store) (model: JsonNode option) (rows: JsonArray list) =
    let o = JsonObject()
    o["schema"] <- TestBaseline.Schema
    o["runId"] <- "run-1"
    o["tree"] <- treeT.Identity
    o["analyzer"] <- AnalysisRecords.analyzerIdentity
    o["projects"] <- JsonObject()
    let manifest = JsonArray()

    for row in rows do
        manifest.Add row

    o["manifest"] <- manifest

    match model with
    | Some m -> o["compileModel"] <- m
    | None -> ()

    let key = TestBaseline.keyOf treeT.Identity

    test
        <@
            (store.Supersede(
                OutcomeStore.Record.ofAction key treeT.Identity OutcomeStore.Outcome.Pass "fshw-test" Root at,
                Some(o.ToJsonString())
            ))
                .IsSome
        @>

    test
        <@
            (TestBaseline.appendIndex
                store
                { Binary = "fshw-test"
                  Worktree = Root
                  Now = at }
                { Tree = treeT.Identity
                  Baseline = key.Value.Text
                  RunId = "run-1"
                  At = at })
                .IsSome
        @>

/// T's compile model as filing writes it.
let private modelJson () =
    let strings (values: string list) =
        JsonArray(values |> List.map text |> List.toArray) :> JsonNode

    let m = JsonObject()

    for KeyValue(project, s) in modelAtT do
        let o = JsonObject()
        o["items"] <- strings s.CompileItems
        o["defines"] <- strings s.Defines
        o["references"] <- s.References
        o["options"] <- s.Options
        m[project] <- o

    Some(m :> JsonNode)

/// The verdict for `T.fs` when T's record names it with `testsRow`, its uses now unchanged
/// and its index entry no longer T's.
let private verdictWithTestsRow (testsRow: JsonArray) =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir
        let libKey = AnalysisRecords.keyOf "id-a" LibPath "A"
        test <@ AnalysisRecords.file store Root LibPath libKey libAnalysis @>

        fileHand
            store
            (modelJson ())
            [ pinnedRow LibPath "id-a" "A" libKey.Value.Text
              plainRow "src/A/A.fsproj" None
              testsRow ]

        AdditiveShadow.observe (envAfter (Some store) (SymbolUses.ofAnalysis testsAnalysis)))

[<Fact>]
let ``a pre-existing file whose analysis at T does not read has no uses at T, and is not narrowable`` () =
    let expected =
        judged (treeT.Identity, NotNarrowable(NoAnalysisAtT("TA", TestsPath)))

    let rightKey = (AnalysisRecords.keyOf "id-t" TestsPath "TA").Value.Text

    // Pinned to the right key, but its record was never filed (or GC evicted it).
    test <@ verdictWithTestsRow (pinnedRow TestsPath "id-t" "TA" rightKey) = expected @>
    // Pinned to a key this analyser build would not compute for it.
    test <@ verdictWithTestsRow (pinnedRow TestsPath "id-t" "TA" "some-other-key") = expected @>
    // Filed before keys were pinned: its analysis was never kept.
    test <@ verdictWithTestsRow (plainRow TestsPath (Some "id-t")) = expected @>
    // T never analysed it.
    test <@ verdictWithTestsRow (plainRow TestsPath None) = expected @>

[<Fact>]
let ``a T filed before compile models were recorded is not judged, naming the tree`` () =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir
        fileT store Map.empty
        let outcome = AdditiveShadow.observe (envAfter (Some store) Set.empty)

        test
            <@ outcome = AdditiveShadow.Outcome.NotJudged(AdditiveShadow.NotJudged.NoCompileModelAtT treeT.Identity) @>

        test
            <@
                AdditiveShadow.describe outcome = $"additive-model: not-judged reason=no-compile-model-at-t tree=%s{treeT.Identity}"
            @>)

[<Fact>]
let ``no store, no readable tree or no retained baseline is not judged, each with its own reason`` () =
    withTempDir "additive-shadow" (fun dir ->
        let empty = storeIn dir

        let lines =
            [ envAfter None Set.empty
              { envAfter (Some empty) Set.empty with
                  Current = None }
              envAfter (Some empty) Set.empty ]
            |> List.map (AdditiveShadow.observe >> AdditiveShadow.describe)

        test
            <@
                lines = [ "additive-model: not-judged reason=no-store"
                          "additive-model: not-judged reason=no-tree"
                          "additive-model: not-judged reason=no-retained-baseline" ]
            @>)

[<Fact>]
let ``a shadow that throws is a line saying so, never a failed launch`` () =
    let line =
        AdditiveShadow.lineWith AdditiveShadow.observe (fun () -> invalidOp "the index is gone")

    test <@ line = "additive-model: not-judged reason=error — InvalidOperationException: the index is gone" @>

[<Fact>]
let ``the line is the observer's outcome, described`` () =
    let line =
        AdditiveShadow.lineWith (fun _ -> judged ("sha256:t", NotNarrowable(ItemsReordered "A"))) (fun () ->
            envAfter None Set.empty)

    test
        <@
            line = "additive-model: not-narrowable reason=items-reordered project=A tree=sha256:t dropped-edges=0 dropped-max=0 multi-overload=no"
        @>

// --- A record's compile model as JSON holds it ---------------------------------------------

/// T's compile model as a hand-filed record with `compileModel` as given (or none) reads.
let private recordWithModel (store: OutcomeStore.Store) (model: JsonNode option) =
    fileHand store model (treeT.Entries |> List.map (fun (path, _) -> plainRow path None))

    TestBaseline.checkoutRecordFor store treeT.Identity |> Result.map _.CompileModel

[<Fact>]
let ``a record without a compile model, or with any shape that does not read, has none`` () =
    let shapeJson (items: JsonNode) (defines: JsonNode) =
        let s = JsonObject()
        s["items"] <- items
        s["defines"] <- defines
        s["references"] <- "refs"
        s["options"] <- "opts"
        s :> JsonNode

    let strings (values: string list) =
        JsonArray(values |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> List.toArray) :> JsonNode

    let modelOf (entries: (string * JsonNode) list) =
        let m = JsonObject()

        for name, value in entries do
            m[name] <- value

        Some(m :> JsonNode)

    let read (model: JsonNode option) =
        withTempDir "additive-shadow" (fun dir -> recordWithModel (storeIn dir) model)

    let good () =
        shapeJson (strings [ LibPath ]) (strings [])

    test <@ read (modelOf [ "A", good () ]) = Ok(Map.ofList [ "A", shape [ LibPath ] ]) @>
    test <@ read None = Ok Map.empty @>
    test <@ read (Some(JsonValue.Create "not an object" :> JsonNode)) = Ok Map.empty @>
    test <@ read (modelOf [ "A", good (); "B", JsonValue.Create 3 :> JsonNode ]) = Ok Map.empty @>

    test
        <@
            read (
                modelOf
                    [ "A", good ()
                      "B", shapeJson (JsonValue.Create "items" :> JsonNode) (strings []) ]
            ) = Ok Map.empty
        @>

    test
        <@
            read (
                modelOf
                    [ "A", good ()
                      "B", shapeJson (JsonArray(JsonValue.Create 1 :> JsonNode) :> JsonNode) (strings []) ]
            ) = Ok Map.empty
        @>

    test <@ read (modelOf [ "A", good (); "B", JsonObject() :> JsonNode ]) = Ok Map.empty @>

// --- What the plugin reads the "now" side from ---------------------------------------------

[<Fact>]
let ``a project's dependents are its graph's transitive dependents by name, and none for a project the graph does not name``
    ()
    =
    let graph =
        { FsHotWatch.PluginFramework.ProjectGraphAccessor.none with
            GetAllProjects = fun () -> [ "/repo/src/A/A.fsproj"; "/repo/tests/TA/TA.fsproj" ]
            GetTransitiveDependentProjects =
                fun fsproj ->
                    if fsproj = "/repo/src/A/A.fsproj" then
                        [ "/repo/tests/TA/TA.fsproj" ]
                    else
                        [] }

    test <@ AdditiveShadow.dependentsIn graph "A" = set [ "TA" ] @>
    test <@ AdditiveShadow.dependentsIn graph "TA" = Set.empty @>
    test <@ AdditiveShadow.dependentsIn graph "Unknown" = Set.empty @>

[<Fact>]
let ``a file's uses now are its core edges only: an extension's edges are no use the compiler resolved`` () =
    let extension =
        { calls "A.f" "Route.get" with
            Source = "falco" }

    test
        <@
            AdditiveShadow.coreUses [ calls "A.f" "A.g"; extension ] = set
                [ { From = "A.f"
                    To = "A.g"
                    Kind = Calls } ]
        @>

// --- Blind-spot fields --------------------------------------------------------------------

[<Fact>]
let ``a judgement logs the relied-on files' dropped edges and a used overloaded member, and judges exactly as without them``
    ()
    =
    withTempDir "additive-shadow" (fun dir ->
        let store = storeIn dir

        // A.fs defines `A.f` twice (two overloads, one name) and dropped 2 uses; T.fs, which uses
        // `A.f`, dropped 3.
        let lib =
            { libAnalysis with
                Symbols =
                    [ { FullName = "A.f"
                        Kind = Function
                        Location = InSource(LibPath, 1, 1, "h1") }
                      { FullName = "A.f"
                        Kind = Function
                        Location = InSource(LibPath, 2, 2, "h2") } ]
                Diagnostics =
                    { AnalysisDiagnostics.Zero with
                        DroppedEdges = 2 } }

        let tests =
            { testsAnalysis with
                Diagnostics =
                    { AnalysisDiagnostics.Zero with
                        DroppedEdges = 3 } }

        fileTWith store modelAtT lib tests

        let outcome =
            AdditiveShadow.observe (envAfter (Some store) (SymbolUses.ofAnalysis testsAnalysis))

        test
            <@
                outcome = AdditiveShadow.Outcome.Judged
                    { Tree = treeT.Identity
                      Verdict =
                        Narrowable
                            { Added = Map.ofList [ "A", [ NewPath ] ]
                              Checked = set [ "A"; "TA" ] }
                      BlindSpots =
                        { Dropped = DroppedEdges(5, TestsPath, 3)
                          Overloads = Overloaded "A.f" } }
            @>

        test
            <@
                AdditiveShadow.describe outcome = $"additive-model: would-narrow projects=A checked=A,TA added=%s{NewPath} tree=%s{treeT.Identity} dropped-edges=5 dropped-max=3 file=%s{TestsPath} multi-overload=yes entity=A.f"
            @>)
