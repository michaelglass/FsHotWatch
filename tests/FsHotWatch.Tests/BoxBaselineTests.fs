/// AUTOMATION-1793 step 2 (intelligence ADR 0651, revised): a checkout with no full pass of
/// its own (a land checks out a new merge every time) restarts from the box's retained green
/// baseline. Among the newest `TestBaseline.RetainedTrees` baselines it takes the tree T whose
/// manifest differs least from its working files, rebuilds its impact index at T from the
/// box-wide analysis records T's manifest pins, seals the index at T, and diffs T to T' as a
/// daemon that had passed T itself would.
///
/// Soundness: the selection from the rebuilt index equals the selection from an index built
/// live at T, for the same edit. Any miss is no baseline, and the index seal is left alone.
module FsHotWatch.Tests.BoxBaselineTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = dir
          Ttl = TimeSpan.FromDays 14.0
          MaxBytes = 1_000_000_000L
          Log = ignore }
    )

// ---------------------------------------------------------------------------
// Choosing T: the retained baseline that differs least from the working files.
// ---------------------------------------------------------------------------

/// A record for `entries`, with an analysis pinned for each path in `analysed`.
let private recordOf (entries: (string * string) list) (analysed: string list) : TestBaseline.CheckoutRecord =
    let tree = TreeHash.hashEntries entries

    { Tree = tree
      Key = (TestBaseline.keyOf tree).Value.Text
      RunId = "run-" + tree
      Entries = entries
      Analyses =
        analysed
        |> List.map (fun path -> path, TestBaseline.RowAnalysis.Pinned("id-" + path, "P", "key-" + path))
        |> Map.ofList }

let private treeOf (entries: (string * string) list) : ReceiptTree =
    { Identity = TreeHash.hashEntries entries
      Entries = entries }

let private sources = [ "src/A.fs"; "src/B.fs"; "src/C.fs" ]

let private current =
    [ "src/A.fs", "a2"; "src/A.fsproj", "ap1"; "src/B.fs", "b2"; "src/C.fs", "c2" ]

[<Fact>]
let ``the retained tree whose manifest differs least from the working files is chosen`` () =
    // Newest first, as the index lists them: 3, 1 and 2 files differ.
    let newest =
        recordOf [ "src/A.fs", "a1"; "src/A.fsproj", "ap1"; "src/B.fs", "b1"; "src/C.fs", "c1" ] sources

    let middle =
        recordOf [ "src/A.fs", "a2"; "src/A.fsproj", "ap1"; "src/B.fs", "b1"; "src/C.fs", "c2" ] sources

    let oldest =
        recordOf [ "src/A.fs", "a1"; "src/A.fsproj", "ap1"; "src/B.fs", "b2"; "src/C.fs", "c2" ] sources

    match BoxBaseline.choose [ newest; middle; oldest ] (treeOf current) with
    | Some(record, distance) ->
        test <@ record.Tree = middle.Tree @>
        test <@ distance = { Differing = 1; NonSource = 0 } @>
    | None -> failwith "expected a choice"

[<Fact>]
let ``a file only one side holds counts as a difference, from either side`` () =
    // The manifest holds a file the working tree deleted, and lacks one it added.
    let record =
        recordOf
            [ "src/A.fs", "a2"
              "src/A.fsproj", "ap1"
              "src/B.fs", "b2"
              "src/Gone.fs", "g" ]
            ("src/Gone.fs" :: sources)

    let withNew = current @ [ "src/New.fs", "n" ]

    test <@ BoxBaseline.distance record withNew = { Differing = 3; NonSource = 0 } @>

[<Fact>]
let ``of two retained trees equally far from the working files, the newest is chosen`` () =
    let newest =
        recordOf [ "src/A.fs", "a1"; "src/A.fsproj", "ap1"; "src/B.fs", "b2"; "src/C.fs", "c2" ] sources

    let older =
        recordOf [ "src/A.fs", "a2"; "src/A.fsproj", "ap1"; "src/B.fs", "b1"; "src/C.fs", "c2" ] sources

    test
        <@
            BoxBaseline.choose [ newest; older ] (treeOf current)
            |> Option.map (fst >> _.Tree) = Some newest.Tree
        @>

    test
        <@
            BoxBaseline.choose [ older; newest ] (treeOf current)
            |> Option.map (fst >> _.Tree) = Some older.Tree
        @>

[<Fact>]
let ``a tree that differs only in source files beats one that matches every source file but differs in a project file``
    ()
    =
    // The rule: fewer non-source differences first, then fewer differences, then the newest.
    // A project file, props, targets or config routes a project to a whole run, so a tree that
    // looks close by sources but differs there is not close.
    let projectFileMoved =
        recordOf [ "src/A.fs", "a2"; "src/A.fsproj", "ap0"; "src/B.fs", "b2"; "src/C.fs", "c2" ] sources

    let twoSourcesMoved =
        recordOf [ "src/A.fs", "a1"; "src/A.fsproj", "ap1"; "src/B.fs", "b1"; "src/C.fs", "c2" ] sources

    test <@ BoxBaseline.distance projectFileMoved current = { Differing = 1; NonSource = 1 } @>
    test <@ BoxBaseline.distance twoSourcesMoved current = { Differing = 2; NonSource = 0 } @>

    test
        <@
            BoxBaseline.choose [ projectFileMoved; twoSourcesMoved ] (treeOf current)
            |> Option.map (fst >> _.Tree) = Some twoSourcesMoved.Tree
        @>

[<Fact>]
let ``a working file the manifest lacks is a source difference only when it is a source file`` () =
    let record = recordOf current sources
    let added = [ "src/New.fs", "n"; "src/New.fsi", "s"; "build.props", "p" ]
    let expected: BoxDistance = { Differing = 3; NonSource = 1 }

    test <@ BoxBaseline.distance record (current @ added) = expected @>

[<Fact>]
let ``no retained tree is no choice`` () =
    test <@ BoxBaseline.choose [] (treeOf current) = None @>

// ---------------------------------------------------------------------------
// The box: two checkouts, one store. "live" passed T and filed its analyses and its
// baseline; "land" checks out T' with an index from an older tree T0.
// ---------------------------------------------------------------------------

let private sym (root: string) (file: string) (name: string) (hash: string) : SymbolInfo =
    { FullName = name
      Kind = SymbolKind.Value
      Location = InSource(Path.Combine(root, file), 1, 1, hash) }

let private calls (from: string) (target: string) : Dependency =
    { FromSymbol = from
      ToSymbol = target
      Kind = DependencyKind.Calls
      Source = CoreSource }

let private testMethod (name: string) : TestMethodInfo =
    { SymbolFullName = "T.Tests." + name
      TestProject = "unset"
      TestClass = "T.Tests"
      TestMethod = name }

[<Literal>]
let private LibPath = "src/Lib/Lib.fs"

[<Literal>]
let private TestsPath = "tests/T/Tests.fs"

/// `Lib.fs` as an FCS check under `root` analyses it: `Lib.f` with body `fHash`, and `Lib.g`.
let private libAt (root: string) (fHash: string) =
    AnalysisResult.Create([ sym root LibPath "Lib.f" fHash; sym root LibPath "Lib.g" "g1" ], [], [])

/// `Tests.fs` under `root`: `testF` calls `fCallee`, `testG` calls `Lib.g`.
let private testsAt (root: string) (fCallee: string) =
    AnalysisResult.Create(
        [ sym root TestsPath "T.Tests.testF" "tf"
          sym root TestsPath "T.Tests.testG" "tg" ],
        [ calls "T.Tests.testF" fCallee; calls "T.Tests.testG" "Lib.g" ],
        [ testMethod "testF"; testMethod "testG" ]
    )

let private projectOf (path: string) =
    match path with
    | LibPath -> "Lib"
    | _ -> "T"

let private projectsOf (path: string) =
    match path with
    | LibPath
    | TestsPath -> [ projectOf path ]
    | _ -> []

let private identityOf (path: string) = "id:" + path

/// T's working files: the two sources and a project file the index never analyses.
let private entriesAt (libHash: string) =
    [ "src/Lib/Lib.fsproj", "sha256:proj"
      LibPath, libHash
      TestsPath, "sha256:tests" ]

let private treeT = treeOf (entriesAt "sha256:lib-T")

/// T' after a covered body edit of `Lib.f`.
let private treeT' = treeOf (entriesAt "sha256:lib-T'")

let private atT (root: string) =
    [ LibPath, libAt root "f-T"; TestsPath, testsAt root "Lib.f" ]

/// `Lib.f`'s body at T', as the cold scan's check under `root` analyses it.
let private libAtT' (root: string) = libAt root "f-T'"

/// The land's index before the rebuild: an older tree T0 in which `testF` called `Lib.g`.
let private atT0 (root: string) =
    [ LibPath, libAt root "f-T0"; TestsPath, testsAt root "Lib.g" ]

/// Row cells of a manifest entry: pinned for an analysed path, null otherwise.
type private Row =
    | Pinned of path: string * hash: string
    | Unpinned of path: string * hash: string
    | Unanalysed of path: string * hash: string

let private rowOf (entries: (string * string) list) (unpinned: Set<string>) =
    entries
    |> List.map (fun (path, hash) ->
        match projectsOf path with
        | [] -> Unanalysed(path, hash)
        | _ when unpinned.Contains path -> Unpinned(path, hash)
        | _ -> Pinned(path, hash))

/// File T's `test-baseline` record, as a green full run in "live" files it, and name it at
/// the front of the index.
let private fileBaselineWith
    (store: OutcomeStore.Store)
    (tree: ReceiptTree)
    (unpinned: Set<string>)
    (at: DateTimeOffset)
    =
    let key = TestBaseline.keyOf tree.Identity
    let o = JsonObject()
    o["schema"] <- TestBaseline.Schema
    o["runId"] <- "run-" + tree.Identity
    o["tree"] <- tree.Identity
    o["analyzer"] <- AnalysisRecords.analyzerIdentity
    o["projects"] <- JsonObject()
    let manifest = JsonArray()
    let text (value: string) = JsonValue.Create value :> JsonNode

    for row in rowOf tree.Entries unpinned do
        match row with
        | Pinned(path, hash) ->
            let project = projectOf path
            let pinned = (AnalysisRecords.keyOf (identityOf path) path project).Value.Text
            manifest.Add(JsonArray(text path, text hash, text (identityOf path), text project, text pinned))
        | Unpinned(path, hash) -> manifest.Add(JsonArray(text path, text hash, text (identityOf path)))
        | Unanalysed(path, hash) -> manifest.Add(JsonArray(text path, text hash, null, null, null))

    o["manifest"] <- manifest

    let producer: TestBaseline.Producer =
        { Binary = "fshw-test"
          Worktree = "/ws/live"
          Now = at }

    let filed =
        store.Supersede(
            OutcomeStore.Record.ofAction key tree.Identity OutcomeStore.Outcome.Pass "fshw-test" "/ws/live" at,
            Some(o.ToJsonString())
        )

    test <@ filed.IsSome @>

    let indexed =
        TestBaseline.appendIndex
            store
            producer
            { Tree = tree.Identity
              Baseline = key.Value.Text
              RunId = "run-" + tree.Identity
              At = at }

    test <@ indexed.IsSome @>

let private fileBaseline (store: OutcomeStore.Store) (tree: ReceiptTree) =
    fileBaselineWith store tree Set.empty (DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero))

/// File every analysis "live" checked at T, under its own root.
let private fileAnalyses (store: OutcomeStore.Store) (liveRoot: string) =
    for path, analysis in atT liveRoot do
        let key = AnalysisRecords.keyOf (identityOf path) path (projectOf path)
        test <@ AnalysisRecords.file store liveRoot path key analysis @>

let private noDeclaration (_: string) =
    { new IDisposable with
        member _.Dispose() = () }

/// An index at `dbPath`, written through the plugin's own flush exactly as the cold scan
/// writes the analyses `at` names: each through the fold's transform, then the flush.
let private liveIndex (dir: string) (root: string) (at: (string * AnalysisResult) list) =
    let dbPath = Path.Combine(dir, "live.db")
    let db = Database.create dbPath
    let init = (TestPrunePlugin.create dbPath root None None None None None []).Init

    let pending =
        at
        |> List.groupBy (fst >> projectOf)
        |> List.map (fun (project, files) ->
            project,
            files
            |> List.map (fun (_, analysis) -> FileCheckedHandler.indexedAnalysisOf root project analysis))
        |> Map.ofList

    let state =
        { init with
            PendingAnalysis = pending
            PendingIdentities = at |> List.map (fun (path, _) -> path, identityOf path) |> Map.ofList }

    IndexMaintenance.flushPendingAnalysis db dbPath (IndexMaintenance.dbBusyTimeout db) noDeclaration state
    |> ignore

    db, dbPath

/// What a cold scan selects for the T' check of `Lib.fs` under `root`, against `db`'s rows:
/// the fold's diff, then the impact query.
let private selectionFor (db: Database) (root: string) =
    let current =
        (FileCheckedHandler.indexedAnalysisOf root "Lib" (libAtT' root)).Symbols

    let changes, _ = detectChanges current (db.GetSymbolsInFile LibPath)

    db.QueryAffectedTests(changedSymbolNames changes)
    |> List.map (fun t -> t.TestProject, t.TestClass, t.TestMethod)
    |> Set.ofList

/// The land checkout: an index from T0, sealed at T0.
type private Land =
    { Dir: string
      Root: string
      Db: Database
      DbPath: string
      Store: OutcomeStore.Store }

let private landing (dir: string) =
    let store = storeIn (Path.Combine(dir, "outcomes"))
    let root = Path.Combine(dir, "land")
    Directory.CreateDirectory root |> ignore
    let db, dbPath = liveIndex root root (atT0 root)
    IndexSeal.save ignore dbPath (Some "sha256:tree-T0")

    { Dir = dir
      Root = root
      Db = db
      DbPath = dbPath
      Store = store }

let private envOf (l: Land) (log: string -> unit) : BoxBaseline.RebuildEnv =
    { Store = l.Store
      Root = l.Root
      ProjectsOf = projectsOf
      DbPath = l.DbPath
      Write =
        fun results keys ->
            IndexMaintenance.writeAnalyses
                l.Db
                l.DbPath
                (IndexMaintenance.dbBusyTimeout l.Db)
                noDeclaration
                results
                keys
      Log = log }

/// The box after "live" passed T: its analyses and its baseline filed.
let private boxAfterLivePassedT (l: Land) =
    fileAnalyses l.Store "/ws/live"
    fileBaseline l.Store treeT

[<Fact>]
let ``a retained T whose analyses all resolve is rebuilt into the index, sealed, and is the restart baseline`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        match BoxBaseline.rebuild (envOf l ignore) treeT' with
        | Ok rebuilt ->
            test <@ rebuilt.Baseline.Tree = treeT.Identity @>
            test <@ rebuilt.Baseline.Key = (TestBaseline.keyOf treeT.Identity).Value.Text @>
            test <@ rebuilt.Baseline.RunId = "run-" + treeT.Identity @>
            test <@ rebuilt.Baseline.Manifest = treeT @>
            test <@ rebuilt.Distance = { Differing = 1; NonSource = 0 } @>
            test <@ IndexSeal.load l.DbPath = Some treeT.Identity @>
            // The index's file_keys are the identities T's manifest names.
            test <@ l.Db.GetFileKeys() = Map.ofList [ LibPath, identityOf LibPath; TestsPath, identityOf TestsPath ] @>
        | Error why -> failwith $"expected a rebuild: %s{why}")

[<Fact>]
let ``SOUNDNESS: the rebuilt index selects for T to T' exactly what an index built live at T selects`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        let liveRoot = Path.Combine(dir, "live")
        Directory.CreateDirectory liveRoot |> ignore
        let live, _ = liveIndex liveRoot liveRoot (atT liveRoot)
        let fromLive = selectionFor live liveRoot

        // Positive controls: the edit selects testF at T, and the land's index from T0, never
        // rebuilt, selects nothing for it (T0's testF called Lib.g): the rebuild is what
        // makes the land's selection right.
        test <@ fromLive = Set.singleton ("T", "T.Tests", "testF") @>
        test <@ selectionFor l.Db l.Root = Set.empty @>

        match BoxBaseline.rebuild (envOf l ignore) treeT' with
        | Ok _ -> test <@ selectionFor l.Db l.Root = fromLive @>
        | Error why -> failwith $"expected a rebuild: %s{why}"

        // The whole index agrees, not only this edit's answer.
        let symbolsOf (db: Database) =
            [ LibPath; TestsPath ]
            |> List.map (fun path -> path, db.GetSymbolsInFile path |> List.sortBy _.FullName)

        test <@ symbolsOf l.Db = symbolsOf live @>
        test <@ l.Db.GetDependenciesFromFile TestsPath = live.GetDependenciesFromFile TestsPath @>
        test <@ l.Db.GetTestMethodsInFile TestsPath = live.GetTestMethodsInFile TestsPath @>)

/// The land after a rebuild attempt that must change nothing: no seal at T, the seal at T0
/// kept, and the T0 rows kept.
let private unchanged (l: Land) =
    test <@ IndexSeal.load l.DbPath = Some "sha256:tree-T0" @>
    test <@ selectionFor l.Db l.Root = Set.empty @>

let private refusedWith (expected: string list) (outcome: Result<BoxRebuilt, string>) =
    match outcome with
    | Ok _ -> failwith "expected no baseline"
    | Error why ->
        for part in expected do
            test <@ why.Contains part @>

[<Fact>]
let ``a missing analysis record is no baseline, logged once naming T and the miss counts, and the seal is unchanged``
    ()
    =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l
        let evicted = AnalysisRecords.keyOf (identityOf TestsPath) TestsPath "T"
        Directory.Delete(l.Store.DirectoryOf evicted, true)
        let logged = ResizeArray<string>()

        BoxBaseline.rebuild (envOf l logged.Add) treeT'
        |> refusedWith [ treeT.Identity; "no-record=1"; "no-project=0" ]

        test <@ logged.Count = 1 @>
        test <@ logged[0].Contains treeT.Identity && logged[0].Contains "no-record=1" @>
        unchanged l)

[<Fact>]
let ``an unpinned manifest row is no baseline and the seal is unchanged`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        fileAnalyses l.Store "/ws/live"

        fileBaselineWith l.Store treeT (Set.singleton LibPath) (DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero))

        BoxBaseline.rebuild (envOf l ignore) treeT' |> refusedWith [ "no-record=1" ]
        unchanged l)

[<Fact>]
let ``a recorded project this checkout no longer compiles the file in is no baseline and the seal is unchanged`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        let env =
            { envOf l ignore with
                ProjectsOf = fun path -> if path = LibPath then [] else projectsOf path }

        BoxBaseline.rebuild env treeT' |> refusedWith [ "no-project=1"; "no-record=0" ]
        unchanged l)

[<Fact>]
let ``no retained baseline is no baseline and the seal is unchanged`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        fileAnalyses l.Store "/ws/live"

        BoxBaseline.rebuild (envOf l ignore) treeT'
        |> refusedWith [ "no retained test-baseline" ]

        unchanged l)

[<Fact>]
let ``a retained record whose manifest does not hash to its tree is not a candidate`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        fileAnalyses l.Store "/ws/live"
        // Filed under T's key, but naming T' bytes.
        fileBaseline l.Store { treeT with Entries = treeT'.Entries }

        test <@ List.isEmpty (BoxBaseline.retained l.Store) @>

        BoxBaseline.rebuild (envOf l ignore) treeT'
        |> refusedWith [ "no retained test-baseline" ]

        unchanged l)

[<Fact>]
let ``only the newest RetainedTrees baselines are candidates`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir

        let trees =
            [ 1..4 ]
            |> List.map (fun n -> treeOf (("extra.txt", string n) :: entriesAt "sha256:lib-T"))

        trees
        |> List.iteri (fun n tree ->
            fileBaselineWith l.Store tree Set.empty (DateTimeOffset(2026, 11, 24, 12, n, 0, TimeSpan.Zero)))

        // Newest first: the last filed leads, the first filed is the fourth and is not kept.
        let expected =
            trees
            |> List.rev
            |> List.truncate TestBaseline.RetainedTrees
            |> List.map _.Identity

        test <@ BoxBaseline.retained l.Store |> List.map _.Tree = expected @>)

[<Fact>]
let ``a retained T that IS the working tree is no baseline: the skip on records answers it`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        BoxBaseline.rebuild (envOf l ignore) treeT |> refusedWith [ "has not moved" ]
        unchanged l)

[<Fact>]
let ``a failure while rebuilding leaves the index not sealed at T`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        let env =
            { envOf l ignore with
                Write = fun _ _ -> raise (IOException "disk full") }

        BoxBaseline.rebuild env treeT' |> refusedWith [ "disk full"; treeT.Identity ]
        test <@ IndexSeal.load l.DbPath <> Some treeT.Identity @>
        unchanged l)

[<Fact>]
let ``a seal that cannot be written is no baseline, and the index is not sealed at T`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l
        // The seal's path is a directory: the seal cannot be written there.
        File.Delete(IndexSeal.pathOf l.DbPath)
        Directory.CreateDirectory(IndexSeal.pathOf l.DbPath) |> ignore

        BoxBaseline.rebuild (envOf l ignore) treeT' |> refusedWith [ "seal" ]
        test <@ IndexSeal.load l.DbPath <> Some treeT.Identity @>)

[<Fact>]
let ``an index entry whose record no longer reads is not a candidate`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l
        test <@ BoxBaseline.retained l.Store |> List.map _.Tree = [ treeT.Identity ] @>
        Directory.Delete(l.Store.DirectoryOf(TestBaseline.keyOf treeT.Identity), true)

        test <@ List.isEmpty (BoxBaseline.retained l.Store) @>

        BoxBaseline.rebuild (envOf l ignore) treeT'
        |> refusedWith [ "no retained test-baseline" ]

        unchanged l)
