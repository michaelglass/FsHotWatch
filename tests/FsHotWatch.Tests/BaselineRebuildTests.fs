/// AUTOMATION-1793 step 1 (intelligence ADR 0651, revised): a checkout with no full pass of
/// its own rebuilds its impact index at a tree T another checkout passed in full, from the
/// box-wide analysis records T's `test-baseline` manifest names. The soundness of the whole
/// thing is that EVERY analysable entry resolves, or there is no baseline: never a partial
/// index, which would diff T to T' against missing rows and select too little.
///
/// Amendment 2: each analysable row names the exact record (project and key) filing found
/// it under, so resolving is an exact lookup, never a search; a row from a record filed
/// before that names no key, and its analyses were never pinned against GC.
module FsHotWatch.Tests.BaselineRebuildTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = 1_000_000_000L
          Log = ignore }
    )

let private analysis: AnalysisResult = AnalysisResult.Create([], [], [])

let private identities =
    Map.ofList [ "src/A/A.fs", "id-a"; "tests/TA/T.fs", "id-t" ]

/// The projects each file compiles in, in the reading checkout's model.
let private projectsOf (path: string) =
    match path with
    | "src/A/A.fs" -> [ "A" ]
    | "tests/TA/T.fs" -> [ "TA" ]
    | _ -> []

/// The row a filed record carries for `path`, pinned to `project`'s record.
let private pinnedTo (project: string) (path: string) =
    TestBaseline.RowAnalysis.Pinned(
        identities[path],
        project,
        (AnalysisRecords.keyOf identities[path] path project).Value.Text
    )

let private pinned =
    Map.ofList
        [ "src/A/A.fs", pinnedTo "A" "src/A/A.fs"
          "tests/TA/T.fs", pinnedTo "TA" "tests/TA/T.fs" ]

/// T's manifest as a `test-baseline` record carries it: a source file and a test file with
/// analyses, and a project file, which is never analysed and has none.
let private recordWith (analyses: Map<string, TestBaseline.RowAnalysis>) : TestBaseline.CheckoutRecord =
    { Tree = "sha256:tree-1"
      Key = "k"
      RunId = "run-1"
      Entries =
        [ "src/A/A.fs", "sha256:a"
          "src/A/A.fsproj", "sha256:ap"
          "tests/TA/T.fs", "sha256:t" ]
      Analyses = analyses
      FullKeys = Map.empty
      KnownInputs = Map.empty
      FiledAt = DateTimeOffset.MinValue }

/// File `path`'s analysis as `project`'s check in checkout `root` would have, under
/// `analyzer`'s key.
let private fileUnder
    (store: OutcomeStore.Store)
    (root: string)
    (analyzer: string)
    (project: string)
    (result: AnalysisResult)
    (path: string)
    =
    let key = AnalysisRecords.keyWith analyzer identities[path] path project
    test <@ AnalysisRecords.file store root path key result @>

let private fileAs (store: OutcomeStore.Store) (root: string) (analyzer: string) (path: string) =
    fileUnder store root analyzer (List.head (projectsOf path)) analysis path

let private fileBoth (store: OutcomeStore.Store) =
    fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"
    fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "tests/TA/T.fs"

[<Fact>]
let ``every pinned manifest entry resolves to its record, and a file with no analysis is not asked for`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        match BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith pinned) with
        | Ok resolved ->
            test <@ resolved |> List.map (fun r -> r.Path, r.Project) = [ "src/A/A.fs", "A"; "tests/TA/T.fs", "TA" ] @>
        | Error misses -> failwith $"expected every entry to resolve: %A{misses}")

[<Fact>]
let ``one missing record is no baseline, never a partial index`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith pinned) = Error
                    [ BaselineRebuild.Miss.NoRecord "tests/TA/T.fs" ]
            @>)

[<Fact>]
let ``a pinned record that GC has since evicted is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store
        let evicted = AnalysisRecords.keyOf identities["src/A/A.fs"] "src/A/A.fs" "A"
        Directory.Delete(store.DirectoryOf evicted, true)

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith pinned) = Error
                    [ BaselineRebuild.Miss.NoRecord "src/A/A.fs" ]
            @>)

[<Fact>]
let ``a record whose identity matches but whose analyser build differs is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"
        fileAs store "/ws/a" "another-analyser-build" "tests/TA/T.fs"

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith pinned) = Error
                    [ BaselineRebuild.Miss.NoRecord "tests/TA/T.fs" ]
            @>)

[<Fact>]
let ``a pinned key that this analyser would not compute for the row is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        let foreign =
            pinned
            |> Map.add
                "src/A/A.fs"
                (TestBaseline.RowAnalysis.Pinned(
                    "id-a",
                    "A",
                    (AnalysisRecords.keyWith "another-analyser-build" "id-a" "src/A/A.fs" "A").Value.Text
                ))

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith foreign) = Error
                    [ BaselineRebuild.Miss.NoRecord "src/A/A.fs" ]
            @>)

[<Fact>]
let ``a manifest file the reading checkout no longer has is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        let gone (path: string) =
            if path = "src/A/A.fs" then [] else projectsOf path

        test
            <@
                BaselineRebuild.resolve store "/ws/a" gone (recordWith pinned) = Error
                    [ BaselineRebuild.Miss.NoProject "src/A/A.fs" ]
            @>)

[<Fact>]
let ``a file the recorded project no longer compiles, though another still does, is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store
        fileUnder store "/ws/a" AnalysisRecords.analyzerIdentity "GateHook" analysis "src/A/A.fs"

        let onlyInGateHook (path: string) =
            if path = "src/A/A.fs" then
                [ "GateHook" ]
            else
                projectsOf path

        test
            <@
                BaselineRebuild.resolve store "/ws/a" onlyInGateHook (recordWith pinned) = Error
                    [ BaselineRebuild.Miss.NoProject "src/A/A.fs" ]
            @>)

[<Fact>]
let ``a row filed before keys were pinned is no baseline, even when a record under its identity exists`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        let older =
            pinned |> Map.add "tests/TA/T.fs" (TestBaseline.RowAnalysis.Unpinned "id-t")

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectsOf (recordWith older) = Error
                    [ BaselineRebuild.Miss.NoRecord "tests/TA/T.fs" ]
            @>)

[<Fact>]
let ``records filed by checkout A resolve in checkout B at another path`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        test
            <@
                BaselineRebuild.resolve store "/ws/b" projectsOf (recordWith pinned)
                |> Result.isOk
            @>)

[<Fact>]
let ``a test-baseline record filed by checkout A resolves in checkout B through the keys it pinned`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store
        let tree = "sha256:tree-1", (recordWith pinned).Entries

        let fullKeyOf (project: string) =
            TestEvidenceRun.testActionKey
                { Value = "kp-" + project
                  Labels = [ "closure", "c-" + project ] }
                "full"

        let record: OutcomeStore.Record =
            { Key = fullKeyOf "TA"
              Subject = "TA"
              Outcome = OutcomeStore.Outcome.Pass
              Counts = None
              DurationMs = None
              Producer =
                { Binary = "fshw-test"
                  Worktree = "/ws/a"
                  RunId = Some "run-1" }
              RecordedAt = DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero) }

        test <@ store.Record record @>

        let facts: TestBaseline.Facts =
            { RunId = "run-1"
              EarnsBaseline = true
              RunnableProjects = Set.ofList [ "TA" ]
              SealedTree = Some(fst tree)
              ReceiptTree = Some tree }

        let producer: TestBaseline.Producer =
            { Binary = "fshw-test"
              Worktree = "/ws/a"
              Now = DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero) }

        match
            TestBaseline.file
                (Some store)
                "/ws/a"
                projectsOf
                (fun () -> Map.ofList [ "TA", Ok (fullKeyOf "TA").Value.Text ])
                (fun () -> identities)
                producer
                facts
        with
        | Ok _ -> ()
        | Error refusals -> failwith $"refused: %A{refusals}"

        match TestBaseline.checkoutRecordFor store (fst tree) with
        | Ok record ->
            test <@ record.Analyses = pinned @>
            // Filing keeps the known inputs the manifest never holds, as K_P digests them.
            test <@ record.KnownInputs = Map.ofList (TestEvidenceKey.knownInputDigests "/ws/a") @>

            test
                <@
                    BaselineRebuild.resolve store "/ws/b" projectsOf record
                    |> Result.map (List.map _.Path) = Ok [ "src/A/A.fs"; "tests/TA/T.fs" ]
                @>
        | Error why -> failwith $"expected a hit: %s{why}")
