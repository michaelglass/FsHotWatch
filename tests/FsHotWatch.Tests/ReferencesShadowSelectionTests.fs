/// AUTOMATION-1816 step 2b, design test 4 on a small real impact DB: a reused analysis of the
/// test file that drops one edge puts T in S but not S′, computed on a temporary copy of the
/// index that is deleted afterwards; the index itself is never written.
module FsHotWatch.Tests.ReferencesShadowSelectionTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.ReferencesShadowSelection
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers

let private symbol (file: string) (name: string) (kind: SymbolKind) : SymbolInfo =
    { FullName = name
      Kind = kind
      Location = InSource(file, 1, 2, "hash-" + name) }

let private edge (from: string) (target: string) : Dependency =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = "core" }

let private testOf (name: string) : TestMethodInfo =
    { SymbolFullName = "Tests." + name
      TestProject = "Tests"
      TestClass = "Tests"
      TestMethod = name }

let private libAnalysis =
    AnalysisResult.Create([ symbol "src/Lib.fs" "Lib.f" Function; symbol "src/Lib.fs" "Lib.g" Function ], [], [])

/// The test file's analysis: T calls `Lib.f` (unless the edge is dropped), U calls `Lib.g`.
let private testsAnalysis (withTEdge: bool) =
    AnalysisResult.Create(
        [ symbol "tests/Tests.fs" "Tests.T" Function
          symbol "tests/Tests.fs" "Tests.U" Function ],
        [ if withTEdge then
              edge "Tests.T" "Lib.f"
          edge "Tests.U" "Lib.g" ],
        [ testOf "T"; testOf "U" ]
    )

let private testsSource = "module Tests\nlet T () = Lib.f ()\nlet U () = Lib.g ()\n"

let private changed = [ "Lib.f"; "Lib.g" ]

/// The servable test file, as the scan would name it from its record's signatures.
let private servable: ServableFile =
    { File = "tests/Tests.fs"
      Project = "tests/Tests.fsproj"
      FcsSignature = "reused-fcs"
      UsedSignatures = UsedSignaturesKey.Described "reused-used" }

/// A repository at `dir` with its index of both files, the test file's source on disk, and a
/// store holding the reused analysis (`stale`) under the identity reuse would have read.
let private fixture (dir: string) (stale: AnalysisResult option) =
    let root = Path.Combine(dir, "repo")
    Directory.CreateDirectory(Path.Combine(root, "tests")) |> ignore
    File.WriteAllText(Path.Combine(root, "tests", "Tests.fs"), testsSource)
    let dbPath = Path.Combine(dir, "test-impact.db")
    let db = Database(dbPath)
    db.RebuildProjects([ libAnalysis; testsAnalysis true ], fileKeys = [ "tests/Tests.fs", "fresh-identity" ])
    let store = OutcomeRecords.InMemoryOutcomeStore()

    match stale with
    | Some analysis ->
        let identity = IndexedAnalysis.identity testsSource "reused-fcs" "reused-used"

        AnalysisRecords.file
            store
            root
            "tests/Tests.fs"
            (AnalysisRecords.keyOf identity "tests/Tests.fs" "Tests")
            analysis
        |> ignore
    | None -> ()

    root, db, dbPath, (store :> OutcomeRecords.IOutcomeStore)

let private row (test: string) (watermark: TestOutcome) (nightly: TestOutcome) : TestRow =
    { Test = test
      Watermark = watermark
      Nightly = nightly
      Quarantined = false
      Unstable = false }

[<Fact>]
let ``a reused analysis that drops an edge puts T in S but not S-prime, on a copy that is deleted`` () =
    withTempDir "refshadow-selection" (fun dir ->
        let root, db, dbPath, store = fixture dir (Some(testsAnalysis false))
        let reused = reusedAnalysisOf store root (db.GetFileKeys()) servable

        test
            <@
                match reused with
                | ReusedAnalysis.Differs _ -> true
                | ReusedAnalysis.Same
                | ReusedAnalysis.Unverifiable -> false
            @>

        let copyDir = Path.Combine(dir, "run-tmp", "copy")
        let selections = selections db dbPath copyDir changed [ reused ]

        test <@ selections.S = set [ "Tests.T"; "Tests.U" ] @>
        test <@ selections.SPrime = set [ "Tests.U" ] @>
        test <@ selections.DifferingAnalyses = 1 && selections.UnverifiableAnalyses = 0 @>

        match selections.Copy with
        | ImpactDbCopy.Copied(bytes, milliseconds) -> test <@ bytes > 0L && milliseconds >= 0L @>
        | ImpactDbCopy.NotCopied -> failwith "a differing analysis is selected on a copy"

        test <@ not (Directory.Exists copyDir) @>
        // The index the gate selects from still holds the edge.
        test <@ (db.QueryAffectedSelection [ "Lib.f" ]).Tests |> List.map testName = [ "Tests.T" ] @>

        // T fails in the nightly: attributable. Passing as it did: nothing. V, outside S: a miss.
        let failing =
            compareTests selections [ row "Tests.T" TestOutcome.Passed TestOutcome.Failed ]

        test <@ failing.Attributable |> List.map _.Test = [ "Tests.T" ] @>

        let passing =
            compareTests selections [ row "Tests.T" TestOutcome.Passed TestOutcome.Passed ]

        test <@ passing.Attributable.IsEmpty && passing.OutsideSPrimeFailedOrChanged = 0 @>

        let miss =
            compareTests selections [ row "Tests.V" TestOutcome.Passed TestOutcome.Failed ]

        test
            <@
                miss.SelectorMiss |> List.map _.Test = [ "Tests.V" ]
                && miss.Attributable.IsEmpty
            @>)

[<Fact>]
let ``no differing analysis copies nothing, and an unreadable one is unverifiable`` () =
    withTempDir "refshadow-selection-none" (fun dir ->
        let root, db, dbPath, store = fixture dir None
        let reused = reusedAnalysisOf store root (db.GetFileKeys()) servable
        test <@ reused = ReusedAnalysis.Unverifiable @>

        let undescribed =
            reusedAnalysisOf
                store
                root
                (db.GetFileKeys())
                { servable with
                    UsedSignatures = UsedSignaturesKey.Undescribed }

        test <@ undescribed = ReusedAnalysis.Unverifiable @>

        let same =
            reusedAnalysisOf
                store
                root
                (Map.ofList [ "tests/Tests.fs", IndexedAnalysis.identity testsSource "reused-fcs" "reused-used" ])
                servable

        test <@ same = ReusedAnalysis.Same @>

        let copyDir = Path.Combine(dir, "copy")
        let selections = selections db dbPath copyDir changed [ reused; same ]
        test <@ selections.SPrime = selections.S @>
        test <@ selections.Copy = ImpactDbCopy.NotCopied @>
        test <@ selections.UnverifiableAnalyses = 1 && selections.DifferingAnalyses = 0 @>
        test <@ not (Directory.Exists copyDir) @>)
