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
open FsHotWatch.Events
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff

/// A run id for `tree`, stable across a test: what a test run names itself by.
let private runOf (tree: string) =
    Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes tree)).ToString "N"

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
      RunId = runOf tree
      Entries = entries
      Analyses =
        analysed
        |> List.map (fun path -> path, TestBaseline.RowAnalysis.Pinned("id-" + path, "P", "key-" + path))
        |> Map.ofList
      FullKeys = Map.empty
      KnownInputs = Map.empty
      FiledAt = DateTimeOffset.MinValue
      CompileModel = Map.empty }

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
let private fileBaselineShaped
    (edit: JsonObject -> unit)
    (store: OutcomeStore.Store)
    (tree: ReceiptTree)
    (unpinned: Set<string>)
    (at: DateTimeOffset)
    =
    let key = TestBaseline.keyOf tree.Identity
    let o = JsonObject()
    o["schema"] <- TestBaseline.Schema
    o["runId"] <- runOf tree.Identity
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
    edit o

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
              RunId = runOf tree.Identity
              At = at }

    test <@ indexed.IsSome @>

let private fileBaselineWith = fileBaselineShaped ignore

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
      Judge = fun _ -> Map.empty
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
            test <@ rebuilt.Baseline.RunId = runOf treeT.Identity @>
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

// ---------------------------------------------------------------------------
// Step 2b: T's pass as this checkout's ledger, what it vouches for per project, and what a
// restart would do, re-checked at the CURRENT K_P.
// ---------------------------------------------------------------------------

let private at (minute: int) =
    DateTimeOffset(2026, 11, 24, 12, minute, 0, TimeSpan.Zero)

/// A project's K_P inputs: each label's entries, with `moved` labels given another digest.
let private inputsWith (moved: (string * string) list) : TestEvidenceKey.Inputs =
    let value (label: string) (digest: string) =
        moved
        |> List.tryFind (fst >> (=) label)
        |> Option.map snd
        |> Option.defaultValue digest

    { Toolchain = value "toolchain" "t1"
      TestConfig = value "testConfig" "c1"
      Options = [ "tests/P/P.fsproj", value "options" "o1" ]
      Packages = [ "tests/P/P.fsproj", value "packages" "p1" ]
      Closure = [ "tests/P/Tests.fs", value "closure" "cl1" ]
      Shared = [ "src/Lib.fs", value "shared" "s1" ]
      OtherProjects = [ "tests/Q/Q.fs", value "otherProjects" "op1" ]
      Declared = [ "data/reads.json", value "declared" "d1" ]
      EnvFiles = [ ".env.test", value "envFiles" "e1" ]
      EnvFilesLoaded = [ ".env.test", value "envFilesLoaded" "el1" ]
      Env = [ "VAR", value "env" "v1" ]
      Scope = value "scope" "sc1" }

/// The K_P of `inputsWith moved`.
let private kpWith (moved: (string * string) list) : TestEvidenceKey.Key =
    TestEvidenceKey.compute (inputsWith moved)

/// T's manifest: every path P's content labels are digested from at T, and more.
let private manifestAtT =
    (BoxBaseline.contentInputs (inputsWith []) |> List.map fst)
    @ [ "tests/P/P.fsproj"; "README.md" ]
    |> Set.ofList

/// What T's manifest covers: its paths, whatever their digest now.
let private coveredAtT (path: string) (_: string) = manifestAtT.Contains path

/// File a run's outcome for a project at K_P `kp` in `scope`.
let private fileOutcome
    (store: OutcomeStore.Store)
    (kp: TestEvidenceKey.Key)
    (scope: string)
    (outcome: OutcomeStore.Outcome)
    (recordedAt: DateTimeOffset)
    =
    let record: OutcomeStore.Record =
        { Key = TestEvidenceRun.testActionKey kp scope
          Subject = "P"
          Outcome = outcome
          Counts = None
          DurationMs = None
          Producer =
            { Binary = "fshw-test"
              Worktree = "/ws/live"
              RunId = Some "run-1" }
          RecordedAt = recordedAt }

    test <@ store.Record record @>

let private fullKeyText (kp: TestEvidenceKey.Key) =
    (TestEvidenceRun.testActionKey kp "full").Value.Text

let private compileInputs = Map.ofList [ "P", ("tests/P/P.fsproj", "identity:P") ]

/// `judgeProject` for P, its pass at T filed under `atT`, its current K_P inputs `now`.
let private judgedWith
    (store: OutcomeStore.Store)
    (atT: TestEvidenceKey.Key)
    (now: Result<TestEvidenceKey.Inputs, string>)
    =
    BoxBaseline.judgeProject
        store
        (Map.ofList [ "P", fullKeyText atT ])
        coveredAtT
        (Map.ofList [ "P", now ])
        compileInputs
        "P"

let private refusal (project: BoxProject) =
    match project with
    | BoxProject.Refused why -> why
    | BoxProject.Vouched _ -> failwith "expected a refusal"

[<Fact>]
let ``a project whose K_P is unchanged since T is vouched for, with its compile inputs now`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        test
            <@ judgedWith store (kpWith []) (Ok(inputsWith [])) = BoxProject.Vouched("tests/P/P.fsproj", "identity:P") @>)

[<Fact>]
let ``a project whose K_P moved only in the tree's file contents is vouched for: the T to T' diff answers those`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        let now = inputsWith [ "closure", "cl2"; "shared", "s2"; "otherProjects", "op2" ]

        test <@ judgedWith store (kpWith []) (Ok now) = BoxProject.Vouched("tests/P/P.fsproj", "identity:P") @>)

[<Theory>]
[<InlineData("toolchain")>]
[<InlineData("testConfig")>]
[<InlineData("options")>]
[<InlineData("packages")>]
[<InlineData("declared")>]
[<InlineData("envFiles")>]
[<InlineData("envFilesLoaded")>]
[<InlineData("env")>]
[<InlineData("scope")>]
let ``a project whose K_P moved outside the tree's file contents is refused, naming what moved`` (label: string) =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        let why =
            judgedWith store (kpWith []) (Ok(inputsWith [ label, "moved" ])) |> refusal

        test <@ why.Contains "moved beyond the tree's file contents" && why.Contains label @>)

[<Fact>]
let ``GUARD: a project whose content labels digest a file T's manifest does not name is refused, and runs whole`` () =
    // The content labels may differ between T and now only because the T to T' diff sees every
    // path they digest. A closure file outside T's manifest (here one T never had) is a path
    // that diff cannot see, so the box cannot vouch for the project.
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        let now =
            { inputsWith [] with
                Closure = [ "tests/P/Tests.fs", "cl1"; "tests/P/Unseen.fs", "u1" ] }

        test
            <@
                judgedWith store (kpWith []) (Ok now) = BoxProject.Refused
                    "its K_P covers inputs outside the baseline manifest: tests/P/Unseen.fs"
            @>)

[<Fact>]
let ``GUARD: a shared file, another project's file or an unreadable directory outside T's manifest refuses the project``
    ()
    =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        let outside (inputs: TestEvidenceKey.Inputs) =
            judgedWith store (kpWith []) (Ok inputs) |> refusal

        test
            <@
                (outside
                    { inputsWith [] with
                        Shared = [ "src/Lib.fs", "s1"; "tools/gen.sh", "g" ] })
                    .EndsWith
                    "tools/gen.sh"
            @>

        test
            <@
                (outside
                    { inputsWith [] with
                        OtherProjects = [ "tests/Q/Q.fs", "op1"; "tests/Q/New.fs", "n" ] })
                    .EndsWith
                    "tests/Q/New.fs"
            @>

        // A directory the K_P walk could not read is keyed as a hole, `dir/`: never in a manifest.
        test
            <@
                (outside
                    { inputsWith [] with
                        Closure = [ "tests/P/Tests.fs", "cl1"; "tests/P/locked/", "unhashable" ] })
                    .EndsWith
                    "tests/P/locked/"
            @>)

[<Fact>]
let ``GUARD: a known input the manifest never holds counts only while its digest is the one T recorded`` () =
    // `.config/dotnet-tools.json` is in every K_P's shared label and never in the verdict tree,
    // so never in a manifest: T's record keeps its digest, and the guard compares it.
    let tools = ".config/dotnet-tools.json"

    let record =
        { recordOf [ "src/A.fs", "a1" ] [ "src/A.fs" ] with
            KnownInputs = Map.ofList [ tools, "sha256:tools-at-T" ] }

    let covered = BoxBaseline.covers record
    test <@ covered "src/A.fs" "any digest now" @>
    test <@ covered tools "sha256:tools-at-T" @>
    test <@ not (covered tools "sha256:tools-moved") @>
    test <@ not (covered "src/Unseen.fs" "x") @>

    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        let withTools (digest: string) =
            { inputsWith [] with
                Shared = [ "src/Lib.fs", "s1"; tools, digest ] }

        let judged (digest: string) =
            BoxBaseline.judgeProject
                store
                (Map.ofList [ "P", fullKeyText (kpWith []) ])
                (fun path digestNow -> coveredAtT path digestNow || (path = tools && digestNow = "sha256:tools-at-T"))
                (Map.ofList [ "P", Ok(withTools digest) ])
                compileInputs
                "P"

        test <@ judged "sha256:tools-at-T" = BoxProject.Vouched("tests/P/P.fsproj", "identity:P") @>

        test
            <@
                judged "sha256:tools-moved" = BoxProject.Refused
                    "its K_P covers inputs outside the baseline manifest: .config/dotnet-tools.json"
            @>)

[<Fact>]
let ``a baseline record keeps the known inputs it was filed with`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir

        fileBaselineShaped
            (fun o ->
                let known = JsonObject()
                known[".config/dotnet-tools.json"] <- "sha256:tools"
                o["knownInputs"] <- known)
            l.Store
            treeT
            Set.empty
            (at 0)

        test
            <@
                TestBaseline.checkoutRecordFor l.Store treeT.Identity
                |> Result.map _.KnownInputs = Ok(Map.ofList [ ".config/dotnet-tools.json", "sha256:tools" ])
            @>)

[<Fact>]
let ``GUARD: a project every one of whose content inputs T's manifest names is vouched for`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        let now = inputsWith [ "closure", "cl9"; "shared", "s9" ]
        test <@ BoxBaseline.contentInputs now |> List.forall (fst >> manifestAtT.Contains) @>
        test <@ judgedWith store (kpWith []) (Ok now) = BoxProject.Vouched("tests/P/P.fsproj", "identity:P") @>)

[<Fact>]
let ``a newer full fail at the K_P T's baseline names refuses the project`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Fail (at 1)
        let why = judgedWith store (kpWith []) (Ok(inputsWith [])) |> refusal
        test <@ why.Contains "not a pass" @>)

[<Fact>]
let ``a newer impact fail at the K_P T's baseline names refuses the project, through the reuse path's refusal`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        fileOutcome store (kpWith []) "impact" OutcomeStore.Outcome.Fail (at 1)
        let why = judgedWith store (kpWith []) (Ok(inputsWith [])) |> refusal
        test <@ why.Contains "at the K_P T's baseline names" && why.Contains "impact fail" @>)

[<Fact>]
let ``a newer fail at the project's CURRENT K_P refuses it`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        let now = inputsWith [ "closure", "cl2" ]
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        fileOutcome store (TestEvidenceKey.compute now) "impact" OutcomeStore.Outcome.Fail (at 1)
        let why = judgedWith store (kpWith []) (Ok now) |> refusal
        test <@ why.Contains "at its current K_P" @>)

[<Fact>]
let ``every other miss refuses the project and says why`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)
        let atT = Map.ofList [ "P", fullKeyText (kpWith []) ]
        let now = Map.ofList [ "P", Ok(inputsWith []) ]

        let judged fullKeys current inputs =
            BoxBaseline.judgeProject store fullKeys coveredAtT current inputs "P" |> refusal

        test <@ (judged Map.empty now compileInputs).Contains "names no full-run K_P" @>
        test <@ (judged (Map.ofList [ "P", "not a key" ]) now compileInputs).Contains "names no full-run K_P" @>

        test
            <@
                (judged (Map.ofList [ "P", fullKeyText (kpWith [ "toolchain", "never-filed" ]) ]) now compileInputs)
                    .Contains
                    "no full-scope record"
            @>

        test <@ (judged atT Map.empty compileInputs).Contains "the project model does not name it" @>

        test
            <@
                (judged atT (Map.ofList [ "P", Error "project options unknown" ]) compileInputs).Contains
                    "no current K_P: project options unknown"
            @>

        test <@ (judged atT now Map.empty).Contains "no compile-input identity" @>)

[<Fact>]
let ``a record at T's named key that carries no K_P refuses the project`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        let foreign = ActionKey.create "test" [ "something", "else" ]

        test
            <@
                store.Record
                    { Key = foreign
                      Subject = "P"
                      Outcome = OutcomeStore.Outcome.Pass
                      Counts = None
                      DurationMs = None
                      Producer =
                        { Binary = "fshw-test"
                          Worktree = "/ws/live"
                          RunId = Some "run-1" }
                      RecordedAt = at 0 }
            @>

        let why =
            BoxBaseline.judgeProject
                store
                (Map.ofList [ "P", foreign.Value.Text ])
                coveredAtT
                (Map.ofList [ "P", Ok(inputsWith []) ])
                compileInputs
                "P"
            |> refusal

        test <@ why.Contains "carries no K_P" @>)

[<Fact>]
let ``judge answers for every project T's pass names and every one the model configures`` () =
    withTempDir "box" (fun dir ->
        let store = storeIn dir
        fileOutcome store (kpWith []) "full" OutcomeStore.Outcome.Pass (at 0)

        let record =
            { recordOf current sources with
                Entries = manifestAtT |> Set.toList |> List.map (fun path -> path, "h")
                FullKeys = Map.ofList [ "P", fullKeyText (kpWith []); "Gone", fullKeyText (kpWith []) ] }

        let judged =
            BoxBaseline.judge
                store
                (Map.ofList [ "P", Ok(inputsWith []); "New", Ok(inputsWith []) ])
                compileInputs
                record

        test <@ judged |> Map.keys |> Set.ofSeq = set [ "P"; "Gone"; "New" ] @>
        test <@ judged["P"] = BoxProject.Vouched("tests/P/P.fsproj", "identity:P") @>
        test <@ (refusal judged["Gone"]).Contains "the project model does not name it" @>
        test <@ (refusal judged["New"]).Contains "names no full-run K_P" @>)

[<Fact>]
let ``a rebuilt box baseline carries T's pass as the ledger records an own one, and what it vouches for`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        fileAnalyses l.Store "/ws/live"

        fileBaselineShaped
            (fun o ->
                let projects = JsonObject()
                projects["T"] <- "ak1:" + String('a', 64)
                projects["Lib"] <- "ak1:" + String('b', 64)
                o["projects"] <- projects)
            l.Store
            treeT
            Set.empty
            (at 0)

        let judged = Map.ofList [ "T", BoxProject.Refused "for the test" ]

        match
            BoxBaseline.rebuild
                { envOf l ignore with
                    Judge = fun _ -> judged }
                treeT'
        with
        | Ok rebuilt ->
            test <@ rebuilt.Ledger.RunId = Guid.Parse(runOf treeT.Identity) @>
            test <@ rebuilt.Ledger.Projects = set [ "T"; "Lib" ] @>
            test <@ rebuilt.Ledger.Tree = Some treeT.Identity @>
            test <@ List.isEmpty rebuilt.Ledger.EarlierTrees @>
            test <@ rebuilt.Projects = judged @>
        | Error why -> failwith $"expected a rebuild: %s{why}")

[<Fact>]
let ``a baseline whose run is not a run id is no baseline and the seal is unchanged`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        fileAnalyses l.Store "/ws/live"
        fileBaselineShaped (fun o -> o["runId"] <- "not-a-run") l.Store treeT Set.empty (at 0)

        BoxBaseline.rebuild (envOf l ignore) treeT'
        |> refusedWith [ "not-a-run"; "not a run id" ]

        unchanged l)

[<Fact>]
let ``a baseline's projects are read as filed, and a record with no projects names none`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir

        fileBaselineShaped
            (fun o ->
                let projects = JsonObject()
                projects["T"] <- "ak1:" + String('a', 64)
                projects["Odd"] <- 3
                o["projects"] <- projects)
            l.Store
            treeT
            Set.empty
            (at 0)

        test
            <@
                TestBaseline.checkoutRecordFor l.Store treeT.Identity |> Result.map _.FullKeys = Ok(
                    Map.ofList [ "T", "ak1:" + String('a', 64) ]
                )
            @>

        fileBaselineShaped (fun o -> o.Remove "projects" |> ignore) l.Store treeT' Set.empty (at 1)

        test <@ TestBaseline.checkoutRecordFor l.Store treeT'.Identity |> Result.map _.FullKeys = Ok Map.empty @>)

[<Fact>]
let ``predict names the tree a restart would rebuild from, and writes nothing`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir
        boxAfterLivePassedT l

        test
            <@
                BoxBaseline.predict l.Store l.Root treeT'
                |> Result.map (fun (record, distance) -> record.Tree, distance) = Ok(
                    treeT.Identity,
                    { Differing = 1; NonSource = 0 }
                )
            @>

        unchanged l)

[<Fact>]
let ``predict refuses what a rebuild would refuse`` () =
    withTempDir "box" (fun dir ->
        let l = landing dir

        test <@ BoxBaseline.predict l.Store l.Root treeT' |> Result.isError @>
        fileAnalyses l.Store "/ws/live"
        fileBaselineWith l.Store treeT (Set.singleton LibPath) (at 0)

        match BoxBaseline.predict l.Store l.Root treeT' with
        | Error why -> test <@ why.Contains "no-record=1" @>
        | Ok _ -> failwith "an unpinned row predicted a rebuild"

        match BoxBaseline.predict l.Store l.Root treeT with
        | Error why -> test <@ why.Contains "has not moved" @>
        | Ok _ -> failwith "an unmoved tree predicted a rebuild")

// ---------------------------------------------------------------------------
// SOUNDNESS from REAL analyses: a small F# project checked by the compiler. "live" folds the
// checks through the test-prune plugin itself (FileChecked, then the cohort's seal flushes
// the index) and files each analysis under the key the plugin files it by; "land", a second
// checkout of the same bytes, rebuilds its index from those records. For a real body edit,
// both select the same tests from the same rows.
// ---------------------------------------------------------------------------

[<Literal>]
let private ValuePath = "tests/P/Value.fs"

/// Project `P` under `root`: a test attribute, two values, a test calling each.
let private writeRealProject (root: string) (answerBody: string) =
    let dir = Path.Combine(root, "tests", "P")
    Directory.CreateDirectory dir |> ignore
    let attr = Path.Combine(dir, "Attr.fs")
    let value = Path.Combine(dir, "Value.fs")
    let tests = Path.Combine(dir, "Tests.fs")
    File.WriteAllText(attr, "namespace Xunit\ntype FactAttribute() =\n    inherit System.Attribute()\n")
    File.WriteAllText(value, $"module P.Value\nlet answer () = %s{answerBody}\nlet other () = 3\n")

    File.WriteAllText(
        tests,
        "module P.Tests\nopen Xunit\n[<Fact>]\nlet ``observes the answer`` () = P.Value.answer () = 2\n"
        + "[<Fact>]\nlet ``observes the other`` () = P.Value.other () = 3\n"
    )

    [ attr; value; tests ]

/// Every file of `P` checked by the compiler under `root`.
let private checkReal (root: string) (files: string list) : FileCheckResult list =
    let checker = sharedChecker.Value
    let pipeline = FsHotWatch.CheckPipeline.CheckPipeline(checker)
    let first = List.head files

    let options, _ =
        scriptProjectOptions (checker, first, FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText first))
        |> Async.RunSynchronously

    pipeline.RegisterProject(
        "P",
        { options with
            ProjectFileName = Path.Combine(root, "tests", "P", "P.fsproj")
            SourceFiles = List.toArray files }
    )

    files
    |> List.map (fun file ->
        pipeline.CheckFile(AbsFilePath.create file)
        |> Async.RunSynchronously
        |> Option.defaultWith (fun () -> failwith $"no check result for {file}")
        |> stampFixture)

let private relOf (root: string) (result: FileCheckResult) =
    Path.GetRelativePath(root, AbsFilePath.value result.File).Replace('\\', '/')

/// The analysis the plugin takes from a live check: `analyzeSourceFromResults`, as
/// `onFileCheckedWith` calls it.
let private analysisOfCheck (result: FileCheckResult) : AnalysisResult =
    match result.CheckResults with
    | FullCheck(parse, check) ->
        match analyzeSourceFromResults (AbsFilePath.value result.File) result.Source parse check "P" with
        | Ok analysis -> analysis
        | Error why -> failwith $"analysis failed: %s{why}"
    | other -> failwith $"expected a full check, got %A{other}"

let private realConfig (root: string) : TestConfig =
    { Project = "P"
      Command = "sh"
      Args = "-c true"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

/// "live"'s index at T, written by the plugin from its own fold of the live checks.
let private liveIndexFromPlugin (root: string) (results: FileCheckResult list) =
    let dbPath = Path.Combine(root, "tp.db")
    let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value root
    host.WorkStore.PublishProjectModel fixtureModel
    host.RegisterHandler(TestPrunePlugin.create dbPath root (Some [ realConfig root ]) None None None None [])

    try
        for result in results do
            host.EmitFileChecked result

        waitForQuiescent host 30000
        host.EmitBatchChecked(fakeBatchChecked (results |> List.map (_.File >> AbsFilePath.value)))
        waitForQuiescent host 30000
    finally
        host.Teardown()

    Database.create dbPath

/// What the cold scan selects for the T' check of `Value.fs` under `root`, against `db`'s rows.
let private realSelection (db: Database) (root: string) (edited: FileCheckResult) =
    let current =
        (FileCheckedHandler.indexedAnalysisOf root "P" (analysisOfCheck edited)).Symbols

    let changes, _ = detectChanges current (db.GetSymbolsInFile ValuePath)

    db.QueryAffectedTests(changedSymbolNames changes)
    |> List.map (fun t -> t.TestProject, t.TestClass, t.TestMethod)
    |> Set.ofList

[<Fact(Timeout = 180000)>]
let ``SOUNDNESS from real compiler analyses: the box-rebuilt index selects what the live index selects`` () =
    withTempDir "box-real" (fun dir ->
        let liveRoot = Path.Combine(dir, "live")
        let landRoot = Path.Combine(dir, "land")
        let liveFiles = writeRealProject liveRoot "2"
        let landFiles = writeRealProject landRoot "2"
        let liveChecks = checkReal liveRoot liveFiles
        let live = liveIndexFromPlugin liveRoot liveChecks

        // The box: each analysis filed under the key the plugin files it by, and T's baseline
        // pinning exactly those records.
        let store = storeIn (Path.Combine(dir, "outcomes"))

        let rows =
            liveChecks
            |> List.map (fun result ->
                let rel = relOf liveRoot result
                let key = (AnalysisRecords.keyFor liveRoot result).Value
                let identity = (IndexedAnalysis.identityOf liveRoot result).Value
                test <@ AnalysisRecords.file store liveRoot rel key (analysisOfCheck result) @>
                rel, ContentHash.ofFile (AbsFilePath.value result.File), identity, key.Value.Text)

        let entries = rows |> List.map (fun (rel, hash, _, _) -> rel, hash)
        let tree = treeOf entries
        let key = TestBaseline.keyOf tree.Identity
        let o = JsonObject()
        o["schema"] <- TestBaseline.Schema
        o["runId"] <- runOf tree.Identity
        o["tree"] <- tree.Identity
        o["analyzer"] <- AnalysisRecords.analyzerIdentity
        o["projects"] <- JsonObject()
        let manifest = JsonArray()
        let text (value: string) = JsonValue.Create value :> JsonNode

        for rel, hash, identity, pinned in rows do
            manifest.Add(JsonArray(text rel, text hash, text identity, text "P", text pinned))

        o["manifest"] <- manifest

        test
            <@
                (store.Supersede(
                    OutcomeStore.Record.ofAction
                        key
                        tree.Identity
                        OutcomeStore.Outcome.Pass
                        "fshw-test"
                        liveRoot
                        (at 0),
                    Some(o.ToJsonString())
                ))
                    .IsSome
            @>

        test
            <@
                (TestBaseline.appendIndex
                    store
                    { Binary = "fshw-test"
                      Worktree = liveRoot
                      Now = at 0 }
                    { Tree = tree.Identity
                      Baseline = key.Value.Text
                      RunId = runOf tree.Identity
                      At = at 0 })
                    .IsSome
            @>

        // T': the same real body edit in both checkouts.
        let edit (root: string) (files: string list) =
            File.WriteAllText(
                Path.Combine(root, ValuePath),
                "module P.Value\nlet answer () = 1 + 1\nlet other () = 3\n"
            )

            checkReal root files |> List.find (fun r -> relOf root r = ValuePath)

        let liveEdited = edit liveRoot liveFiles
        let fromLive = realSelection live liveRoot liveEdited

        // Positive control: the live index selects the test that calls the edited body only.
        test <@ fromLive = Set.singleton ("P", "P.Tests", "``observes the answer``") @>

        let landDbPath = Path.Combine(landRoot, "tp.db")
        let landDb = Database.create landDbPath
        let landEdited = edit landRoot landFiles

        let current =
            treeOf (
                entries
                |> List.map (fun (rel, hash) ->
                    rel,
                    (if rel = ValuePath then
                         ContentHash.ofFile (Path.Combine(landRoot, rel))
                     else
                         hash))
            )

        let env: BoxBaseline.RebuildEnv =
            { Store = store
              Root = landRoot
              ProjectsOf =
                fun path ->
                    if entries |> List.exists (fst >> (=) path) then
                        [ "P" ]
                    else
                        []
              DbPath = landDbPath
              Write =
                fun results keys ->
                    IndexMaintenance.writeAnalyses
                        landDb
                        landDbPath
                        (IndexMaintenance.dbBusyTimeout landDb)
                        noDeclaration
                        results
                        keys
              Judge = fun _ -> Map.empty
              Log = ignore }

        match BoxBaseline.rebuild env current with
        | Ok rebuilt -> test <@ rebuilt.Baseline.Tree = tree.Identity @>
        | Error why -> failwith $"expected a rebuild: %s{why}"

        test <@ realSelection landDb landRoot landEdited = fromLive @>

        // The whole index agrees, from the compiler's own analyses.
        let rowsOf (db: Database) =
            entries
            |> List.map (fun (path, _) ->
                path,
                db.GetSymbolsInFile path
                |> List.map (fun s -> s.FullName, s.ContentHash)
                |> List.sort,
                db.GetDependenciesFromFile path
                |> List.sortBy (fun d -> d.FromSymbol, d.ToSymbol),
                db.GetTestMethodsInFile path |> List.sortBy _.SymbolFullName)

        test <@ rowsOf landDb = rowsOf live @>
        test <@ landDb.GetFileKeys() = live.GetFileKeys() @>)

// ---------------------------------------------------------------------------
// A box-derived ledger entry is marked in the ledger itself, and vanishes with the flag off.
// ---------------------------------------------------------------------------

let private ledgerWith (origin: FullSuiteBaseline.Origin) : FullSuiteBaseline.Baseline =
    { RunId = Guid.Parse "0123456789abcdef0123456789abcdef"
      EarnedAt = DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc)
      Projects = set [ "P" ]
      Tree = Some "sha256:box-tree"
      EarlierTrees = [ "sha256:own-earlier" ]
      Origin = origin }

[<Fact>]
let ``a box-derived ledger entry is written as such and read back as such`` () =
    withTempDir "box-ledger" (fun root ->
        FullSuiteBaseline.save root (ledgerWith FullSuiteBaseline.Origin.Box)

        test
            <@
                FullSuiteBaseline.load root = FullSuiteBaseline.LoadedBaseline.Loaded(
                    Some(ledgerWith FullSuiteBaseline.Origin.Box)
                )
            @>

        FullSuiteBaseline.save root (ledgerWith FullSuiteBaseline.Origin.OwnRun)

        test
            <@
                FullSuiteBaseline.load root = FullSuiteBaseline.LoadedBaseline.Loaded(
                    Some(ledgerWith FullSuiteBaseline.Origin.OwnRun)
                )
            @>)

[<Fact>]
let ``with the flag off a box-derived ledger entry is absent; with it on, and for an own one, it stands`` () =
    withTempDir "box-ledger" (fun root ->
        FullSuiteBaseline.save root (ledgerWith FullSuiteBaseline.Origin.Box)

        test
            <@
                FullSuiteBaseline.loadFor RestartBaselines.OwnCheckoutOnly root = FullSuiteBaseline.LoadedBaseline.Loaded
                    None
            @>

        test
            <@
                FullSuiteBaseline.loadFor RestartBaselines.RetainedBoxBaselines root = FullSuiteBaseline
                    .LoadedBaseline
                    .Loaded(Some(ledgerWith FullSuiteBaseline.Origin.Box))
            @>

        FullSuiteBaseline.save root (ledgerWith FullSuiteBaseline.Origin.OwnRun)

        test
            <@
                FullSuiteBaseline.loadFor RestartBaselines.OwnCheckoutOnly root = FullSuiteBaseline
                    .LoadedBaseline
                    .Loaded(Some(ledgerWith FullSuiteBaseline.Origin.OwnRun))
            @>)

[<Fact>]
let ``a ledger written before origins were kept is its own run's; an unknown origin is unreadable`` () =
    withTempDir "box-ledger" (fun root ->
        let path = FullSuiteBaseline.sidecarPath root
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        let written (origin: string) =
            File.WriteAllText(
                path,
                """{"runId":"0123456789abcdef0123456789abcdef","earnedAt":"2026-11-24T12:00:00.0000000Z","projects":["P"]"""
                + origin
                + "}"
            )

            FullSuiteBaseline.load root

        match written "" with
        | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) ->
            test <@ baseline.Origin = FullSuiteBaseline.Origin.OwnRun @>
        | other -> failwith $"expected an own baseline, got %A{other}"

        match written ""","origin":"elsewhere" """ with
        | FullSuiteBaseline.LoadedBaseline.Unreadable why -> test <@ why.Contains "unknown `origin` elsewhere" @>
        | other -> failwith $"expected unreadable, got %A{other}")

[<Fact>]
let ``an own baseline earned after a box-derived one does not carry the box tree as one this checkout passed`` () =
    let own =
        { ledgerWith FullSuiteBaseline.Origin.OwnRun with
            Tree = Some "sha256:own-next"
            EarlierTrees = [] }

    let afterBox =
        FullSuiteBaseline.succeed (Some(ledgerWith FullSuiteBaseline.Origin.Box)) own

    test <@ afterBox.EarlierTrees = [ "sha256:own-earlier" ] @>

    let afterOwn =
        FullSuiteBaseline.succeed (Some(ledgerWith FullSuiteBaseline.Origin.OwnRun)) own

    test <@ afterOwn.EarlierTrees = [ "sha256:box-tree"; "sha256:own-earlier" ] @>
