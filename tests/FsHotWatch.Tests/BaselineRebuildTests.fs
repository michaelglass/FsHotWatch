/// AUTOMATION-1793 step 1 (intelligence ADR 0651, revised): a checkout with no full pass of
/// its own rebuilds its impact index at a tree T another checkout passed in full, from the
/// box-wide analysis records T's `test-baseline` manifest names. The soundness of the whole
/// thing is that EVERY analysable entry resolves, or there is no baseline: never a partial
/// index, which would diff T to T' against missing rows and select too little.
module FsHotWatch.Tests.BaselineRebuildTests

open System
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

/// T's manifest as a `test-baseline` record carries it: a source file and a test file with
/// analysis identities, and a project file, which is never analysed and has none.
let private recordWith (identities: Map<string, string>) : TestBaseline.CheckoutRecord =
    { Tree = "sha256:tree-1"
      Key = "k"
      RunId = "run-1"
      Entries =
        [ "src/A/A.fs", "sha256:a"
          "src/A/A.fsproj", "sha256:ap"
          "tests/TA/T.fs", "sha256:t" ]
      Identities = identities }

let private identities =
    Map.ofList [ "src/A/A.fs", "id-a"; "tests/TA/T.fs", "id-t" ]

/// The project each file belongs to in the reading checkout's model.
let private projectOf (path: string) =
    match path with
    | "src/A/A.fs" -> Some "A"
    | "tests/TA/T.fs" -> Some "TA"
    | _ -> None

/// File `path`'s analysis as checkout `root` would have, under `analyzer`'s key.
let private fileAs (store: OutcomeStore.Store) (root: string) (analyzer: string) (path: string) =
    let project = (projectOf path).Value
    let key = AnalysisRecords.keyWith analyzer identities[path] path project
    test <@ AnalysisRecords.file store root path key analysis @>

let private fileBoth (store: OutcomeStore.Store) =
    fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"
    fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "tests/TA/T.fs"

[<Fact>]
let ``every analysable manifest entry resolves to its record, and a file with no identity is not asked for`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        match BaselineRebuild.resolve store "/ws/a" projectOf (recordWith identities) with
        | Ok resolved -> test <@ resolved |> List.map _.Path = [ "src/A/A.fs"; "tests/TA/T.fs" ] @>
        | Error misses -> failwith $"expected every entry to resolve: %A{misses}")

[<Fact>]
let ``one missing record is no baseline, never a partial index`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectOf (recordWith identities) = Error
                    [ BaselineRebuild.Miss.NoRecord "tests/TA/T.fs" ]
            @>)

[<Fact>]
let ``a record whose identity matches but whose analyser build differs is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileAs store "/ws/a" AnalysisRecords.analyzerIdentity "src/A/A.fs"
        fileAs store "/ws/a" "another-analyser-build" "tests/TA/T.fs"

        test
            <@
                BaselineRebuild.resolve store "/ws/a" projectOf (recordWith identities) = Error
                    [ BaselineRebuild.Miss.NoRecord "tests/TA/T.fs" ]
            @>)

[<Fact>]
let ``a manifest file the reading checkout no longer has is no baseline`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        let gone (path: string) =
            if path = "src/A/A.fs" then None else projectOf path

        test
            <@
                BaselineRebuild.resolve store "/ws/a" gone (recordWith identities) = Error
                    [ BaselineRebuild.Miss.NoProject "src/A/A.fs" ]
            @>)

[<Fact>]
let ``records filed by checkout A resolve in checkout B at another path`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        fileBoth store

        test
            <@
                BaselineRebuild.resolve store "/ws/b" projectOf (recordWith identities)
                |> Result.isOk
            @>)

[<Fact>]
let ``a filed test-baseline record carries each file's analysis identity back to its reader`` () =
    withTempDir "rebuild" (fun dir ->
        let store = storeIn dir
        let tree = "sha256:tree-1", (recordWith identities).Entries

        let fullKeyOf (project: string) =
            TestEvidenceRun.testActionKey
                { Value = "kp-" + project
                  Labels = [ "closure", "c-" + project ] }
                "full"

        for project in [ "TA" ] do
            let record: OutcomeStore.Record =
                { Key = fullKeyOf project
                  Subject = project
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
                (fun () -> Map.ofList [ "TA", Ok (fullKeyOf "TA").Value.Text ])
                (fun () -> identities)
                producer
                facts
        with
        | Ok _ -> ()
        | Error refusals -> failwith $"refused: %A{refusals}"

        match TestBaseline.checkoutRecordFor store (fst tree) with
        | Ok record -> test <@ record.Identities = identities @>
        | Error why -> failwith $"expected a hit: %s{why}")
