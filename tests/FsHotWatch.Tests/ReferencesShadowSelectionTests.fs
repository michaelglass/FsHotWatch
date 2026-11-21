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

/// The premise held: sealed at the tree on disk, nothing pending (`IndexAtRun`).
let private atRun =
    match IndexAtRun.ofSeal (Some "tree") (Some "tree") 0 with
    | Ok atRun -> atRun
    | Error reason -> failwith reason

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
        let selections = selections atRun db dbPath copyDir changed [ reused ]

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
        let selections = selections atRun db dbPath copyDir changed [ reused; same ]
        test <@ selections.SPrime = selections.S @>
        test <@ selections.Copy = ImpactDbCopy.NotCopied @>
        test <@ selections.UnverifiableAnalyses = 1 && selections.DifferingAnalyses = 0 @>
        test <@ not (Directory.Exists copyDir) @>)

/// Substituting a stale analysis changes C as well as the edges: which of a file's symbols count
/// as changed depends on the analysis that was diffed. Here the stale analysis of `src/Lib.fs`
/// holds other content for both symbols and the same (absent) edges. This run counted `Lib.f`
/// changed and `Lib.g` not; under reuse a symbol whose content the two analyses disagree on counts
/// the other way, so C′ is `Lib.g` alone: S selects T, S′ selects U.
[<Fact>]
let ``a stale analysis changing which symbols count as changed changes S-prime`` () =
    withTempDir "refshadow-selection-c" (fun dir ->
        let root = Path.Combine(dir, "repo")
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        let libSource = "module Lib\nlet f () = 1\nlet g () = 2\n"
        File.WriteAllText(Path.Combine(root, "src", "Lib.fs"), libSource)
        let dbPath = Path.Combine(dir, "test-impact.db")
        let db = Database(dbPath)
        db.RebuildProjects([ libAnalysis; testsAnalysis true ], fileKeys = [ "src/Lib.fs", "fresh-identity" ])
        let store = OutcomeRecords.InMemoryOutcomeStore()

        let staleLib =
            AnalysisResult.Create(
                [ for name in [ "Lib.f"; "Lib.g" ] ->
                      { symbol "src/Lib.fs" name Function with
                          Location = InSource("src/Lib.fs", 1, 2, "stale-" + name) } ],
                [],
                []
            )

        AnalysisRecords.file
            store
            root
            "src/Lib.fs"
            (AnalysisRecords.keyOf (IndexedAnalysis.identity libSource "reused-fcs" "reused-used") "src/Lib.fs" "Lib")
            staleLib
        |> ignore

        let libServable =
            { servable with
                File = "src/Lib.fs"
                Project = "src/Lib.fsproj" }

        let reused = reusedAnalysisOf store root (db.GetFileKeys()) libServable

        test
            <@
                match reused with
                | ReusedAnalysis.Differs _ -> true
                | ReusedAnalysis.Same
                | ReusedAnalysis.Unverifiable -> false
            @>

        let copyDir = Path.Combine(dir, "copy")
        let selections = selections atRun db dbPath copyDir [ "Lib.f" ] [ reused ]

        test <@ selections.S = set [ "Tests.T" ] @>
        test <@ selections.SPrime = set [ "Tests.U" ] @>
        test <@ not (Directory.Exists copyDir) @>
        // The index the gate selects from still holds this run's analysis.
        test
            <@ db.GetSymbolsInFile "src/Lib.fs" |> List.map _.ContentHash |> List.sort = [ "hash-Lib.f"; "hash-Lib.g" ] @>)

/// ITEM 4, the claim in `changedUnder`'s comment, tested: C was derived against a THIRD version V3
/// of Lib (the index before this run), neither the stale analysis nor this run's. Per symbol:
/// X differs in all three; Y is stale = V3 ≠ current; Z is current = V3 ≠ stale; W is stale =
/// current ≠ V3. Each symbol has its own test. The exact S′ re-derives C as reuse would have:
/// the stale analysis diffed against V3, selected over an index holding the stale analysis. The
/// computed attributable set must be a SUPERSET of the exact one: with every test failing (every
/// test counted, the most demanding rows), and so for any rows.
[<Fact>]
let ``with a third version in the index the attributable set is a superset of the exact one`` () =
    withTempDir "refshadow-selection-v3" (fun dir ->
        let root = Path.Combine(dir, "repo")
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore

        let libSource =
            "module Lib\nlet x () = 1\nlet y () = 2\nlet z () = 3\nlet w () = 4\n"

        File.WriteAllText(Path.Combine(root, "src", "Lib.fs"), libSource)

        let lib (hashes: (string * string) list) =
            AnalysisResult.Create(
                [ for name, hash in hashes ->
                      { symbol "src/Lib.fs" name Function with
                          Location = InSource("src/Lib.fs", 1, 2, hash) } ],
                [],
                []
            )

        let v3 = lib [ "Lib.x", "v3-x"; "Lib.y", "s-y"; "Lib.z", "c-z"; "Lib.w", "v3-w" ]

        let current =
            lib [ "Lib.x", "c-x"; "Lib.y", "c-y"; "Lib.z", "c-z"; "Lib.w", "sc-w" ]

        let stale = lib [ "Lib.x", "s-x"; "Lib.y", "s-y"; "Lib.z", "s-z"; "Lib.w", "sc-w" ]

        let tests =
            AnalysisResult.Create(
                [ for name in [ "TX"; "TY"; "TZ"; "TW" ] -> symbol "tests/Tests.fs" ("Tests." + name) Function ],
                [ for name in [ "x"; "y"; "z"; "w" ] -> edge ("Tests.T" + name.ToUpperInvariant()) ("Lib." + name) ],
                [ for name in [ "TX"; "TY"; "TZ"; "TW" ] -> testOf name ]
            )

        let changesAgainst (db: Database) (analysis: AnalysisResult) =
            TestPrune.SymbolDiff.detectChanges analysis.Symbols (db.GetSymbolsInFile "src/Lib.fs")
            |> fst
            |> TestPrune.SymbolDiff.changedSymbolNames
            |> List.sort

        // The index before this run holds V3; this run's C is its analysis diffed against V3, and
        // the run then indexes its own analysis, as the nightly's index holds it when S′ is asked.
        let dbPath = Path.Combine(dir, "test-impact.db")
        let db = Database(dbPath)
        db.RebuildProjects([ v3; tests ], fileKeys = [ "src/Lib.fs", "v3-identity" ])
        let changed = changesAgainst db current
        test <@ changed = [ "Lib.w"; "Lib.x"; "Lib.y" ] @>

        // What reuse would have derived: the stale analysis diffed against the same V3.
        let changedExact = changesAgainst db stale
        test <@ changedExact = [ "Lib.w"; "Lib.x"; "Lib.z" ] @>

        db.RebuildProjects([ current; tests ], fileKeys = [ "src/Lib.fs", "fresh-identity" ])

        // The exact S′: C as reuse would have derived it, over an index holding the stale analysis.
        let exactDb = Database(Path.Combine(dir, "exact-impact.db"))
        exactDb.RebuildProjects([ stale; tests ])

        let sPrimeExact =
            (exactDb.QueryAffectedSelection changedExact).Tests
            |> List.map testName
            |> Set.ofList

        let store = OutcomeRecords.InMemoryOutcomeStore()

        AnalysisRecords.file
            store
            root
            "src/Lib.fs"
            (AnalysisRecords.keyOf (IndexedAnalysis.identity libSource "reused-fcs" "reused-used") "src/Lib.fs" "Lib")
            stale
        |> ignore

        let reused =
            reusedAnalysisOf
                store
                root
                (db.GetFileKeys())
                { servable with
                    File = "src/Lib.fs"
                    Project = "src/Lib.fsproj" }

        let computed =
            selections atRun db dbPath (Path.Combine(dir, "copy")) changed [ reused ]

        let exact = { computed with SPrime = sPrimeExact }

        let everyTestFails =
            [ for name in [ "TX"; "TY"; "TZ"; "TW" ] -> row ("Tests." + name) TestOutcome.Passed TestOutcome.Failed ]

        let attributable (selections: Selections) =
            (compareTests selections everyTestFails).Attributable
            |> List.map _.Test
            |> Set.ofList

        test <@ computed.S = set [ "Tests.TW"; "Tests.TX"; "Tests.TY" ] @>
        test <@ sPrimeExact = set [ "Tests.TW"; "Tests.TX"; "Tests.TZ" ] @>
        // The computed S′ never selects what the exact one does not ...
        test <@ Set.isSubset computed.SPrime sPrimeExact @>
        // ... so its attributable set holds every exactly attributable test.
        test <@ Set.isSubset (attributable exact) (attributable computed) @>
        test <@ attributable exact = set [ "Tests.TY" ] @>
        // The one inexact symbol is X, the one V3 differs on from both: its test is over-attributed.
        test <@ attributable computed = set [ "Tests.TX"; "Tests.TY" ] @>)

/// The analysis this run filed may sit under another identity and still be the reused one: the
/// same record is the same analysis. A file this run never indexed differs whatever it holds.
[<Fact>]
let ``the same record under another identity is the same analysis, and an unindexed file differs`` () =
    withTempDir "refshadow-selection-identity" (fun dir ->
        let root, db, _, store = fixture dir (Some(testsAnalysis true))

        // This run's identity files the very analysis reuse would have kept.
        AnalysisRecords.file
            store
            root
            "tests/Tests.fs"
            (AnalysisRecords.keyOf "fresh-identity" "tests/Tests.fs" "Tests")
            (testsAnalysis true)
        |> ignore

        test <@ reusedAnalysisOf store root (db.GetFileKeys()) servable = ReusedAnalysis.Same @>

        test
            <@
                match reusedAnalysisOf store root Map.empty servable with
                | ReusedAnalysis.Differs(file, _) -> file = "tests/Tests.fs"
                | ReusedAnalysis.Same
                | ReusedAnalysis.Unverifiable -> false
            @>)

/// Nothing changed since the watermark selects nothing, under either analysis.
[<Fact>]
let ``an empty C selects nothing in S or S-prime`` () =
    withTempDir "refshadow-selection-empty" (fun dir ->
        let root, db, dbPath, store = fixture dir (Some(testsAnalysis false))
        let reused = reusedAnalysisOf store root (db.GetFileKeys()) servable

        let selections =
            selections atRun db dbPath (Path.Combine(dir, "copy")) [] [ reused ]

        test <@ selections.S.IsEmpty && selections.SPrime.IsEmpty @>
        test <@ selections.DifferingAnalyses = 1 @>)

/// TEST 7, test-prune's half (gates the land): computing S′ changes nothing the gate selects
/// from. The index answers every selection, file key, symbol and edge exactly as before.
[<Fact>]
let ``computing S-prime leaves the index the gate selects from exactly as it was`` () =
    withTempDir "refshadow-gate-index" (fun dir ->
        let root, db, dbPath, store = fixture dir (Some(testsAnalysis false))

        let gate () =
            [ [ "Lib.f" ]; [ "Lib.g" ]; changed ]
            |> List.map (fun seeds -> (db.QueryAffectedSelection seeds).Tests |> List.map testName |> List.sort),
            db.GetFileKeys(),
            db.GetAllSymbolNames(),
            db.GetDependenciesFromFile "tests/Tests.fs"
            |> List.sortBy (fun d -> d.FromSymbol, d.ToSymbol)

        let before = gate ()
        let reused = reusedAnalysisOf store root (db.GetFileKeys()) servable

        let shadow =
            selections atRun db dbPath (Path.Combine(dir, "copy")) changed [ reused ]
        // The shadow did substitute: S′ lost T.
        test <@ shadow.SPrime <> shadow.S @>
        test <@ gate () = before @>)

/// The plugin answers the nightly's request through its own command: C selected over its index.
/// A servable file gone from disk since the scan named it is unverifiable, not a failure of the
/// request (it threw, and the report for the whole run was lost).
///
/// S′ stands on the index holding this run's analysis (`IndexAtRun`): asked before any cohort has
/// sealed the index, or after the tree moved past the seal, the plugin refuses and says why.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the test-prune plugin answers the nightly's request through its command, only over a sealed index`` () =
    withTempDir "refshadow-selection-command" (fun dir ->
        let dbPath = Path.Combine(dir, "tp.db")
        let database = Database.create dbPath
        // Under a discovery root, so it is an entry of the tree the seal names.
        let sourceDir = Directory.CreateDirectory(Path.Combine(dir, "src")).FullName
        File.WriteAllText(Path.Combine(sourceDir, "Lib.fs"), "module Lib\nlet foo () = 1\n")

        FsHotWatch.Tests.TestPrunePluginTestSupport.PendingQueueHelpers.seedCoveredSymbol
            database
            "Lib.foo"
            "Lib.fs"
            "P1"
            "P1Tests"
            "fooTest"

        let host = createModelHost (Unchecked.defaultof<_>) dir

        host.RegisterHandler(FsHotWatch.TestPrune.TestPrunePlugin.create dbPath dir (Some []) None None None None [])

        try
            let request: TestPruneRequest =
                { RunId = "0123456789abcdef0123456789abcdef"
                  Changed = [ "Lib.foo" ]
                  Files = [ servable ] }

            let ask () =
                host.RunCommand(TestPruneCommand, [| TestPruneRequest.toJson request |])
                |> Async.RunSynchronously
                |> Option.bind SelectionsReply.tryOfJson

            let refusedBecause (needle: string) =
                match ask () with
                | Some(SelectionsAnswer.Refused reason) -> reason.Contains needle
                | other -> failwith $"expected a refusal naming `%s{needle}`, got %A{other}"

            // No cohort has sealed the index: S′ would diff against rows no run vouches for.
            test <@ refusedBecause "not sealed" @>

            FsHotWatch.Tests.TestPrunePluginTestSupport.emitBatchAndQuiesce host []

            match ask () with
            | Some(SelectionsAnswer.Selected selections) ->
                test <@ selections.S = set [ "P1Tests.fooTest" ] && selections.SPrime = selections.S @>
                test <@ selections.UnverifiableAnalyses = 1 && selections.DifferingAnalyses = 0 @>
                test <@ selections.Copy = ImpactDbCopy.NotCopied @>
            | other -> failwith $"a sealed index at the tree on disk answers selections, got %A{other}"

            // The tree moves past the seal: the index no longer describes the tree the run saw.
            File.WriteAllText(Path.Combine(sourceDir, "Lib.fs"), "module Lib\nlet foo () = 2\n")
            test <@ refusedBecause "sealed at tree" @>
        finally
            host.Teardown())

/// Each part of S′'s premise, refused with its own reason; only a seal at the tree on disk with
/// nothing pending a flush is a witness.
[<Fact>]
let ``the premise of S-prime is a seal at the tree on disk with nothing pending`` () =
    let reasonOf (result: Result<IndexAtRun, string>) : string =
        match result with
        | Error reason -> reason
        | Ok atRun -> failwith $"expected a refusal, got a witness at %s{atRun.Tree}"

    test <@ (reasonOf (IndexAtRun.ofSeal None (Some "t1") 0)).Contains "not sealed" @>
    test <@ (reasonOf (IndexAtRun.ofSeal (Some "t1") None 0)).Contains "could not be read" @>

    test
        <@ (reasonOf (IndexAtRun.ofSeal (Some "t1") (Some "t2") 0)).Contains "sealed at tree t1, but the tree is now t2" @>

    test <@ (reasonOf (IndexAtRun.ofSeal (Some "t1") (Some "t1") 3)).Contains "3 analyses are pending" @>

    match IndexAtRun.ofSeal (Some "t1") (Some "t1") 0 with
    | Ok atRun -> test <@ atRun.Tree = "t1" @>
    | Error reason -> failwith reason
